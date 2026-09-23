using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>Hue/Saturation's colour ranges set from the image and by their handles, and the colour picker's HSB.</summary>
public class HueSamplingTests
{
    private static readonly HueBand Reds = ColorRange.Reds.DefaultBand(); // 315, 345, 15, 45

    [Fact]
    public void IncludeWidensTheNearerEdge()
    {
        HueBand wider = Reds.Include(30);
        Assert.Equal(1, wider.Weight(30));
        Assert.Equal(345, wider.RangeStart);
        Assert.Equal(30, wider.RangeEnd);
        Assert.Equal(60, wider.FalloffEnd); // the shoulder keeps its width

        Assert.Same(Reds, Reds.Include(0)); // already fully inside
    }

    [Fact]
    public void ExcludeNarrowsUntilTheHueIsOutside()
    {
        HueBand narrower = Reds.Exclude(35);
        Assert.Equal(0, narrower.Weight(35));
        Assert.Equal(1, narrower.Weight(0));
    }

    [Fact]
    public void AHandleCannotPassItsNeighbour()
    {
        Assert.Equal(20, Reds.WithHandle(3, 20).FalloffEnd);
        Assert.Same(Reds, Reds.WithHandle(2, 50)); // range end beyond the falloff end
        Assert.Same(Reds, Reds.WithHandle(0, 350)); // falloff start past the range start
    }

    [Fact]
    public void AnEyedropperMovesOnlyTheSelectedRange()
    {
        var settings = new HueSaturationSettings { Range = ColorRange.Blues };
        HueSaturationSettings sampled = settings.Sampled(200, HueSampleMode.Replace);

        Assert.Equal(1, sampled.Band.Weight(200));
        Assert.Equal(settings.Bands.ValueOr(ColorRange.Reds, Reds), sampled.Bands.ValueOr(ColorRange.Reds, Reds));

        var master = new HueSaturationSettings();
        Assert.Same(master, master.Sampled(200, HueSampleMode.Add));
    }

    [Fact]
    public void ATargetedDragChangesTheRangeTheColourBelongsTo()
    {
        var settings = new HueSaturationSettings();
        Assert.Equal(ColorRange.Reds, HueSampling.RangeFor(settings, 2));
        Assert.Equal(ColorRange.Greens, HueSampling.RangeFor(settings, 120));
    }

    [Fact]
    public void TheResultBarShiftsTheHuesInTheRange()
    {
        HueSaturationSettings settings = HueSaturationSettings.From(30, 0, 0, colorize: false, ColorRange.Greens);
        Assert.Equal(150, HueSampling.ShiftedHue(120, settings), 6);
        Assert.Equal(300, HueSampling.ShiftedHue(300, settings), 6);
    }

    [Theory]
    [InlineData(1.0, 0.0, 0.0, 0.0)]
    [InlineData(0.0, 1.0, 0.0, 120.0)]
    [InlineData(0.2, 0.4, 0.8, 220.0)]
    public void HsbRoundTrips(double red, double green, double blue, double hue)
    {
        Hsb colour = Hsb.FromRgb(red, green, blue);
        Assert.Equal(hue, colour.Hue, 6);

        (double r, double g, double b) = colour.ToRgb();
        Assert.Equal(red, r, 9);
        Assert.Equal(green, g, 9);
        Assert.Equal(blue, b, 9);
    }

    [Fact]
    public void GreyHasNoHueToSample()
    {
        Assert.Null(HueSampling.HueOf(0.5, 0.5, 0.505));
        Assert.Equal(0.0, HueSampling.HueOf(1, 0, 0));
    }
}
