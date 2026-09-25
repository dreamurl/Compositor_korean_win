namespace Compositor_korean_win.Core;

/// <summary>
/// A layer's pixels shown in the corners a distortion drag has them at, while the drag goes on.
/// </summary>
/// <remarks>
/// <para>
/// Letting go resamples the layer into its corners once, at full size (<see cref="QuadWarp"/>).
/// Until then the canvas used to show only the outline. Here the same resampling runs every frame,
/// but into the frame's own pixels: the corners are carried onto the surface and only the part of
/// it inside the window is filled. Large frames use a bounded interaction resolution and sample
/// the original directly instead of first building full-image pyramid levels.
/// So a frame of the drag costs at most the window, whatever the layer — the property M3 fixed for
/// drawing and M5 for filter previews.
/// </para>
/// <para>
/// The M3 notes put this on <c>D2D13DPerspectiveTransform</c> (docs/windows-port.md 10.4). Doing it with the
/// resampler that commits the drag keeps the two paths consistent. At ordinary window sizes the
/// preview is exact; an unusually large frame is temporarily reduced while the pointer moves.
/// </para>
/// <para>
/// A linked mask sharing the layer's grid is warped with the pixels into the same box every frame,
/// so the preview is what letting go leaves. A mask placed apart is shown where it is until the
/// button comes up; only committing carries it (<see cref="QuadWarp.Distort"/>).
/// </para>
/// </remarks>
public sealed class DistortPreview(ImageLayer layer) : IDisposable
{
    private const long PreviewPixelBudget = 512 * 512;
    private PixelBuffer? _last;
    private PixelBuffer? _lastMask;
    private LiveEdit? _lastEdit;
    private Point[]? _lastCorners;
    private CanvasProjection _lastProjection;
    private int _lastWidth;
    private int _lastHeight;

    public ImageLayer Layer { get; } = layer;

    /// <summary>Pixels the last frame filled, for the tests and the report.</summary>
    public long LastPixelsWarped { get; private set; }

    /// <summary>
    /// What to draw in the layer's place for one frame, or null when the corners cannot be warped
    /// into. The pixels stay the preview's until the next frame or until it is disposed.
    /// </summary>
    public LiveEdit? Frame(IReadOnlyList<Point> corners, CanvasProjection projection, int width, int height)
    {
        if (Layer.Image is not PixelBuffer image) return null;
        if (_lastEdit is not null && _lastCorners is not null && _lastCorners.SequenceEqual(corners)
                                      && _lastProjection == projection
                                      && _lastWidth == width && _lastHeight == height)
            return _lastEdit;

        Point[] onSurface = [.. corners.Select(point => projection.Apply(point))];
        double rasterScale = PreviewScale(onSurface, width, height);
        Point[] rasterCorners = rasterScale == 1
            ? onSurface
            : [.. onSurface.Select(point => new Point(point.X * rasterScale, point.Y * rasterScale))];
        int rasterWidth = Math.Max(1, (int)Math.Ceiling(width * rasterScale));
        int rasterHeight = Math.Max(1, (int)Math.Ceiling(height * rasterScale));
        (PixelBuffer Pixels, LayerTransform Placement)? warped =
            QuadWarp.Resample(image, rasterCorners, null, new PixelRect(0, 0, rasterWidth, rasterHeight),
                              reduce: false, parallel: false);
        if (warped is not (PixelBuffer pixels, LayerTransform placed)) return null;

        _last?.Release();
        _last = pixels;
        LastPixelsWarped = (long)pixels.Width * pixels.Height;

        // Back onto the document, where the compositor expects a placement; it projects it again,
        // landing on the same whole pixels, which is why nothing resamples them a second time.
        var onDocument = new LayerTransform(
            projection.Invert(new Point(placed.Origin.X / rasterScale, placed.Origin.Y / rasterScale)),
            new Size(placed.Size.Width / rasterScale / projection.Scale,
                     placed.Size.Height / rasterScale / projection.Scale))
        {
            Sampling = LayerSampling.Nearest,
        };

        // A linked mask on the layer's grid is warped with it, into the same box, so the preview
        // shows what letting go will leave. A single-pixel mask needs no warping to say the same.
        PixelBuffer? mask = null;
        if (Layer.Mask is { IsEnabled: true, IsLinked: true, Placement: null } owned)
        {
            if (owned.Coverage is { Width: 1, Height: 1 })
            {
                mask = owned.Coverage;
            }
            else
            {
                _lastMask?.Release();
                _lastMask = QuadWarp.MaskInto(owned.Coverage, rasterCorners, outside: null, null,
                                              new PixelRect(0, 0, rasterWidth, rasterHeight), reduce: false,
                                              parallel: false)?.Pixels;
                mask = _lastMask;
            }
        }

        _lastCorners = [.. corners];
        _lastProjection = projection;
        _lastWidth = width;
        _lastHeight = height;
        _lastEdit = new LiveEdit(Layer.Id, new BufferSource(pixels)) { Placement = onDocument, Mask = mask };
        return _lastEdit;
    }

    private static double PreviewScale(IReadOnlyList<Point> corners, int width, int height)
    {
        Rect bounds = Rect.Around(corners);
        double left = Math.Max(0, bounds.X), top = Math.Max(0, bounds.Y);
        double right = Math.Min(width, bounds.MaxX), bottom = Math.Min(height, bounds.MaxY);
        double pixels = Math.Max(1, right - left) * Math.Max(1, bottom - top);
        return pixels <= PreviewPixelBudget ? 1 : Math.Sqrt(PreviewPixelBudget / pixels);
    }

    public void Dispose()
    {
        _last?.Release();
        _last = null;
        _lastMask?.Release();
        _lastMask = null;
        _lastEdit = null;
        _lastCorners = null;
    }
}
