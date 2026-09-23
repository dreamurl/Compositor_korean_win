using Compositor_korean_win.Core;
using Point = Compositor_korean_win.Core.Point;
using Rect = Compositor_korean_win.Core.Rect;

namespace Compositor_korean_win.Shell;

/// <summary>What the menu commands ask of the canvas.</summary>
/// <remarks>
/// Each answers for itself whether it can run, so the menu can grey it out — and each refuses while
/// a filter or an adjustment is open, which owns the document until Enter or Escape closes it.
/// Zooming is the exception: looking closer at a preview is the point of having one.
/// </remarks>
internal sealed partial class CanvasView
{
    /// <summary>The project the document was opened from or last saved to, if any.</summary>
    public string? FilePath { get; private set; }

    /// <summary>What to call the document: its file's name, the image it came from, or "Untitled".</summary>
    public string Title => FilePath is string path
        ? System.IO.Path.GetFileNameWithoutExtension(path)
        : _name ?? Localizer.Text(TextKey.DocumentUntitled);

    public bool HasDocument => _document is not null;

    /// <summary>Images brought in as layers, one step for them all. The document takes the pixels.</summary>
    public void AddImages(IReadOnlyList<(PixelBuffer Pixels, string Name)> images)
    {
        if (images.Count == 0) return;
        Edit(TextKey.CommandImportImages, document =>
        {
            CanvasDocument next = document;
            Guid? chosen = Primary;
            foreach ((PixelBuffer pixels, string name) in images)
            {
                (next, Guid added) = DocumentCommands.AddImage(next, pixels, name, chosen);
                chosen = added;
            }
            return (next, chosen);
        });
    }

    public bool IsModified => _history.IsModified;

    /// <summary>The layer a save records as active.</summary>
    public Guid? ActiveLayerId => Primary;

    /// <summary>Records that the document now matches the project at <paramref name="path"/>.</summary>
    public void MarkSaved(string path)
    {
        FilePath = path;
        _history.MarkSaved();
    }

    public bool CanEdit => _document is not null && !IsFiltering && _drag is null && _stroke is null && _warp is null;

    public bool CanUndo => CanEdit && _history.CanUndo;
    public bool CanRedo => CanEdit && _history.CanRedo;
    public string UndoName => _history.UndoName;
    public string RedoName => _history.RedoName;

    public void Undo() => Restore(_history.Undo());

    public void Redo() => Restore(_history.Redo());

    private void Restore(HistorySnapshot? snapshot)
    {
        if (snapshot is null) return;
        _document = snapshot.Document;
        _chosen.Clear();
        if (snapshot.ActiveLayerId is Guid active) _chosen.Add(active);
        NeedsRedraw = true;
    }

    public bool HasSelection => _selection is not null;

    public void SelectAll()
    {
        if (_document is null) return;
        _selection = DocumentSelection.Rectangle(new Rect(0, 0, _document.Width, _document.Height));
        NeedsRedraw = true;
    }

    public void Deselect()
    {
        _selection = null;
        NeedsRedraw = true;
    }

    public bool CanDuplicateLayer => CanEdit && _chosen.Count > 0;

    public void DuplicateLayer()
    {
        Duplicate();
        NeedsRedraw = true;
    }

    public void FitOnScreen()
    {
        if (_document is null) return;
        _viewport = _viewport.Fit(_document.Size);
        NeedsRedraw = true;
    }

    public void ActualPixels()
    {
        if (_document is null) return;
        _viewport = _viewport.ZoomedTo(1, _viewport.Center, _document.Size);
        NeedsRedraw = true;
    }

    /// <summary>A quarter closer or further, about the middle of the view, as upstream steps.</summary>
    public void Zoom(bool closer)
    {
        if (_document is null) return;
        double zoom = closer ? _viewport.Zoom * 1.25 : _viewport.Zoom / 1.25;
        _viewport = _viewport.ZoomedTo(zoom, _viewport.Center, _document.Size);
        NeedsRedraw = true;
    }

    /// <summary>Chooses the topmost layer with pixels, as opening a document does.</summary>
    internal void ChooseTopImageLayer()
    {
        _chosen.Clear();
        if (_document?.Layers.LastOrDefault(layer => !layer.IsGroup && layer.Image is not null && layer.Adjustment is null)
            is ImageLayer top)
        {
            _chosen.Add(top.Id);
        }
        NeedsRedraw = true;
    }

    /// <summary>Makes one layer the only chosen one — the self-test's click on a layer.</summary>
    internal void Choose(Guid id)
    {
        _chosen.Clear();
        _chosen.Add(id);
        NeedsRedraw = true;
    }

    // MARK: The Layer menu

    public ImageLayer? ActiveLayer => Primary is Guid id ? _document?.Layer(id) : null;

    /// <summary>
    /// Runs one edit as one history step, making <c>Chosen</c> the active layer when it names one.
    /// </summary>
    private void Edit(HistoryName name, Func<CanvasDocument, (CanvasDocument Document, Guid? Chosen)?> change)
    {
        if (_document is null || !CanEdit) return;
        if (change(_document) is not (CanvasDocument next, var chosen)) return;

        _history.Begin(name, _document, Primary);
        _document = next;
        if (chosen is Guid id)
        {
            _chosen.Clear();
            _chosen.Add(id);
        }
        _chosen.RemoveWhere(each => next.Layer(each) is null);
        _history.End(_document, Primary);
        NeedsRedraw = true;
    }

    public void AddLayer() =>
        Edit(TextKey.CommandNewLayer, document => LayerCommands.AddBlankLayer(document, Primary) is var (next, id)
            ? (next, id) : null);

    public bool CanDeleteLayers => CanEdit && _chosen.Count > 0;

    public void DeleteLayers()
    {
        // With the mask as the target, Delete takes the mask and leaves the layer, as upstream does.
        if (EditingMask)
        {
            DeleteMask();
            return;
        }

        if (Primary is not Guid active) return;
        Edit(TextKey.CommandDeleteLayer, document =>
            LayerCommands.Delete(document, _chosen) is CanvasDocument next
                ? (next, LayerCommands.Survivor(document, next, active))
                : null);
    }

    public bool CanMoveLayer(int offset) =>
        CanEdit && Primary is Guid id && LayerCommands.CanMove(_document!, id, offset);

    public void MoveLayer(int offset)
    {
        if (Primary is not Guid id) return;
        Edit(offset > 0 ? TextKey.CommandMoveLayerUp : TextKey.CommandMoveLayerDown,
             document => LayerCommands.Move(document, id, offset) is CanvasDocument next ? (next, null) : null);
    }

    public bool CanGroupLayers => CanEdit && _chosen.Count > 0;

    public void GroupLayers() =>
        Edit(TextKey.CommandGroupLayers, document =>
            LayerCommands.Group(document, [.. _chosen]) is var (next, folder) ? (next, folder) : null);

    public bool CanMoveOutOfGroup => CanEdit && ActiveLayer is { ParentId: not null };

    public void MoveOutOfGroup()
    {
        if (Primary is not Guid id) return;
        Edit(TextKey.CommandMoveOutOfGroup,
             document => LayerCommands.MoveOutOfFolder(document, id) is CanvasDocument next ? (next, id) : null);
    }

    public bool CanToggleVisibility => CanEdit && ActiveLayer is not null;

    public bool ActiveLayerVisible => ActiveLayer?.IsVisible ?? true;

    public void ToggleVisibility()
    {
        if (Primary is not Guid id) return;
        Edit(ActiveLayerVisible ? TextKey.CommandHideLayer : TextKey.CommandShowLayer,
             document => (LayerCommands.ToggleVisibility(document, id), null));
    }

    public bool ActiveLayerClipped => ActiveLayer?.MaskSourceId is not null;

    public bool CanToggleClipping =>
        CanEdit && Primary is Guid id && LayerCommands.ToggleClipping(_document!, id) is not null;

    public void ToggleClipping()
    {
        if (Primary is not Guid id) return;
        Edit(ActiveLayerClipped ? TextKey.CommandReleaseClippingMask : TextKey.CommandCreateClippingMask,
             document => LayerCommands.ToggleClipping(document, id) is CanvasDocument next ? (next, null) : null);
    }

    /// <summary>What Merge would do now; the menu names the item after it.</summary>
    public LayerCommands.MergePlan? MergePlan =>
        CanEdit && _document is CanvasDocument document ? LayerCommands.PlanMerge(document, _chosen, Primary) : null;

    public void Merge()
    {
        if (MergePlan is not LayerCommands.MergePlan plan) return;
        Edit(plan.Action, document => LayerCommands.Merge(document, plan) is var (next, merged) ? (next, merged) : null);
    }

    public bool CanFlipLayers => CanEdit && _chosen.Count > 0 && _document!.Layers.Any(layer => _chosen.Contains(layer.Id) && !layer.IsGroup);

    public void FlipLayers(bool horizontally) =>
        Edit(horizontally ? TextKey.CommandFlipLayerHorizontal : TextKey.CommandFlipLayerVertical,
             document => LayerCommands.Flip(document, [.. _chosen], horizontally) is CanvasDocument next ? (next, null) : null);

    /// <summary>The canvas mirrored, with the selection mirrored along with it.</summary>
    public void FlipCanvas(bool horizontally)
    {
        if (_document is not CanvasDocument before) return;
        Edit(horizontally ? TextKey.CommandFlipCanvasHorizontal : TextKey.CommandFlipCanvasVertical,
             document => (LayerCommands.FlipCanvas(document, horizontally), null));

        if (_selection is DocumentSelection selection)
        {
            _selection = selection.Transformed(point => horizontally
                ? new Point(before.Width - point.X, point.Y)
                : new Point(point.X, before.Height - point.Y));
        }
    }

    /// <summary>Image › Canvas Size: a new canvas size, the content kept to the anchor, the new space filled or not.</summary>
    public void ResizeCanvas(int width, int height, int anchor, Rgba? extension)
    {
        if (_document is not CanvasDocument before) return;
        Point offset = DocumentCommands.AnchorOffset(before.Width, before.Height, width, height, anchor);
        Edit(TextKey.SheetCanvasSize, document =>
            DocumentCommands.ResizeCanvas(document, width, height, anchor, extension) is CanvasDocument next ? (next, null) : null);
        if (ReferenceEquals(_document, before)) return;

        _selection = _selection?.Transformed(point => new Point(point.X + offset.X, point.Y + offset.Y));
        FitOnScreen();
    }

    /// <summary>Image › Image Size with Resample on: every layer resampled to the new size.</summary>
    public void ResizeImage(int width, int height, double resolution)
    {
        if (_document is not CanvasDocument before) return;
        Edit(TextKey.SheetImageSize, document =>
            DocumentCommands.ResizeImage(document, width, height, resolution) is CanvasDocument next ? (next, null) : null);
        if (ReferenceEquals(_document, before)) return;

        double sx = (double)width / before.Width, sy = (double)height / before.Height;
        _selection = _selection?.Transformed(point => new Point(point.X * sx, point.Y * sy));
        FitOnScreen();
    }

    /// <summary>Image › Image Size with Resample off: the resolution alone.</summary>
    public void SetResolution(double resolution) =>
        Edit(TextKey.SheetImageSize, document =>
            document.Resolution == resolution ? null : (document with { Resolution = resolution }, null));

    /// <summary>
    /// The chosen layer placed by numbers — the options bar's X, Y, W, H, scale and angle — as one
    /// history step, its mask moving with it.
    /// </summary>
    public void ChangeTransform(Func<LayerTransform, LayerTransform> change) =>
        Edit(TextKey.HistoryTransformLayer, document =>
        {
            if (ActiveLayer is not { IsGroup: false } layer) return null;
            LayerTransform next = change(layer.Transform);
            if (next == layer.Transform || !next.IsValid) return null;
            return (document.Replacing(layer with
            {
                Transform = next,
                Mask = layer.Mask is { Placement: LayerTransform placement } mask
                    ? mask with { Placement = placement.Following(layer.Transform, next) }
                    : layer.Mask,
            }), layer.Id);
        });
}
