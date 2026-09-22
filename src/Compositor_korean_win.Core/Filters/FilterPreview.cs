namespace Compositor_korean_win.Core;

/// <summary>
/// A filter being tried on a layer: what the canvas shows while the settings are still moving.
/// </summary>
/// <remarks>
/// <para>
/// Upstream previews from a copy of the layer scaled to at most 2048 pixels on its longest side
/// (8000 for the adjustments), filters that, and draws it stretched. Here the preview is cut to
/// what the frame shows instead: the layer's pixels at the pyramid level the renderer would have
/// read anyway, cropped to the part in view plus the filter's reach. At "fit" that is about the
/// window's worth of pixels; zoomed in on a corner it is the corner. Either way a settings change
/// costs the frame, not the layer — which is the same property M3 fixed for drawing and M4 for
/// painting (docs/progress.md 4.4, 5.4).
/// </para>
/// <para>
/// The reduced pixels are kept between frames, so after the first frame only the crop and the
/// filter itself are redone. Lens Correction is the exception to the crop: it bends the whole
/// image about its centre, so it always runs on all of the reduced layer.
/// </para>
/// </remarks>
public sealed class FilterPreview : IDisposable
{
    private readonly DownsamplePyramid _pyramid = new();
    private PixelBuffer? _last;

    public FilterPreview(ImageLayer layer, FilterKind kind, FilterSettings settings,
                         DocumentSelection? selection = null)
    {
        if (layer.Image is null) throw new ArgumentException("the layer has no pixels to filter", nameof(layer));
        Layer = layer;
        Kind = kind;
        Settings = settings;
        Selection = selection;
    }

    public ImageLayer Layer { get; }
    public FilterKind Kind { get; set; }
    public FilterSettings Settings { get; set; }
    public DocumentSelection? Selection { get; }

    /// <summary>Pixels the last frame ran the filter over, for the report and its budget.</summary>
    public long LastPixelsFiltered { get; private set; }

    /// <summary>
    /// What to draw in the layer's place for one frame drawn through <paramref name="projection"/>
    /// onto a surface of <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    /// <remarks>
    /// The edit's pixels stay the preview's, and live until the next frame or until it is disposed.
    /// </remarks>
    public LiveEdit Frame(CanvasProjection projection, int width, int height)
    {
        PixelBuffer image = Layer.Image!;
        int w = image.Width, h = image.Height;

        int level = LayerGeometry.LevelFor(projection.Apply(Layer.Transform), w);
        (PixelBuffer reduced, int applied) = _pyramid.Reduced(image, level);
        int unit = 1 << applied;
        double scale = 1.0 / unit;

        // The reduced grid, padded by the filter's reach, in reduced pixels. It is placed through
        // the layer's own pixels: level k pixel i covers layer pixels i·2^k to (i+1)·2^k, so a grid
        // that rounded up past the layer's edge is placed a little past it, exactly as drawn.
        int reach = (int)Math.Ceiling(LayerFilters.Margin(Layer, Kind, Settings) * scale);
        var grid = new PixelRect(-reach, -reach, reduced.Width + reach * 2, reduced.Height + reach * 2);
        LayerTransform gridPlacement = LayerGeometry.Place(Layer.Transform, Scaled(grid, unit), w, h);

        PixelRect crop = grid;
        if (Kind != FilterKind.LensCorrection)
        {
            LayerTransform onSurface = projection.Apply(gridPlacement);
            PixelRect area = LayerGeometry.Bounds(onSurface).Intersect(new PixelRect(0, 0, width, height));
            if (area.IsEmpty) area = new PixelRect(0, 0, 1, 1);

            PixelRect visible = LayerGeometry.SourceRegion(area, onSurface, grid.Width, grid.Height);

            // Everything the visible pixels reach, and a pixel for the final resample.
            crop = visible.Inflate(reach + 1).Intersect(new PixelRect(0, 0, grid.Width, grid.Height));
            if (crop.IsEmpty) crop = new PixelRect(0, 0, 1, 1);
            crop = crop with { X = crop.X + grid.X, Y = crop.Y + grid.Y };
        }

        using PixelBuffer original = PixelRegion.Copy(reduced, crop);
        PixelPlacement placement = new(0, 0, unit);
        PixelBuffer filtered = PixelFilters.Run(original, Kind, Settings, scale, placement.Offset(crop.X, crop.Y));

        if (Selection is not null)
        {
            byte[] levels = LayerFilters.SelectionLevels(Selection, Layer.Transform, w, h, unit, crop);
            PixelFilters.Confine(original, filtered, levels);
        }

        LastPixelsFiltered = (long)crop.Width * crop.Height;
        _last?.Release();
        _last = filtered;

        return new LiveEdit(Layer.Id, new BufferSource(filtered) { Cacheable = false })
        {
            Placement = LayerGeometry.Place(Layer.Transform, Scaled(crop, unit), w, h),
        };
    }

    /// <summary>The same settings run over the layer for good.</summary>
    public ImageLayer Commit() => LayerFilters.Apply(Layer, Kind, Settings, Selection);

    private static PixelRect Scaled(PixelRect region, int unit) =>
        new(region.X * unit, region.Y * unit, region.Width * unit, region.Height * unit);

    public void Dispose()
    {
        _last?.Release();
        _last = null;
        _pyramid.Dispose();
    }
}
