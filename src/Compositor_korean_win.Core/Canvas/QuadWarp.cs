namespace Compositor_korean_win.Core;

/// <summary>
/// Resampling a layer into four corners that no longer make a rectangle.
/// </summary>
/// <remarks>
/// <para>
/// This is the one transform that cannot stay non-destructive.
/// <see cref="LayerTransform"/> holds six numbers — a box, an angle and two flips — and that is
/// enough for every move, scale, rotation and flip, which is why none of them ever touch a pixel
/// (docs/windows-port.md §2.1). Four corners dragged freely are a projective map, which those six
/// numbers cannot express, so upstream does what this does: it resamples the pixels into the shape
/// once the drag ends, and what comes back is an ordinary upright layer again.
/// </para>
/// <para>
/// The map is the standard eight-parameter one from the unit square to a quadrilateral. Drawing
/// runs it backwards — for each destination pixel, where in the source did it come from — which is
/// the same inverse mapping the software backend uses and the only way to fill every destination
/// pixel exactly once.
/// </para>
/// </remarks>
public static class QuadWarp
{
    /// <summary>
    /// Whether four corners make a shape worth warping into: finite, convex and not folded.
    /// </summary>
    /// <remarks>
    /// A bow-tie or a collapsed quadrilateral has no sensible map — the projective transform either
    /// turns inside out or divides by zero — so upstream refuses them and the drag keeps the last
    /// shape that worked.
    /// </remarks>
    public static bool IsUsable(IReadOnlyList<Point> corners)
    {
        if (corners.Count != 4) return false;

        foreach (Point corner in corners)
        {
            if (!double.IsFinite(corner.X) || !double.IsFinite(corner.Y)) return false;
            if (Math.Abs(corner.X) > 1_000_000 || Math.Abs(corner.Y) > 1_000_000) return false;
        }

        double sign = 0;
        for (int i = 0; i < 4; i++)
        {
            Point a = corners[i], b = corners[(i + 1) % 4], c = corners[(i + 2) % 4];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) < 1e-9) return false;
            if (sign == 0) sign = Math.Sign(cross);
            else if (Math.Sign(cross) != sign) return false;
        }

        return true;
    }

    /// <summary>
    /// The pixels of <paramref name="source"/> resampled into <paramref name="corners"/>, and the
    /// upright placement they now sit at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller owns the returned buffer and releases it. The placement is the box around the
    /// quadrilateral, unrotated and unflipped: whatever angle the corners carried is now in the
    /// pixels, which is exactly what makes this destructive.
    /// </para>
    /// <para>
    /// Like every other draw, a reduction goes through the halvings first
    /// (<see cref="DownsamplePyramid"/>) rather than asking one bilinear tap to stand in for
    /// sixteen source pixels.
    /// </para>
    /// <para>
    /// <paramref name="clip"/> keeps the result to part of the box: a preview asks only for what
    /// the window shows. Clipped away entirely, the result is one transparent pixel at the clip's
    /// corner, so a caller always has something to draw in the layer's place.
    /// </para>
    /// </remarks>
    public static (PixelBuffer Pixels, LayerTransform Placement)? Resample(
        PixelBuffer source, IReadOnlyList<Point> corners, DownsamplePyramid? pyramid = null,
        PixelRect? clip = null)
    {
        if (!IsUsable(corners)) return null;

        PixelRect box = Rect.Around(corners).Enclosing();
        if (clip is PixelRect within)
        {
            box = box.Intersect(within);
            if (box.IsEmpty) box = new PixelRect(within.X, within.Y, 1, 1);
        }

        if (box.IsEmpty || (long)box.Width * box.Height > 300_000L * 300_000L) return null;

        double[] forward = Map(corners);
        double[]? inverse = Invert(forward);
        if (inverse is null) return null;

        // How much of the source one destination pixel covers, taken over the whole shape.
        double area = Math.Abs(Area(corners));
        double factor = Math.Sqrt(area / Math.Max(1, (double)source.Width * source.Height));
        int level = DownsamplePyramid.LevelFor(factor);

        PixelBuffer reduced = source;
        bool owned = false;
        if (level > 0)
        {
            if (pyramid is not null)
            {
                (reduced, _) = pyramid.Reduced(source, level);
            }
            else
            {
                reduced = source;
                for (int i = 0; i < level && (reduced.Width > 1 || reduced.Height > 1); i++)
                {
                    PixelBuffer next = DownsamplePyramid.Halve(reduced);
                    if (owned) reduced.Release();
                    reduced = next;
                    owned = true;
                }
            }
        }

        PixelBuffer result = PixelBuffer.Allocate(box.Width, box.Height);
        Span<byte> pixel = stackalloc byte[4];

        for (int y = 0; y < box.Height; y++)
        {
            Span<byte> row = result.Row(y);

            for (int x = 0; x < box.Width; x++)
            {
                double destinationX = box.X + x + 0.5;
                double destinationY = box.Y + y + 0.5;

                double w = inverse[6] * destinationX + inverse[7] * destinationY + inverse[8];
                if (Math.Abs(w) < 1e-12) continue;

                double u = (inverse[0] * destinationX + inverse[1] * destinationY + inverse[2]) / w;
                double v = (inverse[3] * destinationX + inverse[4] * destinationY + inverse[5]) / w;
                if (u < 0 || u > 1 || v < 0 || v > 1) continue;

                Sample(reduced, u * reduced.Width, v * reduced.Height, pixel);
                if (pixel[3] == 0) continue;
                pixel.CopyTo(row.Slice(x * 4, 4));
            }
        }

        if (owned) reduced.Release();

        var placement = new LayerTransform(new Point(box.X, box.Y), new Size(box.Width, box.Height));
        return (result, placement);
    }

    /// <summary>Twice the signed area of the quadrilateral — the shoelace sum.</summary>
    private static double Area(IReadOnlyList<Point> corners)
    {
        double sum = 0;
        for (int i = 0; i < corners.Count; i++)
        {
            Point a = corners[i], b = corners[(i + 1) % corners.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    /// <summary>
    /// The map from the unit square to the corners, as nine numbers in row order.
    /// </summary>
    /// <remarks>
    /// Corner order is the handles' own: (0,0), (1,0), (1,1), (0,1). A quadrilateral whose
    /// opposite sides stay parallel has no projective part, and the last row falls out as (0, 0, 1)
    /// — an ordinary affine map, and worth keeping exact rather than dividing by a difference of
    /// two numbers that are meant to be equal.
    /// </remarks>
    private static double[] Map(IReadOnlyList<Point> corners)
    {
        double x0 = corners[0].X, y0 = corners[0].Y;
        double x1 = corners[1].X, y1 = corners[1].Y;
        double x2 = corners[2].X, y2 = corners[2].Y;
        double x3 = corners[3].X, y3 = corners[3].Y;

        double dx1 = x1 - x2, dx2 = x3 - x2, dx3 = x0 - x1 + x2 - x3;
        double dy1 = y1 - y2, dy2 = y3 - y2, dy3 = y0 - y1 + y2 - y3;

        if (Math.Abs(dx3) < 1e-12 && Math.Abs(dy3) < 1e-12)
        {
            return [x1 - x0, x3 - x0, x0,
                    y1 - y0, y3 - y0, y0,
                    0, 0, 1];
        }

        double denominator = dx1 * dy2 - dy1 * dx2;
        if (Math.Abs(denominator) < 1e-12) return [1, 0, 0, 0, 1, 0, 0, 0, 1];

        double g = (dx3 * dy2 - dy3 * dx2) / denominator;
        double h = (dx1 * dy3 - dy1 * dx3) / denominator;

        return [x1 - x0 + g * x1, x3 - x0 + h * x3, x0,
                y1 - y0 + g * y1, y3 - y0 + h * y3, y0,
                g, h, 1];
    }

    /// <summary>The inverse of a 3×3 map, or null when it has none.</summary>
    private static double[]? Invert(double[] m)
    {
        double a = m[0], b = m[1], c = m[2];
        double d = m[3], e = m[4], f = m[5];
        double g = m[6], h = m[7], i = m[8];

        double determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (Math.Abs(determinant) < 1e-12) return null;

        return
        [
            (e * i - f * h) / determinant, (c * h - b * i) / determinant, (b * f - c * e) / determinant,
            (f * g - d * i) / determinant, (a * i - c * g) / determinant, (c * d - a * f) / determinant,
            (d * h - e * g) / determinant, (b * g - a * h) / determinant, (a * e - b * d) / determinant,
        ];
    }

    /// <summary>Bilinear sample of premultiplied pixels, transparent outside the buffer.</summary>
    private static void Sample(PixelBuffer buffer, double px, double py, Span<byte> result)
    {
        double x = px - 0.5, y = py - 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double tx = x - x0, ty = y - y0;

        Span<byte> a = stackalloc byte[4], b = stackalloc byte[4], c = stackalloc byte[4], d = stackalloc byte[4];
        Read(buffer, x0, y0, a);
        Read(buffer, x0 + 1, y0, b);
        Read(buffer, x0, y0 + 1, c);
        Read(buffer, x0 + 1, y0 + 1, d);

        for (int channel = 0; channel < 4; channel++)
        {
            double top = a[channel] * (1 - tx) + b[channel] * tx;
            double bottom = c[channel] * (1 - tx) + d[channel] * tx;
            result[channel] = (byte)Math.Clamp(
                Math.Round(top * (1 - ty) + bottom * ty, MidpointRounding.AwayFromZero), 0, 255);
        }

        for (int channel = 0; channel < 3; channel++)
            result[channel] = Math.Min(result[channel], result[3]);
    }

    private static void Read(PixelBuffer buffer, int x, int y, Span<byte> result)
    {
        if (x < 0 || y < 0 || x >= buffer.Width || y >= buffer.Height)
        {
            result.Clear();
            return;
        }

        buffer.Row(y).Slice(x * 4, 4).CopyTo(result);
    }
}
