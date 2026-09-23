using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// Remove Background without the model: what goes into it, what comes out, and everything the
/// sheet does to the answer.
/// </summary>
public class BackgroundRemovalTests
{
    [Fact]
    public void TheInputIsThreeNormalisedPlanesAtTheModelsSize()
    {
        using PixelBuffer image = RenderFixture.Solid(300, 200, 255, 0, 0);
        float[] planes = SubjectMatte.Input(image, 16);

        Assert.Equal(3 * 16 * 16, planes.Length);
        // ImageNet's normalisation: red full, green and blue empty.
        Assert.Equal((1 - 0.485f) / 0.229f, planes[0], 3);
        Assert.Equal((0 - 0.456f) / 0.224f, planes[16 * 16], 3);
        Assert.Equal((0 - 0.406f) / 0.225f, planes[2 * 16 * 16 + 100], 3);
    }

    [Fact]
    public void ALargeImageIsHalvedBeforeItIsSampled()
    {
        // Fine stripes that one tap per output pixel would alias into solid black or white.
        using PixelBuffer stripes = RenderFixture.Coverage(512, 512, (x, _) => x % 2 == 0 ? (byte)0 : (byte)255);
        float[] planes = SubjectMatte.Input(stripes, 16);

        float level = planes[5 * 16 + 5] * 0.229f + 0.485f;
        Assert.InRange(level, 0.4f, 0.6f);
    }

    [Fact]
    public void TheLogitsComeBackAsAnOpaqueGreyMask()
    {
        float[] logits = new float[4 * 4];
        for (int i = 0; i < logits.Length; i++) logits[i] = i % 4 < 2 ? -10 : 10;

        using PixelBuffer mask = SubjectMatte.Mask(logits, 4, 40, 20);

        Assert.Equal((40, 20), (mask.Width, mask.Height));
        Assert.Equal((0, 0, 0, 255), RenderFixture.At(mask, 2, 10));
        Assert.Equal((255, 255, 255, 255), RenderFixture.At(mask, 37, 10));
    }

    private static float[] HalfLogits(int side) =>
        [.. Enumerable.Range(0, side * side).Select(i => i % side < side / 2 ? -8f : 8f)];

    [Fact]
    public void BasicQualityHidesTheBackgroundBehindTheMask()
    {
        using PixelBuffer image = RenderFixture.Solid(60, 40, 10, 20, 30);
        ImageLayer layer = RenderFixture.Layer("a", image);

        using PixelBuffer mask = BackgroundRemoval.Mask(layer, HalfLogits(8), 8, new BackgroundSettings(), null, 1024);

        Assert.Equal((60, 40), (mask.Width, mask.Height));
        Assert.Equal(0, RenderFixture.At(mask, 5, 20).R);
        Assert.Equal(255, RenderFixture.At(mask, 55, 20).R);
    }

    [Fact]
    public void WhatTheLayersMaskHidAlreadyStaysHidden()
    {
        using PixelBuffer image = RenderFixture.Solid(60, 40, 10, 20, 30);
        // The old mask hid the bottom half.
        using PixelBuffer old = RenderFixture.Coverage(60, 40, (_, y) => y < 20 ? (byte)255 : (byte)0);
        ImageLayer layer = RenderFixture.Layer("a", image) with { Mask = new LayerMask { Coverage = old } };

        using PixelBuffer mask = BackgroundRemoval.Mask(layer, HalfLogits(8), 8, new BackgroundSettings(), null, 1024);

        Assert.Equal(255, RenderFixture.At(mask, 55, 5).R);
        Assert.Equal(0, RenderFixture.At(mask, 55, 35).R);
    }

    [Fact]
    public void OutsideTheSelectionTheMaskIsLeftAsItWas()
    {
        using PixelBuffer image = RenderFixture.Solid(60, 40, 10, 20, 30);
        ImageLayer layer = RenderFixture.Layer("a", image);
        // Only the top half is selected; the background there goes, below it nothing changes.
        DocumentSelection top = DocumentSelection.Rectangle(new Rect(0, 0, 60, 20));

        using PixelBuffer mask = BackgroundRemoval.Mask(layer, HalfLogits(8), 8, new BackgroundSettings(), top, 1024);

        Assert.Equal(0, RenderFixture.At(mask, 5, 5).R);
        Assert.Equal(255, RenderFixture.At(mask, 5, 35).R);
    }

    [Fact]
    public void APreviewStaysAtItsOwnSize()
    {
        using PixelBuffer image = RenderFixture.Solid(600, 400, 10, 20, 30);
        ImageLayer layer = RenderFixture.Layer("a", image);

        using PixelBuffer preview = BackgroundRemoval.Mask(layer, HalfLogits(8), 8, new BackgroundSettings(), null, 150, fullSize: false);
        using PixelBuffer kept = BackgroundRemoval.Mask(layer, HalfLogits(8), 8, new BackgroundSettings(), null, 150);

        Assert.Equal((150, 100), (preview.Width, preview.Height));
        Assert.Equal((600, 400), (kept.Width, kept.Height));
    }

    [Fact]
    public void ShiftingTheEdgeInwardsShrinksTheSubject()
    {
        using PixelBuffer image = RenderFixture.Solid(80, 40, 10, 20, 30);
        ImageLayer layer = RenderFixture.Layer("a", image);
        var still = new BackgroundSettings { Quality = BackgroundQuality.Advanced, Refine = 0, Contrast = 0 };

        using PixelBuffer plain = BackgroundRemoval.Mask(layer, HalfLogits(16), 16, still, null, 1024);
        using PixelBuffer shrunk = BackgroundRemoval.Mask(layer, HalfLogits(16), 16, still with { ShiftEdge = -8 }, null, 1024);

        static int Covered(PixelBuffer mask) =>
            Enumerable.Range(0, mask.Width).Count(x => RenderFixture.At(mask, x, 20).R >= 128);

        Assert.True(Covered(shrunk) < Covered(plain) - 2, $"{Covered(shrunk)} of {Covered(plain)}");
    }

    [Fact]
    public void FullContrastIsAHardCutAtTheMiddle()
    {
        using PixelBuffer image = RenderFixture.Solid(40, 40, 10, 20, 30);
        ImageLayer layer = RenderFixture.Layer("a", image);
        float[] soft = [.. Enumerable.Range(0, 64).Select(i => i % 8 < 4 ? -0.3f : 0.3f)];

        using PixelBuffer mask = BackgroundRemoval.Mask(layer, soft, 8,
            new BackgroundSettings { Quality = BackgroundQuality.Advanced, Refine = 0, Contrast = 100 }, null, 1024);

        Assert.Equal(0, RenderFixture.At(mask, 2, 20).R);
        Assert.Equal(255, RenderFixture.At(mask, 37, 20).R);
    }

    [Fact]
    public void TheGuidedFilterSharpensASoftMaskWhereTheGuideHasAnEdge()
    {
        // The guide steps at 12; the model's mask only ramps there, from 8 to 16 — the soft edge a
        // segmentation model leaves round hair.
        const int Width = 24, Height = 4;
        float[] guide = [.. Enumerable.Range(0, Width * Height).Select(i => i % Width < 12 ? 0f : 1f)];
        float[] mask = [.. Enumerable.Range(0, Width * Height).Select(i => Math.Clamp((i % Width - 8) / 8f, 0f, 1f))];

        float[] refined = BackgroundRemoval.GuidedFilter(mask, guide, Width, Height, 4, 1e-4f);

        int row = Height / 2 * Width;
        float before = mask[row + 13] - mask[row + 11], after = refined[row + 13] - refined[row + 11];
        Assert.True(after > before + 0.1f, $"a step of {after} across the guide's edge, from {before}");
    }

    [Fact]
    public void AnAllBackgroundAnswerIsNoSubject()
    {
        Assert.False(BackgroundRemoval.FoundSubject([-3f, -1f, -0.5f]));
        Assert.True(BackgroundRemoval.FoundSubject([-3f, 0.5f]));
    }
}
