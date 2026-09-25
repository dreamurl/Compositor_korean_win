namespace Compositor_korean_win.Core;

/// <summary>
/// Coverage for closed outlines under the nonzero winding rule, sampled finely enough for type.
/// </summary>
/// <remarks>
/// <para>
/// The selection rasteriser (<see cref="DocumentSelection"/>) samples four rows a pixel, which is
/// plenty for a marquee and visibly stepped on the horizontal strokes of small text. This one takes
/// sixteen, keeps only the edges crossing the current row, and measures each span's overlap with a
/// pixel exactly across — so a glyph's edge is soft in both directions.
/// </para>
/// <para>
/// Nonzero rather than even-odd because fonts rely on it: TrueType and CFF outlines overlap their
/// own strokes freely (most Hangul fonts build a syllable out of overlapping pieces), and even-odd
/// would punch holes wherever two pieces cross.
/// </para>
/// </remarks>
public static class OutlineRaster
{
    public const int Samples = 16;

    private readonly record struct Edge(double X0, double Y0, double X1, double Y1, int Direction)
    {
        public double XAt(double y) => X0 + (y - Y0) * (X1 - X0) / (Y1 - Y0);
    }

    /// <summary>One coverage byte per pixel of <paramref name="region"/>, row by row.</summary>
    public static byte[] Coverage(IEnumerable<IReadOnlyList<Point>> loops, PixelRect region, int samples = Samples)
    {
        var levels = new byte[Math.Max(0, region.Width * region.Height)];
        if (levels.Length == 0) return levels;

        var edges = new List<Edge>();
        foreach (IReadOnlyList<Point> loop in loops)
        {
            if (loop.Count < 3) continue;
            for (int i = 0; i < loop.Count; i++)
            {
                Point a = loop[i], b = loop[(i + 1) % loop.Count];
                if (a.Y == b.Y || !double.IsFinite(a.X + a.Y + b.X + b.Y)) continue;
                edges.Add(a.Y < b.Y
                    ? new Edge(a.X - region.X, a.Y - region.Y, b.X - region.X, b.Y - region.Y, 1)
                    : new Edge(b.X - region.X, b.Y - region.Y, a.X - region.X, a.Y - region.Y, -1));
            }
        }
        if (edges.Count == 0) return levels;

        edges.Sort((left, right) => left.Y0.CompareTo(right.Y0));

        var active = new List<Edge>();
        var crossings = new List<(double X, int Direction)>();
        var accumulator = new double[region.Width];
        int next = 0;
        double weight = 1.0 / samples;

        for (int y = 0; y < region.Height; y++)
        {
            // Rows no edge reaches are left at zero without being walked.
            if (active.Count == 0 && next < edges.Count && edges[next].Y0 >= y + 1) continue;

            Array.Clear(accumulator);
            bool touched = false;

            for (int sample = 0; sample < samples; sample++)
            {
                double scanline = y + (sample + 0.5) / samples;

                while (next < edges.Count && edges[next].Y0 <= scanline) active.Add(edges[next++]);
                // Half-open in y, so a vertex shared by two edges is counted once.
                active.RemoveAll(edge => edge.Y1 <= scanline);
                if (active.Count < 2) continue;

                crossings.Clear();
                foreach (Edge edge in active)
                    if (edge.Y0 <= scanline) crossings.Add((edge.XAt(scanline), edge.Direction));
                if (crossings.Count < 2) continue;
                crossings.Sort((left, right) => left.X.CompareTo(right.X));

                int winding = 0;
                for (int i = 0; i < crossings.Count - 1; i++)
                {
                    winding += crossings[i].Direction;
                    if (winding == 0) continue;
                    AddSpan(accumulator, crossings[i].X, crossings[i + 1].X, weight);
                    touched = true;
                }
            }

            if (!touched) continue;
            int row = y * region.Width;
            for (int x = 0; x < region.Width; x++)
                levels[row + x] = (byte)Math.Round(Math.Clamp(accumulator[x], 0, 1) * 255, MidpointRounding.AwayFromZero);
        }

        return levels;
    }

    private static void AddSpan(double[] accumulator, double from, double to, double weight)
    {
        if (to <= from) return;
        from = Math.Max(from, 0);
        to = Math.Min(to, accumulator.Length);
        if (to <= from) return;

        int first = (int)Math.Floor(from), last = Math.Min(accumulator.Length - 1, (int)Math.Ceiling(to) - 1);
        if (first == last)
        {
            accumulator[first] += (to - from) * weight;
            return;
        }

        accumulator[first] += (first + 1 - from) * weight;
        for (int x = first + 1; x < last; x++) accumulator[x] += weight;
        accumulator[last] += (to - last) * weight;
    }

    /// <summary>A fresh premultiplied buffer holding one colour through <paramref name="coverage"/>.</summary>
    /// <remarks>The caller owns the result and releases it.</remarks>
    public static PixelBuffer Paint(byte[] coverage, int width, int height, Rgba colour)
    {
        PixelBuffer result = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = result.Row(y);
            for (int x = 0; x < width; x++)
            {
                int level = coverage[y * width + x];
                if (level == 0) continue;
                Span<byte> pixel = row.Slice(x * 4, 4);
                pixel[0] = (byte)((colour.R * level + 127) / 255);
                pixel[1] = (byte)((colour.G * level + 127) / 255);
                pixel[2] = (byte)((colour.B * level + 127) / 255);
                pixel[3] = (byte)level;
            }
        }
        return result;
    }

    /// <summary>
    /// One colour through <paramref name="coverage"/>, laid over what <paramref name="target"/>
    /// already holds — source over, premultiplied — for a text whose letters differ in colour.
    /// </summary>
    public static void PaintOver(PixelBuffer target, byte[] coverage, Rgba colour)
    {
        int width = target.Width;
        for (int y = 0; y < target.Height; y++)
        {
            Span<byte> row = target.Row(y);
            for (int x = 0; x < width; x++)
            {
                int level = coverage[y * width + x];
                if (level == 0) continue;
                Span<byte> pixel = row.Slice(x * 4, 4);
                int keep = 255 - level;
                pixel[0] = (byte)((colour.R * level + pixel[0] * keep + 127) / 255);
                pixel[1] = (byte)((colour.G * level + pixel[1] * keep + 127) / 255);
                pixel[2] = (byte)((colour.B * level + pixel[2] * keep + 127) / 255);
                pixel[3] = (byte)((255 * level + pixel[3] * keep + 127) / 255);
            }
        }
    }
}
