namespace Compositor_korean_win.Core;

/// <summary>
/// Edit › Transform › Warp: a layer bent over a bicubic Bézier patch — Photoshop's warp grid of
/// sixteen points, in document pixels, row by row from the top-left.
/// </summary>
/// <remarks>
/// <para>
/// Upstream has no warp; this is Photoshop's, added at a user's request. The corners and the eight
/// points beside them are what Photoshop shows as anchors and handles; the four inside are the
/// patch's interior points, which a drag on the surface moves.
/// </para>
/// <para>
/// Like a distortion (<see cref="QuadWarp"/>) this cannot live in a <see cref="LayerTransform"/>, so
/// committing resamples the pixels once and the layer comes back upright. Until then the canvas
/// shows the same resampling run into the frame's own pixels (<see cref="WarpPreview"/>).
/// </para>
/// <para>
/// A patch has no closed-form inverse, so it is drawn forwards: cut into small triangles, each
/// filled with an affine map back into the source. With enough triangles for the size, the
/// difference from the true surface is well under a pixel.
/// </para>
/// </remarks>
public sealed record WarpMesh
{
    /// <summary>Points on a side of the grid.</summary>
    public const int Side = 4;

    public WarpMesh(IReadOnlyList<Point> points)
    {
        if (points.Count != Side * Side) throw new ArgumentException("sixteen points", nameof(points));
        Points = [.. points];
    }

    public IReadOnlyList<Point> Points { get; }

    public Point this[int row, int column] => Points[row * Side + column];

    /// <summary>The grid that leaves a layer as it is: its box cut in thirds.</summary>
    public static WarpMesh Flat(LayerTransform placement)
    {
        var points = new Point[Side * Side];
        for (int row = 0; row < Side; row++)
            for (int column = 0; column < Side; column++)
                points[row * Side + column] = placement.PointAt(new Point(column / 3.0, row / 3.0));
        return new WarpMesh(points);
    }

    /// <summary>
    /// A grid bent into one of the text warp's shapes — Photoshop offers the same fifteen for a
    /// layer. Each point of the flat grid is carried through the shape in the layer's own upright
    /// box, then placed as the layer is; the result is a starting point to edit further.
    /// </summary>
    public static WarpMesh Preset(LayerTransform placement, TextWarp warp)
    {
        if (warp.Style == TextWarpStyle.None) return Flat(placement);
        double width = placement.Size.Width, height = placement.Size.Height;
        var box = new Rect(0, 0, width, height);
        var points = new Point[Side * Side];
        for (int row = 0; row < Side; row++)
        {
            for (int column = 0; column < Side; column++)
            {
                Point bent = TextWarping.Map(new Point(column / 3.0 * width, row / 3.0 * height), box, warp);
                points[row * Side + column] = placement.PointAt(new Point(bent.X / width, bent.Y / height));
            }
        }
        return new WarpMesh(points);
    }

    /// <summary>The cubic Bernstein weights at <paramref name="t"/>.</summary>
    private static (double, double, double, double) Weights(double t)
    {
        double s = 1 - t;
        return (s * s * s, 3 * t * s * s, 3 * t * t * s, t * t * t);
    }

    private static double Weight(int i, double t) => i switch
    {
        0 => (1 - t) * (1 - t) * (1 - t),
        1 => 3 * t * (1 - t) * (1 - t),
        2 => 3 * t * t * (1 - t),
        _ => t * t * t,
    };

    /// <summary>Where the surface is at (<paramref name="u"/>, <paramref name="v"/>) of the layer's unit square.</summary>
    public Point At(double u, double v)
    {
        (double a0, double a1, double a2, double a3) = Weights(u);
        (double b0, double b1, double b2, double b3) = Weights(v);
        Span<double> across = stackalloc double[] { a0, a1, a2, a3 };
        Span<double> down = stackalloc double[] { b0, b1, b2, b3 };
        double x = 0, y = 0;
        for (int row = 0; row < Side; row++)
        {
            for (int column = 0; column < Side; column++)
            {
                double w = down[row] * across[column];
                Point p = Points[row * Side + column];
                x += p.X * w;
                y += p.Y * w;
            }
        }
        return new Point(x, y);
    }

    /// <summary>Whether a point is a corner, which carries the two handles beside it.</summary>
    public static bool IsCorner(int index) => index is 0 or 3 or 12 or 15;

    /// <summary>
    /// The grid with one point moved. A corner takes its two handles with it, as dragging an anchor
    /// does in Photoshop, so the curve leaving it keeps its direction.
    /// </summary>
    public WarpMesh Moved(int index, double dx, double dy)
    {
        Point[] points = [.. Points];
        Move(index);
        if (IsCorner(index))
        {
            int row = index / Side, column = index % Side;
            Move(row * Side + (column == 0 ? 1 : 2));
            Move((row == 0 ? 1 : 2) * Side + column);
        }
        return new WarpMesh(points);

        void Move(int i) => points[i] = new Point(points[i].X + dx, points[i].Y + dy);
    }

    /// <summary>
    /// The grid moved so the surface point at (<paramref name="u"/>, <paramref name="v"/>) goes by
    /// (<paramref name="dx"/>, <paramref name="dy"/>) — a drag on the picture itself.
    /// </summary>
    /// <remarks>
    /// Each point moves in proportion to how much it pulls on that spot: the least change to the
    /// grid that lands the spot exactly under the pointer. Points far from the spot hardly move.
    /// </remarks>
    public WarpMesh Dragged(double u, double v, double dx, double dy)
    {
        var weights = new double[Side * Side];
        double total = 0;
        for (int row = 0; row < Side; row++)
        {
            for (int column = 0; column < Side; column++)
            {
                double w = Weight(row, v) * Weight(column, u);
                weights[row * Side + column] = w;
                total += w * w;
            }
        }
        if (total < 1e-12) return this;

        Point[] points = [.. Points];
        for (int i = 0; i < points.Length; i++)
        {
            double share = weights[i] / total;
            points[i] = new Point(points[i].X + dx * share, points[i].Y + dy * share);
        }
        return new WarpMesh(points);
    }

    /// <summary>
    /// Where on the layer's unit square a document point lies, if the surface covers it — for
    /// grabbing the picture. Found on a grid of triangles fine enough to grab by.
    /// </summary>
    public (double U, double V)? Find(Point point)
    {
        const int cells = 24;
        Point[,] grid = Tessellate(this, cells, p => p);
        for (int j = 0; j < cells; j++)
        {
            for (int i = 0; i < cells; i++)
            {
                if (Inside(grid[i, j], grid[i + 1, j], grid[i + 1, j + 1], point, out double a, out double b, out double c))
                    return ((i * a + (i + 1) * b + (i + 1) * c) / cells, (j * a + j * b + (j + 1) * c) / cells);
                if (Inside(grid[i, j], grid[i + 1, j + 1], grid[i, j + 1], point, out a, out b, out c))
                    return ((i * a + (i + 1) * b + i * c) / cells, (j * a + (j + 1) * b + (j + 1) * c) / cells);
            }
        }
        return null;
    }

    /// <summary>The surface sampled at <paramref name="cells"/>² squares, each point carried by <paramref name="onto"/>.</summary>
    internal static Point[,] Tessellate(WarpMesh mesh, int cells, Func<Point, Point> onto)
    {
        var grid = new Point[cells + 1, cells + 1];
        for (int j = 0; j <= cells; j++)
            for (int i = 0; i <= cells; i++)
                grid[i, j] = onto(mesh.At((double)i / cells, (double)j / cells));
        return grid;
    }

    /// <summary>Barycentric weights of <paramref name="p"/> in the triangle, and whether it is inside.</summary>
    private static bool Inside(Point p0, Point p1, Point p2, Point p, out double w0, out double w1, out double w2)
    {
        double area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
        w0 = w1 = w2 = 0;
        if (Math.Abs(area) < 1e-12) return false;
        w1 = ((p.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p.Y - p0.Y)) / area;
        w2 = ((p1.X - p0.X) * (p.Y - p0.Y) - (p.X - p0.X) * (p1.Y - p0.Y)) / area;
        w0 = 1 - w1 - w2;
        const double slack = -1e-9;
        return w0 >= slack && w1 >= slack && w2 >= slack;
    }

    /// <summary>
    /// <paramref name="source"/> drawn over the surface, each surface point first carried by
    /// <paramref name="onto"/> (the view's projection for a preview, nothing for a commit). The
    /// result covers the whole pixels the surface reaches, cut to <paramref name="clip"/>; null when
    /// there is nothing to draw or it would be absurdly large.
    /// </summary>
    /// <remarks>
    /// The output is cut into bands of rows, each filled by one worker from every triangle that
    /// reaches it, so the result does not depend on the order the workers run in. Where the surface
    /// folds over itself the later triangle in reading order wins, in every band alike.
    /// </remarks>
    public static (PixelBuffer Pixels, PixelRect Box)? Draw(WarpMesh mesh, PixelBuffer source, Func<Point, Point> onto,
                                                             PixelRect? clip = null, int maximumCells = 96)
    {
        // Coarse first, to size the job: the triangles only need to be small on the output.
        Point[,] coarse = Tessellate(mesh, 8, onto);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (Point p in coarse)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return null;
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        // The surface bulges past its control polygon's coarse samples only slightly; a margin covers it.
        double reach = Math.Max(maxX - minX, maxY - minY);
        var extent = PixelRect.FromBounds((int)Math.Floor(minX - reach * 0.05) - 2, (int)Math.Floor(minY - reach * 0.05) - 2,
                                          (int)Math.Ceiling(maxX + reach * 0.05) + 2, (int)Math.Ceiling(maxY + reach * 0.05) + 2);
        if (clip is PixelRect within) extent = extent.Intersect(within);
        if (extent.IsEmpty || extent.Width > ProjectLimits.MaximumSide || extent.Height > ProjectLimits.MaximumSide
                           || (long)extent.Width * extent.Height > ProjectLimits.MaximumPixels) return null;

        int cells = Math.Clamp((int)Math.Ceiling(reach / 12), 12, Math.Clamp(maximumCells, 12, 96));
        Point[,] destination = Tessellate(mesh, cells, onto);

        // Every triangle, with where its corners read from in the source.
        var triangles = new List<(Point A, Point B, Point C, Point Sa, Point Sb, Point Sc)>(cells * cells * 2);
        double sw = source.Width, sh = source.Height;
        Point Source(int i, int j) => new(i * sw / cells, j * sh / cells);
        for (int j = 0; j < cells; j++)
        {
            for (int i = 0; i < cells; i++)
            {
                triangles.Add((destination[i, j], destination[i + 1, j], destination[i + 1, j + 1],
                               Source(i, j), Source(i + 1, j), Source(i + 1, j + 1)));
                triangles.Add((destination[i, j], destination[i + 1, j + 1], destination[i, j + 1],
                               Source(i, j), Source(i + 1, j + 1), Source(i, j + 1)));
            }
        }

        const int band = 32;
        int bands = (extent.Height + band - 1) / band;
        var byBand = new List<(Point A, Point B, Point C, Point Sa, Point Sb, Point Sc)>[bands];
        foreach ((Point a, Point b, Point c, Point sa, Point sb, Point sc) in triangles)
        {
            int first = Math.Clamp(((int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)) - 0.5) - extent.Y) / band, 0, bands - 1);
            int last = Math.Clamp(((int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y)) - 0.5) - extent.Y) / band, 0, bands - 1);
            for (int i = first; i <= last; i++) (byBand[i] ??= []).Add((a, b, c, sa, sb, sc));
        }

        PixelBuffer result = PixelBuffer.Allocate(extent.Width, extent.Height);
        try
        {
            nint sourcePixels = source.Scan0;
            Parallel.For(0, bands, b =>
            {
                int top = extent.Y + b * band, bottom = Math.Min(extent.Bottom, top + band);
                foreach ((Point a, Point bb, Point c, Point sa, Point sb, Point sc) in byBand[b] ?? [])
                {
                    double tMinY = Math.Min(a.Y, Math.Min(bb.Y, c.Y)), tMaxY = Math.Max(a.Y, Math.Max(bb.Y, c.Y));
                    int y0 = Math.Max(top, (int)Math.Floor(tMinY - 0.5)), y1 = Math.Min(bottom - 1, (int)Math.Ceiling(tMaxY - 0.5));
                    if (y0 > y1) continue;
                    double tMinX = Math.Min(a.X, Math.Min(bb.X, c.X)), tMaxX = Math.Max(a.X, Math.Max(bb.X, c.X));
                    int x0 = Math.Max(extent.X, (int)Math.Floor(tMinX - 0.5)), x1 = Math.Min(extent.Right - 1, (int)Math.Ceiling(tMaxX - 0.5));
                    if (x0 > x1) continue;

                    for (int y = y0; y <= y1; y++)
                    {
                        Span<byte> row = result.Row(y - extent.Y);
                        for (int x = x0; x <= x1; x++)
                        {
                            if (!Inside(a, bb, c, new Point(x + 0.5, y + 0.5), out double w0, out double w1, out double w2)) continue;
                            double px = sa.X * w0 + sb.X * w1 + sc.X * w2, py = sa.Y * w0 + sb.Y * w1 + sc.Y * w2;
                            PixelSampling.Bilinear(sourcePixels, source.Stride, source.Width, source.Height,
                                                   px, py, row.Slice((x - extent.X) * 4, 4));
                        }
                    }
                }
            });

            return (result, extent);
        }
        catch
        {
            result.Release();
            throw;
        }
    }

    /// <summary>
    /// A layer warped over the grid for good, upright again at the box the surface covers, and its
    /// mask with it when the mask shares the layer's pixels. Null when there is nothing to warp.
    /// </summary>
    /// <remarks>
    /// A mask placed apart or unlinked stays where it was on the document, as it does for a
    /// distortion that cannot carry it.
    /// </remarks>
    public static ImageLayer? Warp(ImageLayer layer, WarpMesh mesh)
    {
        if (layer.Image is not PixelBuffer image) return null;

        // The grid is in document pixels; the source is read in the layer's own, through the
        // unit square the grid's corners stand for.
        if (Draw(mesh, image, p => p) is not (PixelBuffer drawn, PixelRect extent)) return null;

        // Drawing leaves a margin for how far a patch can bulge; the layer keeps only what it covers.
        PixelRect kept = LayerFilters.Trim(drawn);
        if (kept.IsEmpty)
        {
            drawn.Release();
            return null;
        }
        PixelBuffer pixels = Cropped(drawn, kept);
        var box = new PixelRect(extent.X + kept.X, extent.Y + kept.Y, kept.Width, kept.Height);

        LayerMask? mask = layer.Mask;
        if (mask is { Coverage.Width: 1, Coverage.Height: 1 })
        {
            // Uniform: nothing to carry.
        }
        else if (mask is { IsLinked: true, Placement: null }
                 && Draw(mesh, mask.Coverage, p => p, box) is (PixelBuffer warped, PixelRect maskBox))
        {
            PixelBuffer fitted = maskBox == box ? warped : Cropped(warped, new PixelRect(box.X - maskBox.X, box.Y - maskBox.Y, box.Width, box.Height));
            QuadWarp.Opaque(fitted);
            mask = mask with { Coverage = fitted };
        }
        else if (mask is LayerMask placed)
        {
            mask = placed with { Placement = placed.Placement ?? layer.Transform };
        }

        var placement = new LayerTransform(new Point(box.X, box.Y), new Size(box.Width, box.Height));
        return layer with { Image = pixels, Transform = placement, Mask = mask, Shape = null };

        // A region of a buffer this owns, the rest let go.
        static PixelBuffer Cropped(PixelBuffer whole, PixelRect region)
        {
            if (region.X == 0 && region.Y == 0 && region.Width == whole.Width && region.Height == whole.Height) return whole;
            PixelBuffer part = PixelRegion.Copy(whole, region);
            whole.Release();
            return part;
        }
    }
}

/// <summary>
/// A layer shown bent over a warp grid while the grid is being edited: the same drawing as the
/// commit, run into a bounded number of frame pixels while the pointer is moving.
/// </summary>
public sealed class WarpPreview(ImageLayer layer) : IDisposable
{
    private const long PreviewPixelBudget = 512 * 512;
    private PixelBuffer? _last;
    private LiveEdit? _lastEdit;
    private Point[]? _lastPoints;
    private CanvasProjection _lastProjection;
    private int _lastWidth;
    private int _lastHeight;

    public ImageLayer Layer { get; } = layer;

    /// <summary>Pixels allocated for the last frame, for performance checks.</summary>
    public long LastPixelsWarped { get; private set; }

    public LiveEdit? Frame(WarpMesh mesh, CanvasProjection projection, int width, int height)
    {
        if (Layer.Image is not PixelBuffer image) return null;
        if (_lastEdit is not null && _lastPoints is not null && _lastPoints.SequenceEqual(mesh.Points)
                                      && _lastProjection == projection
                                      && _lastWidth == width && _lastHeight == height)
            return _lastEdit;

        Point[] controls = [.. mesh.Points.Select(projection.Apply)];
        double rasterScale = PreviewScale(controls, width, height);
        Point Onto(Point point)
        {
            Point surface = projection.Apply(point);
            return new Point(surface.X * rasterScale, surface.Y * rasterScale);
        }
        int rasterWidth = Math.Max(1, (int)Math.Ceiling(width * rasterScale));
        int rasterHeight = Math.Max(1, (int)Math.Ceiling(height * rasterScale));

        // Sampling the original directly avoids building a full-image pyramid merely to show a
        // small placed copy. The bounded surface buffer keeps interaction responsive; commit is full quality.
        if (WarpMesh.Draw(mesh, image, Onto, new PixelRect(0, 0, rasterWidth, rasterHeight), maximumCells: 64)
            is not (PixelBuffer pixels, PixelRect box)) return null;

        _last?.Release();
        _last = pixels;
        LastPixelsWarped = (long)pixels.Width * pixels.Height;

        // Back onto the document, where the compositor projects it again onto the same whole pixels.
        var onDocument = new LayerTransform(
            projection.Invert(new Point(box.X / rasterScale, box.Y / rasterScale)),
            new Size(box.Width / rasterScale / projection.Scale, box.Height / rasterScale / projection.Scale))
        {
            Sampling = LayerSampling.Nearest,
        };

        // A mask that goes with the pixels is shown where it will be; one that stays behind is not
        // drawn in the preview, which the commit would otherwise contradict.
        PixelBuffer? mask = Layer.Mask is { Coverage: { Width: 1, Height: 1 } uniform } ? uniform : null;
        _lastPoints = [.. mesh.Points];
        _lastProjection = projection;
        _lastWidth = width;
        _lastHeight = height;
        _lastEdit = new LiveEdit(Layer.Id, new BufferSource(pixels)) { Placement = onDocument, Mask = mask };
        return _lastEdit;
    }

    private static double PreviewScale(IReadOnlyList<Point> controls, int width, int height)
    {
        Rect bounds = Rect.Around(controls);
        double reach = Math.Max(bounds.Width, bounds.Height);
        double margin = reach * 0.05 + 2;
        double left = Math.Max(0, bounds.X - margin), top = Math.Max(0, bounds.Y - margin);
        double right = Math.Min(width, bounds.MaxX + margin), bottom = Math.Min(height, bounds.MaxY + margin);
        double pixels = Math.Max(1, right - left) * Math.Max(1, bottom - top);
        return pixels <= PreviewPixelBudget ? 1 : Math.Sqrt(PreviewPixelBudget / pixels);
    }

    public void Dispose()
    {
        _last?.Release();
        _last = null;
        _lastEdit = null;
        _lastPoints = null;
    }
}
