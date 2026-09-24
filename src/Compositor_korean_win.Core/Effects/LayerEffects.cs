using System.Text.Json.Serialization;

namespace Compositor_korean_win.Core;

/// <summary>Where a stroke sits against the layer's edge.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StrokePosition>))]
public enum StrokePosition
{
    [JsonStringEnumMemberName("Outside")] Outside,
    [JsonStringEnumMemberName("Inside")] Inside,
    [JsonStringEnumMemberName("Center")] Center,
}

/// <summary>A drop shadow: the layer's shape, blurred, offset and tinted, beneath it.</summary>
/// <remarks>Distances are document pixels, whatever the layer is scaled to, as Photoshop's are.</remarks>
public sealed record ShadowEffect
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
    [JsonPropertyName("red")] public double Red { get; init; }
    [JsonPropertyName("green")] public double Green { get; init; }
    [JsonPropertyName("blue")] public double Blue { get; init; }
    [JsonPropertyName("opacity")] public double Opacity { get; init; } = 0.75;
    [JsonPropertyName("blendMode")] public LayerBlendMode Blend { get; init; } = LayerBlendMode.Multiply;

    /// <summary>Where the light comes from, in degrees anticlockwise from the right; 120 is upper left.</summary>
    [JsonPropertyName("angle")] public double Angle { get; init; } = 120;
    [JsonPropertyName("distance")] public double Distance { get; init; } = 10;

    /// <summary>How far the shadow's edge softens.</summary>
    [JsonPropertyName("size")] public double Size { get; init; } = 10;

    /// <summary>0 to 1: how much of <see cref="Size"/> grows the shape before the rest blurs it.</summary>
    [JsonPropertyName("spread")] public double Spread { get; init; }

    [JsonIgnore] public Rgba Colour => Effects.Colour(Red, Green, Blue);

    /// <summary>The offset on the document, away from the light.</summary>
    [JsonIgnore]
    public Point Offset => new(-Math.Cos(Angle * Math.PI / 180) * Distance, Math.Sin(Angle * Math.PI / 180) * Distance);

    [JsonIgnore]
    public bool IsValid => Effects.Unit(Red) && Effects.Unit(Green) && Effects.Unit(Blue) && Effects.Unit(Opacity)
                           && Enum.IsDefined(Blend) && double.IsFinite(Angle)
                           && Effects.Length(Distance, 30_000) && Effects.Length(Size, 250) && Effects.Unit(Spread);
}

/// <summary>An outer glow: the layer's shape, spread and blurred, beneath it.</summary>
public sealed record GlowEffect
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
    [JsonPropertyName("red")] public double Red { get; init; } = 1;
    [JsonPropertyName("green")] public double Green { get; init; } = 1;
    [JsonPropertyName("blue")] public double Blue { get; init; } = 0.75;
    [JsonPropertyName("opacity")] public double Opacity { get; init; } = 0.75;
    [JsonPropertyName("blendMode")] public LayerBlendMode Blend { get; init; } = LayerBlendMode.Screen;
    [JsonPropertyName("size")] public double Size { get; init; } = 10;
    [JsonPropertyName("spread")] public double Spread { get; init; }

    [JsonIgnore] public Rgba Colour => Effects.Colour(Red, Green, Blue);

    [JsonIgnore]
    public bool IsValid => Effects.Unit(Red) && Effects.Unit(Green) && Effects.Unit(Blue) && Effects.Unit(Opacity)
                           && Enum.IsDefined(Blend) && Effects.Length(Size, 250) && Effects.Unit(Spread);
}

/// <summary>A stroke round the layer's shape, over it.</summary>
public sealed record StrokeEffect
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
    [JsonPropertyName("red")] public double Red { get; init; }
    [JsonPropertyName("green")] public double Green { get; init; }
    [JsonPropertyName("blue")] public double Blue { get; init; }
    [JsonPropertyName("opacity")] public double Opacity { get; init; } = 1;
    [JsonPropertyName("size")] public double Size { get; init; } = 3;
    [JsonPropertyName("position")] public StrokePosition Position { get; init; } = StrokePosition.Outside;

    [JsonIgnore] public Rgba Colour => Effects.Colour(Red, Green, Blue);

    [JsonIgnore]
    public bool IsValid => Effects.Unit(Red) && Effects.Unit(Green) && Effects.Unit(Blue) && Effects.Unit(Opacity)
                           && Effects.Length(Size, 250) && Size > 0 && Enum.IsDefined(Position);
}

/// <summary>
/// A layer's styles — Photoshop's layer effects, the three a poster reaches for.
/// </summary>
/// <remarks>
/// <para>
/// Non-destructive, as Photoshop's are: the layer's own pixels are never touched, and the effects
/// are drawn from its shape every time it composites (<see cref="EffectRendering"/>). Moving,
/// scaling or repainting the layer carries them along for free.
/// </para>
/// <para>
/// Upstream has none. They are stored under a key of their own, so the macOS build opens such a
/// project and simply shows the layer plain.
/// </para>
/// </remarks>
public sealed record LayerEffects
{
    [JsonPropertyName("shadow")] public ShadowEffect? Shadow { get; init; }
    [JsonPropertyName("glow")] public GlowEffect? Glow { get; init; }
    [JsonPropertyName("stroke")] public StrokeEffect? Stroke { get; init; }

    [JsonIgnore]
    public bool IsVisible => Shadow is { Enabled: true, Opacity: > 0 }
                             || Glow is { Enabled: true, Opacity: > 0 }
                             || Stroke is { Enabled: true, Opacity: > 0 };

    [JsonIgnore]
    public bool IsEmpty => Shadow is null && Glow is null && Stroke is null;

    [JsonIgnore]
    public bool IsValid => (Shadow?.IsValid ?? true) && (Glow?.IsValid ?? true) && (Stroke?.IsValid ?? true);
}

internal static class Effects
{
    public static bool Unit(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    public static bool Length(double value, double limit) => double.IsFinite(value) && value >= 0 && value <= limit;

    public static Rgba Colour(double red, double green, double blue) => new(Channel(red), Channel(green), Channel(blue));

    private static byte Channel(double value) =>
        (byte)Math.Clamp(Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
}
