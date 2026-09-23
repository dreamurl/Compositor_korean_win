using Compositor_korean_win.Core;
using Xunit;
using static Compositor_korean_win.Core.Tests.RenderFixture;

namespace Compositor_korean_win.Core.Tests;

/// <summary>The Select menu and the edits that change a layer's pixels in one go.</summary>
public class SelectionAndPixelCommandsTests
{
    private static double Area(DocumentSelection selection, int width, int height) =>
        selection.Levels(new PixelRect(0, 0, width, height)).Sum(level => level / 255.0);

    private static byte LevelAt(DocumentSelection selection, int x, int y) =>
        selection.Levels(new PixelRect(x, y, 1, 1))[0];

    // MARK: Select

    [Fact]
    public void InverseSelectsWhatWasNot()
    {
        DocumentSelection left = DocumentSelection.Rectangle(new Rect(0, 0, 10, 20));
        DocumentSelection inverse = SelectionCommands.Inverse(left, 30, 20)!;

        Assert.Equal(400, Area(inverse, 30, 20), precision: 6);
        Assert.Equal(0, LevelAt(inverse, 5, 5));
        Assert.Equal(255, LevelAt(inverse, 20, 5));
        Assert.Null(SelectionCommands.Inverse(null, 30, 20));
    }

    [Fact]
    public void ALayersPixelsBecomeItsOutline()
    {
        using PixelBuffer square = Solid(4, 4, 10, 20, 30);
        ImageLayer layer = Layer("square", square, 2, 3);

        DocumentSelection selection = SelectionCommands.FromLayer(Document(10, 10, layer), layer)!;

        Assert.Equal(16, Area(selection, 10, 10), precision: 6);
        Assert.Equal(255, LevelAt(selection, 3, 4));
        Assert.Equal(0, LevelAt(selection, 1, 4));
    }

    [Fact]
    public void ExpandingRoundsTheCorners()
    {
        DocumentSelection square = DocumentSelection.Rectangle(new Rect(10, 10, 10, 10));
        DocumentSelection grown = SelectionCommands.Expand(square, 40, 40, 2)!;

        Assert.Equal(255, LevelAt(grown, 8, 15));   // Two pixels out along an edge.
        Assert.Equal(0, LevelAt(grown, 7, 15));     // Three is too far.
        Assert.Equal(0, LevelAt(grown, 8, 8));      // The corner diagonal is 2.8 away.
        Assert.Equal(255, LevelAt(grown, 9, 9));
    }

    [Fact]
    public void ContractingPullsTheEdgeIn()
    {
        DocumentSelection square = DocumentSelection.Rectangle(new Rect(10, 10, 10, 10));
        DocumentSelection shrunk = SelectionCommands.Contract(square, 40, 40, 2)!;

        Assert.Equal(36, Area(shrunk, 40, 40), precision: 6);
    }

    [Fact]
    public void TheCanvasEdgeIsNotAnEdgeToContractFrom()
    {
        DocumentSelection all = DocumentSelection.Rectangle(new Rect(0, 0, 20, 20));
        DocumentSelection shrunk = SelectionCommands.Contract(all, 20, 20, 3)!;

        Assert.Equal(400, Area(shrunk, 20, 20), precision: 6);
    }

    // MARK: Pixels

    [Fact]
    public void InvertFlipsColourAndKeepsAlpha()
    {
        using PixelBuffer pixels = Solid(2, 1, 200, 100, 0);
        ImageLayer inverted = PixelCommands.Invert(Layer("p", pixels), null)!;

        Assert.Equal((55, 155, 255, 255), At(inverted.Image!, 0, 0));
        inverted.Image!.Release();
    }

    [Fact]
    public void InvertingASoftEdgeInvertsItsColour()
    {
        // Straight 100 at half alpha is 50 premultiplied; inverted it is straight 155, so 78.
        using PixelBuffer pixels = Solid(1, 1, 100, 100, 100, 128);
        ImageLayer inverted = PixelCommands.Invert(Layer("p", pixels), null)!;

        Assert.InRange(At(inverted.Image!, 0, 0).R, 77, 79);
        inverted.Image!.Release();
    }

    [Fact]
    public void FillingABlankLayerGivesItTheCanvasAndColoursOnlyTheSelection()
    {
        ImageLayer blank = new()
        {
            Id = Guid.NewGuid(),
            Name = "blank",
            Transform = new LayerTransform(Point.Zero, new Size(1, 1)),
        };
        CanvasDocument document = Document(10, 10, blank);
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(2, 2, 3, 3));

        ImageLayer filled = PixelCommands.Fill(document, blank, new Rgba(0, 128, 255), selection)!;

        Assert.Equal(10, filled.Image!.Width);
        Assert.Equal(new Size(10, 10), filled.Transform.Size);
        Assert.Equal((0, 128, 255, 255), At(filled.Image, 3, 3));
        Assert.Equal((0, 0, 0, 0), At(filled.Image, 7, 7));
        filled.Image.Release();
    }

    [Fact]
    public void ClearingEmptiesTheSelection()
    {
        using PixelBuffer pixels = Solid(10, 10, 50, 60, 70);
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(0, 0, 5, 10));

        ImageLayer cleared = PixelCommands.Clear(Layer("p", pixels), selection)!;

        Assert.Equal((0, 0, 0, 0), At(cleared.Image!, 2, 5));
        Assert.Equal((50, 60, 70, 255), At(cleared.Image!, 7, 5));
        cleared.Image!.Release();
    }

    [Fact]
    public void ALayerViaCutTakesTheSelectionIntoANewLayerAbove()
    {
        using PixelBuffer pixels = Solid(10, 10, 50, 60, 70);
        ImageLayer layer = Layer("p", pixels, 5, 5);
        CanvasDocument document = Document(20, 20, layer);
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(5, 5, 4, 4));

        (CanvasDocument next, Guid copy) = PixelCommands.LayerVia(document, layer.Id, selection, cut: true)!.Value;
        ImageLayer added = next.Layer(copy)!, source = next.Layer(layer.Id)!;

        try
        {
            Assert.Equal(1, next.IndexOf(copy));
            Assert.Equal(new Point(5, 5), added.Transform.Origin);
            Assert.Equal(4, added.Image!.Width);
            Assert.Equal((0, 0, 0, 0), At(source.Image!, 1, 1));
            Assert.Equal((50, 60, 70, 255), At(source.Image!, 8, 8));
        }
        finally
        {
            added.Image!.Release();
            source.Image!.Release();
        }
    }

    [Fact]
    public void AMaskFromTheSelectionRevealsOnlyIt()
    {
        using PixelBuffer pixels = Solid(10, 10, 50, 60, 70);
        ImageLayer layer = Layer("p", pixels);
        CanvasDocument document = Document(10, 10, layer);

        ImageLayer masked = PixelCommands.AddMask(document, layer, DocumentSelection.Rectangle(new Rect(0, 0, 5, 10)))!;

        using PixelBuffer shown = LayerCompositor.Render(Document(10, 10, masked), new SoftwareRenderBackend());
        Assert.Equal(255, At(shown, 2, 5).A);
        Assert.Equal(0, At(shown, 7, 5).A);
        masked.Mask!.Coverage.Release();
    }

    [Fact]
    public void AMaskRevealingAllHidesNothing()
    {
        using PixelBuffer pixels = Solid(10, 10, 50, 60, 70);
        ImageLayer layer = Layer("p", pixels);

        ImageLayer masked = PixelCommands.AddMask(Document(10, 10, layer), layer, null)!;

        using PixelBuffer shown = LayerCompositor.Render(Document(10, 10, masked), new SoftwareRenderBackend());
        Assert.Equal((50, 60, 70, 255), At(shown, 5, 5));
        masked.Mask!.Coverage.Release();
    }
}
