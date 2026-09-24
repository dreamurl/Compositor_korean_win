namespace Compositor_korean_win.Core;

/// <summary>
/// What to draw and in what colour.
/// </summary>
/// <remarks>
/// The kind is the format's own <see cref="ShapeKind"/>, which has two cases and not three: a
/// rounded rectangle is a rectangle with a corner radius, exactly as <c>.comp</c> stores it. That
/// is what lets a shape layer be redrawn at a new size from what was saved
/// (<see cref="LayerShapeStyle"/>) rather than from a second vocabulary.
/// </remarks>
public sealed record ShapeSettings
{
    public ShapeKind Kind { get; init; } = ShapeKind.Rectangle;

    public Rgba Color { get; init; } = Rgba.Black;

    public double Opacity { get; init; } = 1;

    /// <summary>Corner radius in layer pixels; 0 is a square corner.</summary>
    public double CornerRadius { get; init; }

    /// <summary>A polygon's sides, or a star's points: 3 to 64.</summary>
    public int Sides { get; init; } = 5;

    /// <summary>A star's inner radius as a fraction of its outer one, 0.05 to 0.95.</summary>
    public double Inset { get; init; } = 0.5;

    /// <summary>
    /// A star whose sides curve inwards between its points — the four-point sparkle of poster
    /// design — rather than meeting at straight corners.
    /// </summary>
    public bool Curved { get; init; }

    /// <summary>Whether the inside is filled with <see cref="Color"/>. A line is always drawn.</summary>
    public bool Filled { get; init; } = true;

    /// <summary>Outline width in layer pixels, centred on the edge; 0 draws none.</summary>
    public double StrokeWidth { get; init; }

    public Rgba StrokeColor { get; init; } = Rgba.White;

    /// <summary>A line's thickness in layer pixels.</summary>
    public double LineWidth { get; init; } = 4;

    public const int MinimumSides = 3, MaximumSides = 64;

    /// <summary>What the format stores for a shape layer.</summary>
    public LayerShapeStyle ToStyle() => new()
    {
        Kind = Kind,
        Red = Color.R / 255.0,
        Green = Color.G / 255.0,
        Blue = Color.B / 255.0,
        CornerRadius = CornerRadius,
    };

    /// <summary>The settings that would draw a saved shape layer again.</summary>
    public static ShapeSettings From(LayerShapeStyle style) => new()
    {
        Kind = style.Kind,
        CornerRadius = style.CornerRadius,
        Color = new Rgba(Channel(style.Red), Channel(style.Green), Channel(style.Blue)),
    };

    private static byte Channel(double value) =>
        (byte)Math.Clamp(Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
}

/// <summary>
/// Rectangles, rounded rectangles, ellipses, polygons, stars and lines, filled or outlined onto a
/// layer.
/// </summary>
/// <remarks>
/// A shape is an outline and a fill, which is what a selection already is, so this builds the
/// outline as a <see cref="DocumentSelection"/> and hands it to the same scanline rasteriser. That
/// is not a shortcut: it means a shape's edge and a selection's edge are antialiased by the same
/// code, so a circle drawn with the shape tool and one selected with the ellipse marquee agree on
/// where the circle is. An outline or a line is a band round a path instead, whose coverage comes
/// from each pixel's distance to it (<see cref="Stroke"/>).
/// </remarks>
public static class ShapeTool
{
    /// <summary>The shape's outline, in layer pixels.</summary>
    public static DocumentSelection Outline(Rect box, ShapeSettings settings) => settings.Kind switch
    {
        ShapeKind.Ellipse => DocumentSelection.Ellipse(box),
        ShapeKind.Polygon => Loop(Polygon(box, settings.Sides)),
        ShapeKind.Star => Loop(Star(box, settings.Sides, settings.Inset, settings.Curved)),
        ShapeKind.Line => Loop(LineBand(new Point(box.MinX, box.MinY), new Point(box.MaxX, box.MaxY), settings.LineWidth)),
        _ when settings.CornerRadius > 0 => Rounded(box, settings.CornerRadius),
        _ => DocumentSelection.Rectangle(box),
    };

    /// <summary>
    /// <paramref name="target"/> with the shape drawn on it.
    /// </summary>
    /// <remarks>The caller owns the result and releases it.</remarks>
    public static PixelBuffer Draw(PixelBuffer? target, int width, int height, Rect box,
                                   ShapeSettings settings, DocumentSelection? selection = null) =>
        Draw(target, width, height, new Point(box.MinX, box.MinY), new Point(box.MaxX, box.MaxY), settings, selection);

    /// <summary>
    /// <paramref name="target"/> with the shape drawn between two corners — or, for a line, from
    /// <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    /// <remarks>The caller owns the result and releases it.</remarks>
    public static PixelBuffer Draw(PixelBuffer? target, int width, int height, Point from, Point to,
                                   ShapeSettings settings, DocumentSelection? selection = null)
    {
        PixelBuffer result = target is PixelBuffer source
            ? PixelRegion.Copy(source, new PixelRect(0, 0, width, height))
            : PixelBuffer.Allocate(width, height);

        var canvas = new PixelRect(0, 0, width, height);
        var box = Rect.FromBounds(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y),
                                  Math.Max(from.X, to.X), Math.Max(from.Y, to.Y));

        if (settings.Kind == ShapeKind.Line)
        {
            double half = Math.Max(0.5, settings.LineWidth / 2);
            PixelRect lineRegion = box.Inflate(half + 1).Enclosing().Intersect(canvas);
            if (lineRegion.IsEmpty) return result;
            byte[] line = Stroke([from, to], closed: false, settings.LineWidth, lineRegion);
            Restrict(line, lineRegion, selection);
            Painting.Fill(result, lineRegion, line, settings.Color, settings.Opacity);
            return result;
        }

        DocumentSelection outline = Outline(box, settings);
        if (settings.Filled)
        {
            PixelRect region = box.Inflate(1).Enclosing().Intersect(canvas);
            if (!region.IsEmpty)
            {
                byte[] coverage = Painting.CoverageFor(outline, region, selection);
                Painting.Fill(result, region, coverage, settings.Color, settings.Opacity);
            }
        }

        if (settings.StrokeWidth > 0)
        {
            PixelRect region = box.Inflate(settings.StrokeWidth / 2 + 1).Enclosing().Intersect(canvas);
            if (!region.IsEmpty)
            {
                var band = new byte[region.Width * region.Height];
                foreach (SelectionShape shape in outline.Shapes)
                    foreach (SelectionLoop loop in shape.Loops)
                    {
                        byte[] edge = Stroke(loop.Points, closed: true, settings.StrokeWidth, region);
                        for (int i = 0; i < band.Length; i++) band[i] = Math.Max(band[i], edge[i]);
                    }
                Restrict(band, region, selection);
                Painting.Fill(result, region, band, settings.StrokeColor, settings.Opacity);
            }
        }

        return result;
    }

    private static void Restrict(byte[] coverage, PixelRect region, DocumentSelection? selection)
    {
        if (selection is null) return;
        byte[] allowed = selection.Levels(region);
        for (int i = 0; i < coverage.Length; i++) coverage[i] = (byte)(coverage[i] * allowed[i] / 255);
    }

    private static DocumentSelection Loop(List<Point> points) => new()
    {
        Shapes = [new SelectionShape(SelectionOperation.Add, [new SelectionLoop(points)])],
    };

    /// <summary>A regular polygon inscribed in the box's ellipse, a vertex straight up.</summary>
    public static List<Point> Polygon(Rect box, int sides)
    {
        sides = Math.Clamp(sides, ShapeSettings.MinimumSides, ShapeSettings.MaximumSides);
        var points = new List<Point>(sides);
        for (int i = 0; i < sides; i++)
        {
            double angle = -Math.PI / 2 + 2 * Math.PI * i / sides;
            points.Add(new Point(box.MidX + Math.Cos(angle) * box.Width / 2, box.MidY + Math.Sin(angle) * box.Height / 2));
        }
        return points;
    }

    /// <summary>
    /// A star of <paramref name="points"/> points in the box's ellipse, a point straight up, its
    /// inner corners at <paramref name="inset"/> of the way out.
    /// </summary>
    /// <remarks>
    /// Curved, each side is a quadratic Bézier from one point to the next with the inner corner as
    /// its control point, so the sides sweep inwards in one curve — the shape a four-point sparkle
    /// is drawn with — and never reach the inner corner itself.
    /// </remarks>
    public static List<Point> Star(Rect box, int points, double inset, bool curved)
    {
        points = Math.Clamp(points, ShapeSettings.MinimumSides, ShapeSettings.MaximumSides);
        inset = Math.Clamp(inset, 0.05, 0.95);
        double rx = box.Width / 2, ry = box.Height / 2;

        Point At(double angle, double scale) =>
            new(box.MidX + Math.Cos(angle) * rx * scale, box.MidY + Math.Sin(angle) * ry * scale);

        var result = new List<Point>(points * (curved ? 24 : 2));
        for (int i = 0; i < points; i++)
        {
            double outer = -Math.PI / 2 + 2 * Math.PI * i / points;
            double inner = outer + Math.PI / points;
            Point tip = At(outer, 1), corner = At(inner, inset);

            if (!curved)
            {
                result.Add(tip);
                result.Add(corner);
                continue;
            }

            Point next = At(outer + 2 * Math.PI / points, 1);
            const int Steps = 24;
            for (int k = 0; k < Steps; k++)
            {
                double t = (double)k / Steps, s = 1 - t;
                result.Add(new Point(s * s * tip.X + 2 * s * t * corner.X + t * t * next.X,
                                     s * s * tip.Y + 2 * s * t * corner.Y + t * t * next.Y));
            }
        }
        return result;
    }

    /// <summary>A line as the rectangle it fills, for previewing its outline.</summary>
    private static List<Point> LineBand(Point from, Point to, double width)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-9) return [from, from, from];
        double nx = -dy / length * width / 2, ny = dx / length * width / 2;
        return
        [
            new Point(from.X + nx, from.Y + ny), new Point(to.X + nx, to.Y + ny),
            new Point(to.X - nx, to.Y - ny), new Point(from.X - nx, from.Y - ny),
        ];
    }

    /// <summary>
    /// Coverage of a band <paramref name="width"/> wide centred on a path, with round joins and
    /// ends, over <paramref name="region"/>.
    /// </summary>
    /// <remarks>
    /// Each segment only visits the pixels near it, and a pixel keeps the most any segment gives
    /// it: the distance from the pixel's centre to the segment, against half the width, with half
    /// a pixel either side for the soft edge.
    /// </remarks>
    public static byte[] Stroke(IReadOnlyList<Point> path, bool closed, double width, PixelRect region)
    {
        var coverage = new byte[Math.Max(0, region.Width * region.Height)];
        if (coverage.Length == 0 || path.Count < 2 || !(width > 0)) return coverage;

        double half = width / 2;
        int segments = closed ? path.Count : path.Count - 1;
        for (int i = 0; i < segments; i++)
        {
            Point a = path[i], b = path[(i + 1) % path.Count];
            int left = Math.Max(region.X, (int)Math.Floor(Math.Min(a.X, b.X) - half - 1));
            int right = Math.Min(region.Right, (int)Math.Ceiling(Math.Max(a.X, b.X) + half + 1));
            int top = Math.Max(region.Y, (int)Math.Floor(Math.Min(a.Y, b.Y) - half - 1));
            int bottom = Math.Min(region.Bottom, (int)Math.Ceiling(Math.Max(a.Y, b.Y) + half + 1));

            double dx = b.X - a.X, dy = b.Y - a.Y, lengthSquared = dx * dx + dy * dy;
            for (int y = top; y < bottom; y++)
            {
                for (int x = left; x < right; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double t = lengthSquared > 0 ? Math.Clamp(((px - a.X) * dx + (py - a.Y) * dy) / lengthSquared, 0, 1) : 0;
                    double ex = a.X + t * dx - px, ey = a.Y + t * dy - py;
                    double distance = Math.Sqrt(ex * ex + ey * ey);
                    double level = Math.Clamp(half - distance + 0.5, 0, 1);
                    if (level <= 0) continue;

                    int index = (y - region.Y) * region.Width + (x - region.X);
                    byte value = (byte)Math.Round(level * 255, MidpointRounding.AwayFromZero);
                    if (value > coverage[index]) coverage[index] = value;
                }
            }
        }
        return coverage;
    }

    /// <summary>
    /// A rectangle with rounded corners, as one outline.
    /// </summary>
    /// <remarks>
    /// The radius is held to half the shorter side: a larger one has no meaning — the corners would
    /// pass through each other — and Photoshop clamps it the same way.
    /// </remarks>
    public static DocumentSelection Rounded(Rect box, double radius)
    {
        double limit = Math.Min(box.Width, box.Height) / 2;
        radius = Math.Clamp(radius, 0, Math.Max(0, limit));

        if (radius <= 0) return DocumentSelection.Rectangle(box);

        // Enough segments that a corner reads as a curve at the zooms a shape is drawn at.
        const int PerCorner = 16;
        var points = new List<Point>((PerCorner + 1) * 4);

        // Clockwise from the top-left corner, matching every other outline in the port.
        AddCorner(new Point(box.MinX + radius, box.MinY + radius), Math.PI, 1.5 * Math.PI);
        AddCorner(new Point(box.MaxX - radius, box.MinY + radius), 1.5 * Math.PI, 2 * Math.PI);
        AddCorner(new Point(box.MaxX - radius, box.MaxY - radius), 0, 0.5 * Math.PI);
        AddCorner(new Point(box.MinX + radius, box.MaxY - radius), 0.5 * Math.PI, Math.PI);

        return new DocumentSelection
        {
            Shapes = [new SelectionShape(SelectionOperation.Add, [new SelectionLoop(points)])],
        };

        void AddCorner(Point centre, double from, double to)
        {
            for (int i = 0; i <= PerCorner; i++)
            {
                double angle = from + (to - from) * i / PerCorner;
                points.Add(new Point(centre.X + Math.Cos(angle) * radius,
                                     centre.Y + Math.Sin(angle) * radius));
            }
        }
    }
}
