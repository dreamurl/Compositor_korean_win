using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// Drawing a view of a document rather than the document: the projection, and the canvas clip.
/// </summary>
/// <remarks>
/// The claim being tested is that the canvas reuses the compositor rather than growing a second
/// one. So the check is an identity: at 100%, what the view shows has to be the finished document
/// with a pair of scissors taken to it — pixel for pixel, because a whole-pixel pan resamples
/// nothing.
/// </remarks>
public class CanvasRenderTests
{
    private static CanvasDocument Scene()
    {
        PixelBuffer background = RenderFixture.Gradient(64, 48);
        PixelBuffer patch = RenderFixture.Solid(20, 20, 220, 40, 40);

        ImageLayer turned = RenderFixture.Layer("turned", patch, 18, 10) with
        {
            Transform = new LayerTransform(new Point(18, 10), new Size(28, 14)) { Rotation = 21 },
        };

        return RenderFixture.Document(64, 48, RenderFixture.Layer("background", background), turned);
    }

    private static void Release(CanvasDocument document)
    {
        foreach (ImageLayer layer in document.Layers) layer.Image?.Release();
    }

    [Fact]
    public void AViewAtFullSizeIsTheDocumentWithScissorsTakenToIt()
    {
        CanvasDocument document = Scene();
        using var backend = new SoftwareRenderBackend();

        // A 32×24 window on a 64×48 document, centred: the view holds document pixels 16–48, 12–36.
        var viewport = new CanvasViewport { ViewSize = new Size(32, 24), Zoom = 1, FollowsFit = false };

        using PixelBuffer whole = LayerCompositor.Render(document, backend);
        using PixelBuffer view = LayerCompositor.RenderView(document, viewport, backend);

        Assert.Equal(32, view.Width);
        Assert.Equal(24, view.Height);

        for (int y = 0; y < view.Height; y++)
        {
            for (int x = 0; x < view.Width; x++)
            {
                Assert.Equal(RenderFixture.At(whole, x + 16, y + 12), RenderFixture.At(view, x, y));
            }
        }

        Release(document);
    }

    [Fact]
    public void NothingIsDrawnPastTheCanvasEdge()
    {
        PixelBuffer pixels = RenderFixture.Solid(80, 60, 30, 200, 90);
        ImageLayer overhanging = RenderFixture.Layer("overhanging", pixels) with
        {
            // Hanging off every side of a 32×24 canvas.
            Transform = new LayerTransform(new Point(-24, -18), new Size(80, 60)),
        };

        CanvasDocument document = RenderFixture.Document(32, 24, overhanging);
        using var backend = new SoftwareRenderBackend();

        // Zoomed out to a third, so there is plenty of window either side of the canvas.
        var viewport = new CanvasViewport
        {
            ViewSize = new Size(96, 72),
            Zoom = 1.0 / 3,
            FollowsFit = false,
        };

        using PixelBuffer view = LayerCompositor.RenderView(document, viewport, backend);
        Rect canvas = viewport.DeviceProjection(document.Size).Apply(new Rect(0, 0, 32, 24));

        // Inside the canvas the layer covers everything; outside it, nothing was drawn at all.
        Assert.Equal(255, RenderFixture.At(view, (int)canvas.MidX, (int)canvas.MidY).A);
        Assert.Equal(0, RenderFixture.At(view, (int)canvas.MinX - 2, (int)canvas.MidY).A);
        Assert.Equal(0, RenderFixture.At(view, (int)canvas.MaxX + 2, (int)canvas.MidY).A);
        Assert.Equal(0, RenderFixture.At(view, (int)canvas.MidX, (int)canvas.MinY - 2).A);
        Assert.Equal(0, RenderFixture.At(view, (int)canvas.MidX, (int)canvas.MaxY + 2).A);

        pixels.Release();
    }

    [Fact]
    public void AClipHoldsWhateverIsDrawnInsideIt()
    {
        using PixelBuffer pixels = RenderFixture.Solid(16, 16, 255, 255, 255);
        using var backend = new SoftwareRenderBackend();
        using IRenderSurface surface = backend.CreateSurface(16, 16);

        surface.Clear();
        surface.PushClip(new Rect(4, 4, 8, 8));
        surface.Draw(new LayerDraw
        {
            Source = new BufferSource(pixels),
            Placement = new LayerTransform(Point.Zero, new Size(16, 16)),
        });
        surface.PopClip();

        using PixelBuffer result = surface.Read();

        Assert.Equal(255, RenderFixture.At(result, 8, 8).A);
        Assert.Equal(255, RenderFixture.At(result, 4, 4).A);
        Assert.Equal(0, RenderFixture.At(result, 3, 8).A);
        Assert.Equal(0, RenderFixture.At(result, 12, 8).A);
    }

    [Fact]
    public void ClipsNest()
    {
        using PixelBuffer pixels = RenderFixture.Solid(16, 16, 255, 255, 255);
        using var backend = new SoftwareRenderBackend();
        using IRenderSurface surface = backend.CreateSurface(16, 16);

        surface.Clear();
        surface.PushClip(new Rect(4, 4, 8, 8));
        surface.PushClip(new Rect(0, 0, 6, 16));   // Wider than the first, so the first still holds.
        surface.Draw(new LayerDraw
        {
            Source = new BufferSource(pixels),
            Placement = new LayerTransform(Point.Zero, new Size(16, 16)),
        });
        surface.PopClip();
        surface.PopClip();

        using PixelBuffer result = surface.Read();

        Assert.Equal(255, RenderFixture.At(result, 5, 5).A);
        Assert.Equal(0, RenderFixture.At(result, 8, 5).A);   // Outside the inner clip.
        Assert.Equal(0, RenderFixture.At(result, 5, 2).A);   // Outside the outer one.
    }

    [Fact]
    public void AProjectionCarriesAPlacementWithoutBendingIt()
    {
        var transform = new LayerTransform(new Point(10, 20), new Size(100, 50)) { Rotation = 30 };
        var projection = new CanvasProjection(0.5, new Point(7, -3));

        LayerTransform projected = projection.Apply(transform);

        Assert.Equal(new Size(50, 25), projected.Size);
        Assert.Equal(30, projected.Rotation);

        // Every point of the layer lands where the projection says it should, corners included.
        foreach (Point unit in TransformDrag.Handles)
        {
            Point expected = projection.Apply(transform.PointAt(unit));
            Point actual = projected.PointAt(unit);
            Assert.Equal(expected.X, actual.X, precision: 9);
            Assert.Equal(expected.Y, actual.Y, precision: 9);
        }
    }

    [Fact]
    public void AZoomTheFormatWouldRefuseStillDraws()
    {
        var transform = new LayerTransform(new Point(0, 0), new Size(100, 50));
        LayerTransform tiny = new CanvasProjection(0.005, Point.Zero).Apply(transform);

        // Half a pixel wide: not something the format may store, and still something to draw.
        Assert.False(tiny.IsValid);
        Assert.True(tiny.IsDrawable);
    }

    [Fact]
    public void TheDeviceProjectionAgreesWithTheViewportsOwnArithmetic()
    {
        var document = new Size(4000, 3000);
        CanvasViewport viewport = new CanvasViewport { ViewSize = new Size(1280, 800), BackingScale = 2 }
            .Fit(document)
            .Translated(new Point(31, -17));

        CanvasProjection device = viewport.DeviceProjection(document);
        var pixel = new Point(1500, 900);

        Point asPoints = viewport.ViewPoint(pixel, document);
        Point asDevice = device.Apply(pixel);

        Assert.Equal(asPoints.X * 2, asDevice.X, precision: 6);
        Assert.Equal(asPoints.Y * 2, asDevice.Y, precision: 6);

        Point back = device.Invert(asDevice);
        Assert.Equal(pixel.X, back.X, precision: 6);
        Assert.Equal(pixel.Y, back.Y, precision: 6);
    }
}
