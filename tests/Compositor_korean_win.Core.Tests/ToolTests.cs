using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// The tools that do not need a kernel: clone, blur, gradient and shapes.
/// </summary>
public class ToolTests
{
    [Fact]
    public void TheCloneStampPaintsWhatIsAtTheOffset()
    {
        // Left half red, right half blue; clone the red onto the blue.
        using PixelBuffer image = PixelBuffer.Allocate(64, 64);
        for (int y = 0; y < 64; y++)
        {
            Span<byte> row = image.Row(y);
            for (int x = 0; x < 64; x++)
            {
                row[x * 4 + 0] = x < 32 ? (byte)220 : (byte)0;
                row[x * 4 + 2] = x < 32 ? (byte)0 : (byte)220;
                row[x * 4 + 3] = 255;
            }
        }

        var settings = new BrushSettings
        {
            Diameter = 12,
            Hardness = 1,
            Mode = BrushMode.Clone,
            Clone = new CloneSource(image, new Point(-40, 0)),
        };

        using var stroke = new BrushStroke(image, 64, 64, settings);
        stroke.Append(new Point(48, 32));

        using PixelBuffer painted = stroke.Commit();

        // Where the stroke went, the red half has been copied over the blue one.
        Assert.Equal((220, 0, 0, 255), RenderFixture.At(painted, 48, 32));
        Assert.Equal((0, 0, 220, 255), RenderFixture.At(painted, 60, 32));
    }

    [Fact]
    public void CloningFromOutsideTheLayerPaintsNothing()
    {
        using PixelBuffer image = RenderFixture.Solid(64, 64, 10, 20, 30);
        var settings = new BrushSettings
        {
            Diameter = 12,
            Mode = BrushMode.Clone,
            Clone = new CloneSource(image, new Point(-1000, 0)),
        };

        using var stroke = new BrushStroke(image, 64, 64, settings);
        stroke.Append(new Point(32, 32));

        using PixelBuffer painted = stroke.Commit();
        Assert.Equal((10, 20, 30, 255), RenderFixture.At(painted, 32, 32));
    }

    [Fact]
    public void TheBlurToolSoftensTheEdgeItIsDraggedOver()
    {
        // A hard edge down the middle: blurring it has to put middling values on it.
        using PixelBuffer image = PixelBuffer.Allocate(64, 64);
        for (int y = 0; y < 64; y++)
        {
            Span<byte> row = image.Row(y);
            for (int x = 0; x < 64; x++)
            {
                byte value = x < 32 ? (byte)0 : (byte)255;
                row[x * 4 + 0] = value;
                row[x * 4 + 1] = value;
                row[x * 4 + 2] = value;
                row[x * 4 + 3] = 255;
            }
        }

        var settings = new BrushSettings
        {
            Diameter = 24,
            Hardness = 1,
            Mode = BrushMode.Blur,
            BlurRadius = 5,
        };

        using var stroke = new BrushStroke(image, 64, 64, settings);
        stroke.Append(new Point(32, 32));

        using PixelBuffer painted = stroke.Commit();

        Assert.InRange(RenderFixture.At(painted, 31, 32).R, 40, 215);
        Assert.InRange(RenderFixture.At(painted, 33, 32).R, 40, 215);

        // Away from the stroke the edge is as hard as it ever was.
        Assert.Equal(0, RenderFixture.At(painted, 31, 2).R);
        Assert.Equal(255, RenderFixture.At(painted, 33, 2).R);
    }

    [Fact]
    public void ALinearGradientRunsFromOneColourToTheOther()
    {
        using PixelBuffer filled = GradientTool.Draw(null, 64, 16, new Point(0, 8), new Point(64, 8),
                                                     new GradientSettings
                                                     {
                                                         From = new Rgba(0, 0, 0),
                                                         To = new Rgba(255, 255, 255),
                                                     });

        try
        {
            Assert.InRange(RenderFixture.At(filled, 0, 8).R, 0, 8);
            Assert.InRange(RenderFixture.At(filled, 63, 8).R, 247, 255);

            // Halfway along, halfway between: a gradient nobody can see the bands in.
            Assert.InRange(RenderFixture.At(filled, 32, 8).R, 120, 136);
            Assert.Equal(255, RenderFixture.At(filled, 32, 8).A);
        }
        finally
        {
            filled.Release();
        }
    }

    [Fact]
    public void ARadialGradientSpreadsFromWhereItStarted()
    {
        using PixelBuffer filled = GradientTool.Draw(null, 64, 64, new Point(32, 32), new Point(64, 32),
                                                     new GradientSettings
                                                     {
                                                         Kind = GradientKind.Radial,
                                                         From = new Rgba(0, 0, 0),
                                                         To = new Rgba(255, 255, 255),
                                                     });

        try
        {
            int middle = RenderFixture.At(filled, 32, 32).R;
            int halfway = RenderFixture.At(filled, 48, 32).R;
            int corner = RenderFixture.At(filled, 63, 63).R;

            Assert.InRange(middle, 0, 8);
            Assert.InRange(halfway, 120, 136);
            Assert.Equal(255, corner);   // Past the end, so the far colour and no further.
        }
        finally
        {
            filled.Release();
        }
    }

    [Fact]
    public void AGradientStaysInsideTheSelection()
    {
        DocumentSelection selection = DocumentSelection.Rectangle(new Rect(0, 0, 32, 16));
        using PixelBuffer filled = GradientTool.Draw(null, 64, 16, new Point(0, 8), new Point(64, 8),
                                                     new GradientSettings(), selection);

        try
        {
            Assert.Equal(255, RenderFixture.At(filled, 10, 8).A);
            Assert.Equal(0, RenderFixture.At(filled, 40, 8).A);
        }
        finally
        {
            filled.Release();
        }
    }

    [Theory]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.RoundedRectangle)]
    [InlineData(ShapeKind.Ellipse)]
    public void AShapeIsFilledWhereItIsAndNowhereElse(ShapeKind kind)
    {
        var settings = new ShapeSettings { Kind = kind, Color = new Rgba(200, 40, 60), CornerRadius = 8 };
        using PixelBuffer drawn = ShapeTool.Draw(null, 64, 64, new Rect(16, 16, 32, 32), settings);

        try
        {
            Assert.Equal((200, 40, 60, 255), RenderFixture.At(drawn, 32, 32));
            Assert.Equal(0, RenderFixture.At(drawn, 4, 4).A);
            Assert.Equal(0, RenderFixture.At(drawn, 60, 32).A);
        }
        finally
        {
            drawn.Release();
        }
    }

    [Fact]
    public void RoundedCornersAreRoundedAndSquareOnesAreNot()
    {
        var rounded = new ShapeSettings { Kind = ShapeKind.RoundedRectangle, CornerRadius = 12 };
        var square = new ShapeSettings { Kind = ShapeKind.Rectangle };

        using PixelBuffer withCorners = ShapeTool.Draw(null, 64, 64, new Rect(16, 16, 32, 32), rounded);
        using PixelBuffer without = ShapeTool.Draw(null, 64, 64, new Rect(16, 16, 32, 32), square);

        // The very corner of the box is inside a rectangle and outside a rounded one.
        Assert.Equal(255, RenderFixture.At(without, 17, 17).A);
        Assert.Equal(0, RenderFixture.At(withCorners, 17, 17).A);

        // Both are solid in the middle.
        Assert.Equal(255, RenderFixture.At(withCorners, 32, 32).A);
    }

    [Fact]
    public void ACornerRadiusLargerThanTheShapeIsHeldToIt()
    {
        DocumentSelection outline = ShapeTool.Rounded(new Rect(0, 0, 20, 10), radius: 1000);
        byte[] levels = outline.Levels(new PixelRect(0, 0, 20, 10));

        double area = 0;
        foreach (byte level in levels) area += level / 255.0;

        // Held to half the shorter side, so this is a stadium: a 10×10 rectangle with two
        // semicircular ends — 100 + π·5², not something that folded through itself.
        Assert.InRange(area, 170, 186);
    }

    [Fact]
    public void AShapeLandsOnWhatIsAlreadyThere()
    {
        using PixelBuffer background = RenderFixture.Solid(32, 32, 255, 255, 255);
        var settings = new ShapeSettings { Color = new Rgba(0, 0, 0) };

        using PixelBuffer drawn = ShapeTool.Draw(background, 32, 32, new Rect(8, 8, 16, 16), settings);

        Assert.Equal((0, 0, 0, 255), RenderFixture.At(drawn, 16, 16));
        Assert.Equal((255, 255, 255, 255), RenderFixture.At(drawn, 2, 2));
    }
}
