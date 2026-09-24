namespace Compositor_korean_win.Core;

/// <summary>What a drag on the transform overlay is doing.</summary>
public enum TransformDragKind
{
    /// <summary>The body of the layer: it moves.</summary>
    Move,

    /// <summary>One of the eight handles: the layer resizes about the opposite one.</summary>
    Resize,

    /// <summary>The handle above the top edge: the layer turns about its centre.</summary>
    Rotate,

    /// <summary>A handle held with the distort modifier: one corner, or one edge, moves alone.</summary>
    Distort,

    /// <summary>Edit › Transform › Skew: an edge slides along itself, a corner along one of its edges.</summary>
    Skew,

    /// <summary>
    /// Edit › Transform › Perspective: a corner slides along one of its edges and the corner at the
    /// other end of that edge slides the opposite way, so the edge widens or narrows about its middle.
    /// </summary>
    Perspective,
}

/// <summary>A drag kind together with the handle it grabbed.</summary>
/// <remarks>
/// Upstream carries the handle inside the enum case. C# enums hold no payload, so the index rides
/// alongside and is meaningless for <see cref="TransformDragKind.Move"/> and
/// <see cref="TransformDragKind.Rotate"/>.
/// </remarks>
public readonly record struct TransformDragMode(TransformDragKind Kind, int Handle = 0)
{
    public static TransformDragMode Move => new(TransformDragKind.Move);
    public static TransformDragMode Rotate => new(TransformDragKind.Rotate);
    public static TransformDragMode Resize(int handle) => new(TransformDragKind.Resize, handle);
    public static TransformDragMode Distort(int handle) => new(TransformDragKind.Distort, handle);
    public static TransformDragMode Skew(int handle) => new(TransformDragKind.Skew, handle);
    public static TransformDragMode Perspective(int handle) => new(TransformDragKind.Perspective, handle);

    /// <summary>Whether the drag moves corners on their own, so the layer is resampled when it ends.</summary>
    public bool MovesCorners => Kind is TransformDragKind.Distort or TransformDragKind.Skew or TransformDragKind.Perspective;
}

/// <summary>
/// One drag of the transform overlay, from where it was grabbed to wherever the pointer is now.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is arithmetic on <see cref="LayerTransform"/> — six numbers in, six numbers out,
/// no pixels — which is what makes a transform non-destructive (docs/windows-port.md §2.1) and what
/// lets the whole of it be tested without a window.
/// </para>
/// <para>
/// The drag holds the transform as it was when the pointer went down, not as it is now, and every
/// update is computed from that original. Accumulating deltas instead would drift, and would make
/// a resize that runs into the minimum size unable to come back out of it.
/// </para>
/// </remarks>
public sealed record TransformDrag
{
    /// <summary>
    /// The unit points the eight handles sit on, clockwise from the top-left corner.
    /// </summary>
    /// <remarks>
    /// Corners are the even indices and edge midpoints the odd ones, which is what lets a resize
    /// tell "this handle moves one side" from "this handle moves two" by looking at whether a
    /// coordinate is 0.5.
    /// </remarks>
    public static readonly IReadOnlyList<Point> Handles =
    [
        new(0, 0), new(0.5, 0), new(1, 0), new(1, 0.5),
        new(1, 1), new(0.5, 1), new(0, 1), new(0, 0.5),
    ];

    /// <summary>How close, in view points, a pointer comes before a handle takes the drag.</summary>
    public const double HandleRadius = 10;

    /// <summary>The transform's corners in handle order: top-left, top-right, bottom-right, bottom-left.</summary>
    public static IReadOnlyList<Point> CornersOf(LayerTransform transform) =>
    [
        transform.PointAt(new Point(0, 0)), transform.PointAt(new Point(1, 0)),
        transform.PointAt(new Point(1, 1)), transform.PointAt(new Point(0, 1)),
    ];

    /// <summary>The placement when the pointer went down.</summary>
    public required LayerTransform Original { get; init; }

    /// <summary>Where the pointer went down, in document pixels.</summary>
    public required Point Start { get; init; }

    public required TransformDragMode Mode { get; init; }

    /// <summary>The corners when the drag began; null unless this drag distorts.</summary>
    public IReadOnlyList<Point>? OriginalCorners { get; init; }

    /// <summary>
    /// The corners after dragging to <paramref name="point"/>, or null when this drag is not a
    /// distortion.
    /// </summary>
    /// <remarks>
    /// A corner handle moves its own corner, an edge handle moves both corners of that edge, and
    /// the body moves all four. Holding shift keeps whatever is moving on one axis.
    /// </remarks>
    public IReadOnlyList<Point>? Corners(Point point, bool shift = false)
    {
        if (OriginalCorners is not { Count: 4 } corners) return null;

        double dx = point.X - Start.X, dy = point.Y - Start.Y;
        if (shift)
        {
            if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
            else dx = 0;
        }

        int[] moved;
        switch (Mode.Kind)
        {
            case TransformDragKind.Distort:
                // An even handle is a corner and moves one; an odd one is an edge midpoint and
                // moves the two corners it sits between.
                int index = Mode.Handle;
                moved = index % 2 == 0
                    ? new[] { index / 2 }
                    : new[] { index / 2, (index / 2 + 1) % 4 };
                break;
            case TransformDragKind.Move:
                moved = new[] { 0, 1, 2, 3 };
                break;
            case TransformDragKind.Skew or TransformDragKind.Perspective:
                return Slid(corners, dx, dy);
            default:
                return null;
        }

        Point[] result = [.. corners];
        foreach (int corner in moved)
            result[corner] = new Point(result[corner].X + dx, result[corner].Y + dy);
        return result;
    }

    /// <summary>
    /// Skew and Perspective: whatever moves, moves along an edge of the shape as it began, never
    /// across it — which is what keeps a skewed box's sides parallel and a perspective's edges on
    /// their lines.
    /// </summary>
    /// <remarks>
    /// An edge handle slides its edge along itself in both modes, as Photoshop's do. A corner picks
    /// whichever of its two edges the drag runs closer to; Skew moves the corner alone along it,
    /// Perspective moves it and sends the far end of that edge the other way by the same amount.
    /// </remarks>
    private Point[] Slid(IReadOnlyList<Point> corners, double dx, double dy)
    {
        Point[] result = [.. corners];
        int index = Math.Clamp(Mode.Handle, 0, Handles.Count - 1);

        if (index % 2 == 1)
        {
            int a = index / 2, b = (a + 1) % 4;
            (double ux, double uy) = Unit(corners[a], corners[b]);
            double along = dx * ux + dy * uy;
            result[a] = Moved(corners[a], ux * along, uy * along);
            result[b] = Moved(corners[b], ux * along, uy * along);
            return result;
        }

        int corner = index / 2, next = (corner + 1) % 4, previous = (corner + 3) % 4;
        (double nx, double ny) = Unit(corners[corner], corners[next]);
        (double px, double py) = Unit(corners[corner], corners[previous]);
        double towardsNext = dx * nx + dy * ny, towardsPrevious = dx * px + dy * py;

        bool alongNext = Math.Abs(towardsNext) >= Math.Abs(towardsPrevious);
        int partner = alongNext ? next : previous;
        (double ex, double ey) = alongNext ? (nx, ny) : (px, py);
        double distance = alongNext ? towardsNext : towardsPrevious;

        result[corner] = Moved(corners[corner], ex * distance, ey * distance);
        if (Mode.Kind == TransformDragKind.Perspective)
            result[partner] = Moved(corners[partner], -ex * distance, -ey * distance);
        return result;

        static (double X, double Y) Unit(Point from, Point to)
        {
            double x = to.X - from.X, y = to.Y - from.Y, length = Math.Sqrt(x * x + y * y);
            return length < 1e-9 ? (1, 0) : (x / length, y / length);
        }

        static Point Moved(Point point, double x, double y) => new(point.X + x, point.Y + y);
    }

    /// <summary>
    /// The placement after dragging to <paramref name="point"/>, in document pixels.
    /// </summary>
    /// <param name="lockRatio">The inspector's proportional-scaling switch.</param>
    /// <param name="shift">Locks a move to an axis, a rotation to 15°, and inverts
    /// <paramref name="lockRatio"/> on a resize, as upstream does.</param>
    /// <param name="option">Resizes about the centre instead of the opposite handle.</param>
    /// <remarks>
    /// A drag that would leave an invalid placement — off the far end of the document, or thinner
    /// than a pixel — yields the original rather than something the format would refuse to store.
    /// </remarks>
    public LayerTransform Updated(Point point, bool lockRatio, bool shift, bool option = false)
    {
        LayerTransform result = Mode.Kind switch
        {
            TransformDragKind.Move => Moved(point, shift),
            TransformDragKind.Rotate => Rotated(point, shift),
            TransformDragKind.Resize => Resized(point, lockRatio, shift, option),
            _ => Original,
        };

        return result.IsValid ? result : Original;
    }

    private LayerTransform Moved(Point point, bool shift)
    {
        double dx = point.X - Start.X, dy = point.Y - Start.Y;
        if (shift)
        {
            if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
            else dx = 0;
        }

        return Original with
        {
            Origin = new Point(Original.Origin.X + dx, Original.Origin.Y + dy),
        };
    }

    private LayerTransform Rotated(Point point, bool shift)
    {
        Point centre = Original.Center;
        double delta = Math.Atan2(point.Y - centre.Y, point.X - centre.X)
                     - Math.Atan2(Start.Y - centre.Y, Start.X - centre.X);

        double rotation = Original.Rotation + delta * 180 / Math.PI;
        if (shift) rotation = Math.Round(rotation / 15, MidpointRounding.AwayFromZero) * 15;

        return Original with { Rotation = rotation };
    }

    /// <remarks>
    /// The one subtle part. The handle is followed by its offset from where it started rather than
    /// by the pointer's position, so grabbing a handle a few pixels off centre does not snap the
    /// layer to the pointer; and the measurement is made along the layer's own axes, so a rotated
    /// layer resizes along its own edges rather than along the document's.
    /// </remarks>
    private LayerTransform Resized(Point point, bool lockRatio, bool shift, bool option)
    {
        Point handle = Handles[Math.Clamp(Mode.Handle, 0, Handles.Count - 1)];
        Point anchorUnit = option ? new Point(0.5, 0.5) : new Point(1 - handle.X, 1 - handle.Y);
        Point anchor = Original.PointAt(anchorUnit);
        Point grabbed = Original.PointAt(handle);

        double dx = grabbed.X + point.X - Start.X - anchor.X;
        double dy = grabbed.Y + point.Y - Start.Y - anchor.Y;

        // Anchored at the centre, the distance from anchor to handle is half the size, so it counts
        // twice; anchored at the opposite handle it is the whole size.
        double span = option ? 2 : 1;
        double cos = Math.Cos(Original.Radians), sin = Math.Sin(Original.Radians);
        double localX = (dx * cos + dy * sin) * span;
        double localY = (-dx * sin + dy * cos) * span;

        // Which way each axis grows, and zero for an edge handle's free axis.
        double sx = handle.X * 2 - 1, sy = handle.Y * 2 - 1;
        double width = sx == 0 ? Original.Size.Width : Math.Max(1, localX * sx);
        double height = sy == 0 ? Original.Size.Height : Math.Max(1, localY * sy);

        if (lockRatio != shift)
        {
            double factor;
            if (sx == 0)
            {
                factor = height / Original.Size.Height;
            }
            else if (sy == 0)
            {
                factor = width / Original.Size.Width;
            }
            else
            {
                // A corner handle rarely stays on the diagonal, so the pointer is projected onto it.
                factor = Math.Max(1 / Math.Min(Original.Size.Width, Original.Size.Height),
                                  (localX * sx * Original.Size.Width + localY * sy * Original.Size.Height)
                                  / (Original.Size.Width * Original.Size.Width
                                     + Original.Size.Height * Original.Size.Height));
            }

            width = Original.Size.Width * factor;
            height = Original.Size.Height * factor;
        }

        // The anchor stays put, so the centre moves by however much of the new size sits past it.
        double offsetX = (0.5 - anchorUnit.X) * width;
        double offsetY = (0.5 - anchorUnit.Y) * height;
        var centre = new Point(anchor.X + offsetX * cos - offsetY * sin,
                               anchor.Y + offsetX * sin + offsetY * cos);

        return Original with
        {
            Size = new Size(width, height),
            Origin = new Point(centre.X - width / 2, centre.Y - height / 2),
        };
    }
}
