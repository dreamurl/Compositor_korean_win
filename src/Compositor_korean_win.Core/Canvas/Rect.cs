namespace Compositor_korean_win.Core;

/// <summary>An upright rectangle in real coordinates — view points or document pixels.</summary>
/// <remarks>
/// <see cref="PixelRect"/> covers whole pixels and is what the render path addresses memory with;
/// this one carries the fractions that a zoom, a pan and a rotated layer's bounding box produce,
/// and it is only ever turned into whole pixels at the edge of the drawing code.
/// </remarks>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public Rect(Point origin, Size size) : this(origin.X, origin.Y, size.Width, size.Height) { }

    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;

    public Point Origin => new(X, Y);
    public Size Size => new(Width, Height);
    public bool IsEmpty => !(Width > 0) || !(Height > 0);

    public static Rect FromBounds(double left, double top, double right, double bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    /// <summary>The smallest rectangle holding every one of <paramref name="points"/>.</summary>
    /// <remarks>
    /// This is how a rotated layer takes part in snapping: what lines up with a guide is the
    /// upright box around its four corners, not its own turned edges.
    /// </remarks>
    public static Rect Around(IReadOnlyList<Point> points)
    {
        if (points.Count == 0) return default;

        double left = points[0].X, right = left, top = points[0].Y, bottom = top;
        for (int i = 1; i < points.Count; i++)
        {
            left = Math.Min(left, points[i].X);
            right = Math.Max(right, points[i].X);
            top = Math.Min(top, points[i].Y);
            bottom = Math.Max(bottom, points[i].Y);
        }
        return FromBounds(left, top, right, bottom);
    }

    public Rect Inflate(double margin) =>
        new(X - margin, Y - margin, Width + margin * 2, Height + margin * 2);

    public Rect Intersect(Rect other) => FromBounds(
        Math.Max(MinX, other.MinX), Math.Max(MinY, other.MinY),
        Math.Min(MaxX, other.MaxX), Math.Min(MaxY, other.MaxY));

    public Rect OffsetBy(double dx, double dy) => new(X + dx, Y + dy, Width, Height);

    public bool Contains(Point point) =>
        point.X >= MinX && point.X <= MaxX && point.Y >= MinY && point.Y <= MaxY;

    /// <summary>Whole pixels covering this rectangle, rounded outwards.</summary>
    public PixelRect Enclosing() => IsEmpty
        ? default
        : PixelRect.FromBounds((int)Math.Floor(MinX), (int)Math.Floor(MinY),
                               (int)Math.Ceiling(MaxX), (int)Math.Ceiling(MaxY));

    /// <summary>Whole pixels nearest this rectangle's edges.</summary>
    /// <remarks>
    /// What a clip rounds to. Rounding rather than enclosing is what lets two backends agree on
    /// where a clip's edge falls: Direct2D's aliased clip rounds, and a rectangle that covers a
    /// document at a fractional zoom would otherwise land a pixel apart in the two.
    /// </remarks>
    public PixelRect Rounded() => IsEmpty
        ? default
        : PixelRect.FromBounds(Round(MinX), Round(MinY), Round(MaxX), Round(MaxY));

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
