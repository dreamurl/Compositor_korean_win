using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// The pyramid of sharp halvings, and the property the rest of the renderer leans on.
/// </summary>
public class DownsamplePyramidTests
{
    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(0.6, 0)]
    [InlineData(0.5, 0)]     // Half size still resamples in one step.
    [InlineData(0.49, 1)]
    [InlineData(0.25, 2)]
    [InlineData(0.1, 3)]
    [InlineData(0.001, DownsamplePyramid.MaximumLevel)]
    [InlineData(0, 0)]
    [InlineData(double.NaN, 0)]
    public void TheLevelFollowsHowFarTheImageShrinks(double factor, int expected) =>
        Assert.Equal(expected, DownsamplePyramid.LevelFor(factor));

    [Fact]
    public void HalvingAveragesEachBlockOfFour()
    {
        using PixelBuffer source = PixelBuffer.Allocate(2, 2);
        source.Row(0)[0] = 10; source.Row(0)[3] = 255;
        source.Row(0)[4] = 20; source.Row(0)[7] = 255;
        source.Row(1)[0] = 30; source.Row(1)[3] = 255;
        source.Row(1)[4] = 40; source.Row(1)[7] = 255;

        using PixelBuffer halved = DownsamplePyramid.Halve(source);

        Assert.Equal(1, halved.Width);
        Assert.Equal(1, halved.Height);
        Assert.Equal(25, (int)halved.Row(0)[0]);    // (10 + 20 + 30 + 40) / 4
        Assert.Equal(255, (int)halved.Row(0)[3]);
    }

    [Fact]
    public void AnOddSideRoundsUpAndAveragesWhatIsThere()
    {
        using PixelBuffer source = PixelBuffer.Allocate(3, 1);
        source.Row(0)[0] = 10; source.Row(0)[3] = 255;
        source.Row(0)[4] = 20; source.Row(0)[7] = 255;
        source.Row(0)[8] = 90; source.Row(0)[11] = 255;

        using PixelBuffer halved = DownsamplePyramid.Halve(source);

        Assert.Equal(2, halved.Width);
        Assert.Equal(15, (int)halved.Row(0)[0]);   // The pair.
        Assert.Equal(90, (int)halved.Row(0)[4]);   // The leftover, on its own.
    }

    [Fact]
    public void AveragingHappensOnPremultipliedValues()
    {
        // A transparent pixel's colour is arbitrary. Averaging straight colour would let it bleed
        // into its neighbour; averaging premultiplied cannot.
        using PixelBuffer source = PixelBuffer.Allocate(2, 1);
        source.Row(0)[0] = 200; source.Row(0)[1] = 0; source.Row(0)[2] = 0; source.Row(0)[3] = 200;
        source.Row(0)[4] = 0; source.Row(0)[5] = 0; source.Row(0)[6] = 0; source.Row(0)[7] = 0;

        using PixelBuffer halved = DownsamplePyramid.Halve(source);

        Assert.Equal(100, (int)halved.Row(0)[0]);
        Assert.Equal(100, (int)halved.Row(0)[3]);
        // Straight colour is unchanged: 100/100 is the same red as 200/200.
        Assert.Equal(halved.Row(0)[0], halved.Row(0)[3]);
    }

    [Fact]
    public void AReducedPieceMatchesThatPieceOfTheWholeReduced()
    {
        // The invariant everything tiled rests on. It holds because a box filter over an aligned
        // 2×2 block reaches nothing outside that block — which is exactly why this port halves by
        // averaging rather than with the Lanczos filter upstream uses.
        using PixelBuffer whole = RenderFixture.Gradient(64, 64);
        using PixelBuffer wholeReduced = DownsamplePyramid.Halve(DownsamplePyramid.Halve(whole));

        // A quarter of the image, taken on its own and reduced the same way.
        var region = new PixelRect(16, 32, 32, 16);
        using PixelBuffer piece = PixelRegion.Copy(whole, region);
        using PixelBuffer pieceReduced = DownsamplePyramid.Halve(DownsamplePyramid.Halve(piece));

        for (int y = 0; y < pieceReduced.Height; y++)
        {
            Span<byte> fromPiece = pieceReduced.Row(y);
            Span<byte> fromWhole = wholeReduced.Row(y + region.Y / 4);
            for (int x = 0; x < pieceReduced.Width * 4; x++)
                Assert.Equal(fromWhole[region.X / 4 * 4 + x], fromPiece[x]);
        }
    }

    [Fact]
    public void TheCacheHandsBackTheSameCopyForTheSameImage()
    {
        using var pyramid = new DownsamplePyramid();
        using PixelBuffer source = RenderFixture.Gradient(32, 32);

        (PixelBuffer first, int firstLevel) = pyramid.Reduced(source, 2);
        (PixelBuffer second, int secondLevel) = pyramid.Reduced(source, 2);

        Assert.Equal(2, firstLevel);
        Assert.Equal(2, secondLevel);
        Assert.Same(first, second);
        Assert.Equal(8, first.Width);
    }

    [Fact]
    public void AskingForMoreLevelsThanAnImageHasStopsWhereItRunsOut()
    {
        using var pyramid = new DownsamplePyramid();
        using PixelBuffer source = RenderFixture.Gradient(4, 4);

        (PixelBuffer image, int level) = pyramid.Reduced(source, 6);

        Assert.Equal(2, level);
        Assert.Equal(1, image.Width);
        Assert.Equal(1, image.Height);
    }
}

/// <summary>
/// Tile replacement: a layer held as an image plus patches has to draw like the finished image.
/// </summary>
/// <remarks>
/// This is what makes painting on a large layer affordable (docs/windows-port.md §2.3), and it is
/// only safe if it is invisible — a stroke must not shift or soften the pixels around it, and the
/// moment it is committed nothing may change. So the test is equality with the flattened layer,
/// pixel for pixel, at several sizes.
/// </remarks>
public class TileReplacementTests
{
    private static PixelBuffer Patch(int width, int height, byte value)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x * 4 + 0] = value;
                row[x * 4 + 1] = (byte)(255 - value);
                row[x * 4 + 2] = (byte)((x * 7 + y * 13) % 256);
                row[x * 4 + 3] = 255;
            }
        }
        return buffer;
    }

    [Fact]
    public void MaterializingAppliesPatchesInOrder()
    {
        using PixelBuffer baseImage = RenderFixture.Solid(8, 8, 10, 10, 10);
        using PixelBuffer first = RenderFixture.Solid(4, 4, 100, 100, 100);
        using PixelBuffer second = RenderFixture.Solid(2, 2, 200, 200, 200);

        var raster = new LayerRaster(baseImage,
        [
            new RasterPatch(new PixelRect(0, 0, 4, 4), first),
            new RasterPatch(new PixelRect(2, 2, 2, 2), second),
        ]);

        using PixelBuffer flat = raster.Flatten();

        Assert.Equal(200, (int)flat.Row(2)[2 * 4]);   // The later patch wins.
        Assert.Equal(100, (int)flat.Row(0)[0]);       // The earlier one where it is alone.
        Assert.Equal(10, (int)flat.Row(6)[6 * 4]);    // The base elsewhere.
    }

    [Fact]
    public void MaterializingPastTheEdgeLeavesTransparentPixels()
    {
        using PixelBuffer baseImage = RenderFixture.Solid(4, 4, 50, 50, 50);
        var raster = new LayerRaster(baseImage, []);

        using PixelBuffer region = raster.Materialize(new PixelRect(2, 2, 4, 4));

        Assert.Equal(50, (int)region.Row(0)[0]);
        Assert.Equal(0, (int)region.Row(3)[3 * 4 + 3]);
    }

    [Theory]
    [InlineData(64)]    // Drawn at its own size.
    [InlineData(32)]    // Half, so the last resample does it.
    [InlineData(16)]    // A quarter: one halving first.
    [InlineData(7)]     // Small enough for several halvings.
    public void ATiledLayerDrawsExactlyLikeTheFlattenedOne(int drawnSize)
    {
        using PixelBuffer baseImage = RenderFixture.Gradient(64, 64);
        using PixelBuffer patch = Patch(16, 16, 220);
        var raster = new LayerRaster(baseImage, [new RasterPatch(new PixelRect(16, 24, 16, 16), patch)]);

        using PixelBuffer flattened = raster.Flatten();
        var placement = new LayerTransform(new Point(0, 0), new Size(drawnSize, drawnSize));

        using PixelBuffer tiled = Draw(raster, placement, drawnSize);
        using PixelBuffer whole = Draw(new BufferSource(flattened), placement, drawnSize);

        for (int y = 0; y < drawnSize; y++)
            Assert.True(tiled.Row(y).SequenceEqual(whole.Row(y)),
                $"row {y} differs when drawn at {drawnSize} pixels");
    }

    [Fact]
    public void ATiledLayerMatchesWhenRotated()
    {
        using PixelBuffer baseImage = RenderFixture.Gradient(64, 64);
        using PixelBuffer patch = Patch(32, 8, 90);
        var raster = new LayerRaster(baseImage, [new RasterPatch(new PixelRect(8, 8, 32, 8), patch)]);

        using PixelBuffer flattened = raster.Flatten();
        var placement = new LayerTransform(new Point(8, 8), new Size(48, 48)) with { Rotation = 23 };

        using PixelBuffer tiled = Draw(raster, placement, 64);
        using PixelBuffer whole = Draw(new BufferSource(flattened), placement, 64);

        for (int y = 0; y < 64; y++)
            Assert.True(tiled.Row(y).SequenceEqual(whole.Row(y)), $"row {y} differs");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void OnlyThePartOnScreenIsBuilt(int reduction)
    {
        // Pushed mostly off the surface, so the renderer materialises a region rather than the
        // layer. That is the path tile replacement exists for, and it has to land on the same
        // pixels as the whole image — which needs the region snapped to the halving grid.
        using PixelBuffer baseImage = RenderFixture.Gradient(128, 128);
        using PixelBuffer patch = Patch(24, 24, 150);
        var raster = new LayerRaster(baseImage, [new RasterPatch(new PixelRect(40, 56, 24, 24), patch)]);

        using PixelBuffer flattened = raster.Flatten();
        int drawn = 128 / reduction;
        var placement = new LayerTransform(new Point(-drawn * 0.6, -drawn * 0.4), new Size(drawn, drawn));

        using PixelBuffer tiled = Draw(raster, placement, 64);
        using PixelBuffer whole = Draw(new BufferSource(flattened), placement, 64);

        for (int y = 0; y < 64; y++)
            Assert.True(tiled.Row(y).SequenceEqual(whole.Row(y)),
                $"row {y} differs at 1/{reduction} size");
    }

    private static PixelBuffer Draw(IPixelSource source, LayerTransform placement, int surfaceSize)
    {
        using var backend = new SoftwareRenderBackend();
        using IRenderSurface surface = backend.CreateSurface(Math.Max(surfaceSize, 64), Math.Max(surfaceSize, 64));
        surface.Clear();
        surface.Draw(new LayerDraw { Source = source, Placement = placement });
        return surface.Read();
    }
}
