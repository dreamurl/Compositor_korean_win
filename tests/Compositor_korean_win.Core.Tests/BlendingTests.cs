using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// The blend formulas, checked against the values the PDF specification defines.
/// </summary>
/// <remarks>
/// These are worth pinning precisely because they are easy to get subtly wrong and hard to notice:
/// a mode that is off only where the source is partly transparent looks fine on a hard-edged shape
/// and wrong on a soft brush. That is the exact failure upstream documents in Core Graphics for
/// Dodge and Burn, so those two get the most attention here.
/// </remarks>
public class BlendingTests
{
    private const double Tolerance = 1e-9;

    [Theory]
    [InlineData(LayerBlendMode.Normal, 0.2, 0.7, 0.7)]
    [InlineData(LayerBlendMode.Multiply, 0.5, 0.5, 0.25)]
    [InlineData(LayerBlendMode.Multiply, 1.0, 0.4, 0.4)]
    [InlineData(LayerBlendMode.Screen, 0.5, 0.5, 0.75)]
    [InlineData(LayerBlendMode.Screen, 0.0, 0.3, 0.3)]
    [InlineData(LayerBlendMode.Darken, 0.3, 0.7, 0.3)]
    [InlineData(LayerBlendMode.Lighten, 0.3, 0.7, 0.7)]
    [InlineData(LayerBlendMode.Difference, 0.3, 0.7, 0.4)]
    [InlineData(LayerBlendMode.Difference, 0.7, 0.3, 0.4)]
    public void SeparableModesFollowTheSpecification(LayerBlendMode mode, double backdrop,
                                                     double source, double expected) =>
        Assert.Equal(expected, Blending.Channel(mode, backdrop, source), Tolerance);

    [Fact]
    public void OverlayIsHardLightWithTheOperandsSwapped()
    {
        // Overlay(cb, cs) = HardLight(cs, cb) — the definition, and a cheap way to catch a mix-up.
        double[] values = [0d, 0.25, 0.5, 0.75, 1d];

        foreach (double backdrop in values)
        {
            foreach (double source in values)
            {
                // Overlay picks its branch on the backdrop; Hard Light picks its own on the source.
                double expected = backdrop <= 0.5
                    ? 2 * backdrop * source
                    : 1 - 2 * (1 - backdrop) * (1 - source);

                Assert.Equal(expected, Blending.Channel(LayerBlendMode.Overlay, backdrop, source), 1e-9);
            }
        }
    }

    [Theory]
    [InlineData(0.0, 0.5, 0.0)]   // A black backdrop stays black however bright the source.
    [InlineData(0.0, 1.0, 0.0)]
    [InlineData(0.4, 1.0, 1.0)]   // A white source blows out to white.
    [InlineData(0.25, 0.5, 0.5)]
    [InlineData(0.5, 0.5, 1.0)]   // 0.5 / (1 − 0.5) clips at 1.
    public void ColorDodgeHandlesBothEnds(double backdrop, double source, double expected) =>
        Assert.Equal(expected, Blending.Channel(LayerBlendMode.ColorDodge, backdrop, source), Tolerance);

    [Theory]
    [InlineData(1.0, 0.5, 1.0)]   // A white backdrop stays white.
    [InlineData(0.5, 0.0, 0.0)]   // A black source burns to black.
    [InlineData(0.75, 0.5, 0.5)]
    [InlineData(0.25, 0.5, 0.0)]
    public void ColorBurnHandlesBothEnds(double backdrop, double source, double expected) =>
        Assert.Equal(expected, Blending.Channel(LayerBlendMode.ColorBurn, backdrop, source), Tolerance);

    [Fact]
    public void LuminosityTakesTheSourcesBrightnessAndTheBackdropsColour()
    {
        (double R, double G, double B) backdrop = (0.8, 0.2, 0.2);
        (double R, double G, double B) source = (0.5, 0.5, 0.5);

        (double R, double G, double B) result = Blending.Rgb(LayerBlendMode.Luminosity, backdrop, source);

        // The result's luminosity is the source's; its hue stays the backdrop's, so red still leads.
        Assert.Equal(0.5, 0.3 * result.R + 0.59 * result.G + 0.11 * result.B, 1e-9);
        Assert.True(result.R > result.G);
        Assert.True(result.R > result.B);
    }

    [Fact]
    public void ColorTakesTheSourcesColourAndTheBackdropsBrightness()
    {
        (double R, double G, double B) backdrop = (0.2, 0.2, 0.2);
        (double R, double G, double B) source = (0.1, 0.8, 0.3);

        (double R, double G, double B) result = Blending.Rgb(LayerBlendMode.Color, backdrop, source);

        Assert.Equal(0.2, 0.3 * result.R + 0.59 * result.G + 0.11 * result.B, 1e-9);
        Assert.True(result.G > result.R);
    }

    [Fact]
    public void ANonSeparableResultStaysInRange()
    {
        // SetLuminosity can push a channel outside 0–1, and the clip has to bring it back by moving
        // towards the luminosity rather than clamping, or the hue shifts.
        (double R, double G, double B) result =
            Blending.Rgb(LayerBlendMode.Color, (0.95, 0.95, 0.95), (1.0, 0.0, 0.0));

        // A hair of slack: the clip lands a channel exactly on 1, and floating point can overshoot.
        Assert.InRange(result.R, -1e-9, 1 + 1e-9);
        Assert.InRange(result.G, -1e-9, 1 + 1e-9);
        Assert.InRange(result.B, -1e-9, 1 + 1e-9);
    }

    // --- Compositing ---------------------------------------------------------------------------

    private static byte[] Premultiplied(double r, double g, double b, double a) =>
        [(byte)Math.Round(r * a * 255), (byte)Math.Round(g * a * 255), (byte)Math.Round(b * a * 255),
         (byte)Math.Round(a * 255)];

    [Fact]
    public void DrawingOntoNothingKeepsTheSource()
    {
        byte[] backdrop = [0, 0, 0, 0];
        byte[] source = Premultiplied(1, 0.5, 0, 1);

        Blending.Composite(LayerBlendMode.Multiply, backdrop, source, 1);

        // With nothing underneath there is no blend to do, whatever the mode says.
        Assert.Equal(source, backdrop);
    }

    [Fact]
    public void OpacityScalesCoverageNotColour()
    {
        byte[] backdrop = [0, 0, 0, 0];
        byte[] source = Premultiplied(1, 0, 0, 1);

        Blending.Composite(LayerBlendMode.Normal, backdrop, source, 0.5);

        Assert.Equal(128, (int)backdrop[3]);
        // Premultiplied, so the colour follows the alpha down; the straight colour is still red.
        Assert.Equal(128, (int)backdrop[0]);
        Assert.Equal(0, (int)backdrop[1]);
    }

    [Fact]
    public void MultiplyOverAnOpaqueBackdropMultiplies()
    {
        byte[] backdrop = Premultiplied(1, 1, 1, 1);
        byte[] source = Premultiplied(0.5, 0.5, 0.5, 1);

        Blending.Composite(LayerBlendMode.Multiply, backdrop, source, 1);

        Assert.Equal(255, (int)backdrop[3]);
        Assert.InRange((int)backdrop[0], 127, 128);
    }

    [Fact]
    public void ASoftSourceFadesOutInsteadOfEndingOnAnEdge()
    {
        // The property Core Graphics loses on Dodge and Burn: at low source alpha the result has to
        // approach the backdrop, not jump to the blended colour.
        byte[] grey = Premultiplied(0.5, 0.5, 0.5, 1);
        byte[] black = Premultiplied(0, 0, 0, 1);

        byte[] barelyThere = [.. grey];
        Blending.Composite(LayerBlendMode.ColorBurn, barelyThere, black, 1.0 / 255);

        byte[] fully = [.. grey];
        Blending.Composite(LayerBlendMode.ColorBurn, fully, black, 1);

        Assert.Equal(0, (int)fully[0]);              // Full coverage burns to black.
        Assert.InRange((int)barelyThere[0], 125, 128); // A whisper of coverage barely moves it.
    }

    [Fact]
    public void AlphaAccumulatesAsSourceOver()
    {
        byte[] backdrop = Premultiplied(0, 0, 0, 0.5);
        Blending.Composite(LayerBlendMode.Normal, backdrop, Premultiplied(0, 0, 0, 0.5), 1);

        // 0.5 + 0.5 × (1 − 0.5) = 0.75
        Assert.InRange((int)backdrop[3], 190, 192);
    }

    [Fact]
    public void ChannelsNeverExceedTheirAlpha()
    {
        // A premultiplied channel above its own alpha is not a colour; it shows up as a bright
        // fringe wherever a soft edge meets something.
        var random = new Random(7);
        byte[] backdrop = new byte[4];

        foreach (LayerBlendMode mode in Enum.GetValues<LayerBlendMode>())
        {
            for (int i = 0; i < 400; i++)
            {
                double alpha = random.NextDouble();
                backdrop = Premultiplied(random.NextDouble(), random.NextDouble(), random.NextDouble(), alpha);
                byte[] source = Premultiplied(random.NextDouble(), random.NextDouble(), random.NextDouble(),
                                              random.NextDouble());

                Blending.Composite(mode, backdrop, source, random.NextDouble());

                for (int channel = 0; channel < 3; channel++)
                    Assert.True(backdrop[channel] <= backdrop[3],
                        $"{mode}: channel {channel} is {backdrop[channel]} with alpha {backdrop[3]}");
            }
        }
    }
}
