namespace Compositor_korean_win.Core;

/// <summary>How a gradient spreads from where the drag began.</summary>
public enum GradientKind
{
    /// <summary>Along the drag, square to it, and flat either side.</summary>
    Linear,

    /// <summary>Out from where the drag began, reaching the second colour at its end.</summary>
    Radial,
}

/// <summary>Which palette colours make the two ends of a gradient.</summary>
public enum GradientStyle
{
    ForegroundToBackground,
    ForegroundToTransparent,
}

/// <summary>A gradient: two colours, a direction and a shape.</summary>
public sealed record GradientSettings
{
    public GradientKind Kind { get; init; } = GradientKind.Linear;

    public Rgba From { get; init; } = Rgba.Black;

    public Rgba To { get; init; } = Rgba.White;

    public GradientStyle Style { get; init; } = GradientStyle.ForegroundToTransparent;

    public bool Reversed { get; init; }

    public double Opacity { get; init; } = 1;

    /// <summary>The actual endpoints after style and direction have been applied.</summary>
    public (Rgba From, Rgba To) Colours()
    {
        Rgba first = From;
        Rgba second = Style == GradientStyle.ForegroundToBackground
            ? To
            : new Rgba(From.R, From.G, From.B, 0);
        return Reversed ? (second, first) : (first, second);
    }
}

/// <summary>
/// The gradient tool: a fill whose colour depends on where the pixel is.
/// </summary>
/// <remarks>
/// Upstream fills the canvas, or the selection when there is one, which is the same statement made
/// twice: the coverage is the selection's, or everything. Both go through
/// <see cref="Painting.Fill(PixelBuffer, PixelRect, byte[], Func{int, int, Rgba}, double)"/>, so a
/// gradient's soft edge against a selection is the selection's own edge and not an approximation
/// of it.
/// </remarks>
public static class GradientTool
{
    /// <summary>
    /// <paramref name="target"/> with a gradient drawn from <paramref name="start"/> to
    /// <paramref name="end"/>, in layer pixels.
    /// </summary>
    /// <remarks>The caller owns the result and releases it.</remarks>
    public static PixelBuffer Draw(PixelBuffer? target, int width, int height,
                                   Point start, Point end, GradientSettings settings,
                                   DocumentSelection? selection = null)
    {
        PixelBuffer result = target is PixelBuffer source
            ? PixelRegion.Copy(source, new PixelRect(0, 0, width, height))
            : PixelBuffer.Allocate(width, height);

        var whole = new PixelRect(0, 0, width, height);
        PixelRect region = selection is null
            ? whole
            : selection.Bounds.Inflate(1).Enclosing().Intersect(whole);

        if (region.IsEmpty) return result;

        byte[] coverage = new byte[region.Width * region.Height];
        if (selection is null) Array.Fill(coverage, (byte)255);
        else coverage = selection.Levels(region);

        Painting.Fill(result, region, coverage, (x, y) => ColorAt(x, y, start, end, settings),
                      settings.Opacity);

        return result;
    }

    /// <summary>The colour a gradient puts at one pixel.</summary>
    private static Rgba ColorAt(int x, int y, Point start, Point end, GradientSettings settings)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        double px = x + 0.5 - start.X, py = y + 0.5 - start.Y;

        double t;
        if (settings.Kind == GradientKind.Radial)
        {
            double radius = Math.Sqrt(dx * dx + dy * dy);
            t = radius <= 0 ? 1 : Math.Sqrt(px * px + py * py) / radius;
        }
        else
        {
            double lengthSquared = dx * dx + dy * dy;

            // A drag that went nowhere has no direction to spread along, so it is all the far
            // colour — which is what upstream's zero-length gradient comes out as.
            t = lengthSquared <= 0 ? 1 : (px * dx + py * dy) / lengthSquared;
        }

        t = Math.Clamp(t, 0, 1);

        (Rgba from, Rgba to) = settings.Colours();
        return new Rgba(Between(from.R, to.R, t),
                        Between(from.G, to.G, t),
                        Between(from.B, to.B, t),
                        Between(from.A, to.A, t));
    }

    private static byte Between(byte from, byte to, double t) =>
        (byte)Math.Clamp(Math.Round(from + (to - from) * t, MidpointRounding.AwayFromZero), 0, 255);
}
