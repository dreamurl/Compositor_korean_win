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
/// The reduced pixels and the finished frame are kept between draws. Geometric distortions are
/// the exception to crop-to-view because they can read from anywhere in the layer; they use a
/// bounded whole-layer interaction raster and the full image only when the user commits.
/// </para>
/// </remarks>
public sealed class FilterPreview : IDisposable
{
    // Geometric filters have to see the whole layer, but an interactive preview does not need the
    // commit's full resolution. Keeping this near a 512 x 512 image makes the slowest trigonometric
    // maps finish within one pointer frame even on a CPU-only machine.
    private const long GeometricPreviewPixelBudget = 512 * 512;
    private const int GeometricPreviewMaximumSide = 1024;

    private readonly DownsamplePyramid _pyramid = new();
    private PixelBuffer? _last;
    private PixelBuffer? _geometricSource;
    private PixelRect _geometricGrid;
    private int _geometricUnit;
    private LiveEdit? _lastEdit;
    private FilterSettings? _lastSettings;
    private FilterKind _lastKind;
    private CanvasProjection _lastProjection;
    private int _lastWidth;
    private int _lastHeight;

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
        bool geometric = DistortFilters.IsGeometric(Kind);
        if (_lastEdit is not null && _lastKind == Kind && _lastSettings == Settings
                                      && (geometric || (_lastProjection == projection
                                                       && _lastWidth == width && _lastHeight == height)))
            return _lastEdit;

        PixelBuffer image = Layer.Image!;
        int w = image.Width, h = image.Height;

        if (geometric)
            return GeometricFrame(image, w, h, projection, width, height);

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

        LayerTransform onSurface = projection.Apply(gridPlacement);
        PixelRect area = LayerGeometry.Bounds(onSurface).Intersect(new PixelRect(0, 0, width, height));
        if (area.IsEmpty) area = new PixelRect(0, 0, 1, 1);

        PixelRect visible = LayerGeometry.SourceRegion(area, onSurface, grid.Width, grid.Height);

        // Everything the visible pixels reach, and a pixel for the final resample.
        PixelRect crop = visible.Inflate(reach + 1).Intersect(new PixelRect(0, 0, grid.Width, grid.Height));
        if (crop.IsEmpty) crop = new PixelRect(0, 0, 1, 1);
        crop = crop with { X = crop.X + grid.X, Y = crop.Y + grid.Y };

        using PixelBuffer original = PixelRegion.Copy(reduced, crop);
        PixelPlacement placement = new(0, 0, unit);
        PixelBuffer filtered = PixelFilters.Run(original, Kind, Settings, scale, placement.Offset(crop.X, crop.Y));

        if (Selection is not null)
        {
            byte[] levels = LayerFilters.SelectionLevels(Selection, Layer.Transform, w, h, unit, crop);
            PixelFilters.Confine(original, filtered, levels);
        }

        LastPixelsFiltered = (long)crop.Width * crop.Height;
        return Remember(filtered, new LiveEdit(Layer.Id, new BufferSource(filtered))
        {
            Placement = LayerGeometry.Place(Layer.Transform, Scaled(crop, unit), w, h),
        }, projection, width, height);
    }

    /// <summary>
    /// Distortions read from anywhere in the layer, so crop-to-view cannot make them cheap. Build
    /// one bounded source directly from the original instead of constructing every full-image
    /// pyramid level, then keep both the filtered pixels and their GPU upload until settings move.
    /// </summary>
    private LiveEdit GeometricFrame(PixelBuffer image, int width, int height,
                                    CanvasProjection projection, int surfaceWidth, int surfaceHeight)
    {
        int margin = LayerFilters.Margin(Layer, Kind, Settings);
        int unit = (int)PreviewUnit(width + margin * 2, height + margin * 2);
        int baseWidth = Math.Max(1, (int)Math.Ceiling((double)width / unit));
        int baseHeight = Math.Max(1, (int)Math.Ceiling((double)height / unit));
        int previewMargin = (int)Math.Ceiling((double)margin / unit);
        var grid = new PixelRect(-previewMargin, -previewMargin,
                                 baseWidth + previewMargin * 2, baseHeight + previewMargin * 2);

        if (_geometricSource is null || _geometricGrid != grid || _geometricUnit != unit)
        {
            _geometricSource?.Release();
            _geometricSource = ReducedCopy(image, baseWidth, baseHeight, previewMargin);
            _geometricGrid = grid;
            _geometricUnit = unit;
        }
        PixelBuffer original = _geometricSource;
        PixelBuffer filtered = PixelFilters.Run(original, Kind, Settings, 1.0 / unit,
                                                 new PixelPlacement(grid.X, grid.Y, unit));

        if (Selection is not null)
        {
            byte[] levels = LayerFilters.SelectionLevels(Selection, Layer.Transform, width, height, unit, grid);
            PixelFilters.Confine(original, filtered, levels);
        }

        LastPixelsFiltered = (long)grid.Width * grid.Height;
        return Remember(filtered, new LiveEdit(Layer.Id, new BufferSource(filtered))
        {
            Placement = LayerGeometry.Place(Layer.Transform, Scaled(grid, unit), width, height),
        }, projection, surfaceWidth, surfaceHeight);
    }

    private LiveEdit Remember(PixelBuffer filtered, LiveEdit edit, CanvasProjection projection, int width, int height)
    {
        _last?.Release();
        _last = filtered;
        _lastEdit = edit;
        _lastSettings = Settings;
        _lastKind = Kind;
        _lastProjection = projection;
        _lastWidth = width;
        _lastHeight = height;
        return edit;
    }

    private static double PreviewUnit(int width, int height)
    {
        double unit = 1;
        while (Math.Ceiling(width / unit) > GeometricPreviewMaximumSide
               || Math.Ceiling(height / unit) > GeometricPreviewMaximumSide
               || (long)Math.Ceiling(width / unit) * (long)Math.Ceiling(height / unit) > GeometricPreviewPixelBudget)
            unit *= 2;
        return unit;
    }

    private static PixelBuffer ReducedCopy(PixelBuffer image, int width, int height)
    {
        if (width == image.Width && height == image.Height) return image.Retain();

        Point[] corners =
        [
            new Point(0, 0), new Point(width, 0), new Point(width, height), new Point(0, height),
        ];
        return QuadWarp.Resample(image, corners, null, new PixelRect(0, 0, width, height), reduce: false)!.Value.Pixels;
    }

    /// <summary>A reduced whole layer with transparent room around filters such as Wave.</summary>
    private static PixelBuffer ReducedCopy(PixelBuffer image, int width, int height, int margin)
    {
        PixelBuffer reduced = ReducedCopy(image, width, height);
        if (margin == 0) return reduced;
        try
        {
            return PixelRegion.Copy(reduced,
                new PixelRect(-margin, -margin, width + margin * 2, height + margin * 2));
        }
        finally
        {
            reduced.Release();
        }
    }

    /// <summary>The same settings run over the layer for good.</summary>
    public ImageLayer Commit() => LayerFilters.Apply(Layer, Kind, Settings, Selection);

    private static PixelRect Scaled(PixelRect region, int unit) =>
        new(region.X * unit, region.Y * unit, region.Width * unit, region.Height * unit);

    public void Dispose()
    {
        _last?.Release();
        _last = null;
        _lastEdit = null;
        _geometricSource?.Release();
        _geometricSource = null;
        _pyramid.Dispose();
    }
}
