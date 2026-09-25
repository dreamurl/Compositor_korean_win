using System.Text.Json;
using static Compositor_korean_win.Core.ToolSchema;

namespace Compositor_korean_win.Core;

public sealed partial class McpTools
{
    private void DefineEffectTools()
    {
        Define("set_effects",
            "Set a layer's effects — drop shadow, outer glow, stroke — which follow the layer wherever it goes. Give an object " +
            "to set or change an effect (only the fields given change), or false to remove it.",
            Build(LayerArgument,
                  Object("drop_shadow", "Or false to remove.",
                         Colour("color", "Default black."), Num("opacity", "0–1. Default 0.75."), Num("angle", "Light angle in degrees. Default 120."),
                         Num("distance", "Pixels. Default 10."), Num("size", "Blur size, 0–250. Default 10."), Num("spread", "0–1."),
                         Choice("blend_mode", "Default multiply.", BlendNames), Bool("enabled", "Switch it off without losing it.")),
                  Object("outer_glow", "Or false to remove.",
                         Colour("color", "Default pale yellow."), Num("opacity", "0–1. Default 0.75."), Num("size", "0–250. Default 10."),
                         Num("spread", "0–1."), Choice("blend_mode", "Default screen.", BlendNames), Bool("enabled", "On or off.")),
                  Object("stroke", "An outline round the layer's shape. Or false to remove.",
                         Colour("color", "Default black."), Num("size", "Width, 1–250. Default 3."), Num("opacity", "0–1. Default 1."),
                         Choice("position", "Default outside.", ["outside", "inside", "center"]), Bool("enabled", "On or off.")),
                  DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                LayerEffects effects = layer.Effects ?? new LayerEffects();

                if (Removed(arguments, "drop_shadow")) effects = effects with { Shadow = null };
                else if (arguments.Object("drop_shadow") is ToolArguments s)
                {
                    ShadowEffect shadow = effects.Shadow ?? new ShadowEffect();
                    if (s.Colour("color") is Rgba c) shadow = shadow with { Red = c.R / 255.0, Green = c.G / 255.0, Blue = c.B / 255.0 };
                    shadow = shadow with
                    {
                        Opacity = Unit(s.Number("opacity")) ?? shadow.Opacity,
                        Angle = s.Number("angle") ?? shadow.Angle,
                        Distance = Math.Clamp(s.Number("distance") ?? shadow.Distance, 0, 30_000),
                        Size = Math.Clamp(s.Number("size") ?? shadow.Size, 0, 250),
                        Spread = Unit(s.Number("spread")) ?? shadow.Spread,
                        Blend = s.String("blend_mode") is string blend ? Blend(blend) : shadow.Blend,
                        Enabled = s.Bool("enabled") ?? true,
                    };
                    effects = effects with { Shadow = shadow };
                }

                if (Removed(arguments, "outer_glow")) effects = effects with { Glow = null };
                else if (arguments.Object("outer_glow") is ToolArguments g)
                {
                    GlowEffect glow = effects.Glow ?? new GlowEffect();
                    if (g.Colour("color") is Rgba c) glow = glow with { Red = c.R / 255.0, Green = c.G / 255.0, Blue = c.B / 255.0 };
                    glow = glow with
                    {
                        Opacity = Unit(g.Number("opacity")) ?? glow.Opacity,
                        Size = Math.Clamp(g.Number("size") ?? glow.Size, 0, 250),
                        Spread = Unit(g.Number("spread")) ?? glow.Spread,
                        Blend = g.String("blend_mode") is string blend ? Blend(blend) : glow.Blend,
                        Enabled = g.Bool("enabled") ?? true,
                    };
                    effects = effects with { Glow = glow };
                }

                if (Removed(arguments, "stroke")) effects = effects with { Stroke = null };
                else if (arguments.Object("stroke") is ToolArguments k)
                {
                    StrokeEffect stroke = effects.Stroke ?? new StrokeEffect();
                    if (k.Colour("color") is Rgba c) stroke = stroke with { Red = c.R / 255.0, Green = c.G / 255.0, Blue = c.B / 255.0 };
                    stroke = stroke with
                    {
                        Opacity = Unit(k.Number("opacity")) ?? stroke.Opacity,
                        Size = Math.Clamp(k.Number("size") ?? stroke.Size, 1, 250),
                        Position = k.String("position") switch
                        {
                            null => stroke.Position,
                            "inside" => StrokePosition.Inside,
                            "center" or "centre" => StrokePosition.Center,
                            "outside" => StrokePosition.Outside,
                            _ => throw new ToolException("'position' is outside, inside or center."),
                        },
                        Enabled = k.Bool("enabled") ?? true,
                    };
                    effects = effects with { Stroke = stroke };
                }

                LayerEffects? result = effects.Shadow is null && effects.Glow is null && effects.Stroke is null ? null : effects;
                if (result is { IsValid: false }) throw new ToolException("Those effect settings are out of range.");
                _session.Edit(open, "Layer Style", document => document.Replacing(layer with { Effects = result }));
                return ToolResult.Text(result is null ? $"'{layer.Name}' has no effects." : $"Effects set on '{layer.Name}'.");
            });

        Define("add_adjustment",
            "Add an adjustment layer, which changes how everything below it looks without changing those layers: levels, " +
            "curves, hue_saturation (saturation -100 makes black and white), exposure, gradient_map (maps dark to 'shadows' " +
            "and light to 'highlights' — a duotone) or grain. Levels and curves take the composite and, in red/green/blue " +
            "(levels) or red_points/green_points/blue_points (curves), each colour channel; hue_saturation takes the master " +
            "values and, in 'ranges', the reds, yellows, greens, cyans, blues or magentas alone. 'clip' limits it to the layer " +
            "directly below. Change one later with edit_adjustment.",
            Build(With([Choice("kind", "Which adjustment.", AdjustmentKinds, true), .. AdjustmentProperties, Bool("clip", "Clip to the layer directly below.")])),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                string kind = arguments.Required("kind");
                LayerAdjustment adjustment = Adjustment(kind, arguments, start: null);
                if (!adjustment.IsValid) throw new ToolException("Those adjustment settings are out of range.");
                var layer = new ImageLayer
                {
                    Id = Guid.NewGuid(),
                    Name = AdjustmentTitle(adjustment.Kind),
                    Transform = Canvas(open.Document),
                    Adjustment = adjustment,
                };
                ToolResult added = AddLayer(open, arguments, "Adjustment Layer", layer, "adjustment");
                // Put inside a clipping group it is clipped already, and toggling would release it.
                if (arguments.Bool("clip") == true && !LayerCommands.IsClipped(open.Document, layer.Id))
                {
                    _session.Edit(open, "Clipping Mask", document =>
                        LayerCommands.ToggleClipping(document, layer.Id) ?? throw new ToolException("It cannot be clipped to what is below."));
                }
                return added;
            });

        Define("edit_adjustment",
            "Change an adjustment layer's settings. Anything left out stays as it is (get_document shows the current values); " +
            "a different 'kind' starts that kind from its defaults.",
            Build([LayerArgument, Choice("kind", "Change it to another kind.", AdjustmentKinds), .. AdjustmentProperties, DocumentArgument]),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                if (layer.Adjustment is not LayerAdjustment current || layer.IsGroup)
                    throw new ToolException($"'{layer.Name}' is not an adjustment layer.");
                string kind = arguments.String("kind") ?? AdjustmentName(current.Kind);
                LayerAdjustment adjustment = Adjustment(kind, arguments, current);
                if (!adjustment.IsValid) throw new ToolException("Those adjustment settings are out of range.");
                _session.Edit(open, "Adjustment", document => document.Replacing(layer with { Adjustment = adjustment }));
                return ToolResult.Text($"'{layer.Name}' is now {AdjustmentName(adjustment.Kind)}: {AdjustmentSummary(adjustment)}.");
            });

        Define("apply_filter",
            "Change a layer's pixels with a filter, for good (undo takes it back): gaussian_blur (radius), motion_blur (angle, " +
            "distance), add_noise (amount, gaussian, monochromatic), lens_correction (distortion), pinch or spherize " +
            "(strength), twirl (angle), wave (wavelength, amplitude), polar_coordinates (to_polar), invert, or an adjustment " +
            "baked into the pixels — levels, curves, hue_saturation, exposure, gradient_map, grain, with add_adjustment's " +
            "settings. Keeps to the selection. A text layer becomes pixels.",
            Build([LayerArgument,
                   Choice("filter", "Which filter.", [.. FilterNames, .. AdjustmentKinds], true),
                   Num("radius", "gaussian_blur: pixels."), Num("angle", "motion_blur: degrees, -90–90; twirl: degrees, -999–999."),
                   Num("distance", "motion_blur: pixels."), Num("amount", "add_noise: 0–400 percent; grain: 0–100."),
                   Bool("gaussian", "add_noise: gaussian rather than uniform."), Bool("monochromatic", "add_noise: grey noise."),
                   Num("distortion", "lens_correction: -100–100; positive straightens barrel distortion."),
                   Num("strength", "pinch, spherize: -100–100."), Num("wavelength", "wave: pixels from crest to crest, 2–999."),
                   Num("amplitude", "wave: height in pixels, 0–999."), Bool("to_polar", "polar_coordinates: rectangular to polar (default) or back."),
                   .. AdjustmentProperties.Where(property => property.Name is not ("amount")),
                   DocumentArgument]),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                if (layer.Image is not PixelBuffer image) throw new ToolException($"'{layer.Name}' has no pixels to filter.");
                string filter = arguments.Required("filter");
                _session.Edit(open, "Filter", document => document.Replacing(Filtered(open, layer, image, filter, arguments)));
                ImageLayer after = open.Document.Layer(layer.Id)!;
                return ToolResult.Text($"Filtered '{after.Name}' with {filter}{(open.Selection is null ? "" : " inside the selection")}.");
            });

        Define("remove_background",
            "Find the subject of a photo layer with the AI model and hide everything else with a layer mask (the pixels are kept).",
            Build(LayerArgument, DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                if (layer.Image is not PixelBuffer image) throw new ToolException($"'{layer.Name}' has no pixels.");
                ISubjectModel model = _session.Services.SubjectModel(out string? error)
                                      ?? throw new ToolException("Background removal is not available: " + error);
                float[] logits = model.Predict(SubjectMatte.Input(image, model.Side));
                if (!BackgroundRemoval.FoundSubject(logits)) throw new ToolException("No subject was found in that layer.");
                PixelBuffer mask = BackgroundRemoval.Mask(layer, logits, model.Side, new BackgroundSettings(), null, BackgroundRemoval.CommitLimit);
                _session.Edit(open, "Remove Background", document => document.Replacing(BackgroundRemoval.WithMask(layer, mask)));
                return ToolResult.Text($"Masked the background of '{layer.Name}'.");
            });

        Define("generate_image",
            "Make a picture from a description — a photograph of a person, a texture — with the image generator this machine " +
            "has set up (Codex's $imagegen, OpenAI's image API, or a configured command), and place it as a layer.",
            Build(With(Str("prompt", "What the picture shows, in detail: subject, lighting, style, background.", true),
                       Int("width", "Wanted width. Default 1024."), Int("height", "Wanted height. Default 1024."),
                       Str("save_to", "Also keep the picture as a file here."),
                       Bool("add_layer", "Place it in the current document (default), or only save it."),
                       Choice("fit", "Size to the canvas.", ["none", "contain", "cover", "fill"]),
                       Num("x", "Left."), Num("y", "Top."))),
            arguments =>
            {
                IImageGenerator generator = _session.Services.ImageGenerator ?? throw new ToolException(
                    "No image generator is set up. Sign in to the Codex CLI (codex on the PATH), set OPENAI_API_KEY, or set " +
                    "COMPOSITOR_IMAGE_COMMAND to a command that writes a PNG to {output}. Or make the picture elsewhere and use add_image.");
                int width = Math.Clamp(arguments.Int("width") ?? 1024, 64, 4096), height = Math.Clamp(arguments.Int("height") ?? 1024, 64, 4096);
                string prompt = arguments.Words("prompt") is { Length: > 0 } given ? given : throw new ToolException("'prompt' is required.");
                byte[] encoded = generator.Generate(prompt, width, height);

                string saved = "";
                if (arguments.String("save_to") is string path)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    File.WriteAllBytes(path, encoded);
                    saved = $" Saved to {path}.";
                }
                if (arguments.Bool("add_layer") == false) return ToolResult.Text($"Generated with {generator.Name}.{saved}");

                PixelBuffer pixels = _session.Services.DecodeImage(encoded);
                if (_session.Current is null)
                {
                    EditorSession.Open made = _session.Add(FromImage(pixels, "Generated"), "Generated", path: null);
                    return Described(made, $"Generated with {generator.Name} as a new document {made.Id}.{saved}");
                }

                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = Placed(open.Document, pixels, "Generated", arguments, sized: false);
                ToolResult added = AddLayer(open, arguments, "Generate Image", layer, "generated image");
                added.Content.Insert(0, ToolContent.Of($"Generated with {generator.Name}.{saved}"));
                return added;
            });
    }

    private static bool Removed(ToolArguments arguments, string name) =>
        arguments.Raw(name) is { ValueKind: JsonValueKind.False };

    private static double? Unit(double? value) => value is double v ? Math.Clamp(v > 1 ? v / 100 : v, 0, 1) : null;

    private static readonly string[] AdjustmentKinds = ["levels", "curves", "hue_saturation", "exposure", "gradient_map", "grain"];

    private static readonly string[] FilterNames =
        ["gaussian_blur", "motion_blur", "add_noise", "lens_correction", "pinch", "spherize", "twirl", "wave", "polar_coordinates", "invert"];

    private static readonly string[] RangeNames = ["reds", "yellows", "greens", "cyans", "blues", "magentas"];

    /// <summary>Every setting an adjustment takes, for add_adjustment, edit_adjustment and apply_filter alike.</summary>
    private static Property[] AdjustmentProperties =>
    [
        Num("black", "levels: input black point 0–254."), Num("white", "levels: input white point 1–255."),
        Num("gamma", "levels: midtones 0.1–9.99 (above 1 brightens); exposure: gamma 0.01–9.99."),
        Num("output_black", "levels: output black 0–255."), Num("output_white", "levels: output white 0–255."),
        Object("red", "levels: the red channel alone.", LevelsChannelProperties),
        Object("green", "levels: the green channel alone.", LevelsChannelProperties),
        Object("blue", "levels: the blue channel alone.", LevelsChannelProperties),
        new Property("points", "array", "curves: [[input, output], …] in 0–255, from 0 to 255 — the composite curve.", Items: "array"),
        new Property("red_points", "array", "curves: the red channel's curve.", Items: "array"),
        new Property("green_points", "array", "curves: the green channel's curve.", Items: "array"),
        new Property("blue_points", "array", "curves: the blue channel's curve.", Items: "array"),
        Num("hue", "hue_saturation: -180–180 (0–360 when colorizing)."), Num("saturation", "hue_saturation: -100–100."),
        Num("lightness", "hue_saturation: -100–100."), Bool("colorize", "hue_saturation: tint everything one hue."),
        Object("ranges", "hue_saturation: one colour range at a time, e.g. {\"reds\": {\"saturation\": -60}}.",
               [.. RangeNames.Select(name => Object(name, "", Num("hue", "-180–180."), Num("saturation", "-100–100."), Num("lightness", "-100–100.")))]),
        Num("exposure", "exposure: stops, -20–20."), Num("offset", "exposure: -0.5–0.5."),
        Colour("shadows", "gradient_map: colour for the darks."), Colour("highlights", "gradient_map: colour for the lights."),
        Bool("reversed", "gradient_map: swap the ends."), Num("amount", "grain: 0–100."), Num("size", "grain: grain size."),
        Num("roughness", "grain: 0–100."), Int("seed", "grain: which random pattern."),
    ];

    private static Property[] LevelsChannelProperties =>
    [
        Num("black", "Input black 0–254."), Num("white", "Input white 1–255."), Num("gamma", "Midtones 0.1–9.99."),
        Num("output_black", "Output black 0–255."), Num("output_white", "Output white 0–255."),
    ];

    private static string AdjustmentTitle(AdjustmentKind kind) => kind switch
    {
        AdjustmentKind.Levels => "Levels",
        AdjustmentKind.Curves => "Curves",
        AdjustmentKind.Hsv => "Hue/Saturation",
        AdjustmentKind.Exposure => "Exposure",
        AdjustmentKind.GradientMap => "Gradient Map",
        _ => "Grain",
    };

    /// <summary>
    /// The adjustment the arguments describe. Starting from <paramref name="start"/> when it is of the
    /// same kind, whatever the arguments leave out stays as it was — how edit_adjustment changes one
    /// setting without resetting the rest.
    /// </summary>
    private static LayerAdjustment Adjustment(string kind, ToolArguments arguments, LayerAdjustment? start)
    {
        AdjustmentKind wanted = kind switch
        {
            "levels" => AdjustmentKind.Levels,
            "curves" => AdjustmentKind.Curves,
            "hue_saturation" => AdjustmentKind.Hsv,
            "exposure" => AdjustmentKind.Exposure,
            "gradient_map" => AdjustmentKind.GradientMap,
            "grain" => AdjustmentKind.Grain,
            _ => throw new ToolException("'kind' is levels, curves, hue_saturation, exposure, gradient_map or grain."),
        };
        LayerAdjustment? same = start?.Kind == wanted ? start : null;

        switch (wanted)
        {
            case AdjustmentKind.Levels:
            {
                LevelsSettings levels = same?.Levels ?? new LevelsSettings();
                List<LevelRange> ranges = [.. levels.Ranges];
                while (ranges.Count < 4) ranges.Add(new LevelRange());
                ranges[0] = Merged(ranges[0], arguments);
                string[] channels = ["red", "green", "blue"];
                for (int i = 0; i < 3; i++)
                    if (arguments.Object(channels[i]) is ToolArguments channel) ranges[i + 1] = Merged(ranges[i + 1], channel);
                return new LayerAdjustment(AdjustmentKind.Levels) { Levels = levels with { Ranges = new EquatableList<LevelRange>(ranges) } };

                static LevelRange Merged(LevelRange range, ToolArguments given) => new LevelRange
                {
                    Black = given.Number("black", range.Black),
                    White = given.Number("white", range.White),
                    Gamma = given.Number("gamma", range.Gamma),
                    OutputBlack = given.Number("output_black", range.OutputBlack),
                    OutputWhite = given.Number("output_white", range.OutputWhite),
                }.Normalized;
            }
            case AdjustmentKind.Curves:
            {
                CurvesSettings curves = same?.Curves ?? new CurvesSettings();
                List<EquatableList<CurvePoint>> channels = [.. curves.Channels];
                string[] names = ["points", "red_points", "green_points", "blue_points"];
                for (int i = 0; i < 4; i++)
                    if (arguments.Raw(names[i]) is { ValueKind: JsonValueKind.Array } list) channels[i] = Curve(list, names[i]);
                return new LayerAdjustment(AdjustmentKind.Curves)
                {
                    Curves = curves with { Channels = new EquatableList<EquatableList<CurvePoint>>(channels) },
                };
            }
            case AdjustmentKind.Hsv:
            {
                HueSaturationSettings hsv = same?.ResolvedHsv ?? new HueSaturationSettings();
                bool colorize = arguments.Bool("colorize") ?? hsv.Colorize;
                RangeAdjustment master = hsv.Adjustments.ValueOr(ColorRange.Master, new RangeAdjustment());
                if (colorize && !hsv.Colorize && !arguments.Has("saturation")) master = new RangeAdjustment(master.Hue, 25, master.Lightness);
                double hue = arguments.Number("hue", master.Hue);
                hue = colorize ? ((hue % 360) + 360) % 360 : Math.Clamp(hue, -180, 180);
                master = new RangeAdjustment(hue,
                                             Math.Clamp(arguments.Number("saturation", master.Saturation), colorize ? 0 : -100, 100),
                                             Math.Clamp(arguments.Number("lightness", master.Lightness), -100, 100));
                ColorRangeMap<RangeAdjustment> adjustments = hsv.Adjustments.With(ColorRange.Master, master);

                if (arguments.Object("ranges") is ToolArguments ranges)
                {
                    for (int i = 0; i < RangeNames.Length; i++)
                    {
                        if (ranges.Object(RangeNames[i]) is not ToolArguments one) continue;
                        ColorRange range = ColorRanges.Colors[i];
                        RangeAdjustment before = adjustments.ValueOr(range, new RangeAdjustment());
                        adjustments = adjustments.With(range, new RangeAdjustment(
                            Math.Clamp(one.Number("hue", before.Hue), -180, 180),
                            Math.Clamp(one.Number("saturation", before.Saturation), -100, 100),
                            Math.Clamp(one.Number("lightness", before.Lightness), -100, 100)));
                    }
                }

                return new LayerAdjustment(AdjustmentKind.Hsv)
                {
                    HsvSettings = hsv with { Colorize = colorize, Adjustments = adjustments, Range = ColorRange.Master },
                };
            }
            case AdjustmentKind.Exposure:
            {
                ExposureSettings exposure = same?.Exposure ?? new ExposureSettings();
                return new LayerAdjustment(AdjustmentKind.Exposure)
                {
                    ExposureSettings = new ExposureSettings
                    {
                        Exposure = arguments.Number("exposure", exposure.Exposure),
                        Offset = arguments.Number("offset", exposure.Offset),
                        Gamma = arguments.Number("gamma", exposure.Gamma),
                    }.Normalized,
                };
            }
            case AdjustmentKind.GradientMap:
            {
                GradientMapSettings map = same?.GradientMap ?? new GradientMapSettings();
                return new LayerAdjustment(AdjustmentKind.GradientMap)
                {
                    GradientMapSettings = map with
                    {
                        Shadows = arguments.Colour("shadows") is Rgba dark ? new AdjustmentColor(dark.R / 255.0, dark.G / 255.0, dark.B / 255.0) : map.Shadows,
                        Highlights = arguments.Colour("highlights") is Rgba light ? new AdjustmentColor(light.R / 255.0, light.G / 255.0, light.B / 255.0) : map.Highlights,
                        Reversed = arguments.Bool("reversed") ?? map.Reversed,
                    },
                };
            }
            default:
            {
                GrainSettings grain = same?.Grain ?? new GrainSettings();
                return new LayerAdjustment(AdjustmentKind.Grain)
                {
                    GrainSettings = grain with
                    {
                        Amount = Math.Clamp(arguments.Number("amount", grain.Amount), 0, 100),
                        Size = Math.Max(0.1, arguments.Number("size", grain.Size)),
                        Roughness = Math.Clamp(arguments.Number("roughness", grain.Roughness), 0, 100),
                        Seed = arguments.Int("seed") is int seed ? unchecked((uint)seed) : grain.Seed,
                    },
                };
            }
        }
    }

    /// <summary>A curve from [[input, output], …] or [{input, output}, …], spanning 0–255 in order.</summary>
    private static EquatableList<CurvePoint> Curve(JsonElement list, string name)
    {
        var points = new List<CurvePoint>();
        foreach (JsonElement point in list.EnumerateArray())
        {
            if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() == 2)
                points.Add(new CurvePoint(Math.Clamp(point[0].GetDouble(), 0, 255), Math.Clamp(point[1].GetDouble(), 0, 255)));
            else if (point.ValueKind == JsonValueKind.Object)
            {
                var each = new ToolArguments(point);
                points.Add(new CurvePoint(Math.Clamp(each.Number("input") ?? each.RequiredNumber("x"), 0, 255),
                                          Math.Clamp(each.Number("output") ?? each.RequiredNumber("y"), 0, 255)));
            }
            else
            {
                throw new ToolException($"'{name}' is a list of [input, output] pairs.");
            }
        }
        List<CurvePoint> sorted = [.. points.OrderBy(point => point.X)];
        var curve = new List<CurvePoint>();
        foreach (CurvePoint point in sorted)
            if (curve.Count == 0 || point.X > curve[^1].X) curve.Add(point);
        if (curve.Count == 0 || curve[0].X > 0) curve.Insert(0, new CurvePoint(0, curve.Count == 0 ? 0 : curve[0].Y));
        if (curve[^1].X < 255) curve.Add(new CurvePoint(255, curve.Count == 1 ? 255 : curve[^1].Y));
        if (curve.Count > 32) throw new ToolException("A curve takes at most 32 points.");
        return new EquatableList<CurvePoint>(curve);
    }

    /// <summary>A layer's pixels run through a filter, kept to the selection, feathered or not.</summary>
    private static ImageLayer Filtered(EditorSession.Open open, ImageLayer layer, PixelBuffer image, string filter, ToolArguments arguments)
    {
        if (filter == "invert")
        {
            PixelBuffer inverted = PixelFilters.Copy(image);
            for (int y = 0; y < inverted.Height; y++)
            {
                Span<byte> row = inverted.Row(y);
                for (int x = 0; x < inverted.Width; x++)
                {
                    // Premultiplied, the inverse of c is a − c: straight 1 − c, multiplied by a again.
                    byte alpha = row[x * 4 + 3];
                    for (int c = 0; c < 3; c++) row[x * 4 + c] = (byte)(alpha - row[x * 4 + c]);
                }
            }
            return Confined(open, layer, image, inverted);
        }

        FilterKind kind;
        FilterSettings settings = new() { Seed = (uint)Random.Shared.Next() };
        if (AdjustmentKinds.Contains(filter))
        {
            LayerAdjustment adjustment = Adjustment(filter, arguments, start: null);
            if (!adjustment.IsValid) throw new ToolException("Those adjustment settings are out of range.");
            kind = FilterKind.Adjustment;
            settings = settings with { Adjustment = adjustment };
        }
        else
        {
            kind = filter switch
            {
                "gaussian_blur" => FilterKind.GaussianBlur,
                "motion_blur" => FilterKind.MotionBlur,
                "add_noise" => FilterKind.AddNoise,
                "lens_correction" => FilterKind.LensCorrection,
                "pinch" => FilterKind.Pinch,
                "spherize" => FilterKind.Spherize,
                "twirl" => FilterKind.Twirl,
                "wave" => FilterKind.Wave,
                "polar_coordinates" => FilterKind.PolarCoordinates,
                _ => throw new ToolException("'filter' is " + string.Join(", ", FilterNames.Concat(AdjustmentKinds)) + "."),
            };
            settings = settings with
            {
                Radius = Math.Max(0.1, arguments.Number("radius", 4)),
                Angle = kind == FilterKind.MotionBlur ? arguments.Number("angle", 0) : settings.Angle,
                TwirlAngle = kind == FilterKind.Twirl ? arguments.Number("angle", 50) : settings.TwirlAngle,
                Distance = Math.Max(1, arguments.Number("distance", 10)),
                Amount = Math.Clamp(arguments.Number("amount", 10), 0, 400),
                Gaussian = arguments.Bool("gaussian") ?? false,
                Monochromatic = arguments.Bool("monochromatic") ?? false,
                Distortion = arguments.Number("distortion", 0),
                Strength = arguments.Number("strength", 50),
                Wavelength = arguments.Number("wavelength", 120),
                Amplitude = arguments.Number("amplitude", 10),
                ToPolar = arguments.Bool("to_polar") ?? true,
            };
        }

        // A hard selection goes to the filter, which can grow the layer for a blur and still keep
        // to it. A feathered one is laid over the result instead, on the layer's own grid.
        if (!(open.Feather > 0)) return LayerFilters.Apply(layer, kind, settings, open.Selection);
        PixelBuffer filtered = PixelFilters.Run(image, kind, settings);
        return Confined(open, layer, image, filtered);
    }

    /// <summary>A filtered copy of a layer's pixels kept to the selection. Takes <paramref name="edited"/> over.</summary>
    private static ImageLayer Confined(EditorSession.Open open, ImageLayer layer, PixelBuffer image, PixelBuffer edited)
    {
        if (SelectionOver(open, layer.Transform, image.Width, image.Height) is byte[] levels) PixelFilters.Confine(image, edited, levels);
        return layer with { Image = edited, Shape = null };
    }
}
