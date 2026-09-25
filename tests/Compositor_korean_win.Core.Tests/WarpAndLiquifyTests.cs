using Compositor_korean_win.Core;
using Xunit;
using static Compositor_korean_win.Core.Tests.RenderFixture;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// Edit › Transform's Skew, Perspective and Warp, and Filter › Liquify — the Photoshop tools added
/// after the port, none of which upstream has.
/// </summary>
public class WarpAndLiquifyTests
{
    private static LayerTransform Box(double x, double y, double width, double height) =>
        new(new Point(x, y), new Size(width, height));

    private static TransformDrag Drag(LayerTransform box, TransformDragMode mode, Point start) => new()
    {
        Original = box,
        Start = start,
        Mode = mode,
        OriginalCorners = TransformDrag.CornersOf(box),
    };

    // MARK: Skew and Perspective

    [Fact]
    public void SkewingAnEdgeSlidesItAlongItself()
    {
        // The top edge's handle, pulled right and down: only the rightward part counts.
        LayerTransform box = Box(0, 0, 100, 50);
        TransformDrag drag = Drag(box, TransformDragMode.Skew(1), new Point(50, 0));
        IReadOnlyList<Point> corners = drag.Corners(new Point(80, 20))!;

        Assert.Equal(new Point(30, 0), corners[0]);
        Assert.Equal(new Point(130, 0), corners[1]);
        Assert.Equal(new Point(100, 50), corners[2]);
        Assert.Equal(new Point(0, 50), corners[3]);
    }

    [Fact]
    public void PerspectiveWidensAnEdgeAboutItsMiddle()
    {
        // The top-left corner pulled left: the top-right goes right by as much.
        LayerTransform box = Box(0, 0, 100, 50);
        TransformDrag drag = Drag(box, TransformDragMode.Perspective(0), new Point(0, 0));
        IReadOnlyList<Point> corners = drag.Corners(new Point(-20, 3))!;

        Assert.Equal(new Point(-20, 0), corners[0]);
        Assert.Equal(new Point(120, 0), corners[1]);
        Assert.Equal(new Point(100, 50), corners[2]);
        Assert.Equal(new Point(0, 50), corners[3]);
        Assert.True(QuadWarp.IsUsable(corners));
    }

    [Fact]
    public void SkewAndPerspectiveResampleTheLayerWhenTheyEnd()
    {
        Assert.True(TransformDragMode.Skew(0).MovesCorners);
        Assert.True(TransformDragMode.Perspective(0).MovesCorners);
        Assert.False(TransformDragMode.Resize(0).MovesCorners);
    }

    // MARK: Warp

    [Fact]
    public void AFlatGridIsTheLayerItself()
    {
        LayerTransform box = Box(10, 20, 90, 60);
        WarpMesh mesh = WarpMesh.Flat(box);

        Assert.Equal(new Point(10, 20), mesh.At(0, 0));
        Assert.Equal(new Point(100, 80), mesh.At(1, 1));
        Point middle = mesh.At(0.5, 0.5);
        Assert.Equal(55, middle.X, 6);
        Assert.Equal(50, middle.Y, 6);
    }

    [Fact]
    public void WarpingOverAFlatGridKeepsThePicture()
    {
        using PixelBuffer pixels = Gradient(40, 30);
        ImageLayer layer = Layer("gradient", pixels, 5, 7);
        ImageLayer warped = WarpMesh.Warp(layer, WarpMesh.Flat(layer.Transform))!;

        try
        {
            Assert.Equal(new Point(5, 7), warped.Transform.Origin);
            Assert.Equal(new Size(40, 30), warped.Transform.Size);
            for (int y = 2; y < 28; y += 5)
                for (int x = 2; x < 38; x += 5)
                {
                    (int R, int G, int B, int A) before = At(pixels, x, y), after = At(warped.Image!, x, y);
                    Assert.InRange(Math.Abs(before.R - after.R) + Math.Abs(before.G - after.G), 0, 4);
                }
        }
        finally
        {
            warped.Image!.Release();
        }
    }

    [Fact]
    public void DraggingThePictureMovesTheSpotUnderThePointer()
    {
        WarpMesh mesh = WarpMesh.Flat(Box(0, 0, 90, 90));
        (double u, double v) = mesh.Find(new Point(30, 60))!.Value;
        WarpMesh dragged = mesh.Dragged(u, v, 12, -8);

        Point moved = dragged.At(u, v);
        Assert.Equal(42, moved.X, 6);
        Assert.Equal(52, moved.Y, 6);

        // The far corner hardly moves.
        Assert.True(Math.Abs(dragged[0, 3].X - mesh[0, 3].X) < 1);
    }

    [Fact]
    public void MovingACornerTakesItsHandles()
    {
        WarpMesh mesh = WarpMesh.Flat(Box(0, 0, 90, 90));
        WarpMesh moved = mesh.Moved(0, -10, 5);

        Assert.Equal(new Point(-10, 5), moved[0, 0]);
        Assert.Equal(new Point(20, 5), moved[0, 1]);
        Assert.Equal(new Point(-10, 35), moved[1, 0]);
        Assert.Equal(mesh[1, 1], moved[1, 1]);
    }

    [Fact]
    public void AnArcPresetBendsTheGrid()
    {
        LayerTransform box = Box(0, 0, 120, 60);
        WarpMesh arc = WarpMesh.Preset(box, new TextWarp { Style = TextWarpStyle.Arc, Bend = 50 });
        WarpMesh flat = WarpMesh.Flat(box);

        Assert.NotEqual(flat.Points, arc.Points);
        Assert.Equal(flat.Points, WarpMesh.Preset(box, new TextWarp()).Points);
    }

    [Fact]
    public void WarpPreviewCapsAFrameInsteadOfBuildingAFullImagePyramid()
    {
        using PixelBuffer pixels = Gradient(64, 48);
        ImageLayer layer = Layer("large on screen", pixels, 0, 0) with
        {
            Transform = Box(0, 0, 4000, 3000),
        };
        using var preview = new WarpPreview(layer);

        LiveEdit frame = preview.Frame(WarpMesh.Flat(layer.Transform), CanvasProjection.Identity, 4000, 3000)!;

        Assert.NotNull(frame);
        Assert.InRange(preview.LastPixelsWarped, 1, 300_000);
    }

    [Fact]
    public void AnUnchangedWarpFrameIsReused()
    {
        using PixelBuffer pixels = Gradient(64, 48);
        ImageLayer layer = Layer("preview", pixels);
        WarpMesh mesh = WarpMesh.Flat(layer.Transform);
        using var preview = new WarpPreview(layer);

        LiveEdit first = preview.Frame(mesh, CanvasProjection.Identity, 80, 60)!;
        LiveEdit second = preview.Frame(mesh, CanvasProjection.Identity, 80, 60)!;

        Assert.Same(first, second);
        Assert.True(((BufferSource)first.Source).Cacheable);
    }

    // MARK: Liquify

    [Fact]
    public void AnUntouchedFieldReadsEveryPointFromItself()
    {
        var field = new LiquifyField(64, 48);
        Assert.True(field.IsIdentity);
        Assert.Equal((10.0, 20.0), field.Source(10, 20));
    }

    [Fact]
    public void ForwardWarpCarriesThePictureWithTheBrush()
    {
        var field = new LiquifyField(64, 64);
        field.Dab(LiquifyTool.Forward, new Point(32, 32), 20, 1, new Point(6, 0));

        // The centre now shows what was to its left: the picture moved right.
        (double x, _) = field.Source(32, 32);
        Assert.True(x < 30, $"the centre reads from {x}");
        Assert.False(field.IsIdentity);
    }

    [Fact]
    public void LiquifyUndoRestoresOnlyTheLatestStroke()
    {
        var field = new LiquifyField(64, 64);
        field.BeginStroke();
        field.Dab(LiquifyTool.Forward, new Point(20, 20), 12, 1, new Point(4, 0));
        field.EndStroke();
        double afterFirst = field.Source(20, 20).X;

        field.BeginStroke();
        field.Dab(LiquifyTool.Forward, new Point(44, 44), 12, 1, new Point(0, 5));
        field.EndStroke();

        Assert.True(field.CanUndo);
        Assert.True(field.Undo());
        Assert.Equal(afterFirst, field.Source(20, 20).X, 6);
        Assert.Equal(44, field.Source(44, 44).Y, 6);
        Assert.True(field.Undo());
        Assert.True(field.IsIdentity);
        Assert.False(field.CanUndo);
    }

    [Fact]
    public void FrozenPartsDoNotMoveAndReconstructBringsTheRestBack()
    {
        var field = new LiquifyField(64, 64);
        field.Dab(LiquifyTool.Freeze, new Point(10, 10), 8, 1, default);
        field.Dab(LiquifyTool.Forward, new Point(10, 10), 8, 1, new Point(5, 5));
        Assert.Equal((10.0, 10.0), field.Source(10, 10));

        field.Dab(LiquifyTool.Forward, new Point(40, 40), 12, 1, new Point(5, 0));
        (double before, _) = field.Source(40, 40);
        for (int i = 0; i < 40; i++) field.Dab(LiquifyTool.Reconstruct, new Point(40, 40), 12, 1, default);
        (double after, _) = field.Source(40, 40);
        Assert.True(Math.Abs(after - 40) < Math.Abs(before - 40) / 4, $"{before} came back only to {after}");
    }

    [Fact]
    public void BloatAndPuckerPushAndPullAboutTheCentre()
    {
        var bloat = new LiquifyField(64, 64);
        var pucker = new LiquifyField(64, 64);
        for (int i = 0; i < 10; i++)
        {
            bloat.Dab(LiquifyTool.Bloat, new Point(32, 32), 20, 1, default);
            pucker.Dab(LiquifyTool.Pucker, new Point(32, 32), 20, 1, default);
        }

        // Right of the centre: a bloat reads from nearer the middle, a pucker from further out.
        Assert.True(bloat.Source(40, 32).X < 40);
        Assert.True(pucker.Source(40, 32).X > 40);
    }

    [Fact]
    public void LiquifyPreviewKeepsOneBufferAndReportsOnlyItsDirtyRectangle()
    {
        using PixelBuffer pixels = Gradient(80, 60);
        ImageLayer layer = Layer("gradient", pixels, 0, 0);
        var field = new LiquifyField(80, 60);
        using var preview = new LiquifyPreview(layer);

        LiveEdit first = preview.Frame(field, CanvasProjection.Identity, 80, 60)!;
        MutableBufferSource source = Assert.IsType<MutableBufferSource>(first.Source);
        source.TakeDirty(source.Revision);

        field.Dab(LiquifyTool.Forward, new Point(20, 20), 8, 1, new Point(3, 0));
        LiveEdit second = preview.Frame(field, CanvasProjection.Identity, 80, 60)!;
        PixelRect dirty = source.TakeDirty(source.Revision);

        Assert.Same(source, second.Source);
        Assert.False(dirty.IsEmpty);
        Assert.True(dirty.Width < source.Width && dirty.Height < source.Height);
    }

    [Fact]
    public void ApplyingKeepsTheLayerWhereItWas()
    {
        using PixelBuffer pixels = Gradient(40, 30);
        ImageLayer layer = Layer("gradient", pixels, 5, 7);
        var field = new LiquifyField(40, 30);
        field.Dab(LiquifyTool.Twirl, new Point(20, 15), 10, 1, default);

        ImageLayer liquified = field.Apply(layer);
        try
        {
            Assert.Equal(layer.Transform, liquified.Transform);
            Assert.NotSame(pixels, liquified.Image);
            Assert.Equal(40, liquified.Image!.Width);
        }
        finally
        {
            liquified.Image!.Release();
        }

        // Nothing pushed, nothing to do.
        Assert.Same(layer, new LiquifyField(40, 30).Apply(layer));
    }
}
