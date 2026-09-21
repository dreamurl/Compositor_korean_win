namespace Compositor_korean_win.Core;

/// <summary>Where an edit left the document, and which layer was active.</summary>
public sealed record HistorySnapshot(CanvasDocument? Document, Guid? ActiveLayerId, Guid Revision);

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
/// </remarks>
public sealed class DocumentHistory
{
    private sealed record Entry(string Name, HistorySnapshot Before, HistorySnapshot After);

    private readonly List<Entry> _past = [];
    private readonly List<Entry> _future = [];

    private Guid _revision = Guid.NewGuid();
    private Guid _savedRevision;
    private HistorySnapshot? _pending;
    private string _pendingName = "Edit";
    private int _depth;

    public DocumentHistory(int entryLimit = 100, long retainedByteLimit = 256L * 1024 * 1024)
    {
        EntryLimit = Math.Max(0, entryLimit);
        RetainedByteLimit = Math.Max(0, retainedByteLimit);
        _savedRevision = _revision;
    }

    public int EntryLimit { get; }
    public long RetainedByteLimit { get; }

    /// <summary>False while an edit is open: undo may not run inside one.</summary>
    public bool CanUndo => _depth == 0 && _past.Count > 0;

    public bool CanRedo => _depth == 0 && _future.Count > 0;

    public string UndoName => _past.Count > 0 ? _past[^1].Name : string.Empty;
    public string RedoName => _future.Count > 0 ? _future[^1].Name : string.Empty;

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
    public void Begin(string name, CanvasDocument? document, Guid? selection)
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
        _future.Clear();
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
        while (_past.Count + _future.Count > EntryLimit || RetainedBytes(current) > RetainedByteLimit)
        {
            // The oldest undo step goes first; only once there are none left does redo give way.
            if (_past.Count > 0) _past.RemoveAt(0);
            else if (_future.Count > 0) _future.RemoveAt(0);
            else break;
        }
    }
}
