using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// The viewport: what the canvas shows, and — the part M3 is judged on — how much of a document a
/// frame has to touch to show it.
/// </summary>
public class CanvasViewportTests
{
    private static readonly Size View = new(1280, 800);

    private static CanvasViewport Fitted(Size document, double backingScale = 1) =>
        new CanvasViewport { ViewSize = View, BackingScale = backingScale }.Fit(document);

    [Fact]
    public void FittingCentresTheDocumentAndLeavesTheMargin()
    {
        var document = new Size(4000, 3000);
        Rect placed = Fitted(document).DocumentRect(document);

        Assert.Equal(View.Width / 2, placed.MidX, precision: 9);
        Assert.Equal(View.Height / 2, placed.MidY, precision: 9);

        // The tighter axis ends exactly on the margin; the other has room to spare.
        Assert.Equal(View.Height - CanvasViewport.FitMargin, placed.Height, precision: 9);
        Assert.True(placed.Width <= View.Width - CanvasViewport.FitMargin);
    }

    [Fact]
    public void FittingIsRememberedUntilSomethingIsZoomedOrPanned()
    {
        var document = new Size(4000, 3000);
        CanvasViewport viewport = Fitted(document);
        Assert.True(viewport.FollowsFit);

        Assert.False(viewport.Translated(new Point(10, 0)).FollowsFit);
        Assert.False(viewport.ZoomedTo(1, viewport.Center, document).FollowsFit);
    }

    [Fact]
    public void ADocumentPixelSurvivesTheTripToTheViewAndBack()
    {
        var document = new Size(4000, 3000);
        CanvasViewport viewport = Fitted(document).Translated(new Point(37, -21));

        var pixel = new Point(1234.5, 678.25);
        Point back = viewport.DocumentPoint(viewport.ViewPoint(pixel, document), document);

        Assert.Equal(pixel.X, back.X, precision: 9);
        Assert.Equal(pixel.Y, back.Y, precision: 9);
    }

    [Fact]
    public void ZoomingKeepsWhateverIsUnderTheAnchorUnderIt()
    {
        var document = new Size(4000, 3000);
        CanvasViewport viewport = Fitted(document);
        var anchor = new Point(300, 220);

        Point pixel = viewport.DocumentPoint(anchor, document);
        Point after = viewport.ZoomedTo(viewport.Zoom * 4, anchor, document).ViewPoint(pixel, document);

        Assert.Equal(anchor.X, after.X, precision: 9);
        Assert.Equal(anchor.Y, after.Y, precision: 9);
    }

    [Theory]
    [InlineData(1000, CanvasViewport.MaximumZoom)]
    [InlineData(0, CanvasViewport.MinimumZoom)]
    [InlineData(-4, CanvasViewport.MinimumZoom)]
    public void ZoomStaysInRange(double asked, double expected)
    {
        var document = new Size(4000, 3000);
        CanvasViewport viewport = Fitted(document).ZoomedTo(asked, new Point(0, 0), document);

        Assert.Equal(expected, viewport.Zoom, precision: 9);
    }

    [Fact]
    public void AnUnreadableZoomIsIgnored()
    {
        var document = new Size(4000, 3000);
        CanvasViewport viewport = Fitted(document);

        Assert.Equal(viewport, viewport.ZoomedTo(double.NaN, new Point(0, 0), document));
    }

    [Fact]
    public void ResizingRefitsWhileTheViewIsStillFollowingTheFit()
    {
        var document = new Size(4000, 3000);
        CanvasViewport viewport = Fitted(document).Resized(new Size(640, 480), 1, document);

        Assert.True(viewport.FollowsFit);
        Assert.True(viewport.DocumentRect(document).Height <= 480 - CanvasViewport.FitMargin + 1e-9);
    }

    [Fact]
    public void ResizingKeepsTheMiddlePixelWhenTheViewHasBeenZoomed()
    {
        var document = new Size(4000, 3000);
        CanvasViewport viewport = Fitted(document)
            .ZoomedTo(2, new Point(400, 300), document)
            .Translated(new Point(60, -40));

        Point before = viewport.DocumentPoint(viewport.Center, document);

        // A move to a denser display: the same window in points, twice the device pixels.
        CanvasViewport moved = viewport.Resized(viewport.ViewSize, 2, document);
        Point after = moved.DocumentPoint(moved.Center, document);

        Assert.False(moved.FollowsFit);
        Assert.Equal(before.X, after.X, precision: 6);
        Assert.Equal(before.Y, after.Y, precision: 6);
    }

    [Fact]
    public void OnlyWhatTheViewCanShowCounts()
    {
        var document = new Size(4000, 3000);
        CanvasViewport viewport = Fitted(document).ZoomedTo(1, new Point(640, 400), document);

        Rect visible = viewport.VisibleDocumentRect(document);

        // At 1:1 the view holds its own size in document pixels, and never more than the canvas.
        Assert.Equal(View.Width, visible.Width, precision: 6);
        Assert.Equal(View.Height, visible.Height, precision: 6);
        Assert.True(visible.MinX >= 0 && visible.MinY >= 0);
        Assert.True(visible.MaxX <= document.Width && visible.MaxY <= document.Height);
    }

    [Fact]
    public void AnOffscreenDocumentCostsNothing()
    {
        var document = new Size(4000, 3000);
        CanvasViewport viewport = Fitted(document).Translated(new Point(100_000, 0));

        Assert.True(viewport.VisibleDocumentRect(document).IsEmpty);
        Assert.Equal(0L, viewport.PixelsToRead(document));
    }

    /// <remarks>
    /// The measurement M3 closes on. Sixty frames a second on a hundred-megapixel document is not a
    /// claim about the machine — it is a claim that a frame's work follows the window and not the
    /// document, and this is that claim stated as a number. The bound is four times the view
    /// because the pyramid halves in whole steps, so a frame can sit just under one more halving.
    /// </remarks>
    [Theory]
    [InlineData(1000)]
    [InlineData(10_000)]   // A hundred megapixels.
    [InlineData(30_000)]   // The largest canvas the format accepts: nine hundred megapixels.
    public void AFramesWorkFollowsTheWindowAndNotTheDocument(int side)
    {
        var document = new Size(side, side);
        long viewPixels = (long)(View.Width * View.Height);

        Assert.True(Fitted(document).PixelsToRead(document) <= viewPixels * 4);

        // And zoomed in, where no halving helps, the visible rectangle is doing the bounding.
        CanvasViewport close = Fitted(document).ZoomedTo(1, new Point(0, 0), document);
        Assert.True(close.PixelsToRead(document) <= viewPixels * 4);
    }

    [Fact]
    public void AHundredMegapixelsCostsAboutWhatOneMegapixelDoes()
    {
        long small = Fitted(new Size(1000, 1000)).PixelsToRead(new Size(1000, 1000));
        long large = Fitted(new Size(10_000, 10_000)).PixelsToRead(new Size(10_000, 10_000));

        // A hundred times the document, within a factor of four of the work — the halvings, not the
        // document, decide. Without the pyramid this ratio would be a hundred.
        Assert.True(large <= small * 4, $"{large} against {small}");
    }
}
