using Compositor_korean_win.Core;
using Xunit;
using static Compositor_korean_win.Core.Tests.RenderFixture;

namespace Compositor_korean_win.Core.Tests;

/// <summary>New documents, canvas size, image size and importing images.</summary>
public class DocumentCommandsTests
{
    [Fact]
    public void ANewDocumentHasABackgroundOrNothing()
    {
        Language before = Localizer.Current;
        Localizer.Current = Language.English;
        try
        {
            CanvasDocument white = DocumentCommands.New(6, 4, 72, new Rgba(255, 255, 255));
            Assert.Equal("Background", white.Layers[0].Name);
            Assert.Equal((255, 255, 255, 255), At(white.Layers[0].Image!, 5, 3));
            white.Layers[0].Image!.Release();

            CanvasDocument clear = DocumentCommands.New(6, 4, 300, null);
            Assert.Null(clear.Layers[0].Image);
            Assert.Equal(300, clear.Resolution);
        }
        finally
        {
            Localizer.Current = before;
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(4, 5, 5)]
    [InlineData(8, 10, 10)]
    [InlineData(2, 10, 0)]
    public void TheAnchorDecidesWhereTheContentGoes(int anchor, double x, double y) =>
        Assert.Equal(new Point(x, y), DocumentCommands.AnchorOffset(10, 10, 20, 20, anchor));

    [Fact]
    public void AnOddExtraPixelGoesRightAndDown() =>
        Assert.Equal(new Point(1, 1), DocumentCommands.AnchorOffset(10, 10, 13, 13, 4));

    [Fact]
    public void CanvasSizeMovesTheLayersNotTheirPixels()
    {
        using PixelBuffer pixels = Solid(4, 4, 1, 2, 3);
        ImageLayer layer = Layer("a", pixels, 3, 3);

        CanvasDocument next = DocumentCommands.ResizeCanvas(Document(10, 10, layer), 20, 20, anchor: 4)!;

        Assert.Equal(20, next.Width);
        Assert.Equal(new Point(8, 8), next.Layer(layer.Id)!.Transform.Origin);
        Assert.Same(pixels, next.Layer(layer.Id)!.Image);
    }

    [Fact]
    public void ACanvasExtensionColourFillsOnlyTheNewSpace()
    {
        using PixelBuffer pixels = Solid(4, 4, 1, 2, 3);
        ImageLayer layer = Layer("a", pixels, 0, 0);

        CanvasDocument next = DocumentCommands.ResizeCanvas(Document(4, 4, layer), 8, 6, anchor: 0, new Rgba(255, 0, 0))!;
        ImageLayer extension = next.Layers[0];

        try
        {
            Assert.Equal(2, next.Layers.Count);
            Assert.Equal(layer.Id, next.Layers[1].Id);
            Assert.Equal((0, 0, 0, 0), At(extension.Image!, 3, 3)); // where the old canvas was
            Assert.Equal((255, 0, 0, 255), At(extension.Image!, 4, 0));
            Assert.Equal((255, 0, 0, 255), At(extension.Image!, 0, 5));
        }
        finally
        {
            extension.Image!.Release();
        }

        // A smaller canvas has no new space to fill.
        CanvasDocument cropped = DocumentCommands.ResizeCanvas(Document(4, 4, layer), 2, 2, anchor: 0, new Rgba(255, 0, 0))!;
        Assert.Single(cropped.Layers);
    }

    [Fact]
    public void ImageSizeResamplesEachLayerIntoItsNewBox()
    {
        using PixelBuffer pixels = Solid(10, 10, 200, 100, 50);
        ImageLayer layer = Layer("a", pixels, 4, 0);

        CanvasDocument next = DocumentCommands.ResizeImage(Document(20, 10, layer), 10, 5, 72)!;
        ImageLayer resized = next.Layer(layer.Id)!;

        try
        {
            Assert.Equal((10, 5), (next.Width, next.Height));
            Assert.Equal(new Point(2, 0), resized.Transform.Origin);
            Assert.Equal(5, resized.Image!.Width);
            Assert.Equal((200, 100, 50, 255), At(resized.Image, 2, 2));
        }
        finally
        {
            resized.Image!.Release();
        }
    }

    [Fact]
    public void ImageSizeKeepsAFlippedLayerTheWayItWasShown()
    {
        using PixelBuffer ramp = Gradient(8, 8);
        ImageLayer layer = Layer("ramp", ramp) with
        {
            Transform = new LayerTransform(Point.Zero, new Size(8, 8)) { FlipX = true },
        };

        CanvasDocument next = DocumentCommands.ResizeImage(Document(8, 8, layer), 16, 16, 72)!;
        using var backend = new SoftwareRenderBackend();
        using PixelBuffer shown = LayerCompositor.Render(next, backend);

        // Flipped, the ramp's bright end was on the left, and still is.
        Assert.True(At(shown, 1, 8).R > At(shown, 14, 8).R);
        next.Layer(layer.Id)!.Image!.Release();
    }

    [Fact]
    public void AnImportedImageLandsInTheMiddleAboveTheActiveLayer()
    {
        using PixelBuffer backdrop = Solid(20, 20, 0, 0, 0);
        ImageLayer bottom = Layer("bottom", backdrop);
        PixelBuffer imported = Solid(4, 6, 9, 9, 9);

        (CanvasDocument next, Guid added) = DocumentCommands.AddImage(Document(20, 20, bottom), imported, "photo", bottom.Id);

        Assert.Equal(1, next.IndexOf(added));
        Assert.Equal(new Point(8, 7), next.Layer(added)!.Transform.Origin);
        imported.Release();
    }

    [Fact]
    public void SizesPastTheFormatsLimitsAreRefused()
    {
        Assert.False(DocumentCommands.IsValidSize(0, 10));
        Assert.False(DocumentCommands.IsValidSize(30_001, 10));
        Assert.False(DocumentCommands.IsValidSize(20_000, 20_000));
        Assert.True(DocumentCommands.IsValidSize(10_000, 10_000));
    }
}
