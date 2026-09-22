namespace Compositor_korean_win.Core;

/// <summary>
/// A uniform scale and a shift: how document pixels land on whatever is being drawn into.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets the canvas reuse the compositor whole. A layer's placement is six numbers, and
/// a uniform scale about the origin followed by a translation carries those six numbers to six
/// others — the box scales and moves, the rotation and the flips do not change, because scaling
/// evenly about any point commutes with turning about the box's own centre. So the renderer never
/// has to know whether it is drawing a document or a view of one.
/// </para>
/// <para>
/// Only uniform scaling qualifies. Scaling the axes differently would shear a rotated layer, which
/// <see cref="LayerTransform"/> cannot express, and there is no zoom that does that.
/// </para>
/// </remarks>
public readonly record struct CanvasProjection(double Scale, Point Offset)
{
    /// <summary>Document pixels drawn as themselves.</summary>
    public static CanvasProjection Identity => new(1, Point.Zero);

    public bool IsIdentity => Scale == 1 && Offset.X == 0 && Offset.Y == 0;

    public Point Apply(Point point) =>
        new(point.X * Scale + Offset.X, point.Y * Scale + Offset.Y);

    public Point Invert(Point point) =>
        new((point.X - Offset.X) / Scale, (point.Y - Offset.Y) / Scale);

    public Rect Apply(Rect rect) =>
        new(rect.X * Scale + Offset.X, rect.Y * Scale + Offset.Y, rect.Width * Scale, rect.Height * Scale);

    public LayerTransform Apply(LayerTransform transform) => IsIdentity ? transform : transform with
    {
        Origin = Apply(transform.Origin),
        Size = new Size(transform.Size.Width * Scale, transform.Size.Height * Scale),
    };

    public MaskClip Apply(MaskClip clip) => clip with { Placement = Apply(clip.Placement) };
}
