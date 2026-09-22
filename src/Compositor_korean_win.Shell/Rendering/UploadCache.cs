using Compositor_korean_win.Core;
using Vortice.Direct2D1;

namespace Compositor_korean_win.Shell;

/// <summary>
/// Pixels already on the GPU, so a frame that has not changed does not send them again.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by the buffer, the halving level and the region — the three things that decide what was
/// uploaded. A buffer compares by identity, which is exactly right here: Core's buffers are
/// immutable, so the same instance is always the same pixels, and an edit makes a new one
/// (docs/windows-port.md §2.2).
/// </para>
/// <para>
/// Eviction is by age and by how much has been uploaded. The cap matters more than the policy:
/// without one, panning across a large document would leave a texture behind for every region the
/// pointer ever passed over.
/// </para>
/// </remarks>
internal sealed class UploadCache : IDisposable
{
    /// <summary>What a cached upload is: some pixels, reduced this far, over this region.</summary>
    internal readonly record struct Key(PixelBuffer Buffer, int Level, PixelRect Region);

    /// <summary>Roughly a hundred megabytes of textures — a handful of windows' worth.</summary>
    private const long PixelBudget = 25_000_000;

    private sealed record Entry(ID2D1Bitmap1 Bitmap, int Width, int Height)
    {
        public long Use { get; set; }

        public long Pixels => (long)Width * Height;
    }

    private readonly Dictionary<Key, Entry> _entries = [];
    private long _clock;

    public bool TryGet(Key key, out ID2D1Bitmap1 bitmap, out int width, out int height)
    {
        if (_entries.TryGetValue(key, out Entry? entry))
        {
            entry.Use = ++_clock;
            bitmap = entry.Bitmap;
            width = entry.Width;
            height = entry.Height;
            return true;
        }

        bitmap = null!;
        width = height = 0;
        return false;
    }

    public void Put(Key key, ID2D1Bitmap1 bitmap, int width, int height)
    {
        if (_entries.TryGetValue(key, out Entry? existing))
        {
            // Somebody else got there first; keep what is already cached and drop this copy.
            existing.Use = ++_clock;
            bitmap.Dispose();
            return;
        }

        _entries[key] = new Entry(bitmap, width, height) { Use = ++_clock };
        Evict(key);
    }

    private void Evict(Key keeping)
    {
        long total = 0;
        foreach (Entry entry in _entries.Values) total += entry.Pixels;

        while (total > PixelBudget && _entries.Count > 1)
        {
            KeyValuePair<Key, Entry>? oldest = null;
            foreach (KeyValuePair<Key, Entry> candidate in _entries)
            {
                if (candidate.Key.Equals(keeping)) continue;
                if (oldest is null || candidate.Value.Use < oldest.Value.Value.Use) oldest = candidate;
            }

            if (oldest is null) break;
            total -= oldest.Value.Value.Pixels;
            oldest.Value.Value.Bitmap.Dispose();
            _entries.Remove(oldest.Value.Key);
        }
    }

    public void Dispose()
    {
        foreach (Entry entry in _entries.Values) entry.Bitmap.Dispose();
        _entries.Clear();
    }
}
