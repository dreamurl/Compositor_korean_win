namespace Compositor_korean_win.Core;

/// <summary>What the blur tool does under the brush — upstream's <c>BlurToolMode</c>.</summary>
public enum BlurToolMode
{
    /// <summary>Softens what is there (<see cref="BrushMode.Blur"/>).</summary>
    Blur,

    /// <summary>Drags colour along the stroke, as a finger through wet paint.</summary>
    Smudge,

    /// <summary>Pushes pixels along with the brush, most at its centre and none at its rim.</summary>
    Liquify,
}

/// <summary>
/// A Smudge or Liquify stroke in progress, over one layer's pixel grid — upstream's
/// <c>WarpStroke</c>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="BrushStroke"/> this does not keep coverage and compose once: each dab reads
/// what the one before it left, which is the whole effect — a smudge carries colour it picked up
/// a moment ago, a push moves pixels that were already moved. So the stroke works on a copy of the
/// layer and changes it in place, dab by dab.
/// </para>
/// <para>
/// Upstream does this at document size and then paints the result back into the layer along the
/// stroke's path. Here it happens in the layer's own grid, as every other tool does
/// (docs/progress.md 5.1), so there is no resampling on the way in or out and the diameter is in
/// layer pixels like the brush's.
/// </para>
/// </remarks>
public sealed class WarpStroke : IDisposable
{
    private readonly PixelBuffer _original;
    private readonly PixelBuffer _pixels;
    private readonly BlurToolMode _mode;
    private readonly double _diameter;
    private readonly double _hardness;
    private readonly double _strength;
    private Point? _last;

    /// <summary>Smudge: the colour the brush carries, a (2r+1)² square of RGBA.</summary>
    private float[] _carried = [];
    private float[] _scratch = [];

    public WarpStroke(PixelBuffer image, BlurToolMode mode, BrushSettings settings)
    {
        if (mode == BlurToolMode.Blur) throw new ArgumentException("Blur is a brush stroke, not a warp", nameof(mode));

        _original = image.Retain();
        _pixels = PixelFilters.Copy(image);
        _mode = mode;
        _diameter = Math.Max(2, settings.Diameter);
        _hardness = Math.Clamp(settings.Hardness, 0, 0.98);
        _strength = Math.Clamp(settings.Opacity, 0.01, 1);
    }

    /// <summary>The layer as the stroke has left it so far. It changes as the stroke goes on.</summary>
    public PixelBuffer Pixels => _pixels;

    public int Width => _pixels.Width;

    public int Height => _pixels.Height;

    /// <summary>The part of the layer any dab has reached.</summary>
    public PixelRect Dirty { get; private set; }

    public bool IsEmpty => Dirty.IsEmpty;

    private int Radius => (int)Math.Ceiling(_diameter / 2);

    /// <summary>How much a dab moves pixels at <paramref name="u"/> (0 centre, 1 rim) from its centre.</summary>
    private float Weight(float u)
    {
        if (u >= 1) return 0;
        float hard = (float)_hardness;
        if (u <= hard) return 1;
        float t = (1 - u) / (1 - hard);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Carries the stroke on to <paramref name="point"/>, in layer pixels, dabbing along the way.</summary>
    /// <remarks>
    /// The first point only picks up colour. After that the dabs are upstream's distance apart; a
    /// move shorter than that waits for the next, so a slow drag does not dab on the spot.
    /// </remarks>
    public void Append(Point point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return;

        if (_last is not Point from)
        {
            _last = point;
            if (_mode == BlurToolMode.Smudge) PickUp(point);
            return;
        }

        double distance = Math.Sqrt((point.X - from.X) * (point.X - from.X) + (point.Y - from.Y) * (point.Y - from.Y));
        double spacing = Math.Max(1, _diameter * (_mode == BlurToolMode.Smudge ? 0.08 : 0.025));
        if (distance < spacing) return;

        int steps = (int)Math.Ceiling(distance / spacing);
        Point previous = from;
        for (int step = 1; step <= steps; step++)
        {
            double t = (double)step / steps;
            var next = new Point(from.X + (point.X - from.X) * t, from.Y + (point.Y - from.Y) * t);
            if (_mode == BlurToolMode.Smudge) Smudge(next);
            else Push(previous, next);
            previous = next;
        }

        _last = point;
    }

    /// <summary>
    /// The finished layer: the stroke's pixels, held back to the old ones wherever the selection
    /// does not reach. The caller owns the result.
    /// </summary>
    /// <param name="selection">The selection in this layer's pixels, or null for none.</param>
    public PixelBuffer Commit(DocumentSelection? selection)
    {
        PixelBuffer result = PixelFilters.Copy(_pixels);
        if (selection is null) return result;

        (PixelRect region, byte[] levels) = selection.Clip(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            ReadOnlySpan<byte> before = _original.Row(y);
            Span<byte> after = result.Row(y);
            for (int x = 0; x < Width; x++)
            {
                int level = region.IsEmpty || x < region.X || y < region.Y || x >= region.Right || y >= region.Bottom
                    ? 0
                    : levels[(y - region.Y) * region.Width + (x - region.X)];
                if (level == 255) continue;

                int i = x * 4;
                for (int c = 0; c < 4; c++)
                    after[i + c] = (byte)((before[i + c] * (255 - level) + after[i + c] * level + 127) / 255);
            }
        }

        return result;
    }

    private void Touch(int cx, int cy, int reach)
    {
        PixelRect touched = new PixelRect(cx - reach, cy - reach, reach * 2 + 1, reach * 2 + 1)
            .Intersect(new PixelRect(0, 0, Width, Height));
        if (touched.IsEmpty) return;

        Dirty = Dirty.IsEmpty ? touched : PixelRect.FromBounds(
            Math.Min(Dirty.X, touched.X), Math.Min(Dirty.Y, touched.Y),
            Math.Max(Dirty.Right, touched.Right), Math.Max(Dirty.Bottom, touched.Bottom));
    }

    private void PickUp(Point centre)
    {
        int r = Radius, side = 2 * r + 1;
        _carried = new float[side * side * 4];
        int cx = (int)Math.Round(centre.X), cy = (int)Math.Round(centre.Y);

        for (int dy = -r; dy <= r; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= Height) continue;
            ReadOnlySpan<byte> row = _pixels.Row(y);

            for (int dx = -r; dx <= r; dx++)
            {
                int x = cx + dx;
                if (x < 0 || x >= Width) continue;
                int c = ((dy + r) * side + dx + r) * 4;
                for (int k = 0; k < 4; k++) _carried[c + k] = row[x * 4 + k];
            }
        }
    }

    private void Smudge(Point centre)
    {
        int r = Radius, side = 2 * r + 1;
        int cx = (int)Math.Round(centre.X), cy = (int)Math.Round(centre.Y);
        float keep = (float)_strength, inverse = 1 / (float)(_diameter / 2);

        for (int dy = -r; dy <= r; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= Height) continue;
            Span<byte> row = _pixels.Row(y);

            for (int dx = -r; dx <= r; dx++)
            {
                int x = cx + dx;
                if (x < 0 || x >= Width) continue;
                float w = Weight(MathF.Sqrt(dx * dx + dy * dy) * inverse);
                if (w <= 0) continue;

                int p = x * 4, c = ((dy + r) * side + dx + r) * 4;
                for (int k = 0; k < 4; k++)
                {
                    float under = row[p + k];
                    float painted = under + (_carried[c + k] - under) * w;
                    row[p + k] = (byte)Math.Clamp(MathF.Round(painted), 0, 255);
                    // The brush picks up some of what it just left, more the weaker the smudge.
                    _carried[c + k] = painted + (_carried[c + k] - painted) * keep;
                }
            }
        }

        Touch(cx, cy, r);
    }

    /// <summary>Forward warp: pixels under the brush move with it, most at its centre, none at its rim.</summary>
    private void Push(Point a, Point b)
    {
        int r = Radius;
        float moveX = (float)((b.X - a.X) * _strength), moveY = (float)((b.Y - a.Y) * _strength);
        int margin = (int)Math.Ceiling(Math.Max(Math.Abs(moveX), Math.Abs(moveY))) + 2;
        int cx = (int)Math.Round(b.X), cy = (int)Math.Round(b.Y);

        // A copy of the area as it was before this dab, which the dab samples from.
        int x0 = Math.Max(0, cx - r - margin), x1 = Math.Min(Width - 1, cx + r + margin);
        int y0 = Math.Max(0, cy - r - margin), y1 = Math.Min(Height - 1, cy + r + margin);
        if (x0 > x1 || y0 > y1) return;

        int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
        if (cw < 2 || ch < 2) return;
        if (_scratch.Length < cw * ch * 4) _scratch = new float[cw * ch * 4];

        for (int y = 0; y < ch; y++)
        {
            ReadOnlySpan<byte> row = _pixels.Row(y + y0);
            for (int x = 0; x < cw; x++)
            {
                int p = (x + x0) * 4, s = (y * cw + x) * 4;
                for (int k = 0; k < 4; k++) _scratch[s + k] = row[p + k];
            }
        }

        float inverse = 1 / (float)(_diameter / 2);
        for (int dy = -r; dy <= r; dy++)
        {
            int y = cy + dy;
            if (y < y0 || y > y1) continue;
            Span<byte> row = _pixels.Row(y);

            for (int dx = -r; dx <= r; dx++)
            {
                int x = cx + dx;
                if (x < x0 || x > x1) continue;
                float w = Weight(MathF.Sqrt(dx * dx + dy * dy) * inverse);
                if (w <= 0) continue;

                // Bilinear sample of the old pixels, from behind the brush's travel.
                float sx = Math.Clamp(x - x0 - moveX * w, 0, cw - 1);
                float sy = Math.Clamp(y - y0 - moveY * w, 0, ch - 1);
                int ix = Math.Min(cw - 2, (int)sx), iy = Math.Min(ch - 2, (int)sy);
                float fx = sx - ix, fy = sy - iy;

                int s00 = (iy * cw + ix) * 4, s10 = s00 + 4, s01 = s00 + cw * 4, s11 = s01 + 4;
                int p = x * 4;
                for (int k = 0; k < 4; k++)
                {
                    float top = _scratch[s00 + k] + (_scratch[s10 + k] - _scratch[s00 + k]) * fx;
                    float bottom = _scratch[s01 + k] + (_scratch[s11 + k] - _scratch[s01 + k]) * fx;
                    row[p + k] = (byte)Math.Clamp(MathF.Round(top + (bottom - top) * fy), 0, 255);
                }
            }
        }

        Touch(cx, cy, r);
    }

    public void Dispose()
    {
        _pixels.Release();
        _original.Release();
    }
}
