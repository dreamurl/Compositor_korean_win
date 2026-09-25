using System.Globalization;
using System.Text;

namespace Compositor_korean_win.Core;

/// <summary>A Photoshop type layer read as this editor's text, and what could not be carried exactly.</summary>
/// <param name="Text">
/// The words and their look; the anchor and raster are set by the importer. Its fonts, the layer's
/// and its runs', are still Photoshop's PostScript names for the importer to find installed.
/// </param>
/// <param name="PostScriptName">The layer's font as Photoshop names it.</param>
/// <param name="Simplified">A setting had no equivalent here, or differed between runs where this editor has one per layer.</param>
/// <param name="AnchorX">The start of the first baseline on the document, where Photoshop's text origin is.</param>
internal sealed record PsdTypeLayer(LayerText Text, string PostScriptName, bool Simplified, double AnchorX, double AnchorY);

/// <summary>
/// Reads a Photoshop type layer's <c>TySh</c> block: the text engine's data for the words, the
/// font, the size and spacing, and the warp.
/// </summary>
/// <remarks>
/// <para>
/// Photoshop keeps its type in two layers of encoding. The block is an ordinary descriptor holding
/// the transform, the warp and an <c>EngineData</c> blob; the blob is the text engine's own
/// PostScript-like dictionary (<see cref="EngineData"/>) with the characters, the runs of character
/// styles over them, the paragraph styles, and the fonts by PostScript name.
/// </para>
/// <para>
/// Runs of letters in another face, size or colour come across as runs (<see cref="TextRun"/>),
/// over the style that covers the most letters. Settings this editor keeps once per layer —
/// tracking, leading, caps — are taken from that main style, and a layer whose runs differ in them
/// says it was simplified. Point type
/// comes across; paragraph (box) type, which wraps inside a frame this editor does not have, and
/// type turned or skewed by its transform stay as Photoshop's pixels, as before.
/// </para>
/// </remarks>
internal static class PsdType
{
    /// <summary>A live text layer as Photoshop's editable <c>TySh</c> block.</summary>
    public static PsdBlockWriter? Writer(ImageLayer layer)
    {
        if (!layer.IsLiveText || layer.Text is not LayerText text || layer.Image is not PixelBuffer image) return null;

        return new PsdBlockWriter("TySh", writer =>
        {
            LayerTransform placement = layer.Transform;
            double scaleX = placement.Size.Width / image.Width * (placement.FlipX ? -1 : 1);
            double scaleY = placement.Size.Height / image.Height * (placement.FlipY ? -1 : 1);
            double cos = Math.Cos(placement.Radians), sin = Math.Sin(placement.Radians);
            Point anchor = LayerGeometry.ToDocument(placement, new Point(text.AnchorX, text.AnchorY), image.Width, image.Height);

            writer.U16(1);
            writer.F64(scaleX * cos);
            writer.F64(scaleX * sin);
            writer.F64(-scaleY * sin);
            writer.F64(scaleY * cos);
            writer.F64(anchor.X);
            writer.F64(anchor.Y);

            writer.U16(50);
            writer.U32(16);
            (string body, List<(LayerText Style, int Length)> runs) = Paragraphs(text);
            string words = body + "\r";
            var textBounds = new PsdDescriptor { ClassId = "bounds" }
                .Add("Left", new PsdUnitFloat("#Pnt", -text.AnchorX))
                .Add("Top ", new PsdUnitFloat("#Pnt", -text.AnchorY))
                .Add("Rght", new PsdUnitFloat("#Pnt", image.Width - text.AnchorX))
                .Add("Btom", new PsdUnitFloat("#Pnt", image.Height - text.AnchorY));
            var inkBounds = new PsdDescriptor { ClassId = "boundingBox" }
                .Add("Left", new PsdUnitFloat("#Pnt", -text.AnchorX))
                .Add("Top ", new PsdUnitFloat("#Pnt", -text.AnchorY))
                .Add("Rght", new PsdUnitFloat("#Pnt", image.Width - text.AnchorX))
                .Add("Btom", new PsdUnitFloat("#Pnt", image.Height - text.AnchorY));
            new PsdDescriptor { ClassId = "TxLr" }
                // The descriptor is null-terminated by its TEXT writer. EngineData alone carries
                // the extra carriage return that closes Photoshop's final paragraph.
                .Add("Txt ", body)
                .Add("textGridding", new PsdEnum("textGridding", "None"))
                .Add("Ornt", new PsdEnum("Ornt", "Hrzn"))
                .Add("AntA", new PsdEnum("Annt", "AnSm"))
                .Add("TxMg", new PsdEnum("TxMg", "TxNM"))
                .Add("bounds", textBounds)
                .Add("boundingBox", inkBounds)
                .Add("TextIndex", 0)
                .Add("EngineData", EngineData.Write(text, words, runs, PostScriptName))
                .Write(writer);

            writer.U16(1);
            writer.U32(16);
            TextWarp warp = text.Warp ?? new TextWarp { Style = TextWarpStyle.None, Bend = 0 };
            new PsdDescriptor { ClassId = "warp" }
                .Add("warpStyle", new PsdEnum("warpStyle", WarpName(warp.Style)))
                .Add("warpValue", warp.Bend)
                .Add("warpPerspective", warp.Horizontal)
                .Add("warpPerspectiveOther", warp.Vertical)
                .Add("warpRotate", new PsdEnum("Ornt", "Hrzn"))
                .Write(writer);

            // Photoshop 2025 writes the TySh rectangle as four signed 32-bit integers. Writing
            // doubles here makes this block 16 bytes too long: permissive readers ignore those
            // bytes, but Photoshop rejects the type layer and rasterises it.
            writer.I32(0);
            writer.I32(0);
            writer.I32(0);
            writer.I32(0);
        });
    }

    private static string WarpName(TextWarpStyle style) => style switch
    {
        TextWarpStyle.Arc => "warpArc",
        TextWarpStyle.ArcLower => "warpArcLower",
        TextWarpStyle.ArcUpper => "warpArcUpper",
        TextWarpStyle.Arch => "warpArch",
        TextWarpStyle.Bulge => "warpBulge",
        TextWarpStyle.ShellLower => "warpShellLower",
        TextWarpStyle.ShellUpper => "warpShellUpper",
        TextWarpStyle.Flag => "warpFlag",
        TextWarpStyle.Wave => "warpWave",
        TextWarpStyle.Fish => "warpFish",
        TextWarpStyle.Rise => "warpRise",
        TextWarpStyle.Fisheye => "warpFisheye",
        TextWarpStyle.Inflate => "warpInflate",
        TextWarpStyle.Squeeze => "warpSqueeze",
        TextWarpStyle.Twist => "warpTwist",
        _ => "warpNone",
    };

    /// <summary>
    /// The words as Photoshop keeps them — lines ended by carriage returns — and the style runs over
    /// them, the closing paragraph mark included: it takes the style of the letter before it, as a
    /// run in Photoshop's own files does.
    /// </summary>
    private static (string Body, List<(LayerText Style, int Length)> Runs) Paragraphs(LayerText text)
    {
        LayerText[] styles = TextRuns.PerCharacter(text);
        var body = new StringBuilder(text.Text.Length);
        var runs = new List<(LayerText Style, int Length)>();

        void Add(LayerText style)
        {
            if (runs.Count > 0 && ReferenceEquals(runs[^1].Style, style)) runs[^1] = (style, runs[^1].Length + 1);
            else runs.Add((style, 1));
        }

        for (int i = 0; i < text.Text.Length; i++)
        {
            char c = text.Text[i];
            if (c == '\r' && i + 1 < text.Text.Length && text.Text[i + 1] == '\n')
            {
                body.Append('\r');
                Add(styles[i]);
                i++;
                continue;
            }
            body.Append(c == '\n' ? '\r' : c);
            Add(styles[i]);
        }
        Add(styles.Length > 0 ? styles[^1] : text with { Runs = null });
        return (body.ToString(), runs);
    }

    /// <summary>A practical PostScript face name from the Windows family and style.</summary>
    private static string PostScriptName(LayerText text)
    {
        string family = text.Font.Trim();
        string stem = family switch
        {
            "Arial" => "ArialMT",
            "Times New Roman" => "TimesNewRomanPSMT",
            "Malgun Gothic" => "MalgunGothic",
            _ => new string(family.Where(char.IsLetterOrDigit).ToArray()),
        };
        if (stem.Length == 0) stem = "ArialMT";
        string style = (text.Weight >= 600, text.Italic) switch
        {
            (true, true) => "-BoldItalic",
            (true, false) when family == "Malgun Gothic" => "Bold",
            (true, false) => "-Bold",
            (false, true) => "-Italic",
            _ => "",
        };
        return stem + style;
    }

    public static PsdTypeLayer? Read(PsdReader block)
    {
        if (block.Remaining < 2 + 48 + 2 + 4 || block.U16() != 1) return null;
        double xx = block.F64(), xy = block.F64(), yx = block.F64(), yy = block.F64(), tx = block.F64(), ty = block.F64();
        if (block.U16() != 50 || block.U32() != 16) return null;
        PsdDescriptor text = PsdDescriptor.Read(block);

        TextWarp? warp = null;
        bool simplified = false;
        if (block.Remaining >= 6 && block.U16() == 1 && block.U32() == 16)
        {
            PsdDescriptor warping = PsdDescriptor.Read(block);
            (warp, bool turned) = Warp(warping);
            simplified |= turned;
        }

        // Photoshop writes four signed 32-bit bounds. Releases of this editor briefly wrote four
        // doubles instead, so imports still accept that 32-byte form to recover those documents.
        double left, top, right, bottom;
        if (block.Remaining == 4 * sizeof(int))
        {
            left = block.I32();
            top = block.I32();
            right = block.I32();
            bottom = block.I32();
        }
        else if (block.Remaining >= 4 * sizeof(double))
        {
            left = block.F64();
            top = block.F64();
            right = block.F64();
            bottom = block.F64();
        }
        else if (block.Remaining >= 4 * sizeof(int))
        {
            left = block.I32();
            top = block.I32();
            right = block.I32();
            bottom = block.I32();
        }
        else return null;
        if (!double.IsFinite(left) || !double.IsFinite(top)
            || !double.IsFinite(right) || !double.IsFinite(bottom)) return null;

        // Only an upright, evenly scaled transform can be carried: this editor's text has a size,
        // not a matrix. The scale goes into the size, as Photoshop shows it in the Character panel.
        double scale = xx;
        if (!(scale > 0) || !double.IsFinite(scale) || Math.Abs(xy) > 1e-6 * scale || Math.Abs(yx) > 1e-6 * scale
            || Math.Abs(xx - yy) > 1e-4 * scale || !double.IsFinite(tx) || !double.IsFinite(ty)) return null;

        if (text["EngineData"] is not byte[] data) return null;
        if (EngineData.Parse(data) is not Dictionary<string, object?> root
            || Dict(root, "EngineDict") is not Dictionary<string, object?> engine) return null;

        // Box type wraps to its frame; there is no frame here to wrap to.
        if (Path(engine, "Rendered", "Shapes", "Children") is List<object?> { Count: > 0 } shapes
            && shapes[0] is Dictionary<string, object?> shape && Number(shape, "ShapeType") is double kind && kind != 0) return null;

        if (Path(engine, "Editor", "Text") is not string words) return null;
        Dictionary<string, object?>? resources = Dict(root, "ResourceDict") ?? Dict(root, "DocumentResources");

        (Dictionary<string, object?> style, bool mixed, List<(Dictionary<string, object?> Style, int Count)> segments) =
            Styles(engine, resources, words.Length);
        simplified |= mixed;
        Dictionary<string, object?> paragraph = MainParagraph(engine, resources);

        string font = FontName(resources, style) ?? "";
        double fontSize = Number(style, "FontSize") ?? 12;
        bool autoLeading = Bool(style, "AutoLeading") ?? true;
        double leading = autoLeading
            ? Number(paragraph, "AutoLeading") ?? 1.2
            : (Number(style, "Leading") ?? fontSize * 1.2) / Math.Max(0.01, fontSize);

        // Settings with no counterpart here: they are dropped, and the layer says so.
        simplified |= (Number(style, "HorizontalScale") ?? 1) != 1 || (Number(style, "VerticalScale") ?? 1) != 1
                      || (Number(style, "BaselineShift") ?? 0) != 0 || Bool(style, "Underline") == true
                      || Bool(style, "Strikethrough") == true || (Number(style, "FontCaps") ?? 0) == 1
                      || (Number(style, "Kerning") ?? 0) != 0 || Bool(style, "AutoKerning") == false;

        // Photoshop ends every text with a paragraph mark and separates lines with carriage returns.
        string body = words.EndsWith('\r') ? words[..^1] : words;
        body = body.Replace('\r', '\n').Replace('\u0003', '\n');
        if ((Number(style, "FontCaps") ?? 0) == 2) body = body.ToUpperInvariant();

        var layerText = new LayerText
        {
            Text = body,
            Font = font,
            Size = Math.Clamp(fontSize * scale, 1, 5000),
            Weight = Bool(style, "FauxBold") == true ? 700 : 400,
            Italic = Bool(style, "FauxItalic") == true,
            Align = (Number(paragraph, "Justification") ?? 0) switch
            {
                1 or 4 => TextAlign.Right,
                2 or 5 => TextAlign.Center,
                _ => TextAlign.Left,
            },
            Tracking = Math.Clamp(Number(style, "Tracking") ?? 0, -1000, 10_000),
            Leading = Math.Clamp(leading, 0.1, 20),
            Warp = warp is { IsIdentity: false } ? warp : null,
        };

        if (Colour(style) is (double r, double g, double b)) layerText = layerText with { Red = r, Green = g, Blue = b };

        // Every letter in its own run's face, size and fill; the runs are then only what differs.
        if (segments.Count > 1)
        {
            var letters = new LayerText[body.Length];
            var made = new Dictionary<Dictionary<string, object?>, LayerText>(ReferenceEqualityComparer.Instance);
            int at = 0;
            foreach ((Dictionary<string, object?> runStyle, int count) in segments)
            {
                if (!made.TryGetValue(runStyle, out LayerText? letter))
                {
                    letter = layerText with
                    {
                        Font = FontName(resources, runStyle) ?? font,
                        Size = Math.Clamp((Number(runStyle, "FontSize") ?? fontSize) * scale, 1, 5000),
                        Weight = Bool(runStyle, "FauxBold") == true ? 700 : 400,
                        Italic = Bool(runStyle, "FauxItalic") == true,
                    };
                    if (Colour(runStyle) is (double rr, double gg, double bb)) letter = letter with { Red = rr, Green = gg, Blue = bb };
                    made[runStyle] = letter;
                }
                for (int i = 0; i < count && at < letters.Length; i++) letters[at++] = letter;
            }
            while (at < letters.Length) letters[at++] = layerText;
            layerText = TextRuns.FromCharacters(layerText, letters);
        }

        return new PsdTypeLayer(layerText, font, simplified, tx, ty);
    }

    /// <summary>The warp, and whether it bends along the vertical, which this editor's warp does not.</summary>
    private static (TextWarp? Warp, bool Vertical) Warp(PsdDescriptor warping)
    {
        TextWarpStyle style = warping.Enum("warpStyle") switch
        {
            "warpArc" => TextWarpStyle.Arc,
            "warpArcLower" => TextWarpStyle.ArcLower,
            "warpArcUpper" => TextWarpStyle.ArcUpper,
            "warpArch" => TextWarpStyle.Arch,
            "warpBulge" => TextWarpStyle.Bulge,
            "warpShellLower" => TextWarpStyle.ShellLower,
            "warpShellUpper" => TextWarpStyle.ShellUpper,
            "warpFlag" => TextWarpStyle.Flag,
            "warpWave" => TextWarpStyle.Wave,
            "warpFish" => TextWarpStyle.Fish,
            "warpRise" => TextWarpStyle.Rise,
            "warpFisheye" => TextWarpStyle.Fisheye,
            "warpInflate" => TextWarpStyle.Inflate,
            "warpSqueeze" => TextWarpStyle.Squeeze,
            "warpTwist" => TextWarpStyle.Twist,
            _ => TextWarpStyle.None,
        };
        if (style == TextWarpStyle.None) return (null, false);

        static double Amount(double? value) => Math.Clamp(value ?? 0, -100, 100);
        var warp = new TextWarp
        {
            Style = style,
            Bend = Amount(warping.Number("warpValue")),
            Horizontal = Amount(warping.Number("warpPerspective")),
            Vertical = Amount(warping.Number("warpPerspectiveOther")),
        };
        return (warp, warping.Enum("warpRotate") == "Vrtc");
    }

    /// <summary>
    /// The character style over most of the text — the normal style with the run's changes laid
    /// over it — the run styles in order with the letters each covers, and whether the runs differ
    /// in a setting this editor keeps once per layer.
    /// </summary>
    private static (Dictionary<string, object?> Style, bool Mixed, List<(Dictionary<string, object?> Style, int Count)> Segments) Styles(
        Dictionary<string, object?> engine, Dictionary<string, object?>? resources, int length)
    {
        Dictionary<string, object?> normal = NormalSheet(resources, "StyleSheetSet", "TheNormalStyleSheet", "StyleSheetData");
        List<object?> runs = Path(engine, "StyleRun", "RunArray") as List<object?> ?? [];
        List<object?> lengths = Path(engine, "StyleRun", "RunLengthArray") as List<object?> ?? [];

        var styles = new List<(Dictionary<string, object?> Style, string Key, string Block, int Count)>();
        int covered = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            Dictionary<string, object?> style = Merged(normal,
                runs[i] is Dictionary<string, object?> run ? Path(run, "StyleSheet", "StyleSheetData") as Dictionary<string, object?> : null);
            int count = i < lengths.Count && lengths[i] is double n ? (int)n : 0;
            // The closing paragraph mark carries a style too; it shows nothing, so it does not vote.
            if (covered + count >= length) count = Math.Max(0, length - 1 - covered);
            covered += i < lengths.Count && lengths[i] is double all ? (int)all : 0;
            styles.Add((style, Signature(style), BlockSignature(style), count));
        }

        if (styles.Count == 0) return (normal, false, []);
        var main = styles.GroupBy(entry => entry.Key).OrderByDescending(group => group.Sum(entry => entry.Count)).First();
        bool mixed = styles.Where(entry => entry.Count > 0).Select(entry => entry.Block).Distinct().Count() > 1;
        return (main.First().Style, mixed, [.. styles.Where(entry => entry.Count > 0).Select(entry => (entry.Style, entry.Count))]);
    }

    /// <summary>What makes two runs look different, as far as this editor's text can show.</summary>
    private static string Signature(Dictionary<string, object?> style) => string.Join("|",
        new[] { "Font", "FontSize", "Leading", "AutoLeading", "Tracking", "FauxBold", "FauxItalic", "FontCaps" }
            .Select(key => Format(style.GetValueOrDefault(key)))
            .Append(Colour(style)?.ToString() ?? ""));

    /// <summary>
    /// The settings this editor keeps for the whole layer. Runs may differ in face, size and fill;
    /// a difference here is what gets merged. Explicit leading is compared per point of size, since a
    /// larger word carries a larger leading in the same paragraph.
    /// </summary>
    private static string BlockSignature(Dictionary<string, object?> style)
    {
        bool auto = Bool(style, "AutoLeading") ?? true;
        double size = Number(style, "FontSize") ?? 12;
        string leading = auto ? "auto" : Math.Round((Number(style, "Leading") ?? size * 1.2) / Math.Max(0.01, size), 3)
                                             .ToString(CultureInfo.InvariantCulture);
        return string.Join("|", leading, Format(style.GetValueOrDefault("Tracking")), Format(style.GetValueOrDefault("FontCaps")));
    }

    private static string Format(object? value) => value switch
    {
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        null => "",
        _ => value.ToString() ?? "",
    };

    private static Dictionary<string, object?> MainParagraph(Dictionary<string, object?> engine, Dictionary<string, object?>? resources)
    {
        Dictionary<string, object?> normal = NormalSheet(resources, "ParagraphSheetSet", "TheNormalParagraphSheet", "Properties");
        var first = (Path(engine, "ParagraphRun", "RunArray") as List<object?>)?.FirstOrDefault() as Dictionary<string, object?>;
        return Merged(normal, first is null ? null : Path(first, "ParagraphSheet", "Properties") as Dictionary<string, object?>);
    }

    /// <summary>The document's normal character or paragraph style, which a run's own data only changes.</summary>
    private static Dictionary<string, object?> NormalSheet(Dictionary<string, object?>? resources, string set, string which, string data)
    {
        if (resources is null || resources.GetValueOrDefault(set) is not List<object?> sheets || sheets.Count == 0) return [];
        int index = Number(resources, which) is double n ? (int)n : 0;
        if (index < 0 || index >= sheets.Count) index = 0;
        return sheets[index] is Dictionary<string, object?> sheet && sheet.GetValueOrDefault(data) is Dictionary<string, object?> found
            ? found
            : [];
    }

    private static Dictionary<string, object?> Merged(Dictionary<string, object?> under, Dictionary<string, object?>? over)
    {
        var merged = new Dictionary<string, object?>(under);
        if (over is not null)
            foreach ((string key, object? value) in over) merged[key] = value;
        return merged;
    }

    private static string? FontName(Dictionary<string, object?>? resources, Dictionary<string, object?> style)
    {
        if (resources?.GetValueOrDefault("FontSet") is not List<object?> fonts || Number(style, "Font") is not double index) return null;
        int i = (int)index;
        return i >= 0 && i < fonts.Count && fonts[i] is Dictionary<string, object?> font ? font.GetValueOrDefault("Name") as string : null;
    }

    /// <summary>The fill as RGB, 0–1, when it is an RGB colour.</summary>
    private static (double R, double G, double B)? Colour(Dictionary<string, object?> style)
    {
        if (style.GetValueOrDefault("FillColor") is not Dictionary<string, object?> fill
            || fill.GetValueOrDefault("Values") is not List<object?> { Count: 4 } values
            || values[1] is not double r || values[2] is not double g || values[3] is not double b) return null;
        return (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
    }

    private static Dictionary<string, object?>? Dict(Dictionary<string, object?> from, string key) =>
        from.GetValueOrDefault(key) as Dictionary<string, object?>;

    private static object? Path(Dictionary<string, object?> from, params string[] keys)
    {
        object? at = from;
        foreach (string key in keys)
        {
            if (at is not Dictionary<string, object?> dictionary) return null;
            at = dictionary.GetValueOrDefault(key);
        }
        return at;
    }

    private static double? Number(Dictionary<string, object?> from, string key) => from.GetValueOrDefault(key) as double?;

    private static bool? Bool(Dictionary<string, object?> from, string key) => from.GetValueOrDefault(key) switch
    {
        bool value => value,
        double number => number != 0,
        _ => null,
    };
}

/// <summary>
/// Photoshop's text engine data: a PostScript-like dictionary of <c>&lt;&lt; /Key value &gt;&gt;</c>
/// with <c>[ ]</c> arrays, numbers, names, <c>true</c>/<c>false</c> and <c>( )</c> strings that
/// hold UTF-16 behind a byte-order mark.
/// </summary>
/// <remarks>
/// Parsed to plain values: a dictionary is <c>Dictionary&lt;string, object?&gt;</c>, an array
/// <c>List&lt;object?&gt;</c>, a number <c>double</c>, a string <c>string</c>, a name its text
/// with the slash, true and false <c>bool</c>. Anything malformed ends the parse with null rather
/// than an exception, and the layer stays pixels.
/// </remarks>
internal static class EngineData
{
    /// <summary>
    /// The text-engine dictionary Photoshop needs to keep a point-text layer editable: one paragraph
    /// run, since this editor sets a text as one block, and a character run for each stretch of
    /// letters styled alike (<paramref name="runs"/>, covering <paramref name="words"/> exactly).
    /// </summary>
    public static byte[] Write(LayerText text, string words, IReadOnlyList<(LayerText Style, int Length)> runs,
                               Func<LayerText, string> postScriptName)
    {
        var output = new EngineDataWriter();
        string length = words.Length.ToString(CultureInfo.InvariantCulture);
        string autoLeading = Real(text.Leading);

        // The layer's face first: the normal style sheet points at font 0.
        var fonts = new List<string> { postScriptName(text with { Runs = null }) };
        int FontIndex(LayerText style)
        {
            string name = postScriptName(style);
            int index = fonts.IndexOf(name);
            if (index >= 0) return index;
            fonts.Add(name);
            return fonts.Count - 1;
        }
        string justification = text.Align switch
        {
            TextAlign.Right => "1",
            TextAlign.Center => "2",
            _ => "0",
        };

        output.Ascii("<< /EngineDict << /Editor << /Text ");
        output.Utf16(words);
        output.Ascii(" >> /ParagraphRun << /DefaultRunData << /ParagraphSheet << /DefaultStyleSheet 0 /Properties << >> >> ");
        WriteAdjustments(output);
        output.Ascii(" >> /RunArray [ << /ParagraphSheet << /DefaultStyleSheet 0 /Properties << ");
        WriteParagraphProperties(output, justification, autoLeading, autoHyphenate: false, leadingType: 1);
        output.Ascii(">> >> ");
        WriteAdjustments(output);
        output.Ascii(" >> ] /RunLengthArray [ ");
        output.Ascii(length);
        output.Ascii(" ] /IsJoinable 1 >> /StyleRun << /DefaultRunData << /StyleSheet << /StyleSheetData << >> >> >> ");
        output.Ascii("/RunArray [ ");
        foreach ((LayerText style, _) in runs) WriteCharacterRun(output, text, style, FontIndex(style));
        output.Ascii("] /RunLengthArray [ ");
        output.Ascii(string.Join(" ", runs.Select(run => run.Length.ToString(CultureInfo.InvariantCulture))));
        output.Ascii(" ] /IsJoinable 2 >> /GridInfo << /GridIsOn false /ShowGrid false /GridSize 18.0 /GridLeading 22.0 ");
        output.Ascii("/GridColor << /Type 1 /Values [ 0.0 0.0 0.0 1.0 ] >> /GridLeadingFillColor << /Type 1 /Values [ 0.0 0.0 0.0 1.0 ] >> ");
        output.Ascii("/AlignLineHeightToGridFlags false >> /AntiAlias 1 ");
        output.Ascii("/UseFractionalGlyphWidths true /Rendered << /Version 1 /Shapes << /WritingDirection 0 /Children [ ");
        output.Ascii("<< /ShapeType 0 /Procession 0 /Lines << /WritingDirection 0 /Children [ ] >> /Cookie << /Photoshop << /ShapeType 0 ");
        output.Ascii("/PointBase [ 0.0 0.0 ] /Base << /ShapeType 0 /TransformPoint0 [ 1.0 0.0 ] /TransformPoint1 [ 0.0 1.0 ] ");
        output.Ascii("/TransformPoint2 [ 0.0 0.0 ] >> >> >> >> ] >> >> >> ");

        output.Ascii("/ResourceDict ");
        WriteResources(output, fonts);
        output.Ascii(" /DocumentResources ");
        WriteResources(output, fonts);
        output.Ascii(" >>");
        return output.ToArray();
    }

    /// <summary>A real number the way Photoshop's text engine writes one: <c>0.0</c>, <c>1.0</c>, <c>.8</c>, <c>57.6</c>.</summary>
    /// <remarks>
    /// Photoshop refuses the whole type layer, rasterizing it with a generic "could not read" warning,
    /// when a number carries a round-trip mantissa such as <c>57.599999999999994</c> (48 × 1.2).
    /// Bisecting a Photoshop-written EngineData one value at a time showed that this alone flips the
    /// layer from editable to rasterized, while <c>57.6</c> in the same place is accepted. So reals are
    /// cut to five decimals and always keep a decimal point, and a leading zero is dropped as
    /// Photoshop does, since its reader tells integer and real tokens apart.
    /// </remarks>
    internal static string Real(double value)
    {
        double rounded = Math.Round(value, 5);
        if (rounded == 0) rounded = 0; // no "-0.0"
        string text = rounded.ToString("0.0####", CultureInfo.InvariantCulture);
        if (text.StartsWith("0.", StringComparison.Ordinal) && text != "0.0") return text[1..];
        if (text.StartsWith("-0.", StringComparison.Ordinal)) return "-" + text[2..];
        return text;
    }

    /// <summary>
    /// One character run. Leading is written per letter from its own size, which is how a line
    /// holding a larger word gets Photoshop's larger line step, as it does in this editor's layout.
    /// </summary>
    private static void WriteCharacterRun(EngineDataWriter output, LayerText layer, LayerText style, int font)
    {
        // Photoshop writes tracking as a whole number of thousandths of an em.
        string tracking = ((int)Math.Round(layer.Tracking)).ToString(CultureInfo.InvariantCulture);
        output.Ascii("<< /StyleSheet << /StyleSheetData << ");
        output.Ascii($"/Font {font.ToString(CultureInfo.InvariantCulture)} /FontSize {Real(style.Size)} ");
        output.Ascii($"/FauxBold {(style.Weight >= 600 ? "true" : "false")} /FauxItalic {(style.Italic ? "true" : "false")} ");
        output.Ascii($"/AutoLeading false /Leading {Real(style.Size * layer.Leading)} ");
        output.Ascii($"/HorizontalScale 1.0 /VerticalScale 1.0 /Tracking {tracking} /AutoKerning true /Kerning 0 ");
        output.Ascii("/BaselineShift 0.0 /FontCaps 0 /FontBaseline 0 /Underline false /Strikethrough false ");
        output.Ascii("/Ligatures true /DLigatures false /BaselineDirection 2 /Tsume 0.0 /StyleRunAlignment 2 /Language 0 /NoBreak false ");
        output.Ascii($"/FillColor << /Type 1 /Values [ 1.0 {Real(style.Red)} {Real(style.Green)} {Real(style.Blue)} ] >> ");
        output.Ascii("/StrokeColor << /Type 1 /Values [ 1.0 0.0 0.0 0.0 ] >> /YUnderline 1 /HindiNumbers false /Kashida 1 >> >> >> ");
    }

    private static void WriteAdjustments(EngineDataWriter output) =>
        output.Ascii("/Adjustments << /Axis [ 1.0 0.0 1.0 ] /XY [ 0.0 0.0 ] >>");

    private static void WriteParagraphProperties(
        EngineDataWriter output, string justification, string autoLeading, bool autoHyphenate, int leadingType)
    {
        output.Ascii($"/Justification {justification} /FirstLineIndent 0.0 /StartIndent 0.0 /EndIndent 0.0 /SpaceBefore 0.0 /SpaceAfter 0.0 ");
        output.Ascii($"/AutoHyphenate {(autoHyphenate ? "true" : "false")} /HyphenatedWordSize 6 /PreHyphen 2 /PostHyphen 2 ");
        output.Ascii("/ConsecutiveHyphens 8 /Zone 36.0 /WordSpacing [ .8 1.0 1.33 ] /LetterSpacing [ 0.0 0.0 0.0 ] /GlyphSpacing [ 1.0 1.0 1.0 ] ");
        output.Ascii($"/AutoLeading {autoLeading} /LeadingType {leadingType} /Hanging false /Burasagari false /KinsokuOrder 0 /EveryLineComposer false ");
    }

    /// <summary>
    /// The complete resource shape emitted by Photoshop 2025. ResourceDict describes the text
    /// object and DocumentResources repeats it for the document text engine; omitting the latter
    /// leaves a layer that lenient PSD libraries can parse but Photoshop may refuse to initialise.
    /// </summary>
    private static void WriteResources(EngineDataWriter output, IReadOnlyList<string> fonts)
    {
        output.Ascii("<< /KinsokuSet [ ] /MojiKumiSet [ ] /TheNormalStyleSheet 0 /TheNormalParagraphSheet 0 ");
        output.Ascii("/ParagraphSheetSet [ << /Name ");
        output.Utf16("Normal RGB");
        output.Ascii(" /DefaultStyleSheet 0 /Properties << ");
        WriteParagraphProperties(output, "0", "1.2", autoHyphenate: true, leadingType: 0);
        output.Ascii(">> >> ] /StyleSheetSet [ << /Name ");
        output.Utf16("Normal RGB");
        output.Ascii(" /StyleSheetData << /Font 0 /FontSize 12.0 /FauxBold false /FauxItalic false /AutoLeading true /Leading 0.0 ");
        output.Ascii("/HorizontalScale 1.0 /VerticalScale 1.0 /Tracking 0 /AutoKerning true /Kerning 0 /BaselineShift 0.0 ");
        output.Ascii("/FontCaps 0 /FontBaseline 0 /Underline false /Strikethrough false /Ligatures true /DLigatures false ");
        output.Ascii("/BaselineDirection 2 /Tsume 0.0 /StyleRunAlignment 2 /Language 0 /NoBreak false ");
        output.Ascii("/FillColor << /Type 1 /Values [ 1.0 0.0 0.0 0.0 ] >> /StrokeColor << /Type 1 /Values [ 1.0 0.0 0.0 0.0 ] >> ");
        output.Ascii("/FillFlag true /StrokeFlag false /FillFirst true /YUnderline 1 /OutlineWidth 1.0 /CharacterDirection 0 ");
        output.Ascii("/HindiNumbers false /Kashida 1 /DiacriticPos 2 >> >> ] /FontSet [ ");
        foreach (string font in fonts)
        {
            output.Ascii("<< /Name ");
            output.Utf16(font);
            output.Ascii(" /Script 0 /FontType 1 /Synthetic 0 >> ");
        }
        // Photoshop keeps its invisible-character font last.
        output.Ascii("<< /Name ");
        output.Utf16("AdobeInvisFont");
        output.Ascii(" /Script 0 /FontType 0 /Synthetic 0 >> ] /SuperscriptSize .583 /SuperscriptPosition .333 ");
        output.Ascii("/SubscriptSize .583 /SubscriptPosition .333 /SmallCapSize .7 >>");
    }

    public static object? Parse(ReadOnlySpan<byte> data)
    {
        int at = 0;
        try
        {
            return Value(data, ref at, depth: 0);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static void Space(ReadOnlySpan<byte> data, ref int at)
    {
        while (at < data.Length && data[at] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0) at++;
    }

    private static object? Value(ReadOnlySpan<byte> data, ref int at, int depth)
    {
        if (depth > 64) throw new FormatException("nested too deep");
        Space(data, ref at);
        if (at >= data.Length) throw new FormatException("ended early");

        byte first = data[at];
        if (first == '<' && at + 1 < data.Length && data[at + 1] == '<')
        {
            at += 2;
            var dictionary = new Dictionary<string, object?>();
            while (true)
            {
                Space(data, ref at);
                if (at + 1 < data.Length && data[at] == '>' && data[at + 1] == '>')
                {
                    at += 2;
                    return dictionary;
                }
                if (data[at] != '/') throw new FormatException("a key without a slash");
                string key = Name(data, ref at);
                dictionary[key] = Value(data, ref at, depth + 1);
            }
        }

        if (first == '[')
        {
            at++;
            var list = new List<object?>();
            while (true)
            {
                Space(data, ref at);
                if (data[at] == ']')
                {
                    at++;
                    return list;
                }
                list.Add(Value(data, ref at, depth + 1));
            }
        }

        if (first == '(') return String(data, ref at);
        if (first == '/') return "/" + Name(data, ref at);

        int start = at;
        while (at < data.Length && data[at] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)']'
                   or (byte)'>' or (byte)'/' or (byte)'[' or (byte)'<' or (byte)'(')) at++;
        string token = Encoding.ASCII.GetString(data[start..at]);
        if (token == "true") return true;
        if (token == "false") return false;
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return number;
        throw new FormatException($"unexpected '{token}'");
    }

    /// <summary>A name after its slash, up to the next space or delimiter.</summary>
    private static string Name(ReadOnlySpan<byte> data, ref int at)
    {
        at++;
        int start = at;
        while (at < data.Length && data[at] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'/'
                   or (byte)'[' or (byte)']' or (byte)'<' or (byte)'>' or (byte)'(')) at++;
        return Encoding.ASCII.GetString(data[start..at]);
    }

    /// <summary>A parenthesised string: backslash takes the next byte as it is; FE FF starts UTF-16.</summary>
    private static string String(ReadOnlySpan<byte> data, ref int at)
    {
        at++;
        var bytes = new List<byte>();
        while (true)
        {
            byte b = data[at++];
            if (b == '\\')
            {
                bytes.Add(data[at++]);
                continue;
            }
            if (b == ')') break;
            bytes.Add(b);
        }

        byte[] raw = [.. bytes];
        return raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF
            ? Encoding.BigEndianUnicode.GetString(raw, 2, (raw.Length - 2) & ~1)
            : Encoding.Latin1.GetString(raw);
    }

    private sealed class EngineDataWriter
    {
        private readonly List<byte> _bytes = [];

        public void Ascii(string text) => _bytes.AddRange(Encoding.ASCII.GetBytes(text));

        public void Utf16(string text)
        {
            _bytes.Add((byte)'(');
            Escaped(0xFE);
            Escaped(0xFF);
            foreach (byte value in Encoding.BigEndianUnicode.GetBytes(text)) Escaped(value);
            _bytes.Add((byte)')');
        }

        public byte[] ToArray() => [.. _bytes];

        private void Escaped(byte value)
        {
            if (value is (byte)'(' or (byte)')' or (byte)'\\') _bytes.Add((byte)'\\');
            _bytes.Add(value);
        }
    }
}

/// <summary>
/// Photoshop names a font by its PostScript name; Windows lists families. This finds the family,
/// weight and slant a PostScript name stands for.
/// </summary>
public static class PsdFonts
{
    /// <summary>
    /// The face for <paramref name="postScript"/>, looked for among <paramref name="families"/> (English
    /// names). When it is not installed, the family is guessed from the name — "WixMadeforDisplay-Bold"
    /// is "Wix Madefor Display", bold — so the text still asks for the right font once it is.
    /// </summary>
    public static TextFace Resolve(string postScript, IReadOnlyCollection<string>? families, out bool installed)
    {
        string name = postScript.Trim();
        int dash = name.IndexOf('-', StringComparison.Ordinal);
        string stem = dash > 0 ? name[..dash] : name;
        string style = dash > 0 ? name[(dash + 1)..] : "";

        string key = Key(stem);
        string? family = null;
        if (families is not null)
        {
            family = families.Where(candidate => Key(candidate).Length > 0 && key.StartsWith(Key(candidate), StringComparison.Ordinal))
                             .OrderByDescending(candidate => Key(candidate).Length)
                             .FirstOrDefault();
        }

        installed = family is not null;
        if (family is not null)
        {
            // "MalgunGothicBold": the style was run into the name.
            if (style.Length == 0) style = key[Key(family).Length..];
        }
        else
        {
            family = Spaced(StripVendor(stem));
        }

        string lower = style.ToLowerInvariant();
        bool italic = lower.Contains("italic", StringComparison.Ordinal) || lower.Contains("oblique", StringComparison.Ordinal)
                      || lower.EndsWith("it", StringComparison.Ordinal);
        int weight = lower switch
        {
            _ when lower.Contains("extrabold", StringComparison.Ordinal) || lower.Contains("ultrabold", StringComparison.Ordinal) => 800,
            _ when lower.Contains("semibold", StringComparison.Ordinal) || lower.Contains("demibold", StringComparison.Ordinal) => 600,
            _ when lower.Contains("extralight", StringComparison.Ordinal) || lower.Contains("ultralight", StringComparison.Ordinal) => 200,
            _ when lower.Contains("black", StringComparison.Ordinal) || lower.Contains("heavy", StringComparison.Ordinal) => 900,
            _ when lower.Contains("bold", StringComparison.Ordinal) => 700,
            _ when lower.Contains("medium", StringComparison.Ordinal) => 500,
            _ when lower.Contains("light", StringComparison.Ordinal) => 300,
            _ when lower.Contains("thin", StringComparison.Ordinal) || lower.Contains("hairline", StringComparison.Ordinal) => 100,
            _ => 400,
        };
        return new TextFace(family, weight, italic);
    }

    /// <summary>Letters and digits only, lower case — how a family and a PostScript stem compare.</summary>
    private static string Key(string text) =>
        new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The foundry marks some PostScript names carry, as "ArialMT" does.</summary>
    private static string StripVendor(string stem) =>
        stem.EndsWith("MT", StringComparison.Ordinal) || stem.EndsWith("PS", StringComparison.Ordinal) ? stem[..^2] : stem;

    /// <summary>"WixMadeforDisplay" as "Wix Madefor Display": a space before each capital that follows a small letter.</summary>
    private static string Spaced(string stem)
    {
        var spaced = new StringBuilder(stem.Length + 4);
        for (int i = 0; i < stem.Length; i++)
        {
            if (i > 0 && char.IsUpper(stem[i]) && char.IsLower(stem[i - 1])) spaced.Append(' ');
            spaced.Append(stem[i]);
        }
        return spaced.ToString();
    }
}
