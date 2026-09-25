using System.Text.Json.Serialization;

namespace Compositor_korean_win.Core;

/// <summary>How the lines of a text layer line up with each other.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TextAlign>))]
public enum TextAlign
{
    [JsonStringEnumMemberName("Left")] Left,
    [JsonStringEnumMemberName("Center")] Center,
    [JsonStringEnumMemberName("Right")] Right,
}

/// <summary>Photoshop's Warp Text styles, by the names its menu gives them.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TextWarpStyle>))]
public enum TextWarpStyle
{
    [JsonStringEnumMemberName("None")] None,
    [JsonStringEnumMemberName("Arc")] Arc,
    [JsonStringEnumMemberName("Arc Lower")] ArcLower,
    [JsonStringEnumMemberName("Arc Upper")] ArcUpper,
    [JsonStringEnumMemberName("Arch")] Arch,
    [JsonStringEnumMemberName("Bulge")] Bulge,
    [JsonStringEnumMemberName("Shell Lower")] ShellLower,
    [JsonStringEnumMemberName("Shell Upper")] ShellUpper,
    [JsonStringEnumMemberName("Flag")] Flag,
    [JsonStringEnumMemberName("Wave")] Wave,
    [JsonStringEnumMemberName("Fish")] Fish,
    [JsonStringEnumMemberName("Rise")] Rise,
    [JsonStringEnumMemberName("Fisheye")] Fisheye,
    [JsonStringEnumMemberName("Inflate")] Inflate,
    [JsonStringEnumMemberName("Squeeze")] Squeeze,
    [JsonStringEnumMemberName("Twist")] Twist,
}

/// <summary>A warp: a style and how hard it bends, each amount from −100 to 100 as Photoshop's.</summary>
public sealed record TextWarp
{
    [JsonPropertyName("style")] public TextWarpStyle Style { get; init; }
    [JsonPropertyName("bend")] public double Bend { get; init; } = 50;
    [JsonPropertyName("horizontal")] public double Horizontal { get; init; }
    [JsonPropertyName("vertical")] public double Vertical { get; init; }

    [JsonIgnore]
    public bool IsValid =>
        Enum.IsDefined(Style) && InRange(Bend) && InRange(Horizontal) && InRange(Vertical);

    [JsonIgnore]
    public bool IsIdentity => Style == TextWarpStyle.None || (Bend == 0 && Horizontal == 0 && Vertical == 0);

    private static bool InRange(double value) => double.IsFinite(value) && value is >= -100 and <= 100;
}

/// <summary>
/// What a text layer says and how it looks, kept so the words can be set again after an edit.
/// </summary>
/// <remarks>
/// <para>
/// Upstream has no text at all. This follows the way it keeps a shape layer
/// (<see cref="LayerShapeStyle"/>): the layer's pixels stay an ordinary raster that clips, masks,
/// blends and filters like any other, and this record is the recipe that drew them. That keeps a
/// project with text readable by the macOS build, which sees a picture of the words and ignores a
/// key it does not know.
/// </para>
/// <para>
/// Once something other than the text tool changes those pixels — a brush, a filter — the recipe
/// no longer describes them. <see cref="Rendered"/> is how that is noticed: the layer is live text
/// only while its raster is the very buffer the recipe drew, the rule upstream uses for shapes.
/// </para>
/// </remarks>
public sealed record LayerText
{
    [JsonPropertyName("text")] public string Text { get; init; } = "";

    /// <summary>A font family name as Windows lists it, such as "Malgun Gothic".</summary>
    [JsonPropertyName("font")] public string Font { get; init; } = "Malgun Gothic";

    /// <summary>The em size in the layer's own pixels.</summary>
    [JsonPropertyName("size")] public double Size { get; init; } = 72;

    /// <summary>100 to 900, as CSS and DirectWrite count it; 400 is regular and 700 bold.</summary>
    [JsonPropertyName("weight")] public int Weight { get; init; } = 400;

    [JsonPropertyName("italic")] public bool Italic { get; init; }

    [JsonPropertyName("red")] public double Red { get; init; }
    [JsonPropertyName("green")] public double Green { get; init; }
    [JsonPropertyName("blue")] public double Blue { get; init; }

    [JsonPropertyName("align")] public TextAlign Align { get; init; }

    /// <summary>Extra space after every character, in thousandths of an em — Photoshop's tracking.</summary>
    [JsonPropertyName("tracking")] public double Tracking { get; init; }

    /// <summary>Baseline to baseline, as a multiple of the size; Photoshop's auto leading is 1.2.</summary>
    [JsonPropertyName("leading")] public double Leading { get; init; } = 1.2;

    [JsonPropertyName("warp")] public TextWarp? Warp { get; init; }

    /// <summary>
    /// Letters set differently from the rest — a bigger word, a coloured syllable — in order and
    /// apart (<see cref="TextRun"/>). Null when every letter looks the same, which keeps a project
    /// of plain text byte for byte what it was before runs existed.
    /// </summary>
    [JsonPropertyName("runs")] public IReadOnlyList<TextRun>? Runs { get; init; }

    /// <summary>
    /// Where the text was set from, in the raster's own pixels: the start of the first baseline for
    /// left-aligned text, its middle or end otherwise. Setting the text again keeps this point
    /// where it is on the document, so typing grows the words away from where they were clicked.
    /// </summary>
    [JsonPropertyName("anchorX")] public double AnchorX { get; init; }
    [JsonPropertyName("anchorY")] public double AnchorY { get; init; }

    /// <summary>The raster this recipe drew. Not stored: a loaded layer's image is the one saved.</summary>
    [JsonIgnore] public PixelBuffer? Rendered { get; init; }

    [JsonIgnore]
    public Rgba Colour => new(Channel(Red), Channel(Green), Channel(Blue));

    [JsonIgnore]
    public TextFace Face => new(Font, Weight, Italic);

    public const int MaximumCharacters = 10_000;

    [JsonIgnore]
    public bool IsValid =>
        Text.Length <= MaximumCharacters
        && !string.IsNullOrWhiteSpace(Font) && Font.Length <= 256
        && double.IsFinite(Size) && Size is >= 1 and <= 5000
        && Weight is >= 1 and <= 1000
        && Unit(Red) && Unit(Green) && Unit(Blue)
        && Enum.IsDefined(Align)
        && double.IsFinite(Tracking) && Tracking is >= -1000 and <= 10_000
        && double.IsFinite(Leading) && Leading is >= 0.1 and <= 20
        && (Warp is null || Warp.IsValid)
        && TextRuns.AreValid(this)
        && double.IsFinite(AnchorX) && double.IsFinite(AnchorY);

    public LayerText WithColour(Rgba colour) => this with
    {
        Red = colour.R / 255.0,
        Green = colour.G / 255.0,
        Blue = colour.B / 255.0,
    };

    private static bool Unit(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    private static byte Channel(double value) =>
        (byte)Math.Clamp(Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
}
