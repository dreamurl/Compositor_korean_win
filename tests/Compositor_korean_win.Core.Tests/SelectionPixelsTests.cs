using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>Moving what is inside a selection.</summary>
public class SelectionPixelsTests
{
    private static PixelBuffer Marked()
    {
        // A red square in the top-left corner of a transparent layer.
        PixelBuffer buffer = PixelBuffer.Allocate(64, 64);
        for (int y = 8; y < 24; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 8; x < 24; x++)
            {
                row[x * 4 + 0] = 200;
                row[x * 4 + 3] = 255;
            }
        }
        return buffer;
    }

    [Fact]
    public void ThePixelsGoWhereTheyWereDraggedAndLeaveNothingBehind()
    {
        using PixelBuffer layer = Marked();
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(8, 8, 16, 16));

        using PixelBuffer moved = SelectionPixels.Move(layer, selection, new Point(20, 10));

        Assert.Equal((200, 0, 0, 255), RenderFixture.At(moved, 36, 20));   // Where it went.
        Assert.Equal(0, RenderFixture.At(moved, 16, 16).A);                // Where it was.
    }

    [Fact]
    public void DuplicatingLeavesTheOriginalWhereItWas()
    {
        using PixelBuffer layer = Marked();
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(8, 8, 16, 16));

        using PixelBuffer moved = SelectionPixels.Move(layer, selection, new Point(20, 10), duplicate: true);

        Assert.Equal((200, 0, 0, 255), RenderFixture.At(moved, 36, 20));
        Assert.Equal((200, 0, 0, 255), RenderFixture.At(moved, 16, 16));
    }

    [Fact]
    public void WhatIsNotSelectedIsNotMoved()
    {
        using PixelBuffer layer = Marked();

        // Only the left half of the square.
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(8, 8, 8, 16));
        using PixelBuffer moved = SelectionPixels.Move(layer, selection, new Point(0, 30));

        Assert.Equal(0, RenderFixture.At(moved, 10, 16).A);                // Lifted.
        Assert.Equal((200, 0, 0, 255), RenderFixture.At(moved, 20, 16));   // Left alone.
        Assert.Equal((200, 0, 0, 255), RenderFixture.At(moved, 10, 46));   // Landed.
    }

    [Fact]
    public void AFeatheredEdgeArrivesFeathered()
    {
        using PixelBuffer layer = Marked();

        // An ellipse, whose edge is antialiased and so partly selected all the way round.
        DocumentSelection selection = DocumentSelection.Ellipse(new Rect(8, 8, 16, 16));
        using PixelBuffer moved = SelectionPixels.Move(layer, selection, new Point(30, 0));

        // Solid where it landed, and a hole where it came from.
        Assert.Equal(255, RenderFixture.At(moved, 46, 16).A);
        Assert.Equal(0, RenderFixture.At(moved, 16, 16).A);

        // Both edges are part-way, which is what a feathered selection is for: a cut one would
        // have nothing between full and nothing anywhere along the rim.
        Assert.True(Soft(moved, 36, 6) >= 8, "the edge it landed on is not soft");
        Assert.True(Soft(moved, 6, 6) >= 8, "the hole it left is not soft");

        static int Soft(PixelBuffer buffer, int left, int top)
        {
            int count = 0;
            for (int y = top; y < top + 20; y++)
            {
                for (int x = left; x < left + 20; x++)
                {
                    int alpha = RenderFixture.At(buffer, x, y).A;
                    if (alpha is > 0 and < 255) count++;
                }
            }
            return count;
        }
    }

    [Fact]
    public void DraggingOffTheEdgeKeepsWhatStillFits()
    {
        using PixelBuffer layer = Marked();
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(8, 8, 16, 16));

        using PixelBuffer moved = SelectionPixels.Move(layer, selection, new Point(52, 0));

        // Half of it is past the right edge; the half that fits is there.
        Assert.Equal((200, 0, 0, 255), RenderFixture.At(moved, 62, 16));
        Assert.Equal(0, RenderFixture.At(moved, 16, 16).A);
    }

    [Fact]
    public void AnEmptySelectionMovesNothing()
    {
        using PixelBuffer layer = Marked();
        using PixelBuffer moved = SelectionPixels.Move(layer, DocumentSelection.Empty, new Point(10, 10));

        Assert.Equal((200, 0, 0, 255), RenderFixture.At(moved, 16, 16));
    }
}
