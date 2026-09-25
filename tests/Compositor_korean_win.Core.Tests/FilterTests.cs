using Compositor_korean_win.Core;
using Xunit;
using static Compositor_korean_win.Core.Tests.RenderFixture;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// The Filter menu on a layer: the filters themselves, committing one, and the live preview.
/// </summary>
/// <remarks>
/// The preview tests are the ones that matter most. A preview that is cut to the view and reduced
/// is only worth having if, where nothing is reduced, it shows exactly what committing would — and
/// if its cost follows the frame rather than the layer.
/// </remarks>
public class FilterTests
{
    private static PixelBuffer Dot(int size, int x, int y)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(size, size);
        buffer.Row(y).Slice(x * 4, 4).Fill(255);
        return buffer;
    }

    private static long Total(PixelBuffer buffer, int channel)
    {
        long total = 0;
        for (int y = 0; y < buffer.Height; y++)
            for (int x = 0; x < buffer.Width; x++)
                total += buffer.Row(y)[x * 4 + channel];
        return total;
    }

    // MARK: The filters

    [Theory]
    [InlineData(PixelFilters.BoxThreshold)]
    [InlineData(5.0)]
    [InlineData(40.0)]
    public void ThreeBoxesAddUpToTheGaussiansVariance(double sigma)
    {
        double variance = PixelFilters.BoxRadii(sigma, 3).Sum(r => ((2.0 * r + 1) * (2 * r + 1) - 1) / 12);
        Assert.InRange(variance, sigma * sigma * 0.8, sigma * sigma * 1.2);
    }

    [Fact]
    public void AGaussianBlurSpreadsASquareEvenlyAndKeepsItsWeight()
    {
        // A square rather than a dot: a dot blurred this far is a few levels high everywhere, and
        // rounding six passes would say more about rounding than about the blur.
        using PixelBuffer white = Solid(11, 11, 255, 255, 255);
        using PixelBuffer square = PixelRegion.Copy(white, new PixelRect(-25, -25, 61, 61));
        using PixelBuffer blurred = PixelFilters.GaussianBlur(square, 3);

        Assert.True(At(blurred, 25, 30).A < 255, "the edge did not soften");
        Assert.True(At(blurred, 22, 30).A > 0, "nothing spread past the edge");
        Assert.Equal(At(blurred, 22, 30), At(blurred, 38, 30));
        Assert.Equal(At(blurred, 30, 22), At(blurred, 30, 38));

        // Far from any edge nothing leaves the buffer.
        Assert.InRange(Total(blurred, 3), 121 * 255 * 0.97, 121 * 255 * 1.03);
    }

    [Fact]
    public void ASmallBlurSpreadsADotAsTheKernelSays()
    {
        // Below the box threshold the kernel runs as it is: the centre keeps 1 / (2πσ²) of the dot.
        using PixelBuffer dot = Dot(21, 10, 10);
        using PixelBuffer blurred = PixelFilters.GaussianBlur(dot, 1);

        Assert.InRange(At(blurred, 10, 10).A, 38, 42);
        Assert.Equal(At(blurred, 9, 10), At(blurred, 11, 10));
        Assert.Equal(At(blurred, 10, 9), At(blurred, 10, 11));
    }

    [Fact]
    public void AGaussianBlurLeavesTheMiddleOfAFlatAreaAlone()
    {
        using PixelBuffer flat = Solid(40, 40, 90, 120, 150);
        using PixelBuffer blurred = PixelFilters.GaussianBlur(flat, 2);

        Assert.Equal(At(flat, 20, 20), At(blurred, 20, 20));
        Assert.True(At(blurred, 0, 20).A < 255, "the edge should soften into the transparency past it");
    }

    [Fact]
    public void AHorizontalMotionBlurStaysOnItsRow()
    {
        using PixelBuffer dot = Dot(31, 15, 15);
        using PixelBuffer streaked = PixelFilters.MotionBlur(dot, 9, 0);

        Assert.True(At(streaked, 19, 15).A > 0, "no streak along the row");
        Assert.Equal(At(streaked, 11, 15), At(streaked, 19, 15));
        Assert.Equal(0, At(streaked, 15, 14).A);
        Assert.Equal(0, At(streaked, 15, 16).A);
    }

    [Fact]
    public void AVerticalMotionBlurRunsUpAndDown()
    {
        using PixelBuffer dot = Dot(31, 15, 15);
        using PixelBuffer streaked = PixelFilters.MotionBlur(dot, 9, 90);

        Assert.True(At(streaked, 15, 11).A > 0, "no streak along the column");
        Assert.Equal(0, At(streaked, 14, 15).A);
    }

    [Fact]
    public void NoLensCorrectionIsACopy()
    {
        using PixelBuffer image = Gradient(24, 17);
        using PixelBuffer corrected = PixelFilters.Run(image, FilterKind.LensCorrection, new FilterSettings());

        for (int y = 0; y < 17; y++)
            Assert.True(image.Row(y)[..(24 * 4)].SequenceEqual(corrected.Row(y)[..(24 * 4)]), $"row {y}");
    }

    [Fact]
    public void RemovingBarrelDistortionPullsTheCornersIn()
    {
        // Positive distortion samples from nearer the centre, so the corners show what was further in.
        using PixelBuffer image = Gradient(64, 64);
        using PixelBuffer corrected = PixelFilters.Run(image, FilterKind.LensCorrection,
                                                       new FilterSettings { Distortion = 100 });

        Assert.True(At(corrected, 0, 0).R > At(image, 0, 0).R, "the top-left corner did not move inwards");
        Assert.Equal(255, At(corrected, 0, 0).A);
    }

    // MARK: Distort

    [Theory]
    [InlineData(FilterKind.Pinch)]
    [InlineData(FilterKind.Spherize)]
    [InlineData(FilterKind.Twirl)]
    [InlineData(FilterKind.Wave)]
    public void ADistortionOfNothingIsACopy(FilterKind kind)
    {
        using PixelBuffer image = Gradient(24, 17);
        var none = new FilterSettings { Strength = 0, TwirlAngle = 0, Amplitude = 0 };
        using PixelBuffer result = PixelFilters.Run(image, kind, none);

        for (int y = 0; y < 17; y++)
            Assert.True(image.Row(y)[..(24 * 4)].SequenceEqual(result.Row(y)[..(24 * 4)]), $"row {y}");
    }

    /// <summary>The centre of the brightest pixels, which is where a dot has gone.</summary>
    private static (double X, double Y) Centroid(PixelBuffer buffer)
    {
        double sx = 0, sy = 0, total = 0;
        for (int y = 0; y < buffer.Height; y++)
            for (int x = 0; x < buffer.Width; x++)
            {
                int a = buffer.Row(y)[x * 4 + 3];
                sx += a * (x + 0.5);
                sy += a * (y + 0.5);
                total += a;
            }
        return (sx / total, sy / total);
    }

    [Fact]
    public void APinchDrawsASpotTowardsTheCentreAndABulgePushesItOut()
    {
        using PixelBuffer white = Solid(3, 3, 255, 255, 255);
        using PixelBuffer spot = PixelRegion.Copy(white, new PixelRect(-44, -31, 64, 64));

        using PixelBuffer pinched = PixelFilters.Run(spot, FilterKind.Pinch, new FilterSettings { Strength = 80 });
        using PixelBuffer bulged = PixelFilters.Run(spot, FilterKind.Spherize, new FilterSettings { Strength = 80 });

        Assert.True(Centroid(pinched).X < 45.5 - 1, $"a pinch left the spot at {Centroid(pinched).X}");
        Assert.True(Centroid(bulged).X > 45.5 + 1, $"a bulge left the spot at {Centroid(bulged).X}");
    }

    [Fact]
    public void APositiveTwirlTurnsClockwise()
    {
        // A spot right of the centre; clockwise on screen, where y runs down, carries it downwards.
        using PixelBuffer white = Solid(3, 3, 255, 255, 255);
        using PixelBuffer spot = PixelRegion.Copy(white, new PixelRect(-42, -31, 64, 64));
        using PixelBuffer twirled = PixelFilters.Run(spot, FilterKind.Twirl, new FilterSettings { TwirlAngle = 90 });

        Assert.True(Centroid(twirled).Y > 32.5 + 1, $"the spot went to {Centroid(twirled)}");
    }

    [Fact]
    public void AWaveNeedsRoomForItsHeight()
    {
        var settings = new FilterSettings { Amplitude = 25 };
        Assert.True(PixelFilters.Reach(FilterKind.Wave, settings) >= 25);
        Assert.Equal(0, PixelFilters.Reach(FilterKind.Twirl, settings));
    }

    [Fact]
    public void RectangularToPolarGathersTheTopRowAtTheCentre()
    {
        using PixelBuffer red = Solid(40, 4, 255, 0, 0);
        using PixelBuffer blue = Solid(40, 40, 0, 0, 255);

        // Red along the top, blue under it.
        using PixelBuffer both = PixelRegion.Copy(blue, new PixelRect(0, 0, 40, 40));
        for (int y = 0; y < 4; y++) red.Row(y)[..(40 * 4)].CopyTo(both.Row(y));

        using PixelBuffer polar = PixelFilters.Run(both, FilterKind.PolarCoordinates, new FilterSettings { ToPolar = true });
        Assert.True(At(polar, 20, 20).R > 200, $"the centre is {At(polar, 20, 20)}");
        Assert.True(At(polar, 20, 36).B > 200, $"near the rim is {At(polar, 20, 36)}");

        // Beyond the ellipse there is nothing to read.
        Assert.Equal(0, At(polar, 0, 0).A);
    }

    [Fact]
    public void AddNoiseIsFixedByItsSeed()
    {
        using PixelBuffer grey = Solid(16, 16, 128, 128, 128);
        var settings = new FilterSettings { Amount = 30, Seed = 5 };

        using PixelBuffer first = PixelFilters.Run(grey, FilterKind.AddNoise, settings);
        using PixelBuffer second = PixelFilters.Run(grey, FilterKind.AddNoise, settings);
        using PixelBuffer other = PixelFilters.Run(grey, FilterKind.AddNoise, settings with { Seed = 6 });

        Assert.True(first.Row(8)[..64].SequenceEqual(second.Row(8)[..64]));
        Assert.False(first.Row(8)[..64].SequenceEqual(other.Row(8)[..64]));
        Assert.Equal((128, 128, 128, 255), At(grey, 3, 3)); // The source is not touched.
    }

    // MARK: Committing

    [Fact]
    public void ABlurGrowsTheLayerSoItHasSomewhereToSpread()
    {
        using PixelBuffer pixels = Solid(10, 10, 200, 0, 0);
        ImageLayer layer = Layer("red", pixels, 20, 20) with
        {
            Shape = new LayerShapeStyle { Kind = ShapeKind.Rectangle, Red = 1 },
        };

        ImageLayer blurred = LayerFilters.Apply(layer, FilterKind.GaussianBlur, new FilterSettings { Radius = 2 }, null);

        Assert.True(blurred.Image!.Width > 10 && blurred.Image.Height > 10);
        Assert.Equal(blurred.Image.Width, blurred.Transform.Size.Width, precision: 9);

        // Grown evenly: the middle has not moved.
        Assert.Equal(25, blurred.Transform.Center.X, precision: 9);
        Assert.Equal(25, blurred.Transform.Center.Y, precision: 9);

        Assert.Null(blurred.Shape);
        blurred.Image.Release();
    }

    [Fact]
    public void AColourFilterKeepsTheLayersBox()
    {
        using PixelBuffer pixels = Solid(10, 10, 200, 200, 200);
        ImageLayer layer = Layer("grey", pixels, 3, 4);

        var settings = new FilterSettings
        {
            Adjustment = new LayerAdjustment(AdjustmentKind.GradientMap)
            {
                GradientMapSettings = new GradientMapSettings { Highlights = new AdjustmentColor(0, 0, 1) },
            },
        };
        ImageLayer mapped = LayerFilters.Apply(layer, FilterKind.Adjustment, settings, null);

        Assert.Equal(layer.Transform, mapped.Transform);
        Assert.True(At(mapped.Image!, 5, 5).B > At(mapped.Image!, 5, 5).R);
        mapped.Image!.Release();
    }

    [Fact]
    public void ASelectionConfinesTheFilter()
    {
        using PixelBuffer pixels = Solid(20, 20, 200, 200, 200);
        // The layer is moved, so the selection has to cross into its pixels to land right.
        ImageLayer layer = Layer("grey", pixels, 10, 0);
        DocumentSelection left = DocumentSelection.Rectangle(new Rect(0, 0, 20, 20));

        var settings = new FilterSettings
        {
            Adjustment = new LayerAdjustment(AdjustmentKind.GradientMap)
            {
                GradientMapSettings = new GradientMapSettings { Highlights = new AdjustmentColor(1, 0, 0) },
            },
        };
        ImageLayer mapped = LayerFilters.Apply(layer, FilterKind.Adjustment, settings, left);

        // Layer pixels 0–9 are document 10–19, inside the selection; 10–19 are outside it.
        Assert.Equal(0, At(mapped.Image!, 5, 10).B);
        Assert.Equal((200, 200, 200, 255), At(mapped.Image!, 15, 10));
        mapped.Image!.Release();
    }

    [Fact]
    public void ALayerWithItsOwnMaskIsNotGrown()
    {
        using PixelBuffer pixels = Solid(10, 10, 200, 0, 0);
        using PixelBuffer coverage = Coverage(10, 10, (_, _) => 255);
        ImageLayer layer = Layer("masked", pixels) with { Mask = new LayerMask { Coverage = coverage } };

        Assert.Equal(0, LayerFilters.Margin(layer, FilterKind.GaussianBlur, new FilterSettings { Radius = 4 }));
    }

    // MARK: Preview

    private static PixelBuffer RenderWith(CanvasDocument document, LiveEdit? live)
    {
        using var backend = new SoftwareRenderBackend();
        using IRenderSurface surface = backend.CreateSurface(document.Width, document.Height);
        surface.Clear();
        LayerCompositor.Draw(document, surface, backend, CanvasProjection.Identity, live);
        return surface.Read();
    }

    [Theory]
    [InlineData(FilterKind.GaussianBlur)]
    [InlineData(FilterKind.MotionBlur)]
    [InlineData(FilterKind.LensCorrection)]
    [InlineData(FilterKind.AddNoise)]
    [InlineData(FilterKind.Pinch)]
    [InlineData(FilterKind.Spherize)]
    [InlineData(FilterKind.Twirl)]
    [InlineData(FilterKind.Wave)]
    [InlineData(FilterKind.PolarCoordinates)]
    public void AtFullSizeThePreviewShowsExactlyWhatCommittingWould(FilterKind kind)
    {
        using PixelBuffer pixels = Gradient(32, 24);
        ImageLayer layer = Layer("gradient", pixels, 16, 20);
        var settings = new FilterSettings { Radius = 2, Distance = 7, Angle = 30, Distortion = 60, Amount = 20, Seed = 3 };

        using var preview = new FilterPreview(layer, kind, settings);
        LiveEdit frame = preview.Frame(CanvasProjection.Identity, 64, 64);
        using PixelBuffer previewed = RenderWith(Document(64, 64, layer), frame);

        ImageLayer committed = LayerFilters.Apply(layer, kind, settings, null);
        using PixelBuffer applied = RenderWith(Document(64, 64, committed), null);
        committed.Image!.Release();

        for (int y = 0; y < 64; y++)
            Assert.True(previewed.Row(y)[..(64 * 4)].SequenceEqual(applied.Row(y)[..(64 * 4)]), $"row {y} differs");
    }

    [Fact]
    public void AnUnchangedDistortionPreviewReusesItsPixels()
    {
        using PixelBuffer pixels = Gradient(320, 240);
        ImageLayer layer = Layer("gradient", pixels);
        using var preview = new FilterPreview(layer, FilterKind.Twirl, new FilterSettings { TwirlAngle = 90 });

        LiveEdit first = preview.Frame(CanvasProjection.Identity, 320, 240);
        LiveEdit second = preview.Frame(CanvasProjection.Identity, 320, 240);

        Assert.Same(first, second);
        Assert.Same(((BufferSource)first.Source).Buffer, ((BufferSource)second.Source).Buffer);
        Assert.True(((BufferSource)first.Source).Cacheable);
    }

    [Fact]
    public void AGeometricPreviewHasABoundedInteractionRaster()
    {
        using PixelBuffer pixels = Gradient(2000, 2000);
        ImageLayer layer = Layer("large", pixels);
        using var preview = new FilterPreview(layer, FilterKind.Spherize, new FilterSettings { Strength = 60 });

        preview.Frame(CanvasProjection.Identity, 1000, 800);

        Assert.InRange(preview.LastPixelsFiltered, 1, 512 * 512);
    }

    [Fact]
    public void ThePreviewFiltersTheFrameNotTheLayer()
    {
        // Two thousand pixels square, drawn at a tenth into a frame of two hundred.
        using PixelBuffer pixels = Solid(2000, 2000, 90, 120, 150);
        ImageLayer layer = Layer("big", pixels);

        using var preview = new FilterPreview(layer, FilterKind.GaussianBlur, new FilterSettings { Radius = 20 });
        preview.Frame(new CanvasProjection(0.1, Point.Zero), 200, 200);

        // The reduced level is at most twice the frame on a side, plus the blur's reach.
        Assert.True(preview.LastPixelsFiltered <= 4 * 200 * 200 * 1.2,
                    $"{preview.LastPixelsFiltered} pixels filtered for a frame of {200 * 200}");
    }

    [Fact]
    public void ZoomedInThePreviewFiltersOnlyWhatIsInView()
    {
        using PixelBuffer pixels = Solid(1000, 1000, 90, 120, 150);
        ImageLayer layer = Layer("big", pixels);

        using var preview = new FilterPreview(layer, FilterKind.GaussianBlur, new FilterSettings { Radius = 3 });
        preview.Frame(new CanvasProjection(1, new Point(-400, -400)), 100, 100);

        // A hundred square in view, the blur's reach around it, and a pixel for resampling.
        int side = 100 + (PixelFilters.Reach(FilterKind.GaussianBlur, preview.Settings) + 2) * 2;
        Assert.True(preview.LastPixelsFiltered <= (long)side * side,
                    $"{preview.LastPixelsFiltered} pixels filtered, expected at most {side * side}");
    }

    [Fact]
    public void ThePreviewKeepsToTheSelectionToo()
    {
        using PixelBuffer pixels = Solid(20, 20, 200, 200, 200);
        ImageLayer layer = Layer("grey", pixels);
        var settings = new FilterSettings
        {
            Adjustment = new LayerAdjustment(AdjustmentKind.GradientMap)
            {
                GradientMapSettings = new GradientMapSettings { Highlights = new AdjustmentColor(1, 0, 0) },
            },
        };

        using var preview = new FilterPreview(layer, FilterKind.Adjustment, settings,
                                              DocumentSelection.Rectangle(new Rect(0, 0, 10, 20)));
        using PixelBuffer shown = RenderWith(Document(20, 20, layer), preview.Frame(CanvasProjection.Identity, 20, 20));

        Assert.Equal(0, At(shown, 5, 10).B);
        Assert.Equal((200, 200, 200, 255), At(shown, 15, 10));
    }
}
