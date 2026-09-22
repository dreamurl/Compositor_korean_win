namespace Compositor_korean_win.Core;

/// <summary>
/// A layer's pixels shown in the corners a distortion drag has them at, while the drag goes on.
/// </summary>
/// <remarks>
/// <para>
/// Letting go resamples the layer into its corners once, at full size (<see cref="QuadWarp"/>).
/// Until then the canvas used to show only the outline. Here the same resampling runs every frame,
/// but into the frame's own pixels: the corners are carried onto the surface, the pyramid level is
/// chosen from how large the shape is there, and only the part of it inside the window is filled.
/// So a frame of the drag costs at most the window, whatever the layer — the property M3 fixed for
/// drawing and M5 for filter previews.
/// </para>
/// <para>
/// The M3 notes put this on <c>D2D13DPerspectiveTransform</c> (docs/windows-port.md 10.4). Doing it with the
/// resampler that commits the drag instead means the preview is the result, not an approximation of
/// it, and both backends draw the same pixels (docs/progress.md 6).
/// </para>
/// <para>
/// A mask sharing the layer's grid stays where the layer was during the drag, since the drawn
/// pixels no longer share that grid. Committing leaves the mask over the layer's new box, so the two
/// differ for a masked layer until the button comes up.
/// </para>
/// </remarks>
public sealed class DistortPreview(ImageLayer layer) : IDisposable
{
    private readonly DownsamplePyramid _pyramid = new();
    private PixelBuffer? _last;

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

        Point[] onSurface = [.. corners.Select(point => projection.Apply(point))];
        (PixelBuffer Pixels, LayerTransform Placement)? warped =
            QuadWarp.Resample(image, onSurface, _pyramid, new PixelRect(0, 0, width, height));
        if (warped is not (PixelBuffer pixels, LayerTransform placed)) return null;

        _last?.Release();
        _last = pixels;
        LastPixelsWarped = (long)pixels.Width * pixels.Height;

        // Back onto the document, where the compositor expects a placement; it projects it again,
        // landing on the same whole pixels, which is why nothing resamples them a second time.
        var onDocument = new LayerTransform(
            projection.Invert(placed.Origin),
            new Size(placed.Size.Width / projection.Scale, placed.Size.Height / projection.Scale))
        {
            Sampling = LayerSampling.Nearest,
        };

        return new LiveEdit(Layer.Id, new BufferSource(pixels) { Cacheable = false }) { Placement = onDocument };
    }

    public void Dispose()
    {
        _last?.Release();
        _last = null;
        _pyramid.Dispose();
    }
}
