using Compositor_korean_win.Core;
using Point = Compositor_korean_win.Core.Point;

namespace Compositor_korean_win.Shell;

/// <summary>One open document and everything that belongs to it rather than to the window.</summary>
/// <remarks>
/// The document, its own undo history, where it lives on disk, how it is zoomed and panned, what is
/// selected and which layers are chosen. The tools, their settings and the colours belong to the
/// window, and carry from one document to the next as they do in Photoshop.
/// </remarks>
internal sealed class DocumentTab
{
    public CanvasDocument? Document { get; set; }
    public DocumentHistory History { get; set; } = new(ownsPixels: true);
    public string? FilePath { get; set; }
    public string? Name { get; set; }
    public CanvasViewport Viewport { get; set; } = new();
    public DocumentSelection? Selection { get; set; }
    public List<Guid> Chosen { get; set; } = [];
}

/// <summary>
/// Several documents open at once, one of them on the canvas — upstream's <c>ProjectTabs</c>.
/// </summary>
/// <remarks>
/// <para>
/// The canvas keeps working on its fields as it always has; a tab is where those fields wait while
/// another document has the canvas. Switching puts the current document's away and brings the
/// other's out, so nothing else in the canvas had to learn that there is more than one document.
/// </para>
/// <para>
/// A switch waits while something is half done — a stroke, a drag, an open filter — since each of
/// those holds pixels or a history step that belongs to the document it started in.
/// </para>
/// </remarks>
internal sealed partial class CanvasView
{
    private readonly List<DocumentTab> _tabs = [];
    private int _active = -1;
    private string? _name;

    /// <summary>The open documents, in the order their tabs show.</summary>
    public IReadOnlyList<DocumentTab> Tabs
    {
        get
        {
            Stash();
            return _tabs;
        }
    }

    /// <summary>The tab on the canvas, or −1 with no document open.</summary>
    public int ActiveTab => _active;

    /// <summary>Whether the canvas can leave the document it is on — nothing is half done in it.</summary>
    public bool CanSwitchTab => _document is null || CanEdit;

    /// <summary>What a tab is called: its file's name, the image it was made from, or "Untitled".</summary>
    public static string TabTitle(DocumentTab tab) =>
        tab.FilePath is string path ? Path.GetFileNameWithoutExtension(path)
        : tab.Name ?? Localizer.Text(TextKey.DocumentUntitled);

    /// <summary>Opens a document in a tab of its own, after the others, and shows it.</summary>
    public void Open(CanvasDocument document, string? path = null, string? name = null)
    {
        if (!CanSwitchTab) return;
        Stash();

        _tabs.Add(new DocumentTab { Document = document, FilePath = path, Name = name });
        Bring(_tabs.Count - 1);
        ChooseTopImageLayer();
        _viewport = _viewport.Fit(document.Size);
    }

    /// <summary>Puts another tab's document on the canvas.</summary>
    public void SwitchTo(int index)
    {
        if (index == _active || index < 0 || index >= _tabs.Count || !CanSwitchTab) return;
        Stash();
        Bring(index);
    }

    /// <summary>The next tab along, or the one before, wrapping round — Ctrl+Tab and Ctrl+Shift+Tab.</summary>
    public void CycleTabs(int step)
    {
        if (_tabs.Count < 2) return;
        SwitchTo(((_active + step) % _tabs.Count + _tabs.Count) % _tabs.Count);
    }

    /// <summary>Closes the document on the canvas, freeing its pixels and its history, and shows its neighbour.</summary>
    public void Close()
    {
        if (_active < 0) return;

        _preview?.Dispose();
        _preview = null;
        _adjusting = null;
        ReleaseSource();
        _history.Clear(_document);

        int closed = _active;
        _tabs.RemoveAt(closed);
        _active = -1;

        if (_tabs.Count > 0)
        {
            Bring(Math.Min(closed, _tabs.Count - 1));
        }
        else
        {
            _document = null;
            _history = new DocumentHistory(ownsPixels: true);
            FilePath = null;
            _name = null;
            _selection = null;
            _chosen.Clear();
            ForgetDocumentState();
            NeedsRedraw = true;
        }
    }

    /// <summary>The canvas's fields for the active document, written back into its tab.</summary>
    private void Stash()
    {
        if (_active < 0 || _active >= _tabs.Count) return;
        DocumentTab tab = _tabs[_active];
        tab.Document = _document;
        tab.History = _history;
        tab.FilePath = FilePath;
        tab.Name = _name;
        tab.Viewport = _viewport;
        tab.Selection = _selection;
        tab.Chosen = [.. _chosen];
    }

    /// <summary>A tab's document brought onto the canvas.</summary>
    private void Bring(int index)
    {
        DocumentTab tab = _tabs[index];
        _active = index;
        _document = tab.Document;
        _history = tab.History;
        FilePath = tab.FilePath;
        _name = tab.Name;
        _selection = tab.Selection;
        _chosen.Clear();
        foreach (Guid id in tab.Chosen) _chosen.Add(id);

        // The viewport keeps the window's size and scale; the tab keeps where it was looking.
        _viewport = tab.Document is CanvasDocument document && tab.Viewport.Zoom > 0
            ? tab.Viewport.Resized(_viewport.ViewSize, _scale, document.Size)
            : _viewport;

        ForgetDocumentState();
        NeedsRedraw = true;
    }

    /// <summary>What the canvas remembers about the document it was on and must not carry to another.</summary>
    private void ForgetDocumentState()
    {
        ReleaseComposite();
        _cloneAnchor = null;
        _cloneOffset = null;
        _polygon = null;
        _lasso = null;
        _marqueeFrom = null;
        _shapeFrom = null;
        _movingFrom = null;
        _distortPreview?.Dispose();
        _distortPreview = null;
        _distorting = null;
    }

    /// <summary>Every tab's pixels and history freed, for when the canvas goes.</summary>
    private void CloseAllTabs()
    {
        Stash();
        foreach (DocumentTab tab in _tabs) tab.History.Clear(tab.Document);
        _tabs.Clear();
        _active = -1;
        _document = null;
    }
}
