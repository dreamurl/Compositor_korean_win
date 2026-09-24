namespace Compositor_korean_win.Core;

/// <summary>A font as the text tool asks for one: a family, a weight, and upright or italic.</summary>
public readonly record struct TextFace(string Family, int Weight, bool Italic);

/// <summary>A font's vertical metrics in ems.</summary>
public sealed record FontMetricsEm(double Ascent, double Descent, double LineGap);

/// <summary>One segment of a glyph's outline: a straight line, or a cubic Bézier when curved.</summary>
public readonly record struct GlyphSegment(Point Control1, Point Control2, Point End, bool IsCurve)
{
    public static GlyphSegment Line(Point end) => new(end, end, end, false);
    public static GlyphSegment Curve(Point control1, Point control2, Point end) => new(control1, control2, end, true);
}

/// <summary>One closed contour of a glyph.</summary>
public sealed record GlyphFigure(Point Start, IReadOnlyList<GlyphSegment> Segments);

/// <summary>
/// A glyph's outline and advance in ems, y down, with the pen at the origin on the baseline — the
/// frame DirectWrite's <c>GetGlyphRunOutline</c> hands back.
/// </summary>
public sealed record GlyphShape(double Advance, IReadOnlyList<GlyphFigure> Figures)
{
    public static GlyphShape Blank(double advance) => new(advance, []);
}

/// <summary>
/// Where glyph outlines come from.
/// </summary>
/// <remarks>
/// Core cannot reach a font engine — it references nothing past <c>System.*</c> — so the shell
/// supplies one over DirectWrite, and the tests supply boxes. Everything after the outlines
/// (lines, alignment, warping, filling) is here, where it can be tested without a display.
/// A source is expected to fall back on its own when a font has no glyph for a character, so that
/// Hangul typed in a Latin face still shows; the text tool never sees a missing glyph.
/// </remarks>
public interface IGlyphSource
{
    FontMetricsEm Metrics(TextFace face);

    GlyphShape Glyph(TextFace face, int codepoint);

    /// <summary>
    /// The font's own kerning between two characters, in ems — Photoshop's "Metrics" kerning. A
    /// source without kerning, or a pair drawn from a fallback font, gives 0.
    /// </summary>
    double Kerning(TextFace face, int left, int right) => 0;
}
