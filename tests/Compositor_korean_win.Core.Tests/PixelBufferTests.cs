using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// The sharing rules that keep memory flat.
/// </summary>
/// <remarks>
/// docs/windows-port.md §2.2 names reference-shared immutable pixels as the single biggest lever on
/// memory use: upstream's history keeps a hundred steps without copying a pixel. These tests pin the
/// behaviour that makes that possible, because a regression here would not show up as a failure —
/// it would show up as a document that quietly costs several times what it should.
/// </remarks>
public class PixelBufferTests
{
    [Fact]
    public void AllocateRoundsStrideUpForAlignment()
    {
        using var buffer = new Owned(PixelBuffer.Allocate(3, 2));

        Assert.Equal(3, buffer.Value.Width);
        Assert.Equal(2, buffer.Value.Height);
        Assert.True(buffer.Value.Stride >= 3 * 4);
        Assert.Equal(0, buffer.Value.Stride % 32);
        Assert.Equal(0, (int)(buffer.Value.Scan0 % 32));
    }

    [Fact]
    public void AllocateClearsToTransparent()
    {
        using var buffer = new Owned(PixelBuffer.Allocate(8, 8));

        for (int y = 0; y < 8; y++)
            foreach (byte value in buffer.Value.Row(y))
                Assert.Equal(0, value);
    }

    [Fact]
    public void RetainAndReleaseBalanceBeforeFreeing()
    {
        long before = PixelBuffer.LiveBytes;
        PixelBuffer buffer = PixelBuffer.Allocate(64, 64);

        Assert.Equal(1, buffer.ReferenceCount);
        Assert.True(PixelBuffer.LiveBytes > before);

        // A second holder — a history snapshot, say — takes a share of the same pixels.
        buffer.Retain();
        Assert.Equal(2, buffer.ReferenceCount);

        buffer.Release();
        Assert.Equal(1, buffer.ReferenceCount);
        Assert.True(PixelBuffer.LiveBytes > before);

        buffer.Release();
        Assert.Equal(before, PixelBuffer.LiveBytes);
    }

    [Fact]
    public void SharingDoesNotCopyPixels()
    {
        using var buffer = new Owned(PixelBuffer.Allocate(256, 256));
        long afterAllocate = PixelBuffer.LiveBytes;

        for (int i = 0; i < 100; i++) buffer.Value.Retain();

        // A hundred history steps holding the same image must not cost a hundred images.
        Assert.Equal(afterAllocate, PixelBuffer.LiveBytes);
        Assert.Equal(101, buffer.Value.ReferenceCount);

        for (int i = 0; i < 100; i++) buffer.Value.Release();
    }

    [Fact]
    public void LayeredBufferCopiesARegionWithoutFlatteningTheWholeImage()
    {
        using PixelBuffer original = RenderFixture.Solid(1024, 1024, 10, 20, 30);
        using PixelBuffer replacement = RenderFixture.Solid(16, 16, 200, 100, 50);
        using PixelBuffer layered = PixelBuffer.Layered(original,
            [new RasterPatch(new PixelRect(400, 500, 16, 16), replacement)]);

        Assert.True(layered.IsDeferred);
        using PixelBuffer region = PixelRegion.Copy(layered, new PixelRect(396, 496, 24, 24));

        Assert.True(layered.IsDeferred);
        Assert.Equal((10, 20, 30, 255), RenderFixture.At(region, 1, 1));
        Assert.Equal((200, 100, 50, 255), RenderFixture.At(region, 8, 8));
    }

    [Fact]
    public void ReleasingMoreOftenThanRetainedThrows()
    {
        PixelBuffer buffer = PixelBuffer.Allocate(4, 4);
        buffer.Release();

        Assert.Throws<InvalidOperationException>(buffer.Release);
    }

    [Fact]
    public void RetainingAFreedBufferThrows()
    {
        PixelBuffer buffer = PixelBuffer.Allocate(4, 4);
        buffer.Release();

        Assert.Throws<ObjectDisposedException>(() => buffer.Retain());
    }

    [Fact]
    public void UsingAFreedBufferThrows()
    {
        PixelBuffer buffer = PixelBuffer.Allocate(4, 4);
        buffer.Release();

        Assert.Throws<ObjectDisposedException>(() => buffer.Scan0);
    }

    [Fact]
    public void FromRowsSkipsSourcePadding()
    {
        // A source whose rows are padded past their pixels — what WIC and most decoders hand back.
        const int Width = 3, Height = 2, SourceStride = 20;
        var source = new byte[SourceStride * Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width * 4; x++)
                source[y * SourceStride + x] = (byte)(y * 16 + x);

        using var buffer = new Owned(PixelBuffer.FromRows(source, Width, Height, SourceStride));

        for (int y = 0; y < Height; y++)
        {
            Span<byte> row = buffer.Value.Row(y);
            for (int x = 0; x < Width * 4; x++)
                Assert.Equal((byte)(y * 16 + x), row[x]);
        }
    }

    [Fact]
    public void FromRowsRejectsAStrideNarrowerThanTheRow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PixelBuffer.FromRows(new byte[64], width: 8, height: 2, sourceStride: 8));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 4)]
    public void AllocateRejectsEmptySizes(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelBuffer.Allocate(width, height));
    }

    /// <summary>Releases a buffer at the end of a test, so one failure cannot skew the next.</summary>
    private sealed class Owned(PixelBuffer value) : IDisposable
    {
        public PixelBuffer Value { get; } = value;
        public void Dispose() => Value.Release();
    }
}
