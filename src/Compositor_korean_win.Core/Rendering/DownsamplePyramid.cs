using System.Runtime.CompilerServices;

namespace Compositor_korean_win.Core;

/// <summary>
/// Sharp halvings of a layer's pixels, so a large reduction never goes through one resample.
/// </summary>
/// <remarks>
/// <para>
/// Shrinking an image 4× or 8× in a single step looks soft or grainy whatever the filter, because
/// a resampler only looks at a few neighbouring pixels and most of the source never gets consulted.
/// Upstream's answer is a chain of halved copies, so the drawing code is only ever left with the
/// last reduction of at most 2×. docs/windows-port.md §2.3 keeps that, and this is it.
/// </para>
/// <para>
/// <b>One deliberate divergence.</b> Upstream halves with Lanczos (vImage) and then has to clamp
/// the result, because Lanczos rings and can push a premultiplied channel above its own alpha. This
/// halves by averaging the four pixels that make up each output pixel. At exactly 2× that is the
/// correct area average, it cannot ring, and — the part that matters here — it reaches nothing
/// outside its own 2×2 block. That last property is what makes a piece of an image reduce to
/// exactly the same pixels as that piece of the whole image reduced, which is the invariant
/// <see cref="LayerRaster"/> needs and which upstream has to buy with a 16·2^level pixel margin.
/// The cost is that a box filter is very slightly softer than Lanczos on detailed images.
/// </para>
/// </remarks>
public sealed class DownsamplePyramid : IDisposable
{
    /// <summary>The most halvings ever used; past this the final resample does the rest.</summary>
    public const int MaximumLevel = 6;

    /// <summary>Pixels of halved copies kept at once — about 400 MB of RGBA.</summary>
    public const long PixelBudget = 100_000_000;

    private sealed class Entry
    {
        public required List<PixelBuffer> Levels { get; init; }
        public long LastUse { get; set; }
        public long Pixels => Levels.Sum(level => (long)level.Width * level.Height);
    }

    private readonly Dictionary<PixelBuffer, Entry> _entries = [];
    private readonly Lock _lock = new();
    private long _clock;

    /// <summary>
    /// How many halvings to draw from when a source pixel lands <paramref name="factor"/> output
    /// pixels wide: the most that still leave the copy at least as large as it is drawn.
    /// </summary>
    public static int LevelFor(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0 || factor >= 0.5) return 0;
        return Math.Min(MaximumLevel, (int)Math.Floor(Math.Log2(1 / factor)));
    }

    /// <summary>
    /// <paramref name="source"/> reduced by <paramref name="level"/> halvings, and how many were
    /// applied — fewer only when the image has run out of pixels to halve.
    /// </summary>
    /// <remarks>
    /// The result is owned by the cache and must not be released by the caller. Each halving rounds
    /// the size up, so level <c>k</c> pixel <c>i</c> always covers source pixels
    /// <c>i·2^k</c> to <c>(i+1)·2^k</c>.
    /// </remarks>
    public (PixelBuffer Image, int Level) Reduced(PixelBuffer source, int level)
    {
        if (level < 1 || (source.Width <= 1 && source.Height <= 1)) return (source, 0);

        List<PixelBuffer> levels;
        lock (_lock)
        {
            _clock++;
            levels = _entries.TryGetValue(source, out Entry? existing) ? existing.Levels : [];
        }

        while (levels.Count < level)
        {
            PixelBuffer previous = levels.Count > 0 ? levels[^1] : source;
            if (previous.Width <= 1 && previous.Height <= 1) break;
            levels.Add(Halve(previous));
        }

        if (levels.Count == 0) return (source, 0);

        lock (_lock)
        {
            if (_entries.TryGetValue(source, out Entry? stored) && stored.Levels.Count >= levels.Count)
            {
                stored.LastUse = _clock;
            }
            else
            {
                if (stored is not null) Release(stored);
                _entries[source] = new Entry { Levels = levels, LastUse = _clock };
            }

            Evict(keeping: source);
        }

        int applied = Math.Min(level, levels.Count);
        return (levels[applied - 1], applied);
    }

    /// <summary>Exactly half the size, rounded up, by averaging each 2×2 block.</summary>
    /// <remarks>
    /// Averaging happens on premultiplied values, which is the only way it is correct: averaging
    /// straight colour would let a fully transparent pixel's arbitrary colour bleed into its
    /// neighbours. An odd last row or column averages the pixels it actually has.
    /// </remarks>
    public static PixelBuffer Halve(PixelBuffer source)
    {
        int width = Math.Max(1, (source.Width + 1) / 2);
        int height = Math.Max(1, (source.Height + 1) / 2);
        PixelBuffer result = PixelBuffer.Allocate(width, height);

        for (int y = 0; y < height; y++)
        {
            int topRow = y * 2;
            int bottomRow = Math.Min(topRow + 1, source.Height - 1);
            bool twoRows = topRow + 1 < source.Height;

            ReadOnlySpan<byte> top = source.Row(topRow);
            ReadOnlySpan<byte> bottom = twoRows ? source.Row(bottomRow) : default;
            Span<byte> target = result.Row(y);

            for (int x = 0; x < width; x++)
            {
                int left = x * 2;
                bool twoColumns = left + 1 < source.Width;

                for (int channel = 0; channel < 4; channel++)
                {
                    int sum = top[left * 4 + channel];
                    int count = 1;

                    if (twoColumns) { sum += top[(left + 1) * 4 + channel]; count++; }
                    if (twoRows)
                    {
                        sum += bottom[left * 4 + channel];
                        count++;
                        if (twoColumns) { sum += bottom[(left + 1) * 4 + channel]; count++; }
                    }

                    target[x * 4 + channel] = (byte)((sum + count / 2) / count);
                }
            }
        }

        return result;
    }

    /// <summary>Drops the least recently used copies until the rest fit the budget.</summary>
    private void Evict(PixelBuffer keeping)
    {
        long total = _entries.Values.Sum(entry => entry.Pixels);
        while (total > PixelBudget)
        {
            KeyValuePair<PixelBuffer, Entry>? oldest = null;
            foreach (KeyValuePair<PixelBuffer, Entry> candidate in _entries)
            {
                if (ReferenceEquals(candidate.Key, keeping)) continue;
                if (oldest is null || candidate.Value.LastUse < oldest.Value.Value.LastUse) oldest = candidate;
            }

            if (oldest is null) break;
            total -= oldest.Value.Value.Pixels;
            Release(oldest.Value.Value);
            _entries.Remove(oldest.Value.Key);
        }
    }

    private static void Release(Entry entry)
    {
        foreach (PixelBuffer level in entry.Levels) level.Release();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (Entry entry in _entries.Values) Release(entry);
            _entries.Clear();
        }
    }

    /// <summary>Compares buffers by identity, so painting — which makes a new buffer — misses.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool SameBuffer(PixelBuffer a, PixelBuffer b) => ReferenceEquals(a, b);
}
