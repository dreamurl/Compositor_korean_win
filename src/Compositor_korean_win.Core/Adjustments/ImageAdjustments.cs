using System.Text.Json.Serialization;

namespace Compositor_korean_win.Core;

internal static class AdjustmentMath
{
    /// <summary>Brings a value inside a range, replacing a non-finite one with a fallback.</summary>
    public static double Clamp(double value, double low, double high, double fallback) =>
        double.IsFinite(value) ? Math.Min(high, Math.Max(low, value)) : fallback;
}

/// <summary>A straight sRGB colour stored with an adjustment, 0–1 per channel.</summary>
public sealed record AdjustmentColor
{
    [JsonPropertyName("red")] public double Red { get; init; }
    [JsonPropertyName("green")] public double Green { get; init; }
    [JsonPropertyName("blue")] public double Blue { get; init; }

    public AdjustmentColor() { }

    public AdjustmentColor(double red, double green, double blue)
    {
        Red = red;
        Green = green;
        Blue = blue;
    }

    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(Red) && Red is >= 0 and <= 1
        && double.IsFinite(Green) && Green is >= 0 and <= 1
        && double.IsFinite(Blue) && Blue is >= 0 and <= 1;

    [JsonIgnore]
    public AdjustmentColor Clamped => new(AdjustmentMath.Clamp(Red, 0, 1, 0),
                                          AdjustmentMath.Clamp(Green, 0, 1, 0),
                                          AdjustmentMath.Clamp(Blue, 0, 1, 0));
}

/// <summary>
/// Photoshop's Exposure: stops scale linear light, an offset shifts it, then gamma bends the
/// result. The same curve runs on every channel and alpha is kept.
/// </summary>
public sealed record ExposureSettings
{
    /// <summary>Stops of light, −20…20.</summary>
    [JsonPropertyName("exposure")] public double Exposure { get; init; }

    /// <summary>Added in linear light, −0.5…0.5: negative deepens the shadows.</summary>
    [JsonPropertyName("offset")] public double Offset { get; init; }

    /// <summary>Gamma correction, 0.01…9.99; above 1 brightens the midtones.</summary>
    [JsonPropertyName("gamma")] public double Gamma { get; init; } = 1;

    [JsonIgnore]
    public bool IsValid => Exposure is >= -20 and <= 20 && Offset is >= -0.5 and <= 0.5
                        && Gamma is >= 0.01 and <= 9.99;

    [JsonIgnore]
    public ExposureSettings Normalized => new()
    {
        Exposure = AdjustmentMath.Clamp(Exposure, -20, 20, 0),
        Offset = AdjustmentMath.Clamp(Offset, -0.5, 0.5, 0),
        Gamma = AdjustmentMath.Clamp(Gamma, 0.01, 9.99, 1),
    };

    /// <summary>
    /// Each input byte's output, 0–1: decoded out of sRGB to linear light, adjusted, encoded back.
    /// </summary>
    public float[] Table()
    {
        double scale = Math.Pow(2, Exposure);
        float[] table = new float[256];
        for (int index = 0; index < 256; index++)
        {
            double encoded = index / 255.0;
            double linear = encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
            linear = Math.Pow(Math.Max(0, linear * scale + Offset), 1 / Gamma);
            double output = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
            table[index] = (float)Math.Min(1, Math.Max(0, output));
        }
        return table;
    }
}

/// <summary>
/// Gradient Map: a pixel's brightness picks a colour between the two ends, and alpha is kept.
/// </summary>
public sealed record GradientMapSettings
{
    [JsonPropertyName("shadows")] public AdjustmentColor Shadows { get; init; } = new(0, 0, 0);
    [JsonPropertyName("highlights")] public AdjustmentColor Highlights { get; init; } = new(1, 1, 1);
    [JsonPropertyName("reversed")] public bool Reversed { get; init; }

    [JsonIgnore]
    public bool IsValid => Shadows.IsValid && Highlights.IsValid;

    [JsonIgnore]
    public GradientMapSettings Normalized => this with
    {
        Shadows = Shadows.Clamped,
        Highlights = Highlights.Clamped,
    };

    /// <summary>The colours for the darkest and lightest tones, in the order they apply.</summary>
    [JsonIgnore]
    public (AdjustmentColor Dark, AdjustmentColor Light) Ends =>
        Reversed ? (Highlights, Shadows) : (Shadows, Highlights);

    /// <summary>The 256 × 3 straight sRGB table the <c>adjust_gradient_map</c> kernel takes.</summary>
    public byte[] Table()
    {
        (AdjustmentColor dark, AdjustmentColor light) = Ends;
        byte[] table = new byte[256 * 3];
        for (int index = 0; index < 256; index++)
        {
            double t = index / 255.0;
            table[index * 3 + 0] = Byte(dark.Red + (light.Red - dark.Red) * t);
            table[index * 3 + 1] = Byte(dark.Green + (light.Green - dark.Green) * t);
            table[index * 3 + 2] = Byte(dark.Blue + (light.Blue - dark.Blue) * t);
        }
        return table;

        static byte Byte(double value) =>
            (byte)Math.Min(255, Math.Max(0, Math.Round(value * 255, MidpointRounding.AwayFromZero)));
    }
}

/// <summary>
/// Film grain: brightness noise, strongest in the midtones, with its pattern fixed in document
/// space by the seed so it stays put as the canvas pans or redraws part of the image.
/// </summary>
public sealed record GrainSettings
{
    /// <summary>Strength, 0–100.</summary>
    [JsonPropertyName("amount")] public double Amount { get; init; } = 25;

    /// <summary>Grain scale in document pixels, 0.5–20.</summary>
    [JsonPropertyName("size")] public double Size { get; init; } = 1.5;

    /// <summary>0–100: how much per-pixel noise roughens the smooth grain.</summary>
    [JsonPropertyName("roughness")] public double Roughness { get; init; } = 50;

    [JsonPropertyName("seed")] public uint Seed { get; init; }

    [JsonIgnore]
    public bool IsValid => Amount is >= 0 and <= 100 && Size is >= 0.5 and <= 20
                        && Roughness is >= 0 and <= 100;

    [JsonIgnore]
    public GrainSettings Normalized => this with
    {
        Amount = AdjustmentMath.Clamp(Amount, 0, 100, 25),
        Size = AdjustmentMath.Clamp(Size, 0.5, 20, 1.5),
        Roughness = AdjustmentMath.Clamp(Roughness, 0, 100, 50),
    };
}
