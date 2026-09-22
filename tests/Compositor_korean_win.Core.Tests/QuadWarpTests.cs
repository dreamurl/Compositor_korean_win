using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// Free distortion: the one transform that has to touch pixels, and the group box that does not.
/// </summary>
public class QuadWarpTests
{
    private static IReadOnlyList<Point> Corners(double x, double y, double width, double height) =>
    [
        new Point(x, y), new Point(x + width, y),
        new Point(x + width, y + height), new Point(x, y + height),
    ];

    [Fact]
    public void ARectangleOfCornersIsAnOrdinaryScale()
    {
        using PixelBuffer source = RenderFixture.Solid(8, 8, 200, 100, 50);
        (PixelBuffer pixels, LayerTransform placement) = QuadWarp.Resample(source, Corners(10, 20, 16, 16))!.Value;

        try
        {
            Assert.Equal(16, pixels.Width);
            Assert.Equal(16, pixels.Height);
            Assert.Equal(new Point(10, 20), placement.Origin);
            Assert.Equal(new Size(16, 16), placement.Size);

            // Doubled in each direction and nothing else: the colour survives inside.
            Assert.Equal((200, 100, 50, 255), RenderFixture.At(pixels, 8, 8));
            Assert.Equal((200, 100, 50, 255), RenderFixture.At(pixels, 2, 13));

            // The outermost pixel is half a sample off the edge, so it fades rather than clipping —
            // the same rule the renderer follows, and what stops an edge hardening as it is warped.
            Assert.InRange(RenderFixture.At(pixels, 0, 0).A, 1, 254);
        }
        finally
        {
            pixels.Release();
        }
    }

    [Fact]
    public void TheCornersEndUpWhereTheyWerePut()
    {
        using PixelBuffer source = RenderFixture.Gradient(16, 16);

        // A shear: the top edge slid sideways, which no LayerTransform can express.
        IReadOnlyList<Point> corners =
        [
            new Point(8, 0), new Point(24, 0), new Point(16, 16), new Point(0, 16),
        ];

        (PixelBuffer pixels, LayerTransform placement) = QuadWarp.Resample(source, corners)!.Value;

        try
        {
            Assert.Equal(new Point(0, 0), placement.Origin);
            Assert.Equal(new Size(24, 16), placement.Size);

            // Inside the sheared shape there are pixels; outside it, along the bottom right where
            // the shape has moved away from the box, there are none.
            Assert.True(RenderFixture.At(pixels, 12, 8).A > 0);
            Assert.Equal(0, RenderFixture.At(pixels, 22, 14).A);
            Assert.Equal(0, RenderFixture.At(pixels, 1, 1).A);
        }
        finally
        {
            pixels.Release();
        }
    }

    [Theory]
    [InlineData(0, 0, 10, 0, 0, 10, 10, 10)]      // A bow-tie: the sides cross.
    [InlineData(0, 0, 10, 0, 10, 0, 0, 0)]        // Collapsed onto a line.
    public void AShapeWithNoSensibleMapIsRefused(double x0, double y0, double x1, double y1,
                                                 double x2, double y2, double x3, double y3)
    {
        using PixelBuffer source = RenderFixture.Solid(4, 4, 255, 255, 255);
        IReadOnlyList<Point> corners =
            [new Point(x0, y0), new Point(x1, y1), new Point(x2, y2), new Point(x3, y3)];

        Assert.False(QuadWarp.IsUsable(corners));
        Assert.Null(QuadWarp.Resample(source, corners));
    }

    [Fact]
    public void ADistortionIsNotSomethingTheFormatHasToStore()
    {
        using PixelBuffer source = RenderFixture.Gradient(8, 8);
        IReadOnlyList<Point> corners =
            [new Point(2, 0), new Point(12, 2), new Point(10, 12), new Point(0, 9)];

        (PixelBuffer pixels, LayerTransform placement) = QuadWarp.Resample(source, corners)!.Value;

        try
        {
            // Whatever the corners were doing is in the pixels now; what is left is an upright box
            // the format has always been able to hold.
            Assert.Equal(0, placement.Rotation);
            Assert.False(placement.FlipX);
            Assert.False(placement.FlipY);
            Assert.True(placement.IsValid);
            Assert.Equal(Rect.Around(corners).Enclosing().Width, pixels.Width);
        }
        finally
        {
            pixels.Release();
        }
    }

    [Fact]
    public void TheGroupBoxSurroundsEverythingSelected()
    {
        LayerTransform box = TransformGroup.BoxAround(
        [
            new LayerTransform(new Point(10, 10), new Size(20, 20)),
            new LayerTransform(new Point(50, 5), new Size(10, 40)),
        ]);

        Assert.Equal(new Point(10, 5), box.Origin);
        Assert.Equal(new Size(50, 40), box.Size);
        Assert.Equal(0, box.Rotation);
    }

    [Fact]
    public void ARotatedLayerContributesTheBoxAroundIt()
    {
        LayerTransform turned = new LayerTransform(new Point(0, 0), new Size(100, 50)) { Rotation = 90 };
        LayerTransform box = TransformGroup.BoxAround([turned]);

        Assert.Equal(50, box.Size.Width, precision: 6);
        Assert.Equal(100, box.Size.Height, precision: 6);
    }

    [Fact]
    public void EveryLayerFollowsTheBox()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var originals = new Dictionary<Guid, LayerTransform>
        {
            [first] = new(new Point(0, 0), new Size(10, 10)),
            [second] = new(new Point(20, 0), new Size(10, 10)),
        };

        LayerTransform from = TransformGroup.BoxAround([.. originals.Values]);
        LayerTransform to = from with { Origin = new Point(from.Origin.X + 7, from.Origin.Y - 3) };

        IReadOnlyDictionary<Guid, LayerTransform> moved = TransformGroup.Follow(originals, from, to);

        // A plain move carries exactly, and the layers keep their distance from each other.
        Assert.Equal(new Point(7, -3), moved[first].Origin);
        Assert.Equal(new Point(27, -3), moved[second].Origin);
    }

    [Fact]
    public void ScalingTheBoxScalesWhatIsInIt()
    {
        Guid id = Guid.NewGuid();
        var originals = new Dictionary<Guid, LayerTransform>
        {
            [id] = new(new Point(10, 10), new Size(10, 10)),
        };

        LayerTransform from = TransformGroup.BoxAround([.. originals.Values]);
        LayerTransform to = new(new Point(10, 10), new Size(20, 20));

        LayerTransform moved = TransformGroup.Follow(originals, from, to)[id];

        Assert.Equal(new Size(20, 20), moved.Size);
        Assert.Equal(new Point(10, 10), moved.Origin);
    }
}
