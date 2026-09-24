using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// What M6 left behind from M4: painting a layer's mask, Smudge and Liquify, and the clone stamp
/// sampling every layer.
/// </summary>
public class MaskAndWarpTests
{
    // MARK: Mask

    [Fact]
    public void ASinglePixelMaskIsSpreadOverTheLayersGridToBePainted()
    {
        using PixelBuffer image = RenderFixture.Solid(40, 30, 10, 20, 30);
        ImageLayer layer = RenderFixture.Layer("a", image, 5, 7) with { Mask = LayerMask.Solid(revealing: false) };

        (PixelBuffer pixels, LayerTransform placement) = MaskEditing.Canvas(layer)!.Value;
        using (pixels)
        {
            Assert.Equal((40, 30), (pixels.Width, pixels.Height));
            Assert.Equal(layer.Transform, placement);
            Assert.Equal((0, 0, 0, 255), RenderFixture.At(pixels, 39, 29));
        }

        layer.Mask!.Coverage.Release();
    }

    [Fact]
    public void AMaskPlacedApartIsPaintedInItsOwnBox()
    {
        using PixelBuffer image = RenderFixture.Solid(40, 30, 10, 20, 30);
        var apart = new LayerTransform(new Point(0, 0), new Size(12, 8));
        ImageLayer layer = RenderFixture.Layer("a", image) with
        {
            Mask = LayerMask.Solid(revealing: true) with { Placement = apart },
        };

        (PixelBuffer pixels, LayerTransform placement) = MaskEditing.Canvas(layer)!.Value;
        using (pixels)
        {
            Assert.Equal((12, 8), (pixels.Width, pixels.Height));
            Assert.Equal(apart, placement);
        }

        layer.Mask!.Coverage.Release();
    }

    [Fact]
    public void ABrushOnAMaskLaysGreyAndKeepsItOpaque()
    {
        using PixelBuffer image = RenderFixture.Solid(32, 32, 200, 100, 50);
        ImageLayer layer = RenderFixture.Layer("a", image) with { Mask = LayerMask.Solid(revealing: true) };
        (PixelBuffer mask, _) = MaskEditing.Canvas(layer)!.Value;

        var settings = new BrushSettings { Diameter = 10, Hardness = 1, Color = MaskEditing.Grey(white: false) };
        using (mask)
        using (var stroke = new BrushStroke(mask, 32, 32, settings))
        {
            stroke.Append(new Point(16, 16));
            using PixelBuffer painted = stroke.Commit();

            Assert.Equal((0, 0, 0, 255), RenderFixture.At(painted, 16, 16));
            Assert.Equal((255, 255, 255, 255), RenderFixture.At(painted, 2, 2));
        }

        layer.Mask!.Coverage.Release();
    }

    [Fact]
    public void InvertingAMaskWithinASelectionLeavesTheRestAlone()
    {
        using PixelBuffer image = RenderFixture.Solid(20, 20, 1, 2, 3);
        ImageLayer layer = RenderFixture.Layer("a", image) with { Mask = LayerMask.Solid(revealing: true) };

        ImageLayer inverted = MaskEditing.Invert(layer, DocumentSelection.Rectangle(new Rect(0, 0, 10, 20)))!;

        Assert.Equal((0, 0, 0, 255), RenderFixture.At(inverted.Mask!.Coverage, 4, 10));
        Assert.Equal((255, 255, 255, 255), RenderFixture.At(inverted.Mask!.Coverage, 15, 10));
        // The layer's own pixels are not the mask's business.
        Assert.Same(image, inverted.Image);

        inverted.Mask.Coverage.Release();
        layer.Mask!.Coverage.Release();
    }

    [Fact]
    public void FillingAMaskUsesWhiteOrBlackNotThePalette()
    {
        using PixelBuffer image = RenderFixture.Solid(20, 20, 1, 2, 3);
        ImageLayer layer = RenderFixture.Layer("a", image) with { Mask = LayerMask.Solid(revealing: true) };

        ImageLayer filled = MaskEditing.Fill(layer, white: false, selection: null)!;
        Assert.Equal((0, 0, 0, 255), RenderFixture.At(filled.Mask!.Coverage, 10, 10));

        filled.Mask.Coverage.Release();
        layer.Mask!.Coverage.Release();
    }

    [Fact]
    public void AMasksDarkHalfLoadsAsASelectionOnTheDocument()
    {
        using PixelBuffer image = RenderFixture.Solid(20, 20, 1, 2, 3);
        // Black on the left half: hidden there.
        using PixelBuffer coverage = RenderFixture.Coverage(20, 20, (x, _) => x < 10 ? (byte)0 : (byte)255);
        ImageLayer layer = RenderFixture.Layer("a", image, 100, 50) with { Mask = new LayerMask { Coverage = coverage } };

        DocumentSelection selection = MaskEditing.Selection(layer)!;

        Assert.True(selection.Contains(new Point(105, 60)));
        Assert.False(selection.Contains(new Point(115, 60)));
    }

    [Fact]
    public void AMaskThatHidesNothingLoadsNoSelection()
    {
        using PixelBuffer image = RenderFixture.Solid(20, 20, 1, 2, 3);
        ImageLayer layer = RenderFixture.Layer("a", image) with { Mask = LayerMask.Solid(revealing: true) };

        Assert.Null(MaskEditing.Selection(layer));
        layer.Mask!.Coverage.Release();
    }

    // MARK: Smudge and Liquify

    private static PixelBuffer Halves()
    {
        // Left half red, right half blue.
        PixelBuffer image = PixelBuffer.Allocate(64, 64);
        for (int y = 0; y < 64; y++)
        {
            Span<byte> row = image.Row(y);
            for (int x = 0; x < 64; x++)
            {
                row[x * 4 + 0] = x < 32 ? (byte)255 : (byte)0;
                row[x * 4 + 2] = x < 32 ? (byte)0 : (byte)255;
                row[x * 4 + 3] = 255;
            }
        }
        return image;
    }

    [Theory]
    [InlineData(BlurToolMode.Smudge)]
    [InlineData(BlurToolMode.Liquify)]
    public void AWarpCarriesColourAlongTheStroke(BlurToolMode mode)
    {
        using PixelBuffer image = Halves();
        var settings = new BrushSettings { Diameter = 16, Hardness = 0.5, Opacity = 1 };

        using var stroke = new WarpStroke(image, mode, settings);
        for (int x = 24; x <= 40; x++) stroke.Append(new Point(x, 32));
        using PixelBuffer result = stroke.Commit(selection: null);

        // Red has been dragged into what was blue, and the layer's own pixels are untouched.
        Assert.True(RenderFixture.At(result, 36, 32).R > 60, $"{RenderFixture.At(result, 36, 32)}");
        Assert.Equal((0, 0, 255, 255), RenderFixture.At(image, 36, 32));
        // Far from the stroke nothing moves.
        Assert.Equal((0, 0, 255, 255), RenderFixture.At(result, 60, 5));
        Assert.False(stroke.IsEmpty);
    }

    [Fact]
    public void AWarpStaysInsideTheSelection()
    {
        using PixelBuffer image = Halves();
        var settings = new BrushSettings { Diameter = 16, Hardness = 0.5, Opacity = 1 };

        using var stroke = new WarpStroke(image, BlurToolMode.Smudge, settings);
        for (int x = 24; x <= 40; x++) stroke.Append(new Point(x, 32));
        using PixelBuffer result = stroke.Commit(DocumentSelection.Rectangle(new Rect(0, 0, 34, 64)));

        Assert.Equal((0, 0, 255, 255), RenderFixture.At(result, 38, 32));
    }

    [Fact]
    public void APressWithoutAMoveChangesNothing()
    {
        using PixelBuffer image = Halves();
        using var stroke = new WarpStroke(image, BlurToolMode.Liquify, new BrushSettings { Diameter = 16 });
        stroke.Append(new Point(30, 30));

        Assert.True(stroke.IsEmpty);
    }

    // MARK: Clone stamp over every layer

    [Fact]
    public void SamplingEveryLayerSeesWhatTheDocumentShows()
    {
        using PixelBuffer bottom = RenderFixture.Solid(20, 20, 255, 0, 0);
        using PixelBuffer top = RenderFixture.Solid(10, 20, 0, 0, 255);
        CanvasDocument document = RenderFixture.Document(20, 20,
            RenderFixture.Layer("bottom", bottom), RenderFixture.Layer("top", top, 10, 0));

        using PixelBuffer sample = CloneSampling.AllLayers(document, new LayerTransform(Point.Zero, new Size(20, 20)), 20, 20);

        Assert.Equal((255, 0, 0, 255), RenderFixture.At(sample, 5, 5));
        Assert.Equal((0, 0, 255, 255), RenderFixture.At(sample, 15, 5));
    }

    [Fact]
    public void SamplingEveryLayerIsCarriedIntoAMovedLayersGrid()
    {
        using PixelBuffer bottom = RenderFixture.Solid(20, 20, 255, 0, 0);
        using PixelBuffer top = RenderFixture.Solid(10, 20, 0, 0, 255);
        CanvasDocument document = RenderFixture.Document(20, 20,
            RenderFixture.Layer("bottom", bottom), RenderFixture.Layer("top", top, 10, 0));

        // A layer of 10×20 pixels sitting over the right half.
        using PixelBuffer sample = CloneSampling.AllLayers(document, new LayerTransform(new Point(10, 0), new Size(10, 20)), 10, 20);

        Assert.Equal((10, 20), (sample.Width, sample.Height));
        Assert.Equal((0, 0, 255, 255), RenderFixture.At(sample, 2, 5));
    }

    [Fact]
    public void SamplingEveryLayerCanRenderOnlyARequestedTile()
    {
        using PixelBuffer bottom = RenderFixture.Solid(512, 512, 255, 0, 0);
        using PixelBuffer top = RenderFixture.Solid(256, 512, 0, 0, 255);
        CanvasDocument document = RenderFixture.Document(512, 512,
            RenderFixture.Layer("bottom", bottom), RenderFixture.Layer("top", top, 256, 0));
        IPixelSource source = CloneSampling.AllLayersSource(document,
            new LayerTransform(Point.Zero, new Size(512, 512)), 512, 512);

        using PixelBuffer tile = source.Materialize(new PixelRect(240, 100, 32, 24));

        Assert.Equal((32, 24), (tile.Width, tile.Height));
        Assert.Equal((255, 0, 0, 255), RenderFixture.At(tile, 4, 4));
        Assert.Equal((0, 0, 255, 255), RenderFixture.At(tile, 24, 4));
    }

    // MARK: Linking, moving and copying a mask

    [Fact]
    public void AnUnlinkedMaskStaysWhereItWasWhenItsLayerMoves()
    {
        using PixelBuffer image = RenderFixture.Solid(20, 20, 1, 2, 3);
        using PixelBuffer coverage = RenderFixture.Coverage(20, 20, (x, _) => (byte)(x * 12));
        ImageLayer layer = RenderFixture.Layer("a", image, 10, 10) with
        {
            Mask = new LayerMask { Coverage = coverage, IsLinked = false },
        };

        ImageLayer moved = MaskEditing.WithTransform(layer, layer.Transform with { Origin = new Point(30, 10) });

        Assert.Equal(new Point(30, 10), moved.Transform.Origin);
        Assert.Equal(layer.Transform, moved.Mask!.Placement);
    }

    [Fact]
    public void ALinkedMaskPlacedApartMovesWithItsLayer()
    {
        using PixelBuffer image = RenderFixture.Solid(20, 20, 1, 2, 3);
        using PixelBuffer coverage = RenderFixture.Coverage(10, 10, (_, _) => 255);
        var apart = new LayerTransform(new Point(0, 0), new Size(10, 10));
        ImageLayer layer = RenderFixture.Layer("a", image, 10, 10) with
        {
            Mask = new LayerMask { Coverage = coverage, Placement = apart },
        };

        ImageLayer moved = MaskEditing.WithTransform(layer, layer.Transform with { Origin = new Point(15, 13) });

        Assert.Equal(new Point(5, 3), moved.Mask!.Placement!.Origin);
    }

    [Fact]
    public void AMaskFollowingItsLayersGridKeepsFollowingIt()
    {
        using PixelBuffer image = RenderFixture.Solid(20, 20, 1, 2, 3);
        using PixelBuffer coverage = RenderFixture.Coverage(20, 20, (_, _) => 255);
        ImageLayer layer = RenderFixture.Layer("a", image) with { Mask = new LayerMask { Coverage = coverage } };

        ImageLayer moved = MaskEditing.WithTransform(layer, layer.Transform with { Origin = new Point(7, 7) });

        Assert.Null(moved.Mask!.Placement);
    }

    [Fact]
    public void AMaskMovedOnItsOwnBackOntoItsLayerFollowsItsGridAgain()
    {
        using PixelBuffer image = RenderFixture.Solid(20, 20, 1, 2, 3);
        using PixelBuffer coverage = RenderFixture.Coverage(20, 20, (_, _) => 255);
        ImageLayer layer = RenderFixture.Layer("a", image, 4, 4) with
        {
            Mask = new LayerMask { Coverage = coverage, IsLinked = false, Placement = new LayerTransform(Point.Zero, new Size(20, 20)) },
        };

        Assert.Null(MaskEditing.WithMaskPlacement(layer, layer.Transform).Mask!.Placement);
        Assert.True(MaskEditing.ToggleLink(layer)!.Mask!.IsLinked);
    }

    [Fact]
    public void AMaskCopiedToAnotherLayerSitsWhereItSat()
    {
        using PixelBuffer image = RenderFixture.Solid(20, 20, 1, 2, 3);
        using PixelBuffer coverage = RenderFixture.Coverage(20, 20, (x, _) => (byte)(x * 12));
        ImageLayer source = RenderFixture.Layer("a", image, 5, 5) with { Mask = new LayerMask { Coverage = coverage } };
        ImageLayer target = RenderFixture.Layer("b", image, 40, 0);

        ImageLayer copied = MaskEditing.CopyTo(source, target)!;

        Assert.Same(coverage, copied.Mask!.Coverage);
        Assert.Equal(source.Transform, copied.Mask.Placement);
        Assert.Null(MaskEditing.CopyTo(source, source));
    }

    [Theory]
    [InlineData(255, 255)]
    [InlineData(0, 0)]
    public void AMaskPlacedApartShowsItsEdgeBeyondItself(byte level, int expected)
    {
        using PixelBuffer image = RenderFixture.Solid(20, 20, 200, 0, 0);
        using PixelBuffer coverage = RenderFixture.Coverage(10, 20, (_, _) => level);
        // The mask covers the left half of the layer; the right half is beyond it.
        ImageLayer layer = RenderFixture.Layer("a", image) with
        {
            Mask = new LayerMask { Coverage = coverage, Placement = new LayerTransform(Point.Zero, new Size(10, 20)) },
        };

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer frame = LayerCompositor.Render(RenderFixture.Document(20, 20, layer), backend);

        Assert.Equal(expected, RenderFixture.At(frame, 15, 10).A);
    }

    [Fact]
    public void AFoldersMaskStillHidesWhatItDoesNotReach()
    {
        using PixelBuffer image = RenderFixture.Solid(20, 20, 200, 0, 0);
        using PixelBuffer coverage = RenderFixture.Coverage(10, 20, (_, _) => 255);
        var folder = new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = "folder",
            IsGroup = true,
            Transform = new LayerTransform(Point.Zero, new Size(10, 20)),
            Mask = new LayerMask { Coverage = coverage },
        };
        ImageLayer inside = RenderFixture.Layer("in", image) with { ParentId = folder.Id };

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer frame = LayerCompositor.Render(RenderFixture.Document(20, 20, folder, inside), backend);

        Assert.Equal(0, RenderFixture.At(frame, 15, 10).A);
    }

    // MARK: Distorting a masked layer

    private static Point[] Square(double x, double y, double size) =>
        [new(x, y), new(x + size, y), new(x + size, y + size), new(x, y + size)];

    [Fact]
    public void ALinkedMaskOnTheLayersGridIsDistortedWithThePixels()
    {
        using PixelBuffer image = RenderFixture.Solid(20, 20, 200, 0, 0);
        using PixelBuffer coverage = RenderFixture.Coverage(20, 20, (x, _) => x < 10 ? (byte)0 : (byte)255);
        ImageLayer layer = RenderFixture.Layer("a", image) with { Mask = new LayerMask { Coverage = coverage } };

        ImageLayer distorted = QuadWarp.Distort(layer, Square(0, 0, 40))!;

        Assert.Null(distorted.Mask!.Placement);
        Assert.Equal((40, 40), (distorted.Mask.Coverage.Width, distorted.Mask.Coverage.Height));
        Assert.Equal((0, 0, 0, 255), RenderFixture.At(distorted.Mask.Coverage, 8, 20));
        Assert.Equal((255, 255, 255, 255), RenderFixture.At(distorted.Mask.Coverage, 32, 20));

        distorted.Image!.Release();
        distorted.Mask.Coverage.Release();
    }

    [Fact]
    public void ALinkedMaskPlacedApartTakesTheSamePerspective()
    {
        using PixelBuffer image = RenderFixture.Solid(20, 20, 200, 0, 0);
        using PixelBuffer coverage = RenderFixture.Coverage(10, 20, (_, _) => 255);
        ImageLayer layer = RenderFixture.Layer("a", image) with
        {
            Mask = new LayerMask { Coverage = coverage, Placement = new LayerTransform(Point.Zero, new Size(10, 20)) },
        };

        // The whole layer carried 100 to the right: the mask's box goes with it.
        ImageLayer distorted = QuadWarp.Distort(layer, Square(100, 0, 20))!;
        LayerTransform box = distorted.Mask!.Placement!;

        Assert.Equal(100, box.Origin.X, 0.5);
        Assert.Equal(10, box.Size.Width, 0.5);
        Assert.True(distorted.Mask.IsLinked);

        distorted.Image!.Release();
        distorted.Mask.Coverage.Release();
    }

    [Fact]
    public void AnUnlinkedMaskStaysWhereItWasWhenItsLayerIsDistorted()
    {
        using PixelBuffer image = RenderFixture.Solid(20, 20, 200, 0, 0);
        using PixelBuffer coverage = RenderFixture.Coverage(20, 20, (x, _) => (byte)(x * 12));
        ImageLayer layer = RenderFixture.Layer("a", image, 5, 5) with
        {
            Mask = new LayerMask { Coverage = coverage, IsLinked = false },
        };

        ImageLayer distorted = QuadWarp.Distort(layer, Square(0, 0, 40))!;

        Assert.Same(coverage, distorted.Mask!.Coverage);
        Assert.Equal(layer.Transform, distorted.Mask.Placement);
        distorted.Image!.Release();
    }

    [Fact]
    public void ASinglePixelMaskPassesThroughADistortion()
    {
        using PixelBuffer image = RenderFixture.Solid(20, 20, 200, 0, 0);
        ImageLayer layer = RenderFixture.Layer("a", image) with { Mask = LayerMask.Solid(revealing: false) };

        ImageLayer distorted = QuadWarp.Distort(layer, Square(0, 0, 40))!;

        Assert.Same(layer.Mask!.Coverage, distorted.Mask!.Coverage);
        distorted.Image!.Release();
        layer.Mask.Coverage.Release();
    }
}
