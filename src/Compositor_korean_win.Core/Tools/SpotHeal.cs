namespace Compositor_korean_win.Core;

/// <summary>Where spot healing looks for what to put in the spot's place.</summary>
public enum SpotHealingMode
{
    /// <summary>Copies from whichever nearby patch's surroundings best match the spot's.</summary>
    ContentAware = 0,

    /// <summary>Fills smoothly from the edges and adds grain to match the detail around it.</summary>
    CreateTexture = 1,

    /// <summary>Like content-aware, but takes the closest good patch rather than the best.</summary>
    ProximityMatch = 2,
}

/// <summary>
/// Spot healing and content-aware fill, both of which are upstream's C kernels.
/// </summary>
/// <remarks>
/// Neither is a filter: they search the image for something that belongs where the hole is, which
/// means reading a wide neighbourhood for every patch considered. That is why they are the two
/// tools upstream wrote in C, and why this port calls that C rather than porting it.
/// </remarks>
public static class SpotHeal
{
    /// <summary>
    /// How far past a spot the kernel looks for a patch, which is what a crop has to include.
    /// </summary>
    /// <remarks>
    /// Upstream's figure: about three spot-widths, plus a floor so a tiny spot still has somewhere
    /// to look. Crop any tighter and healing a blemish would only ever find the blemish.
    /// </remarks>
    public static int ReachFor(PixelRect painted) =>
        (int)Math.Ceiling((Math.Max(painted.Width, painted.Height) + 32) * 3.2);

    /// <summary>
    /// Heals <paramref name="region"/> in place, rebuilding what <paramref name="coverage"/> marks.
    /// </summary>
    /// <param name="coverage">One byte per pixel of the region, 0–255.</param>
    /// <returns>False when the kernel ran out of memory, in which case nothing was changed.</returns>
    public static unsafe bool Heal(PixelBuffer region, byte[] coverage, double opacity,
                                   SpotHealingMode mode, uint seed)
    {
        if (coverage.LongLength != (long)region.Width * region.Height)
            throw new ArgumentException("one byte per pixel of the region", nameof(coverage));

        fixed (byte* marked = coverage)
        {
            return Kernels.SpotHeal(region.Scan0, (nint)marked,
                                    (nuint)region.Width, (nuint)region.Height, (nuint)region.Stride,
                                    (float)Math.Clamp(opacity, 0, 1), (int)mode, seed) == 0;
        }
    }

    /// <summary>The bounds of what a coverage bitmap marks, as the kernel measures them.</summary>
    public static unsafe PixelRect CoverageBounds(byte[] coverage, int width, int height)
    {
        Span<long> bounds = stackalloc long[4];

        fixed (byte* marked = coverage)
        fixed (long* edges = bounds)
        {
            Kernels.HealCoverageBounds((nint)marked, (nuint)width, (nuint)height, (nuint)width, (nint)edges);
        }

        return PixelRect.FromBounds((int)bounds[0], (int)bounds[1], (int)bounds[2], (int)bounds[3]);
    }

    /// <summary>
    /// Fills what <paramref name="coverage"/> marks from elsewhere in the same image, in place.
    /// </summary>
    /// <returns>False when there was nothing to copy from, or memory ran out.</returns>
    public static unsafe bool ContentAwareFill(PixelBuffer image, byte[] coverage)
    {
        if (coverage.LongLength != (long)image.Width * image.Height)
            throw new ArgumentException("one byte per pixel of the image", nameof(coverage));

        fixed (byte* marked = coverage)
        {
            return Kernels.ContentFill(image.Scan0, (nuint)image.Stride, (nint)marked, (nuint)image.Width,
                                       image.Width, image.Height) == 1;
        }
    }
}
