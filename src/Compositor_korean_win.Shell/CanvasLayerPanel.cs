using Compositor_korean_win.Core;

namespace Compositor_korean_win.Shell;

/// <summary>
/// What the Layers panel's rows do beyond a click — upstream's <c>NativeLayerList</c>: dragging
/// layers to a new place or into a folder (Alt: copies), loading a thumbnail as a selection, and
/// the mask thumbnail's Shift-click.
/// </summary>
internal sealed partial class CanvasView
{
    /// <summary>
    /// Dragged rows dropped against <paramref name="target"/>: the chosen layers when the drag
    /// began on one of them, else the row alone; copies of them with <paramref name="copy"/>.
    /// </summary>
    public void PlaceLayers(Guid dragged, Guid target, LayerDrop drop, bool copy)
    {
        IReadOnlyCollection<Guid> ids = _chosen.Contains(dragged) ? [.. _chosen] : [dragged];

        if (copy)
        {
            Edit(TextKey.CommandDuplicateLayer, document =>
                LayerCommands.CopyTo(document, ids, target, drop) is var (next, copies) && copies.Count > 0
                    ? (next, copies[^1])
                    : null);
            return;
        }

        Edit(TextKey.HistoryArrangeLayers, document =>
            LayerCommands.Place(document, ids, target, drop) is CanvasDocument next ? (next, null) : null);
    }

    /// <summary>
    /// Ctrl-click on a thumbnail: the layer's pixels (at least half opaque) as the selection — Shift
    /// adds to what is selected, Alt takes away, as in Photoshop.
    /// </summary>
    public void LoadLayerSelection(Guid id, bool add, bool subtract)
    {
        if (!CanEdit || _document?.Layer(id) is not { Image: not null, IsGroup: false } layer) return;
        Combine(SelectionCommands.FromLayer(_document, layer), add, subtract);
    }

    /// <summary>Ctrl-click on a mask thumbnail: its black areas, the same way.</summary>
    public void LoadMaskSelection(Guid id, bool add, bool subtract)
    {
        if (!CanEdit || _document?.Layer(id) is not { Mask: not null } layer) return;
        Combine(MaskEditing.Selection(layer), add, subtract);
    }

    private void Combine(DocumentSelection? loaded, bool add, bool subtract)
    {
        _selection = (add, subtract, _selection, loaded) switch
        {
            (true, _, DocumentSelection current, DocumentSelection next) => current.Adding(next),
            (true, _, DocumentSelection current, null) => current,
            (_, true, DocumentSelection current, DocumentSelection next) => current.Subtracting(next),
            (_, true, DocumentSelection current, null) => current,
            (_, true, null, _) => null,
            _ => loaded,
        };
        NeedsRedraw = true;
    }

    /// <summary>Shift-click on a mask thumbnail: that layer's mask on or off.</summary>
    public void ToggleMaskOf(Guid id)
    {
        if (_document?.Layer(id) is not { Mask: LayerMask mask }) return;
        Edit(mask.IsEnabled ? TextKey.CommandDisableLayerMask : TextKey.CommandEnableLayerMask, document =>
            document.Layer(id) is ImageLayer layer && PixelCommands.ToggleMask(layer) is ImageLayer toggled
                ? (document.Replacing(toggled), null)
                : null);
    }

    /// <summary>A mask that hides everything, or with a selection hides the selection — Photoshop's "Hide All".</summary>
    public void AddHideMask()
    {
        PixelEdit(TextKey.CommandAddHideMask, (document, layer) => PixelCommands.AddMask(document, layer, _selection, revealing: false));
        if (Primary is Guid id && ActiveLayer?.Mask is not null) _maskOf = id;
    }
}
