namespace Compositor_korean_win.Core;

/// <summary>
/// Photoshop's Filter › Distort family: Pinch, Spherize, Twirl, Wave and Polar Coordinates.
/// </summary>
/// <remarks>
/// <para>
/// Upstream has none of these; they are Photoshop's, added at a user's request. Each is a map from a
/// destination pixel back to where in the source it reads — the inverse mapping every resample here
/// uses (QuadWarp, the software backend), so every destination pixel is filled exactly once and
/// nothing tears.
/// </para>
/// <para>
/// Pinch, Spherize and Polar Coordinates work in the buffer's own unit ellipse — the one that fits
/// it — so a reduced preview and the full-size commit give the same picture, and a wide layer
/// bulges as a wide ellipse as Photoshop's does. Twirl turns within the inscribed circle, since
/// turning an ellipse would shear it. Wave's lengths are layer pixels and scale with the preview.
/// </para>
/// <para>
/// Pixels are read bilinearly in premultiplied form; outside the source reads as transparent, so
/// a pinch that pulls the edge inwards leaves the corners clear rather than smearing them.
/// </para>
/// </remarks>
public static class DistortFilters
{
    /// <summary>Whether <paramref name="kind"/> moves pixels around the whole layer, so a preview must see all of it.</summary>
    public static bool IsGeometric(FilterKind kind) =>
        kind is FilterKind.LensCorrection or FilterKind.Pinch or FilterKind.Spherize or FilterKind.Twirl
            or FilterKind.Wave or FilterKind.PolarCoordinates;

    /// <summary>Runs one of the family; <paramref name="scale"/> is source pixels per layer pixel.</summary>
    public static PixelBuffer Run(PixelBuffer source, FilterKind kind, FilterSettings settings, double scale)
    {
        FilterSettings s = settings.Normalized;
        double w = source.Width, h = source.Height;
        double cx = w / 2, cy = h / 2, rx = Math.Max(0.5, w / 2), ry = Math.Max(0.5, h / 2);

        switch (kind)
        {
            case FilterKind.Pinch:
            {
                // GIMP's and Photoshop's shape: sin(πr/2)^-k, which is 1 at the rim so the edge of
                // the ellipse stays put. Positive pulls the middle in, negative pushes it out.
                double k = s.Strength / 100;
                if (k == 0) return PixelFilters.Copy(source);
                return Remap(source, (x, y) =>
                {
                    double nx = (x - cx) / rx, ny = (y - cy) / ry;
                    double r = Math.Sqrt(nx * nx + ny * ny);
                    if (r >= 1 || r == 0) return (x, y);
                    double factor = Math.Pow(Math.Sin(Math.PI / 2 * r), -k);
                    return (cx + nx * factor * rx, cy + ny * factor * ry);
                });
            }

            case FilterKind.Spherize:
            {
                // Positive reads nearer the middle than the pixel is — the middle swells as on a
                // ball; negative reads further out and it shrinks, as in a bowl. Both meet the
                // unchanged picture at the rim.
                double k = s.Strength / 100;
                if (k == 0) return PixelFilters.Copy(source);
                return Remap(source, (x, y) =>
                {
                    double nx = (x - cx) / rx, ny = (y - cy) / ry;
                    double r = Math.Sqrt(nx * nx + ny * ny);
                    if (r >= 1 || r == 0) return (x, y);
                    double curved = k > 0
                        ? 1 - Math.Sqrt(1 - r * r)
                        : Math.Sqrt(1 - (1 - r) * (1 - r));
                    double read = r + (curved - r) * Math.Abs(k);
                    return (cx + nx / r * read * rx, cy + ny / r * read * ry);
                });
            }

            case FilterKind.Twirl:
            {
                // The full angle at the centre, falling away to none at the rim. Positive turns the
                // picture clockwise on screen, as Photoshop's does.
                double angle = s.TwirlAngle * Math.PI / 180;
                if (angle == 0) return PixelFilters.Copy(source);
                double radius = Math.Min(rx, ry);
                return Remap(source, (x, y) =>
                {
                    double dx = x - cx, dy = y - cy;
                    double r = Math.Sqrt(dx * dx + dy * dy) / radius;
                    if (r >= 1) return (x, y);
                    double turn = -angle * (1 - r) * (1 - r);
                    double cos = Math.Cos(turn), sin = Math.Sin(turn);
                    return (cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
                });
            }

            case FilterKind.Wave:
            {
                // Rows slide sideways by a sine of their height and columns up and down by a sine of
                // their place along, so straight lines come out as waves both ways.
                double length = s.Wavelength * scale, amplitude = s.Amplitude * scale;
                if (amplitude == 0) return PixelFilters.Copy(source);
                double step = 2 * Math.PI / Math.Max(1e-6, length);
                return Remap(source, (x, y) => (x + amplitude * Math.Sin(y * step), y + amplitude * Math.Sin(x * step)));
            }

            case FilterKind.PolarCoordinates:
                return s.ToPolar
                    // The top row gathers to the centre and the bottom row runs round the rim; left
                    // to right goes clockwise from twelve o'clock.
                    ? Remap(source, (x, y) =>
                    {
                        double nx = (x - cx) / rx, ny = (y - cy) / ry;
                        double r = Math.Sqrt(nx * nx + ny * ny);
                        if (r > 1) return (double.NaN, double.NaN);
                        double turn = Math.Atan2(nx, -ny);
                        if (turn < 0) turn += 2 * Math.PI;
                        return (turn / (2 * Math.PI) * w, r * h);
                    })
                    // And back: each column is a spoke from the centre, each row a ring.
                    : Remap(source, (x, y) =>
                    {
                        double turn = x / w * 2 * Math.PI, r = y / h;
                        return (cx + r * rx * Math.Sin(turn), cy - r * ry * Math.Cos(turn));
                    });

            default:
                throw new ArgumentException($"{kind} is not a distortion", nameof(kind));
        }
    }

    /// <summary>
    /// A new buffer the size of <paramref name="source"/>, each pixel read from where
    /// <paramref name="map"/> sends its centre. NaN reads as transparent. Rows run in parallel:
    /// the map is pure and each row is written by one worker only.
    /// </summary>
    public static PixelBuffer Remap(PixelBuffer source, Func<double, double, (double X, double Y)> map)
    {
        PixelBuffer result = PixelBuffer.Allocate(source.Width, source.Height);
        nint sourcePixels = source.Scan0;
        Parallel.For(0, source.Height, y =>
        {
            Span<byte> row = result.Row(y);
            for (int x = 0; x < source.Width; x++)
            {
                (double sx, double sy) = map(x + 0.5, y + 0.5);
                if (double.IsNaN(sx) || double.IsNaN(sy)) continue;
                PixelSampling.Bilinear(sourcePixels, source.Stride, source.Width, source.Height,
                                       sx, sy, row.Slice(x * 4, 4));
            }
        });
        return result;
    }
}

/// <summary>Reading a buffer between its pixels.</summary>
public static class PixelSampling
{
    /// <summary>
    /// The premultiplied colour at (<paramref name="x"/>, <paramref name="y"/>), where a pixel's
    /// centre is at +0.5, blended from the four nearest. Outside the buffer is transparent.
    /// </summary>
    public static void Bilinear(PixelBuffer buffer, double x, double y, Span<byte> into)
        => Bilinear(buffer.Scan0, buffer.Stride, buffer.Width, buffer.Height, x, y, into);

    /// <summary>
    /// The same sample after a caller has materialised the immutable source once. Hot resampling
    /// loops use this overload so four taps do not repeat PixelBuffer state checks for every pixel.
    /// </summary>
    internal static unsafe void Bilinear(nint scan0, int stride, int width, int height,
                                         double x, double y, Span<byte> into)
    {
        double fx = x - 0.5, fy = y - 0.5;
        if (!(fx > -1 && fy > -1 && fx < width && fy < height))
        {
            into.Clear();
            return;
        }

        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        float tx = (float)(fx - x0), ty = (float)(fy - y0);
        float w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty), w01 = (1 - tx) * ty, w11 = tx * ty;

        float r = 0, g = 0, b = 0, a = 0;
        Add(x0, y0, w00);
        Add(x0 + 1, y0, w10);
        Add(x0, y0 + 1, w01);
        Add(x0 + 1, y0 + 1, w11);

        byte alpha = (byte)Math.Clamp(a + 0.5f, 0, 255);
        into[0] = Math.Min((byte)Math.Clamp(r + 0.5f, 0, 255), alpha);
        into[1] = Math.Min((byte)Math.Clamp(g + 0.5f, 0, 255), alpha);
        into[2] = Math.Min((byte)Math.Clamp(b + 0.5f, 0, 255), alpha);
        into[3] = alpha;

        void Add(int px, int py, float weight)
        {
            if (weight <= 0 || px < 0 || py < 0 || px >= width || py >= height) return;
            byte* pixel = (byte*)scan0 + py * stride + px * 4;
            r += pixel[0] * weight;
            g += pixel[1] * weight;
            b += pixel[2] * weight;
            a += pixel[3] * weight;
        }
    }
}
