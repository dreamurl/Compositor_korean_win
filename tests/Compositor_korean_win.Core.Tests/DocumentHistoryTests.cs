using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// Undo and redo, and the sharing that keeps a hundred steps affordable.
/// </summary>
public class DocumentHistoryTests
{
    private static CanvasDocument Document(params ImageLayer[] layers) => ProjectFixture.Document(layers);

    private static CanvasDocument Renamed(CanvasDocument document, string name) =>
        document.Replacing(document.Layers[0] with { Name = name });

    [Fact]
    public void AFreshHistoryHasNothingToUndo()
    {
        var history = new DocumentHistory();

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.False(history.IsModified);
        Assert.Equal(string.Empty, history.UndoName);
    }

    [Fact]
    public void AnEditCanBeUndoneAndRedone()
    {
        var history = new DocumentHistory();
        CanvasDocument before = Document(ProjectFixture.Layer("Layer 1"));
        CanvasDocument after = Renamed(before, "Renamed");

        history.Begin("Rename Layer", before, null);
        history.End(after, null);

        Assert.True(history.CanUndo);
        Assert.True(history.IsModified);
        Assert.Equal("Rename Layer", history.UndoName);

        HistorySnapshot? undone = history.Undo();
        Assert.Equal(before, undone!.Document);
        Assert.True(history.CanRedo);

        HistorySnapshot? redone = history.Redo();
        Assert.Equal(after, redone!.Document);
    }

    [Fact]
    public void AnEditThatChangedNothingIsNotRecorded()
    {
        // Selecting and navigating run through the same begin/end pair as a real edit. If they
        // recorded a step, clicking around after an undo would throw away what could be redone.
        var history = new DocumentHistory();
        CanvasDocument document = Document(ProjectFixture.Layer("Layer 1"));

        history.Begin("Edit", document, null);
        history.End(Renamed(document, "Renamed"), null);
        history.Undo();
        Assert.True(history.CanRedo);

        history.Begin("Select", document, null);
        history.End(document, null);

        Assert.True(history.CanRedo);
        Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public void ANewEditClearsTheRedoStack()
    {
        var history = new DocumentHistory();
        CanvasDocument document = Document(ProjectFixture.Layer("Layer 1"));

        history.Begin("First", document, null);
        history.End(Renamed(document, "First"), null);
        history.Undo();
        Assert.True(history.CanRedo);

        history.Begin("Second", document, null);
        history.End(Renamed(document, "Second"), null);

        Assert.False(history.CanRedo);
    }

    [Fact]
    public void NestedEditsLandAsOneStep()
    {
        var history = new DocumentHistory();
        CanvasDocument document = Document(ProjectFixture.Layer("Layer 1"));

        history.Begin("Outer", document, null);
        history.Begin("Inner", Renamed(document, "halfway"), null);
        history.End(Renamed(document, "halfway"), null);
        history.End(Renamed(document, "done"), null);

        Assert.Equal(1, history.UndoCount);
        Assert.Equal("Outer", history.UndoName);
        Assert.Equal(document, history.Undo()!.Document);
    }

    [Fact]
    public void UndoIsRefusedWhileAnEditIsOpen()
    {
        var history = new DocumentHistory();
        CanvasDocument document = Document(ProjectFixture.Layer("Layer 1"));

        history.Begin("First", document, null);
        history.End(Renamed(document, "First"), null);

        history.Begin("Second", document, null);
        Assert.False(history.CanUndo);
        Assert.Null(history.Undo());

        history.End(Renamed(document, "Second"), null);
        Assert.True(history.CanUndo);
    }

    [Fact]
    public void SavingClearsTheModifiedFlagAndEditingSetsItAgain()
    {
        var history = new DocumentHistory();
        CanvasDocument document = Document(ProjectFixture.Layer("Layer 1"));

        history.Begin("Edit", document, null);
        history.End(Renamed(document, "Renamed"), null);
        Assert.True(history.IsModified);

        history.MarkSaved();
        Assert.False(history.IsModified);

        // Undoing back past the save point counts as modified again, because the file on disk no
        // longer matches what is open.
        history.Undo();
        Assert.True(history.IsModified);

        history.Redo();
        Assert.False(history.IsModified);
    }

    [Fact]
    public void ResetDropsEverything()
    {
        var history = new DocumentHistory();
        CanvasDocument document = Document(ProjectFixture.Layer("Layer 1"));

        history.Begin("Edit", document, null);
        history.End(Renamed(document, "Renamed"), null);
        history.Reset();

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.False(history.IsModified);
    }

    [Fact]
    public void TheOldestStepsFallOffAtTheEntryLimit()
    {
        var history = new DocumentHistory(entryLimit: 5);
        CanvasDocument document = Document(ProjectFixture.Layer("Layer 1"));

        for (int i = 0; i < 20; i++)
        {
            history.Begin($"Edit {i}", document, null);
            history.End(Renamed(document, $"Name {i}"), null);
        }

        Assert.Equal(5, history.UndoCount);
        Assert.Equal("Edit 19", history.UndoName);
    }

    [Fact]
    public void SnapshotsShareTheirPixelsInsteadOfCopyingThem()
    {
        // This is the property docs/windows-port.md §2.2 calls the biggest lever on memory use.
        // A hundred steps that all hold the same image must cost one image.
        var history = new DocumentHistory();
        PixelBuffer pixels = PixelBuffer.Allocate(256, 256);
        CanvasDocument document = Document(ProjectFixture.Layer("Layer 1", pixels));

        for (int i = 0; i < 100; i++)
        {
            history.Begin($"Rename {i}", document, null);
            document = Renamed(document, $"Name {i}");
            history.End(document, null);
        }

        Assert.Equal(100, history.UndoCount);

        // The live document still holds those pixels, so history is keeping nothing of its own.
        Assert.Equal(0, history.RetainedBytes(document));

        // Once the document no longer references them, they count once however many steps hold them.
        CanvasDocument empty = document with { Layers = EquatableList<ImageLayer>.Empty };
        Assert.Equal(pixels.ByteCount, history.RetainedBytes(empty));

        pixels.Release();
    }

    [Fact]
    public void StepsFallOffWhenHistoryHoldsTooManyBytes()
    {
        // A budget of two images: each edit replaces the layer's pixels, so history's hold grows
        // until the oldest steps are dropped.
        PixelBuffer[] frames = [.. Enumerable.Range(0, 6).Select(_ => PixelBuffer.Allocate(64, 64))];
        long budget = frames[0].ByteCount * 2;

        var history = new DocumentHistory(entryLimit: 100, retainedByteLimit: budget);
        CanvasDocument document = Document(ProjectFixture.Layer("Layer 1", frames[0]));

        foreach (PixelBuffer frame in frames.Skip(1))
        {
            history.Begin("Paint", document, null);
            document = document.Replacing(document.Layers[0] with { Image = frame });
            history.End(document, null);
        }

        Assert.True(history.RetainedBytes(document) <= budget,
            $"history holds {history.RetainedBytes(document)} bytes, budget was {budget}");
        Assert.True(history.UndoCount < frames.Length - 1, "no steps were dropped");

        foreach (PixelBuffer frame in frames) frame.Release();
    }

    [Fact]
    public void AnOwningHistoryFreesWhatItDropsAndNothingElse()
    {
        int before = PixelBuffer.LiveCount;
        var history = new DocumentHistory(entryLimit: 2, ownsPixels: true);

        PixelBuffer first = PixelBuffer.Allocate(4, 4);
        ImageLayer layer = RenderFixture.Layer("paint", first);
        CanvasDocument document = RenderFixture.Document(4, 4, layer);

        // Four strokes, each a new buffer. With room for two steps, the oldest two fall off, and
        // with them the first two buffers — nothing kept can reach those any more.
        for (int stroke = 0; stroke < 4; stroke++)
        {
            history.Begin("stroke", document, layer.Id);
            layer = layer with { Image = PixelBuffer.Allocate(4, 4) };
            document = document.Replacing(layer);
            history.End(document, layer.Id);
        }

        Assert.Equal(before + 3, PixelBuffer.LiveCount);

        // Undoing drops nothing: the step undone can still be redone.
        history.Undo();
        Assert.Equal(before + 3, PixelBuffer.LiveCount);

        history.Clear(document);
        Assert.Equal(before, PixelBuffer.LiveCount);
    }
}
