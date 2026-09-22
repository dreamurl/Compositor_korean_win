namespace Compositor_korean_win.Core;

/// <summary>
/// Which pixels a placed layer touches, and which of its own pixels a draw has to read.
/// </summary>
/// <remarks>
/// <para>
/// Shared by both backends, and the reason a frame's cost follows the window rather than the
/// document. A layer is not drawn by handing the whole of it to the renderer and letting the
/// renderer sort it out: the destination area is worked out first, the source region that feeds it
/// second, and only that much is ever materialised, reduced or uploaded.
/// </para>
/// <para>
/// What makes it exact is the pyramid's box filter, which reaches nothing outside its own 2×2
/// block (<see cref="DownsamplePyramid"/>): a region reduced on its own gives the same pixels as
/// that region of the whole image reduced, as long as the region is snapped to the halving grid.
/// Upstream buys the same guarantee with a margin of 16·2^level pixels, because Lanczos reaches.
/// </para>
/// </remarks>
public static class LayerGeometry
{
    /// <summary>The whole-pixel box a placement's four corners fall inside.</summary>
    public static PixelRect Bounds(LayerTransform placement)
    {
        Rect box = Rect.Around(
        [
            placement.PointAt(new Point(0, 0)), placement.PointAt(new Point(1, 0)),
            placement.PointAt(new Point(0, 1)), placement.PointAt(new Point(1, 1)),
        ]);

        return box.Enclosing();
    }

    /// <summary>How many halvings a draw should read from.</summary>
    public static int LevelFor(LayerTransform placement, int sourceWidth) =>
        placement.Sampling == LayerSampling.Nearest
            ? 0
            : DownsamplePyramid.LevelFor(placement.Size.Width / Math.Max(1, sourceWidth));

    /// <summary>
    /// The source pixels feeding <paramref name="area"/> of the destination.
    /// </summary>
    public static PixelRect SourceRegion(PixelRect area, LayerTransform placement, int width, int height)
    {
        double cos = Math.Cos(placement.Radians), sin = Math.Sin(placement.Radians);
        Point center = placement.Center;

        double minU = double.MaxValue, minV = double.MaxValue;
        double maxU = double.MinValue, maxV = double.MinValue;

        ReadOnlySpan<int> xs = [area.X, area.Right, area.X, area.Right];
        ReadOnlySpan<int> ys = [area.Y, area.Y, area.Bottom, area.Bottom];

        for (int corner = 0; corner < 4; corner++)
        {
            double dx = xs[corner] - center.X, dy = ys[corner] - center.Y;
            double localX = dx * cos + dy * sin;
            double localY = -dx * sin + dy * cos;
            if (placement.FlipX) localX = -localX;
            if (placement.FlipY) localY = -localY;

            double u = localX / placement.Size.Width + 0.5;
            double v = localY / placement.Size.Height + 0.5;
            minU = Math.Min(minU, u);
            maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v);
            maxV = Math.Max(maxV, v);
        }

        return PixelRect.FromBounds(
            (int)Math.Floor(Math.Clamp(minU, 0, 1) * width),
            (int)Math.Floor(Math.Clamp(minV, 0, 1) * height),
            (int)Math.Ceiling(Math.Clamp(maxU, 0, 1) * width),
            (int)Math.Ceiling(Math.Clamp(maxV, 0, 1) * height));
    }

    /// <summary>
    /// Pads a region by one reduced pixel for the final resample, then snaps it out to the halving
    /// grid so the reduction lines up with the whole image's.
    /// </summary>
    public static PixelRect Snap(PixelRect region, int step, int width, int height)
    {
        if (region.IsEmpty) return new PixelRect(0, 0, Math.Min(step, width), Math.Min(step, height));

        int left = region.X - step;
        int top = region.Y - step;
        int right = region.Right + step;
        int bottom = region.Bottom + step;

        left = Math.Max(0, left / step * step);
        top = Math.Max(0, top / step * step);
        right = Math.Min(width, (right + step - 1) / step * step);
        bottom = Math.Min(height, (bottom + step - 1) / step * step);

        return PixelRect.FromBounds(left, top, Math.Max(left + 1, right), Math.Max(top + 1, bottom));
    }

    /// <summary>
    /// Where a document point falls in a layer's own pixel grid.
    /// </summary>
    /// <remarks>
    /// Every tool works in this grid and not in document pixels: a brush paints into the layer's
    /// raster, so a layer scaled to a third has a third-sized brush on screen and full-sized dabs
    /// in its own pixels — which is what keeps painting non-destructive with the placement
    /// (docs/windows-port.md §2.1).
    /// </remarks>
    public static Point ToPixels(LayerTransform placement, Point document, int width, int height)
    {
        double cos = Math.Cos(placement.Radians), sin = Math.Sin(placement.Radians);
        Point centre = placement.Center;

        double dx = document.X - centre.X, dy = document.Y - centre.Y;
        double localX = dx * cos + dy * sin;
        double localY = -dx * sin + dy * cos;
        if (placement.FlipX) localX = -localX;
        if (placement.FlipY) localY = -localY;

        double u = localX / Math.Max(1e-9, placement.Size.Width) + 0.5;
        double v = localY / Math.Max(1e-9, placement.Size.Height) + 0.5;

        return new Point(u * width, v * height);
    }

    /// <summary>Where a layer pixel falls on the document — the inverse of <see cref="ToPixels"/>.</summary>
    public static Point ToDocument(LayerTransform placement, Point pixel, int width, int height)
    {
        double u = pixel.X / Math.Max(1, width);
        double v = pixel.Y / Math.Max(1, height);

        double localX = (u - 0.5) * placement.Size.Width;
        double localY = (v - 0.5) * placement.Size.Height;
        if (placement.FlipX) localX = -localX;
        if (placement.FlipY) localY = -localY;

        double cos = Math.Cos(placement.Radians), sin = Math.Sin(placement.Radians);
        Point centre = placement.Center;

        return new Point(centre.X + localX * cos - localY * sin,
                         centre.Y + localX * sin + localY * cos);
    }

    /// <summary>
    /// Where one region of a layer's pixels sits on the document, given where the whole layer does.
    /// </summary>
    /// <remarks>
    /// A rotated layer's region is a rotated rectangle of the same angle, so only its centre and
    /// its size change. Flips are the one catch: the placement's flips still apply to the region's
    /// own pixels, so the region has to be measured from the other side before its centre is taken,
    /// or a flipped layer would draw its pieces in each other's places.
    /// </remarks>
    public static LayerTransform Place(LayerTransform placement, PixelRect region, int width, int height)
    {
        double u0 = (double)region.X / width, u1 = (double)region.Right / width;
        double v0 = (double)region.Y / height, v1 = (double)region.Bottom / height;

        if (placement.FlipX) (u0, u1) = (1 - u1, 1 - u0);
        if (placement.FlipY) (v0, v1) = (1 - v1, 1 - v0);

        Point centre = placement.PointAt(new Point((u0 + u1) / 2, (v0 + v1) / 2));
        var size = new Size(placement.Size.Width * (u1 - u0), placement.Size.Height * (v1 - v0));

        return placement with
        {
            Origin = new Point(centre.X - size.Width / 2, centre.Y - size.Height / 2),
            Size = size,
        };
    }
}
