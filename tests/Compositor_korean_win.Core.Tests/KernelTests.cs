using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// The boundary to the eight upstream C kernels.
/// </summary>
/// <remarks>
/// These are the only upstream files this port reuses as they are, so what needs proving is not the
/// arithmetic — upstream's own tests cover that — but that the managed side hands them memory in the
/// layout they expect. A wrong stride, a wrong channel order or a wrong element count produces
/// plausible-looking output rather than a crash, which is exactly the kind of mistake that survives
/// until someone compares against macOS.
/// </remarks>
public class KernelTests
{
    [Fact]
    public void KernelsAreLinkedIn()
    {
        Assert.Equal(1, Kernels.AbiVersion());
    }

    [Fact]
    public void HistogramTotalMatchesTheOpaquePixelCount()
    {
        using var buffer = new Owned(Fill(16, 16, red: 128, green: 64, blue: 32, alpha: 255));

        Histogram histogram = Histogram.Measure(buffer.Value);

        Assert.Equal(16d * 16d, Total(histogram.Channel(0)), precision: 3);
        Assert.Equal(16d * 16d, Total(histogram.Channel(1)), precision: 3);
    }

    [Fact]
    public void HistogramPutsEachChannelInItsOwnBin()
    {
        using var buffer = new Owned(Fill(8, 8, red: 200, green: 100, blue: 50, alpha: 255));

        Histogram histogram = Histogram.Measure(buffer.Value);

        // Channels 1, 2 and 3 are red, green and blue. Every pixel is the same colour, so each
        // channel's weight lands wholly in one bin — and which bin proves the byte order.
        Assert.Equal(64d, histogram.Channel(1)[200], precision: 3);
        Assert.Equal(64d, histogram.Channel(2)[100], precision: 3);
        Assert.Equal(64d, histogram.Channel(3)[50], precision: 3);
    }

    [Fact]
    public void HistogramIgnoresTransparentPixels()
    {
        using var buffer = new Owned(Fill(8, 8, red: 0, green: 0, blue: 0, alpha: 0));

        Histogram histogram = Histogram.Measure(buffer.Value);

        Assert.Equal(0d, Total(histogram.Channel(0)), precision: 3);
    }

    [Fact]
    public void HistogramReadsEveryRowDespiteStridePadding()
    {
        // 5 pixels is 20 bytes, which the 32-byte row alignment pads. If the binding passed the
        // stride where the kernel wants a pixel count, the padding would be counted as pixels.
        using var buffer = new Owned(Fill(5, 4, red: 10, green: 10, blue: 10, alpha: 255));

        Histogram histogram = Histogram.Measure(buffer.Value);

        Assert.True(buffer.Value.Stride > buffer.Value.Width * 4);
        Assert.Equal(20d, Total(histogram.Channel(1)), precision: 3);
    }

    [Fact]
    public void NoiseChangesColourButKeepsAlpha()
    {
        using var buffer = new Owned(Fill(32, 32, red: 128, green: 128, blue: 128, alpha: 255));

        Kernels.NoiseAdd(buffer.Value.Scan0, (nuint)buffer.Value.Width, (nuint)buffer.Value.Height,
                         (nuint)buffer.Value.Stride, amount: 50f, gaussian: 0, monochromatic: 0, seed: 7);

        bool changed = false;
        for (int y = 0; y < buffer.Value.Height; y++)
        {
            Span<byte> row = buffer.Value.Row(y);
            for (int x = 0; x < buffer.Value.Width; x++)
            {
                Assert.Equal(255, row[x * 4 + 3]);
                if (row[x * 4] != 128) changed = true;
            }
        }

        Assert.True(changed, "noise_add left every pixel untouched");
    }

    [Fact]
    public void NoiseIsReproducibleFromItsSeed()
    {
        using var first = new Owned(Fill(16, 16, 128, 128, 128, 255));
        using var second = new Owned(Fill(16, 16, 128, 128, 128, 255));

        foreach (Owned buffer in new[] { first, second })
        {
            Kernels.NoiseAdd(buffer.Value.Scan0, (nuint)buffer.Value.Width, (nuint)buffer.Value.Height,
                             (nuint)buffer.Value.Stride, 40f, gaussian: 1, monochromatic: 1, seed: 99);
        }

        for (int y = 0; y < 16; y++)
            Assert.True(first.Value.Row(y).SequenceEqual(second.Value.Row(y)), $"row {y} differs");
    }

    [Fact]
    public unsafe void AlphaBoundsFindTheDrawnRegion()
    {
        using var buffer = new Owned(PixelBuffer.Allocate(32, 32));

        // A single opaque pixel at (10, 4). The bounds come back half-open.
        Span<byte> row = buffer.Value.Row(4);
        row[10 * 4 + 3] = 255;

        Span<nuint> bounds = stackalloc nuint[4];
        fixed (nuint* pointer = bounds)
        {
            Kernels.BrushAlphaBounds(buffer.Value.Scan0, (nuint)buffer.Value.Width,
                                     (nuint)buffer.Value.Height, (nuint)buffer.Value.Stride, (nint)pointer);
        }

        Assert.Equal(10u, (uint)bounds[0]);
        Assert.Equal(4u, (uint)bounds[1]);
        Assert.Equal(11u, (uint)bounds[2]);
        Assert.Equal(5u, (uint)bounds[3]);
    }

    private static double Total(ReadOnlySpan<double> bins)
    {
        double total = 0;
        foreach (double bin in bins) total += bin;
        return total;
    }

    private static PixelBuffer Fill(int width, int height, byte red, byte green, byte blue, byte alpha)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x * 4 + 0] = red;
                row[x * 4 + 1] = green;
                row[x * 4 + 2] = blue;
                row[x * 4 + 3] = alpha;
            }
        }
        return buffer;
    }

    private sealed class Owned(PixelBuffer value) : IDisposable
    {
        public PixelBuffer Value { get; } = value;
        public void Dispose() => Value.Release();
    }
}
