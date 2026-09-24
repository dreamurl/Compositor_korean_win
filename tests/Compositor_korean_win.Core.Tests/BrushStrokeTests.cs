using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// The brush: what a dab looks like, what a stroke costs, and what stops it.
/// </summary>
public class BrushStrokeTests
{
    private static BrushSettings Hard(double diameter = 20, double opacity = 1) => new()
    {
        Diameter = diameter,
        Hardness = 1,
        Opacity = opacity,
        Color = Rgba.Black,
    };

    private static PixelBuffer Paint(BrushSettings settings, int width, int height,
                                     IReadOnlyList<Point> path, DocumentSelection? selection = null,
                                     PixelBuffer? over = null)
    {
        using var stroke = new BrushStroke(over, width, height, settings, selection);
        foreach (Point point in path) stroke.Append(point);
        return stroke.Commit();
    }

    [Fact]
    public void ADabIsRoundAndCentredWhereItWasPut()
    {
        using PixelBuffer painted = Paint(Hard(), 64, 64, [new Point(32, 32)]);

        Assert.Equal((0, 0, 0, 255), RenderFixture.At(painted, 32, 32));
        Assert.Equal((0, 0, 0, 255), RenderFixture.At(painted, 32 + 8, 32));

        // Outside the radius there is nothing at all, corners included.
        Assert.Equal(0, RenderFixture.At(painted, 32 + 12, 32).A);
        Assert.Equal(0, RenderFixture.At(painted, 0, 0).A);
    }

    [Fact]
    public void ASoftTipFadesOutTowardsTheRim()
    {
        var settings = Hard() with { Hardness = 0 };
        using PixelBuffer painted = Paint(settings, 64, 64, [new Point(32, 32)]);

        int centre = RenderFixture.At(painted, 32, 32).A;
        int middle = RenderFixture.At(painted, 32 + 6, 32).A;
        int rim = RenderFixture.At(painted, 32 + 9, 32).A;

        // Not quite 255 even at the middle: the falloff is normalised to reach zero at the rim,
        // so it starts a shade under one — which is what stops a soft dab having a hard core.
        Assert.InRange(centre, 245, 255);
        Assert.InRange(middle, 1, 200);
        Assert.True(rim < middle, $"{rim} should be fainter than {middle}");
    }

    [Fact]
    public void AStrokeIsContinuousBetweenTheEventsItWasGiven()
    {
        // Two points a long way apart: the dabs in between are the stroke's own doing.
        using PixelBuffer painted = Paint(Hard(diameter: 10), 256, 64,
                                          [new Point(10, 32), new Point(240, 32)]);

        for (int x = 12; x <= 238; x += 1)
        {
            Assert.Equal(255, RenderFixture.At(painted, x, 32).A);
        }
    }

    [Fact]
    public void OpacityCapsTheStrokeAndNotEachDab()
    {
        var settings = Hard(diameter: 20, opacity: 0.5);

        // Back and forth over the same ground: dozens of dabs on the same pixel.
        using PixelBuffer painted = Paint(settings, 64, 64,
        [
            new Point(20, 32), new Point(44, 32), new Point(20, 32), new Point(44, 32),
        ]);

        // Half, not "half a hundred times over", which would be indistinguishable from opaque.
        Assert.InRange(RenderFixture.At(painted, 32, 32).A, 126, 130);
    }

    [Fact]
    public void ErasingTakesThePixelsAway()
    {
        using PixelBuffer filled = RenderFixture.Solid(64, 64, 200, 40, 40);
        var settings = Hard() with { Mode = BrushMode.Erase };

        using PixelBuffer painted = Paint(settings, 64, 64, [new Point(32, 32)], over: filled);

        Assert.Equal(0, RenderFixture.At(painted, 32, 32).A);
        Assert.Equal(255, RenderFixture.At(painted, 2, 2).A);
    }

    [Fact]
    public void PaintingLandsOnTopOfWhatIsAlreadyThere()
    {
        using PixelBuffer filled = RenderFixture.Solid(64, 64, 255, 255, 255);
        using PixelBuffer painted = Paint(Hard(), 64, 64, [new Point(32, 32)], over: filled);

        Assert.Equal((0, 0, 0, 255), RenderFixture.At(painted, 32, 32));
        Assert.Equal((255, 255, 255, 255), RenderFixture.At(painted, 2, 2));
    }

    /// <remarks>
    /// The figure §2.3 is about: a stroke costs the tiles it touched. One dab in the middle of a
    /// four-megapixel layer has no business allocating four megapixels.
    /// </remarks>
    [Fact]
    public void OnlyTheTilesADabTouchedExistAtAll()
    {
        using var stroke = new BrushStroke(null, 2048, 2048, Hard(diameter: 8));
        stroke.Append(new Point(400, 400));

        Assert.Single(stroke.Patches);
        Assert.Equal(new PixelRect(256, 256, 256, 256), stroke.Patches[0].Region);
        Assert.True(stroke.Dirty.Width <= 12 && stroke.Dirty.Height <= 12);
    }

    [Fact]
    public void ADabOnATileBorderTouchesBothTiles()
    {
        using var stroke = new BrushStroke(null, 1024, 1024, Hard(diameter: 20));
        stroke.Append(new Point(256, 400));

        Assert.Equal(2, stroke.Patches.Count);
    }

    [Fact]
    public void TheTilesDrawAsTheFinishedLayerDoes()
    {
        using PixelBuffer filled = RenderFixture.Solid(300, 300, 20, 120, 220);
        using var stroke = new BrushStroke(filled, 300, 300, Hard(diameter: 40));

        stroke.Append(new Point(100, 150));
        stroke.Append(new Point(260, 150));

        using PixelBuffer committed = stroke.Commit();
        Assert.True(committed.IsDeferred);
        var raster = new LayerRaster(filled, stroke.Patches);
        using PixelBuffer live = raster.Flatten();

        for (int y = 0; y < 300; y += 7)
        {
            for (int x = 0; x < 300; x += 7)
            {
                Assert.Equal(RenderFixture.At(committed, x, y), RenderFixture.At(live, x, y));
            }
        }
    }

    [Fact]
    public void ASelectionKeepsThePaintInsideIt()
    {
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(0, 0, 32, 64));
        using PixelBuffer painted = Paint(Hard(diameter: 30), 64, 64, [new Point(32, 32)], selection);

        Assert.Equal(255, RenderFixture.At(painted, 24, 32).A);
        Assert.Equal(0, RenderFixture.At(painted, 40, 32).A);
    }

    [Fact]
    public void AnEmptySelectionTakesNoPaintAtAll()
    {
        using PixelBuffer painted = Paint(Hard(), 64, 64, [new Point(32, 32)], DocumentSelection.Empty);

        Assert.Equal(0, RenderFixture.At(painted, 32, 32).A);
    }

    [Fact]
    public void ALiveStrokeDrawsThroughTheCompositor()
    {
        using PixelBuffer filled = RenderFixture.Solid(64, 64, 255, 255, 255);
        ImageLayer layer = RenderFixture.Layer("paint", filled);
        CanvasDocument document = RenderFixture.Document(64, 64, layer);

        using var stroke = new BrushStroke(filled, 64, 64, Hard(diameter: 16));
        stroke.Append(new Point(32, 32));

        using var backend = new SoftwareRenderBackend();
        using IRenderSurface surface = backend.CreateSurface(64, 64);
        surface.Clear();
        LayerCompositor.Draw(document, surface, backend, CanvasProjection.Identity,
                             new LiveEdit(layer.Id, new LayerRaster(filled, stroke.Patches)));

        using PixelBuffer result = surface.Read();

        Assert.Equal((0, 0, 0, 255), RenderFixture.At(result, 32, 32));
        Assert.Equal((255, 255, 255, 255), RenderFixture.At(result, 2, 2));
    }

    [Fact]
    public void ABlankLayerCanBePaintedOnBeforeItHasAnyPixels()
    {
        ImageLayer blank = new()
        {
            Id = Guid.NewGuid(),
            Name = "blank",
            Transform = new LayerTransform(Point.Zero, new Size(64, 64)),
        };

        CanvasDocument document = RenderFixture.Document(64, 64, blank);

        using var stroke = new BrushStroke(null, 64, 64, Hard(diameter: 16));
        stroke.Append(new Point(32, 32));

        using PixelBuffer empty = PixelBuffer.Allocate(64, 64);
        using var backend = new SoftwareRenderBackend();
        using IRenderSurface surface = backend.CreateSurface(64, 64);
        surface.Clear();
        LayerCompositor.Draw(document, surface, backend, CanvasProjection.Identity,
                             new LiveEdit(blank.Id, new LayerRaster(empty, stroke.Patches)));

        using PixelBuffer result = surface.Read();
        Assert.Equal((0, 0, 0, 255), RenderFixture.At(result, 32, 32));
    }
}
