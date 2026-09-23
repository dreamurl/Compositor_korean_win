namespace Compositor_korean_win.Core;

/// <summary>What a Levels eyedropper sets from the colour it is clicked on.</summary>
public enum LevelsSample
{
    Black,
    Gray,
    White,
}

/// <summary>Levels' Auto buttons.</summary>
public enum LevelsAuto
{
    /// <summary>One black and white point for all three channels, which keeps their balance.</summary>
    Contrast,

    /// <summary>Each channel stretched on its own, which also removes a colour cast.</summary>
    Color,

    /// <summary>As <see cref="Color"/>, and each channel's midtones pulled to grey.</summary>
    ColorNeutral,
}

/// <summary>
/// Auto Levels and the eyedroppers — upstream's <c>LevelsAutomatic.swift</c>, which Photoshop's
/// Auto Options describe: clip a tenth of a percent at each end, per channel or together.
/// </summary>
public static class LevelsAutomatic
{
    /// <summary>The share of the pixels Auto lets go to pure black and pure white at each end.</summary>
    private const double Clip = 0.001;

    /// <summary>The settings an Auto button gives for this histogram.</summary>
    public static LevelsSettings Auto(LevelsAuto mode, Histogram histogram)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        LevelRange[] ranges = [new(), new(), new(), new()];

        if (mode == LevelsAuto.Contrast)
        {
            // A shared interval preserves the channels' relationship to each other.
            var limits = Enumerable.Range(1, 3)
                .Select(channel => Endpoints(histogram.Channel(channel).ToArray()))
                .OfType<(double Low, double High)>()
                .ToList();
            if (limits.Count > 0)
            {
                double low = limits.Min(limit => limit.Low), high = limits.Max(limit => limit.High);
                if (low < high) ranges[0] = new LevelRange { Black = low, White = high };
            }
        }
        else
        {
            for (int channel = 1; channel <= 3; channel++)
            {
                double[] bins = histogram.Channel(channel).ToArray();
                if (Endpoints(bins) is not var (low, high)) continue;

                var range = new LevelRange { Black = low, White = high };
                if (mode == LevelsAuto.ColorNeutral)
                {
                    // The gamma that brings the stretched channel's mean to middle grey.
                    double total = bins.Sum(), mean = 0;
                    for (int i = 0; i < bins.Length; i++) mean += range.Apply(i / 255.0) * bins[i];
                    mean /= total;
                    if (mean is > 0 and < 1) range = range with { Gamma = Math.Clamp(Math.Log(mean) / Math.Log(0.5), 0.1, 9.99) };
                }
                ranges[channel] = range;
            }
        }

        return new LevelsSettings { Ranges = new EquatableList<LevelRange>(ranges) };
    }

    /// <summary>The darkest and lightest levels with more than <see cref="Clip"/> of the pixels beyond them.</summary>
    private static (double Low, double High)? Endpoints(double[] bins)
    {
        double total = bins.Sum();
        if (total <= 0) return null;

        int low = 0, high = 255;
        double sum = 0;
        for (int i = 0; i < 256; i++)
        {
            sum += bins[i];
            if (sum > total * Clip)
            {
                low = i;
                break;
            }
        }

        sum = 0;
        for (int i = 255; i >= 0; i--)
        {
            sum += bins[i];
            if (sum > total * Clip)
            {
                high = i;
                break;
            }
        }

        return low < high ? (low, high) : null;
    }

    /// <summary>
    /// The settings after an eyedropper click on a colour, in unpremultiplied 0–1 RGB. All three
    /// channels are calibrated together, and the composite channel goes back to doing nothing — the
    /// sample says what each channel should do, and a composite on top would undo it.
    /// </summary>
    public static LevelsSettings Sampling(this LevelsSettings settings, double red, double green, double blue, LevelsSample mode)
    {
        ArgumentNullException.ThrowIfNull(settings);
        double[] rgb = [red, green, blue];
        LevelRange[] ranges = [.. settings.Ranges];
        ranges[0] = new LevelRange();

        for (int channel = 1; channel <= 3; channel++)
        {
            LevelRange range = ranges[channel];
            double value = rgb[channel - 1] * 255;

            switch (mode)
            {
                case LevelsSample.Black:
                    range = range with { Black = Math.Min(range.White - 1, Math.Max(0, value)) };
                    break;
                case LevelsSample.White:
                    range = range with { White = Math.Max(range.Black + 1, Math.Min(255, value)) };
                    break;
                default:
                    double fraction = (value - range.Black) / (range.White - range.Black);
                    if (fraction is <= 0 or >= 1) continue;
                    range = range with { Gamma = Math.Log(fraction) / Math.Log(0.5) };
                    break;
            }

            ranges[channel] = (range with { OutputBlack = 0, OutputWhite = 255 }).Normalized;
        }

        return settings with { Ranges = new EquatableList<LevelRange>(ranges) };
    }

    /// <summary>
    /// How tall the histogram's graph should be: the tallest bar, unless a few spikes — the pure
    /// black and white ends, usually — would flatten everything else, in which case four times a
    /// typical tall bar, and the spikes run off the top. Upstream's <c>LevelsHistogramDisplay</c>.
    /// </summary>
    public static double DisplayScale(ReadOnlySpan<double> bins)
    {
        double peak = 0;
        foreach (double bin in bins)
            if (double.IsFinite(bin) && bin > 0) peak = Math.Max(peak, bin);
        if (peak <= 0) return 0;

        var interior = new List<double>();
        for (int i = 1; i < bins.Length - 1; i++)
            if (double.IsFinite(bins[i]) && bins[i] > 0) interior.Add(bins[i]);
        if (interior.Count == 0) return peak;

        interior.Sort();
        double typical = interior[(int)((interior.Count - 1) * 0.95)];
        return Math.Min(peak, typical * 4);
    }
}
