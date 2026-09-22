namespace Compositor_korean_win.Core;

/// <summary>
/// Several layers transformed as one.
/// </summary>
/// <remarks>
/// Upstream's rule, and the only one that makes a multiple selection behave: the handles belong to
/// an upright box drawn around everything selected, the drag edits that box, and each layer is
/// carried from where the box was to where it is now. Dragging every layer's own handles in step
/// instead would rotate each about its own centre, which scatters them.
/// </remarks>
public static class TransformGroup
{
    /// <summary>The upright box around several placements, which is what the handles edit.</summary>
    public static LayerTransform BoxAround(IReadOnlyList<LayerTransform> placements)
    {
        var points = new List<Point>(placements.Count * 4);
        foreach (LayerTransform placement in placements) points.AddRange(TransformDrag.CornersOf(placement));

        Rect box = Rect.Around(points);
        return new LayerTransform(new Point(box.X, box.Y),
                                  new Size(Math.Max(1, box.Width), Math.Max(1, box.Height)));
    }

    /// <summary>
    /// Each placement carried along as the box moves from <paramref name="from"/> to
    /// <paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// The same arithmetic an unlinked mask uses to follow its layer
    /// (<see cref="LayerTransform.Following"/>), which is not a coincidence: both are "this sat
    /// somewhere relative to that, and that has moved".
    /// </remarks>
    public static IReadOnlyDictionary<Guid, LayerTransform> Follow(
        IReadOnlyDictionary<Guid, LayerTransform> originals, LayerTransform from, LayerTransform to)
    {
        var result = new Dictionary<Guid, LayerTransform>(originals.Count);
        foreach ((Guid id, LayerTransform placement) in originals)
        {
            LayerTransform moved = placement.Following(from, to);
            result[id] = moved.IsValid ? moved : placement;
        }
        return result;
    }
}
