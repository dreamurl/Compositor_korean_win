using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// Dragging the transform overlay: six numbers in, six numbers out, and never a pixel touched.
/// </summary>
public class TransformDragTests
{
    private static LayerTransform At(double x, double y, double width, double height) =>
        new(new Point(x, y), new Size(width, height));

    private static TransformDrag Drag(LayerTransform original, TransformDragMode mode, Point? start = null) => new()
    {
        Original = original,
        Start = start ?? original.Center,
        Mode = mode,
    };

    [Fact]
    public void MovingFollowsThePointer()
    {
        TransformDrag drag = Drag(At(10, 20, 100, 50), TransformDragMode.Move, new Point(60, 45));
        LayerTransform moved = drag.Updated(new Point(85, 15), lockRatio: false, shift: false);

        Assert.Equal(new Point(35, -10), moved.Origin);
        Assert.Equal(new Size(100, 50), moved.Size);
    }

    [Fact]
    public void MovingWhereThePointerWasGrabbedDoesNotJump()
    {
        // Grabbed well away from the centre: the layer must not leap so its centre meets the pointer.
        TransformDrag drag = Drag(At(0, 0, 100, 50), TransformDragMode.Move, new Point(5, 5));
        LayerTransform moved = drag.Updated(new Point(5, 5), lockRatio: false, shift: false);

        Assert.Equal(At(0, 0, 100, 50), moved);
    }

    [Theory]
    [InlineData(40, 5, 40, 0)]    // Mostly sideways: the vertical part is dropped.
    [InlineData(5, 40, 0, 40)]
    public void ShiftKeepsAMoveOnOneAxis(double dx, double dy, double expectedX, double expectedY)
    {
        var start = new Point(60, 45);
        TransformDrag drag = Drag(At(10, 20, 100, 50), TransformDragMode.Move, start);

        LayerTransform moved = drag.Updated(new Point(start.X + dx, start.Y + dy),
                                            lockRatio: false, shift: true);

        Assert.Equal(new Point(10 + expectedX, 20 + expectedY), moved.Origin);
    }

    [Fact]
    public void RotatingTurnsByTheAngleTheDragSweeps()
    {
        LayerTransform original = At(0, 0, 100, 100);
        TransformDrag drag = Drag(original, TransformDragMode.Rotate, new Point(100, 50));

        // From due right of the centre to directly below it: a quarter turn clockwise.
        LayerTransform turned = drag.Updated(new Point(50, 100), lockRatio: false, shift: false);

        Assert.Equal(90, turned.Rotation, precision: 9);
        Assert.Equal(original.Center, turned.Center);
    }

    [Fact]
    public void ShiftRotatesInFifteens()
    {
        LayerTransform original = At(0, 0, 100, 100);
        TransformDrag drag = Drag(original, TransformDragMode.Rotate, new Point(100, 50));

        // About 20° round: snapped down to 15.
        LayerTransform turned = drag.Updated(new Point(50 + 47, 50 + 17), lockRatio: false, shift: true);

        Assert.Equal(15, turned.Rotation, precision: 9);
    }

    [Fact]
    public void ResizingHoldsTheOppositeCornerStill()
    {
        LayerTransform original = At(10, 20, 100, 50);
        Point handle = original.PointAt(new Point(1, 1));
        TransformDrag drag = Drag(original, TransformDragMode.Resize(4), handle);

        LayerTransform sized = drag.Updated(new Point(handle.X + 40, handle.Y + 10),
                                            lockRatio: false, shift: false);

        Assert.Equal(new Size(140, 60), sized.Size);
        Assert.Equal(original.PointAt(new Point(0, 0)), sized.PointAt(new Point(0, 0)));
    }

    [Fact]
    public void AnEdgeHandleLeavesTheOtherAxisAlone()
    {
        LayerTransform original = At(0, 0, 100, 50);
        Point handle = original.PointAt(new Point(1, 0.5));
        TransformDrag drag = Drag(original, TransformDragMode.Resize(3), handle);

        LayerTransform sized = drag.Updated(new Point(handle.X + 20, handle.Y + 33),
                                            lockRatio: false, shift: false);

        Assert.Equal(120, sized.Size.Width, precision: 9);
        Assert.Equal(50, sized.Size.Height, precision: 9);
    }

    [Fact]
    public void HoldingTheOptionKeyResizesAboutTheCentre()
    {
        LayerTransform original = At(0, 0, 100, 50);
        Point handle = original.PointAt(new Point(1, 1));
        TransformDrag drag = Drag(original, TransformDragMode.Resize(4), handle);

        LayerTransform sized = drag.Updated(new Point(handle.X + 10, handle.Y + 5),
                                            lockRatio: false, shift: false, option: true);

        // Both sides grow by twice the drag, and the centre stays where it was.
        Assert.Equal(new Size(120, 60), sized.Size);
        Assert.Equal(original.Center, sized.Center);
    }

    [Fact]
    public void LockingTheRatioKeepsTheShape()
    {
        LayerTransform original = At(0, 0, 100, 50);
        Point handle = original.PointAt(new Point(1, 1));
        TransformDrag drag = Drag(original, TransformDragMode.Resize(4), handle);

        LayerTransform sized = drag.Updated(new Point(handle.X + 50, handle.Y + 2),
                                            lockRatio: true, shift: false);

        Assert.Equal(original.Size.Width / original.Size.Height,
                     sized.Size.Width / sized.Size.Height, precision: 9);
        Assert.True(sized.Size.Width > original.Size.Width);
    }

    [Fact]
    public void ShiftInvertsTheRatioLock()
    {
        LayerTransform original = At(0, 0, 100, 50);
        Point handle = original.PointAt(new Point(1, 1));
        TransformDrag drag = Drag(original, TransformDragMode.Resize(4), handle);
        var point = new Point(handle.X + 50, handle.Y + 2);

        LayerTransform free = drag.Updated(point, lockRatio: true, shift: true);
        LayerTransform locked = drag.Updated(point, lockRatio: false, shift: true);

        Assert.Equal(52, free.Size.Height, precision: 9);
        Assert.Equal(original.Size.Width / original.Size.Height,
                     locked.Size.Width / locked.Size.Height, precision: 9);
    }

    [Fact]
    public void ARotatedLayerResizesAlongItsOwnEdges()
    {
        LayerTransform original = At(0, 0, 100, 50) with { Rotation = 90 };
        Point handle = original.PointAt(new Point(1, 1));
        TransformDrag drag = Drag(original, TransformDragMode.Resize(4), handle);

        // A quarter turn clockwise puts the layer's own width along the document's y axis.
        LayerTransform sized = drag.Updated(new Point(handle.X, handle.Y + 10),
                                            lockRatio: false, shift: false);

        Assert.Equal(110, sized.Size.Width, precision: 9);
        Assert.Equal(50, sized.Size.Height, precision: 9);
        Assert.Equal(original.PointAt(new Point(0, 0)).X, sized.PointAt(new Point(0, 0)).X, precision: 9);
        Assert.Equal(original.PointAt(new Point(0, 0)).Y, sized.PointAt(new Point(0, 0)).Y, precision: 9);
    }

    [Fact]
    public void ALayerNeverResizesBelowOnePixel()
    {
        LayerTransform original = At(0, 0, 100, 50);
        Point handle = original.PointAt(new Point(1, 1));
        TransformDrag drag = Drag(original, TransformDragMode.Resize(4), handle);

        LayerTransform sized = drag.Updated(new Point(handle.X - 400, handle.Y - 400),
                                            lockRatio: false, shift: false);

        Assert.True(sized.Size.Width >= 1 && sized.Size.Height >= 1);
        Assert.True(sized.IsValid);
    }

    [Fact]
    public void ADragOffTheEndOfTheWorldKeepsThePlacementItHad()
    {
        LayerTransform original = At(0, 0, 100, 50);
        TransformDrag drag = Drag(original, TransformDragMode.Move, new Point(50, 25));

        LayerTransform moved = drag.Updated(new Point(9_000_000, 0), lockRatio: false, shift: false);

        Assert.Equal(original, moved);
    }

    [Fact]
    public void DistortingACornerMovesThatCornerAlone()
    {
        LayerTransform original = At(0, 0, 100, 50);
        TransformDrag drag = Drag(original, TransformDragMode.Distort(2), new Point(100, 0)) with
        {
            OriginalCorners = TransformDrag.CornersOf(original),
        };

        IReadOnlyList<Point>? corners = drag.Corners(new Point(130, -10));

        Assert.NotNull(corners);
        Assert.Equal(new Point(130, -10), corners[1]);
        Assert.Equal(new Point(0, 0), corners[0]);
        Assert.Equal(new Point(100, 50), corners[2]);
    }

    [Fact]
    public void DistortingAnEdgeMovesBothOfItsCorners()
    {
        LayerTransform original = At(0, 0, 100, 50);
        TransformDrag drag = Drag(original, TransformDragMode.Distort(3), new Point(100, 25)) with
        {
            OriginalCorners = TransformDrag.CornersOf(original),
        };

        IReadOnlyList<Point>? corners = drag.Corners(new Point(120, 25));

        Assert.NotNull(corners);
        Assert.Equal(new Point(120, 0), corners[1]);
        Assert.Equal(new Point(120, 50), corners[2]);
        Assert.Equal(new Point(0, 0), corners[0]);
        Assert.Equal(new Point(0, 50), corners[3]);
    }

    [Fact]
    public void AnOrdinaryDragDistortsNothing()
    {
        TransformDrag drag = Drag(At(0, 0, 100, 50), TransformDragMode.Resize(4));

        Assert.Null(drag.Corners(new Point(10, 10)));
    }

    [Fact]
    public void TheHandlesRunClockwiseFromTheTopLeft()
    {
        LayerTransform original = At(0, 0, 100, 50);
        IReadOnlyList<Point> handles = [.. TransformDrag.Handles.Select(original.PointAt)];

        Assert.Equal(new Point(0, 0), handles[0]);
        Assert.Equal(new Point(100, 0), handles[2]);
        Assert.Equal(new Point(100, 50), handles[4]);
        Assert.Equal(new Point(0, 50), handles[6]);
        Assert.Equal(new Point(50, 0), handles[1]);
    }
}
