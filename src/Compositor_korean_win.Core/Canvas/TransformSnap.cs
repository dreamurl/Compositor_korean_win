namespace Compositor_korean_win.Core;

/// <summary>Lines a moving layer can line up with, in document pixels.</summary>
public readonly record struct SnapGuides(IReadOnlyList<double> Xs, IReadOnlyList<double> Ys)
{
    public static SnapGuides None => new([], []);
}

/// <summary>Where a snap put a layer, and the guides it met.</summary>
/// <remarks>
/// The guides come back so the overlay can draw a line along whatever the layer landed on — a snap
/// the user cannot see is a snap they will read as the drag sticking.
/// </remarks>
public readonly record struct SnapResult(Point Offset, double? X, double? Y)
{
    public bool Moved => Offset.X != 0 || Offset.Y != 0;
}

/// <summary>
/// Moving a layer pulls its edges and centre onto the canvas and onto the other layers.
/// </summary>
/// <remarks>
/// <para>
/// The pull is a fixed distance on screen, so it feels the same at every zoom, and small enough to
/// slide past without a fight. <see cref="ToleranceFor"/> is what converts it: ten points is ten
/// document pixels at 100%, but four hundred of them on a document zoomed out to a thumbnail.
/// </para>
/// <para>
/// Each axis snaps on its own. A layer can land on a vertical guide without a horizontal one, which
/// is what makes lining a row of layers up along one edge possible at all.
/// </para>
/// </remarks>
public static class TransformSnap
{
    /// <summary>How close, in view points, a guide comes before it snaps.</summary>
    public const double Distance = 10;

    /// <summary>The snap distance in document pixels at a given zoom.</summary>
    public static double ToleranceFor(CanvasViewport viewport) =>
        Distance / Math.Max(viewport.PointsPerPixel, 0.0001);

    /// <summary>
    /// What a moving layer snaps to: the canvas edges and centre, and the upright bounds and centre
    /// of every other layer that is drawn.
    /// </summary>
    /// <remarks>
    /// The layers being moved are left out, or they would snap to where they already are. A
    /// rotated layer contributes the box around its corners, not its own turned edges — that is
    /// what the user sees the layer occupying.
    /// </remarks>
    public static SnapGuides TargetsFor(CanvasDocument document, IReadOnlySet<Guid> moving)
    {
        var xs = new List<double> { 0, document.Width / 2.0, document.Width };
        var ys = new List<double> { 0, document.Height / 2.0, document.Height };

        var byId = new Dictionary<Guid, ImageLayer>(document.Layers.Count);
        foreach (ImageLayer layer in document.Layers) byId[layer.Id] = layer;

        foreach (ImageLayer layer in document.Layers)
        {
            if (layer.IsGroup || layer.Image is null) continue;
            if (moving.Contains(layer.Id)) continue;
            if (!IsDrawn(layer, byId)) continue;

            Rect box = Rect.Around(TransformDrag.CornersOf(layer.Transform));
            xs.Add(Math.Round(box.MinX, MidpointRounding.AwayFromZero));
            xs.Add(Math.Round(box.MidX, MidpointRounding.AwayFromZero));
            xs.Add(Math.Round(box.MaxX, MidpointRounding.AwayFromZero));
            ys.Add(Math.Round(box.MinY, MidpointRounding.AwayFromZero));
            ys.Add(Math.Round(box.MidY, MidpointRounding.AwayFromZero));
            ys.Add(Math.Round(box.MaxY, MidpointRounding.AwayFromZero));
        }

        return new SnapGuides(xs, ys);
    }

    /// <summary>
    /// <paramref name="draft"/> nudged so that the layer it places lines up with a nearby edge or
    /// centre, together with the guides it met.
    /// </summary>
    public static (LayerTransform Transform, SnapResult Snap) Move(
        LayerTransform draft, SnapGuides targets, double tolerance)
    {
        Rect box = Rect.Around(TransformDrag.CornersOf(draft));
        SnapResult snap = Offset(box, targets, tolerance);
        if (!snap.Moved) return (draft, snap);

        LayerTransform moved = draft with
        {
            Origin = new Point(draft.Origin.X + snap.Offset.X, draft.Origin.Y + snap.Offset.Y),
        };

        return (moved.IsValid ? moved : draft, snap);
    }

    /// <summary>
    /// The move that puts whichever of <paramref name="box"/>'s left, centre or right lands nearest
    /// an x target onto it, and the same vertically — each axis on its own, and only within
    /// <paramref name="tolerance"/> document pixels.
    /// </summary>
    public static SnapResult Offset(Rect box, SnapGuides targets, double tolerance)
    {
        (double move, double? target) horizontal =
            Shift([box.MinX, box.MidX, box.MaxX], targets.Xs, tolerance);
        (double move, double? target) vertical =
            Shift([box.MinY, box.MidY, box.MaxY], targets.Ys, tolerance);

        return new SnapResult(new Point(horizontal.move, vertical.move),
                              horizontal.target, vertical.target);
    }

    /// <summary>The smallest move putting one of <paramref name="guides"/> on a target.</summary>
    private static (double Move, double? Target) Shift(
        ReadOnlySpan<double> guides, IReadOnlyList<double> targets, double tolerance)
    {
        double bestMove = 0;
        double? bestTarget = null;

        foreach (double guide in guides)
        {
            foreach (double target in targets)
            {
                double move = target - guide;
                if (Math.Abs(move) > tolerance) continue;
                // Ties go to the guide already found: the first of left, centre, right wins, which
                // is what stops a layer the width of its tolerance flickering between its own edges.
                if (bestTarget is not null && Math.Abs(bestMove) <= Math.Abs(move)) continue;
                bestMove = move;
                bestTarget = target;
            }
        }

        return (bestMove, bestTarget);
    }

    /// <summary>Whether a layer is drawn: visible itself and inside no hidden folder.</summary>
    private static bool IsDrawn(ImageLayer layer, Dictionary<Guid, ImageLayer> byId)
    {
        if (!layer.IsVisible) return false;

        Guid? parent = layer.ParentId;
        for (int depth = 0; parent is Guid id && depth <= ProjectLimits.MaximumNesting; depth++)
        {
            if (!byId.TryGetValue(id, out ImageLayer? folder)) return false;
            if (!folder.IsVisible) return false;
            parent = folder.ParentId;
        }

        return true;
    }
}
