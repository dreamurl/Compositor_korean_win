namespace Compositor_korean_win.Core;

/// <summary>A Levels histogram: one combined channel and one per colour channel, 256 bins each.</summary>
public sealed class Histogram
{
    public const int BinCount = 256;

    private readonly double[] _bins = new double[4 * BinCount];

    /// <summary>Weighted pixel counts, darkest first. Channel 0 is the combined one.</summary>
    public ReadOnlySpan<double> Channel(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 3);
        return _bins.AsSpan(channel * BinCount, BinCount);
    }

    /// <summary>
    /// Measures <paramref name="buffer"/>, one row at a time because the kernel takes a flat run of
    /// pixels and rows are padded to the stride.
    /// </summary>
    public static unsafe Histogram Measure(PixelBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var histogram = new Histogram();
        fixed (double* bins = histogram._bins)
        {
            for (int y = 0; y < buffer.Height; y++)
            {
                nint row = buffer.Scan0 + (nint)((long)y * buffer.Stride);
                Kernels.LevelsHistogram(row, 0, (nuint)buffer.Width, (nint)bins);
            }
        }
        return histogram;
    }
}
