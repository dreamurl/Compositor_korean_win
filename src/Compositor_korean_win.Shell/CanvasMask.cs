using Compositor_korean_win.Core;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The layer mask as something to paint — upstream's <c>isMaskSelected</c> — and the blur tool's
/// Smudge and Liquify.
/// </summary>
/// <remarks>
/// <para>
/// A click on a mask's thumbnail makes the mask what the tools work on, as in Photoshop: the brush
/// and the gradient lay white or black, the eraser lays the other one, Fill, Clear and Invert act
/// on the mask, and the colour swatches show the mask's black and white rather than the palette.
/// The clone stamp, spot healing, Smudge and Liquify rework a layer's pixels and do nothing to a
/// mask, which is upstream's rule.
/// </para>
/// <para>
/// What is targeted is remembered as a layer id and checked against the active layer, so choosing
/// another layer, deleting the mask or undoing its creation all fall back to the layer's pixels
/// without anything having to remember to reset it.
/// </para>
/// </remarks>
internal sealed partial class CanvasView
{
    private Guid? _maskOf;

    /// <summary>Whether the tools work on the active layer's mask rather than its pixels.</summary>
    public bool EditingMask => _maskOf is Guid id && _chosen.Count == 1 && Primary == id && ActiveLayer?.Mask is not null;

    /// <summary>
    /// Whether the Move tool and the transform fields move the mask on its own: it is the target, and
    /// it has been unlinked from its layer — upstream's <c>transformTargetsMask</c>.
    /// </summary>
    public bool TransformsMask => EditingMask && ActiveLayer!.Mask!.IsLinked == false;

    public bool CanToggleMaskLink => CanEdit && ActiveLayer?.Mask is not null;

    public bool MaskLinked => ActiveLayer?.Mask?.IsLinked ?? true;

    /// <summary>Links or unlinks a layer's mask, the active layer's when no id is given.</summary>
    public void ToggleMaskLink(Guid? id = null)
    {
        if ((id ?? Primary) is not Guid target || _document?.Layer(target) is not ImageLayer layer) return;
        Edit(layer.Mask?.IsLinked == false ? TextKey.CommandLinkMask : TextKey.CommandUnlinkMask, document =>
            document.Layer(target) is ImageLayer current && MaskEditing.ToggleLink(current) is ImageLayer toggled
                ? (document.Replacing(toggled), null)
                : null);
    }

    /// <summary>
    /// A mask dropped on another layer's row: a copy of it there, sitting where it sat, the target
    /// layer's own mask replaced. The copy becomes the target, as upstream has it.
    /// </summary>
    public void CopyMask(Guid from, Guid to)
    {
        if (_document?.Layer(from) is not ImageLayer source || _document.Layer(to) is not ImageLayer target) return;
        if (MaskEditing.CopyTo(source, target) is null) return;

        Edit(target.Mask is null ? TextKey.HistoryCopyMask : TextKey.HistoryReplaceMask, document =>
            document.Layer(from) is ImageLayer s && document.Layer(to) is ImageLayer t && MaskEditing.CopyTo(s, t) is ImageLayer copied
                ? (document.Replacing(copied), to)
                : null);
        _maskOf = to;
    }

    public bool CanCopyMaskTo(Guid from, Guid to) =>
        CanEdit && from != to && _document?.Layer(from)?.Mask is not null && _document.Layer(to) is { IsGroup: false };

    /// <summary>
    /// The mask's foreground is white rather than black. Upstream's <c>maskPaintWhite</c>; black by
    /// default, as Photoshop's reset gives.
    /// </summary>
    public bool MaskPaintsWhite { get; set; }

    /// <summary>What the blur tool does: soften, smudge or push.</summary>
    public BlurToolMode BlurMode { get; set; } = BlurToolMode.Blur;

    /// <summary>Whether the clone stamp copies from everything showing rather than the active layer alone.</summary>
    public bool CloneSampleAll { get; set; }

    /// <summary>A click on a layer's mask thumbnail: that layer, alone, with its mask as the target.</summary>
    public void ClickMask(Guid id)
    {
        if (!CanEdit || _document?.Layer(id)?.Mask is null) return;
        Choose(id);
        _maskOf = id;
        NeedsRedraw = true;
    }

    /// <summary>The colour the foreground swatch shows: the palette's, or the mask's white or black.</summary>
    public Rgba ShownForeground => EditingMask ? MaskEditing.Grey(MaskPaintsWhite) : ForegroundColor;

    public Rgba ShownBackground => EditingMask ? MaskEditing.Grey(!MaskPaintsWhite) : BackgroundColor;

    /// <summary>Whether the mask can take a pixel edit: it is showing, and nothing is half done.</summary>
    public bool CanEditMask => CanEdit && EditingMask && ActiveLayer!.Mask!.IsEnabled;

    // MARK: Selecting from a mask

    public bool CanSelectMask => CanEdit && ActiveLayer?.Mask is not null;

    /// <summary>The mask's black areas — what it hides — as the selection.</summary>
    public void SelectMask()
    {
        if (ActiveLayer is not ImageLayer layer) return;
        _selection = MaskEditing.Selection(layer);
        NeedsRedraw = true;
    }

    // MARK: Painting a mask

    private bool _strokeOnMask;
    private ImageLayer? _maskBefore;
    private PixelBuffer? _maskWorking;
    private PixelBuffer? _maskShown;
    private LayerTransform _strokePlacement = new(Point.Zero, new Size(1, 1));

    /// <summary>
    /// Starts a brush, eraser or blur stroke on the active layer's mask. The mask the canvas shows
    /// is a copy the stroke's tiles are laid into as they change; the mask it started from stays
    /// as it was, for the stroke to paint from.
    /// </summary>
    private void BeginMaskStroke(ImageLayer layer, Point document, bool shift)
    {
        if (_document is null) return;
        if (_tool is not (CanvasTool.Brush or CanvasTool.Eraser or CanvasTool.Blur)) return;
        if (_tool == CanvasTool.Blur && BlurMode != BlurToolMode.Blur) return;
        if (layer.Mask is not { IsEnabled: true }) return;
        if (MaskEditing.Canvas(layer) is not (PixelBuffer working, LayerTransform placement)) return;

        _paintGrid = new PixelRect(0, 0, working.Width, working.Height);
        _strokePlacement = placement;
        Point start = LayerGeometry.ToPixels(placement, document, working.Width, working.Height);

        BrushSettings settings = _tool == CanvasTool.Blur
            ? Brush with { Mode = BrushMode.Blur }
            : Brush with { Mode = BrushMode.Paint, Color = MaskEditing.Grey(MaskPaintsWhite ^ (_tool == CanvasTool.Eraser)) };

        _history.Begin(Name(_tool), _document, layer.Id);
        _painting = layer.Id;
        _strokeOnMask = true;
        _maskBefore = layer;
        _maskWorking = working;
        _maskShown = PixelRegion.Copy(working, new PixelRect(0, 0, working.Width, working.Height));
        _document = _document.Replacing(MaskEditing.WithMask(layer, _maskShown));

        _stroke = new BrushStroke(working, working.Width, working.Height, settings, Restricted(placement));
        _strokeStart = document;
        _strokeEnd = document;
        ContinueLastStroke(_stroke, shift, layer.Id, onMask: true, placement);
        _stroke.Append(start);
        ShowMaskStroke();
        NeedsRedraw = true;
    }

    /// <summary>Copies what the stroke changed since last time into the mask the canvas shows.</summary>
    private void ShowMaskStroke()
    {
        if (_stroke is not BrushStroke stroke || _maskShown is not PixelBuffer shown) return;
        PixelRect touched = stroke.TakeTouched();
        if (touched.IsEmpty) return;

        foreach (RasterPatch patch in stroke.Patches)
        {
            PixelRect overlap = patch.Region.Intersect(touched);
            if (overlap.IsEmpty) continue;
            for (int y = overlap.Y; y < overlap.Bottom; y++)
            {
                patch.Pixels.Row(y - patch.Region.Y).Slice((overlap.X - patch.Region.X) * 4, overlap.Width * 4)
                     .CopyTo(shown.Row(y).Slice(overlap.X * 4, overlap.Width * 4));
            }
        }
    }

    /// <summary>Puts the finished mask stroke into the layer as one history step.</summary>
    private void EndMaskStroke(BrushStroke stroke)
    {
        try
        {
            if (_document is null || _maskBefore is not ImageLayer before) return;

            if (stroke.IsEmpty)
            {
                _document = _document.Replacing(before);
                _history.End(_document, _painting);
                return;
            }

            _document = _document.Replacing(MaskEditing.WithMask(before, stroke.Commit()));
            _history.End(_document, _painting);
            NeedsRedraw = true;
        }
        finally
        {
            _strokeOnMask = false;
            _maskBefore = null;
            _maskShown?.Release();
            _maskShown = null;
            _maskWorking?.Release();
            _maskWorking = null;
        }
    }

    /// <summary>A gradient dragged out on the mask, from its foreground grey to its background one.</summary>
    private void MaskGradient(ImageLayer layer, Point corner, Point end)
    {
        if (_document is null || layer.Mask is not { IsEnabled: true }) return;
        if (MaskEditing.Canvas(layer) is not (PixelBuffer working, LayerTransform placement)) return;

        PixelBuffer drawn;
        try
        {
            _paintGrid = new PixelRect(0, 0, working.Width, working.Height);
            Point from = LayerGeometry.ToPixels(placement, corner, working.Width, working.Height);
            Point to = LayerGeometry.ToPixels(placement, end, working.Width, working.Height);
            GradientSettings greys = Gradient with
            {
                From = MaskEditing.Grey(MaskPaintsWhite),
                To = MaskEditing.Grey(!MaskPaintsWhite),
            };
            drawn = GradientTool.Draw(working, working.Width, working.Height, from, to, greys, Restricted(placement));
        }
        finally
        {
            working.Release();
        }

        _history.Begin(Name(_tool), _document, layer.Id);
        _document = _document.Replacing(MaskEditing.WithMask(layer, drawn));
        _history.End(_document, layer.Id);
    }

    // MARK: Smudge and Liquify

    private WarpStroke? _warp;

    private void BeginWarp(ImageLayer layer, Point document)
    {
        if (_document is null || layer.Image is not PixelBuffer image) return;

        _paintGrid = new PixelRect(0, 0, image.Width, image.Height);
        _strokePlacement = layer.Transform;

        _history.Begin(BlurMode == BlurToolMode.Smudge ? TextKey.ToolSmudge : TextKey.ToolLiquify, _document, layer.Id);
        _painting = layer.Id;
        _warp = new WarpStroke(image, BlurMode, Brush);
        _strokeStart = document;
        _warp.Append(LayerGeometry.ToPixels(layer.Transform, document, image.Width, image.Height));
        NeedsRedraw = true;
    }

    private void EndWarp()
    {
        WarpStroke? warp = _warp;
        _warp = null;
        if (warp is null) return;

        try
        {
            if (_document is null || _document.Layer(_painting) is not ImageLayer layer) return;

            if (!warp.IsEmpty)
                _document = _document.Replacing(layer with { Image = warp.Commit(Restricted(_strokePlacement)), Shape = null });
            _history.End(_document, _painting);
            NeedsRedraw = true;
        }
        finally
        {
            warp.Dispose();
        }
    }

    /// <summary>The layer as the warp has left it so far, drawn in place of its own pixels.</summary>
    /// <remarks>
    /// Wrapped as a raster with no patches rather than a plain buffer: the backends keep a plain
    /// buffer's reductions from one frame to the next, and this one changes under them.
    /// </remarks>
    private LiveEdit? Warped() =>
        _warp is WarpStroke warp ? new LiveEdit(_painting, new LayerRaster(warp.Pixels, [])) : null;
}
