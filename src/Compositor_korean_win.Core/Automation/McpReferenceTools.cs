using System.Globalization;
using System.Text;
using static Compositor_korean_win.Core.ToolSchema;

namespace Compositor_korean_win.Core;

/// <summary>
/// Working from a reference picture: carrying its pixels across exactly, drawing free shapes, and
/// measuring how close the result is — and the guide that says how to put them together.
/// </summary>
/// <remarks>
/// <para>
/// A model recreating a poster had a brush and nothing else for the reference's material, so it
/// laid 670,000 one-pixel strokes and reduced the colours to 128 to keep that many down. Neither
/// is what a brush is for. <c>put_pixels</c> writes the reference's pixels as they are, on the
/// layer the element belongs to, so the brush is left for touching up.
/// </para>
/// <para>
/// The shapes in such a poster — a rose, an eagle, the white gaps between petals — are cut-outs,
/// and <c>add_shape</c> only draws regular ones. <c>add_path</c> takes SVG path data, which every
/// model can write, and makes it a layer, a mask or a selection: the pen tool.
/// </para>
/// <para>
/// And a model called that attempt "a perfect restoration" without looking. <c>compare_image</c>
/// gives a number and the places that differ, so "finished" can be checked rather than claimed.
/// </para>
/// </remarks>
public sealed partial class McpTools
{
    private static readonly string[] GuideTopics = ["reproduce", "design", "retouch"];

    private void DefineReferenceTools()
    {
        Define("guide",
            "How to work in this editor: the general way without 'topic', and step by step for a kind of job — reproduce " +
            "(recreate a reference picture), design (make something new) or retouch (correct a photo). Read the topic " +
            "before starting that kind of job.",
            Build(Choice("topic", "The kind of job.", GuideTopics)),
            arguments => ToolResult.Text(Localizer.Text(arguments.String("topic") switch
            {
                null => TextKey.AiWorkflow,
                "reproduce" => TextKey.AiTopicReproduce,
                "design" => TextKey.AiTopicDesign,
                "retouch" => TextKey.AiTopicRetouch,
                string other => throw new ToolException($"'{other}' is not a topic: {string.Join(", ", GuideTopics)}."),
            })));

        Define("put_pixels",
            "Copy pixels exactly — every colour as it is, pixel for pixel at scale 1 — from a picture file (or base64) onto a " +
            "layer. This is how a reference's material comes across: one element per layer, its colours never reduced. " +
            "'region' picks the part of the picture (default all of it); x/y is where that part's top-left lands on the " +
            "canvas (default the region's own x/y, so a reference the canvas's size lines up). With 'layer' the pixels are " +
            "written into that layer (a blank one included), replacing what was there; without it they become a new layer. " +
            "within_selection keeps them to the selection — select the element first (select magic_wand, add_path " +
            "as=selection). For touching up use paint_stroke; for shapes and effects use masks, add_path, liquify and warp.",
            Build(With(Str("path", "The picture file."), Str("data", "The picture as base64, instead of 'path'."),
                       Object("region", "The part of the picture to copy, in its own pixels.",
                              Num("x", "Left.", true), Num("y", "Top.", true), Num("width", "Width.", true), Num("height", "Height.", true)),
                       Num("x", "Canvas left for the region's top-left."), Num("y", "Canvas top for the region's top-left."),
                       Num("scale", "Canvas pixels per picture pixel. Default 1: exact."),
                       Str("layer", "Write into this layer (id or name) instead of making a new one."),
                       Bool("within_selection", "Only where the current selection is."))),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                (PixelBuffer picture, string title) = Picture(arguments);
                try
                {
                    var whole = new PixelRect(0, 0, picture.Width, picture.Height);
                    PixelRect region = whole;
                    if (arguments.Object("region") is ToolArguments part)
                    {
                        int left = (int)Math.Floor(part.RequiredNumber("x")), top = (int)Math.Floor(part.RequiredNumber("y"));
                        region = new PixelRect(left, top, (int)Math.Round(part.RequiredNumber("width")), (int)Math.Round(part.RequiredNumber("height")))
                            .Intersect(whole);
                        if (region.IsEmpty) throw new ToolException("'region' is outside the picture.");
                    }
                    double scale = arguments.Number("scale", 1);
                    if (!(scale > 0) || scale > 64) throw new ToolException("'scale' is above 0 and at most 64.");
                    var target = new Rect(arguments.Number("x") ?? region.X, arguments.Number("y") ?? region.Y,
                                          region.Width * scale, region.Height * scale);
                    bool within = arguments.Bool("within_selection") == true;
                    if (within && open.Selection is null) throw new ToolException("within_selection needs a selection. Call select or add_path as=selection first.");

                    if (arguments.String("layer") is string reference)
                    {
                        ImageLayer layer = EditorSession.Layer(open.Document, reference);
                        _session.Edit(open, "Put Pixels", document =>
                            document.Replacing(Written(document, open, layer, picture, region, target, scale, within)));
                        ImageLayer after = open.Document.Layer(layer.Id)!;
                        return ToolResult.Text($"Wrote {region.Width}×{region.Height} pixels into '{after.Name}' {after.Id} at x {target.X:0.##}, y {target.Y:0.##}.");
                    }

                    var placement = new LayerTransform(new Point(target.X, target.Y), new Size(target.Width, target.Height));
                    PixelBuffer pixels = PixelRegion.Copy(picture, region);
                    if (within)
                    {
                        byte[] levels = CoverageOver(open, placement, pixels.Width, pixels.Height);
                        PixelBuffer kept = Scaled(pixels, levels, keep: true);
                        pixels.Release();
                        pixels = kept;
                    }
                    var made = new ImageLayer { Id = Guid.NewGuid(), Name = title, Image = pixels, Transform = placement };
                    return AddLayer(open, arguments, "Put Pixels", made, "pixels");
                }
                finally
                {
                    picture.Release();
                }
            });

        Define("add_path",
            "Draw a free shape, as the pen tool does, from SVG path data ('d': M L H V C S Q T A Z, absolute or relative, in " +
            "document pixels) or from 'points' for straight sides. 'as' says what it becomes: layer — a new layer filled " +
            "with 'fill' and outlined with 'stroke'; mask — the shape as the mask of 'layer', shown inside it (mode add, " +
            "subtract or intersect combines it with the mask the layer has; subtract cuts gaps); selection — combined with " +
            "the current selection by 'mode'. Silhouettes and cut-outs are masks drawn this way, not painted. Holes: draw " +
            "them in the opposite direction, or use fill_rule evenodd.",
            Build(With(Str("d", "SVG path data, e.g. \"M 100 100 C 150 40 250 40 300 100 L 200 300 Z\"."), PointsArgument,
                       Choice("as", "What the path becomes. Default layer.", ["layer", "mask", "selection"]),
                       Str("layer", "as=mask: the layer (or group) whose mask it is."),
                       Colour("fill", "as=layer: the inside. Default black; \"none\" for an outline only."),
                       Colour("stroke", "as=layer: the outline's colour."), Num("stroke_width", "as=layer: the outline's width."),
                       Choice("fill_rule", "Default nonzero.", ["nonzero", "evenodd"]),
                       Choice("mode", "mask or selection: how it combines with what is there. Default replace.", ["replace", "add", "subtract", "intersect"]),
                       Bool("invert", "as=mask: hide inside the shape instead."),
                       Num("feather", "mask or selection: soften the edge by this many pixels."))),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                List<SelectionLoop> loops = [.. PathFigures(arguments).Where(figure => figure.Points.Count >= 2)
                                                                     .Select(figure => new SelectionLoop(figure.Points))];
                if (loops.Count == 0) throw new ToolException("The path draws nothing: give 'd' or 'points' with at least two points.");
                bool evenOdd = arguments.String("fill_rule") == "evenodd";
                string mode = arguments.String("mode") ?? "replace";

                switch (arguments.String("as") ?? "layer")
                {
                    case "layer":
                        return AddLayer(open, arguments, "Path", PathLayer(loops, evenOdd, arguments), "path");

                    case "mask":
                    {
                        ImageLayer layer = EditorSession.Layer(open.Document, arguments.String("layer")
                                                               ?? throw new ToolException("as=mask needs 'layer': whose mask it is."));
                        _session.Edit(open, "Path Mask", document =>
                        {
                            ImageLayer masked = PathMask(document, layer, loops, evenOdd, mode, arguments.Bool("invert") == true);
                            if (arguments.Number("feather") is double feather && feather > 0) masked = SoftenedMask(masked, feather);
                            return document.Replacing(masked);
                        });
                        return ToolResult.Text($"'{layer.Name}' is masked by the path ({mode}).");
                    }

                    case "selection":
                    {
                        DocumentSelection made = evenOdd
                            ? MagicWand.Outline([.. OddEven(loops, selection => selection.Levels(new PixelRect(0, 0, open.Document.Width, open.Document.Height)))
                                                   .Select(level => level >= 128 ? (byte)1 : (byte)0)],
                                                open.Document.Width, open.Document.Height).Selection ?? DocumentSelection.Empty
                            : new DocumentSelection { Shapes = [new SelectionShape(SelectionOperation.Add, loops)] };
                        open.Selection = Combined(open, made, mode);
                        if (arguments.Number("feather") is double feather) open.Feather = Math.Clamp(feather, 0, 1000);
                        else if (mode == "replace") open.Feather = 0;
                        return ToolResult.Text(SelectionSummary(open));
                    }

                    default:
                        throw new ToolException("'as' is layer, mask or selection.");
                }
            });

        Define("compare_image",
            "Measure how close the document looks to a reference picture: a similarity score, where the differences are, " +
            "and an image of the document with the differences in red. The reference is stretched to the canvas if its " +
            "size differs; both are seen over white. Use it before calling work finished, and report the score and what " +
            "still differs.",
            Build(Str("path", "The reference picture."), Str("data", "The reference as base64, instead of 'path'."),
                  Object("region", "Only this part of the canvas.",
                         Num("x", "Left.", true), Num("y", "Top.", true), Num("width", "Width.", true), Num("height", "Height.", true)),
                  Int("threshold", "A pixel differs when a channel is off by more than this, 0–255. Default 24."),
                  Int("grid", "Cells across for locating differences, 2–32. Default 8."), DocumentArgument),
            arguments => Compared(Doc(arguments), arguments));
    }

    // ---- put_pixels ----------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="layer"/> with the picture written into it over <paramref name="target"/>:
    /// each layer pixel takes the picture pixel under its centre — nearest at scale 1, so colours
    /// are exact; bilinear when stretched — through the selection when <paramref name="within"/>.
    /// </summary>
    private static ImageLayer Written(CanvasDocument document, EditorSession.Open open, ImageLayer layer, PixelBuffer picture,
                                      PixelRect region, Rect target, double scale, bool within)
    {
        (PixelBuffer pixels, LayerTransform placement, bool made) = Editable(document, layer);
        try
        {
            int width = pixels.Width, height = pixels.Height;
            byte[]? levels = within ? SelectionOver(open, placement, width, height) : null;

            // Only the part of the layer the target can reach is visited.
            Point[] corners =
            [
                LayerGeometry.ToPixels(placement, new Point(target.MinX, target.MinY), width, height),
                LayerGeometry.ToPixels(placement, new Point(target.MaxX, target.MinY), width, height),
                LayerGeometry.ToPixels(placement, new Point(target.MaxX, target.MaxY), width, height),
                LayerGeometry.ToPixels(placement, new Point(target.MinX, target.MaxY), width, height),
            ];
            PixelRect reach = Rect.FromBounds(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y))
                .Enclosing().Inflate(1).Intersect(new PixelRect(0, 0, width, height));

            PixelBuffer result = PixelFilters.Copy(pixels);
            var sample = new byte[4];
            bool exact = scale == 1;
            for (int y = reach.Y; y < reach.Bottom; y++)
            {
                Span<byte> row = result.Row(y);
                for (int x = reach.X; x < reach.Right; x++)
                {
                    Point at = LayerGeometry.ToDocument(placement, new Point(x + 0.5, y + 0.5), width, height);
                    double u = (at.X - target.X) / scale + region.X, v = (at.Y - target.Y) / scale + region.Y;
                    if (u < region.X || v < region.Y || u >= region.Right || v >= region.Bottom) continue;

                    if (exact) picture.Row((int)Math.Floor(v)).Slice((int)Math.Floor(u) * 4, 4).CopyTo(sample);
                    else PixelSampling.Bilinear(picture, u, v, sample);

                    int level = levels is null ? 255 : levels[y * width + x];
                    if (level == 0) continue;
                    for (int c = 0; c < 4; c++)
                        row[x * 4 + c] = level == 255 ? sample[c] : (byte)((row[x * 4 + c] * (255 - level) + sample[c] * level + 127) / 255);
                }
            }
            return layer with { Image = result, Transform = placement, Shape = null };
        }
        finally
        {
            if (made) pixels.Release();
        }
    }

    // ---- add_path ------------------------------------------------------------------------------

    private static List<PathData.Figure> PathFigures(ToolArguments arguments)
    {
        if (arguments.String("d") is { Length: > 0 } d) return PathData.Parse(d);
        List<Point> points = Points(arguments, "points");
        return points.Count > 0 ? [new PathData.Figure(points, Closed: true)] : [];
    }

    /// <summary>
    /// Coverage where an odd number of the loops cover — how a hole drawn in the same direction
    /// still comes out a hole. Soft edges combine as an exclusive or of their levels.
    /// </summary>
    private static byte[] OddEven(List<SelectionLoop> loops, Func<DocumentSelection, byte[]> levelsOf)
    {
        byte[]? result = null;
        foreach (SelectionLoop loop in loops)
        {
            byte[] one = levelsOf(new DocumentSelection { Shapes = [new SelectionShape(SelectionOperation.Add, [loop])] });
            if (result is null) { result = one; continue; }
            for (int i = 0; i < result.Length; i++)
            {
                int a = result[i], b = one[i];
                result[i] = (byte)Math.Clamp(a + b - 2 * a * b / 255, 0, 255);
            }
        }
        return result ?? [];
    }

    private static byte[] PathCoverage(List<SelectionLoop> loops, bool evenOdd, Func<DocumentSelection, byte[]> levelsOf) =>
        evenOdd ? OddEven(loops, levelsOf) : levelsOf(new DocumentSelection { Shapes = [new SelectionShape(SelectionOperation.Add, loops)] });

    /// <summary>A layer just large enough for the path, filled and outlined.</summary>
    private static ImageLayer PathLayer(List<SelectionLoop> loops, bool evenOdd, ToolArguments arguments)
    {
        Rgba? fill = arguments.Colour("fill");
        Rgba? stroke = arguments.Colour("stroke");
        double strokeWidth = Math.Max(0, arguments.Number("stroke_width") ?? (stroke is { A: > 0 } ? 2 : 0));
        bool fills = fill is not { A: 0 };
        bool strokes = stroke is { A: > 0 } && strokeWidth > 0;
        if (!fills && !strokes) throw new ToolException("With fill \"none\" give a 'stroke' colour and 'stroke_width', or nothing is drawn.");

        var bounds = Rect.Around([.. loops.SelectMany(loop => loop.Points)]);
        PixelRect box = bounds.Inflate(strokes ? strokeWidth / 2 + 2 : 1).Enclosing();
        if (box.IsEmpty) box = new PixelRect(box.X, box.Y, Math.Max(1, box.Width), Math.Max(1, box.Height));
        if (box.Width > ProjectLimits.MaximumSide || box.Height > ProjectLimits.MaximumSide) throw new ToolException("That path is too large.");

        PixelBuffer pixels = PixelBuffer.Allocate(box.Width, box.Height);
        var local = new PixelRect(0, 0, box.Width, box.Height);
        if (fills)
        {
            byte[] levels = PathCoverage([.. loops.Where(loop => !loop.IsDegenerate)], evenOdd, selection => selection.Levels(box));
            if (levels.Length == local.Width * local.Height) Painting.Fill(pixels, local, levels, fill ?? Rgba.Black);
        }

        if (strokes)
        {
            // The outline is the brush dragged round the path, hard-edged, the way a stroked path is.
            var settings = new BrushSettings { Diameter = strokeWidth, Hardness = 1, Color = stroke ?? Rgba.Black, Mode = BrushMode.Paint };
            List<List<Point>> lines = [.. loops.Select(loop =>
            {
                List<Point> line = [.. loop.Points.Select(point => new Point(point.X - box.X, point.Y - box.Y))];
                if (line.Count > 2) line.Add(line[0]);
                return line;
            })];
            if (Stroked(pixels, settings, lines, selection: null) is PixelBuffer outlined)
            {
                pixels.Release();
                pixels = outlined;
            }
        }

        return new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = "Path",
            Image = pixels,
            Transform = new LayerTransform(new Point(box.X, box.Y), new Size(box.Width, box.Height)),
        };
    }

    /// <summary>The layer with the path as its mask, alone or combined with the mask it has.</summary>
    private static ImageLayer PathMask(CanvasDocument document, ImageLayer layer, List<SelectionLoop> loops, bool evenOdd,
                                       string mode, bool invert)
    {
        (int width, int height) = Grid(document, layer);
        var grid = new PixelRect(0, 0, width, height);
        byte[] levels = PathCoverage(loops, evenOdd, selection => LayerFilters.SelectionLevels(selection, layer.Transform, width, height, 1, grid));
        if (invert) for (int i = 0; i < levels.Length; i++) levels[i] = (byte)(255 - levels[i]);

        if (mode != "replace")
        {
            if (layer.Mask is not LayerMask existing || MaskEditing.Canvas(layer) is not (PixelBuffer working, _))
                throw new ToolException($"'{layer.Name}' has no mask to {mode} with; use mode replace.");
            try
            {
                if (existing.Placement is not null || working.Width != width || working.Height != height)
                    throw new ToolException($"'{layer.Name}''s mask is placed apart from it; use mode replace.");
                for (int y = 0; y < height; y++)
                {
                    Span<byte> row = working.Row(y);
                    for (int x = 0; x < width; x++)
                    {
                        int was = row[x * 4], shape = levels[y * width + x];
                        levels[y * width + x] = (byte)(mode switch
                        {
                            "add" => Math.Max(was, shape),
                            "subtract" => Math.Min(was, 255 - shape),
                            "intersect" => Math.Min(was, shape),
                            _ => throw new ToolException("'mode' is replace, add, subtract or intersect."),
                        });
                    }
                }
            }
            finally
            {
                working.Release();
            }
        }

        PixelBuffer coverage = Coverage(width, height, i => levels[i]);
        return layer with
        {
            Mask = layer.Mask is LayerMask kept ? kept with { Coverage = coverage, Placement = null } : new LayerMask { Coverage = coverage },
        };
    }

    // ---- compare_image -------------------------------------------------------------------------

    private ToolResult Compared(EditorSession.Open open, ToolArguments arguments)
    {
        CanvasDocument document = open.Document;
        (PixelBuffer picture, _) = Picture(arguments);
        PixelBuffer reference;
        bool stretched = picture.Width != document.Width || picture.Height != document.Height;
        string sizes = $"reference {picture.Width}×{picture.Height}";
        try
        {
            reference = stretched ? Stretched(picture, document.Width, document.Height) : PixelFilters.Copy(picture);
        }
        finally
        {
            picture.Release();
        }

        using (reference)
        using (PixelBuffer shown = Flatten(document))
        {
            Backdrop(reference, "white");
            Backdrop(shown, "white");

            var area = new PixelRect(0, 0, document.Width, document.Height);
            if (arguments.Object("region") is ToolArguments region)
            {
                int x = (int)Math.Floor(region.RequiredNumber("x")), y = (int)Math.Floor(region.RequiredNumber("y"));
                area = new PixelRect(x, y, (int)Math.Ceiling(region.RequiredNumber("width")), (int)Math.Ceiling(region.RequiredNumber("height"))).Intersect(area);
                if (area.IsEmpty) throw new ToolException("'region' is outside the canvas.");
            }

            int threshold = Math.Clamp(arguments.Int("threshold") ?? 24, 0, 255);
            int columns = Math.Clamp(arguments.Int("grid") ?? 8, 2, 32);
            int rows = Math.Clamp((int)Math.Round((double)columns * area.Height / area.Width), 1, 64);
            var cellSum = new double[columns * rows];
            var cellCount = new int[columns * rows];

            double total = 0;
            long differing = 0;
            using PixelBuffer marked = PixelBuffer.Allocate(area.Width, area.Height);
            for (int y = 0; y < area.Height; y++)
            {
                ReadOnlySpan<byte> mine = shown.Row(area.Y + y), theirs = reference.Row(area.Y + y);
                Span<byte> row = marked.Row(y);
                int cellRow = Math.Min(rows - 1, y * rows / area.Height);
                for (int x = 0; x < area.Width; x++)
                {
                    int i = (area.X + x) * 4;
                    int dr = Math.Abs(mine[i] - theirs[i]), dg = Math.Abs(mine[i + 1] - theirs[i + 1]), db = Math.Abs(mine[i + 2] - theirs[i + 2]);
                    double mean = (dr + dg + db) / 3.0;
                    int most = Math.Max(dr, Math.Max(dg, db));
                    total += mean;
                    if (most > threshold) differing++;
                    int cell = cellRow * columns + Math.Min(columns - 1, x * columns / area.Width);
                    cellSum[cell] += mean;
                    cellCount[cell]++;

                    // The document faded to grey, reddened by how far it is from the reference.
                    int grey = (int)(0.2126 * mine[i] + 0.7152 * mine[i + 1] + 0.0722 * mine[i + 2]);
                    int faded = 128 + grey / 2;
                    double red = Math.Min(1, most / 64.0);
                    row[x * 4] = (byte)(faded + (255 - faded) * red);
                    row[x * 4 + 1] = (byte)(faded * (1 - red));
                    row[x * 4 + 2] = (byte)(faded * (1 - red));
                    row[x * 4 + 3] = 255;
                }
            }

            long count = (long)area.Width * area.Height;
            double average = total / count;
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"Similarity {100 * (1 - average / 255):0.0}% (mean difference {average:0.0} of 255 per channel); ");
            text.Append(CultureInfo.InvariantCulture, $"{100.0 * differing / count:0.0}% of pixels differ by more than {threshold}.");
            text.Append(stretched ? $" The {sizes} was stretched to the canvas {document.Width}×{document.Height}." : "");
            text.AppendLine();
            text.AppendLine("Most different regions (x, y, width×height: mean difference):");
            int cellWidth = (int)Math.Ceiling((double)area.Width / columns), cellHeight = (int)Math.Ceiling((double)area.Height / rows);
            foreach (int cell in Enumerable.Range(0, cellSum.Length).Where(cell => cellCount[cell] > 0)
                         .OrderByDescending(cell => cellSum[cell] / cellCount[cell]).Take(6))
            {
                int column = cell % columns, row = cell / columns;
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"- {area.X + column * area.Width / columns}, {area.Y + row * area.Height / rows}, {cellWidth}×{cellHeight}: {cellSum[cell] / cellCount[cell]:0.0}");
            }
            text.Append("Look closer with render region=… zoom=…, fix, and compare again.");

            double scale = Math.Min(1, 1024.0 / Math.Max(area.Width, area.Height));
            using PixelBuffer small = Downscaled(marked, Math.Max(1, (int)Math.Round(area.Width * scale)), Math.Max(1, (int)Math.Round(area.Height * scale)));
            var result = new ToolResult();
            result.Content.Add(ToolContent.Of(text.ToString()));
            result.Content.Add(ToolContent.Png(Png.Encode(small)));
            return result;
        }
    }

    /// <summary>A picture stretched to a size, bilinearly. The caller owns the result.</summary>
    private static PixelBuffer Stretched(PixelBuffer picture, int width, int height)
    {
        PixelBuffer result = PixelBuffer.Allocate(width, height);
        var sample = new byte[4];
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = result.Row(y);
            double v = (y + 0.5) * picture.Height / height;
            for (int x = 0; x < width; x++)
            {
                PixelSampling.Bilinear(picture, (x + 0.5) * picture.Width / width, v, sample);
                sample.CopyTo(row.Slice(x * 4, 4));
            }
        }
        return result;
    }
}

/// <summary>
/// SVG path data — the <c>d</c> of a <c>&lt;path&gt;</c> — flattened into straight-sided figures in
/// document pixels.
/// </summary>
/// <remarks>
/// Every model can write SVG paths, which is why a path is taken in this form rather than as a new
/// list of anchors and handles. Curves are cut into segments of about three pixels, finer than a
/// selection's rasteriser can tell apart.
/// </remarks>
public static class PathData
{
    public sealed record Figure(List<Point> Points, bool Closed);

    public static List<Figure> Parse(string d)
    {
        var figures = new List<Figure>();
        var reader = new Reader(d);
        List<Point>? points = null;
        Point pen = Point.Zero, start = Point.Zero, control = Point.Zero;
        char command = '\0', previous = '\0';

        void Finish(bool closed)
        {
            if (points is { Count: > 0 }) figures.Add(new Figure(points, closed));
            points = null;
        }

        void To(Point point)
        {
            if (points is null) points = [pen];
            points.Add(point);
            pen = point;
        }

        while (true)
        {
            reader.Skip();
            if (!reader.More) break;
            if (reader.Letter() is char letter) command = letter;
            else if (command == '\0') throw new ToolException("Path data starts with a command, such as M.");
            else if (command is 'M') command = 'L';
            else if (command is 'm') command = 'l';
            else if (command is 'Z' or 'z') throw new ToolException("Numbers after Z need a command of their own.");

            bool relative = char.IsLower(command);
            Point Read() => relative ? new Point(pen.X + reader.Number(), pen.Y + reader.Number()) : new Point(reader.Number(), reader.Number());

            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                {
                    Finish(closed: false);
                    pen = Read();
                    start = pen;
                    points = [pen];
                    break;
                }
                case 'L':
                    To(Read());
                    break;
                case 'H':
                {
                    double x = reader.Number();
                    To(new Point(relative ? pen.X + x : x, pen.Y));
                    break;
                }
                case 'V':
                {
                    double y = reader.Number();
                    To(new Point(pen.X, relative ? pen.Y + y : y));
                    break;
                }
                case 'C':
                case 'S':
                {
                    Point first = char.ToUpperInvariant(command) == 'C'
                        ? Read()
                        : char.ToUpperInvariant(previous) is 'C' or 'S' ? new Point(2 * pen.X - control.X, 2 * pen.Y - control.Y) : pen;
                    Point second = Read(), end = Read();
                    Cubic(pen, first, second, end, To);
                    control = second;
                    break;
                }
                case 'Q':
                case 'T':
                {
                    Point handle = char.ToUpperInvariant(command) == 'Q'
                        ? Read()
                        : char.ToUpperInvariant(previous) is 'Q' or 'T' ? new Point(2 * pen.X - control.X, 2 * pen.Y - control.Y) : pen;
                    Point end = Read();
                    Point from = pen;
                    // A quadratic is the cubic whose handles sit two thirds of the way to its one.
                    Cubic(from, new Point(from.X + 2.0 / 3 * (handle.X - from.X), from.Y + 2.0 / 3 * (handle.Y - from.Y)),
                          new Point(end.X + 2.0 / 3 * (handle.X - end.X), end.Y + 2.0 / 3 * (handle.Y - end.Y)), end, To);
                    control = handle;
                    break;
                }
                case 'A':
                {
                    double rx = reader.Number(), ry = reader.Number(), rotation = reader.Number();
                    bool large = reader.Flag(), sweep = reader.Flag();
                    Point end = Read();
                    Arc(pen, rx, ry, rotation, large, sweep, end, To);
                    break;
                }
                case 'Z':
                    Finish(closed: true);
                    pen = start;
                    break;
                default:
                    throw new ToolException($"'{command}' is not a path command (M L H V C S Q T A Z).");
            }
            previous = command;
        }

        Finish(closed: false);
        return figures;
    }

    private static void Cubic(Point p0, Point p1, Point p2, Point p3, Action<Point> to)
    {
        double length = Distance(p0, p1) + Distance(p1, p2) + Distance(p2, p3);
        int steps = Math.Clamp((int)Math.Ceiling(length / 3), 2, 256);
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps, s = 1 - t;
            double a = s * s * s, b = 3 * s * s * t, c = 3 * s * t * t, e = t * t * t;
            to(new Point(a * p0.X + b * p1.X + c * p2.X + e * p3.X, a * p0.Y + b * p1.Y + c * p2.Y + e * p3.Y));
        }
    }

    /// <summary>An elliptical arc, by the SVG specification's conversion to centre form (F.6.5).</summary>
    private static void Arc(Point from, double rx, double ry, double rotation, bool large, bool sweep, Point end, Action<Point> to)
    {
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx == 0 || ry == 0 || (from.X == end.X && from.Y == end.Y))
        {
            to(end);
            return;
        }

        double phi = rotation * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (from.X - end.X) / 2, dy = (from.Y - end.Y) / 2;
        double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
        double lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1)
        {
            rx *= Math.Sqrt(lambda);
            ry *= Math.Sqrt(lambda);
        }

        double numerator = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        double denominator = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        double coefficient = Math.Sqrt(Math.Max(0, numerator / denominator)) * (large == sweep ? -1 : 1);
        double cx1 = coefficient * rx * y1 / ry, cy1 = -coefficient * ry * x1 / rx;
        double cx = cos * cx1 - sin * cy1 + (from.X + end.X) / 2, cy = sin * cx1 + cos * cy1 + (from.Y + end.Y) / 2;

        double start = Angle(1, 0, (x1 - cx1) / rx, (y1 - cy1) / ry);
        double delta = Angle((x1 - cx1) / rx, (y1 - cy1) / ry, (-x1 - cx1) / rx, (-y1 - cy1) / ry);
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        else if (sweep && delta < 0) delta += 2 * Math.PI;

        int steps = Math.Clamp((int)Math.Ceiling(Math.Abs(delta) * Math.Max(rx, ry) / 3), 4, 256);
        for (int i = 1; i < steps; i++)
        {
            double angle = start + delta * i / steps;
            double ex = rx * Math.Cos(angle), ey = ry * Math.Sin(angle);
            to(new Point(cx + cos * ex - sin * ey, cy + sin * ex + cos * ey));
        }
        to(end);

        static double Angle(double ux, double uy, double vx, double vy) => Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Numbers and commands as SVG writes them: "M10-5.5.5e2", commas optional, flags unspaced.</summary>
    private sealed class Reader(string text)
    {
        private int _at;

        public bool More => _at < text.Length;

        public void Skip()
        {
            while (_at < text.Length && (char.IsWhiteSpace(text[_at]) || text[_at] == ',')) _at++;
        }

        public char? Letter()
        {
            Skip();
            if (_at < text.Length && char.IsAsciiLetter(text[_at]) && text[_at] is not ('e' or 'E')) return text[_at++];
            return null;
        }

        public bool Flag()
        {
            Skip();
            if (_at < text.Length && text[_at] is '0' or '1') return text[_at++] == '1';
            throw new ToolException("An arc's two flags are 0 or 1.");
        }

        public double Number()
        {
            Skip();
            int begin = _at;
            if (_at < text.Length && text[_at] is '+' or '-') _at++;
            bool dot = false, digits = false;
            while (_at < text.Length)
            {
                char c = text[_at];
                if (char.IsAsciiDigit(c)) digits = true;
                else if (c == '.' && !dot) dot = true;
                else break;
                _at++;
            }
            if (digits && _at < text.Length && text[_at] is 'e' or 'E')
            {
                int mark = _at++;
                if (_at < text.Length && text[_at] is '+' or '-') _at++;
                if (_at < text.Length && char.IsAsciiDigit(text[_at])) while (_at < text.Length && char.IsAsciiDigit(text[_at])) _at++;
                else _at = mark;
            }
            if (!digits || !double.TryParse(text.AsSpan(begin, _at - begin), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new ToolException($"Path data has something that is not a number at character {begin}: \"{Near(begin)}\".");
            return value;
        }

        private string Near(int at) => text.Substring(at, Math.Min(12, text.Length - at));
    }
}
