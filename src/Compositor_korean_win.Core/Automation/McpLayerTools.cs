using static Compositor_korean_win.Core.ToolSchema;

namespace Compositor_korean_win.Core;

public sealed partial class McpTools
{
    private static readonly Property LayerArgument = Str("layer", "The layer's id (from get_document) or its name.", true);

    // A property, not a field: it reads BlendNames from the other part of this class, and static
    // fields in different parts of a partial class initialise in no guaranteed order.
    private static Property[] Placing =>
    [
        Str("above", "Put the new layer directly above this layer (id or name). Default: top of the document."),
        Str("into", "Put the new layer at the top inside this group (id or name)."),
        Num("opacity", "0–1. Default 1."),
        Choice("blend_mode", "Default normal.", BlendNames),
        Str("name", "The new layer's name."),
    ];

    private static Property[] With(params Property[] first) => [.. first, .. Placing, DocumentArgument];

    /// <summary>Adds a layer where the arguments say, as one undo step, and answers with its id.</summary>
    private ToolResult AddLayer(EditorSession.Open open, ToolArguments arguments, string step, ImageLayer layer, string what)
    {
        layer = layer with
        {
            Opacity = Opacity(arguments) ?? layer.Opacity,
            BlendMode = arguments.String("blend_mode") is string blend ? Blend(blend) : layer.BlendMode,
            Name = arguments.String("name") is { Length: > 0 } name ? name : layer.Name,
        };

        _session.Edit(open, step, document => Insert(document, layer, arguments));
        open.Active = layer.Id;
        ImageLayer placed = open.Document.Layer(layer.Id)!;
        string where = placed.Image is null ? "" : $" at {Bounds(placed)}";
        return ToolResult.Text($"Added {what} '{placed.Name}' {placed.Id}{where}.");
    }

    private static string Bounds(ImageLayer layer)
    {
        PixelRect box = LayerGeometry.Bounds(layer.Transform);
        return $"x {box.X}, y {box.Y}, {box.Width}×{box.Height}";
    }

    private static double? Opacity(ToolArguments arguments) =>
        arguments.Number("opacity") is double value ? Math.Clamp(value > 1 ? value / 100 : value, 0, 1) : null;

    /// <summary>A new layer placed above a given layer, inside a given group, or at the top.</summary>
    /// <remarks>
    /// <para>
    /// Siblings are ordered by where they sit in the flat list, and a folder's members need not sit
    /// next to the folder (<see cref="LayerCommands"/>). So "above X" is the entry right after X, and
    /// "into G" is right after G's last member — not G's index plus how many members it has, which
    /// lands wherever the list happens to continue and can put the layer under the members it was
    /// meant to cover.
    /// </para>
    /// <para>
    /// A layer put between a clipping base and the layers clipped to it joins them, as Photoshop
    /// does. Left unclipped it would split the group: the layers above it would no longer sit on
    /// their base and would draw by themselves, restricted to it — text clipped to a shape would
    /// show only partly, or not at all.
    /// </para>
    /// </remarks>
    private static CanvasDocument Insert(CanvasDocument document, ImageLayer layer, ToolArguments arguments) =>
        InsertAt(document, layer, arguments.String("into"), arguments.String("above"));

    private static CanvasDocument InsertAt(CanvasDocument document, ImageLayer layer, string? into, string? above)
    {
        List<ImageLayer> layers = [.. document.Layers];
        int at;
        Guid? parent;
        if (into is string group)
        {
            ImageLayer folder = EditorSession.Layer(document, group);
            if (!folder.IsGroup) throw new ToolException($"'{folder.Name}' is not a group.");
            int last = layers.FindLastIndex(each => each.ParentId == folder.Id);
            at = Math.Max(last, document.IndexOf(folder.Id)) + 1;
            parent = folder.Id;
        }
        else if (above is string reference)
        {
            ImageLayer below = EditorSession.Layer(document, reference);
            at = document.IndexOf(below.Id) + 1;
            parent = below.ParentId;
        }
        else
        {
            at = layers.Count;
            parent = null;
        }

        ImageLayer placed = layer with { ParentId = parent };
        if (!placed.IsGroup && layers.Skip(at).FirstOrDefault(each => each.ParentId == parent) is { MaskSourceId: Guid joined })
            placed = placed with { MaskSourceId = joined };
        layers.Insert(at, placed);
        LayerCommands.ReleaseDetachedClipping(layers);
        return document with { Layers = layers.ToEquatableList() };
    }

    private static LayerTransform Canvas(CanvasDocument document) => new(Point.Zero, new Size(document.Width, document.Height));

    private void DefineLayerTools()
    {
        Define("add_layer", "Add an empty layer.", Build(With()),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                var layer = new ImageLayer
                {
                    Id = Guid.NewGuid(),
                    Name = LayerCommands.NextName(open.Document, TextKey.LayerNameNumbered),
                    Transform = Canvas(open.Document),
                };
                return AddLayer(open, arguments, "New Layer", layer, "empty layer");
            });

        Define("add_image",
            "Place a picture (PNG, JPEG…, from a file or base64) as a new layer. By default it keeps its own size, " +
            "centred; 'fit' sizes it to the canvas, or give x/y/width/height (one of width/height keeps the aspect).",
            Build(With(Str("path", "Image file to place."), Str("data", "The image as base64, instead of 'path'."),
                       Choice("fit", "Size to the canvas: contain (whole image visible), cover (fills, may crop), fill (stretch).",
                              ["none", "contain", "cover", "fill"]),
                       Num("x", "Left edge."), Num("y", "Top edge."), Num("width", "Width on the canvas."),
                       Num("height", "Height on the canvas."), Num("rotation", "Degrees clockwise."))),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                (PixelBuffer pixels, string name) = Picture(arguments);
                return AddLayer(open, arguments, "Place Image", Placed(open.Document, pixels, name, arguments), "image");
            });

        Define("add_text",
            "Add a live text layer. (x, y) is where the first line's baseline starts — or its middle or end for " +
            "align center/right — as in Photoshop's point text. Lines break at \\n (\\\\ is a backslash). The answer gives the text's bounds.",
            Build(With(Str("text", "The words; \\n starts a new line.", true), Num("x", "Anchor x.", true), Num("y", "Anchor y (the first baseline).", true),
                       Str("font", "Font family, e.g. \"Malgun Gothic\", \"Arial\" (see list_fonts). Default Malgun Gothic."),
                       Num("size", "Size in pixels. Default 72."), Int("weight", "100–900; 400 regular, 700 bold."),
                       Bool("bold", "Shorthand for weight 700."), Bool("italic", "Italic."),
                       Colour("color", "Text colour. Default black."), Choice("align", "Default left.", ["left", "center", "right"]),
                       Num("tracking", "Letter spacing in 1/1000 em, like Photoshop's tracking (e.g. 100, -50)."),
                       Num("leading", "Line spacing as a multiple of the size. Default 1.2."),
                       WarpArgument, Num("rotation", "Degrees clockwise, e.g. -90 to run upwards."))),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                IGlyphSource glyphs = Glyphs();
                string words = arguments.Words("text") is { Length: > 0 } given ? given : throw new ToolException("'text' is required.");
                LayerText recipe = TextStyle(new LayerText { Font = "Malgun Gothic", Size = 72 }, arguments) with { Text = words };
                var blank = new ImageLayer { Id = Guid.NewGuid(), Name = FirstLine(recipe.Text), Transform = Canvas(open.Document) };
                ImageLayer layer = TextPlacement.Set(blank, recipe, glyphs,
                                                     new Point(arguments.RequiredNumber("x"), arguments.RequiredNumber("y")));
                if (arguments.Number("rotation") is double rotation)
                    layer = layer with { Transform = layer.Transform with { Rotation = rotation } };
                return AddLayer(open, arguments, "Add Text", layer, "text");
            });

        Define("edit_text",
            "Change a text layer's words or style. Anything left out stays as it is; the anchor stays put unless x/y are given. " +
            "Give start/end (character indices into the words, end exclusive) to set font, size, weight, italic or color on " +
            "those letters only, as selecting them in Photoshop does.",
            Build(LayerArgument, Str("text", "New words."), Num("x", "New anchor x."), Num("y", "New anchor y."),
                  Str("font", "Font family."), Num("size", "Size in pixels."), Int("weight", "100–900."), Bool("bold", "Weight 700 or 400."),
                  Bool("italic", "Italic."), Colour("color", "Text colour."), Choice("align", "Alignment.", ["left", "center", "right"]),
                  Num("tracking", "Letter spacing in 1/1000 em."), Num("leading", "Line spacing multiple."), WarpArgument,
                  Int("start", "First letter to restyle, 0-based."), Int("end", "Letter after the last to restyle."), DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                IGlyphSource glyphs = Glyphs();
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                if (!layer.IsLiveText || layer.Text is not LayerText current)
                    throw new ToolException($"'{layer.Name}' is not live text (it may have been painted on or rasterized).");
                // New words first, so a range counts letters of the words being set.
                LayerText recipe = current;
                if (arguments.Words("text") is string words) recipe = TextRuns.Retype(recipe, words);
                if (arguments.Has("start") != arguments.Has("end"))
                    throw new ToolException("Give both start and end to restyle a range of letters.");
                int start = arguments.Int("start") ?? 0, end = arguments.Int("end") ?? 0;
                if (start < 0 || end > recipe.Text.Length || start > end || (arguments.Has("start") && start == end))
                    throw new ToolException($"start/end must pick letters within 0–{recipe.Text.Length}.");
                recipe = TextRuns.Restyle(recipe, start, end, style => TextStyle(style, arguments));
                Point? anchor = arguments.Number("x") is double x && arguments.Number("y") is double y ? new Point(x, y) : null;
                if (anchor is null && (arguments.Has("x") || arguments.Has("y")))
                    throw new ToolException("Give both x and y to move the text's anchor.");
                ImageLayer set = TextPlacement.Set(layer, recipe, glyphs, anchor);
                _session.Edit(open, "Edit Text", document => document.Replacing(set));
                return ToolResult.Text($"Set '{set.Name}' {set.Id} at {Bounds(set)}.");
            });

        Define("add_shape",
            "Draw a shape on a new layer: rectangle (optionally rounded), ellipse, polygon, star (points, inset, curved for a " +
            "four-point sparkle) or line. Give x/y/width/height for the box — for a line, x1/y1/x2/y2.",
            Build(With(Choice("kind", "What to draw.", ["rectangle", "ellipse", "polygon", "star", "line"], true),
                       Num("x", "Box left."), Num("y", "Box top."), Num("width", "Box width."), Num("height", "Box height."),
                       Num("x1", "Line start x."), Num("y1", "Line start y."), Num("x2", "Line end x."), Num("y2", "Line end y."),
                       Colour("fill", "Inside colour, or \"none\" for an outline only. Default black."),
                       Colour("stroke", "Outline colour. Default none."), Num("stroke_width", "Outline width. Default 0."),
                       Colour("color", "A line's colour. Default black."), Num("line_width", "A line's thickness. Default 4."),
                       Num("corner_radius", "Rounded rectangle corners."), Int("sides", "Polygon sides or star points, 3–64. Default 5."),
                       Num("inset", "Star inner radius as a fraction of the outer, 0.05–0.95. Default 0.5."),
                       Bool("curved", "Star sides curve inwards (a sparkle)."), Num("rotation", "Degrees clockwise."))),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                return AddLayer(open, arguments, "Shape", Shape(open.Document, arguments), "shape");
            });

        Define("add_gradient",
            "Fill a new canvas-sized layer with a gradient from 'start' to 'end'. Clip it to a shape or text with set_clipping " +
            "to colour that shape.",
            Build(With(Choice("kind", "Default linear.", ["linear", "radial"]),
                       PointOf("start", "Where 'from' is fully applied (a radial gradient's centre).", true),
                       PointOf("end", "Where 'to' is reached.", true),
                       Colour("from", "Start colour. Default black."), Colour("to", "End colour; \"transparent\" fades out. Default white."))),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                Rgba first = arguments.Colour("from") ?? Rgba.Black, last = arguments.Colour("to") ?? Rgba.White;
                var settings = new GradientSettings
                {
                    Kind = arguments.String("kind") == "radial" ? GradientKind.Radial : GradientKind.Linear,
                    From = first,
                    To = last.A == 0 ? new Rgba(first.R, first.G, first.B, 0) : last,
                    Style = last.A == 0 ? GradientStyle.ForegroundToTransparent : GradientStyle.ForegroundToBackground,
                };
                PixelBuffer pixels = GradientTool.Draw(null, open.Document.Width, open.Document.Height,
                    arguments.Position("start") ?? throw new ToolException("'start' is required."),
                    arguments.Position("end") ?? throw new ToolException("'end' is required."), settings);
                var layer = new ImageLayer { Id = Guid.NewGuid(), Name = "Gradient", Transform = Canvas(open.Document), Image = pixels };
                return AddLayer(open, arguments, "Gradient", layer, "gradient");
            });

        Define("update_layer",
            "Change a layer's name, visibility, opacity, blend mode or placement (x/y is the top-left before rotation; " +
            "width/height stretch; scale multiplies the size about the centre; rotation in degrees clockwise). Moving a " +
            "group moves everything in it.",
            Build(LayerArgument, Str("name", "New name."), Bool("visible", "Show or hide."), Num("opacity", "0–1."),
                  Choice("blend_mode", "Blend mode.", BlendNames), Num("x", "Left."), Num("y", "Top."), Num("width", "Width."),
                  Num("height", "Height."), Num("scale", "Multiply the size, about the centre."), Num("rotation", "Degrees clockwise."),
                  Bool("flip_x", "Mirror left to right."), Bool("flip_y", "Mirror top to bottom."), DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                _session.Edit(open, "Layer Properties", document => Updated(document, layer, arguments));
                ImageLayer after = open.Document.Layer(layer.Id)!;
                return ToolResult.Text($"Updated '{after.Name}' {after.Id}{(after.Image is null ? "" : " at " + Bounds(after))}.");
            });

        Define("arrange_layer",
            "Move a layer in the stack: to the top or bottom of its group, one step up or down, directly above or below " +
            "another layer, into a group, or out of its group.",
            Build(LayerArgument, Choice("to", "Where to move it.", ["top", "bottom", "up", "down", "out_of_group"]),
                  Str("above", "Directly above this layer."), Str("below", "Directly below this layer."),
                  Str("into", "Into this group."), DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                _session.Edit(open, "Arrange", document => Arranged(document, layer, arguments));
                return Described(open, $"Moved '{layer.Name}'.");
            });

        Define("delete_layers", "Delete layers (a group goes with everything in it).",
            Build(Strings("layers", "Ids or names.", true), DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                List<Guid> ids = [.. (arguments.Strings("layers") ?? []).Select(reference => EditorSession.Layer(open.Document, reference).Id)];
                if (ids.Count == 0) throw new ToolException("'layers' is empty.");
                _session.Edit(open, "Delete Layer", document =>
                    LayerCommands.Delete(document, ids) ?? throw new ToolException("Those layers could not be deleted."));
                return ToolResult.Text($"Deleted {ids.Count} layer{(ids.Count == 1 ? "" : "s")}.");
            });

        Define("duplicate_layer", "Copy a layer (not a group), placing the copy directly above it.",
            Build(LayerArgument, Str("name", "The copy's name."), DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                Guid copy = Guid.Empty;
                _session.Edit(open, "Duplicate Layer", document =>
                {
                    (CanvasDocument next, IReadOnlyList<Guid> copies) = LayerCommands.CopyTo(document, [layer.Id], layer.Id, LayerDrop.Above)
                        ?? throw new ToolException("Groups cannot be duplicated here; duplicate their layers.");
                    copy = copies[0];
                    return arguments.String("name") is { Length: > 0 } name ? LayerCommands.Rename(next, copy, name) ?? next : next;
                });
                open.Active = copy;
                return ToolResult.Text($"Duplicated '{layer.Name}' as {copy}.");
            });

        Define("group_layers", "Put layers into a new group.",
            Build(Strings("layers", "Ids or names.", true), Str("name", "The group's name."), DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                List<Guid> ids = [.. (arguments.Strings("layers") ?? []).Select(reference => EditorSession.Layer(open.Document, reference).Id)];
                Guid folder = Guid.Empty;
                _session.Edit(open, "Group Layers", document =>
                {
                    (CanvasDocument next, Guid made) = LayerCommands.Group(document, ids) ?? throw new ToolException("Those layers could not be grouped.");
                    folder = made;
                    return arguments.String("name") is { Length: > 0 } name ? LayerCommands.Rename(next, made, name) ?? next : next;
                });
                open.Active = folder;
                return Described(open, $"Grouped into {folder}.");
            });

        Define("ungroup", "Take a group's layers out of it and remove the group (its mask goes with it).",
            Build(LayerArgument, DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer folder = EditorSession.Layer(open.Document, arguments.String("layer"));
                if (!folder.IsGroup) throw new ToolException($"'{folder.Name}' is not a group.");
                _session.Edit(open, "Ungroup", document =>
                {
                    List<ImageLayer> layers = [.. document.Layers
                        .Where(each => each.Id != folder.Id)
                        .Select(each => each.ParentId == folder.Id ? each with { ParentId = folder.ParentId } : each)];
                    LayerCommands.ReleaseDetachedClipping(layers);
                    return document with { Layers = layers.ToEquatableList() };
                });
                return Described(open, $"Ungrouped '{folder.Name}'.");
            });

        Define("merge_layers", "Merge layers into one pixel layer, as they look together.",
            Build(Strings("layers", "Ids or names, two or more.", true), DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                List<Guid> ids = [.. (arguments.Strings("layers") ?? []).Select(reference => EditorSession.Layer(open.Document, reference).Id)];
                Guid merged = Guid.Empty;
                _session.Edit(open, "Merge Layers", document =>
                {
                    LayerCommands.MergePlan plan = LayerCommands.PlanMerge(document, ids, ids.LastOrDefault()) ?? throw new ToolException("Those layers cannot be merged.");
                    (CanvasDocument next, Guid layer) = LayerCommands.Merge(document, plan) ?? throw new ToolException("Those layers cannot be merged.");
                    merged = layer;
                    return next;
                });
                open.Active = merged;
                return ToolResult.Text($"Merged into {merged}.");
            });

        Define("rasterize_layer", "Make a text layer plain pixels (its words can no longer be edited).",
            Build(LayerArgument, DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                _session.Edit(open, "Rasterize", document => document.Replacing(layer with { Text = null }));
                return ToolResult.Text($"'{layer.Name}' is pixels now.");
            });

        Define("set_clipping",
            "Clip a layer to the layer directly below it (its base), so it only shows where the base has pixels — e.g. a " +
            "gradient or photo clipped to text. clipped=false releases it.",
            Build(LayerArgument, Bool("clipped", "Default true."), DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                bool wanted = arguments.Bool("clipped") ?? true;
                if (LayerCommands.IsClipped(open.Document, layer.Id) != wanted)
                {
                    _session.Edit(open, "Clipping Mask", document =>
                        LayerCommands.ToggleClipping(document, layer.Id)
                        ?? throw new ToolException("It cannot be clipped: the layer directly below must be a layer, not a group."));
                }
                ImageLayer after = open.Document.Layer(layer.Id)!;
                string baseName = after.MaskSourceId is Guid source ? open.Document.Layer(source)?.Name ?? source.ToString() : "";
                return ToolResult.Text(after.MaskSourceId is null ? $"'{after.Name}' is not clipped." : $"'{after.Name}' is clipped to '{baseName}'.");
            });

        Define("set_mask",
            "Give a layer (or group or adjustment) a mask: reveal_all, hide_all, a rectangle or ellipse that shows " +
            "(invert to hide it), a linear/radial gradient fading from shown at 'start' to hidden at 'end', the current " +
            "selection (feathered as it is), or an image — its lightness, or its alpha with channel alpha — stretched over " +
            "the layer or placed at x/y/width/height. shape none removes the mask; 'enabled' switches it off and on; " +
            "'feather' softens the mask's edges; invert alone flips the mask the layer has. To paint a mask by hand, use " +
            "paint_stroke with mask true.",
            Build(LayerArgument,
                  Choice("shape", "The mask.", ["reveal_all", "hide_all", "rectangle", "ellipse", "linear_gradient", "radial_gradient",
                                                "selection", "image", "none"]),
                  Num("x", "Rectangle/ellipse/image left."), Num("y", "Top."), Num("width", "Width."), Num("height", "Height."),
                  PointOf("start", "Gradient: where the layer is fully shown (a radial gradient's centre)."),
                  PointOf("end", "Gradient: where it is fully hidden."), Bool("invert", "Swap shown and hidden."),
                  Str("path", "image: the picture file."), Str("data", "image: the picture as base64, instead of 'path'."),
                  Choice("channel", "image: what of the picture becomes the mask. Default luminance.", ["luminance", "alpha"]),
                  Num("feather", "Blur the mask's edges by this many pixels."),
                  Bool("enabled", "Switch the mask on or off."), DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                _session.Edit(open, "Layer Mask", document => document.Replacing(Masked(open, document, layer, arguments)));
                ImageLayer after = open.Document.Layer(layer.Id)!;
                return ToolResult.Text(after.Mask is null ? $"'{after.Name}' has no mask." : $"'{after.Name}' mask set{(after.Mask.IsEnabled ? "" : " (off)")}.");
            });
    }

    private static readonly Property WarpArgument = Object("warp", "Bend the text, as Photoshop's Warp Text.",
        Choice("style", "The warp.", ["none", "arc", "arc_lower", "arc_upper", "arch", "bulge", "shell_lower", "shell_upper",
                                      "flag", "wave", "fish", "rise", "fisheye", "inflate", "squeeze", "twist"], true),
        Num("bend", "-100–100. Default 50."), Num("horizontal", "Horizontal distortion -100–100."),
        Num("vertical", "Vertical distortion -100–100."));

    private IGlyphSource Glyphs() =>
        _session.Services.Glyphs ?? throw new ToolException("Text needs fonts, which this build of the server has none of.");

    private static string FirstLine(string words)
    {
        string first = words.Replace("\r", "").Split('\n')[0].Trim();
        if (first.Length > 40) first = first[..40];
        return first.Length > 0 ? first : "Text";
    }

    /// <summary>A text recipe with whatever style the arguments give.</summary>
    private static LayerText TextStyle(LayerText recipe, ToolArguments arguments)
    {
        if (arguments.String("font") is { Length: > 0 } font) recipe = recipe with { Font = font };
        if (arguments.Number("size") is double size) recipe = recipe with { Size = size };
        if (arguments.Bool("bold") is bool bold) recipe = recipe with { Weight = bold ? 700 : 400 };
        if (arguments.Int("weight") is int weight) recipe = recipe with { Weight = Math.Clamp(weight, 1, 999) };
        if (arguments.Bool("italic") is bool italic) recipe = recipe with { Italic = italic };
        if (arguments.Colour("color") is Rgba colour) recipe = recipe.WithColour(colour);
        if (arguments.String("align") is string align)
        {
            recipe = recipe with
            {
                Align = align.ToLowerInvariant() switch
                {
                    "center" or "centre" or "middle" => TextAlign.Center,
                    "right" => TextAlign.Right,
                    "left" => TextAlign.Left,
                    _ => throw new ToolException("'align' is left, center or right."),
                },
            };
        }
        if (arguments.Number("tracking") is double tracking) recipe = recipe with { Tracking = tracking };
        if (arguments.Number("leading") is double leading) recipe = recipe with { Leading = leading };

        if (arguments.Raw("warp") is { ValueKind: System.Text.Json.JsonValueKind.String } plain && plain.GetString() is "none" or "")
        {
            recipe = recipe with { Warp = null };
        }
        else if (arguments.Object("warp") is ToolArguments warp)
        {
            string style = (warp.String("style") ?? "none").Replace("_", "").Replace(" ", "").Replace("-", "");
            if (!Enum.TryParse(style, ignoreCase: true, out TextWarpStyle parsed))
                throw new ToolException($"'{warp.String("style")}' is not a warp style.");
            recipe = recipe with
            {
                Warp = parsed == TextWarpStyle.None ? null : new TextWarp
                {
                    Style = parsed,
                    Bend = Math.Clamp(warp.Number("bend", 50), -100, 100),
                    Horizontal = Math.Clamp(warp.Number("horizontal", 0), -100, 100),
                    Vertical = Math.Clamp(warp.Number("vertical", 0), -100, 100),
                },
            };
        }

        if (!recipe.IsValid) throw new ToolException("Those text settings are out of range (size 1–5000, weight 1–999, leading above 0).");
        return recipe;
    }

    private (PixelBuffer, string) Picture(ToolArguments arguments)
    {
        if (arguments.String("path") is string path)
        {
            if (!File.Exists(path)) throw new ToolException($"No file at {path}.");
            return (_session.Services.DecodeImage(File.ReadAllBytes(path)), Path.GetFileNameWithoutExtension(path));
        }
        if (arguments.String("data") is string data)
        {
            int comma = data.IndexOf(',');
            if (data.StartsWith("data:", StringComparison.Ordinal) && comma > 0) data = data[(comma + 1)..];
            byte[] bytes;
            try { bytes = Convert.FromBase64String(data); }
            catch (FormatException) { throw new ToolException("'data' is not base64."); }
            return (_session.Services.DecodeImage(bytes), "Image");
        }
        throw new ToolException("Give 'path' or 'data'.");
    }

    /// <summary>A picture's layer, sized and placed as the arguments ask.</summary>
    /// <param name="sized">
    /// Whether 'width' and 'height' place the picture. generate_image uses them for the size of the
    /// picture it asks for, so its placement comes from 'fit' and x/y only.
    /// </param>
    private static ImageLayer Placed(CanvasDocument document, PixelBuffer pixels, string name, ToolArguments arguments, bool sized = true)
    {
        double width = pixels.Width, height = pixels.Height;
        string fit = arguments.String("fit") ?? "none";
        if (fit != "none")
        {
            double across = (double)document.Width / width, down = (double)document.Height / height;
            (width, height) = fit switch
            {
                "contain" => (width * Math.Min(across, down), height * Math.Min(across, down)),
                "cover" => (width * Math.Max(across, down), height * Math.Max(across, down)),
                "fill" => (document.Width, (double)document.Height),
                _ => throw new ToolException("'fit' is none, contain, cover or fill."),
            };
        }

        double? givenWidth = sized ? arguments.Number("width") : null, givenHeight = sized ? arguments.Number("height") : null;
        if (givenWidth is double w && givenHeight is double h) (width, height) = (w, h);
        else if (givenWidth is double onlyWidth) (width, height) = (onlyWidth, height * onlyWidth / width);
        else if (givenHeight is double onlyHeight) (width, height) = (width * onlyHeight / height, onlyHeight);
        if (width < 1 || height < 1) throw new ToolException("The picture would be smaller than a pixel.");

        double x = arguments.Number("x") ?? (document.Width - width) / 2, y = arguments.Number("y") ?? (document.Height - height) / 2;
        return new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = name,
            Image = pixels,
            Transform = new LayerTransform(new Point(x, y), new Size(width, height)) { Rotation = arguments.Number("rotation", 0) },
        };
    }

    /// <summary>A shape drawn on a layer just large enough for it.</summary>
    private static ImageLayer Shape(CanvasDocument document, ToolArguments arguments)
    {
        string kind = arguments.Required("kind").ToLowerInvariant();
        ShapeKind shapeKind = kind switch
        {
            "rectangle" or "rect" => ShapeKind.Rectangle,
            "ellipse" or "circle" => ShapeKind.Ellipse,
            "polygon" => ShapeKind.Polygon,
            "star" => ShapeKind.Star,
            "line" => ShapeKind.Line,
            _ => throw new ToolException("'kind' is rectangle, ellipse, polygon, star or line."),
        };

        Point corner, opposite;
        if (shapeKind == ShapeKind.Line)
        {
            corner = new Point(arguments.RequiredNumber("x1"), arguments.RequiredNumber("y1"));
            opposite = new Point(arguments.RequiredNumber("x2"), arguments.RequiredNumber("y2"));
        }
        else
        {
            double x = arguments.RequiredNumber("x"), y = arguments.RequiredNumber("y");
            double width = arguments.RequiredNumber("width"), height = arguments.RequiredNumber("height");
            if (width <= 0 || height <= 0) throw new ToolException("'width' and 'height' must be above 0.");
            corner = new Point(x, y);
            opposite = new Point(x + width, y + height);
        }

        Rgba? fill = arguments.Colour("fill");
        Rgba? stroke = arguments.Colour("stroke");
        double strokeWidth = Math.Max(0, arguments.Number("stroke_width") ?? (stroke is { A: > 0 } ? 2 : 0));
        double lineWidth = Math.Max(0.5, arguments.Number("line_width", 4));
        Rgba colour = shapeKind == ShapeKind.Line
            ? arguments.Colour("color") ?? stroke ?? fill ?? Rgba.Black
            : fill ?? Rgba.Black;
        if (colour.A == 0 && shapeKind != ShapeKind.Line) colour = Rgba.Black;
        var settings = new ShapeSettings
        {
            Kind = shapeKind,
            Color = colour,
            Filled = shapeKind == ShapeKind.Line || fill is not { A: 0 },
            StrokeWidth = shapeKind == ShapeKind.Line || stroke is not { A: > 0 } ? 0 : strokeWidth,
            StrokeColor = stroke ?? Rgba.Black,
            CornerRadius = Math.Max(0, arguments.Number("corner_radius", 0)),
            Sides = Math.Clamp(arguments.Int("sides") ?? 5, ShapeSettings.MinimumSides, ShapeSettings.MaximumSides),
            Inset = Math.Clamp(arguments.Number("inset", 0.5), 0.05, 0.95),
            Curved = arguments.Bool("curved") ?? false,
            LineWidth = lineWidth,
        };
        if (!settings.Filled && settings.StrokeWidth <= 0) throw new ToolException("With fill \"none\" give a 'stroke' colour and 'stroke_width', or nothing is drawn.");

        // The layer covers the shape and its outline with a little room, not the whole canvas.
        double reach = Math.Max(settings.StrokeWidth, shapeKind == ShapeKind.Line ? lineWidth : 0) / 2 + 2;
        int left = (int)Math.Floor(Math.Min(corner.X, opposite.X) - reach), top = (int)Math.Floor(Math.Min(corner.Y, opposite.Y) - reach);
        int right = (int)Math.Ceiling(Math.Max(corner.X, opposite.X) + reach), bottom = (int)Math.Ceiling(Math.Max(corner.Y, opposite.Y) + reach);
        int w = right - left, h = bottom - top;
        if (w > ProjectLimits.MaximumSide || h > ProjectLimits.MaximumSide) throw new ToolException("That shape is too large.");

        var shift = new Point(-left, -top);
        PixelBuffer pixels = ShapeTool.Draw(null, w, h, new Point(corner.X + shift.X, corner.Y + shift.Y),
                                            new Point(opposite.X + shift.X, opposite.Y + shift.Y), settings);
        return new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = char.ToUpperInvariant(kind[0]) + kind[1..],
            Image = pixels,
            Transform = new LayerTransform(new Point(left, top), new Size(w, h)) { Rotation = arguments.Number("rotation", 0) },
        };
    }

    private static CanvasDocument Updated(CanvasDocument document, ImageLayer layer, ToolArguments arguments)
    {
        ImageLayer next = layer;
        if (arguments.String("name") is { Length: > 0 } name) next = next with { Name = name };
        if (arguments.Bool("visible") is bool visible) next = next with { IsVisible = visible };
        if (Opacity(arguments) is double opacity)
        {
            if (layer.IsGroup && opacity < 1) throw new ToolException("A group's opacity stays 1 here; set its layers' opacity instead.");
            next = next with { Opacity = opacity };
        }
        if (arguments.String("blend_mode") is string blend)
        {
            LayerBlendMode mode = Blend(blend);
            if (layer.IsGroup && mode != LayerBlendMode.Normal) throw new ToolException("Groups pass their layers' blending through; set it on the layers.");
            next = next with { BlendMode = mode };
        }

        bool moves = new[] { "x", "y", "width", "height", "scale", "rotation", "flip_x", "flip_y" }.Any(arguments.Has);
        if (!moves) return document.Replacing(next);

        if (layer.IsGroup)
        {
            if (new[] { "width", "height", "scale", "rotation", "flip_x", "flip_y" }.Any(arguments.Has))
                throw new ToolException("A group can only be moved (x/y); resize its layers instead.");
            PixelRect box = GroupBounds(document, layer) ?? throw new ToolException("The group is empty.");
            double dx = (arguments.Number("x") ?? box.X) - box.X, dy = (arguments.Number("y") ?? box.Y) - box.Y;
            HashSet<Guid> inside = LayerCommands.Descendants(document, layer.Id);
            CanvasDocument moved = document.Replacing(next);
            foreach (Guid id in inside)
            {
                ImageLayer member = moved.Layer(id)!;
                if (member.IsGroup || member.Adjustment is not null) continue;
                moved = moved.Replacing(member with
                {
                    Transform = member.Transform with { Origin = new Point(member.Transform.Origin.X + dx, member.Transform.Origin.Y + dy) },
                });
            }
            return moved;
        }

        LayerTransform t = next.Transform;
        if (arguments.Number("scale") is double scale)
        {
            if (scale <= 0) throw new ToolException("'scale' must be above 0.");
            Point centre = t.Center;
            var size = new Size(t.Size.Width * scale, t.Size.Height * scale);
            t = t with { Size = size, Origin = new Point(centre.X - size.Width / 2, centre.Y - size.Height / 2) };
        }
        t = t with
        {
            Origin = new Point(arguments.Number("x") ?? t.Origin.X, arguments.Number("y") ?? t.Origin.Y),
            Size = new Size(arguments.Number("width") ?? t.Size.Width, arguments.Number("height") ?? t.Size.Height),
            Rotation = arguments.Number("rotation") ?? t.Rotation,
            FlipX = arguments.Bool("flip_x") ?? t.FlipX,
            FlipY = arguments.Bool("flip_y") ?? t.FlipY,
        };
        if (!t.IsValid) throw new ToolException("That placement is out of range.");

        // A text layer keeps its words: only its placement changed, not the raster the words set.
        return document.Replacing(next with { Transform = t });
    }

    private static PixelRect? GroupBounds(CanvasDocument document, ImageLayer folder)
    {
        PixelRect? box = null;
        foreach (Guid id in LayerCommands.Descendants(document, folder.Id))
        {
            if (document.Layer(id) is not { IsGroup: false, Image: not null } member) continue;
            PixelRect bounds = LayerGeometry.Bounds(member.Transform);
            box = box is PixelRect known ? Union(known, bounds) : bounds;
        }
        return box;

        static PixelRect Union(PixelRect a, PixelRect b)
        {
            int left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
            return new PixelRect(left, top, Math.Max(a.X + a.Width, b.X + b.Width) - left, Math.Max(a.Y + a.Height, b.Y + b.Height) - top);
        }
    }

    private static CanvasDocument Arranged(CanvasDocument document, ImageLayer layer, ToolArguments arguments)
    {
        (string? target, LayerDrop drop) = arguments.String("above") is string above ? (above, LayerDrop.Above)
            : arguments.String("below") is string below ? (below, LayerDrop.Below)
            : arguments.String("into") is string into ? (into, LayerDrop.Into)
            : ((string?)null, LayerDrop.Above);

        if (target is not null)
        {
            ImageLayer other = EditorSession.Layer(document, target);
            if (drop == LayerDrop.Into && !other.IsGroup) throw new ToolException($"'{other.Name}' is not a group.");
            return LayerCommands.Place(document, [layer.Id], other.Id, drop) ?? document;
        }

        switch (arguments.String("to"))
        {
            case "up":
                return LayerCommands.Move(document, layer.Id, 1) ?? document;
            case "down":
                return LayerCommands.Move(document, layer.Id, -1) ?? document;
            case "top":
            case "bottom":
            {
                int offset = arguments.String("to") == "top" ? 1 : -1;
                CanvasDocument current = document;
                while (LayerCommands.Move(current, layer.Id, offset) is CanvasDocument next) current = next;
                return current;
            }
            case "out_of_group":
                return LayerCommands.MoveOutOfFolder(document, layer.Id) ?? throw new ToolException($"'{layer.Name}' is not in a group.");
            default:
                throw new ToolException("Say where: 'to' (top, bottom, up, down, out_of_group), 'above', 'below' or 'into'.");
        }
    }

    /// <summary>The layer with the mask the arguments describe.</summary>
    private ImageLayer Masked(EditorSession.Open open, CanvasDocument document, ImageLayer layer, ToolArguments arguments)
    {
        string? shape = arguments.String("shape");
        bool invert = arguments.Bool("invert") ?? false;

        ImageLayer next = layer;
        switch (shape)
        {
            case null:
                // Invert on its own flips the mask the layer has.
                if (invert && layer.Mask is not null)
                {
                    if (MaskEditing.Invert(layer, selection: null) is ImageLayer flipped) next = flipped;
                }
                break;
            case "selection":
            {
                if (open.Selection is null) throw new ToolException("Nothing is selected. Call select first.");
                (int width, int height) = Grid(document, layer);
                byte[] levels = CoverageOver(open, layer.Transform, width, height);
                next = layer with { Mask = new LayerMask { Coverage = Coverage(width, height, i => invert ? (byte)(255 - levels[i]) : levels[i]) } };
                break;
            }
            case "image":
                next = layer with { Mask = new LayerMask { Coverage = ImageMask(document, layer, arguments, invert) } };
                break;
            case "none":
                next = layer with { Mask = null };
                break;
            case "reveal_all":
            case "hide_all":
                next = layer with { Mask = LayerMask.Solid((shape == "reveal_all") != invert) };
                break;
            case "rectangle":
            case "ellipse":
            {
                var box = new Rect(arguments.RequiredNumber("x"), arguments.RequiredNumber("y"),
                                   arguments.RequiredNumber("width"), arguments.RequiredNumber("height"));
                DocumentSelection selection = shape == "rectangle" ? DocumentSelection.Rectangle(box) : DocumentSelection.Ellipse(box);
                (int width, int height) = Grid(document, layer);
                byte[] levels = LayerFilters.SelectionLevels(selection, layer.Transform, width, height, 1, new PixelRect(0, 0, width, height));
                next = layer with { Mask = new LayerMask { Coverage = Coverage(width, height, i => invert ? (byte)(255 - levels[i]) : levels[i]) } };
                break;
            }
            case "linear_gradient":
            case "radial_gradient":
            {
                Point start = arguments.Position("start") ?? throw new ToolException("'start' is required.");
                Point end = arguments.Position("end") ?? throw new ToolException("'end' is required.");
                (int width, int height) = Grid(document, layer);
                double dx = end.X - start.X, dy = end.Y - start.Y, length = Math.Max(1e-6, dx * dx + dy * dy);
                double radius = Math.Sqrt(length);
                bool radial = shape == "radial_gradient";
                LayerTransform placement = layer.Transform;
                next = layer with
                {
                    Mask = new LayerMask
                    {
                        Coverage = Coverage(width, height, i =>
                        {
                            Point at = LayerGeometry.ToDocument(placement, new Point(i % width + 0.5, i / width + 0.5), width, height);
                            double t = radial
                                ? Math.Sqrt((at.X - start.X) * (at.X - start.X) + (at.Y - start.Y) * (at.Y - start.Y)) / radius
                                : ((at.X - start.X) * dx + (at.Y - start.Y) * dy) / length;
                            double shown = 1 - Math.Clamp(t, 0, 1);
                            if (invert) shown = 1 - shown;
                            return (byte)Math.Round(shown * 255);
                        }),
                    },
                };
                break;
            }
            default:
                throw new ToolException("'shape' is reveal_all, hide_all, rectangle, ellipse, linear_gradient, radial_gradient or none.");
        }

        if (arguments.Number("feather") is double feather && feather > 0)
        {
            if (next.Mask is null) throw new ToolException($"'{layer.Name}' has no mask to feather.");
            next = SoftenedMask(next, feather);
        }
        if (arguments.Bool("enabled") is bool enabled)
        {
            if (next.Mask is null) throw new ToolException($"'{layer.Name}' has no mask to switch.");
            next = next with { Mask = next.Mask with { IsEnabled = enabled } };
        }
        if (shape is null && !arguments.Has("enabled") && !arguments.Has("feather") && !invert)
            throw new ToolException("Give 'shape', 'invert', 'feather' or 'enabled'.");
        return next;
    }

    /// <summary>
    /// A mask from a picture: its lightness over black, or its alpha, sampled over the layer's own
    /// grid — stretched across the layer, or where x/y/width/height place it on the document, with
    /// everything outside hidden.
    /// </summary>
    private PixelBuffer ImageMask(CanvasDocument document, ImageLayer layer, ToolArguments arguments, bool invert)
    {
        (PixelBuffer picture, _) = Picture(arguments);
        try
        {
            (int width, int height) = Grid(document, layer);
            bool alpha = arguments.String("channel") == "alpha";
            Rect? placed = arguments.Has("x") || arguments.Has("width")
                ? new Rect(arguments.Number("x", 0), arguments.Number("y", 0),
                           arguments.Number("width", picture.Width), arguments.Number("height", picture.Height))
                : null;
            if (placed is { IsEmpty: true }) throw new ToolException("'width' and 'height' must be above 0.");

            PixelBuffer coverage = PixelBuffer.Allocate(width, height);
            var sample = new byte[4];
            for (int y = 0; y < height; y++)
            {
                Span<byte> row = coverage.Row(y);
                for (int x = 0; x < width; x++)
                {
                    double u, v;
                    if (placed is Rect box)
                    {
                        Point at = LayerGeometry.ToDocument(layer.Transform, new Point(x + 0.5, y + 0.5), width, height);
                        u = (at.X - box.X) / box.Width * picture.Width;
                        v = (at.Y - box.Y) / box.Height * picture.Height;
                    }
                    else
                    {
                        u = (x + 0.5) / width * picture.Width;
                        v = (y + 0.5) / height * picture.Height;
                    }

                    PixelSampling.Bilinear(picture, u, v, sample);
                    int level = alpha ? sample[3]
                        : (int)Math.Round(0.2126 * sample[0] + 0.7152 * sample[1] + 0.0722 * sample[2]);
                    if (invert) level = 255 - level;
                    row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = (byte)Math.Clamp(level, 0, 255);
                    row[x * 4 + 3] = 255;
                }
            }
            return coverage;
        }
        finally
        {
            picture.Release();
        }
    }

    /// <summary>
    /// The layer with its mask blurred by <paramref name="feather"/> document pixels, the grid's
    /// edge carried outwards first so the blur does not fade the mask where the layer ends.
    /// </summary>
    private static ImageLayer SoftenedMask(ImageLayer layer, double feather)
    {
        if (MaskEditing.Canvas(layer) is not (PixelBuffer working, LayerTransform placement)) return layer;
        try
        {
            int width = working.Width, height = working.Height;
            double sigma = Math.Max(0.3, feather / 2 * LayerScale(placement, width, height));
            int pad = (int)Math.Ceiling(sigma * 3) + 1;
            int paddedWidth = width + pad * 2, paddedHeight = height + pad * 2;
            var levels = new byte[paddedWidth * paddedHeight];
            for (int y = 0; y < paddedHeight; y++)
            {
                Span<byte> row = working.Row(Math.Clamp(y - pad, 0, height - 1));
                for (int x = 0; x < paddedWidth; x++) levels[y * paddedWidth + x] = row[Math.Clamp(x - pad, 0, width - 1) * 4];
            }

            byte[] soft = Blurred(levels, paddedWidth, paddedHeight, sigma);
            PixelBuffer coverage = Coverage(width, height, i => soft[(i / width + pad) * paddedWidth + i % width + pad]);
            return MaskEditing.WithMask(layer, coverage);
        }
        finally
        {
            working.Release();
        }
    }

    /// <summary>The pixel grid a layer's mask covers: its own pixels, or the canvas for a layer without any.</summary>
    private static (int, int) Grid(CanvasDocument document, ImageLayer layer) =>
        layer.Image is PixelBuffer image ? (image.Width, image.Height) : (document.Width, document.Height);

    private static PixelBuffer Coverage(int width, int height, Func<int, byte> level)
    {
        PixelBuffer coverage = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = coverage.Row(y);
            for (int x = 0; x < width; x++)
            {
                byte value = level(y * width + x);
                row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = value;
                row[x * 4 + 3] = 255;
            }
        }
        return coverage;
    }
}
