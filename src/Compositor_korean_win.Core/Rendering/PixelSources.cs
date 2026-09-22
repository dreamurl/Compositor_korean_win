namespace Compositor_korean_win.Core;

/// <summary>A plain buffer, drawn as it is.</summary>
public sealed class BufferSource(PixelBuffer buffer) : IPixelSource
{
    public PixelBuffer Buffer { get; } = buffer;

    /// <summary>
    /// Whether a backend may keep what it made of this buffer for the next frame.
    /// </summary>
    /// <remarks>
    /// False for a buffer made for one draw and released straight after — a composited clipping
    /// group, an adjusted frame. A cache keyed by the buffer could never hit on one of those again,
    /// and every frame would push a window's worth of dead uploads into it.
    /// </remarks>
    public bool Cacheable { get; init; } = true;

    public int Width => Buffer.Width;
    public int Height => Buffer.Height;

    public PixelBuffer Materialize(PixelRect region) => PixelRegion.Copy(Buffer, region);
}

/// <summary>One replacement tile: pixels that stand in for part of a layer.</summary>
public readonly record struct RasterPatch(PixelRect Region, PixelBuffer Pixels);

/// <summary>
/// A layer held as an unchanged image plus replacement tiles.
/// </summary>
/// <remarks>
/// <para>
/// This is the shape docs/windows-port.md §2.3 calls tile replacement, and the reason a brush
/// stroke on a hundred-megapixel layer costs the tiles it touched rather than the layer. The base
/// is never modified; a patch is another immutable buffer laid over part of it, and a patch that
/// covers an older one wins.
/// </para>
/// <para>
/// What makes this safe to draw from is the pyramid: because halving averages a 2×2 block and
/// reaches nothing outside it, a region materialised on its own and then reduced gives exactly the
/// pixels that region would have had in the whole image reduced. So the renderer can materialise
/// only what it draws. Upstream needs a margin of 16·2^level pixels for the same guarantee, because
/// Lanczos reaches; here the margin only has to cover the final resample.
/// </para>
/// </remarks>
public sealed class LayerRaster(PixelBuffer baseImage, IReadOnlyList<RasterPatch> patches) : IPixelSource
{
    public PixelBuffer Base { get; } = baseImage;
    public IReadOnlyList<RasterPatch> Patches { get; } = patches;

    public int Width => Base.Width;
    public int Height => Base.Height;

    /// <summary>True when nothing has been painted, so this draws like a plain buffer.</summary>
    public bool IsPlain => Patches.Count == 0;

    public PixelBuffer Materialize(PixelRect region)
    {
        PixelBuffer result = PixelRegion.Copy(Base, region);

        // Later patches win, so they go on in order.
        foreach (RasterPatch patch in Patches)
        {
            PixelRect overlap = patch.Region.Intersect(region);
            if (overlap.IsEmpty) continue;

            for (int y = overlap.Y; y < overlap.Bottom; y++)
            {
                ReadOnlySpan<byte> source = patch.Pixels.Row(y - patch.Region.Y)
                    .Slice((overlap.X - patch.Region.X) * 4, overlap.Width * 4);
                Span<byte> target = result.Row(y - region.Y).Slice((overlap.X - region.X) * 4, overlap.Width * 4);
                source.CopyTo(target);
            }
        }

        return result;
    }

    /// <summary>The whole layer as one buffer — what tile replacement exists to avoid.</summary>
    /// <remarks>For tests, and for the moment a stroke is committed.</remarks>
    public PixelBuffer Flatten() => Materialize(new PixelRect(0, 0, Width, Height));
}

/// <summary>Copying rectangles of pixels, with anything outside the source left transparent.</summary>
public static class PixelRegion
{
    public static PixelBuffer Copy(PixelBuffer source, PixelRect region)
    {
        if (region.IsEmpty) throw new ArgumentException("an empty region", nameof(region));

        PixelBuffer result = PixelBuffer.Allocate(region.Width, region.Height);
        PixelRect overlap = region.Intersect(new PixelRect(0, 0, source.Width, source.Height));
        if (overlap.IsEmpty) return result;

        for (int y = overlap.Y; y < overlap.Bottom; y++)
        {
            ReadOnlySpan<byte> from = source.Row(y).Slice(overlap.X * 4, overlap.Width * 4);
            Span<byte> to = result.Row(y - region.Y).Slice((overlap.X - region.X) * 4, overlap.Width * 4);
            from.CopyTo(to);
        }

        return result;
    }
}
