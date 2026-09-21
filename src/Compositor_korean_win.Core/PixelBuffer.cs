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
public sealed unsafe class PixelBuffer
{
    private nint _scan0;
    private int _references = 1;

    private PixelBuffer(nint scan0, int width, int height, int stride)
    {
        _scan0 = scan0;
        Width = width;
        Height = height;
        Stride = stride;
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
            ObjectDisposedException.ThrowIf(_scan0 == 0, this);
            return _scan0;
        }
    }

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
        return new Span<byte>((byte*)Scan0 + (long)y * Stride, Width * 4);
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

        nint scan0 = Interlocked.Exchange(ref _scan0, 0);
        if (scan0 == 0) return;

        NativeMemory.AlignedFree((void*)scan0);
        Interlocked.Decrement(ref s_liveCount);
        Interlocked.Add(ref s_liveBytes, -ByteCount);
    }

    /// <summary>How many holders this buffer has. For tests and diagnostics.</summary>
    public int ReferenceCount => Volatile.Read(ref _references);
}
