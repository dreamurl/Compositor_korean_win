namespace Compositor_korean_win.Core;

/// <summary>The Crop tool's fixed proportions, upstream's ratio picker.</summary>
public enum CropRatio
{
    Free,
    Original,
    Square,
    FourByThree,
    SixteenByNine,
}

/// <summary>What a drag on the crop frame does.</summary>
public enum CropDragKind
{
    /// <summary>Draws a new frame from where the drag began.</summary>
    Create,

    /// <summary>Carries the frame, keeping its size.</summary>
    Move,

    /// <summary>Pulls one of the eight handles.</summary>
    Resize,
}

/// <summary>
/// The crop frame's arithmetic — upstream's <c>CropGeometry</c>, <c>CropDrag</c> and
/// <c>CropSnap</c>.
/// </summary>
/// <remarks>
/// <para>
/// The frame is in document pixels and always lands on whole ones, because what it becomes is a
/// canvas size. It may reach past the canvas: cropping outward grows the canvas, as in Photoshop.
/// </para>
/// <para>
/// Resizing goes through <see cref="TransformDrag"/>, the same arithmetic as a layer's handles, so
/// the frame and a layer answer a handle identically — the opposite edge fixed, or with Alt the
/// centre.
/// </para>
/// </remarks>
public static class CropGeometry
{
    /// <summary>The largest side a canvas may have, as New and Canvas Size allow.</summary>
    public const int MaximumSide = 30_000;

    /// <summary>The frame on whole pixels, the right way up, at least one pixel each way.</summary>
    public static Rect Snapped(Rect rect)
    {
        double left = Math.Min(rect.MinX, rect.MaxX), top = Math.Min(rect.MinY, rect.MaxY);
        double right = Math.Max(rect.MinX, rect.MaxX), bottom = Math.Max(rect.MinY, rect.MaxY);
        double x = Math.Round(left), y = Math.Round(top);
        return new Rect(x, y, Math.Max(1, Math.Round(right) - x), Math.Max(1, Math.Round(bottom) - y));
    }

    /// <summary>Whether the frame can become a canvas.</summary>
    public static bool Valid(Rect rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height)
        && rect.Width is >= 1 and <= MaximumSide && rect.Height is >= 1 and <= MaximumSide
        && (long)rect.Width * (long)rect.Height <= 100_000_000
        && Math.Abs(rect.X) <= 1_000_000 && Math.Abs(rect.Y) <= 1_000_000;

    /// <summary>Width over height for a ratio choice, or null for Free.</summary>
    public static double? Proportion(CropRatio ratio, int canvasWidth, int canvasHeight) => ratio switch
    {
        CropRatio.Original => (double)canvasWidth / Math.Max(1, canvasHeight),
        CropRatio.Square => 1,
        CropRatio.FourByThree => 4.0 / 3,
        CropRatio.SixteenByNine => 16.0 / 9,
        _ => null,
    };

    /// <summary>A frame dragged from <paramref name="start"/> to <paramref name="end"/>, or with Alt grown out from <paramref name="start"/>.</summary>
    public static Rect Create(Point start, Point end, double? proportion, bool symmetric)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        if (proportion is double ratio && ratio > 0)
        {
            if (Math.Abs(dx) > Math.Abs(dy) * ratio) dy = (dy < 0 ? -1 : 1) * Math.Abs(dx) / ratio;
            else dx = (dx < 0 ? -1 : 1) * Math.Abs(dy) * ratio;
        }

        return symmetric
            ? Snapped(new Rect(start.X - Math.Abs(dx), start.Y - Math.Abs(dy), Math.Abs(dx) * 2, Math.Abs(dy) * 2))
            : Snapped(new Rect(Math.Min(start.X, start.X + dx), Math.Min(start.Y, start.Y + dy), Math.Abs(dx), Math.Abs(dy)));
    }

    /// <summary>The frame after a drag of kind <paramref name="kind"/> from <paramref name="start"/> to <paramref name="point"/>.</summary>
    public static Rect Dragged(Rect original, CropDragKind kind, int handle, Point start, Point point,
                               double? proportion, bool symmetric)
    {
        switch (kind)
        {
            case CropDragKind.Move:
                return Snapped(original.OffsetBy(point.X - start.X, point.Y - start.Y));

            case CropDragKind.Resize:
                var drag = new TransformDrag
                {
                    Original = new LayerTransform(original.Origin, original.Size),
                    Start = start,
                    Mode = TransformDragMode.Resize(handle),
                };
                LayerTransform next = drag.Updated(point, lockRatio: proportion is not null, shift: false, option: symmetric);
                return Snapped(new Rect(next.Origin, next.Size));

            default:
                return Create(start, point, proportion, symmetric);
        }
    }

    /// <summary>The frame given a proportion: its width kept, its height fitted, about its middle.</summary>
    public static Rect WithProportion(Rect rect, double proportion)
    {
        double height = rect.Width / proportion;
        return Snapped(new Rect(rect.X, rect.MidY - height / 2, rect.Width, height));
    }

    /// <summary>
    /// What the frame's edges snap to: the canvas edges and every visible layer's upright bounds, in
    /// whole pixels — upstream's <c>cropSnapTargets</c>.
    /// </summary>
    public static SnapGuides Targets(CanvasDocument document)
    {
        List<double> xs = [0, document.Width], ys = [0, document.Height];
        foreach (ImageLayer layer in document.Layers)
        {
            if (layer.Image is null || layer.IsGroup || !Shown(document, layer)) continue;
            Rect bounds = Rect.Around(TransformDrag.CornersOf(layer.Transform));
            xs.Add(Math.Round(bounds.MinX));
            xs.Add(Math.Round(bounds.MaxX));
            ys.Add(Math.Round(bounds.MinY));
            ys.Add(Math.Round(bounds.MaxY));
        }
        return new SnapGuides(xs, ys);
    }

    /// <summary>Whether a layer shows: it and every folder above it visible.</summary>
    private static bool Shown(CanvasDocument document, ImageLayer layer)
    {
        for (ImageLayer? current = layer; current is not null;
             current = current.ParentId is Guid parent ? document.Layer(parent) : null)
        {
            if (!current.IsVisible) return false;
        }
        return true;
    }

    /// <summary>
    /// The frame pulled onto a nearby edge. A move snaps its closest edges and keeps its size; a new
    /// frame or a handle snaps only the edges being dragged — those on the pointer's side. With a
    /// fixed proportion only a move snaps, so the proportion stays exact.
    /// </summary>
    public static Rect Snap(Rect rect, CropDragKind kind, int handle, Point point, double? proportion,
                            SnapGuides targets, double tolerance)
    {
        if (tolerance <= 0) return rect;

        double? Nearest(double value, IReadOnlyList<double> candidates)
        {
            double? best = null;
            foreach (double candidate in candidates)
            {
                if (Math.Abs(candidate - value) > tolerance) continue;
                if (best is double current && Math.Abs(current - value) <= Math.Abs(candidate - value)) continue;
                best = candidate;
            }
            return best;
        }

        if (kind == CropDragKind.Move)
        {
            double Shift(double low, double high, IReadOnlyList<double> candidates)
            {
                double? a = Nearest(low, candidates) - low, b = Nearest(high, candidates) - high;
                return a is null ? b ?? 0 : b is null ? a.Value : Math.Abs(a.Value) <= Math.Abs(b.Value) ? a.Value : b.Value;
            }
            return rect.OffsetBy(Shift(rect.MinX, rect.MaxX, targets.Xs), Shift(rect.MinY, rect.MaxY, targets.Ys));
        }

        if (proportion is not null) return rect;

        bool horizontal = true, vertical = true;
        if (kind == CropDragKind.Resize)
        {
            Point unit = TransformDrag.Handles[handle];
            horizontal = unit.X != 0.5;
            vertical = unit.Y != 0.5;
        }

        double left = rect.MinX, right = rect.MaxX, top = rect.MinY, bottom = rect.MaxY;
        if (horizontal)
        {
            if (Math.Abs(point.X - left) <= Math.Abs(point.X - right))
            {
                if (Nearest(left, targets.Xs) is double x && x < right) left = x;
            }
            else if (Nearest(right, targets.Xs) is double x && x > left) right = x;
        }
        if (vertical)
        {
            if (Math.Abs(point.Y - top) <= Math.Abs(point.Y - bottom))
            {
                if (Nearest(top, targets.Ys) is double y && y < bottom) top = y;
            }
            else if (Nearest(bottom, targets.Ys) is double y && y > top) bottom = y;
        }

        return Rect.FromBounds(left, top, right, bottom);
    }
}
