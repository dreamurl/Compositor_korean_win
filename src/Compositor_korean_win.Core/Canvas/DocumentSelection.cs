namespace Compositor_korean_win.Core;

/// <summary>Whether a shape adds to a selection or takes away from it.</summary>
public enum SelectionOperation
{
    Add,
    Subtract,
}

/// <summary>A closed outline in document pixels.</summary>
/// <remarks>
/// Straight segments only. Upstream keeps a <c>CGPath</c>, which can hold curves, but the only
/// curves it ever puts in one come from the ellipse marquee, and an ellipse drawn as enough
/// segments is indistinguishable from one at any zoom a selection is judged at. What this buys is
/// that the rasteriser here is a scanline fill and nothing more.
/// </remarks>
public sealed record SelectionLoop(IReadOnlyList<Point> Points)
{
    public bool IsDegenerate => Points.Count < 3;

    public Rect Bounds => Rect.Around(Points);
}

/// <summary>One step of a selection: some outlines, added or taken away.</summary>
public sealed record SelectionShape(SelectionOperation Operation, IReadOnlyList<SelectionLoop> Loops);

/// <summary>
/// What is selected on a document: a list of shapes applied in order.
/// </summary>
/// <remarks>
/// <para>
/// Upstream folds everything into one path and fills it with the winding rule. That works for
/// adding and stops working for taking away: a reversed outline cancels where it overlaps and
/// selects where it does not, so a subtraction dragged clear of the selection would select what it
/// was meant to remove. Keeping the steps in order and combining their coverage instead is exact
/// for both, and costs a rasterisation per step of a kind that only touches its own bounds.
/// </para>
/// <para>
/// A selection with no shapes is not the same as no selection at all. No selection means an edit
/// touches the whole canvas; an empty selection means it touches nothing, and upstream is explicit
/// that the two must never be confused.
/// </para>
/// </remarks>
public sealed record DocumentSelection
{
    /// <summary>How many rows a pixel is sampled on when a selection has soft edges.</summary>
    private const int Samples = 4;

    public required IReadOnlyList<SelectionShape> Shapes { get; init; }

    /// <summary>Soft edges, as every selection tool but the pixel-exact ones wants.</summary>
    public bool IsAntialiased { get; init; } = true;

    public static DocumentSelection Empty => new() { Shapes = [] };

    public bool IsEmpty => Shapes.Count == 0
        || Shapes.All(shape => shape.Operation == SelectionOperation.Subtract
                            || shape.Loops.All(loop => loop.IsDegenerate));

    /// <summary>Everything the selection could possibly cover — what the added shapes span.</summary>
    public Rect Bounds
    {
        get
        {
            var points = new List<Point>();
            foreach (SelectionShape shape in Shapes)
            {
                if (shape.Operation != SelectionOperation.Add) continue;
                foreach (SelectionLoop loop in shape.Loops) points.AddRange(loop.Points);
            }
            return Rect.Around(points);
        }
    }

    public static DocumentSelection Rectangle(Rect rect) => new()
    {
        Shapes = [new SelectionShape(SelectionOperation.Add, [RectangleLoop(rect)])],
    };

    /// <summary>The ellipse inscribed in <paramref name="rect"/>.</summary>
    public static DocumentSelection Ellipse(Rect rect, int segments = 96) => new()
    {
        Shapes = [new SelectionShape(SelectionOperation.Add, [EllipseLoop(rect, segments)])],
    };

    /// <summary>A freehand or polygonal outline, closed by joining its ends.</summary>
    public static DocumentSelection Lasso(IReadOnlyList<Point> points) => new()
    {
        Shapes = [new SelectionShape(SelectionOperation.Add, [new SelectionLoop(points)])],
    };

    public DocumentSelection Adding(DocumentSelection other) => this with
    {
        Shapes = [.. Shapes, .. other.Shapes],
    };

    /// <summary>This selection with everything <paramref name="other"/> covers taken out of it.</summary>
    public DocumentSelection Subtracting(DocumentSelection other) => this with
    {
        Shapes =
        [
            .. Shapes,
            .. other.Shapes.Select(shape => shape with { Operation = SelectionOperation.Subtract }),
        ],
    };

    public static SelectionLoop RectangleLoop(Rect rect) => new(
    [
        new Point(rect.MinX, rect.MinY), new Point(rect.MaxX, rect.MinY),
        new Point(rect.MaxX, rect.MaxY), new Point(rect.MinX, rect.MaxY),
    ]);

    public static SelectionLoop EllipseLoop(Rect rect, int segments = 96)
    {
        segments = Math.Max(8, segments);
        var points = new Point[segments];
        double radiusX = rect.Width / 2, radiusY = rect.Height / 2;

        for (int i = 0; i < segments; i++)
        {
            double angle = i * 2 * Math.PI / segments;
            points[i] = new Point(rect.MidX + Math.Cos(angle) * radiusX,
                                  rect.MidY + Math.Sin(angle) * radiusY);
        }

        return new SelectionLoop(points);
    }

    /// <summary>
    /// Coverage over a whole canvas: white where selected, in the colour channels, as a mask.
    /// </summary>
    /// <remarks>
    /// The same shape a <see cref="LayerMask"/> takes, so a selection can be turned into one — and
    /// so the renderer's existing clip path applies to it without learning anything new.
    /// </remarks>
    public PixelBuffer Coverage(int width, int height)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);
        byte[] levels = Levels(new PixelRect(0, 0, width, height));

        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < width; x++)
            {
                byte level = levels[y * width + x];
                row[x * 4 + 0] = level;
                row[x * 4 + 1] = level;
                row[x * 4 + 2] = level;
                row[x * 4 + 3] = 255;
            }
        }

        return buffer;
    }

    /// <summary>
    /// Coverage for just the part of the canvas the selection can reach, ready to clip an edit.
    /// </summary>
    /// <remarks>
    /// An empty region means the edit touches nothing, which is the whole point of telling an empty
    /// selection from no selection at all.
    /// </remarks>
    public (PixelRect Region, byte[] Levels) Clip(int width, int height)
    {
        PixelRect region = Bounds.Inflate(1).Enclosing().Intersect(new PixelRect(0, 0, width, height));
        return region.IsEmpty ? (default, []) : (region, Levels(region));
    }

    /// <summary>Whether a document point is selected at all.</summary>
    public bool Contains(Point point)
    {
        bool inside = false;
        foreach (SelectionShape shape in Shapes)
        {
            bool covered = shape.Loops.Any(loop => Winding(loop, point) != 0);
            if (!covered) continue;
            inside = shape.Operation == SelectionOperation.Add;
        }
        return inside;
    }

    /// <summary>One coverage byte per pixel of <paramref name="region"/>, row by row.</summary>
    public byte[] Levels(PixelRect region)
    {
        var levels = new byte[Math.Max(0, region.Width * region.Height)];
        if (levels.Length == 0) return levels;

        var shape = new byte[levels.Length];

        foreach (SelectionShape step in Shapes)
        {
            Array.Clear(shape);
            Rasterise(step.Loops, region, IsAntialiased, shape);

            if (step.Operation == SelectionOperation.Add)
            {
                for (int i = 0; i < levels.Length; i++)
                    levels[i] = Math.Max(levels[i], shape[i]);
            }
            else
            {
                for (int i = 0; i < levels.Length; i++)
                    levels[i] = Math.Min(levels[i], (byte)(255 - shape[i]));
            }
        }

        return levels;
    }

    /// <summary>
    /// Scanline fill of <paramref name="loops"/> with the nonzero winding rule.
    /// </summary>
    /// <remarks>
    /// Soft edges come from sampling four rows of each pixel and measuring how much of each row a
    /// span covers, which is exact horizontally and stepped vertically — the cheap half of a
    /// coverage rasteriser, and enough for an edge nobody looks at closer than a marching outline.
    /// Hard edges are the same walk asking only whether the pixel's centre is inside.
    /// </remarks>
    private static void Rasterise(IReadOnlyList<SelectionLoop> loops, PixelRect region,
                                  bool antialiased, Span<byte> levels)
    {
        var crossings = new List<(double X, int Direction)>();
        var accumulator = new double[region.Width];
        int rows = antialiased ? Samples : 1;

        for (int y = 0; y < region.Height; y++)
        {
            Array.Clear(accumulator);

            for (int sample = 0; sample < rows; sample++)
            {
                double scanline = region.Y + y + (sample + 0.5) / rows;
                crossings.Clear();

                foreach (SelectionLoop loop in loops)
                {
                    if (loop.IsDegenerate) continue;
                    IReadOnlyList<Point> points = loop.Points;

                    for (int i = 0; i < points.Count; i++)
                    {
                        Point a = points[i], b = points[(i + 1) % points.Count];
                        if (a.Y == b.Y) continue;

                        // Half-open in y, so a vertex shared by two edges is counted once.
                        double low = Math.Min(a.Y, b.Y), high = Math.Max(a.Y, b.Y);
                        if (scanline < low || scanline >= high) continue;

                        double x = a.X + (scanline - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                        crossings.Add((x, b.Y > a.Y ? 1 : -1));
                    }
                }

                if (crossings.Count < 2) continue;
                crossings.Sort((left, right) => left.X.CompareTo(right.X));

                int winding = 0;
                for (int i = 0; i < crossings.Count - 1; i++)
                {
                    winding += crossings[i].Direction;
                    if (winding == 0) continue;

                    AddSpan(accumulator, region, crossings[i].X, crossings[i + 1].X, antialiased, rows);
                }
            }

            for (int x = 0; x < region.Width; x++)
            {
                double value = Math.Clamp(accumulator[x], 0, 1);
                levels[y * region.Width + x] = (byte)Math.Round(value * 255, MidpointRounding.AwayFromZero);
            }
        }
    }

    /// <summary>Adds one covered span of one sampled row to a row of coverage.</summary>
    private static void AddSpan(double[] accumulator, PixelRect region, double from, double to,
                                bool antialiased, int rows)
    {
        if (to <= from) return;

        int first = Math.Max(0, (int)Math.Floor(from) - region.X);
        int last = Math.Min(region.Width - 1, (int)Math.Ceiling(to) - region.X);

        for (int x = first; x <= last; x++)
        {
            double left = region.X + x, right = left + 1;

            if (!antialiased)
            {
                // Hard edges: the pixel is in or out by where its centre falls.
                double centre = left + 0.5;
                if (centre >= from && centre < to) accumulator[x] = 1;
                continue;
            }

            double overlap = Math.Min(right, to) - Math.Max(left, from);
            if (overlap > 0) accumulator[x] += overlap / rows;
        }
    }

    /// <summary>The winding number of a loop about a point, for hit-testing.</summary>
    private static int Winding(SelectionLoop loop, Point point)
    {
        if (loop.IsDegenerate) return 0;

        int winding = 0;
        IReadOnlyList<Point> points = loop.Points;

        for (int i = 0; i < points.Count; i++)
        {
            Point a = points[i], b = points[(i + 1) % points.Count];
            if (a.Y == b.Y) continue;

            double low = Math.Min(a.Y, b.Y), high = Math.Max(a.Y, b.Y);
            if (point.Y < low || point.Y >= high) continue;

            double x = a.X + (point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
            if (x > point.X) winding += b.Y > a.Y ? 1 : -1;
        }

        return winding;
    }
}
