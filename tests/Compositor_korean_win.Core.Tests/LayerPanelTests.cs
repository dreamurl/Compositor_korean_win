using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>Dropping rows in the Layers panel, and the hide-all mask.</summary>
public class LayerPanelTests
{
    private static ImageLayer Layer(string name, Guid? parent = null, bool folder = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Transform = new LayerTransform(Point.Zero, new Size(10, 10)),
        ParentId = parent,
        IsGroup = folder,
    };

    private static string[] Order(CanvasDocument document, Guid? parent = null) =>
        [.. document.Layers.Where(layer => layer.ParentId == parent).Select(layer => layer.Name)];

    [Fact]
    public void ARowDroppedAboveAnotherGoesJustAboveIt()
    {
        ImageLayer a = Layer("a"), b = Layer("b"), c = Layer("c");
        CanvasDocument document = ProjectFixture.Document(a, b, c);

        CanvasDocument moved = LayerCommands.Place(document, [a.Id], c.Id, LayerDrop.Above)!;
        Assert.Equal(["b", "c", "a"], Order(moved));

        CanvasDocument below = LayerCommands.Place(document, [c.Id], a.Id, LayerDrop.Below)!;
        Assert.Equal(["c", "a", "b"], Order(below));
    }

    [Fact]
    public void ARowDroppedIntoAFolderGoesToItsTop()
    {
        ImageLayer folder = Layer("folder", folder: true);
        ImageLayer inside = Layer("inside", folder.Id), loose = Layer("loose");
        CanvasDocument document = ProjectFixture.Document(folder, inside, loose);

        CanvasDocument moved = LayerCommands.Place(document, [loose.Id], folder.Id, LayerDrop.Into)!;

        Assert.Equal(["inside", "loose"], Order(moved, folder.Id));
        Assert.Equal(["folder"], Order(moved));
    }

    [Fact]
    public void AFolderCannotGoInsideItself()
    {
        ImageLayer folder = Layer("folder", folder: true);
        ImageLayer inner = Layer("inner", folder.Id, folder: true);
        CanvasDocument document = ProjectFixture.Document(folder, inner);

        Assert.Null(LayerCommands.Place(document, [folder.Id], inner.Id, LayerDrop.Into));
        Assert.Null(LayerCommands.Place(document, [folder.Id], folder.Id, LayerDrop.Above));
    }

    [Fact]
    public void AFolderCarriesWhatItHolds()
    {
        ImageLayer folder = Layer("folder", folder: true);
        ImageLayer inside = Layer("inside", folder.Id), top = Layer("top");
        CanvasDocument document = ProjectFixture.Document(folder, inside, top);

        CanvasDocument moved = LayerCommands.Place(document, [folder.Id, inside.Id], top.Id, LayerDrop.Above)!;

        Assert.Equal(["top", "folder"], Order(moved));
        Assert.Equal(folder.Id, moved.Layer(inside.Id)!.ParentId);
    }

    [Fact]
    public void AltDraggingCopiesTheLayersToTheDrop()
    {
        ImageLayer a = Layer("a"), b = Layer("b");
        CanvasDocument document = ProjectFixture.Document(a, b);

        (CanvasDocument copied, IReadOnlyList<Guid> copies) = LayerCommands.CopyTo(document, [a.Id], b.Id, LayerDrop.Above)!.Value;

        Assert.Single(copies);
        Assert.Equal(3, copied.Layers.Count);
        Assert.Equal(copies[0], copied.Layers[^1].Id);
    }

    [Fact]
    public void CopyingAcrossProjectsCarriesAFolderAndRemapsItsReferences()
    {
        using PixelBuffer image = RenderFixture.Solid(10, 10, 20, 40, 60);
        ImageLayer folder = Layer("folder", folder: true);
        ImageLayer baseLayer = RenderFixture.Layer("base", image) with { ParentId = folder.Id };
        ImageLayer clipped = Layer("clipped", folder.Id) with { MaskSourceId = baseLayer.Id };
        CanvasDocument source = ProjectFixture.Document(folder, baseLayer, clipped);
        CanvasDocument destination = ProjectFixture.Document(Layer("existing"));

        (CanvasDocument copied, IReadOnlyList<Guid> roots) = LayerCommands.CopyAcross(
            source, [folder.Id], destination)!.Value;

        Assert.Single(roots);
        ImageLayer copiedFolder = copied.Layer(roots[0])!;
        ImageLayer copiedBase = copied.Layers.Single(layer => layer.Name == "base");
        ImageLayer copiedClipped = copied.Layers.Single(layer => layer.Name == "clipped");
        Assert.True(copiedFolder.IsGroup);
        Assert.Equal(copiedFolder.Id, copiedBase.ParentId);
        Assert.Equal(copiedFolder.Id, copiedClipped.ParentId);
        Assert.Equal(copiedBase.Id, copiedClipped.MaskSourceId);
        Assert.NotEqual(baseLayer.Id, copiedBase.Id);

        copiedBase.Image!.Release();
    }

    [Fact]
    public void AHideAllMaskHidesTheSelectionOrEverything()
    {
        using PixelBuffer image = RenderFixture.Solid(20, 20, 1, 2, 3);
        ImageLayer layer = RenderFixture.Layer("a", image);
        CanvasDocument document = RenderFixture.Document(20, 20, layer);

        ImageLayer all = PixelCommands.AddMask(document, layer, null, revealing: false)!;
        Assert.Equal(0, RenderFixture.At(all.Mask!.Coverage, 0, 0).R);

        ImageLayer some = PixelCommands.AddMask(document, layer, DocumentSelection.Rectangle(new Rect(0, 0, 10, 20)), revealing: false)!;
        Assert.Equal(0, RenderFixture.At(some.Mask!.Coverage, 5, 5).R);
        Assert.Equal(255, RenderFixture.At(some.Mask.Coverage, 15, 5).R);

        all.Mask.Coverage.Release();
        some.Mask.Coverage.Release();
    }
}
