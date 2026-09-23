using Compositor_korean_win.Core;
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

    /// <summary>What to call the document: its file's name, or "Untitled".</summary>
    public string Title => FilePath is string path
        ? System.IO.Path.GetFileNameWithoutExtension(path)
        : Localizer.Text(TextKey.DocumentUntitled);

    public bool HasDocument => _document is not null;

    public bool IsModified => _history.IsModified;

    /// <summary>The layer a save records as active.</summary>
    public Guid? ActiveLayerId => Primary;

    /// <summary>Records that the document now matches the project at <paramref name="path"/>.</summary>
    public void MarkSaved(string path)
    {
        FilePath = path;
        _history.MarkSaved();
    }

    public bool CanEdit => _document is not null && !IsFiltering && _drag is null && _stroke is null;

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
}
