using System.Globalization;
using System.Text;

namespace Compositor_korean_win.Core;

/// <summary>A Photoshop type layer read as this editor's text, and what could not be carried exactly.</summary>
/// <param name="Text">The words and their look; the anchor and raster are set by the importer.</param>
/// <param name="PostScriptName">The font Photoshop names, as its PostScript name.</param>
/// <param name="Simplified">Styles that differed within the layer were merged, or a setting had no equivalent.</param>
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
/// This editor's text is one style per layer (<see cref="LayerText"/>), so a layer whose runs
/// differ takes the style that covers the most characters and says it was simplified. Point type
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
            string body = text.Text.Replace("\r\n", "\r", StringComparison.Ordinal)
                                   .Replace('\n', '\r');
            string words = body + "\r";
            new PsdDescriptor { ClassId = "TxLr" }
                // The descriptor is null-terminated by its TEXT writer. EngineData alone carries
                // the extra carriage return that closes Photoshop's final paragraph.
                .Add("Txt ", body)
                .Add("textGridding", new PsdEnum("textGridding", "None"))
                .Add("Ornt", new PsdEnum("Ornt", "Hrzn"))
                .Add("AntA", new PsdEnum("Annt", "AnSm"))
                .Add("TextIndex", 0)
                .Add("EngineData", EngineData.Write(text, words, PostScriptName(text)))
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

            // Adobe's TySh specification calls these four bounds 8-byte doubles. psd-tools reads
            // them as 4-byte integers and therefore did not catch the old, truncated 16-byte tail;
            // Photoshop did, rasterising the layer and then disabling its text engine.
            writer.F64(0);
            writer.F64(0);
            writer.F64(0);
            writer.F64(0);
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

        // Four doubles follow the warp descriptor. Reading them is deliberately strict: a short
        // TySh block may look acceptable to tolerant third-party readers but Photoshop rejects it.
        if (block.Remaining < 4 * sizeof(double)) return null;
        double left = block.F64(), top = block.F64(), right = block.F64(), bottom = block.F64();
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

        (Dictionary<string, object?> style, bool mixed) = MainStyle(engine, resources, words.Length);
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
    /// over it — and whether other runs looked different.
    /// </summary>
    private static (Dictionary<string, object?> Style, bool Mixed) MainStyle(
        Dictionary<string, object?> engine, Dictionary<string, object?>? resources, int length)
    {
        Dictionary<string, object?> normal = NormalSheet(resources, "StyleSheetSet", "TheNormalStyleSheet", "StyleSheetData");
        List<object?> runs = Path(engine, "StyleRun", "RunArray") as List<object?> ?? [];
        List<object?> lengths = Path(engine, "StyleRun", "RunLengthArray") as List<object?> ?? [];

        var styles = new List<(Dictionary<string, object?> Style, string Key, int Count)>();
        int covered = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            Dictionary<string, object?> style = Merged(normal,
                runs[i] is Dictionary<string, object?> run ? Path(run, "StyleSheet", "StyleSheetData") as Dictionary<string, object?> : null);
            int count = i < lengths.Count && lengths[i] is double n ? (int)n : 0;
            // The closing paragraph mark carries a style too; it shows nothing, so it does not vote.
            if (covered + count >= length) count = Math.Max(0, length - 1 - covered);
            covered += i < lengths.Count && lengths[i] is double all ? (int)all : 0;
            styles.Add((style, Signature(style), count));
        }

        if (styles.Count == 0) return (normal, false);
        var main = styles.GroupBy(entry => entry.Key).OrderByDescending(group => group.Sum(entry => entry.Count)).First();
        bool mixed = styles.Where(entry => entry.Count > 0).Select(entry => entry.Key).Distinct().Count() > 1;
        return (main.First().Style, mixed);
    }

    /// <summary>What makes two runs look different, as far as this editor's text can show.</summary>
    private static string Signature(Dictionary<string, object?> style) => string.Join("|",
        new[] { "Font", "FontSize", "Leading", "AutoLeading", "Tracking", "FauxBold", "FauxItalic", "FontCaps" }
            .Select(key => Format(style.GetValueOrDefault(key)))
            .Append(Colour(style)?.ToString() ?? ""));

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
    /// The compact text-engine dictionary Photoshop needs to keep a point-text layer editable.
    /// It deliberately contains one paragraph run and one character run because <see cref="LayerText"/>
    /// has one style for the whole layer.
    /// </summary>
    public static byte[] Write(LayerText text, string words, string postScriptName)
    {
        var output = new EngineDataWriter();
        string length = words.Length.ToString(CultureInfo.InvariantCulture);
        string size = Number(text.Size);
        string tracking = Number(text.Tracking);
        string leading = Number(text.Size * text.Leading);
        string autoLeading = Number(text.Leading);
        string justification = text.Align switch
        {
            TextAlign.Right => "1",
            TextAlign.Center => "2",
            _ => "0",
        };
        string red = Number(text.Red), green = Number(text.Green), blue = Number(text.Blue);

        output.Ascii("<< /EngineDict << /Editor << /Text ");
        output.Utf16(words);
        output.Ascii(" >> /ParagraphRun << /RunArray [ << /ParagraphSheet << /DefaultStyleSheet 0 /Properties << ");
        output.Ascii($"/Justification {justification} /AutoLeading {autoLeading} /HyphenatedWordSize 6 /PreHyphen 2 /PostHyphen 2 ");
        output.Ascii("/ConsecutiveHyphens 8 /Zone 36 /WordSpacing [ 0.8 1.0 1.33 ] /LetterSpacing [ 0 0 0 ] /GlyphSpacing [ 1 1 1 ] ");
        output.Ascii(">> >> /Adjustments << >> >> ] /RunLengthArray [ ");
        output.Ascii(length);
        output.Ascii(" ] /IsJoinable 1 >> /StyleRun << /RunArray [ << /StyleSheet << /StyleSheetData << ");
        output.Ascii($"/Font 0 /FontSize {size} /FauxBold {(text.Weight >= 600 ? "true" : "false")} ");
        output.Ascii($"/FauxItalic {(text.Italic ? "true" : "false")} /AutoLeading false /Leading {leading} ");
        output.Ascii($"/HorizontalScale 1 /VerticalScale 1 /Tracking {tracking} /AutoKerning true /Kerning 0 ");
        output.Ascii("/BaselineShift 0 /FontCaps 0 /Underline false /Strikethrough false ");
        output.Ascii($"/FillColor << /Type 1 /Values [ 1 {red} {green} {blue} ] >> ");
        output.Ascii("/StrokeColor << /Type 1 /Values [ 1 0 0 0 ] >> /FillFlag true /StrokeFlag false /FillFirst true ");
        output.Ascii("/YUnderline 1 /OutlineWidth 1 >> >> >> ] /RunLengthArray [ ");
        output.Ascii(length);
        output.Ascii(" ] /IsJoinable 2 >> /GridInfo << /GridIsOn false /ShowGrid false >> /AntiAlias 3 ");
        output.Ascii("/UseFractionalGlyphWidths true /Rendered << /Version 1 /Shapes << /WritingDirection 0 /Children [ ");
        output.Ascii("<< /ShapeType 0 /Procession 0 /Lines << /WritingDirection 0 >> /Cookie << /Photoshop << /ShapeType 0 ");
        output.Ascii("/PointBase [ 0 0 ] /Base << /ShapeType 0 /TransformPoint0 [ 1 0 ] /TransformPoint1 [ 0 1 ] ");
        output.Ascii("/TransformPoint2 [ 0 0 ] >> >> >> >> ] >> >> >> ");

        output.Ascii("/ResourceDict << /TheNormalStyleSheet 0 /TheNormalParagraphSheet 0 /FontSet [ << /Name ");
        output.Utf16(postScriptName);
        output.Ascii(" /Script 0 /FontType 1 /Synthetic 0 >> ] ");
        output.Ascii("/StyleSheetSet [ << /Name ");
        output.Utf16("Normal RGB");
        output.Ascii(" /StyleSheetData << >> >> ] /ParagraphSheetSet [ << /Name ");
        output.Utf16("Normal RGB");
        output.Ascii(" /DefaultStyleSheet 0 /Properties << /Justification 0 /AutoLeading 1.2 >> >> ] >> >>");
        return output.ToArray();

        static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
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
