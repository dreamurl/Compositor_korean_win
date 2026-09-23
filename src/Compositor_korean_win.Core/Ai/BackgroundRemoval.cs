namespace Compositor_korean_win.Core;

/// <summary>Remove Background's quality: the model's mask as it comes, or refined against the layer.</summary>
public enum BackgroundQuality
{
    Basic,
    Advanced,
}

/// <summary>Remove Background's settings, upstream's defaults and ranges.</summary>
public sealed record BackgroundSettings
{
    public BackgroundQuality Quality { get; init; } = BackgroundQuality.Basic;

    /// <summary>How far the mask reaches for the layer's own edges, 0–40 layer pixels.</summary>
    public double Refine { get; init; } = 12;

    /// <summary>How hard the mask's greys are pushed apart, 0–100 %.</summary>
    public double Contrast { get; init; } = 25;

    /// <summary>How far the mask's edge moves, −10–10 layer pixels: in when negative.</summary>
    public double ShiftEdge { get; init; }
}

/// <summary>
/// Remove Background without the model: refining the model's mask against the layer, and laying
/// it into the layer's mask — upstream's <c>SubjectRemoval</c> and <c>GuidedMatte</c>.
/// </summary>
/// <remarks>
/// <para>
/// The background is hidden behind a layer mask rather than erased, so the pixels stay and can be
/// painted back. What the model gives is where the subject is; everything the sheet offers is done
/// to that afterwards, which is why moving a slider never runs the model again.
/// </para>
/// <para>
/// The refining — a guided filter (He, Sun and Tang) with the layer as its guide, then an edge
/// shift and a contrast — is done on a copy no larger than a limit on its longer side, the radius
/// shrinking with it, then stretched back over the layer. Fine detail comes from the guide either
/// way; the limit is what keeps a preview quick and a large photograph from needing gigabytes of
/// working planes. Upstream does the same for its preview.
/// </para>
/// </remarks>
public static class BackgroundRemoval
{
    /// <summary>The longest side a preview is refined at, upstream's.</summary>
    public const int PreviewLimit = 1400;

    /// <summary>
    /// The longest side the committed mask is refined at. Upstream refines at full size; this caps
    /// the working planes at a few hundred megabytes for any photograph.
    /// </summary>
    public const int CommitLimit = 3072;

    /// <summary>
    /// The layer's new mask: the subject, refined, within the selection when there is one, and
    /// hiding whatever the layer's mask already hid. <paramref name="limit"/> sets how fine the
    /// refining is. The caller owns the result.
    /// </summary>
    /// <param name="logits">The model's answer for the layer, <paramref name="side"/> square.</param>
    /// <param name="fullSize">
    /// Stretched back over the layer's whole grid, for the mask kept; left at the refining size for
    /// a preview, which a mask may be — it covers its layer whatever its own size.
    /// </param>
    public static PixelBuffer Mask(ImageLayer layer, float[] logits, int side, BackgroundSettings settings,
                                   DocumentSelection? selection, int limit, bool fullSize = true)
    {
        if (layer.Image is not PixelBuffer image) throw new ArgumentException("a layer with pixels", nameof(layer));
        int width = image.Width, height = image.Height;

        double factor = Math.Min(1, (double)limit / Math.Max(width, height));
        int gw = Math.Max(1, (int)Math.Round(width * factor)), gh = Math.Max(1, (int)Math.Round(height * factor));

        float[] subject = Plane(SubjectMatte.Mask(logits, side, gw, gh), release: true);
        if (settings.Quality == BackgroundQuality.Advanced)
            subject = Refined(subject, Plane(Scaled(image, gw, gh), release: true), gw, gh, settings, factor);

        // What the layer's mask already hid stays hidden; outside the selection, the mask is as it was.
        float[] existing = Existing(layer, gw, gh);
        float[]? selected = selection is null
            ? null
            : [.. LayerFilters.SelectionLevels(selection, layer.Transform, width, height, 1 / factor,
                                               new PixelRect(0, 0, gw, gh)).Select(level => level / 255f)];

        for (int i = 0; i < subject.Length; i++)
        {
            float combined = subject[i] * existing[i];
            subject[i] = selected is null ? combined : existing[i] + (combined - existing[i]) * selected[i];
        }

        using PixelBuffer grid = Grey(subject, gw, gh);
        return !fullSize || gw == width && gh == height ? grid.Retain() : Scaled(grid, width, height);
    }

    /// <summary>
    /// The layer with <paramref name="mask"/> as its mask: over its own grid, enabled, and linked as
    /// its old one was. A mask placed apart is replaced outright, as upstream does.
    /// </summary>
    public static ImageLayer WithMask(ImageLayer layer, PixelBuffer mask) => layer with
    {
        Mask = new LayerMask { Coverage = mask, IsLinked = layer.Mask?.IsLinked ?? true },
    };

    /// <summary>Whether the model found anything: some of the mask is more subject than not.</summary>
    public static bool FoundSubject(float[] logits) => logits.Any(logit => logit > 0);

    /// <summary>Refine, then shift the edge, then the contrast — upstream's order.</summary>
    private static float[] Refined(float[] mask, float[] guide, int width, int height, BackgroundSettings settings,
                                   double factor)
    {
        if (settings.Refine > 0)
        {
            int radius = Math.Max(1, (int)Math.Round(settings.Refine * factor));
            mask = GuidedFilter(mask, guide, width, height, radius, 1e-4f);
        }

        if (settings.ShiftEdge != 0)
        {
            // A blur then a hard cut at the matching level moves the edge by the blur's reach.
            // Upstream blurs with a Gaussian of half the reach; three box passes of about that
            // radius come to the same spread.
            double reach = Math.Abs(settings.ShiftEdge) * factor;
            int radius = Math.Max(1, (int)Math.Round(reach / 2));
            float[] blurred = Box(Box(Box(mask, width, height, radius), width, height, radius), width, height, radius);
            float level = settings.ShiftEdge < 0 ? 0.75f : 0.25f;
            for (int i = 0; i < mask.Length; i++) mask[i] = blurred[i] >= level ? 1 : 0;
        }

        if (settings.Contrast > 0)
        {
            // 0 leaves the mask as it is; 100 is a hard cut at the middle.
            float slope = 1 / (float)Math.Max(0.02, 1 - settings.Contrast / 100 * 0.98);
            for (int i = 0; i < mask.Length; i++) mask[i] = Math.Clamp(slope * mask[i] + (1 - slope) / 2, 0, 1);
        }

        return mask;
    }

    /// <summary>
    /// <paramref name="mask"/> pulled onto the edges of <paramref name="guide"/>, both 0–1 and the
    /// same size. A bigger radius reaches further for detail; <paramref name="epsilon"/> decides
    /// how much of an edge in the guide counts, so a small one follows fine strands.
    /// </summary>
    public static float[] GuidedFilter(float[] mask, float[] guide, int width, int height, int radius, float epsilon)
    {
        int count = width * height;
        float[] meanGuide = Box(guide, width, height, radius);
        float[] meanMask = Box(mask, width, height, radius);

        var scratch = new float[count];
        for (int i = 0; i < count; i++) scratch[i] = guide[i] * guide[i];
        float[] meanSquares = Box(scratch, width, height, radius);
        for (int i = 0; i < count; i++) scratch[i] = guide[i] * mask[i];
        float[] meanProducts = Box(scratch, width, height, radius);

        // Reused in place: slope into meanSquares, offset into meanProducts.
        for (int i = 0; i < count; i++)
        {
            float variance = meanSquares[i] - meanGuide[i] * meanGuide[i];
            float covariance = meanProducts[i] - meanGuide[i] * meanMask[i];
            float slope = covariance / (variance + epsilon);
            meanSquares[i] = slope;
            meanProducts[i] = meanMask[i] - slope * meanGuide[i];
        }

        float[] meanSlope = Box(meanSquares, width, height, radius);
        float[] meanOffset = Box(meanProducts, width, height, radius);
        var result = new float[count];
        for (int i = 0; i < count; i++) result[i] = Math.Clamp(meanSlope[i] * guide[i] + meanOffset[i], 0, 1);
        return result;
    }

    /// <summary>Mean over a (2r+1)² square, edges clamped, as two running sums — the cost does not grow with the radius.</summary>
    public static float[] Box(float[] source, int width, int height, int radius)
    {
        float span = radius * 2 + 1;
        var pass = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            float sum = 0;
            for (int x = -radius; x <= radius; x++) sum += source[row + Math.Clamp(x, 0, width - 1)];
            for (int x = 0; x < width; x++)
            {
                pass[row + x] = sum / span;
                sum -= source[row + Math.Clamp(x - radius, 0, width - 1)];
                sum += source[row + Math.Clamp(x + radius + 1, 0, width - 1)];
            }
        }

        var result = new float[width * height];
        for (int x = 0; x < width; x++)
        {
            float sum = 0;
            for (int y = -radius; y <= radius; y++) sum += pass[Math.Clamp(y, 0, height - 1) * width + x];
            for (int y = 0; y < height; y++)
            {
                result[y * width + x] = sum / span;
                sum -= pass[Math.Clamp(y - radius, 0, height - 1) * width + x];
                sum += pass[Math.Clamp(y + radius + 1, 0, height - 1) * width + x];
            }
        }

        return result;
    }

    /// <summary>The layer's mask over its grid at <paramref name="width"/> × <paramref name="height"/>, 0–1; all 1 without one.</summary>
    private static float[] Existing(ImageLayer layer, int width, int height)
    {
        if (layer.Mask is not { Placement: null } mask)
        {
            var all = new float[width * height];
            Array.Fill(all, 1f);
            return all;
        }

        return Plane(Scaled(mask.Coverage, width, height), release: true);
    }

    /// <summary>
    /// Pixels resampled to <paramref name="width"/> × <paramref name="height"/>: halved while they
    /// are at least twice as large, then bilinear. The caller owns the result.
    /// </summary>
    public static PixelBuffer Scaled(PixelBuffer source, int width, int height)
    {
        PixelBuffer from = source.Retain();
        try
        {
            while (from.Width >= width * 2 && from.Height >= height * 2)
            {
                PixelBuffer half = DownsamplePyramid.Halve(from);
                from.Release();
                from = half;
            }

            PixelBuffer result = PixelBuffer.Allocate(width, height);
            double sx = (double)from.Width / width, sy = (double)from.Height / height;
            for (int y = 0; y < height; y++)
            {
                double fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, from.Height - 1);
                int y0 = (int)fy, y1 = Math.Min(from.Height - 1, y0 + 1);
                double ty = fy - y0;
                ReadOnlySpan<byte> top = from.Row(y0), bottom = from.Row(y1);
                Span<byte> row = result.Row(y);

                for (int x = 0; x < width; x++)
                {
                    double fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, from.Width - 1);
                    int x0 = (int)fx, x1 = Math.Min(from.Width - 1, x0 + 1);
                    double tx = fx - x0;
                    for (int c = 0; c < 4; c++)
                    {
                        double a = top[x0 * 4 + c] + (top[x1 * 4 + c] - top[x0 * 4 + c]) * tx;
                        double b = bottom[x0 * 4 + c] + (bottom[x1 * 4 + c] - bottom[x0 * 4 + c]) * tx;
                        row[x * 4 + c] = (byte)Math.Clamp(Math.Round(a + (b - a) * ty), 0, 255);
                    }
                }
            }

            return result;
        }
        finally
        {
            from.Release();
        }
    }

    /// <summary>
    /// A buffer's grey levels, 0–1: a mask's own level, or a picture's luminance over black. The
    /// buffer is released when asked, for the callers that made it only for this.
    /// </summary>
    private static float[] Plane(PixelBuffer pixels, bool release)
    {
        try
        {
            var plane = new float[pixels.Width * pixels.Height];
            for (int y = 0; y < pixels.Height; y++)
            {
                ReadOnlySpan<byte> row = pixels.Row(y);
                for (int x = 0; x < pixels.Width; x++)
                {
                    int i = x * 4;
                    plane[y * pixels.Width + x] = (row[i] * 0.299f + row[i + 1] * 0.587f + row[i + 2] * 0.114f) / 255f;
                }
            }
            return plane;
        }
        finally
        {
            if (release) pixels.Release();
        }
    }

    /// <summary>0–1 levels as an opaque grey mask. The caller owns the result.</summary>
    private static PixelBuffer Grey(float[] levels, int width, int height)
    {
        PixelBuffer mask = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = mask.Row(y);
            for (int x = 0; x < width; x++)
            {
                byte level = (byte)Math.Clamp(MathF.Round(levels[y * width + x] * 255), 0, 255);
                row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = level;
                row[x * 4 + 3] = 255;
            }
        }
        return mask;
    }
}
