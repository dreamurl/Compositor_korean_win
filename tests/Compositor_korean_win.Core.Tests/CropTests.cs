using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>The Crop tool's frame and what applying it does to the document.</summary>
public class CropTests
{
    [Fact]
    public void AFrameLandsOnWholePixelsTheRightWayUp()
    {
        Rect frame = CropGeometry.Create(new Point(40.6, 30.2), new Point(10.4, 5.7), null, symmetric: false);
        Assert.Equal(new Rect(10, 6, 31, 24), frame);
    }

    [Fact]
    public void AFixedProportionHoldsWhileDrawing()
    {
        Rect frame = CropGeometry.Create(new Point(0, 0), new Point(160, 20), 16.0 / 9, symmetric: false);
        Assert.Equal(160, frame.Width);
        Assert.Equal(90, frame.Height);
    }

    [Fact]
    public void AltGrowsTheFrameFromWhereTheDragBegan()
    {
        Rect frame = CropGeometry.Create(new Point(50, 50), new Point(60, 70), null, symmetric: true);
        Assert.Equal(new Rect(40, 30, 20, 40), frame);
    }

    [Fact]
    public void AHandleMovesOnlyItsEdge()
    {
        var original = new Rect(10, 10, 100, 50);
        // Handle 4 is the right edge's middle in TransformDrag's order.
        int right = TransformDrag.Handles.ToList().FindIndex(unit => unit.X == 1 && unit.Y == 0.5);
        Rect next = CropGeometry.Dragged(original, CropDragKind.Resize, right, new Point(110, 35), new Point(130, 80), null, false);
        Assert.Equal(new Rect(10, 10, 120, 50), next);
    }

    [Fact]
    public void MovingKeepsTheSize()
    {
        Rect next = CropGeometry.Dragged(new Rect(10, 10, 100, 50), CropDragKind.Move, 0, new Point(20, 20), new Point(25.4, 17.6), null, false);
        Assert.Equal(new Rect(15, 8, 100, 50), next);
    }

    [Fact]
    public void AnEdgeSnapsToACanvasEdgeNearby()
    {
        var targets = new SnapGuides([0, 200], [0, 100]);
        Rect snapped = CropGeometry.Snap(new Rect(3, 10, 190, 80), CropDragKind.Create, 0, new Point(3, 10), null, targets, 5);
        Assert.Equal(0, snapped.MinX);
        Assert.Equal(193, snapped.MaxX);
    }

    [Fact]
    public void AMoveSnapsItsClosestEdgeAndKeepsItsSize()
    {
        var targets = new SnapGuides([0, 200], [0, 100]);
        Rect snapped = CropGeometry.Snap(new Rect(103, 20, 95, 50), CropDragKind.Move, 0, new Point(150, 40), null, targets, 5);
        Assert.Equal(new Rect(105, 20, 95, 50), snapped);
    }

    [Fact]
    public void CroppingMovesEveryLayerAndCutsNothing()
    {
        using PixelBuffer image = RenderFixture.Solid(100, 80, 1, 2, 3);
        ImageLayer layer = RenderFixture.Layer("a", image, 10, 5);
        CanvasDocument document = RenderFixture.Document(200, 100, layer);

        CanvasDocument cropped = DocumentCommands.Crop(document, new PixelRect(20, 10, 50, 40))!;

        Assert.Equal((50, 40), (cropped.Width, cropped.Height));
        ImageLayer moved = cropped.Layers[0];
        Assert.Equal(new Point(-10, -5), moved.Transform.Origin);
        Assert.Same(image, moved.Image);
    }

    [Fact]
    public void CroppingOutwardsGrowsTheCanvas()
    {
        using PixelBuffer image = RenderFixture.Solid(100, 80, 1, 2, 3);
        CanvasDocument document = RenderFixture.Document(100, 80, RenderFixture.Layer("a", image));

        CanvasDocument grown = DocumentCommands.Crop(document, new PixelRect(-20, -10, 140, 100))!;

        Assert.Equal((140, 100), (grown.Width, grown.Height));
        Assert.Equal(new Point(20, 10), grown.Layers[0].Transform.Origin);
    }

    [Fact]
    public void CroppingToTheCanvasItselfIsNothing()
    {
        using PixelBuffer image = RenderFixture.Solid(100, 80, 1, 2, 3);
        CanvasDocument document = RenderFixture.Document(100, 80, RenderFixture.Layer("a", image));
        Assert.Null(DocumentCommands.Crop(document, new PixelRect(0, 0, 100, 80)));
    }

    [Fact]
    public void ChoosingAProportionFitsTheFrameAboutItsMiddle()
    {
        Rect fitted = CropGeometry.WithProportion(new Rect(0, 0, 200, 200), 2);
        Assert.Equal(new Rect(0, 50, 200, 100), fitted);
    }
}
