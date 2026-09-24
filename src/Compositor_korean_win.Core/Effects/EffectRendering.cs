namespace Compositor_korean_win.Core;

/// <summary>One effect ready to composite: pixels, where they go, and how.</summary>
/// <param name="Above">Drawn over the layer (a stroke) rather than beneath it (a shadow, a glow).</param>
public sealed record EffectDraw(PixelBuffer Pixels, LayerTransform Placement, double Opacity,
                                LayerBlendMode Blend, bool Above);

/// <summary>
/// Draws a layer's effects from its shape.
/// </summary>
/// <remarks>
/// <para>
/// Everything is worked out in the layer's own pixel grid, padded by as far as the widest effect
/// reaches, and placed on the document with the layer's own placement grown by that padding — so a
/// rotated, flipped or scaled layer carries its effects exactly as it carries its pixels. Sizes
/// arrive in document pixels and are divided by the layer's scale on the way in.
/// </para>
/// <para>
/// A stroke is a band of a signed distance from the layer's edge: a distance transform gives each
/// pixel how far it is from the nearest pixel on the other side, and the pixel's own coverage
/// places the edge inside it, so a one-pixel stroke on antialiased type stays antialiased.
/// A shadow's or glow's spread grows the shape by the same distance before the blur.
/// </para>
/// <para>
/// Effects are cached by the layer's buffers and settings (<see cref="For"/>): the shape only
/// changes when the pixels do, and the pixels are immutable, so a frame that pans or zooms reuses
/// what the last one built rather than blurring the layer again.
/// </para>
/// </remarks>
public static class EffectRendering
{
    /// <summary>A padded grid bigger than this draws no effects rather than run out of memory.</summary>
    private const long MaximumPixels = 120_000_000;

    private const int CacheEntries = 32;

    private sealed record CacheKey(PixelBuffer Image, PixelBuffer? Mask, LayerEffects Effects, double Scale);

    private sealed class CacheEntry(CacheKey key, List<(PixelBuffer Pixels, int Pad, Point Offset, double Opacity, LayerBlendMode Blend, bool Above)> built)
    {
        public CacheKey Key { get; } = key;
        public List<(PixelBuffer Pixels, int Pad, Point Offset, double Opacity, LayerBlendMode Blend, bool Above)> Built { get; } = built;
    }

    private static readonly LinkedList<CacheEntry> s_cache = new();
    private static readonly Lock s_lock = new();

    /// <summary>Whether a layer has anything for <see cref="For"/> to draw.</summary>
    public static bool HasEffects(ImageLayer layer) =>
        layer.Effects is { IsVisible: true } && layer.Image is not null && !layer.IsGroup && layer.Adjustment is null;

    /// <summary>
    /// A layer's effects, bottom to top within each side of the layer.
    /// </summary>
    /// <remarks>
    /// Each buffer is retained for the caller, who releases it once drawn; the cache keeps its own
    /// reference, so a later frame still finds it.
    /// </remarks>
    public static List<EffectDraw> For(ImageLayer layer)
    {
        var result = new List<EffectDraw>();
        if (!HasEffects(layer)) return result;

        PixelBuffer image = layer.Image!;
        PixelBuffer? mask = layer.Mask is { IsEnabled: true, Placement: null } own ? own.Coverage : null;
        double scale = Scale(layer.Transform, image);
        var key = new CacheKey(image, mask, layer.Effects!, Math.Round(scale, 6));

        List<(PixelBuffer Pixels, int Pad, Point Offset, double Opacity, LayerBlendMode Blend, bool Above)> built;
        lock (s_lock)
        {
            CacheEntry? hit = null;
            for (LinkedListNode<CacheEntry>? node = s_cache.First; node is not null; node = node.Next)
            {
                if (!ReferenceEquals(node.Value.Key.Image, image) || !ReferenceEquals(node.Value.Key.Mask, mask)
                    || node.Value.Key.Effects != key.Effects || node.Value.Key.Scale != key.Scale) continue;
                hit = node.Value;
                s_cache.Remove(node);
                s_cache.AddFirst(node);
                break;
            }

            if (hit is null)
            {
                hit = new CacheEntry(key, Build(image, mask, layer.Effects!, scale));
                s_cache.AddFirst(hit);
                while (s_cache.Count > CacheEntries)
                {
                    foreach (var item in s_cache.Last!.Value.Built) item.Pixels.Release();
                    s_cache.RemoveLast();
                }
            }

            built = hit.Built;
            foreach (var item in built) item.Pixels.Retain();
        }

        foreach ((PixelBuffer pixels, int pad, Point offset, double opacity, LayerBlendMode blend, bool above) in built)
        {
            LayerTransform placement = LayerGeometry.Place(layer.Transform,
                new PixelRect(-pad, -pad, image.Width + 2 * pad, image.Height + 2 * pad), image.Width, image.Height);
            placement = placement with { Origin = new Point(placement.Origin.X + offset.X, placement.Origin.Y + offset.Y) };
            result.Add(new EffectDraw(pixels, placement, opacity, blend, above));
        }
        return result;
    }

    /// <summary>Drops every cached effect, releasing its pixels.</summary>
    public static void ClearCache()
    {
        lock (s_lock)
        {
            foreach (CacheEntry entry in s_cache)
                foreach (var item in entry.Built) item.Pixels.Release();
            s_cache.Clear();
        }
    }

    /// <summary>Document pixels per layer pixel, taking both axes into account.</summary>
    public static double Scale(LayerTransform placement, PixelBuffer image)
    {
        double sx = Math.Abs(placement.Size.Width) / Math.Max(1, image.Width);
        double sy = Math.Abs(placement.Size.Height) / Math.Max(1, image.Height);
        double scale = Math.Sqrt(sx * sy);
        return double.IsFinite(scale) && scale > 1e-6 ? scale : 1;
    }

    private static List<(PixelBuffer, int, Point, double, LayerBlendMode, bool)> Build(
        PixelBuffer image, PixelBuffer? mask, LayerEffects effects, double scale)
    {
        var built = new List<(PixelBuffer, int, Point, double, LayerBlendMode, bool)>();

        ShadowEffect? shadow = effects.Shadow is { Enabled: true, Opacity: > 0 } s ? s : null;
        GlowEffect? glow = effects.Glow is { Enabled: true, Opacity: > 0 } g ? g : null;
        StrokeEffect? stroke = effects.Stroke is { Enabled: true, Opacity: > 0 } k ? k : null;

        double reach = 0;
        if (shadow is not null) reach = Math.Max(reach, shadow.Size / scale * 1.5);
        if (glow is not null) reach = Math.Max(reach, glow.Size / scale * 1.5);
        if (stroke is not null) reach = Math.Max(reach, stroke.Size / scale);
        int pad = (int)Math.Ceiling(reach) + 2;

        int width = image.Width + 2 * pad, height = image.Height + 2 * pad;
        if ((long)width * height > MaximumPixels) return built;

        float[] alpha = Alpha(image, mask, pad, width, height);
        float[]? signed = null;
        float[] Signed() => signed ??= SignedDistance(alpha, width, height);

        // Beneath the layer, bottom first: the shadow, then the glow over it.
        if (shadow is not null)
        {
            double size = shadow.Size / scale;
            float[] shape = shadow.Spread > 0 ? Grown(Signed(), size * shadow.Spread) : alpha;
            PixelBuffer pixels = Blurred(shape, width, height, shadow.Colour, size * (1 - shadow.Spread) / 2.5);
            built.Add((pixels, pad, shadow.Offset, shadow.Opacity, shadow.Blend, false));
        }

        if (glow is not null)
        {
            double size = glow.Size / scale;
            float[] shape = glow.Spread > 0 ? Grown(Signed(), size * glow.Spread) : alpha;
            PixelBuffer pixels = Blurred(shape, width, height, glow.Colour, size * (1 - glow.Spread) / 2.5);
            built.Add((pixels, pad, Point.Zero, glow.Opacity, glow.Blend, false));
        }

        if (stroke is not null)
        {
            double size = stroke.Size / scale;
            (double low, double high) = stroke.Position switch
            {
                StrokePosition.Inside => (-size, 0.0),
                StrokePosition.Center => (-size / 2, size / 2),
                _ => (0.0, size),
            };
            float[] distance = Signed();
            var band = new float[distance.Length];
            for (int i = 0; i < band.Length; i++)
            {
                double d = distance[i];
                band[i] = (float)Math.Max(0, Math.Min(high, d + 0.5) - Math.Max(low, d - 0.5));
            }
            built.Add((Painted(band, width, height, stroke.Colour), pad, Point.Zero, stroke.Opacity,
                       LayerBlendMode.Normal, true));
        }

        return built;
    }

    /// <summary>The layer's coverage — its alpha, through its own mask — in a padded grid.</summary>
    private static float[] Alpha(PixelBuffer image, PixelBuffer? mask, int pad, int width, int height)
    {
        var alpha = new float[width * height];
        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<byte> row = image.Row(y);
            ReadOnlySpan<byte> maskRow = mask is null ? default : mask.Row(Math.Min(mask.Height - 1, y * mask.Height / image.Height));
            int target = (y + pad) * width + pad;
            for (int x = 0; x < image.Width; x++)
            {
                float a = row[x * 4 + 3] / 255f;
                if (mask is not null) a *= maskRow[Math.Min(mask.Width - 1, x * mask.Width / image.Width) * 4] / 255f;
                alpha[target + x] = a;
            }
        }
        return alpha;
    }

    /// <summary>
    /// Each pixel's distance from the shape's edge, positive outside it and negative within, with
    /// the pixel's own coverage placing the edge inside a pixel the edge crosses.
    /// </summary>
    public static float[] SignedDistance(float[] alpha, int width, int height)
    {
        const float Far = 1e20f;
        var inside = new float[alpha.Length];
        var outside = new float[alpha.Length];
        for (int i = 0; i < alpha.Length; i++)
        {
            bool within = alpha[i] >= 0.5f;
            // Squared distance to the nearest pixel inside, and to the nearest outside.
            outside[i] = within ? 0 : Far;
            inside[i] = within ? Far : 0;
        }

        DistanceTransform(outside, width, height);
        DistanceTransform(inside, width, height);

        var signed = new float[alpha.Length];
        for (int i = 0; i < alpha.Length; i++)
        {
            float a = alpha[i];
            signed[i] = a >= 0.5f
                ? -(MathF.Sqrt(inside[i]) - 0.5f) + (1 - a)
                : MathF.Sqrt(outside[i]) - 0.5f - a;
        }
        return signed;
    }

    /// <summary>The shape grown by <paramref name="radius"/> pixels, antialiased.</summary>
    private static float[] Grown(float[] signed, double radius)
    {
        var grown = new float[signed.Length];
        for (int i = 0; i < grown.Length; i++)
            grown[i] = (float)Math.Clamp(radius - signed[i] + 0.5, 0, 1);
        return grown;
    }

    private static PixelBuffer Blurred(float[] shape, int width, int height, Rgba colour, double sigma)
    {
        PixelBuffer painted = Painted(shape, width, height, colour);
        if (sigma < 0.3) return painted;
        try
        {
            return PixelFilters.GaussianBlur(painted, sigma);
        }
        finally
        {
            painted.Release();
        }
    }

    private static PixelBuffer Painted(float[] coverage, int width, int height, Rgba colour)
    {
        PixelBuffer result = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = result.Row(y);
            for (int x = 0; x < width; x++)
            {
                float c = coverage[y * width + x];
                if (c <= 0) continue;
                int level = (int)MathF.Round(Math.Min(1, c) * 255);
                Span<byte> pixel = row.Slice(x * 4, 4);
                pixel[0] = (byte)((colour.R * level + 127) / 255);
                pixel[1] = (byte)((colour.G * level + 127) / 255);
                pixel[2] = (byte)((colour.B * level + 127) / 255);
                pixel[3] = (byte)level;
            }
        }
        return result;
    }

    /// <summary>
    /// Squared Euclidean distance transform in place: columns, then rows, each by the lower envelope
    /// of parabolas (Felzenszwalb and Huttenlocher) — exact, and linear in the pixel count.
    /// </summary>
    private static void DistanceTransform(float[] grid, int width, int height)
    {
        int longest = Math.Max(width, height);
        var f = new float[longest];
        var d = new float[longest];
        var v = new int[longest];
        var z = new double[longest + 1];

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++) f[y] = grid[y * width + x];
            Envelope(f, height, d, v, z);
            for (int y = 0; y < height; y++) grid[y * width + x] = d[y];
        }

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++) f[x] = grid[row + x];
            Envelope(f, width, d, v, z);
            for (int x = 0; x < width; x++) grid[row + x] = d[x];
        }
    }

    private static void Envelope(float[] f, int n, float[] d, int[] v, double[] z)
    {
        // Doubles for the intersections: a squared column index loses whole pixels in a float
        // once an image is a few thousand pixels across.
        int k = 0;
        v[0] = 0;
        z[0] = double.NegativeInfinity;
        z[1] = double.PositiveInfinity;

        for (int q = 1; q < n; q++)
        {
            double s = Intersection(f, v[k], q);
            while (s <= z[k])
            {
                k--;
                s = Intersection(f, v[k], q);
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }

        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            int p = v[k];
            d[q] = (float)((double)(q - p) * (q - p) + f[p]);
        }

        static double Intersection(float[] f, int p, int q) =>
            ((f[q] + (double)q * q) - (f[p] + (double)p * p)) / (2.0 * q - 2.0 * p);
    }
}
