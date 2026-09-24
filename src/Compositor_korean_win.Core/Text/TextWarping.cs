namespace Compositor_korean_win.Core;

/// <summary>
/// Photoshop's Warp Text, as a map from a point of the unwarped text to where it goes.
/// </summary>
/// <remarks>
/// <para>
/// Photoshop warps text through a Bézier envelope whose control points it does not document. These
/// are closed-form maps chosen to read the same: each moves the text box's edges the way the
/// style's icon shows, and moves everything between them in proportion. The amounts match in
/// sign and roughly in size — Bulge at +50 makes the middle of a line about half as tall again,
/// Arc at +100 bends a line into a half circle.
/// </para>
/// <para>
/// A map rather than a pixel warp because it applies to the outlines before they are filled: a
/// glyph bent this way still has a clean edge, however far it is bent, where resampling a raster
/// would soften it. Straight edges are split first (<see cref="Densify"/>) so they can bend at all.
/// </para>
/// </remarks>
public static class TextWarping
{
    /// <summary>Where <paramref name="point"/>, inside <paramref name="box"/>, lands under the warp.</summary>
    public static Point Map(Point point, Rect box, TextWarp warp)
    {
        if (warp.IsIdentity || box.IsEmpty) return point;

        double w = box.Width, h = box.Height;
        double cx = box.MidX, cy = box.MidY;
        // −1 to 1 across and down the box.
        double u = (point.X - cx) / (w / 2);
        double v = (point.Y - cy) / (h / 2);
        double b = warp.Bend / 100;

        // Distortion first, as Photoshop applies it: perspective along one axis, before the bend.
        double horizontal = warp.Horizontal / 100, vertical = warp.Vertical / 100;
        double x = point.X, y = point.Y;
        if (horizontal != 0) y = cy + (y - cy) * (1 + horizontal * u * 0.5);
        if (vertical != 0) x = cx + (x - cx) * (1 + vertical * v * 0.5);
        u = (x - cx) / (w / 2);
        v = (y - cy) / (h / 2);

        double dx = 0, dy = 0;
        double lift = h * 0.5 * b;

        switch (warp.Style)
        {
            case TextWarpStyle.Arc:
                return Arc(new Point(x, y), cx, cy, w, h, b);

            case TextWarpStyle.ArcLower:
                dy = lift * (1 - u * u) * (v + 1) / 2;
                break;

            case TextWarpStyle.ArcUpper:
                dy = -lift * (1 - u * u) * (1 - v) / 2;
                break;

            case TextWarpStyle.Arch:
                dy = -lift * (1 - u * u);
                break;

            case TextWarpStyle.Bulge:
                dy = lift * (1 - u * u) * v;
                break;

            case TextWarpStyle.ShellLower:
                // The bottom edge sweeps up at the ends while the top stays put.
                dy = -lift * u * u * (v + 1) / 2;
                break;

            case TextWarpStyle.ShellUpper:
                dy = lift * u * u * (1 - v) / 2;
                break;

            case TextWarpStyle.Flag:
                dy = -lift * 0.5 * Math.Sin(Math.PI * u);
                break;

            case TextWarpStyle.Wave:
                dy = -lift * 0.5 * Math.Sin(Math.PI * (u + v * 0.5));
                break;

            case TextWarpStyle.Fish:
                // Swells at the head and pinches at the tail.
                dy = lift * v * Math.Sin(Math.PI * (u + 1) / 2) * (1 - u) / 1.2;
                break;

            case TextWarpStyle.Rise:
                dy = -lift * Math.Sin(u * Math.PI / 2);
                break;

            case TextWarpStyle.Fisheye:
            {
                double r2 = Math.Min(1, (u * u + v * v) / 2);
                double scale = b * 0.5 * (1 - r2);
                dx = (x - cx) * scale;
                dy = (y - cy) * scale;
                break;
            }

            case TextWarpStyle.Inflate:
                dy = lift * v * (1 - u * u);
                dx = w * 0.12 * b * u * (1 - v * v);
                break;

            case TextWarpStyle.Squeeze:
                dy = -lift * 0.7 * v * (1 - u * u);
                dx = w * 0.08 * b * u * (1 - v * v);
                break;

            case TextWarpStyle.Twist:
            {
                double r = Math.Min(1, Math.Sqrt((u * u + v * v) / 2));
                double angle = b * Math.PI * 0.5 * (1 - r);
                double cos = Math.Cos(angle), sin = Math.Sin(angle);
                double px = x - cx, py = y - cy;
                return new Point(cx + px * cos - py * sin, cy + px * sin + py * cos);
            }
        }

        return new Point(x + dx, y + dy);
    }

    /// <summary>
    /// The line bent round a circle, as if set on its rim: letters keep their height and turn to
    /// follow the curve. A positive bend arches up, a negative one sags.
    /// </summary>
    private static Point Arc(Point point, double cx, double cy, double w, double h, double b)
    {
        if (Math.Abs(b) < 1e-6) return point;

        // The middle of the text box follows an arc whose length is the box's width and whose
        // angle is the bend: +100 is half a circle.
        double sweep = Math.Abs(b) * Math.PI;
        double radius = w / sweep;
        double sign = Math.Sign(b);

        double theta = (point.X - cx) / radius;
        // Up the box is further from the circle's centre when the arc bulges up.
        double r = radius - sign * (point.Y - cy);
        double x = cx + r * Math.Sin(theta);
        // Keep the top of the arc where the middle of the box was.
        double y = cy + sign * (radius - r * Math.Cos(theta));
        return new Point(x, y);
    }

    /// <summary>
    /// A loop with no segment longer than <paramref name="step"/>, so a map can bend its straight
    /// edges rather than only move their ends.
    /// </summary>
    public static List<Point> Densify(IReadOnlyList<Point> loop, double step)
    {
        var result = new List<Point>(loop.Count * 2);
        for (int i = 0; i < loop.Count; i++)
        {
            Point a = loop[i], b = loop[(i + 1) % loop.Count];
            result.Add(a);
            double length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            int pieces = (int)Math.Min(4096, Math.Ceiling(length / step));
            for (int k = 1; k < pieces; k++)
            {
                double t = (double)k / pieces;
                result.Add(new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
            }
        }
        return result;
    }
}
