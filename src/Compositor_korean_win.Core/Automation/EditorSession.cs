namespace Compositor_korean_win.Core;

/// <summary>
/// The documents an automation client is working on, each with its own history.
/// </summary>
/// <remarks>
/// <para>
/// Every change goes through <see cref="Edit"/>, which records one undo step the way the canvas
/// does, in a history that owns its pixels — so a model that paints for an hour does not leak what
/// it undid, and <c>undo</c> means exactly what it means in the editor.
/// </para>
/// <para>
/// Documents are named <c>doc1</c>, <c>doc2</c>… in the order they open. Tools that act on a
/// document take one of those names, or act on the current one — the last opened or selected —
/// when none is given, which is what a model working on one picture wants.
/// </para>
/// </remarks>
public sealed class EditorSession(IEditorServices services) : IDisposable
{
    public sealed class Open
    {
        public required string Id { get; init; }
        public required CanvasDocument Document { get; set; }
        public DocumentHistory History { get; } = new(ownsPixels: true);
        public string? Path { get; set; }
        public string Title { get; set; } = "";
        public Guid? Active { get; set; }
    }

    private readonly Dictionary<string, Open> _open = [];
    private string? _current;
    private int _next = 1;

    public IEditorServices Services => services;

    public IReadOnlyCollection<Open> Documents => _open.Values;

    public Open? Current => _current is string id && _open.TryGetValue(id, out Open? open) ? open : null;

    public Open Add(CanvasDocument document, string title, string? path)
    {
        var open = new Open
        {
            Id = $"doc{_next++}",
            Document = document,
            Path = path,
            Title = title,
            Active = document.Layers.Count > 0 ? document.Layers[^1].Id : null,
        };
        _open[open.Id] = open;
        _current = open.Id;
        return open;
    }

    /// <summary>A document by its id or title, or the current one when <paramref name="reference"/> is empty.</summary>
    public Open Get(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return Current ?? throw new ToolException("No document is open. Call new_document or open_document first.");
        if (_open.TryGetValue(reference, out Open? open)) return open;
        return _open.Values.FirstOrDefault(each => string.Equals(each.Title, reference, StringComparison.OrdinalIgnoreCase))
               ?? throw new ToolException($"No open document is called '{reference}'. Open ones: {string.Join(", ", _open.Keys)}.");
    }

    public void Select(Open open) => _current = open.Id;

    public void Close(Open open)
    {
        open.History.Clear(open.Document);
        _open.Remove(open.Id);
        if (_current == open.Id) _current = _open.Keys.LastOrDefault();
    }

    /// <summary>One undoable change: <paramref name="change"/> returns the document as it should become.</summary>
    public void Edit(Open open, string name, Func<CanvasDocument, CanvasDocument> change)
    {
        open.History.Begin(name, open.Document, open.Active);
        CanvasDocument next = open.Document;
        try
        {
            next = change(open.Document);
        }
        finally
        {
            open.Document = next;
            if (open.Active is Guid active && next.Layer(active) is null)
                open.Active = next.Layers.Count > 0 ? next.Layers[^1].Id : null;
            open.History.End(next, open.Active);
        }
    }

    /// <summary>
    /// A layer by id, or by name — the topmost of that name, as a person would mean it. Ids are
    /// what every tool answers with, so a model that keeps them never meets an ambiguity.
    /// </summary>
    public static ImageLayer Layer(CanvasDocument document, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) throw new ToolException("'layer' is required: a layer id or name from get_document.");
        if (Guid.TryParse(reference, out Guid id) && document.Layer(id) is ImageLayer byId) return byId;
        for (int i = document.Layers.Count - 1; i >= 0; i--)
            if (string.Equals(document.Layers[i].Name, reference, StringComparison.Ordinal)) return document.Layers[i];
        for (int i = document.Layers.Count - 1; i >= 0; i--)
            if (string.Equals(document.Layers[i].Name, reference, StringComparison.OrdinalIgnoreCase)) return document.Layers[i];
        throw new ToolException($"No layer '{reference}'. Call get_document to see the layers and their ids.");
    }

    public void Dispose()
    {
        foreach (Open open in _open.Values.ToList()) Close(open);
    }
}
