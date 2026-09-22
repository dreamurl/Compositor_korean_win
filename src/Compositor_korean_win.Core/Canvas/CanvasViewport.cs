namespace Compositor_korean_win.Core;

/// <summary>
/// What part of a document the canvas shows: a zoom, a pan, and the view they apply to.
/// </summary>
/// <remarks>
/// <para>
/// Document coordinates are pixels with a top-left origin. View coordinates are the points the
/// shell draws in. <see cref="Zoom"/> is device pixels per document pixel, so 1 shows a document
/// at its own resolution however dense the display is, and <see cref="PointsPerPixel"/> — zoom
/// divided by the backing scale — is what every conversion actually goes through.
/// </para>
/// <para>
/// Immutable, like the document model: a pan or a zoom makes a new viewport rather than editing
/// one. The shell can then keep the viewport it drew the last frame with while working out the
/// next, and everything here is testable without a window.
/// </para>
/// <para>
/// <b>This is where M3's cost argument starts.</b> A hundred-megapixel document cannot be
/// composited whole at sixty frames a second on any machine, and does not have to be:
/// <see cref="VisibleDocumentRect"/> is the only part a frame touches, and it is bounded by the
/// view rather than by the document. Zoomed out far enough that the whole document is on screen
/// the visible rectangle is the document — but then every output pixel comes from a halved copy
/// (<see cref="DownsamplePyramid"/>), so what a frame reads is bounded by the view again.
/// <see cref="PixelsToRead"/> puts a number on that, and the tests hold it there.
/// </para>
/// </remarks>
public sealed record CanvasViewport
{
    /// <summary>The zoom range upstream allows: a thousandth to thirty-two times.</summary>
    public const double MinimumZoom = 0.001;

    public const double MaximumZoom = 32;

    /// <summary>Points left clear around a document that has been fitted to the view.</summary>
    public const double FitMargin = 96;

    /// <summary>The size of the view, in points.</summary>
    public Size ViewSize { get; init; }

    /// <summary>Device pixels per point — 2 on a high-resolution display. Never below 1.</summary>
    public double BackingScale { get; init; } = 1;

    /// <summary>Device pixels per document pixel.</summary>
    public double Zoom { get; init; } = 1;

    /// <summary>How far the document is pushed from the middle of the view, in points.</summary>
    public Point Pan { get; init; }

    /// <summary>
    /// Whether the view refits on a resize instead of keeping this zoom.
    /// </summary>
    /// <remarks>
    /// Set until the user zooms or pans, so opening a document and dragging the window bigger keeps
    /// it fitted, while a document someone has zoomed into stays where they put it.
    /// </remarks>
    public bool FollowsFit { get; init; } = true;

    /// <summary>View points per document pixel.</summary>
    public double PointsPerPixel => Zoom / Scale;

    /// <summary>The middle of the view, in points.</summary>
    public Point Center => new(ViewSize.Width / 2, ViewSize.Height / 2);

    private double Scale => Math.Max(1, BackingScale);

    /// <summary>Where the document lands in the view, in points.</summary>
    public Rect DocumentRect(Size documentSize)
    {
        double width = documentSize.Width * PointsPerPixel;
        double height = documentSize.Height * PointsPerPixel;
        return new Rect(Center.X - width / 2 + Pan.X, Center.Y - height / 2 + Pan.Y, width, height);
    }

    /// <summary>The document pixel under a view point.</summary>
    public Point DocumentPoint(Point view, Size documentSize)
    {
        Rect placed = DocumentRect(documentSize);
        double scale = PointsPerPixel;
        return new Point((view.X - placed.X) / scale, (view.Y - placed.Y) / scale);
    }

    /// <summary>Where a document pixel lands in the view.</summary>
    public Point ViewPoint(Point document, Size documentSize)
    {
        Rect placed = DocumentRect(documentSize);
        double scale = PointsPerPixel;
        return new Point(placed.X + document.X * scale, placed.Y + document.Y * scale);
    }

    /// <summary>The document zoomed to sit inside the view with a margin, centred.</summary>
    public CanvasViewport Fit(Size documentSize)
    {
        if (!(ViewSize.Width > 0) || !(ViewSize.Height > 0)
            || !(documentSize.Width > 0) || !(documentSize.Height > 0))
        {
            return this with { FollowsFit = true };
        }

        double zoom = Math.Min(Math.Max(1, ViewSize.Width - FitMargin) / documentSize.Width,
                               Math.Max(1, ViewSize.Height - FitMargin) / documentSize.Height) * Scale;

        return this with { Zoom = Clamp(zoom), Pan = Point.Zero, FollowsFit = true };
    }

    /// <summary>
    /// The viewport after the window changed size, or after the document moved to another display.
    /// </summary>
    /// <remarks>
    /// A viewport that is not following the fit keeps the document pixel in the middle of the view
    /// where it is. The pan is in points, so it has to be rescaled by however much a point is now
    /// worth in document pixels — otherwise dragging a window between displays shifts the document.
    /// </remarks>
    public CanvasViewport Resized(Size size, double backingScale, Size? documentSize)
    {
        double before = PointsPerPixel;
        CanvasViewport resized = this with { ViewSize = size, BackingScale = Math.Max(1, backingScale) };

        if (FollowsFit && documentSize is Size document) return resized.Fit(document);

        double ratio = resized.PointsPerPixel / before;
        if (!double.IsFinite(ratio) || ratio <= 0) return resized;
        return resized with { Pan = new Point(Pan.X * ratio, Pan.Y * ratio) };
    }

    /// <summary>
    /// The viewport zoomed to <paramref name="value"/>, keeping whatever sits under
    /// <paramref name="anchor"/> under it.
    /// </summary>
    /// <remarks>
    /// The anchor is what makes a scroll wheel feel attached to the image rather than to the
    /// window: the document point beneath the pointer is taken first, and the pan is corrected
    /// afterwards so that point lands back where it was.
    /// </remarks>
    public CanvasViewport ZoomedTo(double value, Point anchor, Size documentSize)
    {
        if (!double.IsFinite(value)) return this;

        Point pixel = DocumentPoint(anchor, documentSize);
        CanvasViewport zoomed = this with { Zoom = Clamp(value), FollowsFit = false };
        Point moved = zoomed.ViewPoint(pixel, documentSize);

        return zoomed with
        {
            Pan = new Point(Pan.X + anchor.X - moved.X, Pan.Y + anchor.Y - moved.Y),
        };
    }

    /// <summary>The viewport panned by <paramref name="delta"/> points.</summary>
    public CanvasViewport Translated(Point delta) => this with
    {
        Pan = new Point(Pan.X + delta.X, Pan.Y + delta.Y),
        FollowsFit = false,
    };

    /// <summary>
    /// The part of the document the view can show, in document pixels, clipped to the canvas.
    /// </summary>
    public Rect VisibleDocumentRect(Size documentSize)
    {
        double scale = PointsPerPixel;
        if (!(scale > 0) || !double.IsFinite(scale)) return default;

        Rect placed = DocumentRect(documentSize);
        Rect seen = Rect.FromBounds((0 - placed.X) / scale, (0 - placed.Y) / scale,
                                    (ViewSize.Width - placed.X) / scale,
                                    (ViewSize.Height - placed.Y) / scale);

        return seen.Intersect(new Rect(0, 0, documentSize.Width, documentSize.Height));
    }

    /// <summary>Whole document pixels a frame has to reach, rounded outwards.</summary>
    public PixelRect VisiblePixels(Size documentSize) => VisibleDocumentRect(documentSize).Enclosing();

    /// <summary>
    /// Source pixels one full-canvas layer contributes to this frame, counting the halvings it is
    /// drawn from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the figure M3 is held to. Reading it straight off the visible rectangle would be
    /// wrong in the case that matters most — zoomed right out, every pixel of a hundred-megapixel
    /// document is visible — because at that zoom the draw comes from a halved copy, and each
    /// halving quarters what is read. What is left is about the number of output pixels, whatever
    /// the document's size.
    /// </para>
    /// <para>
    /// The halvings themselves are built once and cached (<see cref="DownsamplePyramid"/>), so they
    /// are a cost of opening a document, not of drawing a frame.
    /// </para>
    /// </remarks>
    public long PixelsToRead(Size documentSize)
    {
        PixelRect visible = VisiblePixels(documentSize);
        if (visible.IsEmpty) return 0;

        // Device pixels per document pixel is exactly what the pyramid asks for, and that is the
        // zoom — the backing scale cancels out of points-per-pixel on the way back to devices.
        int level = DownsamplePyramid.LevelFor(Zoom);
        long divisor = 1L << (level * 2);
        return ((long)visible.Width * visible.Height + divisor - 1) / divisor;
    }

    private static double Clamp(double value) =>
        double.IsNaN(value) ? MinimumZoom : Math.Clamp(value, MinimumZoom, MaximumZoom);
}
