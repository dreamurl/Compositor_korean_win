namespace Compositor_korean_win.Core;

/// <summary>
/// Putting colour on a layer through coverage — the step every fill-like tool ends with.
/// </summary>
/// <remarks>
/// The brush has its own because it composes tiles; everything that fills a shape at once comes
/// here. Keeping it in one place is what stops the gradient and the shape tools drifting apart on
/// the details nobody tests directly: premultiplication, rounding, and what a selection does to a
/// partly covered pixel.
/// </remarks>
public static class Painting
{
    /// <summary>Composites one colour over a buffer, through coverage.</summary>
    public static void Fill(PixelBuffer target, PixelRect region, byte[] coverage,
                            Rgba colour, double opacity = 1) =>
        Fill(target, region, coverage, (_, _) => colour, opacity);

    /// <summary>
    /// Composites a colour that varies from pixel to pixel over a buffer, through coverage.
    /// </summary>
    /// <param name="coverage">One byte per pixel of <paramref name="region"/>.</param>
    /// <param name="colourAt">Called with layer pixel coordinates.</param>
    public static void Fill(PixelBuffer target, PixelRect region, byte[] coverage,
                            Func<int, int, Rgba> colourAt, double opacity = 1)
    {
        if (coverage.LongLength != (long)region.Width * region.Height)
            throw new ArgumentException("one byte per pixel of the region", nameof(coverage));

        PixelRect area = region.Intersect(new PixelRect(0, 0, target.Width, target.Height));
        if (area.IsEmpty) return;

        for (int y = area.Y; y < area.Bottom; y++)
        {
            Span<byte> row = target.Row(y);

            for (int x = area.X; x < area.Right; x++)
            {
                double alpha = coverage[(y - region.Y) * region.Width + (x - region.X)] / 255.0
                               * Math.Clamp(opacity, 0, 1);
                if (alpha <= 0) continue;

                Rgba colour = colourAt(x, y);
                double strength = alpha * colour.A / 255.0;
                if (strength <= 0) continue;

                Span<byte> pixel = row.Slice(x * 4, 4);

                // Premultiplied throughout, as every buffer in this port is.
                pixel[0] = Mix(colour.R, pixel[0], strength);
                pixel[1] = Mix(colour.G, pixel[1], strength);
                pixel[2] = Mix(colour.B, pixel[2], strength);
                pixel[3] = Mix(255, pixel[3], strength);
            }
        }
    }

    private static byte Mix(int source, int destination, double strength) => (byte)Math.Clamp(
        Math.Round(source * strength + destination * (1 - strength), MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>
    /// Coverage for a shape over a region, multiplied by a selection if there is one.
    /// </summary>
    /// <remarks>
    /// A selection is not a special case for a tool to remember: it is another coverage, and two
    /// coverages multiply. That is the whole of what "the fill stays inside the selection" means.
    /// </remarks>
    public static byte[] CoverageFor(DocumentSelection shape, PixelRect region, DocumentSelection? selection)
    {
        byte[] levels = shape.Levels(region);
        if (selection is null) return levels;

        byte[] allowed = selection.Levels(region);
        for (int i = 0; i < levels.Length; i++) levels[i] = (byte)(levels[i] * allowed[i] / 255);
        return levels;
    }
}
