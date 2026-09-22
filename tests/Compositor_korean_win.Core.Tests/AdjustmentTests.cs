using Compositor_korean_win.Core;
using Xunit;
using static Compositor_korean_win.Core.Tests.RenderFixture;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// The six adjustments on pixels, and adjustment layers in the compositor.
/// </summary>
/// <remarks>
/// Most of the arithmetic is upstream's C and is not re-derived here. What is checked is what this
/// port adds around it: which table goes to which kernel, that a soft edge is unpremultiplied once
/// and only once, that Grain stays put on the document however the frame is cut, and how an
/// adjustment layer's opacity, mask and place in the stack decide what it touches.
/// </remarks>
public class AdjustmentTests
{
    private static LayerAdjustment Levels(double black = 0, double gamma = 1, double white = 255) =>
        new(AdjustmentKind.Levels)
        {
            Levels = new LevelsSettings
            {
                Ranges = new EquatableList<LevelRange>(
                [
                    new LevelRange { Black = black, Gamma = gamma, White = white }, new(), new(), new(),
                ]),
            },
        };

    private static ImageLayer AdjustmentLayer(LayerAdjustment adjustment, int width, int height) => new()
    {
        Id = Guid.NewGuid(),
        Name = adjustment.Kind.ToString(),
        Transform = new LayerTransform(Point.Zero, new Size(width, height)),
        Adjustment = adjustment,
    };

    private static PixelBuffer Render(CanvasDocument document)
    {
        using var backend = new SoftwareRenderBackend();
        return LayerCompositor.Render(document, backend);
    }

    // MARK: Hue/Saturation

    [Fact]
    public void AHueShiftOfAThirdTurnsRedGreen()
    {
        var settings = HueSaturationSettings.From(120, 0, 0, colorize: false);
        (double r, double g, double b) = HueSaturationFilter.Adjust(1, 0, 0, settings);

        Assert.Equal(0, r, precision: 6);
        Assert.Equal(1, g, precision: 6);
        Assert.Equal(0, b, precision: 6);
    }

    [Fact]
    public void ARangeLeavesColoursOutsideItsBandAlone()
    {
        // Reds only: blue sits far outside the band and must not move.
        var settings = HueSaturationSettings.From(120, 0, 0, colorize: false, range: ColorRange.Reds);

        (double r, double g, double b) = HueSaturationFilter.Adjust(0, 0, 1, settings);
        Assert.Equal(0, r, precision: 6);
        Assert.Equal(0, g, precision: 6);
        Assert.Equal(1, b, precision: 6);

        (r, g, _) = HueSaturationFilter.Adjust(1, 0, 0, settings);
        Assert.True(g > 0.99 && r < 0.01, $"red became ({r}, {g})");
    }

    [Fact]
    public void ColorizeGivesEveryGreyTheChosenHue()
    {
        var settings = HueSaturationSettings.From(240, 100, 0, colorize: true);
        (double r, double g, double b) = HueSaturationFilter.Adjust(0.5, 0.5, 0.5, settings);

        Assert.True(b > 0.99 && r < 0.01 && g < 0.01, $"grey became ({r}, {g}, {b})");
    }

    [Fact]
    public void ASoftEdgeIsUnpremultipliedOnceAndOnlyOnce()
    {
        // Half-transparent pure blue. Desaturated fully it is a grey of the same lightness, 0.5 —
        // so 64 premultiplied at alpha 128. Upstream's note is that doing the division twice turned
        // this dark; doing it not at all would treat 128 as the colour.
        using PixelBuffer pixels = Solid(4, 4, 0, 0, 255, 128);
        var adjustment = new LayerAdjustment(AdjustmentKind.Hsv)
        {
            HsvSettings = HueSaturationSettings.From(0, -100, 0, colorize: false),
        };

        AdjustmentRendering.Apply(adjustment, pixels, PixelPlacement.Document);

        (int r, int g, int b, int a) = At(pixels, 1, 1);
        Assert.Equal(128, a);
        Assert.InRange(r, 63, 65);
        Assert.Equal(r, g);
        Assert.Equal(r, b);
    }

    [Fact]
    public void AnIdentityCubeChangesNothingThatMatters()
    {
        using PixelBuffer pixels = Gradient(33, 33);
        using PixelBuffer before = PixelRegion.Copy(pixels, new PixelRect(0, 0, 33, 33));

        // Not an identity by the settings' own test, so it really runs: a hue shift of a full turn.
        var adjustment = new LayerAdjustment(AdjustmentKind.Hsv)
        {
            HsvSettings = HueSaturationSettings.From(360, 0, 0, colorize: false),
        };
        AdjustmentRendering.Apply(adjustment, pixels, PixelPlacement.Document);

        int worst = 0;
        for (int y = 0; y < 33; y++)
            for (int x = 0; x < 33 * 4; x++)
                worst = Math.Max(worst, Math.Abs(pixels.Row(y)[x] - before.Row(y)[x]));

        // Trilinear lookup between cube points cannot be exact for colours between them.
        Assert.True(worst <= 3, $"worst channel moved by {worst}");
    }

    // MARK: Tables

    [Fact]
    public void LevelsMovesTheBlackPoint()
    {
        using PixelBuffer pixels = Solid(2, 1, 200, 200, 200);
        AdjustmentRendering.Apply(Levels(black: 128), pixels, PixelPlacement.Document);

        // (200 − 128) / (255 − 128) of full scale.
        Assert.InRange(At(pixels, 0, 0).R, 144, 146);
    }

    [Fact]
    public void LevelsOnASoftEdgeAdjustsTheColourNotThePremultipliedValue()
    {
        // Straight 200 at half alpha is premultiplied 100. Treating 100 as the colour would push it
        // below the black point; dividing twice would push it to white.
        using PixelBuffer pixels = Solid(2, 1, 200, 200, 200, 128);
        AdjustmentRendering.Apply(Levels(black: 128), pixels, PixelPlacement.Document);

        (int r, _, _, int a) = At(pixels, 0, 0);
        Assert.Equal(128, a);
        Assert.InRange(r, 71, 75);
    }

    [Fact]
    public void AStopOfExposureBrightensAndKeepsAlpha()
    {
        using PixelBuffer pixels = Solid(2, 1, 100, 100, 100, 200);
        (int before, _, _, _) = At(pixels, 0, 0);

        var adjustment = new LayerAdjustment(AdjustmentKind.Exposure)
        {
            ExposureSettings = new ExposureSettings { Exposure = 1 },
        };
        AdjustmentRendering.Apply(adjustment, pixels, PixelPlacement.Document);

        (int after, _, _, int a) = At(pixels, 0, 0);
        Assert.Equal(200, a);
        Assert.True(after > before * 1.2, $"{before} became {after}");
        Assert.True(after <= a);
    }

    [Fact]
    public void CurvesFollowTheirHandles()
    {
        var curves = new CurvesSettings
        {
            Channels = new EquatableList<EquatableList<CurvePoint>>(
            [
                new([new CurvePoint(0, 0), new CurvePoint(128, 200), new CurvePoint(255, 255)]),
                new([new CurvePoint(0, 0), new CurvePoint(255, 255)]),
                new([new CurvePoint(0, 0), new CurvePoint(255, 255)]),
                new([new CurvePoint(0, 0), new CurvePoint(255, 255)]),
            ]),
        };
        using PixelBuffer pixels = Solid(1, 1, 128, 128, 128);

        AdjustmentRendering.Apply(new LayerAdjustment(AdjustmentKind.Curves) { Curves = curves }, pixels,
                                  PixelPlacement.Document);

        Assert.InRange(At(pixels, 0, 0).G, 199, 201);
    }

    [Fact]
    public void AGradientMapRunsFromShadowsToHighlights()
    {
        using PixelBuffer dark = Solid(1, 1, 0, 0, 0);
        using PixelBuffer light = Solid(1, 1, 255, 255, 255);
        var adjustment = new LayerAdjustment(AdjustmentKind.GradientMap)
        {
            GradientMapSettings = new GradientMapSettings
            {
                Shadows = new AdjustmentColor(1, 0, 0),
                Highlights = new AdjustmentColor(0, 0, 1),
            },
        };

        AdjustmentRendering.Apply(adjustment, dark, PixelPlacement.Document);
        AdjustmentRendering.Apply(adjustment, light, PixelPlacement.Document);

        Assert.Equal((255, 0, 0, 255), At(dark, 0, 0));
        Assert.Equal((0, 0, 255, 255), At(light, 0, 0));
    }

    [Fact]
    public void IdentitiesAreRecognisedSoTheFrameIsNotReadBack()
    {
        Assert.True(AdjustmentRendering.IsIdentity(new LayerAdjustment(AdjustmentKind.Levels)));
        Assert.True(AdjustmentRendering.IsIdentity(new LayerAdjustment(AdjustmentKind.Curves)));
        Assert.True(AdjustmentRendering.IsIdentity(new LayerAdjustment(AdjustmentKind.Exposure)));
        Assert.True(AdjustmentRendering.IsIdentity(new LayerAdjustment(AdjustmentKind.Hsv)));
        Assert.False(AdjustmentRendering.IsIdentity(new LayerAdjustment(AdjustmentKind.GradientMap)));
        Assert.False(AdjustmentRendering.IsIdentity(new LayerAdjustment(AdjustmentKind.Grain)));
        Assert.False(AdjustmentRendering.IsIdentity(Levels(black: 1)));
    }

    // MARK: Grain

    [Fact]
    public void GrainIsTheSameWhereverTheFrameIsCut()
    {
        var grain = new LayerAdjustment(AdjustmentKind.Grain)
        {
            GrainSettings = new GrainSettings { Amount = 80, Size = 3, Roughness = 30, Seed = 11 },
        };

        using PixelBuffer whole = Solid(64, 32, 128, 128, 128);
        AdjustmentRendering.Apply(grain, whole, PixelPlacement.Document);

        // The same document drawn as two frames, the right one starting at x = 40.
        using PixelBuffer left = Solid(40, 32, 128, 128, 128);
        using PixelBuffer right = Solid(24, 32, 128, 128, 128);
        AdjustmentRendering.Apply(grain, left, PixelPlacement.Document);
        AdjustmentRendering.Apply(grain, right, new PixelPlacement(40, 0, 1));

        for (int y = 0; y < 32; y++)
        {
            Assert.True(whole.Row(y)[..(40 * 4)].SequenceEqual(left.Row(y)[..(40 * 4)]), $"left, row {y}");
            Assert.True(whole.Row(y).Slice(40 * 4, 24 * 4).SequenceEqual(right.Row(y)[..(24 * 4)]), $"right, row {y}");
        }
    }

    [Fact]
    public void AProjectionPlacesTheFrameOnTheDocument()
    {
        // Two times zoom, shifted: surface pixel 10 is centred on document 10.5 / 2 − 3.
        PixelPlacement placement = PixelPlacement.For(new CanvasProjection(2, new Point(6, 8)));

        Assert.Equal(-3, placement.OriginX, precision: 9);
        Assert.Equal(-4, placement.OriginY, precision: 9);
        Assert.Equal(0.5, placement.UnitsPerPixel, precision: 9);
    }

    // MARK: Adjustment layers

    [Fact]
    public void AnAdjustmentLayerChangesWhatIsBeneathIt()
    {
        using PixelBuffer grey = Solid(8, 8, 200, 200, 200);
        CanvasDocument document = Document(8, 8, Layer("grey", grey), AdjustmentLayer(Levels(black: 128), 8, 8));

        using PixelBuffer result = Render(document);
        Assert.InRange(At(result, 4, 4).R, 144, 146);
    }

    [Fact]
    public void AnAdjustmentLayerDoesNotReachLayersAboveIt()
    {
        using PixelBuffer grey = Solid(8, 8, 200, 200, 200);
        CanvasDocument document = Document(8, 8, AdjustmentLayer(Levels(black: 128), 8, 8), Layer("grey", grey));

        using PixelBuffer result = Render(document);
        Assert.Equal((200, 200, 200, 255), At(result, 4, 4));
    }

    [Fact]
    public void AHiddenAdjustmentLayerDoesNothing()
    {
        using PixelBuffer grey = Solid(8, 8, 200, 200, 200);
        ImageLayer levels = AdjustmentLayer(Levels(black: 128), 8, 8) with { IsVisible = false };

        using PixelBuffer result = Render(Document(8, 8, Layer("grey", grey), levels));
        Assert.Equal((200, 200, 200, 255), At(result, 4, 4));
    }

    [Fact]
    public void OpacityTakesAnAdjustmentPartWay()
    {
        using PixelBuffer grey = Solid(8, 8, 200, 200, 200);
        ImageLayer levels = AdjustmentLayer(Levels(black: 128), 8, 8) with { Opacity = 0.5 };

        using PixelBuffer result = Render(Document(8, 8, Layer("grey", grey), levels));

        // Halfway from 200 to about 145.
        Assert.InRange(At(result, 4, 4).R, 171, 174);
    }

    [Fact]
    public void AMaskKeepsAnAdjustmentToItsWhite()
    {
        using PixelBuffer grey = Solid(8, 8, 200, 200, 200);
        using PixelBuffer coverage = Coverage(8, 8, (x, _) => x < 4 ? (byte)255 : (byte)0);
        ImageLayer levels = AdjustmentLayer(Levels(black: 128), 8, 8) with
        {
            Mask = new LayerMask { Coverage = coverage },
        };

        using PixelBuffer result = Render(Document(8, 8, Layer("grey", grey), levels));

        Assert.InRange(At(result, 1, 4).R, 144, 146);
        Assert.Equal((200, 200, 200, 255), At(result, 6, 4));
    }

    [Fact]
    public void AnAdjustmentLeavesTransparencyTransparent()
    {
        using PixelBuffer small = Solid(4, 4, 200, 200, 200);
        var map = new LayerAdjustment(AdjustmentKind.GradientMap)
        {
            GradientMapSettings = new GradientMapSettings { Shadows = new AdjustmentColor(1, 0, 0) },
        };

        using PixelBuffer result = Render(Document(8, 8, Layer("small", small), AdjustmentLayer(map, 8, 8)));

        Assert.Equal((0, 0, 0, 0), At(result, 6, 6));
        Assert.Equal(255, At(result, 1, 1).A);
    }

    [Fact]
    public void AClippedAdjustmentStaysInsideItsBase()
    {
        // A base covering the left half, a grey backdrop under everything, and an adjustment
        // clipped to the base: the backdrop's right half must be left as it was.
        using PixelBuffer backdrop = Solid(8, 8, 200, 200, 200);
        using PixelBuffer half = Solid(4, 8, 200, 200, 200);
        ImageLayer baseLayer = Layer("base", half);
        ImageLayer levels = AdjustmentLayer(Levels(black: 128), 8, 8) with { MaskSourceId = baseLayer.Id };

        using PixelBuffer result = Render(Document(8, 8, Layer("backdrop", backdrop), baseLayer, levels));

        Assert.InRange(At(result, 1, 4).R, 144, 146);
        Assert.Equal((200, 200, 200, 255), At(result, 6, 4));
    }

    [Fact]
    public void AnAdjustmentWithABlendModeBlendsItsResultWithTheOriginal()
    {
        // An identity adjustment in Multiply multiplies the picture by itself: 200 × 200 / 255.
        using PixelBuffer grey = Solid(8, 8, 200, 200, 200);
        ImageLayer curves = AdjustmentLayer(new LayerAdjustment(AdjustmentKind.Curves), 8, 8) with
        {
            BlendMode = LayerBlendMode.Multiply,
        };

        using PixelBuffer result = Render(Document(8, 8, Layer("grey", grey), curves));
        Assert.InRange(At(result, 4, 4).R, 155, 158);
    }

    [Fact]
    public void MixingByWeightInterpolatesPremultipliedColour()
    {
        using PixelBuffer original = Solid(1, 1, 0, 0, 0, 200);
        using PixelBuffer adjusted = Solid(1, 1, 255, 255, 255, 200);

        AdjustmentRendering.Mix(original, adjusted, weights: null, opacity: 0.5);

        // Alpha does not thicken: both sides had 200, so does the result.
        (int r, _, _, int a) = At(adjusted, 0, 0);
        Assert.Equal(200, a);
        Assert.InRange(r, 99, 101);
    }
}
