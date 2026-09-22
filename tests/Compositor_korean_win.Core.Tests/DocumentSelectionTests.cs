using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// What is selected, and the coverage an edit would be held to.
/// </summary>
public class DocumentSelectionTests
{
    private static byte Level(byte[] levels, PixelRect region, int x, int y) =>
        levels[(y - region.Y) * region.Width + (x - region.X)];

    private static byte[] Over(DocumentSelection selection, int width, int height) =>
        selection.Levels(new PixelRect(0, 0, width, height));

    private static double Area(byte[] levels)
    {
        double total = 0;
        foreach (byte level in levels) total += level / 255.0;
        return total;
    }

    [Fact]
    public void ARectangleSelectsExactlyThePixelsItCovers()
    {
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(2, 3, 4, 5));
        byte[] levels = Over(selection, 10, 10);
        var region = new PixelRect(0, 0, 10, 10);

        Assert.Equal(255, Level(levels, region, 2, 3));
        Assert.Equal(255, Level(levels, region, 5, 7));
        Assert.Equal(0, Level(levels, region, 1, 3));
        Assert.Equal(0, Level(levels, region, 6, 3));
        Assert.Equal(0, Level(levels, region, 2, 8));
        Assert.Equal(20, Area(levels), precision: 6);
    }

    [Fact]
    public void AnEllipseCoversTheAreaOfOne()
    {
        DocumentSelection selection = DocumentSelection.Ellipse(new Rect(0, 0, 40, 40));

        double expected = Math.PI * 20 * 20;
        double measured = Area(Over(selection, 40, 40));

        Assert.InRange(measured, expected * 0.97, expected * 1.03);
    }

    [Fact]
    public void TakingSomethingAwayLeavesAHole()
    {
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(0, 0, 10, 10))
            .Subtracting(DocumentSelection.Rectangle(new Rect(2, 2, 3, 3)));

        byte[] levels = Over(selection, 10, 10);
        var region = new PixelRect(0, 0, 10, 10);

        Assert.Equal(0, Level(levels, region, 3, 3));
        Assert.Equal(255, Level(levels, region, 1, 1));
        Assert.Equal(255, Level(levels, region, 6, 6));
        Assert.Equal(100 - 9, Area(levels), precision: 6);
    }

    /// <remarks>
    /// The reason a selection keeps its steps rather than folding them into one outline. Reversing
    /// an outline into a single path cancels it where the two overlap and <em>selects</em> it where
    /// they do not, so a subtraction dragged clear of the selection would select what it was asked
    /// to remove.
    /// </remarks>
    [Fact]
    public void TakingAwaySomethingThatDoesNotOverlapChangesNothing()
    {
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(0, 0, 4, 4))
            .Subtracting(DocumentSelection.Rectangle(new Rect(6, 6, 3, 3)));

        byte[] levels = Over(selection, 10, 10);
        var region = new PixelRect(0, 0, 10, 10);

        Assert.Equal(255, Level(levels, region, 1, 1));
        Assert.Equal(0, Level(levels, region, 7, 7));
        Assert.Equal(16, Area(levels), precision: 6);
    }

    [Fact]
    public void AddingJoinsTwoShapes()
    {
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(0, 0, 4, 4))
            .Adding(DocumentSelection.Rectangle(new Rect(6, 0, 4, 4)));

        Assert.Equal(32, Area(Over(selection, 10, 10)), precision: 6);
    }

    [Fact]
    public void ASoftEdgeIsPartlySelected()
    {
        // 2.5 across to 5.5: half of the pixel at each end, all of the two between.
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(2.5, 0, 3, 4));
        byte[] levels = Over(selection, 10, 10);
        var region = new PixelRect(0, 0, 10, 10);

        Assert.Equal(128, Level(levels, region, 2, 1));
        Assert.Equal(255, Level(levels, region, 3, 1));
        Assert.Equal(255, Level(levels, region, 4, 1));
        Assert.Equal(128, Level(levels, region, 5, 1));
    }

    [Fact]
    public void AHardEdgeIsAllOrNothing()
    {
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(2.5, 0, 3, 4))
            with { IsAntialiased = false };

        byte[] levels = Over(selection, 10, 10);
        foreach (byte level in levels) Assert.True(level is 0 or 255, $"{level} is neither");

        // Every pixel is in or out by where its centre falls, and an edge exactly on a centre
        // counts as in — so the same rectangle that was half-covered either side is now four
        // whole pixels wide.
        var region = new PixelRect(0, 0, 10, 10);
        Assert.Equal(255, Level(levels, region, 2, 1));
        Assert.Equal(255, Level(levels, region, 4, 1));
        Assert.Equal(0, Level(levels, region, 5, 1));
        Assert.Equal(0, Level(levels, region, 1, 1));
    }

    [Fact]
    public void AnEmptySelectionSelectsNothingAndSaysSo()
    {
        DocumentSelection selection = DocumentSelection.Empty;

        Assert.True(selection.IsEmpty);
        Assert.Equal(0, Area(Over(selection, 8, 8)), precision: 6);

        // An empty selection is not the absence of one: the absence is a null, and it means the
        // whole canvas. Nothing here may quietly turn one into the other.
        Assert.True(selection.Bounds.IsEmpty);
    }

    [Fact]
    public void ALassoIsClosedByJoiningItsEnds()
    {
        DocumentSelection selection = DocumentSelection.Lasso(
            [new Point(0, 0), new Point(8, 0), new Point(8, 8)]);

        // Half of an 8×8 square, give or take the antialiased diagonal.
        Assert.InRange(Area(Over(selection, 8, 8)), 30, 34);
    }

    [Fact]
    public void ClippingCoversOnlyWhatCouldBeSelected()
    {
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(20, 30, 10, 10));
        (PixelRect region, byte[] levels) = selection.Clip(100, 100);

        Assert.Equal(new PixelRect(19, 29, 12, 12), region);
        Assert.Equal(100, Area(levels), precision: 6);
        Assert.Equal(255, Level(levels, region, 25, 35));
        Assert.Equal(0, Level(levels, region, 19, 29));
    }

    [Fact]
    public void ClippingOffTheCanvasCoversNothing()
    {
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(200, 200, 10, 10));
        (PixelRect region, byte[] levels) = selection.Clip(100, 100);

        Assert.True(region.IsEmpty);
        Assert.Empty(levels);
    }

    [Fact]
    public void CoverageIsTheShapeAMaskAlreadyTakes()
    {
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(1, 1, 2, 2));
        using PixelBuffer coverage = selection.Coverage(4, 4);

        Assert.Equal((255, 255, 255, 255), RenderFixture.At(coverage, 2, 2));
        Assert.Equal((0, 0, 0, 255), RenderFixture.At(coverage, 0, 0));
    }

    [Fact]
    public void ContainsFollowsWhatWasAddedAndTakenAway()
    {
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(0, 0, 10, 10))
            .Subtracting(DocumentSelection.Rectangle(new Rect(4, 4, 2, 2)));

        Assert.True(selection.Contains(new Point(1, 1)));
        Assert.False(selection.Contains(new Point(5, 5)));
        Assert.False(selection.Contains(new Point(20, 20)));
    }
}
