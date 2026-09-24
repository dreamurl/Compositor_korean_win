using System.Text;

namespace Compositor_korean_win.Core;

/// <summary>Text laid out as outlines, in pixels with the first baseline's start at the origin line.</summary>
/// <param name="Loops">Every glyph contour, flattened to points.</param>
/// <param name="Box">The text box: the lines' full width, top of the first line to the bottom of the last.</param>
/// <param name="Anchor">Where the text was set from — see <see cref="LayerText.AnchorX"/>.</param>
public sealed record TextOutline(IReadOnlyList<IReadOnlyList<Point>> Loops, Rect Box, Point Anchor);

/// <summary>A text layer's pixels, and where its anchor fell in them.</summary>
/// <remarks>The caller owns <paramref name="Pixels"/> and releases them.</remarks>
public sealed record RenderedText(PixelBuffer Pixels, Point Anchor);

/// <summary>
/// Sets a <see cref="LayerText"/>: lines, alignment, tracking, the warp, and the fill.
/// </summary>
/// <remarks>
/// Layout is deliberately simple — one run per line, glyph advances plus tracking, no kerning, no
/// shaping and no line wrapping — because what the text tool is for is the kind of display type a
/// poster uses, set line by line. Hangul syllables are precomposed in Unicode, so a Korean line
/// needs no shaping to come out right; scripts that do (Arabic, Devanagari) would.
/// </remarks>
public static class TextRendering
{
    /// <summary>A flattened curve is kept within this many pixels of the true one.</summary>
    private const double Flatness = 0.2;

    /// <summary>Warped outlines are split to this length first, in pixels, so straight edges bend.</summary>
    private const double WarpStep = 2;

    /// <summary>Space left round the ink, so the soft edge is never cut off.</summary>
    private const int Margin = 2;

    public static TextOutline Layout(LayerText text, IGlyphSource glyphs)
    {
        double size = text.Size;
        TextFace face = text.Face;
        FontMetricsEm metrics = glyphs.Metrics(face);
        double ascent = metrics.Ascent * size, descent = metrics.Descent * size;
        double step = text.Leading * size;
        double tracking = text.Tracking / 1000 * size;

        string[] lines = text.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var perLine = new List<(List<List<Point>> Loops, double Width)>(lines.Length);
        double widest = 0;

        for (int index = 0; index < lines.Length; index++)
        {
            double baseline = ascent + index * step;
            double pen = 0;
            bool any = false;
            var loops = new List<List<Point>>();

            foreach (Rune rune in lines[index].EnumerateRunes())
            {
                int codepoint = rune.Value == '\t' ? ' ' : rune.Value;
                GlyphShape glyph = glyphs.Glyph(face, codepoint);
                foreach (GlyphFigure figure in glyph.Figures)
                {
                    List<Point> loop = Flatten(figure, pen, baseline, size);
                    if (loop.Count >= 3) loops.Add(loop);
                }

                int repeat = rune.Value == '\t' ? 4 : 1;
                pen += (glyph.Advance * size + tracking) * repeat;
                any = true;
            }

            double width = any ? Math.Max(0, pen - tracking) : 0;
            widest = Math.Max(widest, width);
            perLine.Add((loops, width));
        }

        var all = new List<IReadOnlyList<Point>>();
        foreach ((List<List<Point>> loops, double width) in perLine)
        {
            double shift = text.Align switch
            {
                TextAlign.Center => (widest - width) / 2,
                TextAlign.Right => widest - width,
                _ => 0,
            };
            foreach (List<Point> loop in loops)
            {
                if (shift != 0)
                    for (int i = 0; i < loop.Count; i++) loop[i] = new Point(loop[i].X + shift, loop[i].Y);
                all.Add(loop);
            }
        }

        double height = ascent + (lines.Length - 1) * step + descent;
        var box = new Rect(0, 0, Math.Max(1, widest), Math.Max(1, height));
        double anchorX = text.Align switch
        {
            TextAlign.Center => widest / 2,
            TextAlign.Right => widest,
            _ => 0,
        };
        return new TextOutline(all, box, new Point(anchorX, ascent));
    }

    /// <summary>The text's outlines once the warp has bent them.</summary>
    public static TextOutline Warped(TextOutline outline, TextWarp? warp)
    {
        if (warp is null || warp.IsIdentity) return outline;

        var loops = new List<IReadOnlyList<Point>>(outline.Loops.Count);
        foreach (IReadOnlyList<Point> loop in outline.Loops)
        {
            List<Point> dense = TextWarping.Densify(loop, WarpStep);
            for (int i = 0; i < dense.Count; i++) dense[i] = TextWarping.Map(dense[i], outline.Box, warp);
            loops.Add(dense);
        }
        return outline with { Loops = loops, Anchor = TextWarping.Map(outline.Anchor, outline.Box, warp) };
    }

    /// <summary>The text as pixels, cropped to its ink.</summary>
    public static RenderedText Render(LayerText text, IGlyphSource glyphs)
    {
        TextOutline outline = Warped(Layout(text, glyphs), text.Warp);

        Rect ink = Bounds(outline.Loops) ?? new Rect(outline.Anchor.X, outline.Anchor.Y, 1, 1);
        int left = (int)Math.Floor(ink.MinX) - Margin, top = (int)Math.Floor(ink.MinY) - Margin;
        int right = (int)Math.Ceiling(ink.MaxX) + Margin, bottom = (int)Math.Ceiling(ink.MaxY) + Margin;

        // Held to what the format stores, so a 5000-pixel word bent into a spiral cannot allocate
        // without bound.
        int width = Math.Clamp(right - left, 1, ProjectLimits.MaximumSide);
        int height = Math.Clamp(bottom - top, 1, ProjectLimits.MaximumSide);
        var region = new PixelRect(left, top, width, height);

        byte[] coverage = OutlineRaster.Coverage(outline.Loops, region);
        PixelBuffer pixels = OutlineRaster.Paint(coverage, width, height, text.Colour);
        return new RenderedText(pixels, new Point(outline.Anchor.X - left, outline.Anchor.Y - top));
    }

    private static Rect? Bounds(IReadOnlyList<IReadOnlyList<Point>> loops)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (IReadOnlyList<Point> loop in loops)
            foreach (Point point in loop)
            {
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) continue;
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }
        return minX <= maxX ? Rect.FromBounds(minX, minY, maxX, maxY) : null;
    }

    /// <summary>One contour in pixels, its curves cut into short enough lines.</summary>
    private static List<Point> Flatten(GlyphFigure figure, double penX, double baseline, double size)
    {
        Point Place(Point em) => new(penX + em.X * size, baseline + em.Y * size);

        var points = new List<Point>(figure.Segments.Count * 4 + 1);
        Point current = Place(figure.Start);
        points.Add(current);

        foreach (GlyphSegment segment in figure.Segments)
        {
            Point end = Place(segment.End);
            if (!segment.IsCurve)
            {
                points.Add(end);
                current = end;
                continue;
            }

            Point c1 = Place(segment.Control1), c2 = Place(segment.Control2);
            // A cubic's control polygon bounds its length; its deviation from its chord shrinks
            // with the square of the pieces, so this many keeps it within the flatness.
            double hull = Distance(current, c1) + Distance(c1, c2) + Distance(c2, end);
            int pieces = (int)Math.Clamp(Math.Ceiling(Math.Sqrt(hull / Flatness) / 2), 1, 64);
            for (int k = 1; k <= pieces; k++)
            {
                double t = (double)k / pieces, s = 1 - t;
                double a = s * s * s, b = 3 * s * s * t, c = 3 * s * t * t, d = t * t * t;
                points.Add(new Point(a * current.X + b * c1.X + c * c2.X + d * end.X,
                                     a * current.Y + b * c1.Y + c * c2.Y + d * end.Y));
            }
            current = end;
        }

        // The figure closes itself; a repeated first point would only add an empty edge.
        if (points.Count > 1 && points[^1] == points[0]) points.RemoveAt(points.Count - 1);
        return points;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
}
