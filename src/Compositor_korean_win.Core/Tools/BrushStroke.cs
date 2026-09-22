namespace Compositor_korean_win.Core;

/// <summary>A colour in straight (not premultiplied) RGBA.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba Black => new(0, 0, 0);
    public static Rgba White => new(255, 255, 255);
}

/// <summary>What a stroke does to the pixels under it.</summary>
public enum BrushMode
{
    /// <summary>Lays the settings' colour down.</summary>
    Paint,

    /// <summary>Takes the layer's pixels away.</summary>
    Erase,

    /// <summary>Marks what to rebuild from nearby texture, once the stroke ends.</summary>
    Heal,

    /// <summary>Paints pixels sampled from somewhere else, at a fixed offset.</summary>
    Clone,

    /// <summary>Softens what is already there rather than adding anything.</summary>
    Blur,
}

/// <summary>Where the clone stamp takes its pixels from.</summary>
/// <remarks>
/// The offset is in layer pixels, from the painted point to the sampled one, and it is the shell's
/// to work out: aligned cloning keeps one offset for the whole stroke, unaligned resets it at every
/// stroke, and neither is something the stroke itself has an opinion about.
/// </remarks>
public sealed record CloneSource(PixelBuffer Sample, Point Offset);

/// <summary>What the brush paints and how it is shaped.</summary>
/// <remarks>
/// <see cref="Opacity"/> caps the whole stroke, as in Photoshop: dabs overlap constantly — a
/// 40-pixel brush lays one every pixel — so if each dab composited on its own, a slow drag would
/// come out darker than a quick one over the same ground. The stroke keeps coverage of its own and
/// paints once through it instead.
/// </remarks>
public sealed record BrushSettings
{
    public double Diameter { get; init; } = 40;

    /// <summary>1 is a hard edge; below that the tip fades from this fraction of its radius.</summary>
    public double Hardness { get; init; } = 1;

    public Rgba Color { get; init; } = Rgba.Black;

    public double Opacity { get; init; } = 1;

    public BrushMode Mode { get; init; } = BrushMode.Paint;

    public SpotHealingMode HealingMode { get; init; } = SpotHealingMode.ContentAware;

    /// <summary>
    /// Where <see cref="BrushMode.Clone"/> takes its pixels from.
    /// </summary>
    /// <remarks>Not named Clone: a record already has one of those, and it means something else.</remarks>
    public CloneSource? CloneFrom { get; init; }

    /// <summary>How far <see cref="BrushMode.Blur"/> reaches, in layer pixels.</summary>
    public double BlurRadius { get; init; } = 4;

    [System.Text.Json.Serialization.JsonIgnore]
    public double Radius => Math.Max(0.5, Diameter / 2);
}

/// <summary>
/// A brush stroke in progress, over one layer's pixel grid.
/// </summary>
/// <remarks>
/// <para>
/// Two things make a stroke on a hundred-megapixel layer cost what it touches rather than what the
/// layer is. Coverage is kept in 256-pixel tiles, and only the tiles a dab lands on ever allocate
/// anything; and what the canvas draws while the stroke is live is the layer's own pixels plus
/// those tiles as replacement patches (<see cref="LayerRaster"/>), so nothing is rebuilt whole
/// until the stroke ends. Both are upstream's (docs/windows-port.md §2.3).
/// </para>
/// <para>
/// Dabs accumulate into coverage rather than onto pixels — lighten for a hard tip, which keeps its
/// antialiased silhouette, and screen for a soft one, which builds up within the stroke. The
/// pixels are composed from that coverage once per publish, which is what makes
/// <see cref="BrushSettings.Opacity"/> a cap on the stroke instead of a cap on each dab.
/// </para>
/// </remarks>
public sealed class BrushStroke : IDisposable
{
    /// <summary>Tile edge in layer pixels. Upstream measured wider tiles as no faster.</summary>
    public const int TileSize = 256;

    private sealed class Tile
    {
        public required PixelRect Rect { get; init; }
        public required byte[] Coverage { get; init; }
        public required PixelBuffer Pixels { get; init; }

        /// <summary>The tile softened, made once for a blur stroke and kept for the stroke.</summary>
        public PixelBuffer? Softened { get; set; }
    }

    private readonly Dictionary<int, Tile> _tiles = [];
    private readonly PixelBuffer? _base;
    private readonly BrushSettings _settings;
    private readonly byte[] _tip;
    private readonly int _tipSize;
    private readonly double _spacing;

    /// <summary>What the stroke may touch: null for the whole layer.</summary>
    private readonly byte[]? _selection;
    private readonly PixelRect _selectionRegion;

    private Point? _previous;
    private double _distanceToNext;

    public BrushStroke(PixelBuffer? baseImage, int width, int height, BrushSettings settings,
                       DocumentSelection? selection = null)
    {
        Width = width;
        Height = height;
        _base = baseImage;
        _settings = settings;

        (_tip, _tipSize) = Tip(settings);

        // Upstream's rate: a hard tip can afford wider gaps because its silhouette is the edge,
        // while a soft one has to lay often enough for the falloffs to add up smoothly.
        _spacing = Math.Max(0.25, settings.Diameter * (settings.Hardness >= 1 ? 0.015 : 0.025));

        if (selection is not null) (_selectionRegion, _selection) = selection.Clip(width, height);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Whether anything has been painted yet.</summary>
    public bool IsEmpty => _tiles.Count == 0;

    /// <summary>
    /// The tiles the stroke has rebuilt, ready to draw over the layer's own pixels.
    /// </summary>
    /// <remarks>The patches share the stroke's buffers; they live until it is disposed.</remarks>
    public IReadOnlyList<RasterPatch> Patches =>
        [.. _tiles.Values.Select(tile => new RasterPatch(tile.Rect, tile.Pixels))];

    /// <summary>The region of the layer the stroke has changed.</summary>
    public PixelRect Dirty { get; private set; }

    /// <summary>Carries the stroke on to <paramref name="point"/>, in layer pixels.</summary>
    /// <remarks>
    /// Dabs are laid at even distances along the way, and the leftover distance is carried into the
    /// next call — otherwise a fast drag, which delivers few and far-apart points, would lay one
    /// dab per event and come out as beads.
    /// </remarks>
    public void Append(Point point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return;

        if (_previous is not Point previous)
        {
            Dab(point);
            _distanceToNext = _spacing;
            _previous = point;
            return;
        }

        double dx = point.X - previous.X, dy = point.Y - previous.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);

        if (length > 0)
        {
            double distance = _distanceToNext;
            while (distance <= length)
            {
                Dab(new Point(previous.X + dx * distance / length, previous.Y + dy * distance / length));
                distance += _spacing;
            }
            _distanceToNext = distance - length;
        }

        _previous = point;
    }

    /// <summary>
    /// Replaces what the stroke covered with texture from around it, once the stroke has ended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The kernel is given a crop, not the layer: a blemish healed on a hundred-megapixel photograph
    /// has no business reading a hundred megapixels. The crop is the painted area plus the distance
    /// the patch search actually reaches (<see cref="SpotHeal.ReachFor"/>) — any tighter and healing
    /// a blemish would only ever find the blemish.
    /// </para>
    /// <para>
    /// It reads the layer's own pixels, never the wash the stroke was showing while it was painted:
    /// healing is meant to look at what was there, and the stroke is only saying where.
    /// </para>
    /// </remarks>
    public bool Heal(uint seed)
    {
        if (_settings.Mode != BrushMode.Heal || _tiles.Count == 0 || Dirty.IsEmpty) return false;

        int reach = SpotHeal.ReachFor(Dirty);
        PixelRect region = Dirty.Inflate(reach).Intersect(new PixelRect(0, 0, Width, Height));
        if (region.IsEmpty) return false;

        PixelBuffer crop = _base is PixelBuffer image
            ? PixelRegion.Copy(image, region)
            : PixelBuffer.Allocate(region.Width, region.Height);

        try
        {
            var coverage = new byte[region.Width * region.Height];
            foreach (Tile tile in _tiles.Values)
            {
                PixelRect overlap = tile.Rect.Intersect(region);
                if (overlap.IsEmpty) continue;

                for (int y = overlap.Y; y < overlap.Bottom; y++)
                {
                    for (int x = overlap.X; x < overlap.Right; x++)
                    {
                        byte level = tile.Coverage[(y - tile.Rect.Y) * tile.Rect.Width + (x - tile.Rect.X)];
                        if (level == 0) continue;
                        coverage[(y - region.Y) * region.Width + (x - region.X)] = level;
                    }
                }
            }

            if (!SpotHeal.Heal(crop, coverage, _settings.Opacity, _settings.HealingMode, seed)) return false;

            // The tiles were showing paint; now they show what the kernel put there instead.
            foreach (Tile tile in _tiles.Values)
            {
                PixelRect overlap = tile.Rect.Intersect(region);
                if (overlap.IsEmpty) continue;

                for (int y = overlap.Y; y < overlap.Bottom; y++)
                {
                    ReadOnlySpan<byte> healed = crop.Row(y - region.Y);
                    Span<byte> target = tile.Pixels.Row(y - tile.Rect.Y);

                    for (int x = overlap.X; x < overlap.Right; x++)
                    {
                        healed.Slice((x - region.X) * 4, 4)
                              .CopyTo(target.Slice((x - tile.Rect.X) * 4, 4));
                    }
                }
            }

            return true;
        }
        finally
        {
            crop.Release();
        }
    }

    /// <summary>The finished layer: its old pixels with the stroke's tiles laid over them.</summary>
    /// <remarks>The caller owns the result and releases it.</remarks>
    public PixelBuffer Commit()
    {
        PixelBuffer baseImage = _base ?? PixelBuffer.Allocate(Width, Height);
        try
        {
            return new LayerRaster(baseImage, Patches).Flatten();
        }
        finally
        {
            if (_base is null) baseImage.Release();
        }
    }

    /// <summary>One dab of the tip, centred on <paramref name="point"/>.</summary>
    private void Dab(Point point)
    {
        double radius = _settings.Radius;
        PixelRect touched = new Rect(point.X - radius - 1, point.Y - radius - 1,
                                     radius * 2 + 2, radius * 2 + 2)
            .Enclosing()
            .Intersect(new PixelRect(0, 0, Width, Height));

        if (touched.IsEmpty) return;

        for (int tileY = touched.Y / TileSize; tileY <= (touched.Bottom - 1) / TileSize; tileY++)
        {
            for (int tileX = touched.X / TileSize; tileX <= (touched.Right - 1) / TileSize; tileX++)
            {
                Tile tile = TileAt(tileX, tileY);
                PixelRect area = touched.Intersect(tile.Rect);
                if (area.IsEmpty) continue;

                Stamp(tile, area, point);
                Compose(tile, area);
            }
        }

        Dirty = Dirty.IsEmpty ? touched : PixelRect.FromBounds(
            Math.Min(Dirty.X, touched.X), Math.Min(Dirty.Y, touched.Y),
            Math.Max(Dirty.Right, touched.Right), Math.Max(Dirty.Bottom, touched.Bottom));
    }

    /// <summary>Adds the tip's coverage to a tile, where the dab overlaps it.</summary>
    private void Stamp(Tile tile, PixelRect area, Point point)
    {
        double radius = _settings.Radius;
        double scale = _tipSize / (radius * 2 + 2);
        bool hard = _settings.Hardness >= 1;

        for (int y = area.Y; y < area.Bottom; y++)
        {
            int row = (y - tile.Rect.Y) * tile.Rect.Width;

            for (int x = area.X; x < area.Right; x++)
            {
                // Into the tip's own grid, which is sampled at the pixel's centre.
                double tx = (x + 0.5 - (point.X - radius - 1)) * scale;
                double ty = (y + 0.5 - (point.Y - radius - 1)) * scale;

                int ix = (int)tx, iy = (int)ty;
                if (ix < 0 || iy < 0 || ix >= _tipSize || iy >= _tipSize) continue;

                byte level = _tip[iy * _tipSize + ix];
                if (level == 0) continue;

                int index = row + (x - tile.Rect.X);
                byte have = tile.Coverage[index];

                // Lighten keeps a hard tip's silhouette; screen lets a soft one build up.
                tile.Coverage[index] = hard
                    ? Math.Max(have, level)
                    : (byte)(255 - (255 - have) * (255 - level) / 255);
            }
        }
    }

    /// <summary>Rebuilds a tile's pixels from the layer's own and the coverage over them.</summary>
    /// <remarks>
    /// Every mode but healing goes through here, and they differ only in what they put on: the
    /// settings' colour, a sample taken from somewhere else, or the tile softened. Healing cannot:
    /// it has to look at the finished shape of the stroke, so it happens once at the end.
    /// </remarks>
    private void Compose(Tile tile, PixelRect area)
    {
        Span<byte> colour = stackalloc byte[4];
        colour[0] = _settings.Color.R;
        colour[1] = _settings.Color.G;
        colour[2] = _settings.Color.B;
        colour[3] = _settings.Color.A;

        if (_settings.Mode == BrushMode.Blur) tile.Softened ??= Soften(tile);

        // Out here, not in the loop: a stackalloc per pixel is a stack overflow waiting for a
        // wide enough brush.
        Span<byte> taken = stackalloc byte[4];

        for (int y = area.Y; y < area.Bottom; y++)
        {
            Span<byte> target = tile.Pixels.Row(y - tile.Rect.Y);
            ReadOnlySpan<byte> source = _base is PixelBuffer image && y < image.Height
                ? image.Row(y)
                : default;

            for (int x = area.X; x < area.Right; x++)
            {
                int local = x - tile.Rect.X;
                double alpha = tile.Coverage[(y - tile.Rect.Y) * tile.Rect.Width + local] / 255.0
                               * _settings.Opacity * Selected(x, y);

                Span<byte> pixel = target.Slice(local * 4, 4);

                // The layer's own pixels first: a tile is rebuilt, not accumulated into.
                if (!source.IsEmpty && x < _base!.Width) source.Slice(x * 4, 4).CopyTo(pixel);
                else pixel.Clear();

                if (alpha <= 0) continue;

                if (_settings.Mode == BrushMode.Erase)
                {
                    for (int channel = 0; channel < 4; channel++)
                        pixel[channel] = (byte)Math.Round(pixel[channel] * (1 - alpha), MidpointRounding.AwayFromZero);
                    continue;
                }

                // A clone or a blur puts a whole pixel down — colour and alpha both — rather than
                // painting one colour through coverage.
                if (_settings.Mode is BrushMode.Clone or BrushMode.Blur)
                {
                    if (!Sampled(tile, x, y, taken)) continue;

                    for (int channel = 0; channel < 4; channel++)
                    {
                        pixel[channel] = (byte)Math.Clamp(
                            Math.Round(taken[channel] * alpha + pixel[channel] * (1 - alpha),
                                       MidpointRounding.AwayFromZero), 0, 255);
                    }
                    continue;
                }

                double strength = alpha * colour[3] / 255.0;
                for (int channel = 0; channel < 3; channel++)
                {
                    double painted = colour[channel] * strength;
                    pixel[channel] = (byte)Math.Clamp(
                        Math.Round(painted + pixel[channel] * (1 - strength), MidpointRounding.AwayFromZero), 0, 255);
                }

                pixel[3] = (byte)Math.Clamp(
                    Math.Round(255 * strength + pixel[3] * (1 - strength), MidpointRounding.AwayFromZero), 0, 255);
            }
        }
    }

    /// <summary>The pixel a clone or a blur puts down at this position, if there is one.</summary>
    private bool Sampled(Tile tile, int x, int y, Span<byte> result)
    {
        if (_settings.Mode == BrushMode.Blur)
        {
            if (tile.Softened is not PixelBuffer softened) return false;
            softened.Row(y - tile.Rect.Y).Slice((x - tile.Rect.X) * 4, 4).CopyTo(result);
            return true;
        }

        if (_settings.CloneFrom is not CloneSource clone) return false;

        int sx = (int)Math.Round(x + clone.Offset.X, MidpointRounding.AwayFromZero);
        int sy = (int)Math.Round(y + clone.Offset.Y, MidpointRounding.AwayFromZero);
        if (sx < 0 || sy < 0 || sx >= clone.Sample.Width || sy >= clone.Sample.Height) return false;

        clone.Sample.Row(sy).Slice(sx * 4, 4).CopyTo(result);
        return true;
    }

    /// <summary>
    /// The tile's own pixels, softened, ready for a blur stroke to paint back in.
    /// </summary>
    /// <remarks>
    /// Two box passes over a margin that reaches outside the tile, so the softening does not stop
    /// at a tile edge and leave a seam where one tile meets the next. Made once per tile per
    /// stroke: it depends on the layer's pixels, which a stroke never changes.
    /// </remarks>
    private PixelBuffer Soften(Tile tile)
    {
        int radius = (int)Math.Clamp(Math.Round(_settings.BlurRadius, MidpointRounding.AwayFromZero), 1, 64);
        PixelRect region = tile.Rect.Inflate(radius * 2).Intersect(new PixelRect(0, 0, Width, Height));

        PixelBuffer source = _base is PixelBuffer image
            ? PixelRegion.Copy(image, region)
            : PixelBuffer.Allocate(region.Width, region.Height);

        try
        {
            PixelBuffer blurred = BoxBlur(source, radius);
            try
            {
                return PixelRegion.Copy(blurred, new PixelRect(tile.Rect.X - region.X, tile.Rect.Y - region.Y,
                                                               tile.Rect.Width, tile.Rect.Height));
            }
            finally
            {
                blurred.Release();
            }
        }
        finally
        {
            source.Release();
        }
    }

    /// <summary>Two separable box passes, which is close enough to a Gaussian to look like one.</summary>
    private static PixelBuffer BoxBlur(PixelBuffer source, int radius)
    {
        PixelBuffer first = Pass(source, radius, horizontal: true);
        PixelBuffer second = Pass(first, radius, horizontal: false);
        first.Release();

        PixelBuffer third = Pass(second, radius, horizontal: true);
        second.Release();

        PixelBuffer fourth = Pass(third, radius, horizontal: false);
        third.Release();
        return fourth;

        static PixelBuffer Pass(PixelBuffer from, int radius, bool horizontal)
        {
            PixelBuffer result = PixelBuffer.Allocate(from.Width, from.Height);
            int outer = horizontal ? from.Height : from.Width;
            int inner = horizontal ? from.Width : from.Height;
            Span<int> sums = stackalloc int[4];

            for (int line = 0; line < outer; line++)
            {
                for (int i = 0; i < inner; i++)
                {
                    sums.Clear();
                    int count = 0;

                    for (int k = -radius; k <= radius; k++)
                    {
                        int at = i + k;
                        if (at < 0 || at >= inner) continue;

                        ReadOnlySpan<byte> pixel = horizontal
                            ? from.Row(line).Slice(at * 4, 4)
                            : from.Row(at).Slice(line * 4, 4);

                        for (int channel = 0; channel < 4; channel++) sums[channel] += pixel[channel];
                        count++;
                    }

                    Span<byte> target = horizontal
                        ? result.Row(line).Slice(i * 4, 4)
                        : result.Row(i).Slice(line * 4, 4);

                    for (int channel = 0; channel < 4; channel++)
                        target[channel] = (byte)((sums[channel] + count / 2) / Math.Max(1, count));
                }
            }

            return result;
        }
    }

    /// <summary>How much of a pixel the selection lets through: 1 when there is no selection.</summary>
    private double Selected(int x, int y)
    {
        if (_selection is null) return 1;

        // An empty selection is not the absence of one: it means the edit touches nothing.
        if (_selection.Length == 0) return 0;
        if (x < _selectionRegion.X || y < _selectionRegion.Y
            || x >= _selectionRegion.Right || y >= _selectionRegion.Bottom) return 0;

        int index = (y - _selectionRegion.Y) * _selectionRegion.Width + (x - _selectionRegion.X);
        return _selection[index] / 255.0;
    }

    private Tile TileAt(int tileX, int tileY)
    {
        int key = tileY * ((Width + TileSize - 1) / TileSize) + tileX;
        if (_tiles.TryGetValue(key, out Tile? existing)) return existing;

        PixelRect rect = new PixelRect(tileX * TileSize, tileY * TileSize, TileSize, TileSize)
            .Intersect(new PixelRect(0, 0, Width, Height));

        // A patch replaces its whole tile, so a new tile starts as a copy of what is under it.
        // Only the pixels a dab actually reaches are rebuilt after that.
        var tile = new Tile
        {
            Rect = rect,
            Coverage = new byte[rect.Width * rect.Height],
            Pixels = _base is PixelBuffer image ? PixelRegion.Copy(image, rect)
                                                : PixelBuffer.Allocate(rect.Width, rect.Height),
        };

        _tiles[key] = tile;
        return tile;
    }

    /// <summary>
    /// The tip, rendered once and stamped for every dab.
    /// </summary>
    /// <remarks>
    /// Drawing the falloff for each dab is what upstream found made strokes stutter — a 60-pixel
    /// brush lays about fourteen dabs per mouse move. The shape is upstream's: solid to the
    /// hardness radius, then a normalised Gaussian that reaches zero exactly at the rim. A hard tip
    /// has no falloff at all and gets a single pixel of antialiasing instead, so its edge is smooth
    /// without being soft.
    /// </remarks>
    private static (byte[] Tip, int Size) Tip(BrushSettings settings)
    {
        double radius = settings.Radius;
        int size = (int)Math.Ceiling(radius * 2 + 2);
        var tip = new byte[size * size];

        double centre = size / 2.0;
        double inner = radius * Math.Clamp(settings.Hardness, 0, 1);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x + 0.5 - centre, dy = y + 0.5 - centre;
                double distance = Math.Sqrt(dx * dx + dy * dy);

                double value;
                if (settings.Hardness >= 1)
                {
                    value = Math.Clamp(radius + 0.5 - distance, 0, 1);
                }
                else if (distance <= inner)
                {
                    value = 1;
                }
                else if (distance >= radius)
                {
                    value = 0;
                }
                else
                {
                    value = Falloff((distance - inner) / Math.Max(1e-9, radius - inner));
                }

                tip[y * size + x] = (byte)Math.Round(Math.Clamp(value, 0, 1) * 255, MidpointRounding.AwayFromZero);
            }
        }

        return (tip, size);
    }

    /// <summary>
    /// Upstream's soft-brush falloff: a Gaussian normalised to reach zero at the rim.
    /// </summary>
    private static double Falloff(double u)
    {
        const double K = 2.5;
        return Math.Max(0, (Math.Exp(-K * u * u) - Math.Exp(-K)) / (1 - Math.Exp(-K)));
    }

    public void Dispose()
    {
        foreach (Tile tile in _tiles.Values)
        {
            tile.Pixels.Release();
            tile.Softened?.Release();
        }

        _tiles.Clear();
    }
}
