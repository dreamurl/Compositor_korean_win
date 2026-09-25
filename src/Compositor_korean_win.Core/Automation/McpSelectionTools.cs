using System.Text.Json;
using static Compositor_korean_win.Core.ToolSchema;

namespace Compositor_korean_win.Core;

/// <summary>
/// The selection, and the pixel edits that keep to it: filling, clearing, rebuilding and copying.
/// </summary>
/// <remarks>
/// <para>
/// The selection is the editor's own (<see cref="DocumentSelection"/>): outlines in document pixels,
/// added and taken away in order. A model draws one with coordinates the way a person drags one,
/// and every pixel tool — these, <c>paint_stroke</c>, <c>apply_filter</c>, a mask made from it —
/// keeps to it, as the canvas's tools do.
/// </para>
/// <para>
/// Feathering is the one thing the editor's selections do not have, since the canvas only ever
/// makes hard-edged ones. It is kept beside the outline (<see cref="EditorSession.Open.Feather"/>)
/// and applied where the selection becomes coverage over a layer's pixels, so the outline stays
/// exact and can still be inverted, grown or moved after it has been softened.
/// </para>
/// </remarks>
public sealed partial class McpTools
{
    private static readonly Property PointsArgument =
        new("points", "array", "Points in document pixels: [[x, y], …] (or [{\"x\": …, \"y\": …}, …]).", Items: "array");

    private void DefineSelectionTools()
    {
        Define("select",
            "Select part of the canvas (the marching ants). paint_stroke, fill_selection, copy_to_layer, apply_filter and " +
            "set_mask shape=selection then keep to it. shape: rectangle or ellipse (x, y, width, height); lasso (points, " +
            "joined into a closed outline); layer (wherever a layer has pixels); magic_wand (pixels like the one at x, y " +
            "on 'layer', or on the whole picture with sample_all_layers); all; none. 'mode' adds to, subtracts from or " +
            "intersects with the current selection. 'feather' softens its edge.",
            Build(Choice("shape", "What to select.", ["rectangle", "ellipse", "lasso", "layer", "magic_wand", "all", "none"], true),
                  Num("x", "Box left, or the magic wand's point."), Num("y", "Box top, or the magic wand's point."),
                  Num("width", "Box width."), Num("height", "Box height."), PointsArgument,
                  Str("layer", "shape layer: whose pixels. magic_wand: the layer to sample (default the active one)."),
                  Int("tolerance", "magic_wand: 0–255 difference allowed per channel. Default 32."),
                  Bool("contiguous", "magic_wand: only pixels connected to the point. Default true."),
                  Bool("sample_all_layers", "magic_wand: match the whole picture rather than one layer."),
                  Choice("mode", "Default replace.", ["replace", "add", "subtract", "intersect"]),
                  Num("feather", "Soften the edge over this many pixels each side of the outline."),
                  Bool("antialias", "Smooth edges. Default true (the magic wand's are pixel-exact)."),
                  DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                string shape = arguments.Required("shape");
                string mode = arguments.String("mode") ?? "replace";
                if (shape == "none")
                {
                    open.Selection = null;
                    open.Feather = 0;
                    return ToolResult.Text("Nothing is selected; tools act on whole layers again.");
                }

                DocumentSelection made = Selected(open, shape, arguments);
                if (arguments.Bool("antialias") is bool antialias) made = made with { IsAntialiased = antialias };

                DocumentSelection? current = open.Selection;
                open.Selection = mode switch
                {
                    "replace" => made,
                    "add" => current?.Adding(made) ?? made,
                    "subtract" => (current ?? throw new ToolException("Nothing is selected to subtract from.")).Subtracting(made),
                    "intersect" => current is null ? made : Intersection(open.Document, current, made),
                    _ => throw new ToolException("'mode' is replace, add, subtract or intersect."),
                };
                if (arguments.Number("feather") is double feather) open.Feather = Math.Clamp(feather, 0, 1000);
                else if (mode == "replace") open.Feather = 0;
                return ToolResult.Text(SelectionSummary(open));
            });

        Define("modify_selection",
            "Change the current selection: invert it, grow (expand) or shrink (contract) it by pixels, scale or rotate it " +
            "about its centre, move it by dx/dy, or set its feather. Several can be given; they apply in that order.",
            Build(Bool("invert", "Select everything that was not selected."), Int("expand", "Grow by this many pixels."),
                  Int("contract", "Shrink by this many pixels."), Num("scale", "Multiply its size about its centre."),
                  Num("rotation", "Degrees clockwise about its centre."), Num("dx", "Move right by this much."),
                  Num("dy", "Move down by this much."), Num("feather", "Soften the edge over this many pixels (0 for hard)."),
                  DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                DocumentSelection selection = open.Selection ?? throw new ToolException("Nothing is selected. Call select first.");
                int width = open.Document.Width, height = open.Document.Height;

                if (arguments.Bool("invert") == true)
                    selection = SelectionCommands.Inverse(selection, width, height) ?? DocumentSelection.Empty;
                if (arguments.Int("expand") is int grow && grow > 0)
                    selection = SelectionCommands.Expand(selection, width, height, grow) ?? DocumentSelection.Empty;
                if (arguments.Int("contract") is int shrink && shrink > 0)
                    selection = SelectionCommands.Contract(selection, width, height, shrink) ?? DocumentSelection.Empty;

                double scale = arguments.Number("scale", 1), turn = arguments.Number("rotation", 0);
                if (!(scale > 0)) throw new ToolException("'scale' must be above 0.");
                if ((scale != 1 || turn != 0) && !selection.IsEmpty)
                {
                    Rect box = selection.Bounds;
                    double cx = box.MidX, cy = box.MidY;
                    double cos = Math.Cos(turn * Math.PI / 180) * scale, sin = Math.Sin(turn * Math.PI / 180) * scale;
                    selection = selection.Transformed(point =>
                    {
                        double x = point.X - cx, y = point.Y - cy;
                        return new Point(cx + x * cos - y * sin, cy + x * sin + y * cos);
                    });
                }

                double dx = arguments.Number("dx", 0), dy = arguments.Number("dy", 0);
                if (dx != 0 || dy != 0) selection = selection.Transformed(point => new Point(point.X + dx, point.Y + dy));
                if (arguments.Number("feather") is double feather) open.Feather = Math.Clamp(feather, 0, 1000);

                open.Selection = selection;
                return ToolResult.Text(SelectionSummary(open));
            });

        Define("fill_selection",
            "Fill the selection — or the whole layer, with nothing selected — on a pixel layer: with a colour, with " +
            "\"transparent\" to clear it, or with content_aware to rebuild it from what is around it (removing an object). " +
            "A text layer becomes pixels.",
            Build(LayerArgument, Colour("color", "The fill; \"transparent\" clears."), Num("opacity", "0–1. Default 1."),
                  Bool("content_aware", "Rebuild the selected pixels from their surroundings instead of filling."),
                  DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                bool rebuild = arguments.Bool("content_aware") == true;
                Rgba? colour = arguments.Colour("color");
                if (!rebuild && colour is null) throw new ToolException("Give 'color' (\"transparent\" to clear) or content_aware true.");
                if (rebuild && open.Selection is null) throw new ToolException("content_aware needs a selection: select what to remove first.");

                _session.Edit(open, rebuild ? "Content-Aware Fill" : "Fill", document =>
                    document.Replacing(Filled(document, open, layer, colour, Math.Clamp(Opacity(arguments) ?? 1, 0, 1), rebuild)));
                ImageLayer after = open.Document.Layer(layer.Id)!;
                return ToolResult.Text($"Filled '{after.Name}' {after.Id}{(open.Selection is null ? " (whole layer)" : " inside the selection")}.");
            });

        Define("copy_to_layer",
            "Copy the selected pixels of a layer onto a new layer directly above it (Photoshop's Layer via Copy), or move " +
            "them there with cut. With nothing selected the whole layer is copied.",
            Build(LayerArgument, Bool("cut", "Take the pixels out of the source (Layer via Cut)."), Str("name", "The new layer's name."),
                  DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer"));
                if (layer.Image is not PixelBuffer image) throw new ToolException($"'{layer.Name}' has no pixels to copy.");
                bool cut = arguments.Bool("cut") == true;

                byte[] levels = CoverageOver(open, layer.Transform, image.Width, image.Height);
                PixelBuffer copied = Scaled(image, levels, keep: true);
                PixelRect trim = LayerFilters.Trim(copied);
                if (trim.IsEmpty)
                {
                    copied.Release();
                    throw new ToolException($"The selection covers none of '{layer.Name}''s pixels.");
                }
                PixelBuffer pixels = PixelRegion.Copy(copied, trim);
                copied.Release();

                var made = new ImageLayer
                {
                    Id = Guid.NewGuid(),
                    Name = arguments.String("name") is { Length: > 0 } name ? name : layer.Name + (cut ? " cut" : " copy"),
                    Image = pixels,
                    Transform = LayerGeometry.Place(layer.Transform, trim, image.Width, image.Height),
                };
                _session.Edit(open, cut ? "Layer via Cut" : "Layer via Copy", document =>
                {
                    CanvasDocument next = cut ? document.Replacing(layer with { Image = Scaled(image, levels, keep: false), Shape = null }) : document;
                    return InsertAt(next, made, into: null, above: layer.Id.ToString());
                });
                open.Active = made.Id;
                return ToolResult.Text($"{(cut ? "Cut" : "Copied")} to '{made.Name}' {made.Id} at {Bounds(open.Document.Layer(made.Id)!)}.");
            });
    }

    /// <summary>The selection a <c>select</c> call describes, before it is combined with the current one.</summary>
    private DocumentSelection Selected(EditorSession.Open open, string shape, ToolArguments arguments)
    {
        CanvasDocument document = open.Document;
        switch (shape)
        {
            case "rectangle":
            case "ellipse":
            {
                var box = new Rect(arguments.RequiredNumber("x"), arguments.RequiredNumber("y"),
                                   arguments.RequiredNumber("width"), arguments.RequiredNumber("height"));
                if (box.IsEmpty) throw new ToolException("'width' and 'height' must be above 0.");
                return shape == "rectangle" ? DocumentSelection.Rectangle(box) : DocumentSelection.Ellipse(box);
            }
            case "lasso":
            {
                List<Point> points = Points(arguments, "points");
                if (points.Count < 3) throw new ToolException("A lasso needs at least three points.");
                return DocumentSelection.Lasso(points);
            }
            case "all":
                return DocumentSelection.Rectangle(new Rect(0, 0, document.Width, document.Height));
            case "layer":
            {
                ImageLayer layer = EditorSession.Layer(document, arguments.String("layer"));
                return SelectionCommands.FromLayer(document, layer) ?? throw new ToolException($"'{layer.Name}' covers no pixels.");
            }
            case "magic_wand":
            {
                var at = new Point(arguments.RequiredNumber("x"), arguments.RequiredNumber("y"));
                var settings = new WandSettings
                {
                    Tolerance = Math.Clamp(arguments.Int("tolerance") ?? 32, 0, 255),
                    Contiguous = arguments.Bool("contiguous") ?? true,
                    SampleAllLayers = arguments.Bool("sample_all_layers") ?? false,
                };

                DocumentSelection? matched;
                WandOutcome outcome;
                if (settings.SampleAllLayers)
                {
                    using PixelBuffer composite = Flatten(document);
                    (matched, outcome) = MagicWand.Select(composite, at, settings);
                }
                else
                {
                    ImageLayer layer = arguments.String("layer") is string reference
                        ? EditorSession.Layer(document, reference)
                        : open.Active is Guid active && document.Layer(active) is ImageLayer chosen ? chosen
                        : throw new ToolException("Say which layer to sample with 'layer', or use sample_all_layers.");
                    if (layer.Image is not PixelBuffer pixels) throw new ToolException($"'{layer.Name}' has no pixels to sample.");
                    Point pixel = LayerGeometry.ToPixels(layer.Transform, at, pixels.Width, pixels.Height);
                    (matched, outcome) = MagicWand.Select(pixels, pixel, settings);
                    matched = matched?.Transformed(point => LayerGeometry.ToDocument(layer.Transform, point, pixels.Width, pixels.Height));
                }

                return matched ?? throw new ToolException(outcome switch
                {
                    WandOutcome.TooDetailed => "The matching pixels make a shape too detailed to outline; lower the tolerance.",
                    WandOutcome.OutOfMemory => "Not enough memory to outline the matching pixels.",
                    _ => "Nothing there matched: the point is outside the layer or on a transparent pixel.",
                });
            }
            default:
                throw new ToolException("'shape' is rectangle, ellipse, lasso, layer, magic_wand, all or none.");
        }
    }

    /// <summary>
    /// Where two selections overlap, traced along pixel edges. A selection is a list of steps that
    /// add or take away, and there is no step that keeps only an overlap, so it goes through pixels.
    /// </summary>
    private static DocumentSelection Intersection(CanvasDocument document, DocumentSelection first, DocumentSelection second)
    {
        var canvas = new PixelRect(0, 0, document.Width, document.Height);
        byte[] a = first.Levels(canvas), b = second.Levels(canvas);
        var both = new byte[a.Length];
        for (int i = 0; i < both.Length; i++) both[i] = a[i] >= 128 && b[i] >= 128 ? (byte)1 : (byte)0;
        return MagicWand.Outline(both, document.Width, document.Height).Selection ?? DocumentSelection.Empty;
    }

    private static string SelectionSummary(EditorSession.Open open)
    {
        if (open.Selection is not DocumentSelection selection) return "Nothing is selected.";
        PixelRect box = selection.IsEmpty ? default
            : selection.Bounds.Enclosing().Intersect(new PixelRect(0, 0, open.Document.Width, open.Document.Height));
        if (box.IsEmpty) return "The selection is empty: pixel tools will change nothing until it is changed or cleared (select shape none).";
        return $"Selected within x {box.X}, y {box.Y}, {box.Width}×{box.Height}{(open.Feather > 0 ? $", feathered {open.Feather:0.#} px" : "")}.";
    }

    /// <summary>Points as <c>[[x, y], …]</c>, <c>[{x, y}, …]</c> or a flat <c>[x, y, x, y, …]</c>.</summary>
    private static List<Point> Points(ToolArguments arguments, string name) =>
        arguments.Raw(name) is JsonElement raw ? Points(raw, name) : [];

    private static List<Point> Points(JsonElement raw, string name)
    {
        if (raw.ValueKind != JsonValueKind.Array) throw new ToolException($"'{name}' should be a list of points, [[x, y], …].");
        var points = new List<Point>();
        List<double> flat = [];
        foreach (JsonElement item in raw.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.Array when item.GetArrayLength() >= 2:
                    points.Add(new Point(Number(item[0]), Number(item[1])));
                    break;
                case JsonValueKind.Object:
                {
                    var each = new ToolArguments(item);
                    points.Add(new Point(each.RequiredNumber("x"), each.RequiredNumber("y")));
                    break;
                }
                case JsonValueKind.Number:
                    flat.Add(item.GetDouble());
                    break;
                default:
                    throw new ToolException($"'{name}' should be a list of points, [[x, y], …].");
            }
        }
        for (int i = 0; i + 1 < flat.Count; i += 2) points.Add(new Point(flat[i], flat[i + 1]));
        if (points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            throw new ToolException($"'{name}' has a point that is not a number.");
        return points;

        static double Number(JsonElement value) =>
            value.ValueKind == JsonValueKind.Number ? value.GetDouble()
            : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                                                                         System.Globalization.CultureInfo.InvariantCulture, out double parsed) ? parsed
            : throw new ToolException("A point's coordinates should be numbers.");
    }

    // ---- Coverage ------------------------------------------------------------------------------

    /// <summary>
    /// How much of each pixel of a layer's grid the selection takes, feathered; null with no
    /// selection, which means all of it.
    /// </summary>
    private static byte[]? SelectionOver(EditorSession.Open open, LayerTransform placement, int width, int height)
    {
        if (open.Selection is not DocumentSelection selection) return null;
        var grid = new PixelRect(0, 0, width, height);
        if (!(open.Feather > 0)) return LayerFilters.SelectionLevels(selection, placement, width, height, 1, grid);

        // The outline is rasterised past the grid by the blur's reach, so the fade at the layer's
        // edge is the selection's and not the edge of the buffer it was blurred in.
        double sigma = Math.Max(0.3, open.Feather / 2 * LayerScale(placement, width, height));
        int pad = (int)Math.Ceiling(sigma * 3) + 1;
        var padded = new PixelRect(-pad, -pad, width + pad * 2, height + pad * 2);
        byte[] levels = LayerFilters.SelectionLevels(selection, placement, width, height, 1, padded);
        byte[] soft = Blurred(levels, padded.Width, padded.Height, sigma);

        var result = new byte[width * height];
        for (int y = 0; y < height; y++)
            Array.Copy(soft, (y + pad) * padded.Width + pad, result, y * width, width);
        return result;
    }

    /// <summary><see cref="SelectionOver"/>, with no selection as full coverage.</summary>
    private static byte[] CoverageOver(EditorSession.Open open, LayerTransform placement, int width, int height)
    {
        if (SelectionOver(open, placement, width, height) is byte[] levels) return levels;
        var all = new byte[width * height];
        Array.Fill(all, (byte)255);
        return all;
    }

    /// <summary>Layer pixels per document pixel, averaged across the two directions.</summary>
    private static double LayerScale(LayerTransform placement, int width, int height)
    {
        double across = placement.Size.Width > 0 ? width / placement.Size.Width : 1;
        double down = placement.Size.Height > 0 ? height / placement.Size.Height : 1;
        double scale = Math.Sqrt(Math.Abs(across * down));
        return double.IsFinite(scale) && scale > 0 ? scale : 1;
    }

    /// <summary>A grid of levels blurred with a Gaussian of <paramref name="sigma"/> pixels.</summary>
    private static byte[] Blurred(byte[] levels, int width, int height, double sigma)
    {
        using PixelBuffer grey = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = grey.Row(y);
            for (int x = 0; x < width; x++)
            {
                byte level = levels[y * width + x];
                row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = row[x * 4 + 3] = level;
            }
        }

        using PixelBuffer soft = PixelFilters.GaussianBlur(grey, sigma);
        var result = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = soft.Row(y);
            for (int x = 0; x < width; x++) result[y * width + x] = row[x * 4 + 3];
        }
        return result;
    }

    /// <summary>
    /// A copy of <paramref name="image"/> with each pixel kept by its level (<paramref name="keep"/>)
    /// or by what its level leaves (a cut). The caller owns the result.
    /// </summary>
    private static PixelBuffer Scaled(PixelBuffer image, byte[] levels, bool keep)
    {
        PixelBuffer result = PixelFilters.Copy(image);
        for (int y = 0; y < result.Height; y++)
        {
            Span<byte> row = result.Row(y);
            for (int x = 0; x < result.Width; x++)
            {
                int level = levels[y * result.Width + x];
                int factor = keep ? level : 255 - level;
                if (factor == 255) continue;
                for (int c = 0; c < 4; c++) row[x * 4 + c] = (byte)((row[x * 4 + c] * factor + 127) / 255);
            }
        }
        return result;
    }

    /// <summary>
    /// A layer's pixels to change and the grid they sit on: its own, or for a layer with none yet a
    /// transparent canvas-sized grid over the canvas. The pixels are the caller's to release when
    /// <paramref name="made"/> is true.
    /// </summary>
    private static (PixelBuffer Pixels, LayerTransform Placement, bool Made) Editable(CanvasDocument document, ImageLayer layer)
    {
        if (layer.IsGroup || layer.Adjustment is not null)
            throw new ToolException($"'{layer.Name}' is a {(layer.IsGroup ? "group" : "adjustment")} and has no pixels; paint on a layer, or on its mask with mask true.");
        return layer.Image is PixelBuffer image
            ? (image, layer.Transform, false)
            : (PixelBuffer.Allocate(document.Width, document.Height), new LayerTransform(Point.Zero, document.Size), true);
    }

    private static ImageLayer Filled(CanvasDocument document, EditorSession.Open open, ImageLayer layer, Rgba? colour,
                                     double opacity, bool rebuild)
    {
        (PixelBuffer pixels, LayerTransform placement, bool made) = Editable(document, layer);
        try
        {
            byte[] levels = CoverageOver(open, placement, pixels.Width, pixels.Height);
            PixelBuffer result;
            if (rebuild)
            {
                // The kernel fills whatever is marked; a feathered edge is then blended back.
                byte[] hole = [.. levels.Select(level => level >= 128 ? (byte)255 : (byte)0)];
                result = PixelFilters.Copy(pixels);
                if (!SpotHeal.ContentAwareFill(result, hole))
                {
                    result.Release();
                    throw new ToolException("There is nothing around the selection to rebuild it from.");
                }
                if (open.Feather > 0) PixelFilters.Confine(pixels, result, levels);
            }
            else if (colour is { A: 0 })
            {
                result = Scaled(pixels, [.. levels.Select(level => (byte)Math.Round(level * opacity))], keep: false);
            }
            else if (colour is Rgba fill)
            {
                result = PixelFilters.Copy(pixels);
                Painting.Fill(result, new PixelRect(0, 0, pixels.Width, pixels.Height), levels, fill, opacity);
            }
            else
            {
                throw new ToolException("Give 'color' or content_aware true.");
            }
            return layer with { Image = result, Transform = placement, Shape = null };
        }
        finally
        {
            if (made) pixels.Release();
        }
    }
}
