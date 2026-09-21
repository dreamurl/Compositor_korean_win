using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>Buffers and documents to render, and a way to look at what came out.</summary>
internal static class RenderFixture
{
    public static PixelBuffer Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x * 4 + 0] = (byte)(r * a / 255);
                row[x * 4 + 1] = (byte)(g * a / 255);
                row[x * 4 + 2] = (byte)(b * a / 255);
                row[x * 4 + 3] = a;
            }
        }
        return buffer;
    }

    /// <summary>Grey coverage: opaque, with the level repeated across every channel.</summary>
    public static PixelBuffer Coverage(int width, int height, Func<int, int, byte> level)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < width; x++)
            {
                byte value = level(x, y);
                row[x * 4 + 0] = value;
                row[x * 4 + 1] = value;
                row[x * 4 + 2] = value;
                row[x * 4 + 3] = 255;
            }
        }
        return buffer;
    }

    /// <summary>Pixels that vary in every direction, so a flip or a transpose shows up.</summary>
    public static PixelBuffer Gradient(int width, int height)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x * 4 + 0] = (byte)(x * 255 / Math.Max(1, width - 1));
                row[x * 4 + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                row[x * 4 + 2] = (byte)((x + y) % 256);
                row[x * 4 + 3] = 255;
            }
        }
        return buffer;
    }

    public static ImageLayer Layer(string name, PixelBuffer image, double x = 0, double y = 0) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Image = image,
        Transform = new LayerTransform(new Point(x, y), new Size(image.Width, image.Height)),
    };

    public static CanvasDocument Document(int width, int height, params ImageLayer[] layers) => new()
    {
        Id = Guid.NewGuid(),
        Width = width,
        Height = height,
        Layers = new EquatableList<ImageLayer>(layers),
    };

    /// <summary>One pixel, as plain numbers so a test can write what it expects inline.</summary>
    public static (int R, int G, int B, int A) At(PixelBuffer buffer, int x, int y)
    {
        Span<byte> row = buffer.Row(y);
        return (row[x * 4], row[x * 4 + 1], row[x * 4 + 2], row[x * 4 + 3]);
    }
}

/// <summary>
/// The compositor: what a document turns into, and why.
/// </summary>
/// <remarks>
/// Every case here is a rule the renderer has to follow rather than a picture that happens to come
/// out. The software backend is the reference; the Direct2D one is checked against it separately,
/// where a GPU is available.
/// </remarks>
public class LayerCompositorTests
{
    [Fact]
    public void ALayerLandsWhereItsTransformPutsIt()
    {
        using PixelBuffer red = RenderFixture.Solid(4, 4, 255, 0, 0);
        CanvasDocument document = RenderFixture.Document(16, 16, RenderFixture.Layer("Red", red, x: 3, y: 5));

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer output = LayerCompositor.Render(document, backend);

        Assert.Equal((255, 0, 0, 255), RenderFixture.At(output, 4, 6));
        Assert.Equal((0, 0, 0, 0), RenderFixture.At(output, 2, 6));   // Just left of it.
        Assert.Equal((0, 0, 0, 0), RenderFixture.At(output, 7, 6));   // Just right of it.
        Assert.Equal((0, 0, 0, 0), RenderFixture.At(output, 4, 4));   // Just above it.
    }

    [Fact]
    public void LayersStackBottomToTop()
    {
        using PixelBuffer red = RenderFixture.Solid(8, 8, 255, 0, 0);
        using PixelBuffer blue = RenderFixture.Solid(8, 8, 0, 0, 255);
        CanvasDocument document = RenderFixture.Document(8, 8,
            RenderFixture.Layer("Red", red), RenderFixture.Layer("Blue", blue));

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer output = LayerCompositor.Render(document, backend);

        // The array is bottom to top, so blue wins.
        Assert.Equal((0, 0, 255, 255), RenderFixture.At(output, 4, 4));
    }

    [Fact]
    public void AHiddenLayerDrawsNothing()
    {
        using PixelBuffer red = RenderFixture.Solid(8, 8, 255, 0, 0);
        using PixelBuffer blue = RenderFixture.Solid(8, 8, 0, 0, 255);
        CanvasDocument document = RenderFixture.Document(8, 8,
            RenderFixture.Layer("Red", red),
            RenderFixture.Layer("Blue", blue) with { IsVisible = false });

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer output = LayerCompositor.Render(document, backend);

        Assert.Equal((255, 0, 0, 255), RenderFixture.At(output, 4, 4));
    }

    [Fact]
    public void OpacityMixesTowardsWhatIsUnderneath()
    {
        using PixelBuffer black = RenderFixture.Solid(8, 8, 0, 0, 0);
        using PixelBuffer white = RenderFixture.Solid(8, 8, 255, 255, 255);
        CanvasDocument document = RenderFixture.Document(8, 8,
            RenderFixture.Layer("Black", black),
            RenderFixture.Layer("White", white) with { Opacity = 0.5 });

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer output = LayerCompositor.Render(document, backend);

        (int r, _, _, int a) = RenderFixture.At(output, 4, 4);
        Assert.Equal(255, a);
        Assert.InRange(r, 127, 129);
    }

    [Fact]
    public void AMaskHidesWhereItIsBlack()
    {
        using PixelBuffer red = RenderFixture.Solid(8, 8, 255, 0, 0);
        using PixelBuffer coverage = RenderFixture.Coverage(8, 8, (x, _) => x < 4 ? (byte)255 : (byte)0);

        ImageLayer layer = RenderFixture.Layer("Red", red) with
        {
            Mask = new LayerMask { Coverage = coverage },
        };

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer output = LayerCompositor.Render(RenderFixture.Document(8, 8, layer), backend);

        Assert.Equal((255, 0, 0, 255), RenderFixture.At(output, 1, 4));
        Assert.Equal((0, 0, 0, 0), RenderFixture.At(output, 6, 4));
    }

    [Fact]
    public void ADisabledMaskChangesNothing()
    {
        using PixelBuffer red = RenderFixture.Solid(8, 8, 255, 0, 0);
        using PixelBuffer coverage = RenderFixture.Coverage(8, 8, (_, _) => 0);

        ImageLayer layer = RenderFixture.Layer("Red", red) with
        {
            Mask = new LayerMask { Coverage = coverage, IsEnabled = false },
        };

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer output = LayerCompositor.Render(RenderFixture.Document(8, 8, layer), backend);

        // The mask stays embedded and editable, but it does not affect compositing.
        Assert.Equal((255, 0, 0, 255), RenderFixture.At(output, 4, 4));
    }

    [Fact]
    public void AFolderMaskAppliesToEveryLayerInside()
    {
        using PixelBuffer red = RenderFixture.Solid(8, 8, 255, 0, 0);
        using PixelBuffer blue = RenderFixture.Solid(8, 8, 0, 0, 255);
        using PixelBuffer coverage = RenderFixture.Coverage(8, 8, (_, y) => y < 4 ? (byte)255 : (byte)0);

        Guid folderId = Guid.NewGuid();
        var folder = new ImageLayer
        {
            Id = folderId,
            Name = "Folder",
            IsGroup = true,
            Transform = new LayerTransform(Point.Zero, new Size(8, 8)),
            Mask = new LayerMask { Coverage = coverage },
        };

        CanvasDocument document = RenderFixture.Document(8, 8,
            folder,
            RenderFixture.Layer("Red", red) with { ParentId = folderId },
            RenderFixture.Layer("Blue", blue) with { ParentId = folderId });

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer output = LayerCompositor.Render(document, backend);

        Assert.Equal((0, 0, 255, 255), RenderFixture.At(output, 4, 1));
        Assert.Equal((0, 0, 0, 0), RenderFixture.At(output, 4, 6));
    }

    [Fact]
    public void NestedFolderMasksMultiply()
    {
        using PixelBuffer red = RenderFixture.Solid(8, 8, 255, 0, 0);
        using PixelBuffer outerCoverage = RenderFixture.Coverage(8, 8, (x, _) => x < 6 ? (byte)255 : (byte)0);
        using PixelBuffer innerCoverage = RenderFixture.Coverage(8, 8, (_, y) => y < 6 ? (byte)255 : (byte)0);

        Guid outerId = Guid.NewGuid(), innerId = Guid.NewGuid();
        var canvas = new LayerTransform(Point.Zero, new Size(8, 8));

        CanvasDocument document = RenderFixture.Document(8, 8,
            new ImageLayer
            {
                Id = outerId, Name = "Outer", IsGroup = true, Transform = canvas,
                Mask = new LayerMask { Coverage = outerCoverage },
            },
            new ImageLayer
            {
                Id = innerId, Name = "Inner", IsGroup = true, Transform = canvas, ParentId = outerId,
                Mask = new LayerMask { Coverage = innerCoverage },
            },
            RenderFixture.Layer("Red", red) with { ParentId = innerId });

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer output = LayerCompositor.Render(document, backend);

        Assert.Equal((255, 0, 0, 255), RenderFixture.At(output, 2, 2)); // Inside both.
        Assert.Equal((0, 0, 0, 0), RenderFixture.At(output, 7, 2));     // Outside the outer one.
        Assert.Equal((0, 0, 0, 0), RenderFixture.At(output, 2, 7));     // Outside the inner one.
    }

    [Fact]
    public void AHiddenFolderHidesEverythingInside()
    {
        using PixelBuffer red = RenderFixture.Solid(8, 8, 255, 0, 0);
        Guid folderId = Guid.NewGuid();

        CanvasDocument document = RenderFixture.Document(8, 8,
            new ImageLayer
            {
                Id = folderId, Name = "Folder", IsGroup = true, IsVisible = false,
                Transform = new LayerTransform(Point.Zero, new Size(8, 8)),
            },
            RenderFixture.Layer("Red", red) with { ParentId = folderId });

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer output = LayerCompositor.Render(document, backend);

        // Visibility is inherited without changing the child's own flag.
        Assert.Equal((0, 0, 0, 0), RenderFixture.At(output, 4, 4));
    }

    [Fact]
    public void AClippedLayerShowsOnlyWhereItsBaseIsOpaque()
    {
        // A base covering the left half, and a layer clipped to it covering everything.
        using PixelBuffer baseImage = RenderFixture.Solid(4, 8, 0, 255, 0);
        using PixelBuffer clippedImage = RenderFixture.Solid(8, 8, 255, 0, 255);

        ImageLayer baseLayer = RenderFixture.Layer("Base", baseImage);
        ImageLayer clipped = RenderFixture.Layer("Clipped", clippedImage) with { MaskSourceId = baseLayer.Id };

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer output = LayerCompositor.Render(
            RenderFixture.Document(8, 8, baseLayer, clipped), backend);

        Assert.Equal((255, 0, 255, 255), RenderFixture.At(output, 1, 4)); // Over the base.
        Assert.Equal((0, 0, 0, 0), RenderFixture.At(output, 6, 4));       // Past its edge.
    }

    [Fact]
    public void AClippingGroupKeepsTheBasesOwnCoverage()
    {
        // This is the property the group surface exists for. The base is half transparent; the
        // layer clipped to it is opaque. Compositing them one after another would end up more
        // opaque than the base, thickening every soft edge.
        using PixelBuffer baseImage = RenderFixture.Solid(8, 8, 0, 255, 0, a: 128);
        using PixelBuffer clippedImage = RenderFixture.Solid(8, 8, 255, 0, 0);

        ImageLayer baseLayer = RenderFixture.Layer("Base", baseImage);
        ImageLayer clipped = RenderFixture.Layer("Clipped", clippedImage) with { MaskSourceId = baseLayer.Id };

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer output = LayerCompositor.Render(
            RenderFixture.Document(8, 8, baseLayer, clipped), backend);

        (int r, _, _, int a) = RenderFixture.At(output, 4, 4);
        Assert.InRange(a, 127, 129);  // Exactly the base's coverage, not more.
        Assert.InRange(r, 127, 129);  // Fully red underneath that coverage.
    }

    [Fact]
    public void SeveralLayersShareOneClippingBase()
    {
        using PixelBuffer baseImage = RenderFixture.Solid(8, 4, 0, 0, 255);
        using PixelBuffer first = RenderFixture.Solid(8, 8, 255, 0, 0);
        using PixelBuffer second = RenderFixture.Solid(4, 8, 0, 255, 0);

        ImageLayer baseLayer = RenderFixture.Layer("Base", baseImage);

        CanvasDocument document = RenderFixture.Document(8, 8,
            baseLayer,
            RenderFixture.Layer("First", first) with { MaskSourceId = baseLayer.Id },
            RenderFixture.Layer("Second", second) with { MaskSourceId = baseLayer.Id });

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer output = LayerCompositor.Render(document, backend);

        Assert.Equal((0, 255, 0, 255), RenderFixture.At(output, 1, 1)); // Second wins where it is.
        Assert.Equal((255, 0, 0, 255), RenderFixture.At(output, 6, 1)); // First shows elsewhere.
        Assert.Equal((0, 0, 0, 0), RenderFixture.At(output, 4, 6));     // Below the base, nothing.
    }

    [Fact]
    public void AnAdjustmentLayerDrawsNothingYet()
    {
        // M2 renders pixels. Applying an adjustment means filtering what is underneath, which is
        // M5; until then one must not silently paint its blank raster over the document.
        using PixelBuffer red = RenderFixture.Solid(8, 8, 255, 0, 0);

        CanvasDocument document = RenderFixture.Document(8, 8,
            RenderFixture.Layer("Red", red),
            new ImageLayer
            {
                Id = Guid.NewGuid(),
                Name = "Levels",
                Transform = new LayerTransform(Point.Zero, new Size(8, 8)),
                Adjustment = new LayerAdjustment(AdjustmentKind.Levels),
            });

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer output = LayerCompositor.Render(document, backend);

        Assert.Equal((255, 0, 0, 255), RenderFixture.At(output, 4, 4));
    }

    [Fact]
    public void FlippingMirrorsThePixels()
    {
        using PixelBuffer gradient = RenderFixture.Gradient(8, 8);
        using var backend = new SoftwareRenderBackend();

        using PixelBuffer plain = LayerCompositor.Render(
            RenderFixture.Document(8, 8, RenderFixture.Layer("G", gradient)), backend);

        ImageLayer flipped = RenderFixture.Layer("G", gradient);
        flipped = flipped with { Transform = flipped.Transform with { FlipX = true } };
        using PixelBuffer mirrored = LayerCompositor.Render(
            RenderFixture.Document(8, 8, flipped), backend);

        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(RenderFixture.At(plain, x, y), RenderFixture.At(mirrored, 7 - x, y));
    }

    [Fact]
    public void AFullTurnChangesNothing()
    {
        using PixelBuffer gradient = RenderFixture.Gradient(8, 8);
        using var backend = new SoftwareRenderBackend();

        ImageLayer layer = RenderFixture.Layer("G", gradient);
        using PixelBuffer plain = LayerCompositor.Render(RenderFixture.Document(8, 8, layer), backend);

        ImageLayer turned = layer with { Transform = layer.Transform with { Rotation = 360 } };
        using PixelBuffer full = LayerCompositor.Render(RenderFixture.Document(8, 8, turned), backend);

        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(RenderFixture.At(plain, x, y), RenderFixture.At(full, x, y));
    }
}
