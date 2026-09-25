using System.Text.Json;
using static Compositor_korean_win.Core.ToolSchema;

namespace Compositor_korean_win.Core;

/// <summary>
/// The hand tools, driven by coordinates: brush strokes, Liquify, warp and distort.
/// </summary>
/// <remarks>
/// <para>
/// Each runs the same code the canvas runs when a person drags — <see cref="BrushStroke"/>,
/// <see cref="LiquifyField"/>, <see cref="WarpMesh"/>, <see cref="QuadWarp"/> — with the points a
/// model gives in place of the pointer's. Points are in document pixels like every other
/// coordinate the tools take, and are carried into the layer's own grid here, so a moved, scaled or
/// turned layer is painted where the model sees it in <c>render</c>.
/// </para>
/// <para>
/// Sizes are in document pixels too and are scaled into the layer's grid the same way; a 20-pixel
/// brush on a photo placed at half size is 40 of the photo's own pixels.
/// </para>
/// </remarks>
public sealed partial class McpTools
{
    private static readonly Property StrokesArgument =
        new("strokes", "array", "Several strokes in one step, each a list of points: [[[x, y], …], …].", Items: "array");

    private void DefinePixelTools()
    {
        Define("paint_stroke",
            "Drag a tool through points on a layer, as a person would: brush (paints 'color'), eraser, clone (copies from " +
            "'source', which matches the first point), heal (spot healing — blemishes, wires, small objects vanish into what " +
            "is around them) or blur (softens). size is the tip's diameter, hardness 0 (soft) to 1 (hard). With mask true it " +
            "paints the layer's mask instead: brush with black hides, white shows, eraser shows again (a mask is added if " +
            "there is none). Keeps to the selection. One call is one undo step; a text layer becomes pixels. It is for touching up, " +
            "masks and small areas: to carry a reference's pixels exactly use put_pixels, and make shapes and effects with " +
            "add_path, masks, liquify and warp rather than tracing them.",
            Build(LayerArgument, Choice("tool", "The tool.", ["brush", "eraser", "clone", "heal", "blur"], true),
                  PointsArgument, StrokesArgument,
                  Num("size", "Tip diameter in pixels. Default 20."), Num("hardness", "0 (soft edge)–1 (hard). Default 1."),
                  Colour("color", "brush: the colour; on a mask, its lightness (black hides, white shows). Default black."),
                  Num("opacity", "0–1: how strong the stroke is. Default 1."),
                  PointOf("source", "clone: where to copy from, standing for the first point of the first stroke."),
                  Bool("sample_all_layers", "clone: copy from the whole picture rather than this layer."),
                  Choice("heal_mode", "heal: default content_aware.", ["content_aware", "create_texture", "proximity_match"]),
                  Num("blur_radius", "blur: how far it softens, in pixels. Default 4."),
                  Bool("mask", "Paint the layer's (or group's or adjustment's) mask rather than its pixels."),
                  DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                string tool = arguments.Required("tool");
                List<List<Point>> strokes = Strokes(arguments);
                bool onMask = arguments.Bool("mask") == true;
                _session.Edit(open, tool switch
                {
                    "eraser" => "Eraser",
                    "clone" => "Clone Stamp",
                    "heal" => "Spot Healing",
                    "blur" => "Blur",
                    _ => "Brush",
                }, document => document.Replacing(onMask
                    ? MaskPainted(open, layer, tool, strokes, arguments)
                    : Painted(document, open, layer, tool, strokes, arguments)));
                ImageLayer after = open.Document.Layer(layer.Id)!;
                int points = strokes.Sum(stroke => stroke.Count);
                return ToolResult.Text($"Painted {strokes.Count} stroke{(strokes.Count == 1 ? "" : "s")} ({points} points) " +
                                       $"with {tool} on '{after.Name}'{(onMask ? "'s mask" : "")}.");
            });

        Define("liquify",
            "Reshape a layer's pixels with Liquify brushes dragged through points: forward pushes them along the stroke; " +
            "push_left moves them to the stroke's left; twirl turns them (reverse for anticlockwise); pucker pulls in towards " +
            "the brush; bloat swells out; reconstruct paints back towards how the layer began; freeze protects what it covers " +
            "from the other strokes of this call, thaw takes that off. twirl, pucker, bloat and reconstruct act where they " +
            "stand, 'repeat' dabs at each given point (as holding the mouse still). 'strokes' runs several in one step, each " +
            "an object with its own tool, points, size, pressure, repeat, reverse. A text layer becomes pixels.",
            Build(LayerArgument,
                  Choice("tool", "The brush. Default forward.", ["forward", "push_left", "twirl", "pucker", "bloat", "reconstruct", "freeze", "thaw"]),
                  PointsArgument,
                  new Property("strokes", "array", "Several strokes: [{\"tool\": …, \"points\": [[x, y], …], \"size\": …}, …]; " +
                                                   "anything left out comes from the top level.", Items: "object"),
                  Num("size", "Brush diameter in pixels. Default 100."), Num("pressure", "0.01–1. Default 0.5."),
                  Int("repeat", "Dabs at each point for the brushes that act in place. Default 10."),
                  Bool("reverse", "twirl: turn anticlockwise."), DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                if (layer.Image is not PixelBuffer image) throw new ToolException($"'{layer.Name}' has no pixels to liquify.");
                List<ToolArguments> strokes = LiquifyStrokes(arguments);

                var field = new LiquifyField(image.Width, image.Height);
                foreach (ToolArguments stroke in strokes) Liquify(field, layer, stroke, arguments);
                if (field.IsIdentity) return ToolResult.Text("Nothing moved: the strokes were outside the layer or too short to push.");

                _session.Edit(open, "Liquify", document => document.Replacing(field.Apply(document.Layer(layer.Id)!)));
                return ToolResult.Text($"Liquified '{layer.Name}' with {strokes.Count} stroke{(strokes.Count == 1 ? "" : "s")}.");
            });

        Define("warp_layer",
            "Bend a layer over a 4×4 warp grid, as Edit › Transform › Warp: a preset 'style' (the text warp shapes, with " +
            "bend/horizontal/vertical), all sixteen grid 'points' row by row from the top-left, or 'moves' of some points from " +
            "where they sit on the layer's box (row and column 0–3; corners are 0,0 0,3 3,0 3,3). Pixels are resampled once; " +
            "a text layer becomes pixels (to bend live text, use edit_text warp).",
            Build(LayerArgument,
                  Choice("style", "A preset shape.", ["arc", "arc_lower", "arc_upper", "arch", "bulge", "shell_lower", "shell_upper",
                                                     "flag", "wave", "fish", "rise", "fisheye", "inflate", "squeeze", "twist"]),
                  Num("bend", "-100–100. Default 50."), Num("horizontal", "Horizontal distortion -100–100."),
                  Num("vertical", "Vertical distortion -100–100."),
                  new Property("points", "array", "All sixteen grid points in document pixels, row by row: [[x, y], …].", Items: "array"),
                  new Property("moves", "array", "[{\"row\": 0–3, \"column\": 0–3, \"dx\": …, \"dy\": …}, …].", Items: "object"),
                  DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                if (layer.Image is null) throw new ToolException($"'{layer.Name}' has no pixels to warp.");
                WarpMesh mesh = Mesh(layer, arguments);
                ImageLayer warped = WarpMesh.Warp(layer, mesh) ?? throw new ToolException("That grid leaves nothing of the layer.");
                _session.Edit(open, "Warp", document => document.Replacing(warped));
                return ToolResult.Text($"Warped '{warped.Name}' {warped.Id}; it now spans {Bounds(warped)}.");
            });

        Define("distort_layer",
            "Move a layer's four corners, as Edit › Transform › Distort or Perspective: 'corners' as four [x, y] — top-left, " +
            "top-right, bottom-right, bottom-left — or 'moves' of some corners from where they are now. For perspective, move " +
            "the two corners of one side towards or away from each other. The corners must make a convex shape. Pixels are " +
            "resampled once; a text layer becomes pixels.",
            Build(LayerArgument,
                  new Property("corners", "array", "Four points: top-left, top-right, bottom-right, bottom-left.", Items: "array"),
                  Object("moves", "Corners to move by dx/dy.",
                         Object("top_left", "", Num("dx", "Right."), Num("dy", "Down.")),
                         Object("top_right", "", Num("dx", "Right."), Num("dy", "Down.")),
                         Object("bottom_right", "", Num("dx", "Right."), Num("dy", "Down.")),
                         Object("bottom_left", "", Num("dx", "Right."), Num("dy", "Down."))),
                  DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                if (layer.Image is null) throw new ToolException($"'{layer.Name}' has no pixels to distort.");

                List<Point> corners = [.. TransformDrag.CornersOf(layer.Transform)];
                if (arguments.Has("corners"))
                {
                    corners = Points(arguments, "corners");
                    if (corners.Count != 4) throw new ToolException("'corners' needs four points: top-left, top-right, bottom-right, bottom-left.");
                }
                if (arguments.Object("moves") is ToolArguments moves)
                {
                    string[] names = ["top_left", "top_right", "bottom_right", "bottom_left"];
                    for (int i = 0; i < 4; i++)
                        if (moves.Object(names[i]) is ToolArguments move)
                            corners[i] = new Point(corners[i].X + move.Number("dx", 0), corners[i].Y + move.Number("dy", 0));
                }
                if (!QuadWarp.IsUsable(corners)) throw new ToolException("Those corners fold over or collapse; they must make a convex shape.");

                ImageLayer distorted = QuadWarp.Distort(layer, corners) ?? throw new ToolException("Those corners leave nothing of the layer.");
                _session.Edit(open, "Distort", document => document.Replacing(distorted));
                return ToolResult.Text($"Distorted '{distorted.Name}' {distorted.Id}; it now spans {Bounds(distorted)}.");
            });
    }

    // ---- Strokes -------------------------------------------------------------------------------

    /// <summary>'points' as one stroke, or 'strokes' as several.</summary>
    private static List<List<Point>> Strokes(ToolArguments arguments)
    {
        var strokes = new List<List<Point>>();
        if (arguments.Raw("strokes") is { ValueKind: JsonValueKind.Array } many)
            foreach (JsonElement stroke in many.EnumerateArray()) strokes.Add(Points(stroke, "strokes"));
        if (arguments.Has("points")) strokes.Add(Points(arguments, "points"));
        strokes.RemoveAll(stroke => stroke.Count == 0);
        if (strokes.Count == 0) throw new ToolException("Give 'points' ([[x, y], …]) or 'strokes'.");
        return strokes;
    }

    private static BrushSettings BrushFor(string tool, ToolArguments arguments, double scale, Rgba colour) => new()
    {
        Diameter = Math.Clamp(arguments.Number("size", 20), 0.5, 5000) * scale,
        Hardness = Math.Clamp(arguments.Number("hardness", 1), 0, 1),
        Color = colour,
        Opacity = Math.Clamp(Opacity(arguments) ?? 1, 0, 1),
        Mode = tool switch
        {
            "brush" => BrushMode.Paint,
            "eraser" => BrushMode.Erase,
            "clone" => BrushMode.Clone,
            "heal" => BrushMode.Heal,
            "blur" => BrushMode.Blur,
            _ => throw new ToolException("'tool' is brush, eraser, clone, heal or blur."),
        },
        HealingMode = arguments.String("heal_mode") switch
        {
            null or "content_aware" => SpotHealingMode.ContentAware,
            "create_texture" => SpotHealingMode.CreateTexture,
            "proximity_match" => SpotHealingMode.ProximityMatch,
            _ => throw new ToolException("'heal_mode' is content_aware, create_texture or proximity_match."),
        },
        BlurRadius = Math.Max(0.5, arguments.Number("blur_radius", 4) * scale),
    };

    /// <summary>The layer with the strokes painted into its pixels.</summary>
    private ImageLayer Painted(CanvasDocument document, EditorSession.Open open, ImageLayer layer, string tool,
                               List<List<Point>> strokes, ToolArguments arguments)
    {
        (PixelBuffer pixels, LayerTransform placement, bool made) = Editable(document, layer);
        try
        {
            int width = pixels.Width, height = pixels.Height;
            BrushSettings settings = BrushFor(tool, arguments, LayerScale(placement, width, height), arguments.Colour("color") ?? Rgba.Black);
            if (made && settings.Mode is BrushMode.Heal or BrushMode.Blur or BrushMode.Erase)
                throw new ToolException($"'{layer.Name}' has no pixels yet for {tool} to work on.");

            List<List<Point>> inGrid = [.. strokes.Select(stroke => stroke.Select(point => LayerGeometry.ToPixels(placement, point, width, height)).ToList())];

            // Aligned, as Photoshop's default: every stroke keeps the offset the first one set. Each
            // stroke gets a source of its own, since a stroke disposes its source when it ends, and
            // reads the layer as the strokes before it left it.
            Func<PixelBuffer, CloneSource>? clone = null;
            if (settings.Mode == BrushMode.Clone)
            {
                Point anchor = arguments.Position("source") ?? throw new ToolException("clone needs 'source': where to copy from.");
                Point from = LayerGeometry.ToPixels(placement, anchor, width, height);
                var offset = new Point(from.X - inGrid[0][0].X, from.Y - inGrid[0][0].Y);
                if (arguments.Bool("sample_all_layers") == true)
                {
                    IPixelSource everything = CloneSampling.AllLayersSource(document, placement, width, height);
                    clone = _ => new CloneSource(everything, offset);
                }
                else
                {
                    if (made) throw new ToolException($"'{layer.Name}' has no pixels to copy from; use sample_all_layers.");
                    clone = current => new CloneSource(new BufferSource(current), offset);
                }
            }

            PixelBuffer? painted = Stroked(pixels, settings, inGrid, Restriction(open, placement, width, height), clone);
            if (painted is null) return layer;
            painted = Feathered(open, placement, pixels, painted);
            return layer with { Image = painted, Transform = placement, Shape = null };
        }
        finally
        {
            if (made) pixels.Release();
        }
    }

    /// <summary>The layer with the strokes painted into its mask, which it is given if it has none.</summary>
    private static ImageLayer MaskPainted(EditorSession.Open open, ImageLayer layer, string tool, List<List<Point>> strokes,
                                          ToolArguments arguments)
    {
        if (tool is "clone" or "heal") throw new ToolException("A mask takes brush, eraser or blur.");
        ImageLayer target = layer.Mask is null ? layer with { Mask = LayerMask.Solid(true) } : layer;
        if (MaskEditing.Canvas(target) is not (PixelBuffer working, LayerTransform placement))
            throw new ToolException($"'{layer.Name}''s mask is too large to paint.");

        try
        {
            int width = working.Width, height = working.Height;
            // A mask has no colour: brush lays the colour's lightness, eraser shows the layer again.
            Rgba given = arguments.Colour("color") ?? Rgba.Black;
            byte grey = (byte)Math.Clamp(Math.Round(0.2126 * given.R + 0.7152 * given.G + 0.0722 * given.B), 0, 255);
            BrushSettings settings = BrushFor(tool == "eraser" ? "brush" : tool, arguments, LayerScale(placement, width, height),
                                              tool == "eraser" ? Rgba.White : new Rgba(grey, grey, grey));

            List<List<Point>> inGrid = [.. strokes.Select(stroke => stroke.Select(point => LayerGeometry.ToPixels(placement, point, width, height)).ToList())];
            PixelBuffer? painted = Stroked(working, settings, inGrid, Restriction(open, placement, width, height));
            if (painted is null) return target;
            painted = Feathered(open, placement, working, painted);
            return MaskEditing.WithMask(target, painted);
        }
        finally
        {
            working.Release();
        }
    }

    /// <summary>The selection in a grid's own pixels, for a stroke to keep to — when it is not feathered.</summary>
    private static DocumentSelection? Restriction(EditorSession.Open open, LayerTransform placement, int width, int height) =>
        open.Selection is DocumentSelection selection && !(open.Feather > 0)
            ? selection.Transformed(point => LayerGeometry.ToPixels(placement, point, width, height))
            : null;

    /// <summary>
    /// A feathered selection applied to what a tool made: the result moved back towards the
    /// original by how little of each pixel is selected. The caller's <paramref name="edited"/> is
    /// taken over; what comes back is the caller's.
    /// </summary>
    private static PixelBuffer Feathered(EditorSession.Open open, LayerTransform placement, PixelBuffer original, PixelBuffer edited)
    {
        if (!(open.Feather > 0) || SelectionOver(open, placement, original.Width, original.Height) is not byte[] levels) return edited;
        PixelBuffer flat = PixelFilters.Copy(edited);
        edited.Release();
        PixelFilters.Confine(original, flat, levels);
        return flat;
    }

    /// <summary>
    /// Each stroke run over the result of the one before, as separate drags are. Null when nothing
    /// was painted; otherwise the caller owns the result.
    /// </summary>
    private static PixelBuffer? Stroked(PixelBuffer start, BrushSettings settings, List<List<Point>> strokes, DocumentSelection? selection,
                                        Func<PixelBuffer, CloneSource>? clone = null)
    {
        PixelBuffer current = start;
        bool owned = false;
        try
        {
            foreach (List<Point> points in strokes)
            {
                BrushSettings each = clone is null ? settings : settings with { CloneFrom = clone(current) };
                using var stroke = new BrushStroke(current, current.Width, current.Height, each, selection);
                foreach (Point point in points) stroke.Append(point);
                if (stroke.IsEmpty) continue;
                if (settings.Mode == BrushMode.Heal) stroke.Heal((uint)Random.Shared.Next());

                PixelBuffer next = stroke.Commit();
                if (owned) current.Release();
                current = next;
                owned = true;
            }

            PixelBuffer? result = owned ? current : null;
            owned = false;
            return result;
        }
        finally
        {
            if (owned) current.Release();
        }
    }

    // ---- Liquify -------------------------------------------------------------------------------

    /// <summary>The strokes a liquify call asks for, each with what it did not say filled from the call.</summary>
    private static List<ToolArguments> LiquifyStrokes(ToolArguments arguments)
    {
        var strokes = new List<ToolArguments>();
        if (arguments.Raw("strokes") is { ValueKind: JsonValueKind.Array } many)
        {
            foreach (JsonElement stroke in many.EnumerateArray())
            {
                if (stroke.ValueKind != JsonValueKind.Object) throw new ToolException("Each of 'strokes' is an object with 'points'.");
                strokes.Add(new ToolArguments(stroke));
            }
        }
        if (arguments.Has("points")) strokes.Add(arguments);
        if (strokes.Count == 0) throw new ToolException("Give 'points' ([[x, y], …]) or 'strokes'.");
        return strokes;
    }

    private static void Liquify(LiquifyField field, ImageLayer layer, ToolArguments stroke, ToolArguments call)
    {
        int width = field.Width, height = field.Height;
        string name = stroke.String("tool") ?? call.String("tool") ?? "forward";
        LiquifyTool tool = name switch
        {
            "forward" => LiquifyTool.Forward,
            "push_left" => LiquifyTool.PushLeft,
            "twirl" => LiquifyTool.Twirl,
            "pucker" => LiquifyTool.Pucker,
            "bloat" => LiquifyTool.Bloat,
            "reconstruct" => LiquifyTool.Reconstruct,
            "freeze" => LiquifyTool.Freeze,
            "thaw" => LiquifyTool.Thaw,
            _ => throw new ToolException($"'{name}' is not a liquify tool: forward, push_left, twirl, pucker, bloat, reconstruct, freeze or thaw."),
        };
        double radius = Math.Max(1, Math.Clamp(stroke.Number("size") ?? call.Number("size", 100), 1, 2000)
                                    * LayerScale(layer.Transform, width, height) / 2);
        double pressure = Math.Clamp(stroke.Number("pressure") ?? call.Number("pressure", 0.5), 0.01, 1);
        int repeat = Math.Clamp(stroke.Int("repeat") ?? call.Int("repeat") ?? 10, 1, 200);
        bool reverse = stroke.Bool("reverse") ?? call.Bool("reverse") ?? false;

        List<Point> points = [.. Points(stroke, "points").Select(point => LayerGeometry.ToPixels(layer.Transform, point, width, height))];
        if (points.Count == 0) throw new ToolException("A liquify stroke needs 'points'.");

        bool pushes = tool is LiquifyTool.Forward or LiquifyTool.PushLeft;
        field.BeginStroke();
        try
        {
            // The brushes that act where they stand dab at every point given, as a held mouse does;
            // along the way between points they dab once per step, as a moving one does.
            if (!pushes)
                for (int i = 0; i < repeat; i++) field.Dab(tool, points[0], radius, pressure, default, reverse);

            // A quarter of the brush apart, each pushing by the way it came — the canvas's spacing.
            double spacing = Math.Max(1, radius * 0.25);
            for (int i = 1; i < points.Count; i++)
            {
                Point from = points[i - 1], to = points[i];
                double distance = Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y));
                int steps = Math.Max(1, (int)Math.Ceiling(distance / spacing));
                Point previous = from;
                for (int step = 1; step <= steps; step++)
                {
                    double t = (double)step / steps;
                    var next = new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
                    field.Dab(tool, next, radius, pressure, new Point(next.X - previous.X, next.Y - previous.Y), reverse);
                    previous = next;
                }
                if (!pushes)
                    for (int k = 1; k < repeat; k++) field.Dab(tool, to, radius, pressure, default, reverse);
            }
        }
        finally
        {
            field.EndStroke();
        }
    }

    // ---- Warp ----------------------------------------------------------------------------------

    private static WarpMesh Mesh(ImageLayer layer, ToolArguments arguments)
    {
        if (arguments.String("style") is string named)
        {
            string style = named.Replace("_", "").Replace(" ", "").Replace("-", "");
            if (!Enum.TryParse(style, ignoreCase: true, out TextWarpStyle parsed) || parsed == TextWarpStyle.None)
                throw new ToolException($"'{named}' is not a warp style.");
            var warp = new TextWarp
            {
                Style = parsed,
                Bend = Math.Clamp(arguments.Number("bend", 50), -100, 100),
                Horizontal = Math.Clamp(arguments.Number("horizontal", 0), -100, 100),
                Vertical = Math.Clamp(arguments.Number("vertical", 0), -100, 100),
            };
            return WarpMesh.Preset(layer.Transform, warp);
        }

        if (arguments.Has("points"))
        {
            List<Point> points = Points(arguments, "points");
            if (points.Count != WarpMesh.Side * WarpMesh.Side) throw new ToolException("'points' needs all sixteen grid points, row by row.");
            return new WarpMesh(points);
        }

        if (arguments.Raw("moves") is { ValueKind: JsonValueKind.Array } moves)
        {
            Point[] points = [.. WarpMesh.Flat(layer.Transform).Points];
            foreach (JsonElement item in moves.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new ToolException("Each move is {\"row\", \"column\", \"dx\", \"dy\"}.");
                var move = new ToolArguments(item);
                int row = move.Int("row") ?? throw new ToolException("A move needs 'row'.");
                int column = move.Int("column") ?? throw new ToolException("A move needs 'column'.");
                if (row is < 0 or > 3 || column is < 0 or > 3) throw new ToolException("'row' and 'column' are 0–3.");
                int index = row * WarpMesh.Side + column;
                points[index] = new Point(points[index].X + move.Number("dx", 0), points[index].Y + move.Number("dy", 0));
            }
            return new WarpMesh(points);
        }

        throw new ToolException("Give 'style', 'points' or 'moves'.");
    }
}
