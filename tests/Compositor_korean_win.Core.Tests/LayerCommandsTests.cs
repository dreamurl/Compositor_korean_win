using Compositor_korean_win.Core;
using Xunit;
using static Compositor_korean_win.Core.Tests.RenderFixture;

namespace Compositor_korean_win.Core.Tests;

/// <summary>The Layer menu's edits to the tree, each checked against upstream's rule.</summary>
public class LayerCommandsTests
{
    private static ImageLayer Blank(string name, Guid? parent = null, bool group = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Transform = new LayerTransform(Point.Zero, new Size(8, 8)),
        ParentId = parent,
        IsGroup = group,
    };

    private static string[] Names(CanvasDocument document) => [.. document.Layers.Select(layer => layer.Name)];

    [Fact]
    public void ANewLayerIsNamedTheFirstFreeNumberAndGoesAboveTheActiveOne()
    {
        Language before = Localizer.Current;
        Localizer.Current = Language.English;
        try
        {
            ImageLayer a = Blank("Layer 1"), b = Blank("b");
            CanvasDocument document = Document(8, 8, a, b);

            (CanvasDocument next, Guid added) = LayerCommands.AddBlankLayer(document, a.Id);

            Assert.Equal(["Layer 1", "Layer 2", "b"], Names(next));
            Assert.Equal(added, next.Layers[1].Id);
        }
        finally
        {
            Localizer.Current = before;
        }
    }

    [Fact]
    public void ANewLayerWithAFolderActiveGoesToTheTopOfTheFolder()
    {
        ImageLayer folder = Blank("folder", group: true);
        ImageLayer inside = Blank("inside", folder.Id);
        ImageLayer above = Blank("above");
        CanvasDocument document = Document(8, 8, folder, inside, above);

        (CanvasDocument next, Guid added) = LayerCommands.AddBlankLayer(document, folder.Id);

        Assert.Equal(folder.Id, next.Layer(added)!.ParentId);
        Assert.Equal(2, next.IndexOf(added));
    }

    [Fact]
    public void DeletingAFolderTakesItsContentsAndReleasesWhatClippedToThem()
    {
        ImageLayer folder = Blank("folder", group: true);
        ImageLayer inside = Blank("inside", folder.Id);
        ImageLayer outside = Blank("outside");
        ImageLayer clipped = Blank("clipped") with { MaskSourceId = outside.Id };
        CanvasDocument document = Document(8, 8, folder, inside, outside, clipped);

        CanvasDocument next = LayerCommands.Delete(document, [folder.Id])!;
        Assert.Equal(["outside", "clipped"], Names(next));
        Assert.Equal(outside.Id, next.Layer(clipped.Id)!.MaskSourceId);

        CanvasDocument without = LayerCommands.Delete(next, [outside.Id])!;
        Assert.Null(without.Layer(clipped.Id)!.MaskSourceId);
        Assert.Equal(clipped.Id, LayerCommands.Survivor(next, without, outside.Id));
    }

    [Fact]
    public void MovingSwapsWithTheNextSiblingAndStopsAtTheEnds()
    {
        ImageLayer a = Blank("a"), folder = Blank("folder", group: true), inside = Blank("inside", folder.Id), b = Blank("b");
        CanvasDocument document = Document(8, 8, a, folder, inside, b);

        // a's siblings are a, folder and b: inside is not one of them.
        CanvasDocument up = LayerCommands.Move(document, a.Id, 1)!;
        Assert.Equal(["folder", "a", "inside", "b"], Names(up));

        Assert.False(LayerCommands.CanMove(document, a.Id, -1));
        Assert.False(LayerCommands.CanMove(document, inside.Id, 1));
    }

    [Fact]
    public void GroupingWrapsTheChosenLayersInANewFolderWhereTheTopmostWas()
    {
        ImageLayer a = Blank("a"), b = Blank("b"), c = Blank("c");
        CanvasDocument document = Document(8, 8, a, b, c);

        (CanvasDocument next, Guid folder) = LayerCommands.Group(document, [a.Id, b.Id])!.Value;

        Assert.True(next.Layer(folder)!.IsGroup);
        Assert.Equal(folder, next.Layer(a.Id)!.ParentId);
        Assert.Equal(folder, next.Layer(b.Id)!.ParentId);
        Assert.Null(next.Layer(c.Id)!.ParentId);

        // Among the root layers the folder sits where b was, below c.
        Guid[] roots = [.. next.Layers.Where(layer => layer.ParentId is null).Select(layer => layer.Id)];
        Assert.Equal([folder, c.Id], roots);

        // Out again: the layer lands just above its old folder.
        CanvasDocument outside = LayerCommands.MoveOutOfFolder(next, b.Id)!;
        Assert.Null(outside.Layer(b.Id)!.ParentId);
        Assert.Equal(outside.IndexOf(folder) + 1, outside.IndexOf(b.Id));
    }

    [Fact]
    public void ClippingSharesTheBaseBelowAndReleasingTakesTheRunAboveWithIt()
    {
        ImageLayer basis = Blank("base"), first = Blank("first"), second = Blank("second");
        CanvasDocument document = Document(8, 8, basis, first, second);

        document = LayerCommands.ToggleClipping(document, first.Id)!;
        document = LayerCommands.ToggleClipping(document, second.Id)!;
        Assert.Equal(basis.Id, document.Layer(first.Id)!.MaskSourceId);
        Assert.Equal(basis.Id, document.Layer(second.Id)!.MaskSourceId);

        document = LayerCommands.ToggleClipping(document, first.Id)!;
        Assert.Null(document.Layer(first.Id)!.MaskSourceId);
        Assert.Null(document.Layer(second.Id)!.MaskSourceId);

        // Nothing below the bottom layer to clip to.
        Assert.Null(LayerCommands.ToggleClipping(document, basis.Id));
    }

    [Fact]
    public void FlippingALayerMirrorsItAboutItsOwnMiddle()
    {
        ImageLayer layer = Blank("a") with
        {
            Transform = new LayerTransform(new Point(10, 20), new Size(30, 10)) { Rotation = 15 },
        };
        CanvasDocument document = Document(100, 100, layer);

        LayerTransform flipped = LayerCommands.Flip(document, [layer.Id], horizontally: true)!.Layer(layer.Id)!.Transform;

        Assert.True(flipped.FlipX);
        Assert.Equal(-15, flipped.Rotation);
        Assert.Equal(layer.Transform.Center.X, flipped.Center.X, precision: 9);
        Assert.Equal(layer.Transform.Center.Y, flipped.Center.Y, precision: 9);
    }

    [Fact]
    public void FlippingTheCanvasSendsALayerToTheOtherSide()
    {
        ImageLayer layer = Blank("a") with { Transform = new LayerTransform(new Point(10, 0), new Size(20, 10)) };
        CanvasDocument document = Document(100, 50, layer);

        LayerTransform flipped = LayerCommands.FlipCanvas(document, horizontally: true).Layer(layer.Id)!.Transform;

        Assert.Equal(new Point(70, 0), flipped.Origin);
        Assert.True(flipped.FlipX);
    }

    [Fact]
    public void MergeDownCompositesTheTwoIntoOneTrimmedLayer()
    {
        using PixelBuffer red = Solid(4, 4, 255, 0, 0);
        using PixelBuffer blue = Solid(4, 4, 0, 0, 255, 128);
        ImageLayer bottom = Layer("bottom", red, 2, 2);
        ImageLayer top = Layer("top", blue, 4, 4);
        CanvasDocument document = Document(16, 16, bottom, top);

        LayerCommands.MergePlan plan = LayerCommands.PlanMerge(document, [top.Id], top.Id)!;
        Assert.Equal(TextKey.CommandMergeDown, plan.Action);

        (CanvasDocument next, Guid merged) = LayerCommands.Merge(document, plan)!.Value;
        ImageLayer result = next.Layer(merged)!;

        try
        {
            Assert.Single(next.Layers);
            Assert.Equal("bottom", result.Name);

            // Trimmed to the two squares' union: 2..8 on both axes.
            Assert.Equal(new Point(2, 2), result.Transform.Origin);
            Assert.Equal(6, result.Image!.Width);

            // Where they overlap, half-transparent blue over red.
            (int r, _, int b, int a) = At(result.Image, 3, 3);
            Assert.Equal(255, a);
            Assert.InRange(r, 125, 130);
            Assert.InRange(b, 125, 130);
        }
        finally
        {
            result.Image!.Release();
        }
    }

    [Fact]
    public void NothingBelowMeansNothingToMergeDown()
    {
        ImageLayer only = Blank("only");
        Assert.Null(LayerCommands.PlanMerge(Document(8, 8, only), [only.Id], only.Id));
    }

    [Fact]
    public void ARenameToNothingIsRefused()
    {
        ImageLayer layer = Blank("a");
        CanvasDocument document = Document(8, 8, layer);

        Assert.Null(LayerCommands.Rename(document, layer.Id, "   "));
        Assert.Equal("b", LayerCommands.Rename(document, layer.Id, " b ")!.Layer(layer.Id)!.Name);
    }

    [Fact]
    public void AHistoryStepNamedAfterAMenuItemReadsAsPlainWords()
    {
        Assert.Equal("New Layer", Localizer.Plain("&New Layer"));
        Assert.Equal("Open", Localizer.Plain("&Open…"));
        Assert.Equal("Save As", Localizer.Plain("Save &As…"));
        Assert.Equal("X Y", Localizer.Plain("X Y(&N)"));
    }
}
