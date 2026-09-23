using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>Layer › Transform Selection's last step: the moved pixels laid back into their layer.</summary>
public class FloatingMergeTests
{
    [Fact]
    public void PixelsMovedPastTheLayerGrowIt()
    {
        using PixelBuffer red = RenderFixture.Solid(20, 20, 255, 0, 0);
        using PixelBuffer blue = RenderFixture.Solid(5, 5, 0, 0, 255);
        ImageLayer source = RenderFixture.Layer("source", red);
        ImageLayer floating = RenderFixture.Layer("floating", blue, 18, 18);

        ImageLayer merged = FloatingMerge.Merge(source, floating)!;

        Assert.Equal((23, 23), (merged.Image!.Width, merged.Image.Height));
        Assert.Equal(new LayerTransform(Point.Zero, new Size(23, 23)), merged.Transform);
        Assert.Equal((255, 0, 0, 255), RenderFixture.At(merged.Image, 5, 5));
        Assert.Equal((0, 0, 255, 255), RenderFixture.At(merged.Image, 20, 20));
        Assert.Equal(0, RenderFixture.At(merged.Image, 22, 2).A);
        merged.Image.Release();
    }

    [Fact]
    public void AMoveToWholePixelsIsCopiedExactly()
    {
        using PixelBuffer clear = PixelBuffer.Allocate(20, 20);
        using PixelBuffer ramp = RenderFixture.Gradient(6, 6);
        ImageLayer source = RenderFixture.Layer("source", clear);
        ImageLayer floating = RenderFixture.Layer("floating", ramp, 7, 3);

        ImageLayer merged = FloatingMerge.Merge(source, floating)!;

        for (int y = 0; y < 6; y++)
            for (int x = 0; x < 6; x++)
                Assert.Equal(RenderFixture.At(ramp, x, y), RenderFixture.At(merged.Image!, x + 7, y + 3));
        merged.Image!.Release();
    }

    [Fact]
    public void AMaskOnTheLayersGridGrowsWhiteWhereTheLayerGrew()
    {
        using PixelBuffer red = RenderFixture.Solid(10, 10, 255, 0, 0);
        using PixelBuffer black = RenderFixture.Coverage(10, 10, (_, _) => 0);
        using PixelBuffer blue = RenderFixture.Solid(4, 4, 0, 0, 255);
        ImageLayer source = RenderFixture.Layer("source", red) with { Mask = new LayerMask { Coverage = black } };

        ImageLayer merged = FloatingMerge.Merge(source, RenderFixture.Layer("floating", blue, 12, 0))!;

        Assert.Equal((16, 10), (merged.Mask!.Coverage.Width, merged.Mask.Coverage.Height));
        Assert.Equal(0, RenderFixture.At(merged.Mask.Coverage, 5, 5).R);
        Assert.Equal(255, RenderFixture.At(merged.Mask.Coverage, 14, 2).R);
        merged.Image!.Release();
        merged.Mask.Coverage.Release();
    }
}
