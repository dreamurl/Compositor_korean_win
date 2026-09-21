using Compositor_korean_win.Core;

namespace Compositor_korean_win.Shell;

/// <summary>
/// Renders the same document twice — once on the GPU, once on the reference rasteriser — and says
/// how far apart they are.
/// </summary>
/// <remarks>
/// This is how M2 closes. docs/windows-port.md asks for a pixel comparison against a reference,
/// and with no Mac in the loop the reference has to be something this project can produce and
/// defend: the software backend, whose compositing rules are pinned by tests that run everywhere.
///
/// Two kinds of comparison, because they mean different things:
/// <list type="bullet">
/// <item><b>Exact</b> — a document drawn at 1:1 with Nearest sampling. Nothing is resampled, so
/// every difference here is a difference in the blend, mask or composite arithmetic, and there
/// should be none worth speaking of.</item>
/// <item><b>Resampled</b> — the same document scaled and rotated. Direct2D's filters are not this
/// port's filters and never will be, so the number here is reported rather than asserted.</item>
/// </list>
/// </remarks>
internal static class RenderCheck
{
    internal readonly record struct Difference(int Max, double Mean, long Pixels)
    {
        public override string ToString() => $"max {Max}, mean {Mean:F2} over {Pixels} pixels";
    }

    internal sealed record Result(Difference Exact, Difference Resampled, string BackendName);

    public static Result Run(GraphicsDevice device, string? imageFolder)
    {
        using var software = new SoftwareRenderBackend();
        using var hardware = new Direct2DBackend(device);

        Difference exact = Compare(Scene(scaled: false), software, hardware, imageFolder, "exact");
        Difference resampled = Compare(Scene(scaled: true), software, hardware, imageFolder, "resampled");

        return new Result(exact, resampled, hardware.Name);
    }

    private static Difference Compare(CanvasDocument document, IRenderBackend software,
                                      IRenderBackend hardware, string? imageFolder, string name)
    {
        using PixelBuffer reference = LayerCompositor.Render(document, software);
        using PixelBuffer actual = LayerCompositor.Render(document, hardware);

        if (imageFolder is not null)
        {
            Directory.CreateDirectory(imageFolder);
            File.WriteAllBytes(Path.Combine(imageFolder, $"{name}-software.png"), Png.Encode(reference));
            File.WriteAllBytes(Path.Combine(imageFolder, $"{name}-direct2d.png"), Png.Encode(actual));
        }

        int max = 0;
        long total = 0;
        long samples = 0;

        for (int y = 0; y < reference.Height; y++)
        {
            Span<byte> a = reference.Row(y);
            Span<byte> b = actual.Row(y);

            for (int i = 0; i < reference.Width * 4; i++)
            {
                int difference = Math.Abs(a[i] - b[i]);
                max = Math.Max(max, difference);
                total += difference;
                samples++;
            }
        }

        return new Difference(max, samples == 0 ? 0 : (double)total / samples,
                              (long)reference.Width * reference.Height);
    }

    /// <summary>
    /// A document that exercises the compositing rules rather than looking like anything.
    /// </summary>
    /// <remarks>
    /// Every layer below is here to catch a specific mistake: a blend mode wired to the wrong one,
    /// a mask read from the wrong channel, a folder mask that stops at its own folder, a clipping
    /// group that thickens the base's edge.
    /// </remarks>
    private static CanvasDocument Scene(bool scaled)
    {
        const int Size = 128;
        var layers = new List<ImageLayer>();

        PixelBuffer backdrop = Checkerboard(Size, Size);
        layers.Add(new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = "Backdrop",
            Image = backdrop,
            Transform = Place(0, 0, Size, Size, scaled),
        });

        // One layer per blend mode, in a row, each a soft-edged blob so the partly transparent
        // cases are covered too.
        LayerBlendMode[] modes =
        [
            LayerBlendMode.Multiply, LayerBlendMode.Screen, LayerBlendMode.Overlay,
            LayerBlendMode.Darken, LayerBlendMode.Lighten, LayerBlendMode.Difference,
            LayerBlendMode.ColorDodge, LayerBlendMode.ColorBurn,
        ];

        for (int i = 0; i < modes.Length; i++)
        {
            layers.Add(new ImageLayer
            {
                Id = Guid.NewGuid(),
                Name = modes[i].ToString(),
                Image = Blob(16, 16, (byte)(30 + i * 28)),
                Transform = Place(i * 16, 8, 16, 16, scaled),
                BlendMode = modes[i],
                Opacity = 0.8,
            });
        }

        // A masked layer, a folder with a mask over two layers, and a clipping group.
        Guid folderId = Guid.NewGuid();
        layers.Add(new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = "Masked",
            Image = Solid(48, 32, 255, 64, 0),
            Transform = Place(8, 48, 48, 32, scaled),
            Mask = new LayerMask { Coverage = Ramp(48, 32) },
        });

        layers.Add(new ImageLayer
        {
            Id = folderId,
            Name = "Folder",
            IsGroup = true,
            Transform = Place(0, 0, Size, Size, scaled),
            Mask = new LayerMask { Coverage = Ramp(Size, Size) },
        });
        layers.Add(new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = "In folder",
            Image = Solid(40, 24, 0, 200, 255),
            Transform = Place(64, 48, 40, 24, scaled),
            ParentId = folderId,
        });

        Guid baseId = Guid.NewGuid();
        layers.Add(new ImageLayer
        {
            Id = baseId,
            Name = "Clip base",
            Image = Blob(56, 40, 200),
            Transform = Place(16, 84, 56, 40, scaled),
        });
        layers.Add(new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = "Clipped",
            Image = Stripes(56, 40),
            Transform = Place(16, 84, 56, 40, scaled),
            MaskSourceId = baseId,
        });

        return new CanvasDocument
        {
            Id = Guid.NewGuid(),
            Width = Size,
            Height = Size,
            Layers = new EquatableList<ImageLayer>(layers),
        };
    }

    /// <summary>
    /// A placement: 1:1 and pixel-aligned for the exact pass, scaled and turned for the other.
    /// </summary>
    private static LayerTransform Place(double x, double y, double width, double height, bool scaled)
    {
        var transform = new LayerTransform(new Point(x, y), new Size(width, height))
        {
            Sampling = scaled ? LayerSampling.High : LayerSampling.Nearest,
        };

        return scaled
            ? transform with { Size = new Size(width * 0.7, height * 0.7), Rotation = 11 }
            : transform;
    }

    private static PixelBuffer Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
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

    private static PixelBuffer Checkerboard(int width, int height)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < width; x++)
            {
                bool dark = ((x / 8) + (y / 8)) % 2 == 0;
                row[x * 4 + 0] = dark ? (byte)40 : (byte)210;
                row[x * 4 + 1] = (byte)(x * 255 / Math.Max(1, width - 1));
                row[x * 4 + 2] = dark ? (byte)200 : (byte)60;
                row[x * 4 + 3] = 255;
            }
        }
        return buffer;
    }

    /// <summary>A round shape that fades out, so soft edges are part of every blend test.</summary>
    private static PixelBuffer Blob(int width, int height, byte tone)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);
        double centreX = width / 2.0, centreY = height / 2.0;
        double radius = Math.Min(centreX, centreY);

        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < width; x++)
            {
                double distance = Math.Sqrt(Math.Pow(x + 0.5 - centreX, 2) + Math.Pow(y + 0.5 - centreY, 2));
                byte alpha = (byte)Math.Clamp(255 * (1 - distance / radius), 0, 255);
                row[x * 4 + 0] = (byte)(tone * alpha / 255);
                row[x * 4 + 1] = (byte)((255 - tone) * alpha / 255);
                row[x * 4 + 2] = (byte)(128 * alpha / 255);
                row[x * 4 + 3] = alpha;
            }
        }
        return buffer;
    }

    private static PixelBuffer Stripes(int width, int height)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < width; x++)
            {
                bool on = (x / 4) % 2 == 0;
                row[x * 4 + 0] = on ? (byte)250 : (byte)10;
                row[x * 4 + 1] = on ? (byte)250 : (byte)10;
                row[x * 4 + 2] = on ? (byte)0 : (byte)250;
                row[x * 4 + 3] = 255;
            }
        }
        return buffer;
    }

    /// <summary>Coverage rising left to right, which a mask read from the wrong channel gets wrong.</summary>
    private static PixelBuffer Ramp(int width, int height)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < width; x++)
            {
                byte level = (byte)(x * 255 / Math.Max(1, width - 1));
                row[x * 4 + 0] = level;
                row[x * 4 + 1] = level;
                row[x * 4 + 2] = level;
                row[x * 4 + 3] = 255;
            }
        }
        return buffer;
    }
}
