namespace Compositor_korean_win.Core;

/// <summary>Coverage that restricts what a draw may touch, placed on the document.</summary>
/// <remarks>
/// A folder's mask and a clipping mask's borrowed alpha both reduce to this, and so does a layer's
/// own mask once it has been moved apart from its layer. Several clips multiply.
/// </remarks>
public readonly record struct MaskClip(PixelBuffer Coverage, LayerTransform Placement);

/// <summary>One layer, ready to be drawn.</summary>
public sealed record LayerDraw
{
    /// <summary>The layer's pixels, premultiplied RGBA.</summary>
    public required IPixelSource Source { get; init; }

    /// <summary>Where those pixels go on the document.</summary>
    public required LayerTransform Placement { get; init; }

    public double Opacity { get; init; } = 1;
    public LayerBlendMode Blend { get; init; } = LayerBlendMode.Normal;

    /// <summary>
    /// The layer's own mask, in the layer's own pixel grid — so the placement applies to both.
    /// </summary>
    public PixelBuffer? Mask { get; init; }

    /// <summary>Folder masks and clipping coverage, in document space. They multiply.</summary>
    public IReadOnlyList<MaskClip> Clips { get; init; } = [];
}

/// <summary>
/// Pixels to draw from, which may be a plain buffer or a buffer with replacement tiles.
/// </summary>
/// <remarks>
/// The distinction exists so that a layer being painted does not have to be rebuilt whole on every
/// mouse move (docs/windows-port.md §2.3). A source is asked only for the regions actually drawn.
/// </remarks>
public interface IPixelSource
{
    int Width { get; }
    int Height { get; }

    /// <summary>
    /// The pixels of <paramref name="region"/>, which may fall partly outside the source — anything
    /// outside is transparent. The caller owns the result and releases it.
    /// </summary>
    PixelBuffer Materialize(PixelRect region);
}

/// <summary>A rectangle of whole pixels, half-open on the right and bottom.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromBounds(int left, int top, int right, int bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public PixelRect Intersect(PixelRect other) => FromBounds(
        Math.Max(X, other.X), Math.Max(Y, other.Y),
        Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));

    public PixelRect Inflate(int margin) =>
        new(X - margin, Y - margin, Width + margin * 2, Height + margin * 2);
}

/// <summary>Somewhere to draw layers, in document pixels.</summary>
public interface IRenderSurface : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Resets every pixel to transparent.</summary>
    void Clear();

    void Draw(LayerDraw draw);

    /// <summary>The composited pixels. The caller owns the result and releases it.</summary>
    PixelBuffer Read();

    /// <summary>
    /// Replaces everything on the surface with <paramref name="pixels"/>, which must be its size.
    /// </summary>
    /// <remarks>
    /// A clipping group needs this: the base is drawn, its alpha is lifted off and its colour made
    /// opaque, and that has to go back onto the surface before the clipped layers draw over it.
    /// </remarks>
    void Write(PixelBuffer pixels);
}

/// <summary>
/// What the renderer is built on.
/// </summary>
/// <remarks>
/// docs/windows-port.md §4.2 keeps this boundary because upstream already had one — its
/// <c>LayerRenderer</c> and <c>TiledLayerRenderer</c> are the only places that speak to Core
/// Graphics. Two implementations exist for the same reason the boundary does: Direct2D for what
/// ships, and a software rasteriser that needs no GPU and no display, so the compositing rules can
/// be tested for what they are rather than for what one driver does with them.
/// </remarks>
public interface IRenderBackend : IDisposable
{
    string Name { get; }

    IRenderSurface CreateSurface(int width, int height);

    /// <summary>
    /// <paramref name="source"/> reduced by <paramref name="level"/> exact halvings.
    /// </summary>
    PixelBuffer Downsample(PixelBuffer source, int level);
}
