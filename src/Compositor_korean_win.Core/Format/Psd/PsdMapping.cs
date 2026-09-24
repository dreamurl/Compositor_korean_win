namespace Compositor_korean_win.Core;

/// <summary>
/// What an exchange with Photoshop could not carry exactly, so the person can be told rather than
/// find out.
/// </summary>
public enum PsdNote
{
    /// <summary>A blend mode this editor does not have (Soft Light, Linear Dodge…); drawn as Normal.</summary>
    BlendModeReplaced,
    /// <summary>An adjustment layer of a kind this editor does not have (Brightness/Contrast…); left out.</summary>
    AdjustmentDropped,
    /// <summary>A Gradient Map with more than two stops, or stops of their own spacing; kept as its two ends.</summary>
    GradientMapSimplified,
    /// <summary>An enabled effect this editor does not have (Bevel, Inner Shadow, overlays…); left out.</summary>
    EffectDropped,
    /// <summary>A stroke painted with a gradient or pattern, or blended, drawn in one colour.</summary>
    EffectSimplified,
    /// <summary>Type kept as the pixels Photoshop last drew for it; the words are no longer editable.</summary>
    TypeRasterized,
    /// <summary>Type whose runs differed, or used a setting this editor lacks, opened as editable text in one style.</summary>
    TypeSimplified,
    /// <summary>Type in a font that is not installed; it shows as Photoshop drew it until the words are changed.</summary>
    FontMissing,
    /// <summary>A smart object kept as the pixels Photoshop last drew for it.</summary>
    SmartObjectRasterized,
    /// <summary>A vector mask on a layer whose pixels do not already carry it; left out.</summary>
    VectorMaskDropped,
    /// <summary>A mask's density or feather, which is not applied.</summary>
    MaskParametersDropped,
    /// <summary>A group with its own opacity or blend mode, merged into one layer that keeps them.</summary>
    GroupMerged,
    /// <summary>A layer clipped to something that cannot be a clipping base here; shown unclipped.</summary>
    ClippingDropped,
    /// <summary>The file has no layers; opened as its flattened image.</summary>
    FlattenedOnly,

    /// <summary>Export: live type is written as pixels, as Photoshop cannot read this editor's text.</summary>
    TextExportedAsPixels,
    /// <summary>Export: a Grain adjustment, which Photoshop has no layer for; left out.</summary>
    GrainDropped,
    /// <summary>Export: a layer clipped to a base that is not directly below it, written with the clip applied to its pixels.</summary>
    ClippingBaked,
    /// <summary>Export: an option Photoshop's layer has no place for (Hue/Saturation's inverted range…), left out.</summary>
    AdjustmentSimplified,
}

/// <summary>An additional-information block to write: its key, and what goes in it.</summary>
internal sealed record PsdBlockWriter(string Key, Action<PsdWriter> Write);

/// <summary>Blend modes by Photoshop's keys: the four-character ones in layer records and the ones descriptors use.</summary>
internal static class PsdBlend
{
    public static LayerBlendMode? FromKey(string key) => key switch
    {
        "norm" => LayerBlendMode.Normal,
        "mul " => LayerBlendMode.Multiply,
        "scrn" => LayerBlendMode.Screen,
        "over" => LayerBlendMode.Overlay,
        "dark" => LayerBlendMode.Darken,
        "lite" => LayerBlendMode.Lighten,
        "diff" => LayerBlendMode.Difference,
        "div " => LayerBlendMode.ColorDodge,
        "idiv" => LayerBlendMode.ColorBurn,
        "hue " => LayerBlendMode.Hue,
        "sat " => LayerBlendMode.Saturation,
        "colr" => LayerBlendMode.Color,
        "lum " => LayerBlendMode.Luminosity,
        _ => null,
    };

    public static string ToKey(LayerBlendMode mode) => mode switch
    {
        LayerBlendMode.Multiply => "mul ",
        LayerBlendMode.Screen => "scrn",
        LayerBlendMode.Overlay => "over",
        LayerBlendMode.Darken => "dark",
        LayerBlendMode.Lighten => "lite",
        LayerBlendMode.Difference => "diff",
        LayerBlendMode.ColorDodge => "div ",
        LayerBlendMode.ColorBurn => "idiv",
        LayerBlendMode.Hue => "hue ",
        LayerBlendMode.Saturation => "sat ",
        LayerBlendMode.Color => "colr",
        LayerBlendMode.Luminosity => "lum ",
        _ => "norm",
    };

    public static LayerBlendMode? FromDescriptor(string? value) => value switch
    {
        "Nrml" => LayerBlendMode.Normal,
        "Mltp" => LayerBlendMode.Multiply,
        "Scrn" => LayerBlendMode.Screen,
        "Ovrl" => LayerBlendMode.Overlay,
        "Drkn" => LayerBlendMode.Darken,
        "Lghn" => LayerBlendMode.Lighten,
        "Dfrn" => LayerBlendMode.Difference,
        "CDdg" => LayerBlendMode.ColorDodge,
        "CBrn" => LayerBlendMode.ColorBurn,
        "H   " => LayerBlendMode.Hue,
        "Strt" => LayerBlendMode.Saturation,
        "Clr " => LayerBlendMode.Color,
        "Lmns" => LayerBlendMode.Luminosity,
        _ => null,
    };

    public static string ToDescriptor(LayerBlendMode mode) => mode switch
    {
        LayerBlendMode.Multiply => "Mltp",
        LayerBlendMode.Screen => "Scrn",
        LayerBlendMode.Overlay => "Ovrl",
        LayerBlendMode.Darken => "Drkn",
        LayerBlendMode.Lighten => "Lghn",
        LayerBlendMode.Difference => "Dfrn",
        LayerBlendMode.ColorDodge => "CDdg",
        LayerBlendMode.ColorBurn => "CBrn",
        LayerBlendMode.Hue => "H   ",
        LayerBlendMode.Saturation => "Strt",
        LayerBlendMode.Color => "Clr ",
        LayerBlendMode.Luminosity => "Lmns",
        _ => "Nrml",
    };
}

/// <summary>
/// The adjustment layers both editors have, in Photoshop's fixed-layout blocks.
/// </summary>
/// <remarks>
/// Upstream modelled its adjustments on Photoshop's — Levels in 0–255 with gamma as Photoshop's
/// middle slider, Curves in 0–255, Hue/Saturation with the same six ranges and their four
/// handles, Exposure's three values — so these are translations of layout, not of meaning.
/// Grain has no Photoshop layer at all.
/// </remarks>
internal static class PsdAdjustments
{
    /// <summary>Adjustment blocks this port reads.</summary>
    public static readonly string[] Supported = ["levl", "curv", "hue2", "expA", "grdm"];

    /// <summary>Adjustment and fill blocks it does not, so a layer holding one is known to be an adjustment.</summary>
    public static readonly string[] Unsupported =
        ["brit", "blnc", "vibA", "blwh", "phfl", "mixr", "clrL", "nvrt", "post", "thrs", "selc", "hue "];

    public static LayerAdjustment? Read(PsdFile file, PsdLayerRecord layer, Action<PsdNote> note)
    {
        foreach (string key in Supported)
        {
            if (file.Block(layer, key) is not PsdReader block) continue;
            try
            {
                return key switch
                {
                    "levl" => Levels(block),
                    "curv" => Curves(block),
                    "hue2" => HueSaturation(block),
                    "expA" => Exposure(block),
                    _ => GradientMap(block, note),
                };
            }
            catch (ProjectException)
            {
                note(PsdNote.AdjustmentDropped);
                return null;
            }
        }
        return null;
    }

    private static LayerAdjustment Levels(PsdReader block)
    {
        block.U16();
        var ranges = new List<LevelRange>(4);
        for (int i = 0; i < 4; i++)
        {
            int inFloor = block.U16(), inCeiling = block.U16(), outFloor = block.U16(), outCeiling = block.U16(), gamma = block.U16();
            ranges.Add(new LevelRange
            {
                Black = inFloor,
                White = inCeiling,
                OutputBlack = outFloor,
                OutputWhite = outCeiling,
                Gamma = gamma / 100.0,
            }.Normalized);
        }
        return new LayerAdjustment(AdjustmentKind.Levels)
        {
            Levels = new LevelsSettings { Ranges = new EquatableList<LevelRange>(ranges) },
        };
    }

    private static LayerAdjustment Curves(PsdReader block)
    {
        bool isMap = block.U8() != 0;
        int version = block.U16();
        uint map = block.U32();
        if (isMap) throw PsdFormat.Invalid("a Curves layer stored as a lookup table");

        // Version 1 flags which channels follow; version 4 counts them, composite first.
        var channels = new List<int>();
        if (version == 1)
        {
            for (int bit = 0; bit < 32; bit++)
                if ((map & (1u << bit)) != 0) channels.Add(bit);
        }
        else
        {
            for (int i = 0; i < Math.Min(map, 32u); i++) channels.Add(i);
        }

        var curves = new List<EquatableList<CurvePoint>>
        {
            Diagonal(), Diagonal(), Diagonal(), Diagonal(),
        };

        foreach (int channel in channels)
        {
            int count = block.U16();
            var points = new List<CurvePoint>(count);
            for (int i = 0; i < count; i++)
            {
                int output = block.U16(), input = block.U16();
                points.Add(new CurvePoint(Math.Clamp(input, 0, 255), Math.Clamp(output, 0, 255)));
            }
            if (channel < 4) curves[channel] = Tidy(points);
        }

        return new LayerAdjustment(AdjustmentKind.Curves)
        {
            Curves = new CurvesSettings { Channels = new EquatableList<EquatableList<CurvePoint>>(curves) },
        };
    }

    private static EquatableList<CurvePoint> Diagonal() => new([new CurvePoint(0, 0), new CurvePoint(255, 255)]);

    /// <summary>
    /// A Photoshop curve as this editor stores one: strictly increasing, from 0 to 255, at most 32
    /// handles. Photoshop's curve may start past 0 or end short of 255 and stays flat beyond its
    /// ends, which a handle at each end repeating the end's value reproduces.
    /// </summary>
    private static EquatableList<CurvePoint> Tidy(List<CurvePoint> points)
    {
        var sorted = points.OrderBy(point => point.X).ToList();
        var result = new List<CurvePoint>();
        foreach (CurvePoint point in sorted)
            if (result.Count == 0 || point.X > result[^1].X) result.Add(point);
        if (result.Count == 0) return Diagonal();
        if (result[0].X > 0) result.Insert(0, new CurvePoint(0, result[0].Y));
        if (result[^1].X < 255) result.Add(new CurvePoint(255, result[^1].Y));
        if (result.Count > 32) result = [.. result.Take(31), result[^1]];
        return new EquatableList<CurvePoint>(result);
    }

    private static LayerAdjustment HueSaturation(PsdReader block)
    {
        int version = block.U16();
        if (version != 2) throw PsdFormat.Invalid($"Hue/Saturation version {version}");
        bool colorize = block.U8() != 0;
        block.U8();
        (int h, int s, int l) colorization = (block.I16(), block.I16(), block.I16());
        (int h, int s, int l) master = (block.I16(), block.I16(), block.I16());

        var adjustments = new List<KeyValuePair<ColorRange, RangeAdjustment>>();
        var bands = new List<KeyValuePair<ColorRange, HueBand>>
        {
            new(ColorRange.Master, ColorRange.Master.DefaultBand()),
        };

        var top = colorize
            ? new RangeAdjustment(((colorization.h % 360) + 360) % 360, Math.Clamp(colorization.s, 0, 100), Math.Clamp(colorization.l, -100, 100))
            : new RangeAdjustment(Math.Clamp(master.h, -180, 180), Math.Clamp(master.s, -100, 100), Math.Clamp(master.l, -100, 100));
        adjustments.Add(new(ColorRange.Master, top));

        foreach (ColorRange range in ColorRanges.Colors)
        {
            int a = block.I16(), b = block.I16(), c = block.I16(), d = block.I16();
            int hue = block.I16(), saturation = block.I16(), lightness = block.I16();
            bands.Add(new(range, new HueBand(Wrap(a), Wrap(b), Wrap(c), Wrap(d))));
            if (hue != 0 || saturation != 0 || lightness != 0)
                adjustments.Add(new(range, new RangeAdjustment(Math.Clamp(hue, -180, 180), Math.Clamp(saturation, -100, 100), Math.Clamp(lightness, -100, 100))));
        }

        var settings = new HueSaturationSettings
        {
            Colorize = colorize,
            Adjustments = new ColorRangeMap<RangeAdjustment>(adjustments),
            Bands = new ColorRangeMap<HueBand>(bands),
        };
        return new LayerAdjustment(AdjustmentKind.Hsv)
        {
            HsvSettings = settings,
            Hue = top.Hue,
            Saturation = top.Saturation,
            Lightness = top.Lightness,
            Colorize = colorize,
        };

        static double Wrap(int degrees) => ((degrees % 360) + 360) % 360;
    }

    private static LayerAdjustment Exposure(PsdReader block)
    {
        block.U16();
        var settings = new ExposureSettings { Exposure = block.F32(), Offset = block.F32(), Gamma = block.F32() };
        return new LayerAdjustment(AdjustmentKind.Exposure) { ExposureSettings = settings.Normalized };
    }

    private static LayerAdjustment GradientMap(PsdReader block, Action<PsdNote> note)
    {
        int version = block.U16();
        bool reversed = block.U8() != 0;
        block.U8();
        if (version == 3) block.Key();
        block.Unicode();

        int count = block.U16();
        var stops = new List<(uint Location, uint Midpoint, int Mode, AdjustmentColor Colour)>();
        for (int i = 0; i < count; i++)
        {
            uint location = block.U32(), midpoint = block.U32();
            int mode = block.U16();
            double r = block.U16() / 65535.0, g = block.U16() / 65535.0, b = block.U16() / 65535.0;
            block.U16();
            block.U16();
            stops.Add((location, midpoint, mode, new AdjustmentColor(r, g, b)));
        }

        int transparent = block.U16();
        bool opaque = true;
        for (int i = 0; i < transparent; i++)
        {
            block.U32();
            block.U32();
            if (block.U16() < 255) opaque = false;
        }

        if (stops.Count == 0) throw PsdFormat.Invalid("a Gradient Map without colours");
        var ordered = stops.OrderBy(stop => stop.Location).ToList();
        if (ordered.Count != 2 || ordered.Any(stop => stop.Midpoint != 50 || stop.Mode != 0) || !opaque)
            note(PsdNote.GradientMapSimplified);

        return new LayerAdjustment(AdjustmentKind.GradientMap)
        {
            GradientMapSettings = new GradientMapSettings
            {
                Shadows = ordered[0].Colour.Clamped,
                Highlights = ordered[^1].Colour.Clamped,
                Reversed = reversed,
            },
        };
    }

    /// <summary>The block that stands for an adjustment in Photoshop, or null for Grain, which has none.</summary>
    public static PsdBlockWriter? Writer(LayerAdjustment adjustment, Action<PsdNote> note)
    {
        switch (adjustment.Kind)
        {
            case AdjustmentKind.Levels:
                return new PsdBlockWriter("levl", writer => WriteLevels(writer, adjustment.Levels));
            case AdjustmentKind.Curves:
                return new PsdBlockWriter("curv", writer => WriteCurves(writer, adjustment.Curves));
            case AdjustmentKind.Hsv:
                if (adjustment.ResolvedHsv.InvertRange) note(PsdNote.AdjustmentSimplified);
                return new PsdBlockWriter("hue2", writer => WriteHueSaturation(writer, adjustment.ResolvedHsv));
            case AdjustmentKind.Exposure:
                return new PsdBlockWriter("expA", writer =>
                {
                    ExposureSettings exposure = adjustment.Exposure.Normalized;
                    writer.U16(1);
                    writer.F32((float)exposure.Exposure);
                    writer.F32((float)exposure.Offset);
                    writer.F32((float)exposure.Gamma);
                });
            case AdjustmentKind.GradientMap:
                return new PsdBlockWriter("grdm", writer => WriteGradientMap(writer, adjustment.GradientMap));
            default:
                note(PsdNote.GrainDropped);
                return null;
        }
    }

    private static void WriteLevels(PsdWriter writer, LevelsSettings levels)
    {
        writer.U16(2);
        for (int i = 0; i < 29; i++)
        {
            LevelRange range = i < levels.Ranges.Count ? levels.Ranges[i].Normalized : new LevelRange();
            writer.U16((ushort)Math.Clamp(Math.Round(range.Black), 0, 253));
            writer.U16((ushort)Math.Clamp(Math.Round(range.White), 2, 255));
            writer.U16((ushort)Math.Clamp(Math.Round(range.OutputBlack), 0, 255));
            writer.U16((ushort)Math.Clamp(Math.Round(range.OutputWhite), 0, 255));
            writer.U16((ushort)Math.Clamp(Math.Round(range.Gamma * 100), 10, 999));
        }
    }

    private static void WriteCurves(PsdWriter writer, CurvesSettings curves)
    {
        List<List<(int Input, int Output)>> channels = [.. Enumerable.Range(0, 4).Select(channel => Handles(curves, channel))];

        writer.U8(0);
        writer.U16(1);
        writer.U32(0b1111);
        foreach (var points in channels) WritePoints(writer, points);

        // Photoshop follows the curves with the same curves again, keyed by channel, and reads
        // that copy in preference; writing both keeps every version reading the same thing.
        writer.Key("Crv ");
        writer.U16(4);
        writer.U32(4);
        for (int channel = 0; channel < 4; channel++)
        {
            writer.U16((ushort)channel);
            WritePoints(writer, channels[channel]);
        }

        static void WritePoints(PsdWriter writer, List<(int Input, int Output)> points)
        {
            writer.U16((ushort)points.Count);
            foreach ((int input, int output) in points)
            {
                writer.U16((ushort)output);
                writer.U16((ushort)input);
            }
        }
    }

    /// <summary>
    /// A channel's handles as whole numbers, strictly increasing, at most sixteen — Photoshop's
    /// limit; a longer curve is sampled at sixteen evenly spaced inputs.
    /// </summary>
    private static List<(int Input, int Output)> Handles(CurvesSettings curves, int channel)
    {
        EquatableList<CurvePoint> source = curves.Channels[channel];
        IEnumerable<(double X, double Y)> points = source.Count <= 16
            ? source.Select(point => (point.X, point.Y))
            : Enumerable.Range(0, 16).Select(i => (i * 17.0, curves.Value(i * 17.0, channel)));

        var result = new List<(int Input, int Output)>();
        foreach ((double x, double y) in points)
        {
            int input = (int)Math.Clamp(Math.Round(x), 0, 255), output = (int)Math.Clamp(Math.Round(y), 0, 255);
            if (result.Count == 0 || input > result[^1].Input) result.Add((input, output));
        }
        if (result.Count < 2) return [(0, 0), (255, 255)];
        return result;
    }

    private static void WriteHueSaturation(PsdWriter writer, HueSaturationSettings settings)
    {
        RangeAdjustment master = settings.Adjustments.ValueOr(ColorRange.Master, new RangeAdjustment());

        writer.U16(2);
        writer.U8(settings.Colorize ? (byte)1 : (byte)0);
        writer.U8(0);

        // Colorize keeps its own three values beside the master's; this editor keeps one set.
        if (settings.Colorize)
        {
            double hue = master.Hue > 180 ? master.Hue - 360 : master.Hue;
            Triple(hue, master.Saturation, master.Lightness);
            Triple(0, 0, 0);
        }
        else
        {
            Triple(0, 25, 0);
            Triple(master.Hue, master.Saturation, master.Lightness);
        }

        foreach (ColorRange range in ColorRanges.Colors)
        {
            HueBand band = settings.Bands.ValueOr(range, range.DefaultBand());
            foreach (double handle in band.Handles) writer.I16((short)Math.Round(((handle % 360) + 360) % 360));
            RangeAdjustment adjustment = settings.Adjustments.ValueOr(range, new RangeAdjustment());
            Triple(adjustment.Hue, adjustment.Saturation, adjustment.Lightness);
        }

        void Triple(double h, double s, double l)
        {
            writer.I16((short)Math.Clamp(Math.Round(h), -180, 180));
            writer.I16((short)Math.Clamp(Math.Round(s), -100, 100));
            writer.I16((short)Math.Clamp(Math.Round(l), -100, 100));
        }
    }

    private static void WriteGradientMap(PsdWriter writer, GradientMapSettings settings)
    {
        GradientMapSettings map = settings.Normalized;
        writer.U16(1);
        writer.U8(map.Reversed ? (byte)1 : (byte)0);
        writer.U8(0);
        writer.Unicode("Custom", terminated: true);

        writer.U16(2);
        Stop(0, map.Shadows);
        Stop(4096, map.Highlights);

        writer.U16(2);
        foreach (uint location in new uint[] { 0, 4096 })
        {
            writer.U32(location);
            writer.U32(50);
            writer.U16(255);
        }

        // Expansion, smoothness, length and mode, then noise-gradient settings left at
        // Photoshop's own defaults for a solid gradient.
        writer.U16(2);
        writer.U16(4096);
        writer.U16(32);
        writer.U16(0);
        writer.U32(0);
        writer.U16(0);
        writer.U16(1);
        writer.U32(2048);
        writer.U16(3);
        writer.Zeros(8);
        for (int i = 0; i < 4; i++) writer.U16(32768);
        writer.Zeros(2);

        void Stop(uint location, AdjustmentColor colour)
        {
            writer.U32(location);
            writer.U32(50);
            writer.U16(0);
            writer.U16((ushort)Math.Round(colour.Red * 65535));
            writer.U16((ushort)Math.Round(colour.Green * 65535));
            writer.U16((ushort)Math.Round(colour.Blue * 65535));
            writer.U16(0);
            writer.Zeros(2);
        }
    }
}

/// <summary>
/// Drop shadow, outer glow and stroke in Photoshop's object-based effects block (<c>lfx2</c>).
/// </summary>
/// <remarks>
/// Written with every key Photoshop itself writes for each effect, in its order, so Photoshop opens
/// them as editable effects rather than baked pixels. Photoshop stores each size before the
/// document's effect scale (<c>Scl </c>) is applied, and a spread as a percentage even where the
/// unit says pixels.
/// </remarks>
internal static class PsdEffects
{
    public static LayerEffects? Read(PsdFile file, PsdLayerRecord layer, Action<PsdNote> note)
    {
        PsdReader? block = file.Block(layer, "lfx2") ?? file.Block(layer, "lmfx");
        if (block is null) return null;

        PsdDescriptor root;
        try
        {
            block.U32();
            block.U32();
            root = PsdDescriptor.Read(block);
        }
        catch (ProjectException)
        {
            note(PsdNote.EffectDropped);
            return null;
        }

        if (root.Bool("masterFXSwitch") == false) return null;
        double scale = (root.Number("Scl ") ?? 100) / 100;

        foreach (string key in new[] { "IrSh", "innerShadowMulti", "IrGl", "ebbl", "ChFX", "SoFi", "solidFillMulti", "GrFl", "gradientFillMulti", "patFill", "patternFill" })
            if (Enabled(root, key) is not null) note(PsdNote.EffectDropped);

        ShadowEffect? shadow = null;
        if ((Enabled(root, "DrSh") ?? Enabled(root, "dropShadowMulti")) is PsdDescriptor s)
        {
            (double r, double g, double b) = Colour(s);
            shadow = new ShadowEffect
            {
                Red = r, Green = g, Blue = b,
                Opacity = Percent(s, "Opct", 75),
                Blend = PsdBlend.FromDescriptor(s.Enum("Md  ")) ?? LayerBlendMode.Multiply,
                Angle = s.Bool("uglg") == true ? file.GlobalAngle : s.Number("lagl") ?? 120,
                Distance = Math.Clamp((s.Number("Dstn") ?? 0) * scale, 0, 30_000),
                Size = Math.Clamp((s.Number("blur") ?? 0) * scale, 0, 250),
                Spread = Percent(s, "Ckmt", 0),
            };
        }

        GlowEffect? glow = null;
        if (Enabled(root, "OrGl") is PsdDescriptor g2)
        {
            (double r, double g, double b) = Colour(g2);
            glow = new GlowEffect
            {
                Red = r, Green = g, Blue = b,
                Opacity = Percent(g2, "Opct", 75),
                Blend = PsdBlend.FromDescriptor(g2.Enum("Md  ")) ?? LayerBlendMode.Screen,
                Size = Math.Clamp((g2.Number("blur") ?? 0) * scale, 0, 250),
                Spread = Percent(g2, "Ckmt", 0),
            };
        }

        StrokeEffect? stroke = null;
        if ((Enabled(root, "FrFX") ?? Enabled(root, "frameFXMulti")) is PsdDescriptor k)
        {
            if ((k.Enum("PntT") ?? "SClr") != "SClr" || (k.Enum("Md  ") ?? "Nrml") != "Nrml")
                note(PsdNote.EffectSimplified);
            (double r, double g, double b) = Colour(k);
            stroke = new StrokeEffect
            {
                Red = r, Green = g, Blue = b,
                Opacity = Percent(k, "Opct", 100),
                Size = Math.Clamp((k.Number("Sz  ") ?? 3) * scale, 1, 250),
                Position = k.Enum("Styl") switch
                {
                    "InsF" => StrokePosition.Inside,
                    "CtrF" => StrokePosition.Center,
                    _ => StrokePosition.Outside,
                },
            };
        }

        if (shadow is null && glow is null && stroke is null) return null;
        return new LayerEffects { Shadow = shadow, Glow = glow, Stroke = stroke };
    }

    /// <summary>An effect's descriptor when it is switched on: the key itself, or the first enabled one of a list.</summary>
    private static PsdDescriptor? Enabled(PsdDescriptor root, string key) => root[key] switch
    {
        PsdDescriptor effect when effect.Bool("enab") != false => effect,
        List<object> list => list.OfType<PsdDescriptor>().FirstOrDefault(effect => effect.Bool("enab") != false),
        _ => null,
    };

    private static double Percent(PsdDescriptor effect, string key, double fallback) =>
        Math.Clamp((effect.Number(key) ?? fallback) / 100, 0, 1);

    private static (double R, double G, double B) Colour(PsdDescriptor effect)
    {
        PsdDescriptor? colour = effect.Object("Clr ");
        if (colour is null) return (0, 0, 0);
        return (Channel(colour.Number("Rd  ")), Channel(colour.Number("Grn ")), Channel(colour.Number("Bl  ") ?? colour.Number("Bl ")));

        static double Channel(double? value) => Math.Clamp((value ?? 0) / 255, 0, 1);
    }

    /// <summary>The effects block's data, or null when the layer has none switched on.</summary>
    public static Action<PsdWriter>? Writer(LayerEffects? effects)
    {
        if (effects is null) return null;
        if (effects.Shadow is null && effects.Glow is null && effects.Stroke is null) return null;

        var root = new PsdDescriptor()
            .Add("Scl ", new PsdUnitFloat("#Prc", 100))
            .Add("masterFXSwitch", true);

        if (effects.Shadow is ShadowEffect shadow)
        {
            root.Add("DrSh", new PsdDescriptor { ClassId = "DrSh" }
                .Add("enab", shadow.Enabled)
                .Add("present", true)
                .Add("showInDialog", true)
                .Add("Md  ", new PsdEnum("BlnM", PsdBlend.ToDescriptor(shadow.Blend)))
                .Add("Clr ", Rgb(shadow.Red, shadow.Green, shadow.Blue))
                .Add("Opct", new PsdUnitFloat("#Prc", shadow.Opacity * 100))
                .Add("uglg", false)
                .Add("lagl", new PsdUnitFloat("#Ang", shadow.Angle))
                .Add("Dstn", new PsdUnitFloat("#Pxl", shadow.Distance))
                .Add("Ckmt", new PsdUnitFloat("#Pxl", shadow.Spread * 100))
                .Add("blur", new PsdUnitFloat("#Pxl", shadow.Size))
                .Add("Nose", new PsdUnitFloat("#Prc", 0))
                .Add("AntA", false)
                .Add("TrnS", Linear())
                .Add("layerConceals", true));
        }

        if (effects.Glow is GlowEffect glow)
        {
            root.Add("OrGl", new PsdDescriptor { ClassId = "OrGl" }
                .Add("enab", glow.Enabled)
                .Add("present", true)
                .Add("showInDialog", true)
                .Add("Md  ", new PsdEnum("BlnM", PsdBlend.ToDescriptor(glow.Blend)))
                .Add("Clr ", Rgb(glow.Red, glow.Green, glow.Blue))
                .Add("Opct", new PsdUnitFloat("#Prc", glow.Opacity * 100))
                .Add("GlwT", new PsdEnum("BETE", "SfBL"))
                .Add("Ckmt", new PsdUnitFloat("#Pxl", glow.Spread * 100))
                .Add("blur", new PsdUnitFloat("#Pxl", glow.Size))
                .Add("Nose", new PsdUnitFloat("#Prc", 0))
                .Add("ShdN", new PsdUnitFloat("#Prc", 0))
                .Add("AntA", false)
                .Add("TrnS", Linear())
                .Add("Inpr", new PsdUnitFloat("#Prc", 50)));
        }

        if (effects.Stroke is StrokeEffect stroke)
        {
            root.Add("FrFX", new PsdDescriptor { ClassId = "FrFX" }
                .Add("enab", stroke.Enabled)
                .Add("present", true)
                .Add("showInDialog", true)
                .Add("Styl", new PsdEnum("FStl", stroke.Position switch
                {
                    StrokePosition.Inside => "InsF",
                    StrokePosition.Center => "CtrF",
                    _ => "OutF",
                }))
                .Add("PntT", new PsdEnum("FrFl", "SClr"))
                .Add("Md  ", new PsdEnum("BlnM", "Nrml"))
                .Add("Opct", new PsdUnitFloat("#Prc", stroke.Opacity * 100))
                .Add("Sz  ", new PsdUnitFloat("#Pxl", stroke.Size))
                .Add("Clr ", Rgb(stroke.Red, stroke.Green, stroke.Blue))
                .Add("overprint", false));
        }

        return writer =>
        {
            writer.U32(0);
            writer.U32(16);
            root.Write(writer);
        };
    }

    private static PsdDescriptor Rgb(double red, double green, double blue) =>
        new PsdDescriptor { ClassId = "RGBC" }
            .Add("Rd  ", red * 255)
            .Add("Grn ", green * 255)
            .Add("Bl  ", blue * 255);

    private static PsdDescriptor Linear() =>
        new PsdDescriptor { ClassId = "ShpC" }
            .Add("Nm  ", "Linear")
            .Add("Crv ", new List<object>
            {
                new PsdDescriptor { ClassId = "CrPt" }.Add("Hrzn", 0.0).Add("Vrtc", 0.0),
                new PsdDescriptor { ClassId = "CrPt" }.Add("Hrzn", 255.0).Add("Vrtc", 255.0),
            });
}
