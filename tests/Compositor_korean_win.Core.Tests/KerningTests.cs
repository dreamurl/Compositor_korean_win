using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// The font's own pair kerning, which Photoshop applies by default ("Metrics") and which text read
/// from a PSD needs to set again the way Photoshop did.
/// </summary>
public class KerningTests
{
    /// <summary>
    /// The smallest GPOS table with one kern pair: glyph 1 then glyph 2 closer by 50 units, in a
    /// format 1 PairPos subtable reached through a "kern" feature.
    /// </summary>
    private static byte[] Gpos() =>
    [
        0, 1, 0, 0,             // version 1.0
        0, 10, 0, 12, 0, 26,    // script, feature and lookup lists
        0, 0,                   // 10: no scripts
        0, 1, (byte)'k', (byte)'e', (byte)'r', (byte)'n', 0, 8, // 12: one feature, "kern", at 20
        0, 0, 0, 1, 0, 0,       // 20: feature: lookup 0
        0, 1, 0, 4,             // 26: one lookup, at 30
        0, 2, 0, 0, 0, 1, 0, 8, // 30: pair adjustment, one subtable, at 38
        0, 1, 0, 12, 0, 4, 0, 0, 0, 1, 0, 18, // 38: format 1, coverage at 50, XAdvance only, one pair set at 56
        0, 1, 0, 1, 0, 1,       // 50: coverage: glyph 1
        0, 1, 0, 2, 0xFF, 0xCE, // 56: one pair: glyph 2, XAdvance -50
    ];

    [Fact]
    public void APairInGposIsReadInEms()
    {
        FontKerning kerning = FontKerning.Read(Gpos(), [], 1000)!;

        Assert.Equal(-0.05, kerning.Pair(1, 2), 9);
        Assert.Equal(0, kerning.Pair(2, 1));
        Assert.Equal(0, kerning.Pair(1, 3));
    }

    [Fact]
    public void AnOldKernTableIsReadWhenThereIsNoGpos()
    {
        byte[] kern =
        [
            0, 0, 0, 1,                 // version 0, one subtable
            0, 0, 0, 20, 0, 1,          // horizontal format 0, 20 bytes
            0, 1, 0, 6, 0, 0, 0, 0,     // one pair
            0, 3, 0, 4, 0xFF, 0xE2,     // glyph 3 then 4: -30
        ];
        FontKerning kerning = FontKerning.Read([], kern, 1000)!;
        Assert.Equal(-0.03, kerning.Pair(3, 4), 9);
    }

    [Fact]
    public void AFontWithoutKerningHasNone()
    {
        Assert.Null(FontKerning.Read([], [], 1000));
        Assert.Null(FontKerning.Read([1, 2, 3], [], 1000));
    }

    /// <summary>Square glyphs six tenths of an em wide, with A then V kerned together by a tenth.</summary>
    private sealed class KernedBoxes(bool kern) : IGlyphSource
    {
        public FontMetricsEm Metrics(TextFace face) => new(0.8, 0.2, 0);

        public GlyphShape Glyph(TextFace face, int codepoint) => new(0.6,
        [
            new GlyphFigure(new Point(0.05, 0), [GlyphSegment.Line(new Point(0.55, 0)), GlyphSegment.Line(new Point(0.55, -0.7)),
                                                 GlyphSegment.Line(new Point(0.05, -0.7))]),
        ]);

        public double Kerning(TextFace face, int left, int right) => kern && left == 'A' && right == 'V' ? -0.1 : 0;
    }

    [Fact]
    public void LayoutDrawsAKernedPairTogether()
    {
        var text = new LayerText { Text = "AV", Size = 100 };
        double plain = TextRendering.Layout(text, new KernedBoxes(kern: false)).Box.Width;
        double kerned = TextRendering.Layout(text, new KernedBoxes(kern: true)).Box.Width;

        Assert.Equal(120, plain, 6);
        Assert.Equal(110, kerned, 6);
    }
}
