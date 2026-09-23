namespace Compositor_korean_win.Core;

/// <summary>Where an edit left the document, and which layer was active.</summary>
public sealed record HistorySnapshot(CanvasDocument? Document, Guid? ActiveLayerId, Guid Revision);

/// <summary>
/// What a history step is called: a phrase from the text table, or words given as they are.
/// </summary>
/// <remarks>
/// A step keeps its key rather than its words, so switching the interface's language renames the
/// whole history at once — the undo menu of a Korean session switched to English reads English.
/// Arguments that are keys themselves are translated too ("New {0} Layer" with Levels in it).
/// </remarks>
public readonly record struct HistoryName(TextKey? Key, string? Literal = null, object?[]? Arguments = null)
{
    public static implicit operator HistoryName(string literal) => new(null, literal);

    public static implicit operator HistoryName(TextKey key) => new(key);

    public static HistoryName Of(TextKey key, params object?[] arguments) => new(key, null, arguments);

    public override string ToString()
    {
        if (Key is not TextKey key) return Literal ?? string.Empty;
        if (Arguments is not { Length: > 0 } arguments) return Localizer.Text(key);

        object?[] words = [.. arguments.Select(argument => argument is TextKey inner ? Localizer.Text(inner) : argument)];
        return Localizer.Format(key, words);
    }
}

/// <summary>
/// Undo and redo over whole-document value snapshots.
/// </summary>
/// <remarks>
/// <para>
/// Upstream's comment on this type is the whole design: "Value snapshots share immutable CGImages;
/// no pixel copies for layer edits." A snapshot is a <see cref="CanvasDocument"/>, which is a value
/// — but the pixels inside it are <see cref="PixelBuffer"/> references, and buffers are immutable,
/// so a hundred snapshots of a hundred-megapixel document cost a hundred small records and one set
/// of pixels. docs/windows-port.md §2.2 names this the largest single lever on memory use, and it
/// is why the model is built out of records rather than mutable objects.
/// </para>
/// <para>
/// Two limits keep it bounded: a hundred steps, and 256 MiB of pixels held by history alone.
/// Whichever is reached first drops the oldest step.
/// </para>
/// <para>
/// A history built with <c>ownsPixels</c> also frees what it drops. Pixels are unmanaged, so a
/// step falling off the end freed nothing until M6 — a stroke on a hundred-megapixel layer left
/// 400 MB behind for good. The rule is reachability: a dropped step's buffers are released once
/// neither the current document nor any step still kept refers to them, which is exactly when
/// nothing can bring them back. The canvas owns its documents this way; code that manages its own
/// buffers, as the tests do, leaves the flag off.
/// </para>
/// </remarks>
public sealed class DocumentHistory
{
    private sealed record Entry(HistoryName Name, HistorySnapshot Before, HistorySnapshot After);

    private readonly List<Entry> _past = [];
    private readonly List<Entry> _future = [];

    private Guid _revision = Guid.NewGuid();
    private Guid _savedRevision;
    private HistorySnapshot? _pending;
    private HistoryName _pendingName = TextKey.HistoryEdit;
    private int _depth;

    public DocumentHistory(int entryLimit = 100, long retainedByteLimit = 256L * 1024 * 1024,
                           bool ownsPixels = false)
    {
        EntryLimit = Math.Max(0, entryLimit);
        RetainedByteLimit = Math.Max(0, retainedByteLimit);
        OwnsPixels = ownsPixels;
        _savedRevision = _revision;
    }

    /// <summary>Whether dropping a step frees the pixels only it held.</summary>
    public bool OwnsPixels { get; }

    public int EntryLimit { get; }
    public long RetainedByteLimit { get; }

    /// <summary>False while an edit is open: undo may not run inside one.</summary>
    public bool CanUndo => _depth == 0 && _past.Count > 0;

    public bool CanRedo => _depth == 0 && _future.Count > 0;

    public string UndoName => _past.Count > 0 ? _past[^1].Name.ToString() : string.Empty;
    public string RedoName => _future.Count > 0 ? _future[^1].Name.ToString() : string.Empty;

    public bool IsModified => _revision != _savedRevision;
    public int UndoCount => _past.Count;

    public void MarkSaved() => _savedRevision = _revision;

    public void Reset()
    {
        _past.Clear();
        _future.Clear();
        _pending = null;
        _depth = 0;
        _revision = Guid.NewGuid();
        _savedRevision = _revision;
    }

    /// <summary>
    /// Opens an edit. Nested calls join the one already open, so a compound operation lands as a
    /// single undo step.
    /// </summary>
    public void Begin(HistoryName name, CanvasDocument? document, Guid? selection)
    {
        if (_depth == 0)
        {
            _pending = new HistorySnapshot(document, selection, _revision);
            _pendingName = name;
        }
        _depth++;
    }

    /// <summary>
    /// Closes an edit, recording a step only if the document actually changed.
    /// </summary>
    /// <remarks>
    /// Selecting, navigating and edits that came to nothing must leave the redo stack alone —
    /// otherwise clicking around after an undo would silently discard what could still be redone.
    /// That test is a value comparison of the whole document, which is cheap because pixels compare
    /// by identity.
    /// </remarks>
    public void End(CanvasDocument? document, Guid? selection)
    {
        if (_depth == 0) return;
        _depth--;
        if (_depth != 0 || _pending is not HistorySnapshot before) return;

        _pending = null;
        if (before.Document == document) return;

        _revision = Guid.NewGuid();
        _past.Add(new Entry(_pendingName, before, new HistorySnapshot(document, selection, _revision)));

        // A new step ends the redo branch: nothing can reach those steps again.
        List<Entry> abandoned = [.. _future];
        _future.Clear();
        Forget(abandoned, document);
        Trim(document);
    }

    public HistorySnapshot? Undo()
    {
        if (!CanUndo) return null;

        Entry entry = _past[^1];
        _past.RemoveAt(_past.Count - 1);
        _future.Add(entry);
        _revision = entry.Before.Revision;
        Trim(entry.Before.Document);
        return entry.Before;
    }

    public HistorySnapshot? Redo()
    {
        if (!CanRedo) return null;

        Entry entry = _future[^1];
        _future.RemoveAt(_future.Count - 1);
        _past.Add(entry);
        _revision = entry.After.Revision;
        Trim(entry.After.Document);
        return entry.After;
    }

    /// <summary>
    /// Bytes held only by history — pixels the live document no longer references.
    /// </summary>
    /// <remarks>
    /// Buffers in the open document are excluded because they are not history's cost; they would be
    /// resident either way. What this measures is what undo is actually keeping alive.
    /// </remarks>
    public long RetainedBytes(CanvasDocument? current)
    {
        // PixelBuffer does not override Equals, so the default comparer is identity — which is the
        // question being asked: two layers sharing one buffer share its cost exactly once.
        var seen = new HashSet<PixelBuffer>();
        foreach (PixelBuffer buffer in BuffersOf(current)) seen.Add(buffer);

        long bytes = 0;
        foreach (Entry entry in _past.Concat(_future))
        {
            foreach (PixelBuffer buffer in BuffersOf(entry.Before.Document))
                if (seen.Add(buffer)) bytes += buffer.ByteCount;
            foreach (PixelBuffer buffer in BuffersOf(entry.After.Document))
                if (seen.Add(buffer)) bytes += buffer.ByteCount;
        }

        return bytes;
    }

    private static IEnumerable<PixelBuffer> BuffersOf(CanvasDocument? document)
    {
        foreach (ImageLayer layer in document?.Layers ?? EquatableList<ImageLayer>.Empty)
        {
            if (layer.Image is PixelBuffer image) yield return image;
            if (layer.Mask?.Coverage is PixelBuffer coverage) yield return coverage;
        }
    }

    private void Trim(CanvasDocument? current)
    {
        var dropped = new List<Entry>();
        while (_past.Count + _future.Count > EntryLimit || RetainedBytes(current) > RetainedByteLimit)
        {
            // The oldest undo step goes first; only once there are none left does redo give way.
            if (_past.Count > 0) { dropped.Add(_past[0]); _past.RemoveAt(0); }
            else if (_future.Count > 0) { dropped.Add(_future[0]); _future.RemoveAt(0); }
            else break;
        }
        Forget(dropped, current);
    }

    /// <summary>
    /// Releases the buffers of <paramref name="dropped"/> steps that nothing kept still reaches.
    /// </summary>
    private void Forget(List<Entry> dropped, CanvasDocument? current)
    {
        if (!OwnsPixels || dropped.Count == 0) return;

        var reachable = new HashSet<PixelBuffer>(BuffersOf(current));
        if (_pending is HistorySnapshot pending) reachable.UnionWith(BuffersOf(pending.Document));
        foreach (Entry entry in _past.Concat(_future))
        {
            reachable.UnionWith(BuffersOf(entry.Before.Document));
            reachable.UnionWith(BuffersOf(entry.After.Document));
        }

        var released = new HashSet<PixelBuffer>();
        foreach (Entry entry in dropped)
        {
            foreach (PixelBuffer buffer in BuffersOf(entry.Before.Document).Concat(BuffersOf(entry.After.Document)))
                if (!reachable.Contains(buffer) && released.Add(buffer)) buffer.Release();
        }
    }

    /// <summary>
    /// Forgets every step and, when it owns them, frees every buffer the steps and
    /// <paramref name="current"/> hold — for closing a document.
    /// </summary>
    public void Clear(CanvasDocument? current)
    {
        if (OwnsPixels)
        {
            var all = new HashSet<PixelBuffer>(BuffersOf(current));
            if (_pending is HistorySnapshot pending) all.UnionWith(BuffersOf(pending.Document));
            foreach (Entry entry in _past.Concat(_future))
            {
                all.UnionWith(BuffersOf(entry.Before.Document));
                all.UnionWith(BuffersOf(entry.After.Document));
            }
            foreach (PixelBuffer buffer in all) buffer.Release();
        }

        Reset();
    }
}
