namespace Compositor_korean_win.Core;

/// <summary>What the Filter and Image menus can run over a layer's pixels.</summary>
public enum FilterKind
{
    /// <summary>One of the six adjustments, applied to the pixels rather than kept as a layer.</summary>
    Adjustment,

    GaussianBlur,
    MotionBlur,
    AddNoise,
    LensCorrection,
}

/// <summary>
/// Every filter's settings; each reads only its own. Ranges and defaults are upstream's.
/// </summary>
public sealed record FilterSettings
{
    /// <summary>Gaussian Blur's standard deviation in layer pixels, 0.1–250.</summary>
    public double Radius { get; init; } = 1;

    /// <summary>Motion Blur's direction in degrees, counterclockwise from horizontal, −90–90.</summary>
    public double Angle { get; init; }

    /// <summary>Motion Blur's streak length in layer pixels, 1–2000.</summary>
    public double Distance { get; init; } = 10;

    /// <summary>Add Noise's strength as Photoshop's percentage, 0.1–400.</summary>
    public double Amount { get; init; } = 10;

    /// <summary>Add Noise: Gaussian rather than uniform.</summary>
    public bool Gaussian { get; init; }

    /// <summary>Add Noise: the same change on every channel, so brightness only.</summary>
    public bool Monochromatic { get; init; }

    /// <summary>
    /// Lens Correction's Remove Distortion, −100–100: positive straightens barrel distortion,
    /// negative straightens pincushion.
    /// </summary>
    public double Distortion { get; init; }

    /// <summary>The adjustment to run, for <see cref="FilterKind.Adjustment"/>.</summary>
    public LayerAdjustment? Adjustment { get; init; }

    /// <summary>
    /// The random pattern for Add Noise, and for Grain run as a filter. Fixed while a filter is being
    /// previewed, so changing the amount does not reshuffle the grain under the pointer.
    /// </summary>
    public uint Seed { get; init; }

    public FilterSettings Normalized => this with
    {
        Radius = AdjustmentMath.Clamp(Radius, 0.1, 250, 1),
        Angle = AdjustmentMath.Clamp(Angle, -90, 90, 0),
        Distance = AdjustmentMath.Clamp(Distance, 1, 2000, 10),
        Amount = AdjustmentMath.Clamp(Amount, 0.1, 400, 10),
        Distortion = AdjustmentMath.Clamp(Distortion, -100, 100, 0),
    };
}

/// <summary>
/// The filters themselves, on whole buffers.
/// </summary>
/// <remarks>
/// <para>
/// Add Noise and Lens Correction are upstream's C, called as they are. The two blurs went through
/// Core Image upstream and are written out here.
/// </para>
/// <para>
/// Gaussian Blur is three box blurs in each direction, the standard approximation: its cost does not
/// grow with the radius, which a true Gaussian kernel's does, and a radius of 250 is within the
/// range. The boxes are sized so their combined variance matches σ² as closely as whole widths allow
/// — which for a small σ is not close at all: at σ = 1 the nearest three odd widths give two thirds
/// of the variance or four thirds of it. Below <see cref="BoxThreshold"/> the kernel is small
/// enough to run as it is, so it does. The threshold sits where the boxes come within a sixth of σ²
/// (at σ = 2 they give five sixths); raising it buys little accuracy for a kernel whose cost grows
/// with σ, which the preview at "fit" feels first.
/// </para>
/// <para>
/// Motion Blur smears evenly along the whole distance, which is what Photoshop does and what
/// upstream's constant was calibrating Core Image's tapered streak towards. Its cost is one sample
/// per pixel of streak, so a long streak on a large layer is the slowest thing here; the preview
/// runs it on the view's reduced pixels, and only committing pays for the full size.
/// </para>
/// <para>
/// Nothing is clamped at the edge. Past a buffer's edge is transparent, so a blur softens a layer's
/// border and spreads into the room made for it rather than smearing the edge outwards.
/// </para>
/// </remarks>
public static class PixelFilters
{
    /// <summary>
    /// Upstream's Remove Distortion at ±100 moves the corners by this share of their distance from
    /// the centre.
    /// </summary>
    public const double LensStrength = 0.35;

    /// <summary>The σ from which three boxes stand in for the true kernel.</summary>
    public const double BoxThreshold = 2;

    /// <summary>How far a filter reaches past a pixel, in layer pixels — the room a blur needs.</summary>
    public static int Reach(FilterKind kind, FilterSettings settings)
    {
        FilterSettings s = settings.Normalized;
        return kind switch
        {
            FilterKind.GaussianBlur => (int)Math.Ceiling(s.Radius * 3 + 2),
            FilterKind.MotionBlur => (int)Math.Ceiling(s.Distance / 2 + 2),
            _ => 0,
        };
    }

    /// <summary>Whether the filter changes a pixel by what is around it rather than by itself.</summary>
    public static bool IsSpatial(FilterKind kind) =>
        kind is FilterKind.GaussianBlur or FilterKind.MotionBlur or FilterKind.LensCorrection;

    /// <summary>
    /// Runs a filter over <paramref name="source"/>, returning a new buffer of the same size.
    /// </summary>
    /// <param name="scale">
    /// Pixels of <paramref name="source"/> per layer pixel: 1 when committing, less for a preview
    /// made from reduced pixels, so a blur reaches proportionally less and looks the same.
    /// </param>
    /// <param name="placement">
    /// Where the buffer sits on the document, for Grain. Its pattern is the one thing here tied to
    /// the document rather than to the layer.
    /// </param>
    public static PixelBuffer Run(PixelBuffer source, FilterKind kind, FilterSettings settings, double scale = 1,
                                  PixelPlacement? placement = null)
    {
        FilterSettings s = settings.Normalized;
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;

        switch (kind)
        {
            case FilterKind.GaussianBlur:
                return GaussianBlur(source, s.Radius * scale);

            case FilterKind.MotionBlur:
                return MotionBlur(source, s.Distance * scale, s.Angle);

            case FilterKind.LensCorrection:
                return Lens(source, s.Distortion / 100 * LensStrength);

            case FilterKind.AddNoise:
            {
                PixelBuffer result = Copy(source);
                Kernels.NoiseAdd(result.Scan0, (nuint)result.Width, (nuint)result.Height, (nuint)result.Stride,
                                 (float)s.Amount, s.Gaussian ? 1 : 0, s.Monochromatic ? 1 : 0, s.Seed);
                return result;
            }

            default:
            {
                PixelBuffer result = Copy(source);
                if (s.Adjustment is LayerAdjustment adjustment)
                {
                    // Grain run as a filter takes the filter's seed, so each application is its own.
                    if (adjustment.Kind == AdjustmentKind.Grain)
                        adjustment = adjustment with { GrainSettings = adjustment.Grain with { Seed = s.Seed } };

                    AdjustmentRendering.Apply(adjustment, result,
                                              placement ?? new PixelPlacement(0, 0, 1 / scale));
                }
                return result;
            }
        }
    }

    /// <summary>A Gaussian blur of standard deviation <paramref name="sigma"/> pixels.</summary>
    public static PixelBuffer GaussianBlur(PixelBuffer source, double sigma)
    {
        if (sigma > 0 && sigma < BoxThreshold) return TrueGaussian(source, sigma);

        PixelBuffer result = Copy(source);
        if (!(sigma > 0)) return result;

        PixelBuffer scratch = PixelBuffer.Allocate(source.Width, source.Height);
        try
        {
            foreach (int radius in BoxRadii(sigma, 3))
            {
                if (radius <= 0) continue;
                BoxHorizontal(result, scratch, radius);
                BoxVertical(scratch, result, radius);
            }
        }
        finally
        {
            scratch.Release();
        }

        return result;
    }

    /// <summary>A separable Gaussian with the kernel itself, reaching three σ.</summary>
    private static PixelBuffer TrueGaussian(PixelBuffer source, double sigma)
    {
        int radius = (int)Math.Ceiling(sigma * 3);
        var weights = new double[radius * 2 + 1];
        double total = 0;
        for (int k = -radius; k <= radius; k++) total += weights[k + radius] = Math.Exp(-k * k / (2 * sigma * sigma));
        for (int k = 0; k < weights.Length; k++) weights[k] /= total;

        PixelBuffer across = PixelBuffer.Allocate(source.Width, source.Height);
        PixelBuffer result = PixelBuffer.Allocate(source.Width, source.Height);
        try
        {
            Convolve(source, across, weights, horizontal: true);
            Convolve(across, result, weights, horizontal: false);
        }
        finally
        {
            across.Release();
        }
        return result;
    }

    /// <summary>One direction of a separable kernel, transparent past the edges.</summary>
    /// <remarks>
    /// Both directions go a row at a time — the vertical one by adding whole rows into a row of
    /// sums — so memory is read in order either way.
    /// </remarks>
    private static void Convolve(PixelBuffer from, PixelBuffer to, double[] weights, bool horizontal)
    {
        int radius = weights.Length / 2;
        int width = from.Width, height = from.Height;
        var sums = new double[width * 4];

        for (int y = 0; y < height; y++)
        {
            Array.Clear(sums);

            if (horizontal)
            {
                ReadOnlySpan<byte> input = from.Row(y);
                for (int x = 0; x < width; x++)
                {
                    int first = Math.Max(-radius, -x), last = Math.Min(radius, width - 1 - x);
                    for (int k = first; k <= last; k++)
                    {
                        double w = weights[k + radius];
                        int i = (x + k) * 4;
                        sums[x * 4] += input[i] * w;
                        sums[x * 4 + 1] += input[i + 1] * w;
                        sums[x * 4 + 2] += input[i + 2] * w;
                        sums[x * 4 + 3] += input[i + 3] * w;
                    }
                }
            }
            else
            {
                int first = Math.Max(-radius, -y), last = Math.Min(radius, height - 1 - y);
                for (int k = first; k <= last; k++)
                {
                    double w = weights[k + radius];
                    ReadOnlySpan<byte> input = from.Row(y + k);
                    for (int i = 0; i < width * 4; i++) sums[i] += input[i] * w;
                }
            }

            Span<byte> output = to.Row(y);
            for (int x = 0; x < width; x++)
            {
                int o = x * 4;
                byte alpha = (byte)Math.Min(255, Math.Round(sums[o + 3], MidpointRounding.AwayFromZero));
                output[o + 3] = alpha;
                for (int c = 0; c < 3; c++)
                    output[o + c] = (byte)Math.Min(alpha, Math.Round(sums[o + c], MidpointRounding.AwayFromZero));
            }
        }
    }

    /// <summary>
    /// The radii of <paramref name="passes"/> box blurs whose variances add up to σ².
    /// </summary>
    /// <remarks>
    /// A box of odd width w has variance (w² − 1) / 12. Two widths two apart are mixed so the total
    /// lands as near σ² as whole widths can.
    /// </remarks>
    public static int[] BoxRadii(double sigma, int passes)
    {
        double ideal = Math.Sqrt(12 * sigma * sigma / passes + 1);
        int lower = (int)Math.Floor(ideal);
        if (lower % 2 == 0) lower--;
        lower = Math.Max(1, lower);
        int upper = lower + 2;

        double mixed = (12 * sigma * sigma - passes * lower * lower - 4.0 * passes * lower - 3.0 * passes)
                       / (-4.0 * lower - 4);
        int lowerCount = (int)Math.Clamp(Math.Round(mixed, MidpointRounding.AwayFromZero), 0, passes);

        var radii = new int[passes];
        for (int i = 0; i < passes; i++) radii[i] = ((i < lowerCount ? lower : upper) - 1) / 2;
        return radii;
    }

    /// <summary>A running-sum box along each row, transparent past the ends.</summary>
    private static void BoxHorizontal(PixelBuffer from, PixelBuffer to, int radius)
    {
        int width = from.Width, span = radius * 2 + 1, half = span / 2;

        for (int y = 0; y < from.Height; y++)
        {
            ReadOnlySpan<byte> input = from.Row(y);
            Span<byte> output = to.Row(y);
            int r = 0, g = 0, b = 0, a = 0;

            // Prime the window with what lies right of the first pixel.
            for (int x = 0; x < Math.Min(radius, width); x++)
            {
                r += input[x * 4]; g += input[x * 4 + 1]; b += input[x * 4 + 2]; a += input[x * 4 + 3];
            }

            for (int x = 0; x < width; x++)
            {
                int entering = x + radius;
                if (entering < width)
                {
                    int i = entering * 4;
                    r += input[i]; g += input[i + 1]; b += input[i + 2]; a += input[i + 3];
                }

                int o = x * 4;
                output[o] = (byte)((r + half) / span);
                output[o + 1] = (byte)((g + half) / span);
                output[o + 2] = (byte)((b + half) / span);
                output[o + 3] = (byte)((a + half) / span);

                int leaving = x - radius;
                if (leaving >= 0)
                {
                    int i = leaving * 4;
                    r -= input[i]; g -= input[i + 1]; b -= input[i + 2]; a -= input[i + 3];
                }
            }
        }
    }

    /// <summary>
    /// A running-sum box down each column, a row at a time so memory is read in order.
    /// </summary>
    private static void BoxVertical(PixelBuffer from, PixelBuffer to, int radius)
    {
        int width = from.Width, height = from.Height, span = radius * 2 + 1, half = span / 2;
        int[] sums = new int[width * 4];

        for (int y = 0; y < Math.Min(radius, height); y++)
        {
            ReadOnlySpan<byte> row = from.Row(y);
            for (int i = 0; i < width * 4; i++) sums[i] += row[i];
        }

        for (int y = 0; y < height; y++)
        {
            int entering = y + radius;
            if (entering < height)
            {
                ReadOnlySpan<byte> row = from.Row(entering);
                for (int i = 0; i < width * 4; i++) sums[i] += row[i];
            }

            Span<byte> output = to.Row(y);
            for (int i = 0; i < width * 4; i++) output[i] = (byte)((sums[i] + half) / span);

            int leaving = y - radius;
            if (leaving >= 0)
            {
                ReadOnlySpan<byte> row = from.Row(leaving);
                for (int i = 0; i < width * 4; i++) sums[i] -= row[i];
            }
        }
    }

    /// <summary>
    /// An even smear <paramref name="length"/> pixels long, centred on each pixel, along
    /// <paramref name="degrees"/> counterclockwise from horizontal.
    /// </summary>
    public static PixelBuffer MotionBlur(PixelBuffer source, double length, double degrees)
    {
        if (!(length > 1)) return Copy(source);

        PixelBuffer result = PixelBuffer.Allocate(source.Width, source.Height);

        // Rows run downwards, so counterclockwise on screen is a negative y step.
        double radians = degrees * Math.PI / 180;
        double stepX = Math.Cos(radians), stepY = -Math.Sin(radians);
        int samples = (int)Math.Ceiling(length) + 1;
        double spacing = length / (samples - 1);
        double start = -length / 2;

        Span<double> sum = stackalloc double[4];
        Span<byte> sample = stackalloc byte[4];

        for (int y = 0; y < source.Height; y++)
        {
            Span<byte> output = result.Row(y);
            for (int x = 0; x < source.Width; x++)
            {
                sum.Clear();
                for (int k = 0; k < samples; k++)
                {
                    double t = start + k * spacing;
                    Bilinear(source, x + 0.5 + t * stepX, y + 0.5 + t * stepY, sample);
                    sum[0] += sample[0]; sum[1] += sample[1]; sum[2] += sample[2]; sum[3] += sample[3];
                }

                int o = x * 4;
                byte alpha = (byte)Math.Round(sum[3] / samples, MidpointRounding.AwayFromZero);
                output[o + 3] = alpha;
                for (int c = 0; c < 3; c++)
                    output[o + c] = (byte)Math.Min(alpha, Math.Round(sum[c] / samples, MidpointRounding.AwayFromZero));
            }
        }

        return result;
    }

    /// <summary>Radial distortion by upstream's kernel; <paramref name="k"/> is its coefficient.</summary>
    public static PixelBuffer Lens(PixelBuffer source, double k)
    {
        PixelBuffer result = PixelBuffer.Allocate(source.Width, source.Height);
        // The two share a stride: same size, same allocator.
        Kernels.LensDistort(source.Scan0, result.Scan0, (nuint)source.Width, (nuint)source.Height,
                            (nuint)source.Stride, k);
        return result;
    }

    /// <summary>
    /// <paramref name="filtered"/> moved back towards <paramref name="original"/> wherever
    /// <paramref name="levels"/> is less than full — how a selection confines a filter.
    /// </summary>
    public static void Confine(PixelBuffer original, PixelBuffer filtered, byte[] levels)
    {
        if (levels.LongLength != (long)filtered.Width * filtered.Height)
            throw new ArgumentException("one level per pixel", nameof(levels));

        for (int y = 0; y < filtered.Height; y++)
        {
            ReadOnlySpan<byte> from = original.Row(y);
            Span<byte> to = filtered.Row(y);
            for (int x = 0; x < filtered.Width; x++)
            {
                int w = levels[y * filtered.Width + x];
                if (w == 255) continue;
                int i = x * 4;
                for (int c = 0; c < 4; c++)
                    to[i + c] = (byte)((from[i + c] * (255 - w) + to[i + c] * w + 127) / 255);
            }
        }
    }

    internal static PixelBuffer Copy(PixelBuffer source) =>
        PixelRegion.Copy(source, new PixelRect(0, 0, source.Width, source.Height));

    private static void Bilinear(PixelBuffer buffer, double px, double py, Span<byte> result)
    {
        double fx = px - 0.5, fy = py - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double tx = fx - x0, ty = fy - y0;

        for (int c = 0; c < 4; c++)
        {
            double top = At(buffer, x0, y0, c) * (1 - tx) + At(buffer, x0 + 1, y0, c) * tx;
            double bottom = At(buffer, x0, y0 + 1, c) * (1 - tx) + At(buffer, x0 + 1, y0 + 1, c) * tx;
            result[c] = (byte)Math.Round(top * (1 - ty) + bottom * ty, MidpointRounding.AwayFromZero);
        }

        static int At(PixelBuffer buffer, int x, int y, int channel) =>
            x < 0 || y < 0 || x >= buffer.Width || y >= buffer.Height ? 0 : buffer.Row(y)[x * 4 + channel];
    }
}
