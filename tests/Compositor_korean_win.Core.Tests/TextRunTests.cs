using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>Letters styled apart from the rest of their layer: Photoshop's character style runs.</summary>
public class TextRunTests
{
    private static readonly BoxGlyphs Glyphs = new();

    private static LayerText Hello => new() { Text = "hello", Size = 100 };

    private static LayerText Bigger(LayerText style) => style with { Size = 200 };

    [Fact]
    public void RestylingSelectedLettersMakesARunOfJustThem()
    {
        LayerText text = TextRuns.Restyle(Hello, 0, 3, Bigger);

        TextRun run = Assert.Single(text.Runs!);
        Assert.Equal((0, 3), (run.Start, run.Length));
        Assert.Equal(200, run.Size);
        Assert.Null(run.Font);
        Assert.Null(run.Red);
        Assert.Equal(100, text.Size);
        Assert.True(text.IsValid);
    }

    [Fact]
    public void RestylingEveryLetterAlikeLeavesNoRuns()
    {
        LayerText text = TextRuns.Restyle(Hello, 0, 3, Bigger);
        LayerText whole = TextRuns.Restyle(text, 0, 5, style => style with { Size = 150 });

        Assert.Null(whole.Runs);
        Assert.Equal(150, whole.Size);
    }

    [Fact]
    public void ARestyleWithNothingSelectedReachesTheLayerAndItsRuns()
    {
        LayerText text = TextRuns.Restyle(Hello, 0, 3, Bigger);
        LayerText red = TextRuns.Restyle(text, 2, 2, style => style.WithColour(new Rgba(255, 0, 0)));

        Assert.Equal(1, red.Red);
        TextRun run = Assert.Single(red.Runs!);
        // The run keeps its size, and its colour went with the layer's, so the run no longer names one.
        Assert.Equal(200, run.Size);
        Assert.Null(run.Red);
    }

    [Fact]
    public void AParagraphSettingReachesTheWholeLayerEvenWithLettersSelected()
    {
        LayerText text = TextRuns.Restyle(Hello, 1, 2, style => style with { Tracking = 80, Size = 120 });

        Assert.Equal(80, text.Tracking);
        TextRun run = Assert.Single(text.Runs!);
        Assert.Equal((1, 1, 120.0), (run.Start, run.Length, run.Size!.Value));
    }

    [Fact]
    public void NeighbouringRunsThatLookTheSameBecomeOne()
    {
        LayerText text = TextRuns.Restyle(Hello, 0, 2, Bigger);
        text = TextRuns.Restyle(text, 2, 4, Bigger);

        TextRun run = Assert.Single(text.Runs!);
        Assert.Equal((0, 4), (run.Start, run.Length));
    }

    [Fact]
    public void TypingAfterAStyledLetterTakesItsStyle()
    {
        LayerText text = TextRuns.Restyle(Hello, 0, 3, Bigger);

        LayerText typed = TextRuns.Retype(text, "helXlo");
        TextRun run = Assert.Single(typed.Runs!);
        Assert.Equal((0, 4), (run.Start, run.Length));

        LayerText after = TextRuns.Retype(text, "hello!");
        Assert.Equal((0, 3), (after.Runs![0].Start, after.Runs[0].Length));
    }

    [Fact]
    public void TypingBeforeARunMovesItAlong()
    {
        LayerText text = TextRuns.Restyle(Hello, 3, 5, Bigger);

        LayerText typed = TextRuns.Retype(text, "oh hello");

        TextRun run = Assert.Single(typed.Runs!);
        Assert.Equal((6, 2), (run.Start, run.Length));
        Assert.Equal("lo", typed.Text.Substring(run.Start, run.Length));
    }

    [Fact]
    public void DeletingARunsLettersRemovesIt()
    {
        LayerText text = TextRuns.Restyle(Hello, 1, 3, Bigger);

        LayerText cut = TextRuns.Retype(text, "hllo");
        TextRun run = Assert.Single(cut.Runs!);
        Assert.Equal((1, 1), (run.Start, run.Length));

        Assert.Null(TextRuns.Retype(text, "ho").Runs);
    }

    [Fact]
    public void RunsPastTheWordsOrOverlappingAreInvalid()
    {
        Assert.False((Hello with { Runs = [new TextRun { Start = 3, Length = 5, Size = 10 }] }).IsValid);
        Assert.False((Hello with
        {
            Runs = [new TextRun { Start = 0, Length = 3, Size = 10 }, new TextRun { Start = 2, Length = 2, Size = 20 }],
        }).IsValid);
        Assert.False((Hello with { Runs = [new TextRun { Start = 0, Length = 2, Red = 1 }] }).IsValid);
        Assert.True((Hello with { Runs = [new TextRun { Start = 0, Length = 2, Red = 1, Green = 0, Blue = 0 }] }).IsValid);
    }

    [Fact]
    public void ALineIsAsTallAsItsLargestLetterAndTheyShareABaseline()
    {
        // "AB": B twice the size of A.
        LayerText text = TextRuns.Restyle(new LayerText { Text = "AB", Size = 100 }, 1, 2, Bigger);
        TextOutline outline = TextRendering.Layout(text, Glyphs);

        Assert.Equal(2, outline.Loops.Count);
        // The first baseline sits under the larger letter's ascent.
        Assert.Equal(new Point(0, 160), outline.Anchor);
        Assert.Equal(160 + 40, outline.Box.Height, 6);
        // One advance at each size.
        Assert.Equal(60 + 120, outline.Box.Width, 6);
        // Both letters stand on the same baseline.
        Assert.Equal(outline.Loops[0].Max(p => p.Y), outline.Loops[1].Max(p => p.Y), 6);
        Assert.Equal(70, outline.Loops[0].Max(p => p.Y) - outline.Loops[0].Min(p => p.Y), 6);
        Assert.Equal(140, outline.Loops[1].Max(p => p.Y) - outline.Loops[1].Min(p => p.Y), 6);
    }

    [Fact]
    public void TheLineStepFollowsTheLargestLetterOnTheLine()
    {
        // The second line holds a letter twice the size, so it drops by the leading of that size.
        LayerText text = TextRuns.Restyle(new LayerText { Text = "A\nB", Size = 100, Leading = 1.2 }, 2, 3, Bigger);
        TextOutline outline = TextRendering.Layout(text, Glyphs);

        double firstBaseline = outline.Loops[0].Max(p => p.Y), secondBaseline = outline.Loops[1].Max(p => p.Y);
        Assert.Equal(240, secondBaseline - firstBaseline, 6);
    }

    [Fact]
    public void LettersInAnotherColourArePaintedInIt()
    {
        LayerText text = TextRuns.Restyle(new LayerText { Text = "AB", Size = 40 }, 1, 2,
                                          style => style.WithColour(new Rgba(255, 0, 0)));
        RenderedText rendered = TextRendering.Render(text, Glyphs);
        try
        {
            PixelBuffer pixels = rendered.Pixels;
            int y = (int)Math.Round(rendered.Anchor.Y - 10);
            int black = (int)Math.Round(rendered.Anchor.X + 0.3 * 40), red = (int)Math.Round(rendered.Anchor.X + (0.6 + 0.3) * 40);
            Assert.Equal([0, 0, 0, 255], pixels.Row(y).Slice(black * 4, 4).ToArray());
            Assert.Equal([255, 0, 0, 255], pixels.Row(y).Slice(red * 4, 4).ToArray());
        }
        finally
        {
            rendered.Pixels.Release();
        }
    }

    [Fact]
    public void TextWithoutRunsIsSetExactlyAsBefore()
    {
        LayerText text = new() { Text = "AB\nC", Size = 100, Leading = 1.5 };
        TextOutline outline = TextRendering.Layout(text, Glyphs);

        Assert.Null(outline.Colours);
        Assert.Equal(80 + 150 + 20, outline.Box.Height, 6);
    }

    [Fact]
    public void RunsSurviveSavingAndReopeningAProject()
    {
        using var folder = new ProjectFixture.TemporaryFolder();
        LayerText styled = TextRuns.Restyle(new LayerText { Text = "hello", Size = 60 }, 0, 3,
                                            style => style.WithColour(new Rgba(0, 128, 255)) with { Size = 90, Weight = 700 });
        RenderedText rendered = TextRendering.Render(styled, Glyphs);
        Guid id = Guid.NewGuid();
        LayerText recipe = styled with { AnchorX = rendered.Anchor.X, AnchorY = rendered.Anchor.Y, Rendered = rendered.Pixels };
        ImageLayer layer = ProjectFixture.Layer("Styled", rendered.Pixels, id) with { Text = recipe };

        string path = folder.File("Runs.comp");
        ProjectStore.Save(ProjectMapping.ToSnapshot(ProjectFixture.Document(layer), id), path);
        ImageLayer back = ProjectMapping.ToDocument(ProjectStore.Load(path)).Layers[0];

        Assert.True(back.IsLiveText);
        Assert.Equal(recipe.Runs, back.Text!.Runs);
    }

    [Fact]
    public void AnOverlappingRunInAProjectIsRefused()
    {
        LayerText bad = Hello with
        {
            Runs = [new TextRun { Start = 0, Length = 3, Size = 10 }, new TextRun { Start = 1, Length = 1, Size = 20 }],
        };
        Assert.False(bad.IsValid);
    }
}
