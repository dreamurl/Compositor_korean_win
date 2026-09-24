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
            "and light to 'highlights' — a duotone) or grain. 'clip' limits it to the layer directly below.",
            Build(With(Choice("kind", "Which adjustment.", ["levels", "curves", "hue_saturation", "exposure", "gradient_map", "grain"], true),
                       Num("black", "levels: input black point 0–254."), Num("white", "levels: input white point 1–255."),
                       Num("gamma", "levels: midtones 0.1–9.99 (above 1 brightens); exposure: gamma 0.01–9.99."),
                       Num("output_black", "levels: output black 0–255."), Num("output_white", "levels: output white 0–255."),
                       new Property("points", "array", "curves: [[input, output], …] in 0–255, from 0 to 255.", Items: "array"),
                       Num("hue", "hue_saturation: -180–180 (0–360 when colorizing)."), Num("saturation", "hue_saturation: -100–100."),
                       Num("lightness", "hue_saturation: -100–100."), Bool("colorize", "hue_saturation: tint everything one hue."),
                       Num("exposure", "exposure: stops, -20–20."), Num("offset", "exposure: -0.5–0.5."),
                       Colour("shadows", "gradient_map: colour for the darks."), Colour("highlights", "gradient_map: colour for the lights."),
                       Bool("reversed", "gradient_map: swap the ends."), Num("amount", "grain: 0–100."), Num("size", "grain: grain size."),
                       Num("roughness", "grain: 0–100."), Bool("clip", "Clip to the layer directly below."))),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                LayerAdjustment adjustment = Adjustment(arguments);
                if (!adjustment.IsValid) throw new ToolException("Those adjustment settings are out of range.");
                var layer = new ImageLayer
                {
                    Id = Guid.NewGuid(),
                    Name = arguments.Required("kind") switch
                    {
                        "levels" => "Levels",
                        "curves" => "Curves",
                        "hue_saturation" => "Hue/Saturation",
                        "exposure" => "Exposure",
                        "gradient_map" => "Gradient Map",
                        _ => "Grain",
                    },
                    Transform = Canvas(open.Document),
                    Adjustment = adjustment,
                };
                ToolResult added = AddLayer(open, arguments, "Adjustment Layer", layer, "adjustment");
                if (arguments.Bool("clip") == true)
                {
                    _session.Edit(open, "Clipping Mask", document =>
                        LayerCommands.ToggleClipping(document, layer.Id) ?? throw new ToolException("It cannot be clipped to what is below."));
                }
                return added;
            });

        Define("apply_filter",
            "Change a layer's pixels with a filter: gaussian_blur (radius), motion_blur (angle, distance) or add_noise (amount, " +
            "gaussian, monochromatic). This is not live; undo takes it back.",
            Build(LayerArgument, Choice("filter", "Which filter.", ["gaussian_blur", "motion_blur", "add_noise"], true),
                  Num("radius", "gaussian_blur: pixels."), Num("angle", "motion_blur: degrees."), Num("distance", "motion_blur: pixels."),
                  Num("amount", "add_noise: 0–400 percent."), Bool("gaussian", "add_noise: gaussian rather than uniform."),
                  Bool("monochromatic", "add_noise: grey noise."), DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                if (layer.Image is null) throw new ToolException($"'{layer.Name}' has no pixels to filter.");
                FilterKind kind = arguments.Required("filter") switch
                {
                    "gaussian_blur" => FilterKind.GaussianBlur,
                    "motion_blur" => FilterKind.MotionBlur,
                    "add_noise" => FilterKind.AddNoise,
                    _ => throw new ToolException("'filter' is gaussian_blur, motion_blur or add_noise."),
                };
                var settings = new FilterSettings
                {
                    Radius = Math.Max(0.1, arguments.Number("radius", 4)),
                    Angle = arguments.Number("angle", 0),
                    Distance = Math.Max(1, arguments.Number("distance", 10)),
                    Amount = Math.Clamp(arguments.Number("amount", 10), 0, 400),
                    Gaussian = arguments.Bool("gaussian") ?? false,
                    Monochromatic = arguments.Bool("monochromatic") ?? false,
                };
                _session.Edit(open, "Filter", document => document.Replacing(LayerFilters.Apply(layer, kind, settings, selection: null)));
                return ToolResult.Text($"Filtered '{layer.Name}'.");
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
                byte[] encoded = generator.Generate(arguments.Required("prompt"), width, height);

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
                ImageLayer layer = Placed(open.Document, pixels, "Generated", arguments);
                ToolResult added = AddLayer(open, arguments, "Generate Image", layer, "generated image");
                added.Content.Insert(0, ToolContent.Of($"Generated with {generator.Name}.{saved}"));
                return added;
            });
    }

    private static bool Removed(ToolArguments arguments, string name) =>
        arguments.Raw(name) is { ValueKind: JsonValueKind.False };

    private static double? Unit(double? value) => value is double v ? Math.Clamp(v > 1 ? v / 100 : v, 0, 1) : null;

    private static LayerAdjustment Adjustment(ToolArguments arguments)
    {
        switch (arguments.Required("kind"))
        {
            case "levels":
            {
                var range = new LevelRange
                {
                    Black = arguments.Number("black", 0),
                    White = arguments.Number("white", 255),
                    Gamma = arguments.Number("gamma", 1),
                    OutputBlack = arguments.Number("output_black", 0),
                    OutputWhite = arguments.Number("output_white", 255),
                }.Normalized;
                return new LayerAdjustment(AdjustmentKind.Levels)
                {
                    Levels = new LevelsSettings { Ranges = new EquatableList<LevelRange>([range, new(), new(), new()]) },
                };
            }
            case "curves":
            {
                var points = new List<CurvePoint>();
                if (arguments.Raw("points") is { ValueKind: JsonValueKind.Array } list)
                {
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
                    }
                }
                List<CurvePoint> sorted = [.. points.OrderBy(point => point.X)];
                var curve = new List<CurvePoint>();
                foreach (CurvePoint point in sorted)
                    if (curve.Count == 0 || point.X > curve[^1].X) curve.Add(point);
                if (curve.Count == 0 || curve[0].X > 0) curve.Insert(0, new CurvePoint(0, curve.Count == 0 ? 0 : curve[0].Y));
                if (curve[^1].X < 255) curve.Add(new CurvePoint(255, curve.Count == 1 ? 255 : curve[^1].Y));
                if (curve.Count > 32) throw new ToolException("A curve takes at most 32 points.");
                var identity = new EquatableList<CurvePoint>([new CurvePoint(0, 0), new CurvePoint(255, 255)]);
                return new LayerAdjustment(AdjustmentKind.Curves)
                {
                    Curves = new CurvesSettings
                    {
                        Channels = new EquatableList<EquatableList<CurvePoint>>([new EquatableList<CurvePoint>(curve), identity, identity, identity]),
                    },
                };
            }
            case "hue_saturation":
            {
                bool colorize = arguments.Bool("colorize") ?? false;
                double hue = arguments.Number("hue", 0), saturation = arguments.Number("saturation", colorize ? 25 : 0);
                double lightness = arguments.Number("lightness", 0);
                hue = colorize ? ((hue % 360) + 360) % 360 : Math.Clamp(hue, -180, 180);
                return new LayerAdjustment(AdjustmentKind.Hsv)
                {
                    HsvSettings = HueSaturationSettings.From(hue, Math.Clamp(saturation, colorize ? 0 : -100, 100), Math.Clamp(lightness, -100, 100), colorize),
                };
            }
            case "exposure":
                return new LayerAdjustment(AdjustmentKind.Exposure)
                {
                    ExposureSettings = new ExposureSettings
                    {
                        Exposure = arguments.Number("exposure", 0),
                        Offset = arguments.Number("offset", 0),
                        Gamma = arguments.Number("gamma", 1),
                    }.Normalized,
                };
            case "gradient_map":
            {
                Rgba dark = arguments.Colour("shadows") ?? Rgba.Black, light = arguments.Colour("highlights") ?? Rgba.White;
                return new LayerAdjustment(AdjustmentKind.GradientMap)
                {
                    GradientMapSettings = new GradientMapSettings
                    {
                        Shadows = new AdjustmentColor(dark.R / 255.0, dark.G / 255.0, dark.B / 255.0),
                        Highlights = new AdjustmentColor(light.R / 255.0, light.G / 255.0, light.B / 255.0),
                        Reversed = arguments.Bool("reversed") ?? false,
                    },
                };
            }
            case "grain":
                return new LayerAdjustment(AdjustmentKind.Grain)
                {
                    GrainSettings = new GrainSettings
                    {
                        Amount = Math.Clamp(arguments.Number("amount", 25), 0, 100),
                        Size = Math.Max(0.1, arguments.Number("size", 1.5)),
                        Roughness = Math.Clamp(arguments.Number("roughness", 50), 0, 100),
                    },
                };
            default:
                throw new ToolException("'kind' is levels, curves, hue_saturation, exposure, gradient_map or grain.");
        }
    }
}
