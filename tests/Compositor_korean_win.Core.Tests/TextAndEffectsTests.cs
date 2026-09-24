using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>A font whose every glyph is a box, so layout can be checked by arithmetic.</summary>
internal sealed class BoxGlyphs : IGlyphSource
{
    public const double Advance = 0.6, Ascent = 0.8, Descent = 0.2, CapHeight = 0.7;

    public FontMetricsEm Metrics(TextFace face) => new(Ascent, Descent, 0);

    public GlyphShape Glyph(TextFace face, int codepoint)
    {
        if (codepoint == ' ') return GlyphShape.Blank(0.3);
        // Clockwise in y-down, as TrueType outlines are.
        var start = new Point(0.05, -CapHeight);
        return new GlyphShape(Advance,
        [
            new GlyphFigure(start,
            [
                GlyphSegment.Line(new Point(0.55, -CapHeight)),
                GlyphSegment.Line(new Point(0.55, 0)),
                GlyphSegment.Line(new Point(0.05, 0)),
                GlyphSegment.Line(start),
            ]),
        ]);
    }
}

public class TextAndEffectsTests
{
    private static readonly BoxGlyphs Glyphs = new();

    private static LayerText Text(string words, double size = 100) => new() { Text = words, Size = size };

    [Fact]
    public void LinesAreSetOneUnderAnotherAtTheLeading()
    {
        TextOutline outline = TextRendering.Layout(Text("AB\nC") with { Leading = 1.5 }, Glyphs);

        // Three glyphs, one box each.
        Assert.Equal(3, outline.Loops.Count);
        // The widest line is two advances.
        Assert.Equal(120, outline.Box.Width, 6);
        // Ascent, one line step, descent.
        Assert.Equal(80 + 150 + 20, outline.Box.Height, 6);
        // The anchor is the first baseline's start.
        Assert.Equal(new Point(0, 80), outline.Anchor);

        // The second line's glyph sits a leading lower than the first line's.
        double firstBottom = outline.Loops[0].Max(p => p.Y), thirdBottom = outline.Loops[2].Max(p => p.Y);
        Assert.Equal(150, thirdBottom - firstBottom, 6);
    }

    [Theory]
    [InlineData(TextAlign.Left, 0, 0)]
    [InlineData(TextAlign.Center, 30, 60)]
    [InlineData(TextAlign.Right, 60, 120)]
    public void ShortLinesLineUpByTheAlignment(TextAlign align, double shortLineShift, double anchorX)
    {
        TextOutline outline = TextRendering.Layout(Text("AB\nC") with { Align = align }, Glyphs);

        double left = outline.Loops[2].Min(p => p.X);
        Assert.Equal(5 + shortLineShift, left, 6);
        Assert.Equal(anchorX, outline.Anchor.X, 6);
    }

    [Fact]
    public void TrackingSpacesTheCharactersApart()
    {
        TextOutline plain = TextRendering.Layout(Text("AB"), Glyphs);
        TextOutline tracked = TextRendering.Layout(Text("AB") with { Tracking = 200 }, Glyphs);

        // 200 thousandths of 100 pixels, once — none after the last character.
        Assert.Equal(plain.Box.Width + 20, tracked.Box.Width, 6);
    }

    [Fact]
    public void RenderedTextIsTheColourWhereTheGlyphsAreAndClearElsewhere()
    {
        LayerText text = Text("A", 100).WithColour(new Rgba(200, 30, 60));
        RenderedText rendered = TextRendering.Render(text, Glyphs);
        using PixelBuffer pixels = rendered.Pixels;

        // The box glyph spans 50 × 70 pixels, with the margin round it.
        Assert.InRange(pixels.Width, 50, 56);
        Assert.InRange(pixels.Height, 70, 76);
        int cx = pixels.Width / 2, cy = pixels.Height / 2;
        Assert.Equal((200, 30, 60, 255), RenderFixture.At(pixels, cx, cy));
        Assert.Equal(0, RenderFixture.At(pixels, 0, 0).A);

        // The anchor was the baseline's start, which is five pixels left of the box's edge.
        Assert.Equal(rendered.Anchor.Y, pixels.Height - 2, 0);
    }

    [Fact]
    public void OverlappingContoursFillWithoutAHole()
    {
        // Two overlapping squares wound the same way: nonzero keeps the overlap filled, where
        // even-odd would clear it — the case every Hangul syllable built from pieces relies on.
        List<Point> a = [new(0, 0), new(20, 0), new(20, 20), new(0, 20)];
        List<Point> b = [new(10, 10), new(30, 10), new(30, 30), new(10, 30)];
        byte[] coverage = OutlineRaster.Coverage([a, b], new PixelRect(0, 0, 32, 32));

        Assert.Equal(255, coverage[15 * 32 + 15]);
        Assert.Equal(255, coverage[5 * 32 + 5]);
        Assert.Equal(0, coverage[5 * 32 + 25]);
    }

    [Fact]
    public void AHalfCoveredPixelIsHalfCovered()
    {
        List<Point> square = [new(0, 0), new(10.5, 0), new(10.5, 10), new(0, 10)];
        byte[] coverage = OutlineRaster.Coverage([square], new PixelRect(0, 0, 12, 12));
        Assert.InRange(coverage[5 * 12 + 10], 120, 136);
    }

    [Fact]
    public void BulgeMakesTheMiddleTallerAndLeavesTheEndsAlone()
    {
        var box = new Rect(0, 0, 200, 100);
        var bulge = new TextWarp { Style = TextWarpStyle.Bulge, Bend = 50 };

        Point top = TextWarping.Map(new Point(100, 0), box, bulge);
        Point bottom = TextWarping.Map(new Point(100, 100), box, bulge);
        Assert.True(bottom.Y - top.Y > 140, $"middle is {bottom.Y - top.Y} tall");

        Point cornerTop = TextWarping.Map(new Point(0, 0), box, bulge);
        Assert.Equal(0, cornerTop.Y, 6);

        // A negative bend pinches instead.
        var pinch = bulge with { Bend = -50 };
        double pinched = TextWarping.Map(new Point(100, 100), box, pinch).Y - TextWarping.Map(new Point(100, 0), box, pinch).Y;
        Assert.True(pinched < 60, $"middle is {pinched} tall");
    }

    [Fact]
    public void ArcLiftsTheMiddleAboveTheEnds()
    {
        var box = new Rect(0, 0, 400, 100);
        var arc = new TextWarp { Style = TextWarpStyle.Arc, Bend = 50 };

        Point middle = TextWarping.Map(new Point(200, 50), box, arc);
        Point end = TextWarping.Map(new Point(400, 50), box, arc);
        Assert.Equal(50, middle.Y, 6);
        Assert.True(end.Y > middle.Y + 50, $"end at {end.Y}");
    }

    [Theory]
    [InlineData(TextWarpStyle.Arc)]
    [InlineData(TextWarpStyle.ArcLower)]
    [InlineData(TextWarpStyle.ArcUpper)]
    [InlineData(TextWarpStyle.Arch)]
    [InlineData(TextWarpStyle.Bulge)]
    [InlineData(TextWarpStyle.ShellLower)]
    [InlineData(TextWarpStyle.ShellUpper)]
    [InlineData(TextWarpStyle.Flag)]
    [InlineData(TextWarpStyle.Wave)]
    [InlineData(TextWarpStyle.Fish)]
    [InlineData(TextWarpStyle.Rise)]
    [InlineData(TextWarpStyle.Fisheye)]
    [InlineData(TextWarpStyle.Inflate)]
    [InlineData(TextWarpStyle.Squeeze)]
    [InlineData(TextWarpStyle.Twist)]
    public void EveryWarpIsTheIdentityAtNoBendAndStillDrawsAtFullBend(TextWarpStyle style)
    {
        var box = new Rect(0, 0, 300, 90);
        var none = new TextWarp { Style = style, Bend = 0 };
        Point point = new(70, 30);
        Assert.Equal(point, TextWarping.Map(point, box, none));

        LayerText text = Text("AB C", 90) with { Warp = new TextWarp { Style = style, Bend = 100, Horizontal = 30 } };
        RenderedText rendered = TextRendering.Render(text, Glyphs);
        using PixelBuffer pixels = rendered.Pixels;
        Assert.True(Enumerable.Range(0, pixels.Height).Any(y => pixels.Row(y).ToArray().Where((_, i) => i % 4 == 3).Any(a => a > 0)));
    }

    [Fact]
    public void EmptyTextRendersATransparentPixel()
    {
        RenderedText rendered = TextRendering.Render(Text(""), Glyphs);
        using PixelBuffer pixels = rendered.Pixels;
        Assert.True(pixels.Width >= 1 && pixels.Height >= 1);
        Assert.Equal(0, RenderFixture.At(pixels, 0, 0).A);
    }

    [Fact]
    public void ALayerIsLiveTextOnlyWhileItsPixelsAreTheOnesTheTextSet()
    {
        RenderedText rendered = TextRendering.Render(Text("A"), Glyphs);
        var layer = ProjectFixture.Layer("A", rendered.Pixels) with
        {
            Text = Text("A") with { Rendered = rendered.Pixels },
        };
        Assert.True(layer.IsLiveText);

        using PixelBuffer painted = PixelRegion.Copy(rendered.Pixels, new PixelRect(0, 0, rendered.Pixels.Width, rendered.Pixels.Height));
        Assert.False((layer with { Image = painted }).IsLiveText);
        rendered.Pixels.Release();
    }

    // MARK: Shapes

    [Theory]
    [InlineData(ShapeKind.Polygon, false)]
    [InlineData(ShapeKind.Star, false)]
    [InlineData(ShapeKind.Star, true)]
    public void PolygonsAndStarsFillTheirMiddle(ShapeKind kind, bool curved)
    {
        var settings = new ShapeSettings { Kind = kind, Sides = 5, Inset = 0.4, Curved = curved, Color = new Rgba(10, 200, 90) };
        using PixelBuffer drawn = ShapeTool.Draw(null, 100, 100, new Rect(10, 10, 80, 80), settings);

        Assert.Equal((10, 200, 90, 255), RenderFixture.At(drawn, 50, 50));
        // The top point reaches the top of the box; a corner of the box stays empty.
        Assert.True(RenderFixture.At(drawn, 50, 12).A > 0);
        Assert.Equal(0, RenderFixture.At(drawn, 12, 12).A);
    }

    [Fact]
    public void AStarsInnerCornersAreCutIn()
    {
        var star = new ShapeSettings { Kind = ShapeKind.Star, Sides = 4, Inset = 0.2 };
        using PixelBuffer drawn = ShapeTool.Draw(null, 100, 100, new Rect(0, 0, 100, 100), star);

        // Halfway to the corner, between two points, a four-point star with a small inset is empty.
        Assert.Equal(0, RenderFixture.At(drawn, 75, 25).A);
        Assert.True(RenderFixture.At(drawn, 50, 5).A > 0);
    }

    [Fact]
    public void AnOutlineOnlyShapeIsHollow()
    {
        var ring = new ShapeSettings
        {
            Kind = ShapeKind.Rectangle, Filled = false, StrokeWidth = 4, StrokeColor = new Rgba(255, 0, 0),
        };
        using PixelBuffer drawn = ShapeTool.Draw(null, 64, 64, new Rect(16, 16, 32, 32), ring);

        Assert.Equal(0, RenderFixture.At(drawn, 32, 32).A);
        Assert.Equal((255, 0, 0, 255), RenderFixture.At(drawn, 16, 32));
    }

    [Fact]
    public void ALineRunsFromWhereItStartedToWhereItEnded()
    {
        var line = new ShapeSettings { Kind = ShapeKind.Line, LineWidth = 3, Color = new Rgba(0, 0, 255) };
        using PixelBuffer drawn = ShapeTool.Draw(null, 64, 64, new Point(60, 4), new Point(4, 60), line);

        // Up-right to down-left: along the anti-diagonal, and not the main diagonal.
        Assert.Equal(255, RenderFixture.At(drawn, 32, 32).A);
        Assert.Equal(0, RenderFixture.At(drawn, 10, 10).A);
        Assert.True(RenderFixture.At(drawn, 55, 9).A > 200);

        // A horizontal line has no box to speak of and still draws.
        using PixelBuffer flat = ShapeTool.Draw(null, 64, 64, new Point(4, 20), new Point(60, 20), line);
        Assert.True(RenderFixture.At(flat, 30, 19).A > 200);
    }

    // MARK: Effects

    private static ImageLayer Square(LayerEffects effects, int size = 40, int at = 30)
    {
        PixelBuffer pixels = RenderFixture.Solid(size, size, 255, 255, 255);
        return ProjectFixture.Layer("Square", pixels) with
        {
            Transform = new LayerTransform(new Point(at, at), new Size(size, size)),
            Effects = effects,
        };
    }

    private static PixelBuffer Composite(ImageLayer layer, int width = 100, int height = 100)
    {
        var document = new CanvasDocument { Id = Guid.NewGuid(), Width = width, Height = height, Layers = new EquatableList<ImageLayer>([layer]) };
        using var backend = new SoftwareRenderBackend();
        return LayerCompositor.Render(document, backend);
    }

    [Fact]
    public void AnOutsideStrokeRingsTheShapeAndLeavesItAlone()
    {
        ImageLayer layer = Square(new LayerEffects { Stroke = new StrokeEffect { Size = 4, Red = 1 } });
        using PixelBuffer frame = Composite(layer);

        Assert.Equal((255, 0, 0, 255), RenderFixture.At(frame, 27, 50));   // 3 px outside the left edge
        Assert.Equal((255, 255, 255, 255), RenderFixture.At(frame, 35, 50)); // inside
        Assert.Equal(0, RenderFixture.At(frame, 20, 50).A);                 // past the stroke
    }

    [Fact]
    public void AnInsideStrokeStaysWithinTheShape()
    {
        ImageLayer layer = Square(new LayerEffects { Stroke = new StrokeEffect { Size = 4, Blue = 1, Position = StrokePosition.Inside } });
        using PixelBuffer frame = Composite(layer);

        Assert.Equal((0, 0, 255, 255), RenderFixture.At(frame, 32, 50));
        Assert.Equal(0, RenderFixture.At(frame, 28, 50).A);
        Assert.Equal((255, 255, 255, 255), RenderFixture.At(frame, 50, 50));
    }

    [Fact]
    public void AShadowFallsAwayFromTheLight()
    {
        // Light from the upper left, so the shadow falls down and to the right.
        var shadow = new ShadowEffect { Angle = 135, Distance = 10, Size = 0, Opacity = 1, Blend = LayerBlendMode.Normal };
        ImageLayer layer = Square(new LayerEffects { Shadow = shadow });
        using PixelBuffer frame = Composite(layer);

        Assert.Equal(255, RenderFixture.At(frame, 75, 75).A);   // past the square's corner, in the shadow
        Assert.Equal(0, RenderFixture.At(frame, 25, 25).A);     // the other side has none
        Assert.Equal((255, 255, 255, 255), RenderFixture.At(frame, 50, 50));
    }

    [Fact]
    public void AGlowSoftensOutwardsOnAllSides()
    {
        ImageLayer layer = Square(new LayerEffects { Glow = new GlowEffect { Size = 12, Opacity = 1, Blend = LayerBlendMode.Normal } });
        using PixelBuffer frame = Composite(layer);

        int near = RenderFixture.At(frame, 27, 50).A, far = RenderFixture.At(frame, 20, 50).A;
        Assert.True(near > far && far > 0, $"near {near}, far {far}");
        Assert.True(RenderFixture.At(frame, 50, 73).A > 0);
    }

    [Fact]
    public void EffectsFollowTheLayerWhenItIsScaled()
    {
        // The same square at half its pixels' size: a 4-pixel stroke is still 4 document pixels.
        PixelBuffer pixels = RenderFixture.Solid(80, 80, 255, 255, 255);
        ImageLayer layer = ProjectFixture.Layer("Half", pixels) with
        {
            Transform = new LayerTransform(new Point(30, 30), new Size(40, 40)),
            Effects = new LayerEffects { Stroke = new StrokeEffect { Size = 4, Green = 1 } },
        };
        using PixelBuffer frame = Composite(layer);

        Assert.True(RenderFixture.At(frame, 27, 50).G > 240);
        Assert.Equal(0, RenderFixture.At(frame, 22, 50).A);
    }

    [Fact]
    public void DisabledEffectsDrawNothing()
    {
        ImageLayer layer = Square(new LayerEffects { Stroke = new StrokeEffect { Size = 4, Enabled = false } });
        using PixelBuffer frame = Composite(layer);
        Assert.Equal(0, RenderFixture.At(frame, 27, 50).A);
    }

    [Fact]
    public void TextAndEffectsSurviveSavingAndReopening()
    {
        using var folder = new ProjectFixture.TemporaryFolder();
        RenderedText rendered = TextRendering.Render(Text("안녕 A"), Glyphs);
        Guid id = Guid.NewGuid();
        LayerText recipe = Text("안녕 A") with
        {
            Font = "Noto Serif KR", Weight = 700, Italic = true, Align = TextAlign.Center, Tracking = 50,
            Warp = new TextWarp { Style = TextWarpStyle.Arc, Bend = -40, Vertical = 10 },
            AnchorX = rendered.Anchor.X, AnchorY = rendered.Anchor.Y, Rendered = rendered.Pixels,
        };
        var effects = new LayerEffects
        {
            Shadow = new ShadowEffect { Distance = 7, Size = 3, Angle = 90 },
            Stroke = new StrokeEffect { Size = 2, Position = StrokePosition.Center, Red = 0.5 },
        };
        ImageLayer layer = ProjectFixture.Layer("Title", rendered.Pixels, id) with { Text = recipe, Effects = effects };

        string path = folder.File("Text.comp");
        ProjectStore.Save(ProjectMapping.ToSnapshot(ProjectFixture.Document(layer), id), path);
        CanvasDocument reopened = ProjectMapping.ToDocument(ProjectStore.Load(path));
        ImageLayer back = reopened.Layers[0];

        Assert.True(back.IsLiveText);
        Assert.Equal(recipe with { Rendered = null }, back.Text! with { Rendered = null });
        Assert.Equal(effects, back.Effects);
    }

    [Fact]
    public void TextNoLongerSetByItsRecipeIsNotSaved()
    {
        RenderedText rendered = TextRendering.Render(Text("A"), Glyphs);
        using PixelBuffer painted = PixelRegion.Copy(rendered.Pixels, new PixelRect(0, 0, rendered.Pixels.Width, rendered.Pixels.Height));
        Guid id = Guid.NewGuid();
        ImageLayer layer = ProjectFixture.Layer("A", painted, id) with { Text = Text("A") with { Rendered = rendered.Pixels } };

        ProjectSnapshot snapshot = ProjectMapping.ToSnapshot(ProjectFixture.Document(layer), id);
        Assert.Null(snapshot.Manifest.Layers[0].Text);
        rendered.Pixels.Release();
    }

    [Fact]
    public void OutOfRangeEffectsAreRejected()
    {
        ProjectLayerRecord record = ProjectFixture.Record("Bad") with
        {
            Effects = new LayerEffects { Stroke = new StrokeEffect { Size = double.NaN } },
        };
        ProjectManifest manifest = ProjectFixture.Manifest(ProjectManifest.CurrentVersion, record);
        Assert.Throws<ProjectException>(() => ManifestValidator.Validate(manifest));
    }
}
