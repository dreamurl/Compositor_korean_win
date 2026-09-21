using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// Folders have to form a tree, and clipping-mask links a chain, before anything renders them.
/// </summary>
/// <remarks>
/// Both graphs are walked by the renderer, so a cycle here is not a wrong picture — it is a hang.
/// Upstream rejects these at the door and so does this, which is why the checks live in the format
/// layer rather than in the renderer that would trip over them.
/// </remarks>
public class HierarchyTests
{
    private static ProjectLayerRecord Folder(Guid id, Guid? parent = null) =>
        ProjectFixture.Record("Folder", id) with { IsGroup = true, ParentId = parent };

    private static ProjectLayerRecord Leaf(Guid id, Guid? parent = null) =>
        ProjectFixture.Record("Layer", id) with { ParentId = parent };

    [Fact]
    public void AFolderWithChildrenIsAccepted()
    {
        Guid folder = Guid.NewGuid();
        LayerHierarchy.Validate([Folder(folder), Leaf(Guid.NewGuid(), folder), Leaf(Guid.NewGuid(), folder)]);
    }

    [Fact]
    public void AParentThatIsNotAFolderIsRejected()
    {
        Guid leaf = Guid.NewGuid();
        Assert.Throws<ProjectException>(() =>
            LayerHierarchy.Validate([Leaf(leaf), Leaf(Guid.NewGuid(), leaf)]));
    }

    [Fact]
    public void AMissingParentIsRejected() =>
        Assert.Throws<ProjectException>(() =>
            LayerHierarchy.Validate([Leaf(Guid.NewGuid(), Guid.NewGuid())]));

    [Fact]
    public void AFolderInsideItselfIsRejected()
    {
        Guid id = Guid.NewGuid();
        Assert.Throws<ProjectException>(() => LayerHierarchy.Validate([Folder(id, id)]));
    }

    [Fact]
    public void ACycleOfFoldersIsRejected()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        Assert.Throws<ProjectException>(() =>
            LayerHierarchy.Validate([Folder(first, second), Folder(second, first)]));
    }

    [Fact]
    public void AFolderCarryingAnImageIsRejected()
    {
        Guid id = Guid.NewGuid();
        Assert.Throws<ProjectException>(() => LayerHierarchy.Validate(
            [Folder(id) with { ImageFile = ManifestValidator.ImageFileName(id) }]));
    }

    [Fact]
    public void ADuplicateIdIsRejected()
    {
        Guid id = Guid.NewGuid();
        Assert.Throws<ProjectException>(() => LayerHierarchy.Validate([Leaf(id), Leaf(id)]));
    }

    [Fact]
    public void NestingToTheLimitIsAcceptedAndOneDeeperIsNot()
    {
        // Sixty-four ancestor levels, with room for a leaf at the deepest one.
        Assert.Null(Record.Exception(() => LayerHierarchy.Validate(Chain(ProjectLimits.MaximumNesting))));
        Assert.Throws<ProjectException>(() => LayerHierarchy.Validate(Chain(ProjectLimits.MaximumNesting + 1)));

        static List<ProjectLayerRecord> Chain(int depth)
        {
            var layers = new List<ProjectLayerRecord>();
            Guid? parent = null;
            for (int i = 0; i < depth; i++)
            {
                Guid id = Guid.NewGuid();
                layers.Add(Folder(id, parent));
                parent = id;
            }
            layers.Add(Leaf(Guid.NewGuid(), parent));
            return layers;
        }
    }
}

/// <summary>Clipping masks: one layer lending its alpha to another.</summary>
public class ClippingMaskTests
{
    private static ProjectLayerRecord Layer(Guid id, Guid? source = null) =>
        ProjectFixture.Record("Layer", id) with { MaskSourceId = source };

    [Fact]
    public void ALayerClippedToAnotherIsAccepted()
    {
        Guid baseLayer = Guid.NewGuid();
        LiveMaskGraph.Validate([Layer(baseLayer), Layer(Guid.NewGuid(), baseLayer)]);
    }

    [Fact]
    public void SeveralLayersSharingOneBaseAreAccepted()
    {
        Guid baseLayer = Guid.NewGuid();
        LiveMaskGraph.Validate(
        [
            Layer(baseLayer),
            Layer(Guid.NewGuid(), baseLayer),
            Layer(Guid.NewGuid(), baseLayer),
        ]);
    }

    [Fact]
    public void ALayerClippedToItselfIsRejected()
    {
        Guid id = Guid.NewGuid();
        Assert.Throws<ProjectException>(() => LiveMaskGraph.Validate([Layer(id, id)]));
    }

    [Fact]
    public void ACycleIsRejected()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        Assert.Throws<ProjectException>(() =>
            LiveMaskGraph.Validate([Layer(first, second), Layer(second, first)]));
    }

    [Fact]
    public void AMissingBaseIsRejected() =>
        Assert.Throws<ProjectException>(() =>
            LiveMaskGraph.Validate([Layer(Guid.NewGuid(), Guid.NewGuid())]));

    [Fact]
    public void AFolderCannotBeABase()
    {
        Guid folder = Guid.NewGuid();
        Assert.Throws<ProjectException>(() => LiveMaskGraph.Validate(
        [
            ProjectFixture.Record("Folder", folder) with { IsGroup = true },
            Layer(Guid.NewGuid(), folder),
        ]));
    }

    [Fact]
    public void AFolderCannotBeClipped()
    {
        Guid baseLayer = Guid.NewGuid(), folder = Guid.NewGuid();
        Assert.Throws<ProjectException>(() => LiveMaskGraph.Validate(
        [
            Layer(baseLayer),
            ProjectFixture.Record("Folder", folder) with { IsGroup = true, MaskSourceId = baseLayer },
        ]));
    }

    [Fact]
    public void AnAdjustmentCannotBeABase()
    {
        Guid adjustment = Guid.NewGuid();
        Assert.Throws<ProjectException>(() => LiveMaskGraph.Validate(
        [
            ProjectFixture.Record("Levels", adjustment) with
            {
                Adjustment = new LayerAdjustment(AdjustmentKind.Levels),
            },
            Layer(Guid.NewGuid(), adjustment),
        ]));
    }

    [Fact]
    public void AChainToTheLimitIsAcceptedAndOneLongerIsNot()
    {
        Assert.Null(Record.Exception(() => LiveMaskGraph.Validate(Chain(ProjectLimits.MaximumMaskChain))));
        Assert.Throws<ProjectException>(() => LiveMaskGraph.Validate(Chain(ProjectLimits.MaximumMaskChain + 1)));

        static List<ProjectLayerRecord> Chain(int length)
        {
            var layers = new List<ProjectLayerRecord>();
            Guid? previous = null;
            for (int i = 0; i < length; i++)
            {
                Guid id = Guid.NewGuid();
                layers.Add(Layer(id, previous));
                previous = id;
            }
            return layers;
        }
    }
}
