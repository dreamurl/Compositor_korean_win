namespace Compositor_korean_win.Core;

/// <summary>What a Hue/Saturation eyedropper does to the selected colour range.</summary>
public enum HueSampleMode
{
    /// <summary>Centres the range on the colour clicked.</summary>
    Replace,

    /// <summary>Widens the range until the colour is fully inside it.</summary>
    Add,

    /// <summary>Narrows the range until the colour is outside it, shoulder and all.</summary>
    Remove,
}

/// <summary>
/// Setting Hue/Saturation's colour ranges from the image and by hand — upstream's <c>HueBand</c>
/// editing and <c>sampleHueRange</c>, and the spectrum bars' arithmetic.
/// </summary>
public static class HueSampling
{
    /// <summary>The widest a band may be; a full circle would leave nothing outside it to fall off to.</summary>
    private const double MaximumSpan = 350;

    /// <summary>Widens the band so <paramref name="hue"/> is fully inside it, moving whichever edge is nearer.</summary>
    public static HueBand Include(this HueBand band, double hue)
    {
        ArgumentNullException.ThrowIfNull(band);
        if (band.Weight(hue) >= 1) return band;

        double shoulderIn = HueBand.Forward(band.FalloffStart, band.RangeStart);
        double shoulderOut = HueBand.Forward(band.RangeEnd, band.FalloffEnd);
        double beforeStart = HueBand.Forward(hue, band.RangeStart);
        double afterEnd = HueBand.Forward(band.RangeEnd, hue);

        return Normalized(beforeStart <= afterEnd
            ? band with { RangeStart = hue, FalloffStart = hue - shoulderIn }
            : band with { RangeEnd = hue, FalloffEnd = hue + shoulderOut });
    }

    /// <summary>Narrows the band so <paramref name="hue"/> falls outside it entirely, shoulder included.</summary>
    public static HueBand Exclude(this HueBand band, double hue)
    {
        ArgumentNullException.ThrowIfNull(band);
        if (band.Weight(hue) <= 0) return band;

        double shoulderIn = HueBand.Forward(band.FalloffStart, band.RangeStart);
        double shoulderOut = HueBand.Forward(band.RangeEnd, band.FalloffEnd);
        double fromStart = HueBand.Forward(band.FalloffStart, hue);
        double toEnd = HueBand.Forward(hue, band.FalloffEnd);

        return Normalized(fromStart <= toEnd
            ? band with { FalloffStart = hue + 1, RangeStart = hue + 1 + shoulderIn }
            : band with { FalloffEnd = hue - 1, RangeEnd = hue - 1 - shoulderOut });
    }

    /// <summary>
    /// The band with one handle — 0 to 3, falloff start to falloff end — moved to
    /// <paramref name="degrees"/>, or the band unchanged when that would put the handles out of order.
    /// </summary>
    public static HueBand WithHandle(this HueBand band, int index, double degrees)
    {
        ArgumentNullException.ThrowIfNull(band);
        double value = Wrap(degrees);
        HueBand moved = index switch
        {
            0 => band with { FalloffStart = value },
            1 => band with { RangeStart = value },
            2 => band with { RangeEnd = value },
            _ => band with { FalloffEnd = value },
        };

        double span = HueBand.Forward(moved.FalloffStart, moved.FalloffEnd);
        double toStart = HueBand.Forward(moved.FalloffStart, moved.RangeStart);
        double toEnd = HueBand.Forward(moved.FalloffStart, moved.RangeEnd);
        return span > 1 && span <= MaximumSpan && toStart <= toEnd && toEnd <= span ? moved : band;
    }

    /// <summary>The settings after an eyedropper click on a colour of this hue: the selected range's band moves.</summary>
    public static HueSaturationSettings Sampled(this HueSaturationSettings settings, double hue, HueSampleMode mode)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Range == ColorRange.Master || settings.Colorize) return settings;

        HueBand band = settings.Band;
        HueBand changed = mode switch
        {
            HueSampleMode.Replace => band.CenteredOn(hue),
            HueSampleMode.Add => band.Include(hue),
            _ => band.Exclude(hue),
        };
        return settings with { Bands = settings.Bands.With(settings.Range, changed) };
    }

    /// <summary>The colour range a hue belongs to most — the one a targeted drag on that colour changes.</summary>
    public static ColorRange RangeFor(HueSaturationSettings settings, double hue)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ColorRanges.Colors.MaxBy(range => settings.Bands.ValueOr(range, range.DefaultBand()).Weight(hue));
    }

    /// <summary>What a hue becomes under the settings, for the spectrum bar that shows the result.</summary>
    public static double ShiftedHue(double hue, HueSaturationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        double shift = 0;
        foreach ((ColorRange range, RangeAdjustment adjustment) in settings.Adjustments)
            if (adjustment.Hue != 0) shift += adjustment.Hue * HueSaturationFilter.Weight(settings, range, hue);
        return Wrap(hue + shift);
    }

    /// <summary>
    /// The hue of an unpremultiplied 0–1 colour, or null for one so near grey that its hue means
    /// nothing — an eyedropper on it does nothing, as upstream's beeps.
    /// </summary>
    public static double? HueOf(double red, double green, double blue)
    {
        Hsb colour = Hsb.FromRgb(red, green, blue);
        return colour.Saturation > 0.02 ? colour.Hue : null;
    }

    private static HueBand Normalized(HueBand band)
    {
        var wrapped = new HueBand(Wrap(band.FalloffStart), Wrap(band.RangeStart), Wrap(band.RangeEnd), Wrap(band.FalloffEnd));
        return HueBand.Forward(wrapped.FalloffStart, wrapped.FalloffEnd) > MaximumSpan
            ? wrapped with { FalloffEnd = Wrap(wrapped.FalloffStart + MaximumSpan) }
            : wrapped;
    }

    private static double Wrap(double degrees) => ((degrees % 360) + 360) % 360;
}

/// <summary>A colour as hue (0–360), saturation and brightness (0–1) — the colour picker's terms.</summary>
public readonly record struct Hsb(double Hue, double Saturation, double Brightness)
{
    public static Hsb FromRgb(double red, double green, double blue)
    {
        double high = Math.Max(red, Math.Max(green, blue)), low = Math.Min(red, Math.Min(green, blue));
        double delta = high - low;
        double hue = 0;
        if (delta > 0)
        {
            if (high == red) hue = 60 * ((green - blue) / delta % 6);
            else if (high == green) hue = 60 * ((blue - red) / delta + 2);
            else hue = 60 * ((red - green) / delta + 4);
        }
        if (hue < 0) hue += 360;
        return new Hsb(hue, high > 0 ? delta / high : 0, high);
    }

    public (double Red, double Green, double Blue) ToRgb()
    {
        double h = (Hue % 360 + 360) % 360 / 60, c = Brightness * Saturation;
        double x = c * (1 - Math.Abs(h % 2 - 1)), m = Brightness - c;
        (double r, double g, double b) = (int)h switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (r + m, g + m, b + m);
    }
}
