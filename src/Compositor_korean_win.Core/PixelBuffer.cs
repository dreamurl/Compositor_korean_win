using System.Runtime.InteropServices;

namespace Compositor_korean_win.Core;

/// <summary>
/// An immutable block of premultiplied RGBA8888 pixels, shared by reference count.
/// </summary>
/// <remarks>
/// This replaces upstream Compositor's dependency on <c>CGImage</c>, which the document model and
/// history layer lean on in 281 places. Three properties carry over and matter more than the type
/// itself:
/// <list type="bullet">
/// <item>Immutable — an edit produces a new buffer or a tile patch, never a write in place, so a
/// history snapshot can hold the same pixels as the live document without copying them.</item>
/// <item>Reference counted — the last holder to let go frees the memory.</item>
/// <item>Unmanaged — a 100-megapixel document is 400 MB. On the managed heap that means large
/// object heap fragmentation and GC pauses.</item>
/// </list>
/// </remarks>
public sealed unsafe class PixelBuffer : IDisposable
{
    private nint _scan0;
    private int _references = 1;
    private readonly object _materializeGate = new();
    private PixelBuffer? _base;
    private RasterPatch[]? _patches;
    private bool _disposed;

    private PixelBuffer(nint scan0, int width, int height, int stride)
    {
        _scan0 = scan0;
        Width = width;
        Height = height;
        Stride = stride;
    }

    private PixelBuffer(PixelBuffer baseImage, RasterPatch[] patches)
    {
        Width = baseImage.Width;
        Height = baseImage.Height;
        Stride = baseImage.Stride;
        _base = baseImage.Retain();
        _patches = patches;
        foreach (RasterPatch patch in patches) patch.Pixels.Retain();
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Bytes per row. Rows are 32-byte aligned so SIMD loads stay aligned.</summary>
    public int Stride { get; }

    public long ByteCount => (long)Stride * Height;

    /// <summary>The first byte of the first row. Rows run top-down.</summary>
    public nint Scan0
    {
        get
        {
            EnsureMaterialized();
            return _scan0;
        }
    }

    /// <summary>Whether this buffer is still an immutable base plus replacement tiles.</summary>
    /// <remarks>
    /// Rendering asks such a buffer for just the visible region. Export and filters may still ask
    /// for a row, which materialises it once and releases the tile references afterwards.
    /// </remarks>
    public bool IsDeferred => !_disposed && _scan0 == 0 && _base is not null;

    /// <summary>Live buffers and the bytes they hold, for the memory figures M0 reports.</summary>
    public static int LiveCount => Volatile.Read(ref s_liveCount);
    public static long LiveBytes => Volatile.Read(ref s_liveBytes);

    private static int s_liveCount;
    private static long s_liveBytes;

    private const int RowAlignment = 32;

    public static PixelBuffer Allocate(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        int stride = checked(width * 4 + RowAlignment - 1) / RowAlignment * RowAlignment;
        nuint bytes = checked((nuint)stride * (nuint)height);

        nint scan0 = (nint)NativeMemory.AlignedAlloc(bytes, RowAlignment);
        NativeMemory.Clear((void*)scan0, bytes);

        Interlocked.Increment(ref s_liveCount);
        Interlocked.Add(ref s_liveBytes, (long)bytes);
        return new PixelBuffer(scan0, width, height, stride);
    }

    /// <summary>
    /// An immutable snapshot made from <paramref name="baseImage"/> and replacement tiles, without
    /// copying the untouched part of the image. The snapshot owns references to both until it is
    /// materialised or released.
    /// </summary>
    public static PixelBuffer Layered(PixelBuffer baseImage, IReadOnlyList<RasterPatch> patches)
    {
        ArgumentNullException.ThrowIfNull(baseImage);
        if (patches.Count == 0) return baseImage.Retain();

        RasterPatch[] kept = [.. patches];
        foreach (RasterPatch patch in kept)
        {
            if (patch.Region.IsEmpty || patch.Pixels.Width != patch.Region.Width
                                     || patch.Pixels.Height != patch.Region.Height)
                throw new ArgumentException("a replacement tile does not match its region", nameof(patches));
        }

        PixelBuffer result;
        lock (baseImage._materializeGate)
        {
            ObjectDisposedException.ThrowIf(baseImage._disposed, baseImage);
            if (baseImage._scan0 == 0 && baseImage._base is PixelBuffer root
                                      && baseImage._patches is RasterPatch[] earlier)
            {
                // Keep a flat replacement list rather than a linked snapshot per stroke. A later
                // whole-tile patch makes the same earlier tile unreachable, so omit that metadata
                // while all other pixel buffers remain shared with history.
                var replacing = new HashSet<PixelRect>(kept.Select(patch => patch.Region));
                RasterPatch[] combined = [.. earlier.Where(patch => !replacing.Contains(patch.Region)), .. kept];
                result = new PixelBuffer(root, combined);
            }
            else
            {
                result = new PixelBuffer(baseImage, kept);
            }
        }
        Interlocked.Increment(ref s_liveCount);
        return result;
    }

    /// <summary>Copies <paramref name="source"/> row by row, honouring its own stride.</summary>
    public static PixelBuffer FromRows(ReadOnlySpan<byte> source, int width, int height, int sourceStride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceStride, width * 4);
        if (source.Length < (long)sourceStride * height)
            throw new ArgumentException("source is shorter than height × sourceStride", nameof(source));

        PixelBuffer buffer = Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            source.Slice(y * sourceStride, width * 4)
                  .CopyTo(new Span<byte>((byte*)buffer._scan0 + (long)y * buffer.Stride, width * 4));
        }
        return buffer;
    }

    /// <summary>The row at <paramref name="y"/>, as writable bytes.</summary>
    /// <remarks>
    /// Writable so a buffer can be filled while it is still being built — before any other holder
    /// can see it. Once a buffer is shared, treat it as read-only; that assumption is what lets
    /// history snapshots skip copying.
    /// </remarks>
    public Span<byte> Row(int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        EnsureMaterialized();
        return RawRow(y);
    }

    /// <summary>Takes a share of this buffer. Pair every call with <see cref="Release"/>.</summary>
    public PixelBuffer Retain()
    {
        int count = Interlocked.Increment(ref _references);
        if (count <= 1)
        {
            Interlocked.Decrement(ref _references);
            throw new ObjectDisposedException(nameof(PixelBuffer));
        }
        return this;
    }

    /// <summary>Gives up a share. The memory goes back when the last one does.</summary>
    public void Release()
    {
        int count = Interlocked.Decrement(ref _references);
        if (count > 0) return;
        if (count < 0) throw new InvalidOperationException("PixelBuffer released more often than retained");

        lock (_materializeGate)
        {
            if (_disposed) return;
            _disposed = true;

            nint scan0 = Interlocked.Exchange(ref _scan0, 0);
            if (scan0 != 0)
            {
                NativeMemory.AlignedFree((void*)scan0);
                Interlocked.Add(ref s_liveBytes, -ByteCount);
            }

            ReleaseSources();
            Interlocked.Decrement(ref s_liveCount);
        }
    }

    /// <summary>How many holders this buffer has. For tests and diagnostics.</summary>
    public int ReferenceCount => Volatile.Read(ref _references);

    /// <summary>Gives up a share, so a buffer can be held by <c>using</c>.</summary>
    /// <remarks>
    /// Disposing is releasing: it says this holder is finished, not that the pixels are gone. They
    /// go when the last holder does.
    /// </remarks>
    void IDisposable.Dispose() => Release();

    /// <summary>Copies a region without forcing a deferred whole image into memory.</summary>
    internal PixelBuffer CopyRegion(PixelRect region)
    {
        if (region.IsEmpty) throw new ArgumentException("an empty region", nameof(region));
        PixelBuffer result = Allocate(region.Width, region.Height);
        try
        {
            CopyInto(result, region);
            return result;
        }
        catch
        {
            result.Release();
            throw;
        }
    }

    private void EnsureMaterialized()
    {
        // Sampling a filter calls Row several times per output pixel. Once a buffer is already
        // materialized there is nothing to protect, and taking this monitor millions of times
        // makes parallel Liquify workers serialize behind one lock. The slow path still owns all
        // allocation and deferred-patch state transitions.
        if (Volatile.Read(ref _scan0) != 0) return;

        lock (_materializeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_scan0 != 0) return;

            nuint bytes = checked((nuint)Stride * (nuint)Height);
            nint scan0 = (nint)NativeMemory.AlignedAlloc(bytes, RowAlignment);
            NativeMemory.Clear((void*)scan0, bytes);
            _scan0 = scan0;
            Interlocked.Add(ref s_liveBytes, (long)bytes);

            try
            {
                _base!.CopyInto(this, new PixelRect(0, 0, Width, Height));
                foreach (RasterPatch patch in _patches!) ApplyPatch(this, new PixelRect(0, 0, Width, Height), patch);
                ReleaseSources();
            }
            catch
            {
                NativeMemory.AlignedFree((void*)_scan0);
                _scan0 = 0;
                Interlocked.Add(ref s_liveBytes, -(long)bytes);
                throw;
            }
        }
    }

    /// <summary>
    /// Copies <paramref name="sourceRegion"/> into a same-sized destination whose origin is zero.
    /// The destination is already clear, so pixels outside this buffer need no work.
    /// </summary>
    private void CopyInto(PixelBuffer destination, PixelRect sourceRegion)
    {
        lock (_materializeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_scan0 != 0)
            {
                PixelRect overlap = sourceRegion.Intersect(new PixelRect(0, 0, Width, Height));
                for (int y = overlap.Y; y < overlap.Bottom; y++)
                {
                    ReadOnlySpan<byte> from = RawRow(y).Slice(overlap.X * 4, overlap.Width * 4);
                    Span<byte> to = destination.RawRow(y - sourceRegion.Y)
                        .Slice((overlap.X - sourceRegion.X) * 4, overlap.Width * 4);
                    from.CopyTo(to);
                }
                return;
            }

            _base!.CopyInto(destination, sourceRegion);
            foreach (RasterPatch patch in _patches!) ApplyPatch(destination, sourceRegion, patch);
        }
    }

    private static void ApplyPatch(PixelBuffer destination, PixelRect sourceRegion, RasterPatch patch)
    {
        PixelRect overlap = patch.Region.Intersect(sourceRegion);
        if (overlap.IsEmpty) return;

        for (int y = overlap.Y; y < overlap.Bottom; y++)
        {
            ReadOnlySpan<byte> from = patch.Pixels.Row(y - patch.Region.Y)
                .Slice((overlap.X - patch.Region.X) * 4, overlap.Width * 4);
            Span<byte> to = destination.RawRow(y - sourceRegion.Y)
                .Slice((overlap.X - sourceRegion.X) * 4, overlap.Width * 4);
            from.CopyTo(to);
        }
    }

    private Span<byte> RawRow(int y) =>
        new((byte*)_scan0 + (long)y * Stride, Width * 4);

    private void ReleaseSources()
    {
        PixelBuffer? baseImage = _base;
        RasterPatch[]? patches = _patches;
        _base = null;
        _patches = null;

        baseImage?.Release();
        if (patches is null) return;
        foreach (RasterPatch patch in patches) patch.Pixels.Release();
    }
}
