namespace Compositor_korean_win.Core;

/// <summary>
/// Where a buffer's pixels sit on the document, for the one adjustment that cares: Grain.
/// </summary>
/// <remarks>
/// Pixel (x, y) is centred on (<see cref="OriginX"/> + (x + 0.5) × <see cref="UnitsPerPixel"/>,
/// likewise for y). Grain's pattern is fixed in document space, so a frame of the canvas has to say
/// which part of the document it is showing, and at what scale, or panning would slide the grain
/// across the picture and zooming would change its size.
/// </remarks>
public readonly record struct PixelPlacement(double OriginX, double OriginY, double UnitsPerPixel)
{
    /// <summary>Document pixels, as themselves.</summary>
    public static PixelPlacement Document => new(0, 0, 1);

    /// <summary>The pixels of a surface drawn through <paramref name="projection"/>.</summary>
    public static PixelPlacement For(CanvasProjection projection) =>
        new(-projection.Offset.X / projection.Scale, -projection.Offset.Y / projection.Scale, 1 / projection.Scale);

    /// <summary>The same placement, starting at pixel (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public PixelPlacement Offset(int x, int y) =>
        this with { OriginX = OriginX + x * UnitsPerPixel, OriginY = OriginY + y * UnitsPerPixel };
}

/// <summary>
/// The six adjustments, run over pixels.
/// </summary>
/// <remarks>
/// <para>
/// This is upstream's <c>LayerAdjustment.apply</c>. Five of the six are C kernels upstream already
/// had — Levels, Curves and Exposure all reduce to a table per channel for <c>levels_apply</c>,
/// Gradient Map and Grain have kernels of their own — and are called here as they are. Only
/// Hue/Saturation went through Core Image, as a colour cube (<see cref="HueSaturationFilter"/>).
/// </para>
/// <para>
/// So the design table's line "<c>CIColorCube</c> → <c>D2D1LookupTable3D</c>" was right about the
/// one filter it names, and the rest never touched Core Image at all. Running every adjustment on
/// the CPU, in Core, is what upstream does, and it keeps the two backends giving the same pixels —
/// Direct2D's effects would each bring their own rounding and their own idea of premultiplication.
/// </para>
/// <para>
/// Levels is one place this parts from upstream on purpose. Upstream's <c>LevelsFilter</c>
/// unpremultiplies a soft edge before calling <c>levels_apply</c>, but the kernel divides by alpha
/// itself, so a half-transparent pixel is divided twice and comes out brighter than it should.
/// Curves and Exposure call the same kernel on premultiplied pixels directly, and so does Levels
/// here.
/// </para>
/// </remarks>
public static class AdjustmentRendering
{
    private static long s_pixelsAdjusted;

    /// <summary>
    /// Pixels run through an adjustment since the process started, for the M5 report: it is how the
    /// bench shows that a frame adjusts the window's pixels and not the document's.
    /// </summary>
    public static long PixelsAdjusted => Interlocked.Read(ref s_pixelsAdjusted);

    /// <summary>
    /// Settings that would leave every pixel exactly as it is.
    /// </summary>
    /// <remarks>
    /// Worth knowing because the compositor can then skip reading the frame back altogether. It
    /// errs towards false: Gradient Map is never an identity, whatever its colours.
    /// </remarks>
    public static bool IsIdentity(LayerAdjustment adjustment) => adjustment.Kind switch
    {
        AdjustmentKind.Hsv => HueSaturationFilter.IsIdentity(adjustment.ResolvedHsv),
        AdjustmentKind.Levels => adjustment.Levels.IsIdentity,
        AdjustmentKind.Curves => !adjustment.Curves.IsValid || adjustment.Curves.Channels.All(IsDiagonal),
        AdjustmentKind.Exposure => adjustment.Exposure.Normalized is { Exposure: 0.0, Offset: 0.0, Gamma: 1.0 },
        AdjustmentKind.GradientMap => false,
        AdjustmentKind.Grain => !(adjustment.Grain.Normalized.Amount > 0),
        _ => true,
    };

    private static bool IsDiagonal(EquatableList<CurvePoint> points) =>
        points.All(point => point.X == point.Y);

    /// <summary>Adjusts every pixel of <paramref name="pixels"/> in place.</summary>
    /// <remarks>
    /// The buffer must be one nobody else holds: pixels are otherwise immutable, and this writes.
    /// </remarks>
    public static void Apply(LayerAdjustment adjustment, PixelBuffer pixels, PixelPlacement placement) =>
        Apply(adjustment, pixels, new PixelRect(0, 0, pixels.Width, pixels.Height), placement);

    /// <summary>Adjusts <paramref name="region"/> of <paramref name="pixels"/> in place.</summary>
    /// <param name="placement">Where pixel (0, 0) of the whole buffer sits on the document.</param>
    public static void Apply(LayerAdjustment adjustment, PixelBuffer pixels, PixelRect region, PixelPlacement placement)
    {
        region = region.Intersect(new PixelRect(0, 0, pixels.Width, pixels.Height));
        if (region.IsEmpty || IsIdentity(adjustment)) return;
        Interlocked.Add(ref s_pixelsAdjusted, (long)region.Width * region.Height);

        switch (adjustment.Kind)
        {
            case AdjustmentKind.Hsv:
                HueSaturationFilter.Apply(pixels, region, HueSaturationFilter.Cube(adjustment.ResolvedHsv));
                break;

            case AdjustmentKind.Levels:
                ApplyTables(pixels, region, LevelsTables(adjustment.Levels));
                break;

            case AdjustmentKind.Curves:
                ApplyTables(pixels, region, adjustment.Curves.Table());
                break;

            case AdjustmentKind.Exposure:
                float[] curve = adjustment.Exposure.Normalized.Table();
                ApplyTables(pixels, region, [.. curve, .. curve, .. curve]);
                break;

            case AdjustmentKind.GradientMap:
                ApplyGradientMap(pixels, region, adjustment.GradientMap.Normalized.Table());
                break;

            case AdjustmentKind.Grain:
                ApplyGrain(pixels, region, adjustment.Grain.Normalized, placement.Offset(region.X, region.Y));
                break;
        }
    }

    /// <summary>
    /// The 3 × 256 table <c>levels_apply</c> takes: each colour channel's range, then the composite.
    /// </summary>
    public static float[] LevelsTables(LevelsSettings settings)
    {
        var tables = new float[3 * 256];
        ReadOnlySpan<LevelsChannel> channels = [LevelsChannel.Red, LevelsChannel.Green, LevelsChannel.Blue];
        for (int channel = 0; channel < 3; channel++)
            for (int value = 0; value < 256; value++)
                tables[channel * 256 + value] = (float)settings.Apply(value / 255.0, channels[channel]);
        return tables;
    }

    /// <summary>Blends two buffers of one size, by a weight taken from a third's alpha.</summary>
    /// <remarks>
    /// <para>
    /// How an adjustment layer's opacity and mask take effect: the result is the frame beneath,
    /// moved towards its adjusted self by the weight. Linear interpolation of premultiplied colour is
    /// exact — both inputs have the same alpha here, and so does what comes out.
    /// </para>
    /// <para>
    /// Drawing the adjusted frame over the original with the weight as opacity would not be: over a
    /// soft edge that is two translucent copies source-over, and the edge thickens. Upstream draws
    /// it that way, and here, as with Levels, its result is not reproduced on purpose.
    /// </para>
    /// </remarks>
    /// <param name="adjusted">Receives the result.</param>
    /// <param name="weights">Its alpha is the weight; null means everywhere at <paramref name="opacity"/>.</param>
    public static void Mix(PixelBuffer original, PixelBuffer adjusted, PixelBuffer? weights, double opacity = 1)
    {
        if (original.Width != adjusted.Width || original.Height != adjusted.Height)
            throw new ArgumentException("the two frames differ in size", nameof(adjusted));
        if (weights is not null && (weights.Width != adjusted.Width || weights.Height != adjusted.Height))
            throw new ArgumentException("the weights differ in size", nameof(weights));

        int scale = (int)Math.Round(Math.Clamp(opacity, 0, 1) * 255, MidpointRounding.AwayFromZero);

        for (int y = 0; y < adjusted.Height; y++)
        {
            ReadOnlySpan<byte> from = original.Row(y);
            Span<byte> to = adjusted.Row(y);
            ReadOnlySpan<byte> weight = weights is null ? default : weights.Row(y);

            for (int x = 0; x < adjusted.Width; x++)
            {
                int w = weights is null ? scale : (weight[x * 4 + 3] * scale + 127) / 255;
                if (w >= 255) continue;

                int i = x * 4;
                for (int channel = 0; channel < 4; channel++)
                    to[i + channel] = (byte)((from[i + channel] * (255 - w) + to[i + channel] * w + 127) / 255);
            }
        }
    }

    private static void ApplyTables(PixelBuffer pixels, PixelRect region, float[] tables)
    {
        // levels_apply takes a pixel count, not a stride, so rows go one at a time: rows are padded
        // to 32 bytes, and handing it the whole buffer would read the padding as pixels.
        unsafe
        {
            fixed (float* table = tables)
            {
                for (int y = region.Y; y < region.Bottom; y++)
                    Kernels.LevelsApply(Address(pixels, region.X, y), (nuint)region.Width, (nint)table);
            }
        }
    }

    private static void ApplyGradientMap(PixelBuffer pixels, PixelRect region, byte[] colours)
    {
        unsafe
        {
            fixed (byte* table = colours)
            {
                Kernels.AdjustGradientMap(Address(pixels, region.X, region.Y), (nuint)region.Width,
                                          (nuint)region.Height, (nuint)pixels.Stride, (nint)table);
            }
        }
    }

    private static void ApplyGrain(PixelBuffer pixels, PixelRect region, GrainSettings grain, PixelPlacement placement) =>
        Kernels.AdjustGrain(Address(pixels, region.X, region.Y), (nuint)region.Width, (nuint)region.Height,
                            (nuint)pixels.Stride, grain.Amount, grain.Size, grain.Roughness, grain.Seed,
                            placement.OriginX, placement.OriginY, placement.UnitsPerPixel);

    private static nint Address(PixelBuffer pixels, int x, int y) =>
        pixels.Scan0 + (nint)((long)y * pixels.Stride + x * 4L);
}
