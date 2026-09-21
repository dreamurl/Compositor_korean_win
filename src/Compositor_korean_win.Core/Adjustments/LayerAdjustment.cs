using System.Text.Json.Serialization;

namespace Compositor_korean_win.Core;

/// <summary>The six kinds of adjustment a layer can be.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AdjustmentKind>))]
public enum AdjustmentKind
{
    [JsonStringEnumMemberName("Hue/Saturation")] Hsv,
    [JsonStringEnumMemberName("Levels")] Levels,
    [JsonStringEnumMemberName("Curves")] Curves,
    [JsonStringEnumMemberName("Exposure")] Exposure,
    [JsonStringEnumMemberName("Gradient Map")] GradientMap,
    [JsonStringEnumMemberName("Grain")] Grain,
}

/// <summary>
/// An adjustment layer's settings: one kind, and the parameters for every kind alongside it.
/// </summary>
/// <remarks>
/// The shape is upstream's, including its history. <c>hue</c>, <c>saturation</c>, <c>lightness</c>
/// and <c>colorize</c> sit at the top level because that is how Hue/Saturation was stored before it
/// grew per-range bands; <c>hsvSettings</c> supersedes them and falls back to them when a project
/// predates it. The three settings added later are optional for the same reason — a project saved
/// before they existed decodes, and saves again, exactly as it was.
///
/// Version 7 of the format is what carries this. Reading it is what lets this port open a project
/// the macOS build saved, since that build writes version 7 by default.
/// </remarks>
public sealed record LayerAdjustment
{
    [JsonPropertyName("kind")] public AdjustmentKind Kind { get; init; }

    [JsonPropertyName("hue")] public double Hue { get; init; }
    [JsonPropertyName("saturation")] public double Saturation { get; init; }
    [JsonPropertyName("lightness")] public double Lightness { get; init; }
    [JsonPropertyName("colorize")] public bool Colorize { get; init; }

    /// <summary>Range-aware Hue/Saturation. Absent in projects saved before it existed.</summary>
    [JsonPropertyName("hsvSettings")] public HueSaturationSettings? HsvSettings { get; init; }

    [JsonPropertyName("levels")] public LevelsSettings Levels { get; init; } = new();
    [JsonPropertyName("curves")] public CurvesSettings Curves { get; init; } = new();

    [JsonPropertyName("exposureSettings")] public ExposureSettings? ExposureSettings { get; init; }
    [JsonPropertyName("gradientMapSettings")] public GradientMapSettings? GradientMapSettings { get; init; }
    [JsonPropertyName("grainSettings")] public GrainSettings? GrainSettings { get; init; }

    public LayerAdjustment() { }

    public LayerAdjustment(AdjustmentKind kind) => Kind = kind;

    /// <summary>The range-aware settings, standing in for the older flat fields when absent.</summary>
    [JsonIgnore]
    public HueSaturationSettings ResolvedHsv =>
        HsvSettings ?? HueSaturationSettings.From(Hue, Saturation, Lightness, Colorize);

    [JsonIgnore]
    public ExposureSettings Exposure => ExposureSettings ?? new ExposureSettings();

    [JsonIgnore]
    public GradientMapSettings GradientMap => GradientMapSettings ?? new GradientMapSettings();

    [JsonIgnore]
    public GrainSettings Grain => GrainSettings ?? new GrainSettings();

    /// <summary>
    /// What upstream's <c>ProjectStore</c> requires before an adjustment may replace the open
    /// document.
    /// </summary>
    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(Hue) && double.IsFinite(Saturation) && double.IsFinite(Lightness)
        && Math.Abs(Hue) <= 360 && Math.Abs(Saturation) <= 100 && Math.Abs(Lightness) <= 100
        && ResolvedHsv.Adjustments.Values.All(adjustment =>
            double.IsFinite(adjustment.Hue) && Math.Abs(adjustment.Hue) <= 360
            && double.IsFinite(adjustment.Saturation) && Math.Abs(adjustment.Saturation) <= 100
            && double.IsFinite(adjustment.Lightness) && Math.Abs(adjustment.Lightness) <= 100)
        && ResolvedHsv.Bands.Values.All(band => band.Handles.All(double.IsFinite))
        && Levels.Ranges.Count == 4
        && Levels.Ranges.All(range => range == range.Normalized)
        && Curves.IsValid
        && Exposure.IsValid && GradientMap.IsValid && Grain.IsValid;
}

/// <summary>The two shapes the Shape tool draws.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ShapeKind>))]
public enum ShapeKind
{
    [JsonStringEnumMemberName("Rectangle")] Rectangle,
    [JsonStringEnumMemberName("Ellipse")] Ellipse,
}

/// <summary>
/// What a shape layer drew, kept so the shape can be drawn again at a new size.
/// </summary>
/// <remarks>
/// A shape layer's pixels are an ordinary raster — it clips, masks, blends and filters like any
/// layer. This record is only the recipe. Once anything else changes those pixels the layer is
/// plain pixels from then on, which upstream detects by the raster no longer being the one the
/// shape drew.
/// </remarks>
public sealed record LayerShapeStyle
{
    [JsonPropertyName("kind")] public ShapeKind Kind { get; init; }
    [JsonPropertyName("red")] public double Red { get; init; }
    [JsonPropertyName("green")] public double Green { get; init; }
    [JsonPropertyName("blue")] public double Blue { get; init; }

    /// <summary>Document pixels, whatever size the shape is scaled to.</summary>
    [JsonPropertyName("cornerRadius")] public double CornerRadius { get; init; }
}
