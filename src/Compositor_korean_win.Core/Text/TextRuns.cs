using System.Text.Json.Serialization;

namespace Compositor_korean_win.Core;

/// <summary>
/// A stretch of a text layer's characters set differently from the rest: Photoshop's character
/// style run. Only what it changes is set; everything left null comes from the layer.
/// </summary>
/// <remarks>
/// <para>
/// Character-level settings only — face, size and fill — the ones Photoshop lets a selection of
/// letters have. Alignment, tracking, leading and the warp stay the layer's, because this editor
/// sets a text as one paragraph block and those describe the block.
/// </para>
/// <para>
/// <see cref="Start"/> and <see cref="Length"/> count UTF-16 units of <see cref="LayerText.Text"/>,
/// the same units the edit box and Photoshop's run lengths count, so neither side has to convert.
/// </para>
/// </remarks>
public sealed record TextRun
{
    [JsonPropertyName("start")] public int Start { get; init; }
    [JsonPropertyName("length")] public int Length { get; init; }

    [JsonPropertyName("font")] public string? Font { get; init; }
    [JsonPropertyName("size")] public double? Size { get; init; }
    [JsonPropertyName("weight")] public int? Weight { get; init; }
    [JsonPropertyName("italic")] public bool? Italic { get; init; }

    /// <summary>The fill, 0–1 per channel; the three are set together or not at all.</summary>
    [JsonPropertyName("red")] public double? Red { get; init; }
    [JsonPropertyName("green")] public double? Green { get; init; }
    [JsonPropertyName("blue")] public double? Blue { get; init; }

    [JsonIgnore] public int End => Start + Length;

    [JsonIgnore]
    public bool ChangesAnything =>
        Font is not null || Size is not null || Weight is not null || Italic is not null || Red is not null;

    [JsonIgnore]
    public bool IsValid =>
        Start >= 0 && Length > 0 && Start <= LayerText.MaximumCharacters && Length <= LayerText.MaximumCharacters
        && (Font is null || (!string.IsNullOrWhiteSpace(Font) && Font.Length <= 256))
        && (Size is null || (double.IsFinite(Size.Value) && Size.Value is >= 1 and <= 5000))
        && (Weight is null || Weight.Value is >= 1 and <= 1000)
        && (Red is null) == (Green is null) && (Red is null) == (Blue is null)
        && (Red is null || (Unit(Red.Value) && Unit(Green!.Value) && Unit(Blue!.Value)));

    /// <summary>The layer's own style with this run's changes laid over it.</summary>
    public LayerText Over(LayerText layer) => layer with
    {
        Font = Font ?? layer.Font,
        Size = Size ?? layer.Size,
        Weight = Weight ?? layer.Weight,
        Italic = Italic ?? layer.Italic,
        Red = Red ?? layer.Red,
        Green = Green ?? layer.Green,
        Blue = Blue ?? layer.Blue,
        Runs = null,
    };

    /// <summary>The character settings of <paramref name="style"/> that differ from <paramref name="layer"/>'s.</summary>
    public static TextRun Difference(LayerText layer, LayerText style)
    {
        bool colour = style.Red != layer.Red || style.Green != layer.Green || style.Blue != layer.Blue;
        return new TextRun
        {
            Font = string.Equals(style.Font, layer.Font, StringComparison.Ordinal) ? null : style.Font,
            Size = style.Size == layer.Size ? null : style.Size,
            Weight = style.Weight == layer.Weight ? null : style.Weight,
            Italic = style.Italic == layer.Italic ? null : style.Italic,
            Red = colour ? style.Red : null,
            Green = colour ? style.Green : null,
            Blue = colour ? style.Blue : null,
        };
    }

    private static bool Unit(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}

/// <summary>
/// Working with a text layer's runs: the style of each character, restyling a selection, and
/// keeping runs on the right letters while the words are typed.
/// </summary>
/// <remarks>
/// Everything goes through one character-by-character array and back. Texts are held to
/// <see cref="LayerText.MaximumCharacters"/>, so the array costs nothing that matters, and it makes
/// splitting, merging and shifting runs the same simple operation instead of a tangle of interval
/// cases.
/// </remarks>
public static class TextRuns
{
    /// <summary>
    /// Every character's own style: the layer's, or a run's laid over it. Characters in the same
    /// run share one instance, which layout and export use to tell runs apart cheaply.
    /// </summary>
    public static LayerText[] PerCharacter(LayerText text)
    {
        LayerText plain = text with { Runs = null };
        var styles = new LayerText[text.Text.Length];
        Array.Fill(styles, plain);
        if (text.Runs is null) return styles;

        foreach (TextRun run in text.Runs)
        {
            LayerText styled = run.Over(text);
            int end = Math.Min(run.End, styles.Length);
            for (int i = Math.Max(0, run.Start); i < end; i++) styles[i] = styled;
        }
        return styles;
    }

    /// <summary>The style of the character at <paramref name="index"/>, or of the layer past the end.</summary>
    public static LayerText StyleAt(LayerText text, int index)
    {
        if (text.Runs is not null)
            foreach (TextRun run in text.Runs)
                if (index >= run.Start && index < run.End) return run.Over(text);
        return text with { Runs = null };
    }

    /// <summary>
    /// <paramref name="change"/> applied to the characters from <paramref name="start"/> to
    /// <paramref name="end"/>; a change to a paragraph setting still reaches the whole layer.
    /// </summary>
    /// <remarks>
    /// The change is applied to each character's own style, so "one size larger" or "toggle bold"
    /// work on a mixed selection as they would letter by letter. An empty selection changes the
    /// layer itself, runs included, the way Photoshop's options bar does with the cursor parked.
    /// </remarks>
    public static LayerText Restyle(LayerText text, int start, int end, Func<LayerText, LayerText> change)
    {
        start = Math.Clamp(start, 0, text.Text.Length);
        end = Math.Clamp(end, start, text.Text.Length);
        if (start == end) return RestyleAll(text, change);

        LayerText[] styles = PerCharacter(text);

        // Paragraph settings belong to the block: take them from what the change does to the layer.
        LayerText paragraph = change(text with { Runs = null });
        LayerText layer = text with
        {
            Align = paragraph.Align,
            Tracking = paragraph.Tracking,
            Leading = paragraph.Leading,
            Warp = paragraph.Warp,
        };

        var changed = new Dictionary<LayerText, LayerText>(ReferenceEqualityComparer.Instance);
        for (int i = start; i < end; i++)
        {
            LayerText own = styles[i];
            if (!changed.TryGetValue(own, out LayerText? after))
            {
                after = change(own);
                changed[own] = after;
            }
            styles[i] = after;
        }

        return FromCharacters(layer, styles);
    }

    /// <summary>The change applied to the layer and to each of its runs, keeping the runs where they are.</summary>
    private static LayerText RestyleAll(LayerText text, Func<LayerText, LayerText> change)
    {
        LayerText layer = change(text with { Runs = null }) with { Text = text.Text };
        if (text.Runs is null) return layer;

        LayerText[] styles = PerCharacter(text);
        var changed = new Dictionary<LayerText, LayerText>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < styles.Length; i++)
        {
            if (!changed.TryGetValue(styles[i], out LayerText? after))
            {
                after = change(styles[i]);
                changed[styles[i]] = after;
            }
            styles[i] = after;
        }
        return FromCharacters(layer, styles);
    }

    /// <summary>
    /// The layer with its words replaced by <paramref name="words"/> and its runs moved with them:
    /// what came before and after the typed change keeps its style, and new letters take the style
    /// of the letter before them, as typing in Photoshop does.
    /// </summary>
    public static LayerText Retype(LayerText text, string words)
    {
        if (text.Runs is null) return text with { Text = words };

        string before = text.Text;
        int prefix = 0;
        int shortest = Math.Min(before.Length, words.Length);
        while (prefix < shortest && before[prefix] == words[prefix]) prefix++;
        int suffix = 0;
        while (suffix < shortest - prefix && before[^(suffix + 1)] == words[^(suffix + 1)]) suffix++;

        LayerText[] styles = PerCharacter(text);
        LayerText plain = text with { Runs = null };
        LayerText typed = prefix > 0 ? styles[prefix - 1] : styles.Length > 0 ? styles[0] : plain;

        var next = new LayerText[words.Length];
        for (int i = 0; i < prefix; i++) next[i] = styles[i];
        for (int i = prefix; i < words.Length - suffix; i++) next[i] = typed;
        for (int i = 0; i < suffix; i++) next[words.Length - 1 - i] = styles[before.Length - 1 - i];

        return FromCharacters(text with { Text = words }, next);
    }

    /// <summary>
    /// The layer with runs rebuilt from a style per character: only what differs from the layer is
    /// kept, neighbours that look the same become one run, and a text styled all alike has none.
    /// </summary>
    public static LayerText FromCharacters(LayerText layer, IReadOnlyList<LayerText> styles)
    {
        LayerText plain = layer with { Runs = null };
        var runs = new List<TextRun>();
        TextRun? open = null;
        for (int i = 0; i < styles.Count; i++)
        {
            TextRun difference = TextRun.Difference(plain, styles[i]);
            if (!difference.ChangesAnything)
            {
                if (open is not null) runs.Add(open);
                open = null;
                continue;
            }

            if (open is not null && (open with { Start = 0, Length = 0 }) == difference)
            {
                open = open with { Length = open.Length + 1 };
                continue;
            }

            if (open is not null) runs.Add(open);
            open = difference with { Start = i, Length = 1 };
        }
        if (open is not null) runs.Add(open);

        return plain with { Runs = runs.Count == 0 ? null : runs };
    }

    /// <summary>Whether the runs are in order, apart, inside the text and each valid.</summary>
    public static bool AreValid(LayerText text)
    {
        if (text.Runs is null) return true;
        if (text.Runs.Count > LayerText.MaximumCharacters) return false;
        int after = 0;
        foreach (TextRun run in text.Runs)
        {
            if (run is null || !run.IsValid || run.Start < after || run.End > text.Text.Length) return false;
            after = run.End;
        }
        return true;
    }
}
