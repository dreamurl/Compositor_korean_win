using Compositor_korean_win.Core;
using Point = Compositor_korean_win.Core.Point;
using Size = Compositor_korean_win.Core.Size;

namespace Compositor_korean_win.Shell;

/// <summary>The Select menu, the pixel edits, the clipboard's side of Edit, and the layer mask.</summary>
internal sealed partial class CanvasView
{
    /// <summary>The colour Fill with Background Color lays down. Foreground is the brush's.</summary>
    public Rgba BackgroundColor { get; set; } = new(255, 255, 255);

    /// <summary>How far Expand and Contract move the selection's edge, in pixels.</summary>
    public int SelectionStep { get; set; } = 2;

    // MARK: Select

    public bool CanInverse => CanEdit && HasSelection;

    public void Inverse()
    {
        if (_document is null) return;
        _selection = SelectionCommands.Inverse(_selection, _document.Width, _document.Height);
        NeedsRedraw = true;
    }

    public bool CanSelectLayerPixels => CanEdit && ActiveLayer is { Image: not null, IsGroup: false };

    public void SelectLayerPixels()
    {
        if (_document is null || ActiveLayer is not ImageLayer layer) return;
        _selection = SelectionCommands.FromLayer(_document, layer);
        NeedsRedraw = true;
    }

    public void GrowSelection(bool outwards)
    {
        if (_document is null || _selection is not DocumentSelection selection) return;
        _selection = outwards
            ? SelectionCommands.Expand(selection, _document.Width, _document.Height, SelectionStep)
            : SelectionCommands.Contract(selection, _document.Width, _document.Height, SelectionStep);
        NeedsRedraw = true;
    }

    // MARK: Pixels

    /// <summary>Whether the active layer takes pixel edits: not a folder, not an adjustment.</summary>
    public bool CanEditPixels => CanEdit && ActiveLayer is { IsGroup: false, Adjustment: null };

    public bool CanEditExistingPixels => CanEditPixels && ActiveLayer!.Image is not null;

    public bool CanEditSelectedPixels => CanEditExistingPixels && HasSelection;

    private void PixelEdit(HistoryName name, Func<CanvasDocument, ImageLayer, ImageLayer?> change)
    {
        if (Primary is not Guid id) return;
        Edit(name, document =>
            document.Layer(id) is ImageLayer layer && change(document, layer) is ImageLayer edited
                ? (document.Replacing(edited), null)
                : null);
    }

    public void Invert() => PixelEdit(TextKey.CommandInvert, (_, layer) => PixelCommands.Invert(layer, _selection));

    public void Fill(bool foreground) =>
        PixelEdit(foreground ? TextKey.CommandFillForeground : TextKey.CommandFillBackground,
                  (document, layer) => PixelCommands.Fill(document, layer, foreground ? Brush.Color : BackgroundColor, _selection));

    public void Clear()
    {
        if (_selection is not DocumentSelection selection) return;
        PixelEdit(TextKey.CommandClear, (_, layer) => PixelCommands.Clear(layer, selection));
    }

    public void ContentAwareFill()
    {
        if (_selection is not DocumentSelection selection) return;
        PixelEdit(TextKey.CommandContentAwareFill, (_, layer) => PixelCommands.ContentAwareFill(layer, selection));
    }

    /// <summary>Ctrl+J: the selection copied to a new layer, or the whole layer when nothing is selected.</summary>
    public void LayerViaCopy()
    {
        if (_selection is null)
        {
            DuplicateLayer();
            return;
        }

        if (Primary is not Guid id) return;
        Edit(TextKey.CommandLayerViaCopy, document =>
            PixelCommands.LayerVia(document, id, _selection, cut: false) is var (next, copy) ? (next, copy) : null);
    }

    public void LayerViaCut()
    {
        if (Primary is not Guid id || _selection is null) return;
        Edit(TextKey.CommandLayerViaCut, document =>
            PixelCommands.LayerVia(document, id, _selection, cut: true) is var (next, copy) ? (next, copy) : null);
    }

    // MARK: Clipboard

    /// <summary>
    /// The selected pixels of the active layer — or, merged, of everything showing — for the
    /// clipboard. The caller releases the pixels.
    /// </summary>
    public (PixelBuffer Pixels, LayerTransform Placement)? CopyPixels(bool merged)
    {
        if (_document is not CanvasDocument document) return null;
        if (!merged) return ActiveLayer is ImageLayer layer ? PixelCommands.Copy(layer, _selection) : null;

        using var backend = new SoftwareRenderBackend();
        PixelBuffer everything = LayerCompositor.Render(document, backend);
        try
        {
            var flat = new ImageLayer
            {
                Id = Guid.NewGuid(),
                Name = string.Empty,
                Image = everything,
                Transform = new LayerTransform(Point.Zero, document.Size),
            };
            return PixelCommands.Copy(flat, _selection);
        }
        finally
        {
            everything.Release();
        }
    }

    /// <summary>Clears what Cut just copied, as the Cut step.</summary>
    public void ClearAfterCut()
    {
        if (_selection is DocumentSelection selection)
            PixelEdit(TextKey.CommandCut, (_, layer) => PixelCommands.Clear(layer, selection));
    }

    /// <summary>
    /// Pixels as a new layer above the active one: where they were copied from when that is known,
    /// else in the middle of the canvas. The document takes the pixels.
    /// </summary>
    public void Paste(PixelBuffer pixels, LayerTransform? placement)
    {
        if (_document is not CanvasDocument document || !CanEdit)
        {
            pixels.Release();
            return;
        }

        LayerTransform where = placement ?? new LayerTransform(
            new Point(Math.Round((document.Width - pixels.Width) / 2.0), Math.Round((document.Height - pixels.Height) / 2.0)),
            new Size(pixels.Width, pixels.Height));

        Guid? active = Primary;
        Edit(TextKey.CommandPaste, current =>
        {
            ImageLayer? above = active is Guid id ? current.Layer(id) : null;
            var layer = new ImageLayer
            {
                Id = Guid.NewGuid(),
                Name = LayerCommands.NextName(current, TextKey.LayerNameNumbered),
                Image = pixels,
                Transform = where,
                ParentId = above is { IsGroup: false } ? above.ParentId : above?.Id,
            };

            var layers = current.Layers.ToList();
            layers.Insert(above is null ? layers.Count : current.IndexOf(above.Id) + 1, layer);
            return (current with { Layers = layers.ToEquatableList() }, layer.Id);
        });

        // A paste replaces the selection, as in Photoshop.
        _selection = null;
    }

    // MARK: Layer mask

    public bool CanAddMask => CanEdit && ActiveLayer is { Mask: null, Adjustment: null };

    public bool CanChangeMask => CanEdit && ActiveLayer?.Mask is not null;

    public bool MaskEnabled => ActiveLayer?.Mask?.IsEnabled ?? true;

    public void AddMask() =>
        PixelEdit(TextKey.CommandAddLayerMask, (document, layer) => PixelCommands.AddMask(document, layer, _selection));

    public void DeleteMask() => PixelEdit(TextKey.CommandDeleteLayerMask, (_, layer) => PixelCommands.DeleteMask(layer));

    public void ToggleMask() =>
        PixelEdit(MaskEnabled ? TextKey.CommandDisableLayerMask : TextKey.CommandEnableLayerMask,
                  (_, layer) => PixelCommands.ToggleMask(layer));
}
