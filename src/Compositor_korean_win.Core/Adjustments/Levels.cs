using System.Text.Json.Serialization;

namespace Compositor_korean_win.Core;

/// <summary>The composite channel and the three colour channels, in Photoshop's menu order.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LevelsChannel>))]
public enum LevelsChannel
{
    [JsonStringEnumMemberName("RGB")] Rgb,
    [JsonStringEnumMemberName("Red")] Red,
    [JsonStringEnumMemberName("Green")] Green,
    [JsonStringEnumMemberName("Blue")] Blue,
}

/// <summary>One channel's Levels: input black, gamma and white, mapped onto an output range.</summary>
public sealed record LevelRange
{
    [JsonPropertyName("black")] public double Black { get; init; }
    [JsonPropertyName("gamma")] public double Gamma { get; init; } = 1;
    [JsonPropertyName("white")] public double White { get; init; } = 255;
    [JsonPropertyName("outputBlack")] public double OutputBlack { get; init; }
    [JsonPropertyName("outputWhite")] public double OutputWhite { get; init; } = 255;

    /// <summary>
    /// The same range with every value brought inside its limits, non-finite values replaced by
    /// their defaults, and white kept above black.
    /// </summary>
    /// <remarks>
    /// Upstream validates a stored adjustment by checking each range equals its own normalised
    /// form, so this has to clamp exactly as upstream does or valid projects would be rejected.
    /// </remarks>
    [JsonIgnore]
    public LevelRange Normalized
    {
        get
        {
            double black = Clamp(Black, 0, 254, 0);
            double white = Clamp(White, black + 1, 255, 255);
            return new LevelRange
            {
                Black = black,
                White = white,
                Gamma = Clamp(Gamma, 0.1, 9.99, 1),
                OutputBlack = Clamp(OutputBlack, 0, 255, 0),
                OutputWhite = Clamp(OutputWhite, 0, 255, 255),
            };
        }
    }

    private static double Clamp(double value, double low, double high, double fallback) =>
        double.IsFinite(value) ? Math.Min(high, Math.Max(low, value)) : fallback;

    /// <summary>Maps one 0–1 value through this range.</summary>
    public double Apply(double value)
    {
        LevelRange s = Normalized;
        double input = Math.Min(1, Math.Max(0, (value * 255 - s.Black) / (s.White - s.Black)));
        return (s.OutputBlack + Math.Pow(input, 1 / s.Gamma) * (s.OutputWhite - s.OutputBlack)) / 255;
    }
}

/// <summary>Levels for all four channels, plus which one the panel is editing.</summary>
public sealed record LevelsSettings
{
    [JsonPropertyName("channel")] public LevelsChannel Channel { get; init; } = LevelsChannel.Rgb;

    [JsonPropertyName("ranges")]
    [JsonConverter(typeof(EquatableListConverter<LevelRange>))]
    public EquatableList<LevelRange> Ranges { get; init; } = new([new(), new(), new(), new()]);

    [JsonIgnore]
    public bool IsIdentity => Ranges.All(range => range.Normalized == new LevelRange());

    /// <summary>A channel's adjustment, then the composite one — the order they apply in.</summary>
    public double Apply(double value, LevelsChannel channel) =>
        Ranges[0].Apply(Ranges[(int)channel].Apply(value));
}
