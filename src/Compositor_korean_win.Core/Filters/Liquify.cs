namespace Compositor_korean_win.Core;

/// <summary>The brushes of Filter › Liquify — Photoshop's, less Smooth and the face tools.</summary>
public enum LiquifyTool
{
    /// <summary>Pushes the picture along with the brush.</summary>
    Forward,

    /// <summary>Paints the picture back towards how it began.</summary>
    Reconstruct,

    /// <summary>Turns what is under the brush clockwise while held; Alt turns it the other way.</summary>
    Twirl,

    /// <summary>Draws what is under the brush in towards its centre.</summary>
    Pucker,

    /// <summary>Swells what is under the brush out from its centre.</summary>
    Bloat,

    /// <summary>Moves the picture to the left of the way the brush goes.</summary>
    PushLeft,

    /// <summary>Protects what it paints over from every other brush.</summary>
    Freeze,

    /// <summary>Takes that protection off again.</summary>
    Thaw,
}

/// <summary>
/// Filter › Liquify: how far each part of a layer has been pushed, and the brushes that push it.
/// </summary>
/// <remarks>
/// <para>
/// Photoshop's Liquify keeps a mesh of displacements rather than changing pixels as it goes, which
/// is what lets Reconstruct paint the original back and lets every brush work on the picture as it
/// began rather than on a picture already resampled many times over. This does the same. For each
/// point of the layer the field says where in the original to read (the backward map), so the
/// result is always one resample of the original, however many strokes went into it.
/// </para>
/// <para>
/// The field is kept on a grid a few pixels apart on a large layer — about a million points at
/// most — and read between its points bilinearly. Brushes are far larger than that spacing, so
/// the result is as smooth as a field at every pixel and a fraction of the memory.
/// </para>
/// <para>
/// This is separate from the Blur tool's Liquify mode (<see cref="WarpStroke"/>), upstream's, which
/// pushes the pixels themselves one dab at a time.
/// </para>
/// </remarks>
public sealed class LiquifyField
{
    private readonly float[] _dx, _dy, _frozen;

    public LiquifyField(int width, int height)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "a layer of no size");
        Width = width;
        Height = height;
        Step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)width * height / 1_000_000)));
        Columns = (width + Step - 1) / Step + 1;
        Rows = (height + Step - 1) / Step + 1;
        _dx = new float[Columns * Rows];
        _dy = new float[Columns * Rows];
        _frozen = new float[Columns * Rows];
    }

    /// <summary>The layer's size in its own pixels.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>Layer pixels between neighbouring points of the grid.</summary>
    public int Step { get; }

    public int Columns { get; }

    public int Rows { get; }

    /// <summary>Whether anything has been pushed at all.</summary>
    public bool IsIdentity
    {
        get
        {
            for (int i = 0; i < _dx.Length; i++)
                if (_dx[i] != 0 || _dy[i] != 0) return false;
            return true;
        }
    }

    /// <summary>Whether any of the layer is frozen, for showing it.</summary>
    public bool HasFrozen { get; private set; }

    /// <summary>Counts every change, so a preview can tell whether it is still showing the field.</summary>
    public int Revision { get; private set; }

    private PixelRect _dirty;

    /// <summary>
    /// The part of the layer (its own pixels) changed since the last time this was asked, and a
    /// clean start again — so a preview redraws only under the brush rather than the whole layer.
    /// </summary>
    public PixelRect TakeDirty()
    {
        PixelRect dirty = _dirty;
        _dirty = default;
        return dirty;
    }

    private void Changed(PixelRect area)
    {
        Revision++;
        _dirty = _dirty.IsEmpty ? area : PixelRect.FromBounds(Math.Min(_dirty.X, area.X), Math.Min(_dirty.Y, area.Y),
                                                               Math.Max(_dirty.Right, area.Right), Math.Max(_dirty.Bottom, area.Bottom));
    }

    /// <summary>Everything back to how it began, frozen parts excepted — Photoshop's Restore All.</summary>
    public void Reset()
    {
        for (int i = 0; i < _dx.Length; i++)
        {
            float keep = _frozen[i];
            _dx[i] *= keep;
            _dy[i] *= keep;
        }
        Changed(new PixelRect(0, 0, Width, Height));
    }

    /// <summary>Where the original is read for layer point (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public (double X, double Y) Source(double x, double y)
    {
        (float dx, float dy, _) = Read(x, y);
        return (x + dx, y + dy);
    }

    /// <summary>How frozen layer point (<paramref name="x"/>, <paramref name="y"/>) is, 0–1.</summary>
    public double FrozenAt(double x, double y) => Read(x, y).Frozen;

    private (float Dx, float Dy, float Frozen) Read(double x, double y)
    {
        double gx = Math.Clamp(x / Step, 0, Columns - 1), gy = Math.Clamp(y / Step, 0, Rows - 1);
        int x0 = Math.Min((int)gx, Columns - 2), y0 = Math.Min((int)gy, Rows - 2);
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);
        float tx = (float)(gx - x0), ty = (float)(gy - y0);
        int x1 = Math.Min(x0 + 1, Columns - 1), y1 = Math.Min(y0 + 1, Rows - 1);
        int a = y0 * Columns + x0, b = y0 * Columns + x1, c = y1 * Columns + x0, d = y1 * Columns + x1;
        float Mix(float[] f) => (f[a] * (1 - tx) + f[b] * tx) * (1 - ty) + (f[c] * (1 - tx) + f[d] * tx) * ty;
        return (Mix(_dx), Mix(_dy), Mix(_frozen));
    }

    /// <summary>
    /// One dab of <paramref name="tool"/> at <paramref name="centre"/> (layer pixels), with a
    /// brush <paramref name="radius"/> across and <paramref name="pressure"/> 0–1. Forward and Push
    /// Left carry the picture by <paramref name="motion"/>, how far the brush moved since the last
    /// dab; Twirl turns the other way with <paramref name="reverse"/>.
    /// </summary>
    public void Dab(LiquifyTool tool, Point centre, double radius, double pressure, Point motion, bool reverse = false)
    {
        radius = Math.Max(1, radius);
        pressure = Math.Clamp(pressure, 0, 1);
        int left = Math.Max(0, (int)Math.Floor((centre.X - radius) / Step));
        int right = Math.Min(Columns - 1, (int)Math.Ceiling((centre.X + radius) / Step));
        int top = Math.Max(0, (int)Math.Floor((centre.Y - radius) / Step));
        int bottom = Math.Min(Rows - 1, (int)Math.Ceiling((centre.Y + radius) / Step));
        if (left > right || top > bottom) return;

        // New values are worked out from the field as it was before this dab, then written, so a
        // point read by its neighbour's move is not one already moved.
        int span = right - left + 1;
        var nextX = new float[span * (bottom - top + 1)];
        var nextY = new float[nextX.Length];
        var nextFrozen = new float[nextX.Length];

        for (int row = top; row <= bottom; row++)
        {
            for (int column = left; column <= right; column++)
            {
                int i = row * Columns + column, k = (row - top) * span + (column - left);
                nextX[k] = _dx[i];
                nextY[k] = _dy[i];
                nextFrozen[k] = _frozen[i];

                double px = column * Step, py = row * Step;
                double distance = Math.Sqrt((px - centre.X) * (px - centre.X) + (py - centre.Y) * (py - centre.Y)) / radius;
                if (distance >= 1) continue;

                // A soft round brush: full at the centre, easing to nothing at the rim.
                double falloff = (1 - distance * distance) * (1 - distance * distance);
                double weight = falloff * pressure;

                if (tool is LiquifyTool.Freeze or LiquifyTool.Thaw)
                {
                    nextFrozen[k] = (float)Math.Clamp(_frozen[i] + (tool == LiquifyTool.Freeze ? weight : -weight), 0, 1);
                    continue;
                }

                weight *= 1 - _frozen[i];
                if (weight <= 0) continue;

                // Where this point should now read from, in layer pixels, as a point before it moved.
                double fromX, fromY;
                switch (tool)
                {
                    case LiquifyTool.Forward:
                        fromX = px - motion.X * weight;
                        fromY = py - motion.Y * weight;
                        break;

                    case LiquifyTool.PushLeft:
                        // Left of the way the brush goes, on a screen where y runs down.
                        fromX = px - motion.Y * weight;
                        fromY = py + motion.X * weight;
                        break;

                    case LiquifyTool.Twirl:
                    {
                        double turn = (reverse ? 1 : -1) * 0.06 * weight;
                        double cos = Math.Cos(turn), sin = Math.Sin(turn), ox = px - centre.X, oy = py - centre.Y;
                        fromX = centre.X + ox * cos - oy * sin;
                        fromY = centre.Y + ox * sin + oy * cos;
                        break;
                    }

                    case LiquifyTool.Pucker or LiquifyTool.Bloat:
                    {
                        double scale = 1 + (tool == LiquifyTool.Pucker ? 0.04 : -0.04) * weight;
                        fromX = centre.X + (px - centre.X) * scale;
                        fromY = centre.Y + (py - centre.Y) * scale;
                        break;
                    }

                    default:
                        // Reconstruct: a share of the way back to reading from where the point is.
                        nextX[k] = (float)(_dx[i] * (1 - 0.25 * weight));
                        nextY[k] = (float)(_dy[i] * (1 - 0.25 * weight));
                        continue;
                }

                (float dx, float dy, _) = Read(fromX, fromY);
                nextX[k] = (float)(fromX + dx - px);
                nextY[k] = (float)(fromY + dy - py);
            }
        }

        for (int row = top; row <= bottom; row++)
        {
            for (int column = left; column <= right; column++)
            {
                int i = row * Columns + column, k = (row - top) * span + (column - left);
                _dx[i] = nextX[k];
                _dy[i] = nextY[k];
                _frozen[i] = nextFrozen[k];
                if (nextFrozen[k] > 0) HasFrozen = true;
            }
        }

        // A point is read between it and its neighbours, so the pixels a grid step round the
        // points that moved change too.
        Changed(PixelRect.FromBounds((left - 1) * Step, (top - 1) * Step, (right + 1) * Step + 1, (bottom + 1) * Step + 1)
                    .Intersect(new PixelRect(0, 0, Width, Height)));
    }

    /// <summary>
    /// <paramref name="source"/> — the original, or a reduction of it <paramref name="unit"/> layer
    /// pixels to a pixel — read through the field over <paramref name="region"/> of that grid. With
    /// <paramref name="showFrozen"/> the frozen parts are tinted red, as Photoshop shows its mask.
    /// </summary>
    public PixelBuffer Render(PixelBuffer source, int unit, PixelRect region, bool showFrozen = false)
    {
        PixelBuffer result = PixelBuffer.Allocate(region.Width, region.Height);
        RenderInto(result, region, source, unit, region, showFrozen);
        return result;
    }

    /// <summary>
    /// Like <see cref="Render"/>, but into part of a buffer already made: <paramref name="target"/>
    /// covers <paramref name="covers"/> of the grid, and only <paramref name="part"/> of it is drawn.
    /// </summary>
    public void RenderInto(PixelBuffer target, PixelRect covers, PixelBuffer source, int unit, PixelRect part,
                           bool showFrozen = false)
    {
        PixelRect region = part.Intersect(covers);
        if (region.IsEmpty) return;
        bool tint = showFrozen && HasFrozen;
        Parallel.For(0, region.Height, y =>
        {
            Span<byte> row = target.Row(region.Y - covers.Y + y).Slice((region.X - covers.X) * 4);
            for (int x = 0; x < region.Width; x++)
            {
                double lx = (region.X + x + 0.5) * unit, ly = (region.Y + y + 0.5) * unit;
                (float dx, float dy, float frozen) = Read(lx, ly);
                Span<byte> pixel = row.Slice(x * 4, 4);
                PixelSampling.Bilinear(source, (lx + dx) / unit, (ly + dy) / unit, pixel);

                if (tint && frozen > 0)
                {
                    // Half-strength red over the frozen part, opaque enough to see on any picture.
                    float share = 0.5f * frozen;
                    pixel[0] = (byte)(pixel[0] + (255 - pixel[0]) * share);
                    pixel[1] = (byte)(pixel[1] * (1 - share));
                    pixel[2] = (byte)(pixel[2] * (1 - share));
                    pixel[3] = (byte)(pixel[3] + (255 - pixel[3]) * share);
                }
            }
        });
    }

    /// <summary>
    /// The layer liquified for good, its mask with it when the mask shares the layer's pixels. The
    /// layer keeps its size and place; what is pushed past its edge is gone, as in Photoshop.
    /// </summary>
    public ImageLayer Apply(ImageLayer layer)
    {
        if (layer.Image is not PixelBuffer image || IsIdentity) return layer;

        PixelBuffer pixels = Render(image, 1, new PixelRect(0, 0, image.Width, image.Height));
        LayerMask? mask = layer.Mask;
        if (mask is { IsLinked: true, Placement: null } && mask.Coverage is { Width: > 1 } coverage
            && coverage.Width == image.Width && coverage.Height == image.Height)
        {
            PixelBuffer moved = Render(coverage, 1, new PixelRect(0, 0, coverage.Width, coverage.Height));
            QuadWarp.Opaque(moved);
            mask = mask with { Coverage = moved };
        }

        return layer with { Image = pixels, Mask = mask, Shape = null };
    }
}

/// <summary>
/// A Liquify session's picture in the frame: the field read over the part of the layer the window
/// shows, at the reduction the zoom calls for — the filter preview's way (<see cref="FilterPreview"/>).
/// </summary>
public sealed class LiquifyPreview(ImageLayer layer) : IDisposable
{
    private readonly DownsamplePyramid _pyramid = new();
    private PixelBuffer? _last;
    private (int Level, PixelRect Crop)? _shown;
    private int _revision;

    public ImageLayer Layer { get; } = layer;

    public LiveEdit? Frame(LiquifyField field, CanvasProjection projection, int width, int height)
    {
        if (Layer.Image is not PixelBuffer image) return null;
        int w = image.Width, h = image.Height;

        int level = LayerGeometry.LevelFor(projection.Apply(Layer.Transform), w);
        (PixelBuffer reduced, int applied) = _pyramid.Reduced(image, level);
        int unit = 1 << applied;

        // Only the part of the reduced layer the window shows, and a pixel round it for the resample.
        var grid = new PixelRect(0, 0, reduced.Width, reduced.Height);
        LayerTransform gridPlacement = LayerGeometry.Place(Layer.Transform, Scaled(grid, unit), w, h);
        LayerTransform onSurface = projection.Apply(gridPlacement);
        PixelRect area = LayerGeometry.Bounds(onSurface).Intersect(new PixelRect(0, 0, width, height));
        if (area.IsEmpty) return null;
        PixelRect crop = LayerGeometry.SourceRegion(area, onSurface, grid.Width, grid.Height).Inflate(1).Intersect(grid);
        if (crop.IsEmpty) return null;

        // The frame before, where it still stands: nothing to do when the field has not changed —
        // a pointer only hovering — and under the brush only when it has.
        if (_last is PixelBuffer last && _shown == (applied, crop))
        {
            if (field.Revision != _revision)
            {
                PixelRect dirty = field.TakeDirty();
                var inGrid = PixelRect.FromBounds(dirty.X / unit - 1, dirty.Y / unit - 1,
                                                  (dirty.Right + unit - 1) / unit + 1, (dirty.Bottom + unit - 1) / unit + 1);
                // A new buffer each change: buffers are never changed once drawn from.
                PixelBuffer next = PixelRegion.Copy(last, new PixelRect(0, 0, last.Width, last.Height));
                field.RenderInto(next, crop, reduced, unit, inGrid, showFrozen: true);
                last.Release();
                _last = next;
                _revision = field.Revision;
            }
        }
        else
        {
            PixelBuffer pixels = field.Render(reduced, unit, crop, showFrozen: true);
            field.TakeDirty();
            _last?.Release();
            _last = pixels;
            _shown = (applied, crop);
            _revision = field.Revision;
        }
        PixelBuffer shownPixels = _last!;

        return new LiveEdit(Layer.Id, new BufferSource(shownPixels) { Cacheable = false })
        {
            Placement = LayerGeometry.Place(Layer.Transform, Scaled(crop, unit), w, h),
        };
    }

    private static PixelRect Scaled(PixelRect region, int unit) =>
        new(region.X * unit, region.Y * unit, region.Width * unit, region.Height * unit);

    public void Dispose()
    {
        _last?.Release();
        _last = null;
        _pyramid.Dispose();
    }
}
