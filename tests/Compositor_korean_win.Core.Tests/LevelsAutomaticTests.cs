using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>Levels' Auto buttons, its eyedroppers, and how tall its histogram is drawn.</summary>
public class LevelsAutomaticTests
{
    /// <summary>An opaque image, one pixel per colour given.</summary>
    private static PixelBuffer Pixels(params (byte R, byte G, byte B)[] colours)
    {
        var pixels = PixelBuffer.Allocate(colours.Length, 1);
        Span<byte> row = pixels.Row(0);
        for (int i = 0; i < colours.Length; i++)
        {
            row[i * 4] = colours[i].R;
            row[i * 4 + 1] = colours[i].G;
            row[i * 4 + 2] = colours[i].B;
            row[i * 4 + 3] = 255;
        }
        return pixels;
    }

    /// <summary>Greys evenly from <paramref name="low"/> to <paramref name="high"/>, with a colour cast added to red.</summary>
    private static Histogram Spread(int low, int high, int redShift = 0)
    {
        var colours = Enumerable.Range(low, high - low + 1)
            .Select(v => ((byte)Math.Min(255, v + redShift), (byte)v, (byte)v))
            .ToArray();
        using PixelBuffer pixels = Pixels(colours);
        return Histogram.Measure(pixels);
    }

    [Fact]
    public void ContrastStretchesAllChannelsTogether()
    {
        LevelsSettings settings = LevelsAutomatic.Auto(LevelsAuto.Contrast, Spread(40, 200, redShift: 30));

        // Red runs 70–230 and the others 40–200: one shared interval covers them all.
        Assert.Equal(40, settings.Ranges[0].Black);
        Assert.Equal(230, settings.Ranges[0].White);
        Assert.All(settings.Ranges.Skip(1), range => Assert.Equal(new LevelRange(), range));
    }

    [Fact]
    public void ColorStretchesEachChannelOnItsOwn()
    {
        LevelsSettings settings = LevelsAutomatic.Auto(LevelsAuto.Color, Spread(40, 200, redShift: 30));

        Assert.Equal(new LevelRange(), settings.Ranges[0]);
        Assert.Equal((70.0, 230.0), (settings.Ranges[1].Black, settings.Ranges[1].White));
        Assert.Equal((40.0, 200.0), (settings.Ranges[2].Black, settings.Ranges[2].White));
        Assert.Equal((40.0, 200.0), (settings.Ranges[3].Black, settings.Ranges[3].White));
    }

    [Fact]
    public void NeutralMidtonesBringEachChannelsMeanToMiddleGrey()
    {
        // Mostly dark: the stretched mean sits below the middle, so gamma lifts it.
        var colours = Enumerable.Repeat(((byte)20, (byte)20, (byte)20), 30)
            .Append(((byte)220, (byte)220, (byte)220)).Append(((byte)120, (byte)120, (byte)120)).ToArray();
        using PixelBuffer pixels = Pixels(colours);
        LevelsSettings settings = LevelsAutomatic.Auto(LevelsAuto.ColorNeutral, Histogram.Measure(pixels));

        for (int channel = 1; channel <= 3; channel++)
        {
            Assert.True(settings.Ranges[channel].Gamma > 1, $"channel {channel}: {settings.Ranges[channel].Gamma}");
            Assert.InRange(settings.Ranges[channel].Gamma, 0.1, 9.99);
        }
    }

    [Fact]
    public void AnImageOfOneLevelHasNothingToStretch()
    {
        LevelsSettings settings = LevelsAutomatic.Auto(LevelsAuto.Color, Spread(128, 128));
        Assert.True(settings.IsIdentity);
    }

    [Fact]
    public void TheBlackEyedropperSetsEachChannelsBlackPoint()
    {
        LevelsSettings settings = new LevelsSettings().Sampling(0.2, 0.3, 0.4, LevelsSample.Black);

        Assert.Equal(new LevelRange(), settings.Ranges[0]);
        Assert.Equal(51, settings.Ranges[1].Black, 6);
        Assert.Equal(76.5, settings.Ranges[2].Black, 6);
        Assert.Equal(102, settings.Ranges[3].Black, 6);
    }

    [Fact]
    public void TheGrayEyedropperMakesTheSampleMiddleGrey()
    {
        LevelsSettings settings = new LevelsSettings().Sampling(0.25, 0.5, 0.75, LevelsSample.Gray);

        Assert.Equal(0.5, settings.Apply(0.25, LevelsChannel.Red), 3);
        Assert.Equal(0.5, settings.Apply(0.5, LevelsChannel.Green), 3);
        Assert.Equal(0.5, settings.Apply(0.75, LevelsChannel.Blue), 3);
    }

    [Fact]
    public void TheWhiteEyedropperStaysAboveTheBlackPoint()
    {
        LevelsSettings dark = new LevelsSettings().Sampling(0.5, 0.5, 0.5, LevelsSample.Black);
        LevelsSettings settings = dark.Sampling(0.1, 0.1, 0.1, LevelsSample.White);

        Assert.All(settings.Ranges.Skip(1), range => Assert.True(range.White > range.Black));
    }

    [Fact]
    public void ASpikeAtAnEndDoesNotFlattenTheGraph()
    {
        double[] bins = new double[256];
        for (int i = 1; i < 255; i++) bins[i] = 10;
        bins[0] = 100_000;

        Assert.Equal(40, LevelsAutomatic.DisplayScale(bins));
        Assert.Equal(0, LevelsAutomatic.DisplayScale(new double[256]));
    }
}
