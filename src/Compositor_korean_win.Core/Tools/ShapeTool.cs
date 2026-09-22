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
/// Rectangles, rounded rectangles and ellipses, filled onto a layer.
/// </summary>
/// <remarks>
/// A shape is an outline and a fill, which is what a selection already is, so this builds the
/// outline as a <see cref="DocumentSelection"/> and hands it to the same scanline rasteriser. That
/// is not a shortcut: it means a shape's edge and a selection's edge are antialiased by the same
/// code, so a circle drawn with the shape tool and one selected with the ellipse marquee agree on
/// where the circle is.
/// </remarks>
public static class ShapeTool
{
    /// <summary>The shape's outline, in layer pixels.</summary>
    public static DocumentSelection Outline(Rect box, ShapeSettings settings) => settings.Kind switch
    {
        ShapeKind.Ellipse => DocumentSelection.Ellipse(box),
        _ when settings.CornerRadius > 0 => Rounded(box, settings.CornerRadius),
        _ => DocumentSelection.Rectangle(box),
    };

    /// <summary>
    /// <paramref name="target"/> with the shape drawn on it.
    /// </summary>
    /// <remarks>The caller owns the result and releases it.</remarks>
    public static PixelBuffer Draw(PixelBuffer? target, int width, int height, Rect box,
                                   ShapeSettings settings, DocumentSelection? selection = null)
    {
        PixelBuffer result = target is PixelBuffer source
            ? PixelRegion.Copy(source, new PixelRect(0, 0, width, height))
            : PixelBuffer.Allocate(width, height);

        DocumentSelection outline = Outline(box, settings);
        PixelRect region = box.Inflate(1).Enclosing().Intersect(new PixelRect(0, 0, width, height));
        if (region.IsEmpty) return result;

        byte[] coverage = Painting.CoverageFor(outline, region, selection);
        Painting.Fill(result, region, coverage, settings.Color, settings.Opacity);
        return result;
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
