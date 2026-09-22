namespace Compositor_korean_win.Core;

/// <summary>
/// Hue/Saturation applied to pixels, through a colour cube built from the settings.
/// </summary>
/// <remarks>
/// <para>
/// Upstream builds a 33-point cube and hands it to Core Image's <c>CIColorCube</c>, so dragging a
/// slider costs 36 thousand HSL round trips rather than one per pixel. The cube is the part that
/// defines what the adjustment <i>is</i>, so it is ported line for line; the lookup that follows is
/// trilinear interpolation, which is what <c>CIColorCube</c> does, done here instead of on the GPU.
/// </para>
/// <para>
/// The lookup runs on straight colour. <c>CIColorCube</c> unpremultiplies and premultiplies around
/// itself, and upstream's note records what happened when that was done twice: half-transparent blue
/// came out at 94 of 128, which turned every soft edge dark. Once, here, is the whole of it.
/// </para>
/// </remarks>
public static class HueSaturationFilter
{
    /// <summary>Points per axis: upstream's, and the usual size for this kind of lookup.</summary>
    public const int Dimension = 33;

    /// <summary>What every range adds up to at one hue, sampled once per degree.</summary>
    public readonly record struct HueResponse(double Shift, double Saturation, double Lightness);

    /// <summary>Settings that leave every colour as it is.</summary>
    public static bool IsIdentity(HueSaturationSettings settings) =>
        !settings.Colorize && settings.Adjustments.Values.All(adjustment => adjustment == new RangeAdjustment());

    /// <summary>How strongly a range applies to a hue: Master everywhere, the others through their band.</summary>
    public static double Weight(HueSaturationSettings settings, ColorRange range, double hue)
    {
        if (range == ColorRange.Master) return 1;
        double weight = settings.Bands.ValueOr(range, range.DefaultBand()).Weight(hue);
        return settings.InvertRange && range == settings.Range ? 1 - weight : weight;
    }

    /// <summary>
    /// Every range's contribution at each whole degree, 0–360. Built once per settings so that each
    /// of the cube's entries does not re-evaluate all seven ranges.
    /// </summary>
    public static HueResponse[] Response(HueSaturationSettings settings)
    {
        var response = new HueResponse[361];
        for (int degree = 0; degree <= 360; degree++)
        {
            double shift = 0, saturation = 0, lightness = 0;
            foreach ((ColorRange range, RangeAdjustment adjustment) in settings.Adjustments)
            {
                if (adjustment == new RangeAdjustment()) continue;
                double weight = Weight(settings, range, degree);
                if (weight <= 0) continue;
                shift += adjustment.Hue * weight;
                saturation += adjustment.Saturation * weight;
                lightness += adjustment.Lightness * weight;
            }
            response[degree] = new HueResponse(shift, saturation, lightness);
        }
        return response;
    }

    /// <summary>One straight colour, 0–1 per channel, through the adjustment.</summary>
    public static (double Red, double Green, double Blue) Adjust(
        double red, double green, double blue, HueSaturationSettings settings, HueResponse[]? response = null)
    {
        (double hue, double saturation, double lightness) = ToHsl(red, green, blue);
        double lightnessAmount;

        if (settings.Colorize)
        {
            hue = settings.Hue % 360;
            saturation = Math.Min(1, Math.Max(0, settings.Saturation / 100));
            lightnessAmount = settings.Lightness / 100;
        }
        else
        {
            // Every range contributes, weighted by how strongly it claims the original hue.
            HueResponse[] table = response ?? Response(settings);
            int index = (int)Math.Min(table.Length - 1, Math.Max(0, Math.Round(hue, MidpointRounding.AwayFromZero)));
            HueResponse sampled = table[index];
            lightnessAmount = sampled.Lightness / 100;
            hue = (hue + sampled.Shift) % 360;
            if (hue < 0) hue += 360;

            // Multiplicative, so neutral greys stay neutral.
            saturation = Math.Min(1, Math.Max(0, saturation * (1 + sampled.Saturation / 100)));
        }

        // Lightness pulls towards white above 0 and towards black below, reaching either at ±100.
        double amount = Math.Min(1, Math.Max(-1, lightnessAmount));
        lightness = amount >= 0 ? lightness + (1 - lightness) * amount : lightness * (1 + amount);

        return ToRgb(hue, saturation, Math.Min(1, Math.Max(0, lightness)));
    }

    /// <summary>
    /// The lookup table: every corner of the cube adjusted, red varying fastest, three floats each.
    /// </summary>
    public static float[] Cube(HueSaturationSettings settings)
    {
        HueResponse[] response = Response(settings);
        var values = new float[Dimension * Dimension * Dimension * 3];
        double step = Dimension - 1;
        int index = 0;

        for (int blue = 0; blue < Dimension; blue++)
            for (int green = 0; green < Dimension; green++)
                for (int red = 0; red < Dimension; red++)
                {
                    (double r, double g, double b) = Adjust(red / step, green / step, blue / step, settings, response);
                    values[index++] = (float)r;
                    values[index++] = (float)g;
                    values[index++] = (float)b;
                }

        return values;
    }

    /// <summary>Runs <paramref name="region"/> of premultiplied pixels through a cube, in place.</summary>
    public static void Apply(PixelBuffer pixels, PixelRect region, float[] cube)
    {
        const int d = Dimension;
        const float top = d - 1;

        for (int y = region.Y; y < region.Bottom; y++)
        {
            Span<byte> row = pixels.Row(y);
            for (int x = region.X; x < region.Right; x++)
            {
                Span<byte> p = row.Slice(x * 4, 4);
                int alpha = p[3];
                if (alpha == 0) continue;

                float unpremultiply = top / alpha;
                float fr = Math.Min(top, p[0] * unpremultiply);
                float fg = Math.Min(top, p[1] * unpremultiply);
                float fb = Math.Min(top, p[2] * unpremultiply);

                int r0 = Math.Min(d - 2, (int)fr), g0 = Math.Min(d - 2, (int)fg), b0 = Math.Min(d - 2, (int)fb);
                float tr = fr - r0, tg = fg - g0, tb = fb - b0;

                int i000 = ((b0 * d + g0) * d + r0) * 3;
                int i100 = i000 + 3;
                int i010 = i000 + d * 3;
                int i110 = i010 + 3;
                int i001 = i000 + d * d * 3;
                int i101 = i001 + 3;
                int i011 = i001 + d * 3;
                int i111 = i011 + 3;

                for (int channel = 0; channel < 3; channel++)
                {
                    float c00 = cube[i000 + channel] + (cube[i100 + channel] - cube[i000 + channel]) * tr;
                    float c10 = cube[i010 + channel] + (cube[i110 + channel] - cube[i010 + channel]) * tr;
                    float c01 = cube[i001 + channel] + (cube[i101 + channel] - cube[i001 + channel]) * tr;
                    float c11 = cube[i011 + channel] + (cube[i111 + channel] - cube[i011 + channel]) * tr;
                    float c0 = c00 + (c10 - c00) * tg;
                    float c1 = c01 + (c11 - c01) * tg;
                    float value = c0 + (c1 - c0) * tb;

                    p[channel] = (byte)Math.Min(alpha, Math.Max(0, MathF.Round(value * alpha)));
                }
            }
        }
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(double red, double green, double blue)
    {
        double high = Math.Max(red, Math.Max(green, blue)), low = Math.Min(red, Math.Min(green, blue));
        double lightness = (high + low) / 2;
        double delta = high - low;
        if (delta <= 0) return (0, 0, lightness);

        double saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        double hue;
        if (high == red) hue = (green - blue) / delta;
        else if (high == green) hue = (blue - red) / delta + 2;
        else hue = (red - green) / delta + 4;
        hue *= 60;
        if (hue < 0) hue += 360;

        return (hue, Math.Min(1, saturation), lightness);
    }

    private static (double Red, double Green, double Blue) ToRgb(double hue, double saturation, double lightness)
    {
        if (saturation <= 0) return (lightness, lightness, lightness);

        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double sector = hue / 60;
        double second = chroma * (1 - Math.Abs(sector % 2 - 1));
        double baseLevel = lightness - chroma / 2;

        (double red, double green, double blue) = (int)sector switch
        {
            0 => (chroma, second, 0.0),
            1 => (second, chroma, 0.0),
            2 => (0.0, chroma, second),
            3 => (0.0, second, chroma),
            4 => (second, 0.0, chroma),
            _ => (chroma, 0.0, second),
        };

        return (Math.Min(1, Math.Max(0, red + baseLevel)),
                Math.Min(1, Math.Max(0, green + baseLevel)),
                Math.Min(1, Math.Max(0, blue + baseLevel)));
    }
}
