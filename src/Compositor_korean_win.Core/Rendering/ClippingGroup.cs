namespace Compositor_korean_win.Core;

/// <summary>
/// The three steps that make a clipping group composite once instead of twice.
/// </summary>
/// <remarks>
/// <para>
/// A layer clipped to another shows only inside that layer's shape, and at full strength within it.
/// The obvious way to get that — restrict the clipped layer to the base's coverage and draw it over
/// — is wrong: over a half-transparent base, a clipped layer would only half-cover it, so the base
/// would show through something meant to hide it, and the result would end up more opaque than the
/// base was. Upstream's note is that source-over of two translucent copies thickens soft edges.
/// </para>
/// <para>
/// What actually works is to take the base's alpha off, make it opaque, let the clipped layers
/// composite against that normally, and put the alpha back at the end. The three kernels doing it
/// are upstream's own, reused unmodified.
/// </para>
/// </remarks>
public static class ClippingGroup
{
    /// <summary>An 8-bit coverage bitmap, one byte per pixel.</summary>
    public sealed class Coverage(int width, int height) : IDisposable
    {
        private byte[] _bytes = new byte[checked(width * height)];

        public int Width => width;
        public int Height => height;
        public int Stride => width;

        internal Span<byte> Bytes => _bytes;

        public byte this[int x, int y] => _bytes[y * Stride + x];

        public void Dispose() => _bytes = [];
    }

    /// <summary>Lifts the alpha channel off <paramref name="pixels"/>.</summary>
    public static unsafe Coverage ExtractAlpha(PixelBuffer pixels)
    {
        var coverage = new Coverage(pixels.Width, pixels.Height);
        fixed (byte* gray = coverage.Bytes)
        {
            Kernels.LayerExtractAlpha(pixels.Scan0, (nuint)pixels.Stride, (nint)gray, (nuint)coverage.Stride,
                                      (nuint)pixels.Width, (nuint)pixels.Height);
        }
        return coverage;
    }

    /// <summary>Divides the alpha back out and sets every pixel to fully opaque.</summary>
    public static void MakeOpaque(PixelBuffer pixels) =>
        Kernels.LayerUnpremultiplyOpaque(pixels.Scan0, (nuint)pixels.Stride,
                                         (nuint)pixels.Width, (nuint)pixels.Height);

    /// <summary>Puts a saved coverage back as the alpha, premultiplying the colour again.</summary>
    public static unsafe void RestoreAlpha(PixelBuffer pixels, Coverage coverage)
    {
        if (coverage.Width != pixels.Width || coverage.Height != pixels.Height)
            throw new ArgumentException("coverage is not the same size as the pixels", nameof(coverage));

        fixed (byte* gray = coverage.Bytes)
        {
            Kernels.LayerRestoreAlpha(pixels.Scan0, (nuint)pixels.Stride, (nint)gray, (nuint)coverage.Stride,
                                      (nuint)pixels.Width, (nuint)pixels.Height);
        }
    }

    /// <summary>The same coverage as an opaque grey buffer, for use as a mask.</summary>
    public static PixelBuffer AsMask(Coverage coverage)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(coverage.Width, coverage.Height);
        for (int y = 0; y < coverage.Height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < coverage.Width; x++)
            {
                byte value = coverage[x, y];
                row[x * 4 + 0] = value;
                row[x * 4 + 1] = value;
                row[x * 4 + 2] = value;
                row[x * 4 + 3] = 255;
            }
        }
        return buffer;
    }
}
