using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// The three tools that are upstream's C, called as they are: the wand, spot healing and
/// content-aware fill.
/// </summary>
/// <remarks>
/// These tests need the kernels, which CI builds and drops beside the test binaries. They check
/// the boundary as much as the behaviour — a stride misread or a channel order swapped shows up
/// here as a selection of the wrong half of the image.
/// </remarks>
public class KernelToolTests
{
    /// <summary>Two flat halves, so what matches and what does not is not a matter of taste.</summary>
    private static PixelBuffer TwoHalves(int width = 32, int height = 32)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < width; x++)
            {
                byte value = x < width / 2 ? (byte)20 : (byte)220;
                row[x * 4 + 0] = value;
                row[x * 4 + 1] = value;
                row[x * 4 + 2] = value;
                row[x * 4 + 3] = 255;
            }
        }
        return buffer;
    }

    [Fact]
    public void TheWandTakesTheHalfThatWasClicked()
    {
        using PixelBuffer image = TwoHalves();
        (DocumentSelection? selection, WandOutcome outcome) =
            MagicWand.Select(image, new Point(4, 16), new WandSettings { Tolerance = 20 });

        Assert.Equal(WandOutcome.Selected, outcome);
        Assert.NotNull(selection);

        byte[] levels = selection.Levels(new PixelRect(0, 0, 32, 32));
        Assert.Equal(255, levels[16 * 32 + 4]);    // The dark half.
        Assert.Equal(0, levels[16 * 32 + 28]);     // The light one.

        // Exactly half the image, to the pixel: the outline runs along pixel edges.
        double area = 0;
        foreach (byte level in levels) area += level / 255.0;
        Assert.Equal(512, area, precision: 6);
    }

    [Fact]
    public void ToleranceDecidesHowMuchComesWithIt()
    {
        using PixelBuffer image = TwoHalves();

        (DocumentSelection? everything, _) =
            MagicWand.Select(image, new Point(4, 16), new WandSettings { Tolerance = 255 });

        Assert.NotNull(everything);

        double area = 0;
        foreach (byte level in everything.Levels(new PixelRect(0, 0, 32, 32))) area += level / 255.0;
        Assert.Equal(1024, area, precision: 6);
    }

    [Fact]
    public void ClickingOutsideTheLayerSelectsNothing()
    {
        using PixelBuffer image = TwoHalves();

        Assert.Equal(WandOutcome.NothingMatched,
                     MagicWand.Select(image, new Point(-1, 0), new WandSettings()).Outcome);
        Assert.Equal(WandOutcome.NothingMatched,
                     MagicWand.Select(image, new Point(0, 99), new WandSettings()).Outcome);
    }

    [Fact]
    public void ContiguousStopsAtTheGap()
    {
        // Two dark squares with a light band between them, so "similar" and "connected" differ.
        using PixelBuffer image = PixelBuffer.Allocate(32, 8);
        for (int y = 0; y < 8; y++)
        {
            Span<byte> row = image.Row(y);
            for (int x = 0; x < 32; x++)
            {
                byte value = x is < 8 or >= 24 ? (byte)30 : (byte)230;
                row[x * 4 + 0] = value;
                row[x * 4 + 1] = value;
                row[x * 4 + 2] = value;
                row[x * 4 + 3] = 255;
            }
        }

        double Area(bool contiguous)
        {
            (DocumentSelection? selection, _) = MagicWand.Select(
                image, new Point(2, 4), new WandSettings { Tolerance = 20, Contiguous = contiguous });

            if (selection is null) return 0;

            double total = 0;
            foreach (byte level in selection.Levels(new PixelRect(0, 0, 32, 8))) total += level / 255.0;
            return total;
        }

        Assert.Equal(64, Area(contiguous: true), precision: 6);     // The near square only.
        Assert.Equal(128, Area(contiguous: false), precision: 6);   // Both of them.
    }

    [Fact]
    public void HealingPutsTheSurroundingsBackOverTheSpot()
    {
        // A flat field with one dark blot in the middle of it.
        using PixelBuffer image = RenderFixture.Solid(96, 96, 180, 170, 160);
        for (int y = 44; y < 52; y++)
        {
            Span<byte> row = image.Row(y);
            for (int x = 44; x < 52; x++)
            {
                row[x * 4 + 0] = 10;
                row[x * 4 + 1] = 10;
                row[x * 4 + 2] = 10;
            }
        }

        using var stroke = new BrushStroke(image, 96, 96, new BrushSettings
        {
            Diameter = 12,
            Hardness = 1,
            Mode = BrushMode.Heal,
        });

        stroke.Append(new Point(48, 48));
        Assert.True(stroke.Heal(seed: 1));

        using PixelBuffer healed = stroke.Commit();
        (int R, int G, int B, int A) middle = RenderFixture.At(healed, 48, 48);

        // Whatever it copied, it came from the field around the blot rather than the blot.
        Assert.InRange(middle.R, 120, 255);
        Assert.Equal(255, middle.A);
    }

    [Fact]
    public void AHealingStrokeLeavesTheRestOfTheLayerAlone()
    {
        using PixelBuffer image = RenderFixture.Solid(96, 96, 60, 90, 120);
        using var stroke = new BrushStroke(image, 96, 96, new BrushSettings
        {
            Diameter = 10,
            Mode = BrushMode.Heal,
        });

        stroke.Append(new Point(20, 20));
        stroke.Heal(seed: 7);

        using PixelBuffer healed = stroke.Commit();
        Assert.Equal((60, 90, 120, 255), RenderFixture.At(healed, 80, 80));
    }

    [Fact]
    public void ContentAwareFillTakesTheMarkedPixelsFromElsewhere()
    {
        using PixelBuffer image = RenderFixture.Gradient(64, 64);
        var coverage = new byte[64 * 64];

        for (int y = 28; y < 36; y++)
        {
            for (int x = 28; x < 36; x++) coverage[y * 64 + x] = 255;
        }

        (int R, int G, int B, int A) before = RenderFixture.At(image, 32, 32);
        bool filled = SpotHeal.ContentAwareFill(image, coverage);
        (int R, int G, int B, int A) after = RenderFixture.At(image, 32, 32);

        Assert.True(filled);
        Assert.NotEqual(before, after);
        Assert.Equal(255, after.A);
    }

    [Fact]
    public void CoverageBoundsFindWhatWasMarked()
    {
        var coverage = new byte[32 * 32];
        coverage[10 * 32 + 5] = 200;
        coverage[20 * 32 + 15] = 30;

        Assert.Equal(PixelRect.FromBounds(5, 10, 16, 21), SpotHeal.CoverageBounds(coverage, 32, 32));
    }
}
