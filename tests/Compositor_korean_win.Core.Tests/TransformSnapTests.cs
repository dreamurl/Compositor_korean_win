using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// Snapping: the pull that puts a layer's edge or centre on the canvas, or on another layer.
/// </summary>
public class TransformSnapTests
{
    private static readonly SnapGuides Canvas = new([0, 500, 1000], [0, 400, 800]);

    private static LayerTransform At(double x, double y, double width, double height) =>
        new(new Point(x, y), new Size(width, height));

    private static CanvasDocument Document(params ImageLayer[] layers) => new()
    {
        Id = Guid.NewGuid(),
        Width = 1000,
        Height = 800,
        Layers = new EquatableList<ImageLayer>(layers),
    };

    private static ImageLayer Layer(LayerTransform transform, PixelBuffer image,
                                    bool visible = true, Guid? parent = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Layer",
        Transform = transform,
        Image = image,
        IsVisible = visible,
        ParentId = parent,
    };

    [Fact]
    public void AnEdgeNearTheCanvasEdgeLandsOnIt()
    {
        (LayerTransform snapped, SnapResult snap) = TransformSnap.Move(At(3, 200, 100, 50), Canvas, 10);

        Assert.Equal(0, snapped.Origin.X, precision: 9);
        Assert.Equal(200, snapped.Origin.Y, precision: 9);
        Assert.Equal(0d, snap.X);
        Assert.Null(snap.Y);
    }

    [Fact]
    public void ACentreNearTheCanvasCentreLandsOnIt()
    {
        // Centred at 494 across: six pixels from the canvas centre, and no edge is anywhere near.
        (LayerTransform snapped, SnapResult snap) = TransformSnap.Move(At(444, 200, 100, 50), Canvas, 10);

        Assert.Equal(450, snapped.Origin.X, precision: 9);
        Assert.Equal(500d, snap.X);
    }

    [Fact]
    public void NothingBeyondTheToleranceMoves()
    {
        LayerTransform draft = At(40, 200, 100, 50);
        (LayerTransform snapped, SnapResult snap) = TransformSnap.Move(draft, Canvas, 10);

        Assert.Equal(draft, snapped);
        Assert.False(snap.Moved);
        Assert.Null(snap.X);
        Assert.Null(snap.Y);
    }

    [Fact]
    public void EachAxisSnapsOnItsOwn()
    {
        // Left edge four pixels from the canvas edge; nothing near vertically.
        (LayerTransform snapped, SnapResult snap) = TransformSnap.Move(At(4, 210, 100, 50), Canvas, 10);

        Assert.Equal(0, snapped.Origin.X, precision: 9);
        Assert.Equal(210, snapped.Origin.Y, precision: 9);
        Assert.NotNull(snap.X);
        Assert.Null(snap.Y);
    }

    [Fact]
    public void ARotatedLayerSnapsByTheBoxAroundIt()
    {
        // Turned a quarter, a 100×50 layer occupies 50 across and 100 down. Placed so that box
        // sits five pixels inside the canvas corner, which is where its own origin is not.
        LayerTransform draft = At(-20, 30, 100, 50) with { Rotation = 90 };
        Rect box = Rect.Around(TransformDrag.CornersOf(draft));
        Assert.Equal(50, box.Width, precision: 6);
        Assert.Equal(5, box.MinX, precision: 6);

        (LayerTransform snapped, _) = TransformSnap.Move(draft, Canvas, 10);
        Rect moved = Rect.Around(TransformDrag.CornersOf(snapped));

        Assert.Equal(0, moved.MinX, precision: 6);
        Assert.Equal(0, moved.MinY, precision: 6);
    }

    [Fact]
    public void AnotherLayersEdgesAndCentreAreTargets()
    {
        using PixelBuffer pixels = ProjectFixture.Image();
        ImageLayer other = Layer(At(300, 100, 200, 100), pixels);
        SnapGuides targets = TransformSnap.TargetsFor(Document(other), new HashSet<Guid>());

        Assert.Contains(300d, targets.Xs);
        Assert.Contains(400d, targets.Xs);   // Its centre.
        Assert.Contains(500d, targets.Xs);
        Assert.Contains(150d, targets.Ys);   // Its centre.
    }

    [Fact]
    public void ALayerIsNotItsOwnTarget()
    {
        using PixelBuffer pixels = ProjectFixture.Image();
        ImageLayer moving = Layer(At(300, 100, 200, 100), pixels);
        ImageLayer other = Layer(At(700, 600, 100, 100), pixels);

        SnapGuides targets = TransformSnap.TargetsFor(Document(moving, other), new HashSet<Guid> { moving.Id });

        Assert.DoesNotContain(300d, targets.Xs);
        Assert.Contains(700d, targets.Xs);
    }

    [Fact]
    public void WhatIsNotDrawnIsNotATarget()
    {
        using PixelBuffer pixels = ProjectFixture.Image();
        ImageLayer hidden = Layer(At(300, 100, 200, 100), pixels, visible: false);
        ImageLayer folder = new()
        {
            Id = Guid.NewGuid(),
            Name = "Folder",
            Transform = At(0, 0, 1000, 800),
            IsGroup = true,
            IsVisible = false,
        };
        ImageLayer inside = Layer(At(600, 100, 200, 100), pixels, parent: folder.Id);
        ImageLayer blank = new()
        {
            Id = Guid.NewGuid(),
            Name = "Blank",
            Transform = At(900, 700, 50, 50),
        };

        SnapGuides targets = TransformSnap.TargetsFor(Document(hidden, folder, inside, blank),
                                                      new HashSet<Guid>());

        Assert.DoesNotContain(300d, targets.Xs);   // Hidden.
        Assert.DoesNotContain(600d, targets.Xs);   // Inside a hidden folder.
        Assert.DoesNotContain(900d, targets.Xs);   // No pixels to line up with.

        // The canvas is always there, whatever the layers are doing.
        Assert.Contains(0d, targets.Xs);
        Assert.Contains(1000d, targets.Xs);
    }

    [Fact]
    public void TheNearestTargetWins()
    {
        var targets = new SnapGuides([0, 6], [0]);
        SnapResult snap = TransformSnap.Offset(new Rect(4, 400, 100, 50), targets, 10);

        Assert.Equal(6d, snap.X);
        Assert.Equal(2, snap.Offset.X, precision: 9);
    }

    [Fact]
    public void ThePullIsTheSameDistanceOnScreenAtAnyZoom()
    {
        var document = new Size(4000, 3000);
        var view = new CanvasViewport { ViewSize = new Size(1280, 800) };

        double atFullSize = TransformSnap.ToleranceFor(view.Fit(document) with { Zoom = 1 });
        double zoomedOut = TransformSnap.ToleranceFor(view with { Zoom = 0.25 });
        double zoomedIn = TransformSnap.ToleranceFor(view with { Zoom = 4 });

        Assert.Equal(TransformSnap.Distance, atFullSize, precision: 9);
        Assert.Equal(TransformSnap.Distance * 4, zoomedOut, precision: 9);
        Assert.Equal(TransformSnap.Distance / 4, zoomedIn, precision: 9);
    }

    [Fact]
    public void ASnapThatWouldLeaveTheDocumentBehindIsRefused()
    {
        LayerTransform draft = At(-999_995, 0, 100, 50);
        var targets = new SnapGuides([-1_000_020], []);

        (LayerTransform snapped, _) = TransformSnap.Move(draft, targets, 30);

        Assert.Equal(draft, snapped);
    }
}
