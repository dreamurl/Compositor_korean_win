using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// The placement record, which is the whole of a non-destructive transform.
/// </summary>
/// <remarks>
/// Scaling a layer rewrites six numbers and never touches a pixel, so an image shrunk to a
/// thumbnail and scaled back up is the original again. These tests pin the arithmetic that has to
/// hold for that to be true.
/// </remarks>
public class LayerTransformTests
{
    private static LayerTransform At(double x, double y, double width, double height) =>
        new(new Point(x, y), new Size(width, height));

    [Fact]
    public void CentreIsTheMiddleOfTheUnrotatedBounds()
    {
        LayerTransform transform = At(10, 20, 100, 50);
        Assert.Equal(new Point(60, 45), transform.Center);
    }

    [Fact]
    public void TheUnitSquareMapsToTheCorners()
    {
        LayerTransform transform = At(0, 0, 100, 50);

        Assert.Equal(new Point(0, 0), transform.PointAt(new Point(0, 0)));
        Assert.Equal(new Point(100, 50), transform.PointAt(new Point(1, 1)));
        Assert.Equal(new Point(50, 25), transform.PointAt(new Point(0.5, 0.5)));
    }

    [Fact]
    public void RotationTurnsClockwiseAroundTheCentre()
    {
        LayerTransform transform = At(0, 0, 100, 50) with { Rotation = 90 };
        Point corner = transform.PointAt(new Point(0, 0));

        // A quarter turn clockwise puts the top-left corner where the bottom-left one was.
        Assert.Equal(75d, corner.X, precision: 9);
        Assert.Equal(-25d, corner.Y, precision: 9);
    }

    [Fact]
    public void ContainsFollowsTheRotatedRectangle()
    {
        LayerTransform transform = At(0, 0, 100, 20) with { Rotation = 90 };

        Assert.True(transform.Contains(new Point(50, 10)));
        Assert.True(transform.Contains(new Point(50, 55)));   // Along the rotated long side.
        Assert.False(transform.Contains(new Point(95, 10)));  // Off its rotated short side.
    }

    [Fact]
    public void ScalingByPercentKeepsTheCentre()
    {
        LayerTransform transform = At(0, 0, 100, 50);
        LayerTransform half = transform.ScaledToPercent(50, new Size(100, 50));

        Assert.Equal(new Size(50, 25), half.Size);
        Assert.Equal(transform.Center, half.Center);
        Assert.Equal(50d, half.ScalePercent(new Size(100, 50)));
    }

    [Fact]
    public void ShrinkingAndGrowingBackRestoresTheOriginalPlacement()
    {
        // The claim non-destructive transforms rest on: the pixels were never consulted, so this
        // is exact rather than merely close.
        var pixels = new Size(1920, 1080);
        LayerTransform original = At(40, 30, 1920, 1080);

        LayerTransform tiny = original.ScaledToPercent(2, pixels);
        LayerTransform back = tiny.ScaledToPercent(100, pixels);

        Assert.Equal(original.Size, back.Size);
        Assert.Equal(original.Center, back.Center);
    }

    [Fact]
    public void RoundingSnapsToWholePixelsAndDegrees()
    {
        LayerTransform rounded = (At(10.4, -3.6, 99.5, 0.2) with { Rotation = 37.5 }).Rounded();

        Assert.Equal(new Point(10, -4), rounded.Origin);
        Assert.Equal(new Size(100, 1), rounded.Size);   // A side never rounds away to nothing.
        Assert.Equal(38d, rounded.Rotation);
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(300_000, 300_000, true)]
    [InlineData(0.5, 10, false)]
    [InlineData(300_001, 10, false)]
    [InlineData(double.NaN, 10, false)]
    [InlineData(double.PositiveInfinity, 10, false)]
    public void ValidityFollowsTheFormatsLimits(double width, double height, bool valid) =>
        Assert.Equal(valid, At(0, 0, width, height).IsValid);

    [Theory]
    [InlineData(1_000_001)]
    [InlineData(-1_000_001)]
    public void AnOriginFarOffTheCanvasIsRejected(double x) =>
        Assert.False(At(x, 0, 10, 10).IsValid);

    [Fact]
    public void AMaskCarriesExactlyWhenItsLayerOnlyMoves()
    {
        // An unlinked mask keeps its own placement, so a plain move has to carry it without
        // introducing any drift.
        LayerTransform mask = At(10, 10, 20, 20);
        LayerTransform before = At(0, 0, 100, 100);
        LayerTransform after = At(35, -12, 100, 100);

        LayerTransform moved = mask.Following(before, after);

        Assert.Equal(new Point(45, -2), moved.Origin);
        Assert.Equal(mask.Size, moved.Size);
        Assert.Equal(mask.Rotation, moved.Rotation);
    }

    [Fact]
    public void AMaskScalesWithItsLayer()
    {
        LayerTransform mask = At(0, 0, 50, 50);
        LayerTransform before = At(0, 0, 100, 100);
        LayerTransform after = At(0, 0, 200, 200);

        LayerTransform scaled = mask.Following(before, after);

        Assert.Equal(new Size(100, 100), scaled.Size);
        Assert.Equal(new Point(0, 0), scaled.Origin);
    }

    [Fact]
    public void CarryingAcrossNoChangeIsIdentity()
    {
        LayerTransform mask = At(10, 10, 20, 20);
        LayerTransform placement = At(0, 0, 100, 100);

        Assert.Equal(mask, mask.Following(placement, placement));
    }

    [Fact]
    public void SamplingIsPartOfTheStoredTransform()
    {
        // It rides along with the placement because it describes how these pixels are resampled,
        // not a preference — a layer scaled with Nearest stays crisp after a reload.
        LayerTransform transform = At(0, 0, 10, 10) with { Sampling = LayerSampling.Nearest };
        ProjectManifest manifest = ProjectFixture.Manifest(
            layers: [ProjectFixture.Record("Layer 1") with { Transform = transform }]);

        ProjectManifest reparsed = ProjectStore.FromJson(ProjectStore.ToJson(manifest));
        Assert.Equal(LayerSampling.Nearest, reparsed.Layers[0].Transform.Sampling);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(30.0)]
    [InlineData(-135.0)]
    public void UnitAtUndoesPointAt(double rotation)
    {
        var transform = new LayerTransform(new Point(40, -12), new Size(300, 120)) { Rotation = rotation };
        foreach (Point unit in new[] { new Point(0, 0), new Point(1, 0.25), new Point(0.3, 0.9) })
        {
            Point back = transform.UnitAt(transform.PointAt(unit));
            Assert.Equal(unit.X, back.X, 9);
            Assert.Equal(unit.Y, back.Y, 9);
        }
    }
}
