namespace Compositor_korean_win.Core;

/// <summary>
/// The blend modes, as the PDF specification and Photoshop define them.
/// </summary>
/// <remarks>
/// <para>
/// Everything works on straight (unpremultiplied) sRGB values in 0–1. Blending happens on the
/// encoded values rather than in linear light, which is what Core Graphics and Photoshop both do;
/// converting to linear first would give different — arguably more correct, but visibly different —
/// results, and matching the macOS build matters more than being theoretically right.
/// </para>
/// <para>
/// Upstream carries a note worth repeating: Core Graphics gets Color Burn and Color Dodge wrong,
/// ignoring how transparent the source is, and works around it by routing those two through Core
/// Image. Direct2D's own blend effect has no such flaw, and this reference implementation follows
/// the specification, so the workaround does not travel with the port.
/// </para>
/// </remarks>
public static class Blending
{
    /// <summary>Blends one channel. Both values are straight sRGB in 0–1.</summary>
    public static double Channel(LayerBlendMode mode, double backdrop, double source) => mode switch
    {
        LayerBlendMode.Normal => source,
        LayerBlendMode.Multiply => backdrop * source,
        LayerBlendMode.Screen => backdrop + source - backdrop * source,
        LayerBlendMode.Overlay => HardLight(source, backdrop),
        LayerBlendMode.Darken => Math.Min(backdrop, source),
        LayerBlendMode.Lighten => Math.Max(backdrop, source),
        LayerBlendMode.Difference => Math.Abs(backdrop - source),
        LayerBlendMode.ColorDodge => ColorDodge(backdrop, source),
        LayerBlendMode.ColorBurn => ColorBurn(backdrop, source),
        _ => source, // The four non-separable modes are handled whole, in Rgb below.
    };

    /// <summary>True when the mode mixes the channels and cannot be done one at a time.</summary>
    public static bool IsNonSeparable(LayerBlendMode mode) => mode
        is LayerBlendMode.Hue or LayerBlendMode.Saturation
        or LayerBlendMode.Color or LayerBlendMode.Luminosity;

    /// <summary>Blends a whole colour, covering the non-separable modes too.</summary>
    public static (double R, double G, double B) Rgb(LayerBlendMode mode,
                                                     (double R, double G, double B) backdrop,
                                                     (double R, double G, double B) source)
    {
        switch (mode)
        {
            case LayerBlendMode.Hue:
                return SetLuminosity(SetSaturation(source, Saturation(backdrop)), Luminosity(backdrop));
            case LayerBlendMode.Saturation:
                return SetLuminosity(SetSaturation(backdrop, Saturation(source)), Luminosity(backdrop));
            case LayerBlendMode.Color:
                return SetLuminosity(source, Luminosity(backdrop));
            case LayerBlendMode.Luminosity:
                return SetLuminosity(backdrop, Luminosity(source));
            default:
                return (Channel(mode, backdrop.R, source.R),
                        Channel(mode, backdrop.G, source.G),
                        Channel(mode, backdrop.B, source.B));
        }
    }

    private static double HardLight(double backdrop, double source) =>
        source <= 0.5
            ? backdrop * (2 * source)
            : backdrop + (2 * source - 1) - backdrop * (2 * source - 1);

    /// <summary>
    /// Dodge, the way the specification defines it — including the two ends Core Graphics gets
    /// wrong: a black backdrop stays black, and a white source goes to white.
    /// </summary>
    private static double ColorDodge(double backdrop, double source)
    {
        if (backdrop <= 0) return 0;
        if (source >= 1) return 1;
        return Math.Min(1, backdrop / (1 - source));
    }

    private static double ColorBurn(double backdrop, double source)
    {
        if (backdrop >= 1) return 1;
        if (source <= 0) return 0;
        return 1 - Math.Min(1, (1 - backdrop) / source);
    }

    // --- The non-separable helpers, straight from the PDF specification -------------------------

    private static double Luminosity((double R, double G, double B) c) =>
        0.3 * c.R + 0.59 * c.G + 0.11 * c.B;

    private static double Saturation((double R, double G, double B) c) =>
        Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));

    private static (double R, double G, double B) SetLuminosity((double R, double G, double B) c, double luminosity)
    {
        double delta = luminosity - Luminosity(c);
        return ClipColor((c.R + delta, c.G + delta, c.B + delta));
    }

    /// <summary>
    /// Pulls a colour back inside 0–1 by moving it towards its own luminosity, which keeps the hue
    /// rather than clamping each channel and shifting it.
    /// </summary>
    private static (double R, double G, double B) ClipColor((double R, double G, double B) c)
    {
        double luminosity = Luminosity(c);
        double low = Math.Min(c.R, Math.Min(c.G, c.B));
        double high = Math.Max(c.R, Math.Max(c.G, c.B));

        if (low < 0)
        {
            double scale = luminosity / (luminosity - low);
            c = (luminosity + (c.R - luminosity) * scale,
                 luminosity + (c.G - luminosity) * scale,
                 luminosity + (c.B - luminosity) * scale);
        }

        if (high > 1)
        {
            double scale = (1 - luminosity) / (high - luminosity);
            c = (luminosity + (c.R - luminosity) * scale,
                 luminosity + (c.G - luminosity) * scale,
                 luminosity + (c.B - luminosity) * scale);
        }

        return c;
    }

    private static (double R, double G, double B) SetSaturation((double R, double G, double B) c, double saturation)
    {
        double low = Math.Min(c.R, Math.Min(c.G, c.B));
        double high = Math.Max(c.R, Math.Max(c.G, c.B));
        if (high <= low) return (0, 0, 0);

        // The middle channel keeps its position between the other two; the spread becomes the
        // saturation asked for.
        double Scale(double value) => (value - low) / (high - low) * saturation;
        return (Scale(c.R), Scale(c.G), Scale(c.B));
    }

    /// <summary>
    /// Composites one premultiplied source pixel over one premultiplied backdrop pixel.
    /// </summary>
    /// <remarks>
    /// This is source-over with a blend function, as the specification writes it:
    /// <c>co = Cs·(1 − αb) + Cb·(1 − αs) + αs·αb·B(Cb/αb, Cs/αs)</c> and
    /// <c>αo = αs + αb·(1 − αs)</c>. The blend term only applies where both are opaque, which is
    /// what makes a soft-edged brush in Multiply fade out instead of ending on a hard edge — and
    /// is exactly the part Core Graphics drops for Dodge and Burn.
    /// </remarks>
    public static void Composite(LayerBlendMode mode, Span<byte> backdrop, ReadOnlySpan<byte> source,
                                 double opacity)
    {
        double sourceAlpha = source[3] / 255.0 * opacity;
        if (sourceAlpha <= 0) return;

        double backdropAlpha = backdrop[3] / 255.0;
        double outAlpha = sourceAlpha + backdropAlpha * (1 - sourceAlpha);
        if (outAlpha <= 0)
        {
            backdrop[0] = backdrop[1] = backdrop[2] = backdrop[3] = 0;
            return;
        }

        // Straight values, which is what the blend functions are defined on.
        (double R, double G, double B) straightSource = Straight(source, source[3] / 255.0);
        (double R, double G, double B) straightBackdrop = Straight(backdrop, backdropAlpha);

        (double R, double G, double B) blended = mode == LayerBlendMode.Normal
            ? straightSource
            : Rgb(mode, straightBackdrop, straightSource);

        Span<double> result = stackalloc double[3];
        for (int i = 0; i < 3; i++)
        {
            double cs = i == 0 ? straightSource.R : i == 1 ? straightSource.G : straightSource.B;
            double cb = i == 0 ? straightBackdrop.R : i == 1 ? straightBackdrop.G : straightBackdrop.B;
            double cx = i == 0 ? blended.R : i == 1 ? blended.G : blended.B;

            // Premultiplied output: the three terms are source-only, backdrop-only, and the
            // overlap where the blend function applies.
            result[i] = cs * sourceAlpha * (1 - backdropAlpha)
                      + cb * backdropAlpha * (1 - sourceAlpha)
                      + cx * sourceAlpha * backdropAlpha;
        }

        backdrop[0] = ToByte(result[0]);
        backdrop[1] = ToByte(result[1]);
        backdrop[2] = ToByte(result[2]);
        backdrop[3] = ToByte(outAlpha);

        // Rounding each channel independently can leave a channel a step above the alpha, which is
        // not a representable premultiplied colour and shows up as a bright fringe.
        for (int i = 0; i < 3; i++) backdrop[i] = Math.Min(backdrop[i], backdrop[3]);
    }

    private static (double R, double G, double B) Straight(ReadOnlySpan<byte> premultiplied, double alpha) =>
        alpha <= 0
            ? (0, 0, 0)
            : (premultiplied[0] / 255.0 / alpha, premultiplied[1] / 255.0 / alpha, premultiplied[2] / 255.0 / alpha);

    private static byte ToByte(double value) =>
        (byte)Math.Clamp(Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
}
