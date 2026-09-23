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
    /// <summary>
    /// How far two renders are apart, split by whether the pixel sits on an edge.
    /// </summary>
    /// <remarks>
    /// The split is what makes this measurement mean anything once resampling is involved. Two
    /// different filters must disagree where an image has an edge, and there is no version of this
    /// port that changes that. What they must not disagree about is anywhere else: if the geometry
    /// is right, a flat area resamples to the same colour under any filter. So
    /// <see cref="FlatMax"/> is the one to watch — a half-pixel offset, a transposed matrix or a
    /// wrong centre would all light it up, while a different filter leaves it at zero.
    /// </remarks>
    internal readonly record struct Difference(int Max, double Mean, int FlatMax, double EdgeMean, long Pixels)
    {
        public override string ToString() =>
            $"max {Max}, mean {Mean:F2}, flat max {FlatMax}, edge mean {EdgeMean:F2} over {Pixels} pixels";
    }

    internal sealed record Result(Difference Exact, Difference Placement, Difference Resampled,
                                  Difference Adjusted, Difference Previewed, string BackendName);

    public static Result Run(GraphicsDevice device, string? imageFolder)
    {
        using var software = new SoftwareRenderBackend();
        using var hardware = new Direct2DBackend(device);

        Difference exact = Measure(software, hardware, imageFolder, "exact", () => Scene(scaled: false));
        Difference placement = Measure(software, hardware, imageFolder, "placement", PlacementOnly);
        Difference resampled = Measure(software, hardware, imageFolder, "resampled", () => Scene(scaled: true));
        Difference adjusted = Measure(software, hardware, imageFolder, "adjusted", Adjusted);
        Difference previewed = Previewed(software, hardware, imageFolder);

        return new Result(exact, placement, resampled, adjusted, previewed, hardware.Name);
    }

    /// <summary>Builds a scene, compares it, and releases the pixels it allocated.</summary>
    private static Difference Measure(IRenderBackend software, IRenderBackend hardware,
                                      string? imageFolder, string name,
                                      Func<(CanvasDocument Document, List<PixelBuffer> Owned)> build)
    {
        (CanvasDocument document, List<PixelBuffer> owned) = build();
        try
        {
            return Compare(document, software, hardware, imageFolder, name);
        }
        finally
        {
            foreach (PixelBuffer buffer in owned) buffer.Release();
        }
    }

    /// <summary>
    /// The two previews that stand in for a layer while it is being edited — a filter being tried
    /// and a distortion being dragged — drawn by both backends.
    /// </summary>
    /// <remarks>
    /// Both hand the compositor pixels that are not the layer's own grid, placed somewhere the layer
    /// is not, and the layer's own mask then goes on as a clip in document space instead. That is
    /// the path this checks; the pixels themselves come from Core and are the same for both. The
    /// layers are Nearest, so the exact pass's tolerance applies.
    /// </remarks>
    private static Difference Previewed(IRenderBackend software, IRenderBackend hardware, string? imageFolder)
    {
        const int Size = 128;
        PixelBuffer backdrop = Checkerboard(Size, Size), subject = Ramp(56, 40), mask = Blob(56, 40, 255);
        PixelBuffer other = Stripes(40, 32);
        try
        {
            var filtered = new ImageLayer
            {
                Id = Guid.NewGuid(),
                Name = "Filtered",
                Image = subject,
                Transform = Place(12, 16, 56, 40, scaled: false),
                Mask = new LayerMask { Coverage = mask },
            };
            var distorted = new ImageLayer
            {
                Id = Guid.NewGuid(),
                Name = "Distorted",
                Image = other,
                Transform = Place(70, 70, 40, 32, scaled: false),
            };
            var document = new CanvasDocument
            {
                Id = Guid.NewGuid(),
                Width = Size,
                Height = Size,
                Layers = new EquatableList<ImageLayer>(
                [
                    new ImageLayer
                    {
                        Id = Guid.NewGuid(), Name = "Backdrop", Image = backdrop,
                        Transform = Place(0, 0, Size, Size, scaled: false),
                    },
                    filtered,
                    distorted,
                ]),
            };

            using var blur = new FilterPreview(filtered, FilterKind.GaussianBlur, new FilterSettings { Radius = 3 });
            using var warp = new DistortPreview(distorted);
            IReadOnlyList<Point> corners = [new(66, 72), new(118, 64), new(122, 110), new(72, 100)];

            Difference blurred = Compare(document, software, hardware, imageFolder, "preview-filter",
                                         () => blur.Frame(CanvasProjection.Identity, Size, Size));
            Difference warped = Compare(document, software, hardware, imageFolder, "preview-distort",
                                        () => warp.Frame(corners, CanvasProjection.Identity, Size, Size));

            return blurred.Max >= warped.Max ? blurred : warped;
        }
        finally
        {
            backdrop.Release();
            subject.Release();
            mask.Release();
            other.Release();
        }
    }

    private static Difference Compare(CanvasDocument document, IRenderBackend software,
                                      IRenderBackend hardware, string? imageFolder, string name,
                                      Func<LiveEdit?>? live = null)
    {
        using PixelBuffer reference = Render(document, software, live?.Invoke());
        using PixelBuffer actual = Render(document, hardware, live?.Invoke());

        if (imageFolder is not null)
        {
            Directory.CreateDirectory(imageFolder);
            File.WriteAllBytes(Path.Combine(imageFolder, $"{name}-software.png"), Png.Encode(reference));
            File.WriteAllBytes(Path.Combine(imageFolder, $"{name}-direct2d.png"), Png.Encode(actual));
        }

        int max = 0, flatMax = 0;
        long total = 0, samples = 0, edgeTotal = 0, edgeSamples = 0;

        for (int y = 0; y < reference.Height; y++)
        {
            Span<byte> a = reference.Row(y);
            Span<byte> b = actual.Row(y);

            for (int x = 0; x < reference.Width; x++)
            {
                bool edge = OnAnEdge(reference, x, y);

                for (int channel = 0; channel < 4; channel++)
                {
                    int difference = Math.Abs(a[x * 4 + channel] - b[x * 4 + channel]);
                    max = Math.Max(max, difference);
                    total += difference;
                    samples++;

                    if (edge) { edgeTotal += difference; edgeSamples++; }
                    else flatMax = Math.Max(flatMax, difference);
                }
            }
        }

        return new Difference(max,
                              samples == 0 ? 0 : (double)total / samples,
                              flatMax,
                              edgeSamples == 0 ? 0 : (double)edgeTotal / edgeSamples,
                              (long)reference.Width * reference.Height);
    }

    private static PixelBuffer Render(CanvasDocument document, IRenderBackend backend, LiveEdit? live)
    {
        if (live is null) return LayerCompositor.Render(document, backend);

        using IRenderSurface surface = backend.CreateSurface(document.Width, document.Height);
        surface.Clear();
        LayerCompositor.Draw(document, surface, backend, CanvasProjection.Identity, live);
        return surface.Read();
    }

    /// <summary>Whether anything around this pixel changes enough for a filter to matter.</summary>
    private static bool OnAnEdge(PixelBuffer pixels, int x, int y)
    {
        const int Threshold = 24;
        int low = 255, high = 0;

        for (int dy = -1; dy <= 1; dy++)
        {
            int row = y + dy;
            if (row < 0 || row >= pixels.Height) continue;
            Span<byte> values = pixels.Row(row);

            for (int dx = -1; dx <= 1; dx++)
            {
                int column = x + dx;
                if (column < 0 || column >= pixels.Width) continue;

                for (int channel = 0; channel < 4; channel++)
                {
                    int value = values[column * 4 + channel];
                    low = Math.Min(low, value);
                    high = Math.Max(high, value);
                }
            }
        }

        return high - low > Threshold;
    }

    /// <summary>
    /// A document that exercises the compositing rules rather than looking like anything.
    /// </summary>
    /// <remarks>
    /// Every layer below is here to catch a specific mistake: a blend mode wired to the wrong one,
    /// a mask read from the wrong channel, a folder mask that stops at its own folder, a clipping
    /// group that thickens the base's edge.
    /// </remarks>
    /// <summary>
    /// One layer, scaled and turned, in Normal with nothing else.
    /// </summary>
    /// <remarks>
    /// Worth separating out. A raised average over the whole scene could be a resampling filter
    /// that differs by design, or a half-pixel offset that does not — and only one of those is a
    /// defect. With a single layer and no blending, whatever shows up here is the placement.
    /// </remarks>
    private static (CanvasDocument, List<PixelBuffer>) PlacementOnly()
    {
        const int Size = 128;
        PixelBuffer image = Checkerboard(Size, Size);

        var document = new CanvasDocument
        {
            Id = Guid.NewGuid(),
            Width = Size,
            Height = Size,
            Layers = new EquatableList<ImageLayer>(
            [
                new ImageLayer
                {
                    Id = Guid.NewGuid(),
                    Name = "Placed",
                    Image = image,
                    Transform = Place(0, 0, Size, Size, scaled: true),
                },
            ]),
        };

        return (document, [image]);
    }

    /// <summary>
    /// The exact scene with every kind of adjustment layer over it, one masked, one clipped, one
    /// blended.
    /// </summary>
    /// <remarks>
    /// Both backends run the same Core code over their own composite, so what this measures is the
    /// read, the write and the weight drawing around it — and how far each adjustment stretches the
    /// small arithmetic differences the exact pass already allows. Levels' black point and the
    /// Curves bend make those a little larger, which is why this pass has a tolerance of its own.
    /// </remarks>
    private static (CanvasDocument, List<PixelBuffer>) Adjusted()
    {
        (CanvasDocument document, List<PixelBuffer> owned) = Scene(scaled: false);
        int size = document.Width;

        PixelBuffer maskRamp = Ramp(size, size);
        owned.Add(maskRamp);

        ImageLayer clipBase = document.Layers.First(layer => layer.Name == "Clip base");
        var full = new LayerTransform(Point.Zero, new Size(size, size));

        ImageLayer Adjustment(LayerAdjustment adjustment) => new()
        {
            Id = Guid.NewGuid(),
            Name = adjustment.Kind.ToString(),
            Transform = full,
            Adjustment = adjustment,
        };

        // The clipped one has to sit directly above the clipped layer to join its group.
        var layers = document.Layers.ToList();
        int clipped = layers.FindIndex(layer => layer.Name == "Clipped");
        layers.Insert(clipped + 1, Adjustment(new LayerAdjustment(AdjustmentKind.Hsv)
        {
            HsvSettings = HueSaturationSettings.From(90, 20, 0, colorize: false),
        }) with { MaskSourceId = clipBase.Id });

        layers.Add(Adjustment(new LayerAdjustment(AdjustmentKind.Levels)
        {
            Levels = new LevelsSettings
            {
                Ranges = new EquatableList<LevelRange>([new LevelRange { Black = 10, Gamma = 1.2 }, new(), new(), new()]),
            },
        }));
        layers.Add(Adjustment(new LayerAdjustment(AdjustmentKind.Exposure)
        {
            ExposureSettings = new ExposureSettings { Exposure = 0.3 },
        }) with { BlendMode = LayerBlendMode.Screen, Opacity = 0.7 });
        layers.Add(Adjustment(new LayerAdjustment(AdjustmentKind.GradientMap)
        {
            GradientMapSettings = new GradientMapSettings { Shadows = new AdjustmentColor(0.2, 0, 0.4) },
        }) with { Mask = new LayerMask { Coverage = maskRamp }, Opacity = 0.6 });
        layers.Add(Adjustment(new LayerAdjustment(AdjustmentKind.Grain)
        {
            GrainSettings = new GrainSettings { Amount = 40, Size = 2, Seed = 9 },
        }));

        return (document with { Layers = new EquatableList<ImageLayer>(layers) }, owned);
    }

    private static (CanvasDocument, List<PixelBuffer>) Scene(bool scaled)
    {
        const int Size = 128;
        var layers = new List<ImageLayer>();
        var owned = new List<PixelBuffer>();

        PixelBuffer backdrop = Checkerboard(Size, Size);
        owned.Add(backdrop);
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
            PixelBuffer blob = Blob(16, 16, (byte)(30 + i * 28));
            owned.Add(blob);
            layers.Add(new ImageLayer
            {
                Id = Guid.NewGuid(),
                Name = modes[i].ToString(),
                Image = blob,
                Transform = Place(i * 16, 8, 16, 16, scaled),
                BlendMode = modes[i],
                Opacity = 0.8,
            });
        }

        // A masked layer, a folder with a mask over two layers, and a clipping group.
        PixelBuffer masked = Solid(48, 32, 255, 64, 0), maskedRamp = Ramp(48, 32);
        PixelBuffer folderRamp = Ramp(Size, Size), inFolder = Solid(40, 24, 0, 200, 255);
        PixelBuffer clipBase = Blob(56, 40, 200), clipStripes = Stripes(56, 40);
        owned.AddRange([masked, maskedRamp, folderRamp, inFolder, clipBase, clipStripes]);

        Guid folderId = Guid.NewGuid();
        layers.Add(new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = "Masked",
            Image = masked,
            Transform = Place(8, 48, 48, 32, scaled),
            Mask = new LayerMask { Coverage = maskedRamp },
        });

        layers.Add(new ImageLayer
        {
            Id = folderId,
            Name = "Folder",
            IsGroup = true,
            Transform = Place(0, 0, Size, Size, scaled),
            Mask = new LayerMask { Coverage = folderRamp },
        });
        layers.Add(new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = "In folder",
            Image = inFolder,
            Transform = Place(64, 48, 40, 24, scaled),
            ParentId = folderId,
        });

        // A mask moved apart from its layer, half off it: past its own box it shows its edge's level.
        PixelBuffer apart = Solid(40, 24, 120, 0, 220), apartRamp = Ramp(24, 16);
        owned.AddRange([apart, apartRamp]);
        layers.Add(new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = "Mask apart",
            Image = apart,
            Transform = Place(84, 88, 40, 24, scaled),
            Mask = new LayerMask { Coverage = apartRamp, Placement = Place(96, 92, 24, 16, scaled), IsLinked = false },
        });

        Guid baseId = Guid.NewGuid();
        layers.Add(new ImageLayer
        {
            Id = baseId,
            Name = "Clip base",
            Image = clipBase,
            Transform = Place(16, 84, 56, 40, scaled),
        });
        layers.Add(new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = "Clipped",
            Image = clipStripes,
            Transform = Place(16, 84, 56, 40, scaled),
            MaskSourceId = baseId,
        });

        var document = new CanvasDocument
        {
            Id = Guid.NewGuid(),
            Width = Size,
            Height = Size,
            Layers = new EquatableList<ImageLayer>(layers),
        };

        return (document, owned);
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
