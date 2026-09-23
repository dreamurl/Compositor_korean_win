using Compositor_korean_win.Core;
using Point = Compositor_korean_win.Core.Point;
using Size = Compositor_korean_win.Core.Size;

namespace Compositor_korean_win.Shell;

/// <summary>The Image and Filter menus' entries that open a preview.</summary>
internal enum FilterCommand
{
    Levels,
    Curves,
    HueSaturation,
    Exposure,
    GradientMap,
    Grain,
    GaussianBlur,
    MotionBlur,
    AddNoise,
    LensCorrection,
}

/// <summary>
/// An adjustment or a filter being set: its settings, the preview they drive, and OK or Cancel.
/// </summary>
/// <remarks>
/// <para>
/// The canvas holds the edit and the sheet (<see cref="FilterSheet"/>) only reads and writes its
/// settings, the way upstream's <c>EditorSession</c> owns <c>FilterEdit</c> and its sheets bind to
/// it. So the preview follows whatever changed the settings — a slider, a typed number, an
/// eyedropper on the canvas — without the sheet knowing how a preview is made.
/// </para>
/// <para>
/// Three paths. On its own, a command previews on the chosen layer's pixels and commits into them as
/// one history step. From Layer › New Adjustment Layer it adds an adjustment layer above the chosen
/// one, which the ordinary compositor already draws — so its preview is simply the document drawn
/// with the new settings, and Cancel takes the layer away again. And an existing adjustment layer is
/// edited in place the same way, Cancel putting its old settings back.
/// </para>
/// </remarks>
internal sealed partial class CanvasView
{
    private FilterCommand _command;
    private FilterPreview? _preview;
    private Guid? _adjusting;
    private CanvasDocument? _beforeAdjusting;
    private bool _visibleBeforeAdjusting;
    private FilterSettings _filterSettings = new();
    private bool _previewOn = true;

    /// <summary>
    /// Each filter's last settings this session, so opening one again starts where it was left, as
    /// in Photoshop. Adjustments start from nothing each time: their settings live in the layer.
    /// </summary>
    private readonly Dictionary<FilterCommand, FilterSettings> _lastFilterSettings = [];

    /// <summary>Whether a command is open, which takes the keys and the pointer until it closes.</summary>
    public bool IsFiltering => _preview is not null || _adjusting is not null;

    /// <summary>The open command, while one is.</summary>
    public FilterCommand? OpenFilter => IsFiltering ? _command : null;

    /// <summary>Whether the open command edits an adjustment layer rather than a layer's pixels.</summary>
    public bool FilteringLayer => _adjusting is not null;

    /// <summary>Whether the open command runs over pixels inside a selection only.</summary>
    public bool FilterLimitedToSelection => _preview is not null && _selection is not null;

    internal static bool IsAdjustment(FilterCommand command) => command <= FilterCommand.Grain;

    /// <summary>What menus and the history call it.</summary>
    internal static TextKey FilterTitle(FilterCommand command) => command switch
    {
        FilterCommand.Levels => TextKey.AdjustLevels,
        FilterCommand.Curves => TextKey.AdjustCurves,
        FilterCommand.HueSaturation => TextKey.AdjustHueSaturation,
        FilterCommand.Exposure => TextKey.AdjustExposure,
        FilterCommand.GradientMap => TextKey.AdjustGradientMap,
        FilterCommand.Grain => TextKey.AdjustGrain,
        FilterCommand.GaussianBlur => TextKey.FilterGaussianBlur,
        FilterCommand.MotionBlur => TextKey.FilterMotionBlur,
        FilterCommand.AddNoise => TextKey.FilterAddNoise,
        _ => TextKey.FilterLensCorrection,
    };

    private static FilterCommand CommandFor(AdjustmentKind kind) => kind switch
    {
        AdjustmentKind.Levels => FilterCommand.Levels,
        AdjustmentKind.Curves => FilterCommand.Curves,
        AdjustmentKind.Hsv => FilterCommand.HueSaturation,
        AdjustmentKind.Exposure => FilterCommand.Exposure,
        AdjustmentKind.GradientMap => FilterCommand.GradientMap,
        _ => FilterCommand.Grain,
    };

    /// <summary>
    /// The open command's settings. For an adjustment, <see cref="FilterSettings.Adjustment"/> is
    /// the adjustment. Setting them moves the preview.
    /// </summary>
    public FilterSettings FilterSettings
    {
        get => _filterSettings;
        set
        {
            _filterSettings = value;
            ShowFilter();
        }
    }

    /// <summary>The open adjustment, as a shorthand for the part of the settings that holds it.</summary>
    public LayerAdjustment FilterAdjustment
    {
        get => _filterSettings.Adjustment ?? StartingAdjustment(_command, _filterSettings.Seed);
        set => FilterSettings = _filterSettings with { Adjustment = value };
    }

    /// <summary>Whether the canvas shows the command's result or the pixels as they were.</summary>
    public bool FilterPreviewOn
    {
        get => _previewOn;
        set
        {
            _previewOn = value;
            ShowFilter();
        }
    }

    /// <summary>Opens a command from the Image, Layer or Filter menu.</summary>
    public void StartFilter(FilterCommand command, bool asLayer)
    {
        if (IsFiltering) return;
        Start(command, asLayer && IsAdjustment(command));
        NeedsRedraw = true;
    }

    /// <summary>Whether a command could run over the chosen layer's pixels now.</summary>
    public bool CanFilter =>
        // A filter reworks a layer's pixels; a mask has none to rework, as upstream has it.
        !IsFiltering && !EditingMask && _document is not null && Primary is Guid id
        && _document.Layer(id) is { Image: not null, IsGroup: false, Adjustment: null };

    /// <summary>Whether an adjustment layer could be added now.</summary>
    public bool CanAddAdjustmentLayer => !IsFiltering && _document is not null;

    /// <summary>Opens the settings of an adjustment layer already in the document.</summary>
    public void EditAdjustmentLayer(Guid id)
    {
        if (IsFiltering || _document?.Layer(id) is not { Adjustment: LayerAdjustment adjustment } layer) return;

        _command = CommandFor(adjustment.Kind);
        _previewOn = true;
        _filterSettings = new FilterSettings { Adjustment = adjustment, Seed = adjustment.Grain.Seed };
        _beforeAdjusting = _document;
        _visibleBeforeAdjusting = layer.IsVisible;
        _history.Begin(HistoryName.Of(TextKey.HistoryEditAdjustmentLayer, FilterTitle(_command)), _document, id);
        _adjusting = id;

        _chosen.Clear();
        _chosen.Add(id);
        NeedsRedraw = true;
    }

    private void Start(FilterCommand command, bool asLayer)
    {
        if (_document is null) return;

        _command = command;
        _previewOn = true;
        uint seed = (uint)Random.Shared.Next();
        _filterSettings = IsAdjustment(command)
            ? new FilterSettings { Adjustment = StartingAdjustment(command, seed), Seed = seed }
            : (_lastFilterSettings.GetValueOrDefault(command) ?? new FilterSettings()) with { Seed = seed };

        ImageLayer? chosen = Primary is Guid id ? _document.Layer(id) : null;

        if (asLayer)
        {
            // Above the chosen layer and in its folder, or at the top when nothing is chosen.
            int index = chosen is null ? _document.Layers.Count : _document.IndexOf(chosen.Id) + 1;
            var layer = new ImageLayer
            {
                Id = Guid.NewGuid(),
                // A layer's name is the user's from here on, so it is fixed in today's language.
                Name = Localizer.Text(FilterTitle(command)),
                Transform = new LayerTransform(Point.Zero, new Size(_document.Width, _document.Height)),
                ParentId = chosen?.ParentId,
                Adjustment = _filterSettings.Adjustment,
            };

            _beforeAdjusting = _document;
            _visibleBeforeAdjusting = true;
            _history.Begin(HistoryName.Of(TextKey.HistoryAdjustmentLayer, FilterTitle(command)), _document, layer.Id);

            var layers = _document.Layers.ToList();
            layers.Insert(Math.Clamp(index, 0, layers.Count), layer);
            _document = _document with { Layers = layers.ToEquatableList() };
            _adjusting = layer.Id;
            return;
        }

        // Pixels to run over: a layer with an image, not a folder and not an adjustment.
        if (chosen is not { Image: not null, IsGroup: false, Adjustment: null }) return;

        _preview = new FilterPreview(chosen, KindFor(command), _filterSettings, _selection);
    }

    /// <summary>Puts the settings where the canvas draws from.</summary>
    private void ShowFilter()
    {
        if (_preview is not null)
        {
            _preview.Settings = _filterSettings;
        }
        else if (_adjusting is Guid id && _document?.Layer(id) is ImageLayer layer)
        {
            _document = _document.Replacing(layer with
            {
                Adjustment = _filterSettings.Adjustment,
                // Preview off hides the adjustment layer, which is what "as it was" means for it.
                IsVisible = _previewOn && _visibleBeforeAdjusting,
            });
        }

        NeedsRedraw = true;
    }

    /// <summary>Closes the open command: OK keeps what it did, Cancel puts everything back.</summary>
    public void FinishFilter(bool keep)
    {
        ReleaseSource();

        if (_preview is FilterPreview preview)
        {
            _preview = null;

            if (keep && _document is not null)
            {
                _lastFilterSettings[_command] = _filterSettings;
                _history.Begin(FilterTitle(_command), _document, preview.Layer.Id);
                _document = _document.Replacing(preview.Commit());
                _history.End(_document, preview.Layer.Id);
            }

            preview.Dispose();
        }

        if (_adjusting is Guid added)
        {
            _adjusting = null;

            if (keep && _document?.Layer(added) is ImageLayer layer)
            {
                _document = _document.Replacing(layer with { IsVisible = _visibleBeforeAdjusting });
                _chosen.Clear();
                _chosen.Add(added);
            }
            else
            {
                _document = _beforeAdjusting;
            }

            _beforeAdjusting = null;
            _history.End(_document, keep ? added : Primary);
        }

        NeedsRedraw = true;
    }

    /// <summary>Handles a key for an open command. Returns true when it was the command's.</summary>
    private bool FilterKey(int key, bool control)
    {
        if (!IsFiltering) return false;

        switch (key)
        {
            case Win32.VK_RETURN:
                FinishFilter(keep: true);
                return true;

            case Win32.VK_ESCAPE:
                FinishFilter(keep: false);
                return true;

            // Zooming to look closer is fine; anything that edits the document is not.
            case Win32.VK_0 or Win32.VK_1 when control:
                return false;

            default:
                return true;
        }
    }

    /// <summary>
    /// A click on the canvas while a command is open does nothing to the document. The sheet's
    /// eyedroppers take their clicks before they get here (<see cref="Sheet.UsesCanvas"/>).
    /// </summary>
    private bool FilterClick(Point view) => IsFiltering;

    /// <summary>The document point under a point in the window, or null with no document.</summary>
    public Point? DocumentAt(Point window) =>
        _document is null ? null : _viewport.DocumentPoint(ToView((int)window.X, (int)window.Y), _document.Size);

    private CanvasDocument? _compositeOf;
    private PixelBuffer? _composite;

    /// <summary>
    /// The colour the document shows at a point, unpremultiplied over nothing, for the colour
    /// picker's eyedropper. The composite is drawn once and kept until the document changes.
    /// </summary>
    public (double Red, double Green, double Blue)? CompositeColour(Point document)
    {
        if (_document is null) return null;
        if (_composite is null || !ReferenceEquals(_compositeOf, _document))
        {
            _composite?.Release();
            using var backend = new SoftwareRenderBackend();
            _composite = LayerCompositor.Render(_document, backend);
            _compositeOf = _document;
        }

        int column = (int)Math.Floor(document.X), row = (int)Math.Floor(document.Y);
        if (column < 0 || row < 0 || column >= _composite.Width || row >= _composite.Height) return null;
        ReadOnlySpan<byte> pixel = _composite.Row(row).Slice(column * 4, 4);
        if (pixel[3] == 0) return null;
        double alpha = pixel[3];
        return (Math.Min(1, pixel[0] / alpha), Math.Min(1, pixel[1] / alpha), Math.Min(1, pixel[2] / alpha));
    }

    private void ReleaseComposite()
    {
        _composite?.Release();
        _composite = null;
        _compositeOf = null;
    }

    // MARK: What Levels measures and samples

    private PixelBuffer? _sourcePixels;
    private LayerTransform? _sourcePlacement;
    private Histogram? _histogram;

    /// <summary>
    /// The pixels the open command works from, measured: the layer's own when it runs over a layer,
    /// the layers under it when it is an adjustment layer — which is what an adjustment layer sees.
    /// </summary>
    public Histogram? FilterHistogram
    {
        get
        {
            PrepareSource();
            return _histogram;
        }
    }

    /// <summary>The colour under a document point in the pixels the command works from, unpremultiplied, or null off them.</summary>
    public (double Red, double Green, double Blue)? FilterSourceColour(Point document)
    {
        PrepareSource();
        if (_sourcePixels is not PixelBuffer pixels) return null;

        double x = document.X, y = document.Y;
        if (_sourcePlacement is LayerTransform placement)
        {
            Point unit = placement.UnitAt(document);
            if (unit.X is < 0 or >= 1 || unit.Y is < 0 or >= 1) return null;
            x = (placement.FlipX ? 1 - unit.X : unit.X) * pixels.Width;
            y = (placement.FlipY ? 1 - unit.Y : unit.Y) * pixels.Height;
        }

        int column = (int)Math.Floor(x), row = (int)Math.Floor(y);
        if (column < 0 || row < 0 || column >= pixels.Width || row >= pixels.Height) return null;

        ReadOnlySpan<byte> pixel = pixels.Row(row).Slice(column * 4, 4);
        if (pixel[3] == 0) return null;
        double alpha = pixel[3];
        return (Math.Min(1, pixel[0] / alpha), Math.Min(1, pixel[1] / alpha), Math.Min(1, pixel[2] / alpha));
    }

    private void PrepareSource()
    {
        if (_sourcePixels is not null || _document is null) return;

        if (_preview is FilterPreview preview)
        {
            _sourcePixels = preview.Layer.Image!.Retain();
            _sourcePlacement = preview.Layer.Transform;
        }
        else if (_adjusting is Guid id)
        {
            _sourcePixels = Below(_document, id);
            _sourcePlacement = null;
        }
        else
        {
            return;
        }

        // Halved to about a megapixel first: the histogram's shape does not need every pixel of a
        // hundred-megapixel layer, and the sheet waits for it.
        PixelBuffer reduced = _sourcePixels.Retain();
        while (Math.Max(reduced.Width, reduced.Height) > 1024)
        {
            PixelBuffer next = DownsamplePyramid.Halve(reduced);
            reduced.Release();
            reduced = next;
        }
        _histogram = Histogram.Measure(reduced);
        reduced.Release();
    }

    /// <summary>The document drawn with only the layers under <paramref name="id"/>.</summary>
    private static PixelBuffer Below(CanvasDocument document, Guid id)
    {
        int index = document.IndexOf(id);
        List<ImageLayer> below = [.. document.Layers.Take(Math.Max(0, index))];
        var kept = below.Select(layer => layer.Id).ToHashSet();
        below = [.. below.Select(layer => layer.ParentId is Guid parent && !kept.Contains(parent) ? layer with { ParentId = null } : layer)];

        using var backend = new SoftwareRenderBackend();
        return LayerCompositor.Render(document with { Layers = below.ToEquatableList() }, backend);
    }

    private void ReleaseSource()
    {
        _sourcePixels?.Release();
        _sourcePixels = null;
        _sourcePlacement = null;
        _histogram = null;
    }

    /// <summary>The frame's stand-in for a layer being filtered, when one is.</summary>
    private LiveEdit? Previewing(int width, int height) =>
        _preview is FilterPreview preview && _previewOn && _document is not null
            ? preview.Frame(Projection(_document), width, height)
            : null;

    private static FilterKind KindFor(FilterCommand command) => command switch
    {
        FilterCommand.GaussianBlur => FilterKind.GaussianBlur,
        FilterCommand.MotionBlur => FilterKind.MotionBlur,
        FilterCommand.AddNoise => FilterKind.AddNoise,
        FilterCommand.LensCorrection => FilterKind.LensCorrection,
        _ => FilterKind.Adjustment,
    };

    /// <summary>An adjustment that changes nothing yet, as upstream opens each one.</summary>
    internal static LayerAdjustment StartingAdjustment(FilterCommand command, uint seed) => command switch
    {
        FilterCommand.Levels => new LayerAdjustment(AdjustmentKind.Levels),
        FilterCommand.Curves => new LayerAdjustment(AdjustmentKind.Curves),
        FilterCommand.HueSaturation => new LayerAdjustment(AdjustmentKind.Hsv)
        {
            HsvSettings = new HueSaturationSettings(),
        },
        FilterCommand.Exposure => new LayerAdjustment(AdjustmentKind.Exposure) { ExposureSettings = new ExposureSettings() },
        FilterCommand.GradientMap => new LayerAdjustment(AdjustmentKind.GradientMap)
        {
            GradientMapSettings = new GradientMapSettings(),
        },
        _ => new LayerAdjustment(AdjustmentKind.Grain) { GrainSettings = new GrainSettings { Seed = seed } },
    };
}
