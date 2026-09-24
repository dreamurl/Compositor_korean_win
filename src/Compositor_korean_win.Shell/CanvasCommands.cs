using Compositor_korean_win.Core;
using Point = Compositor_korean_win.Core.Point;
using Rect = Compositor_korean_win.Core.Rect;
using Size = Compositor_korean_win.Core.Size;

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
        bool useFirstImageAsCanvas = _document is CanvasDocument current && IsPristineBlankCanvas(current);
        Edit(TextKey.CommandImportImages, document =>
        {
            CanvasDocument next = document;
            Guid? chosen = Primary;
            for (int index = 0; index < images.Count; index++)
            {
                (PixelBuffer pixels, string name) = images[index];
                (CanvasDocument updated, Guid added) = index == 0 && useFirstImageAsCanvas
                    ? DocumentCommands.UseImageAsCanvas(next, pixels, name)
                    : AddImage(next, pixels, name, chosen);
                next = updated;
                chosen = added;
            }
            return (next, chosen);
        });
        if (useFirstImageAsCanvas && _document is not null) _viewport = _viewport.Fit(_document.Size);

        (CanvasDocument Document, Guid Layer) AddImage(CanvasDocument document, PixelBuffer pixels,
                                                       string name, Guid? active)
        {
            if (_isPhotoshopDocument)
                return DocumentCommands.AddImage(document, pixels, name, active, maximumCanvasFraction: 0.9);
            return pixels.Width > document.Width || pixels.Height > document.Height
                ? DocumentCommands.AddImage(document, pixels, name, active, maximumCanvasFraction: 1)
                : DocumentCommands.AddImage(document, pixels, name, active);
        }
    }

    /// <summary>
    /// A never-edited transparent new document may adopt its first imported image. An opened file,
    /// a filled background, or a layer the user has already changed must keep its canvas.
    /// </summary>
    private bool IsPristineBlankCanvas(CanvasDocument document)
    {
        if (_history.IsModified || FilePath is not null || _isPhotoshopDocument) return false;
        if (document.Layers.Count == 0) return true;
        if (document.Layers.Count != 1) return false;

        ImageLayer layer = document.Layers[0];
        return layer.Image is null && !layer.IsGroup && layer.ParentId is null
            && layer.MaskSourceId is null && layer.Mask is null && layer.Adjustment is null
            && layer.Shape is null && layer.Text is null && layer.Effects is null
            && layer.IsVisible && layer.Opacity == 1 && layer.BlendMode == LayerBlendMode.Normal
            && layer.Transform == new LayerTransform(Point.Zero, document.Size);
    }

    public bool IsModified => _history.IsModified;

    /// <summary>The layer a save records as active.</summary>
    public Guid? ActiveLayerId => Primary;

    /// <summary>Records that the document now matches the project at <paramref name="path"/>.</summary>
    public void MarkSaved(string path)
    {
        FilePath = path;
        _isPhotoshopDocument = Path.GetExtension(path).ToLowerInvariant() is ".psd" or ".psb";
        _history.MarkSaved();
    }

    public bool CanEdit => _document is not null && !IsFiltering && _drag is null && _stroke is null && _warp is null
                           && _floating is null && EditingText is null && _meshWarp is null && _liquify is null;

    public bool CanUndo => _liquify?.CanUndo == true || CanEdit && _history.CanUndo;
    public bool CanRedo => CanEdit && _history.CanRedo;
    public string UndoName => _liquify?.CanUndo == true ? Localizer.Text(TextKey.HistoryLiquify) : _history.UndoName;
    public string RedoName => _history.RedoName;

    public void Undo()
    {
        if (_liquify is LiquifyField field)
        {
            if (field.Undo())
            {
                _liquifyLast = null;
                _liquifyPendingView = null;
                NeedsRedraw = true;
            }
            return;
        }
        Restore(_history.Undo());
    }

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
        if (_gradientFrom is not null && id != _gradientTarget) CommitGradient();
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
    public void ChangeTransform(Func<LayerTransform, LayerTransform> change)
    {
        // Floating pixels are placed inside the one step Transform Selection already opened.
        if (IsFloating && _document is not null && ActiveLayer is ImageLayer floating)
        {
            LayerTransform placed = change(floating.Transform);
            if (placed != floating.Transform && placed.IsValid) _document = _document.Replacing(floating with { Transform = placed });
            NeedsRedraw = true;
            return;
        }

        if (TransformsMask)
        {
            Edit(TextKey.HistoryTransformMask, document =>
            {
                if (ActiveLayer is not ImageLayer layer || MaskEditing.PlacementOf(layer) is not LayerTransform at) return null;
                LayerTransform next = change(at);
                if (next == at || !next.IsValid) return null;
                return (document.Replacing(MaskEditing.WithMaskPlacement(layer, next)), layer.Id);
            });
            return;
        }

        Edit(TextKey.HistoryTransformLayer, document =>
        {
            if (ActiveLayer is not { IsGroup: false } layer) return null;
            LayerTransform next = change(layer.Transform);
            if (next == layer.Transform || !next.IsValid) return null;
            return (document.Replacing(MaskEditing.WithTransform(layer, next)), layer.Id);
        });
    }

    /// <summary>What the Move tool and the transform fields work on: the layer, or an unlinked mask.</summary>
    public LayerTransform? TransformTarget =>
        ActiveLayer is not ImageLayer layer ? null
        : TransformsMask ? MaskEditing.PlacementOf(layer)
        : layer.Transform;

    /// <summary>The pixels behind <see cref="TransformTarget"/>, for its scale.</summary>
    public Size TransformTargetPixels
    {
        get
        {
            if (ActiveLayer is not ImageLayer layer || TransformTarget is not LayerTransform t) return new Size(1, 1);
            PixelBuffer? pixels = TransformsMask ? layer.Mask!.Coverage : layer.Image;
            return pixels is { Width: > 1 } or { Height: > 1 } ? new Size(pixels.Width, pixels.Height) : t.Size;
        }
    }
}
