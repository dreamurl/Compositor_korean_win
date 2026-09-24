using System.Text;
using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// Photoshop documents in both directions.
/// </summary>
/// <remarks>
/// <para>
/// The files under <c>Fixtures/psd</c> were saved by Photoshop and come from psd-tools' test suite
/// (MIT; the licence sits beside them). Every PSD carries the flattened image Photoshop drew for
/// it, so an import is measured against Photoshop itself: the imported layers, composited here,
/// must come out as that image.
/// </para>
/// <para>
/// Exports are read back both here and, in CI, by psd-tools — an independent reader — from the
/// copies these tests leave in <c>build/psd-out</c>.
/// </para>
/// </remarks>
public sealed class PsdTests
{
    private static string Fixtures => Path.Combine(RepositoryRoot(), "tests", "Compositor_korean_win.Core.Tests", "Fixtures", "psd");

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(Fixtures, name));

    // ---- Reading what Photoshop wrote -------------------------------------------------------

    [Fact]
    public void GroupsBecomeFoldersWithTheirMembersInside()
    {
        using var opened = Open("group.psd");
        CanvasDocument document = opened.Document;

        Assert.Equal(100, document.Width);
        Assert.Equal(200, document.Height);
        Assert.Equal(["Background", "Group 1", "Shape 1"], document.Layers.Select(layer => layer.Name));

        ImageLayer folder = document.Layers[1];
        Assert.True(folder.IsGroup);
        Assert.Equal(folder.Id, document.Layers[2].ParentId);
        Assert.Null(document.Layers[0].ParentId);
    }

    [Fact]
    public void AClippedLayerClipsToTheLayerUnderIt()
    {
        using var opened = Open("clipping-mask.psd");
        ImageLayer clipped = Named(opened.Document, "Shape 2");
        ImageLayer baseLayer = Named(opened.Document, "Shape 1");

        Assert.Equal(baseLayer.Id, clipped.MaskSourceId);
        Assert.Null(baseLayer.MaskSourceId);
        Assert.Equal(baseLayer.ParentId, clipped.ParentId);
    }

    [Fact]
    public void AClipToAFolderIsDroppedAndSaidSo()
    {
        using var opened = Open("clipping-mask4.psd");
        Assert.Null(Named(opened.Document, "Rectangle 2").MaskSourceId);
        Assert.True(opened.Notes.ContainsKey(PsdNote.ClippingDropped));
        Assert.True(opened.Notes.ContainsKey(PsdNote.BlendModeReplaced));
    }

    [Fact]
    public void MasksKeepTheirPixelsAndWhetherTheyAreOn()
    {
        using var enabled = Open("mask.psd");
        using var disabled = Open("mask-disabled.psd");

        LayerMask on = Named(enabled.Document, "Background copy").Mask!;
        LayerMask off = Named(disabled.Document, "Background copy").Mask!;
        Assert.True(on.IsEnabled);
        Assert.False(off.IsEnabled);
        Assert.True(on.Coverage.Width > 1);
    }

    [Fact]
    public void HiddenLayersAndFoldersStayHidden()
    {
        using var layers = Open("hidden-layer.psd");
        using var folders = Open("hidden-groups.psd");

        Assert.False(Named(layers.Document, "Shape 2").IsVisible);
        Assert.True(Named(layers.Document, "Shape 1").IsVisible);
        Assert.False(Named(folders.Document, "Group 1").IsVisible);
        Assert.True(Named(folders.Document, "Group 2").IsVisible);
    }

    [Fact]
    public void NamesComeFromTheirUnicodeCopy()
    {
        using var cyrillic = Open("2layers.psd");
        using var emoji = Open("layer-name-emoji.psd");

        Assert.Equal(["Фон", "Слой"], cyrillic.Document.Layers.Select(layer => layer.Name));
        Assert.Equal("👽", emoji.Document.Layers.Single().Name);
    }

    [Fact]
    public void AnUnsharedBlendModeIsDrawnAsNormalAndSaidSo()
    {
        using var opened = Open("layer-name-emoji.psd");
        ImageLayer layer = opened.Document.Layers.Single();

        Assert.Equal(LayerBlendMode.Normal, layer.BlendMode);
        Assert.Equal(128 / 255.0, layer.Opacity, 3);
        Assert.Equal(1, opened.Notes[PsdNote.BlendModeReplaced]);
    }

    [Fact]
    public void SharedBlendModesComeAcross()
    {
        using var multiply = Open("blend-multiply.psd");
        using var luminosity = Open("blend-luminosity.psd");

        Assert.All(multiply.Document.Layers, layer => Assert.Equal(LayerBlendMode.Multiply, layer.BlendMode));
        Assert.All(luminosity.Document.Layers, layer => Assert.Equal(LayerBlendMode.Luminosity, layer.BlendMode));
        Assert.Empty(multiply.Notes);
    }

    [Fact]
    public void AdjustmentLayersKeepPhotoshopsValues()
    {
        using var levels = Open("levels_rgb.psd");
        LevelRange composite = Named(levels.Document, "Levels 4").Adjustment!.Levels.Ranges[0];
        Assert.Equal(48, composite.Black);
        Assert.Equal(135, composite.White);
        Assert.Equal(74, composite.OutputBlack);
        Assert.Equal(141, composite.OutputWhite);
        Assert.Equal(1, composite.Gamma);

        using var exposure = Open("exposure_rgb.psd");
        ExposureSettings settings = Named(exposure.Document, "Exposure 1").Adjustment!.Exposure;
        Assert.Equal(2.03, settings.Exposure, 3);
        Assert.Equal(0.0775, settings.Offset, 4);
        Assert.Equal(1.52, settings.Gamma, 3);

        using var curves = Open("curves_rgb.psd");
        LayerAdjustment curve = Named(curves.Document, "Curves 2").Adjustment!;
        Assert.Equal(AdjustmentKind.Curves, curve.Kind);
        Assert.True(curve.Curves.IsValid);

        using var hue = Open("huesaturation_rgb.psd");
        HueSaturationSettings hsv = Named(hue.Document, "Hue/Saturation 2").Adjustment!.ResolvedHsv;
        Assert.Equal(-27, hsv.Adjustments[ColorRange.Master].Hue);
        Assert.Equal(10, hsv.Adjustments[ColorRange.Master].Lightness);
        Assert.Equal(93, hsv.Adjustments[ColorRange.Yellows].Saturation);
    }

    [Fact]
    public void DeepDocumentsAndPsbOpen()
    {
        using var sixteen = Open("16bit5x5.psd");
        using var thirtyTwo = Open("32bit5x5.psd");
        using var big = Open("1layer.psb");

        Assert.Equal(3, sixteen.Document.Layers.Count);
        Assert.Equal(3, thirtyTwo.Document.Layers.Count);
        Assert.Equal(new PixelRectSize(1, 3), Dimensions(Named(sixteen.Document, "Background copy 2")));
        Assert.Equal("Фон", big.Document.Layers.Single().Name);
    }

    [Fact]
    public void TypeAndSmartObjectsArriveAsTheirPixels()
    {
        using var type = Open("text.psd");
        using var placed = Open("placedLayer.psd");

        Assert.NotNull(type.Document.Layers[1].Image);
        Assert.True(type.Notes.ContainsKey(PsdNote.TypeRasterized));
        Assert.True(placed.Notes.ContainsKey(PsdNote.SmartObjectRasterized));
    }

    [Fact]
    public void AFileWithoutLayersOpensAsItsFlattenedImage()
    {
        using var opened = Open("0layers.psd");
        Assert.Single(opened.Document.Layers);
        Assert.True(opened.Notes.ContainsKey(PsdNote.FlattenedOnly));
    }

    [Fact]
    public void AGroupWithItsOwnOpacityIsMergedIntoOneLayer()
    {
        using var opened = Open("passthrough_opacity.psd");
        ImageLayer merged = Named(opened.Document, "Group 1");
        Assert.False(merged.IsGroup);
        Assert.Equal(128 / 255.0, merged.Opacity, 3);
        Assert.True(opened.Notes.ContainsKey(PsdNote.GroupMerged));
    }

    /// <summary>
    /// The files whose every feature is one this editor has: once imported and composited here they
    /// must look as Photoshop drew them.
    /// </summary>
    public static TheoryData<string> Faithful => new()
    {
        "1layer.psd", "2layers.psd", "1layer.psb", "group.psd", "clipping-mask.psd", "mask.psd",
        "mask-disabled.psd", "hidden-layer.psd", "hidden-groups.psd", "opacity-fill.psd", "16bit5x5.psd",
        "32bit5x5.psd", "gray0.psd", "empty-layer.psd", "semi-transparent-layers.psd", "text.psd",
        "placedLayer.psd", "levels_rgb.psd", "curves_rgb.psd", "huesaturation_rgb.psd", "exposure_rgb.psd",
        "blend-multiply.psd", "blend-screen.psd", "blend-overlay.psd", "blend-color-dodge.psd",
        "blend-color-burn.psd", "blend-difference.psd", "blend-hue.psd", "blend-color.psd",
        "blend-luminosity.psd", "blend-pass-through.psd",
    };

    [Theory]
    [MemberData(nameof(Faithful))]
    public void AnImportLooksAsPhotoshopDrewIt(string name)
    {
        byte[] data = Fixture(name);
        using var opened = new Opened(PsdImport.Read(data));
        using PixelBuffer photoshop = PsdImport.Composite(data);
        using PixelBuffer ours = Render(opened.Document);

        (double mean, double off) = Difference(photoshop, ours);
        Assert.True(mean <= 2.5 && off <= 0.02,
            $"{name}: mean difference {mean:F2} a channel, {off:P2} of pixels off by more than 16");
    }

    // ---- Writing what Photoshop will read ---------------------------------------------------

    [Fact]
    public void ADocumentComesBackFromItsPsdLookingTheSame()
    {
        CanvasDocument document = Rich();
        try
        {
            byte[] psd = Export(document, "rich.psd", out _);
            using var back = new Opened(PsdImport.Read(psd));

            using PixelBuffer before = Render(document);
            using PixelBuffer after = Render(back.Document);
            (double mean, double off) = Difference(before, after);
            Assert.True(mean <= 1.5 && off <= 0.01, $"round trip: mean {mean:F2}, {off:P2} off");

            // The flattened image written beside the layers is the document too.
            using PixelBuffer flattened = PsdImport.Composite(psd);
            (mean, off) = Difference(before, flattened);
            Assert.True(mean <= 1 && off <= 0.005, $"flattened image: mean {mean:F2}, {off:P2} off");
        }
        finally
        {
            Release(document);
        }
    }

    [Fact]
    public void TheStructureComesBackToo()
    {
        CanvasDocument document = Rich();
        try
        {
            byte[] psd = Export(document, "structure.psd", out IReadOnlyDictionary<PsdNote, int> notes);
            using var back = new Opened(PsdImport.Read(psd));
            CanvasDocument read = back.Document;

            Assert.Equal(document.Layers.Select(layer => layer.Name), read.Layers.Select(layer => layer.Name));
            Assert.Empty(back.Notes);
            Assert.True(notes.ContainsKey(PsdNote.GrainDropped) is false);

            ImageLayer folder = Named(read, "폴더");
            Assert.True(folder.IsGroup);
            Assert.NotNull(folder.Mask);
            Assert.Equal(folder.Id, Named(read, "안").ParentId);
            Assert.Equal(Named(read, "안").Id, Named(read, "클립").MaskSourceId);

            Assert.False(Named(read, "숨김").IsVisible);
            Assert.Null(Named(read, "빈").Image);

            ImageLayer turned = Named(read, "회전");
            Assert.Equal(LayerBlendMode.Multiply, turned.BlendMode);
            Assert.Equal(0.8, turned.Opacity, 2);

            Assert.Equal(Named(document, "레벨").Adjustment!.Levels, Named(read, "레벨").Adjustment!.Levels);
            Assert.Equal(Named(document, "커브").Adjustment!.Curves, Named(read, "커브").Adjustment!.Curves);
            Assert.Equal(Named(document, "노출").Adjustment!.Exposure.Exposure, Named(read, "노출").Adjustment!.Exposure.Exposure, 4);
            Assert.Equal(Named(document, "그레이디언트").Adjustment!.GradientMap.Highlights.Red,
                         Named(read, "그레이디언트").Adjustment!.GradientMap.Highlights.Red, 3);

            HueSaturationSettings hueBefore = Named(document, "색조").Adjustment!.ResolvedHsv;
            HueSaturationSettings hueAfter = Named(read, "색조").Adjustment!.ResolvedHsv;
            Assert.Equal(hueBefore.Adjustments[ColorRange.Master], hueAfter.Adjustments[ColorRange.Master]);
            Assert.Equal(hueBefore.Adjustments[ColorRange.Reds], hueAfter.Adjustments[ColorRange.Reds]);

            LayerEffects effects = Named(read, "효과").Effects!;
            LayerEffects original = Named(document, "효과").Effects!;
            Assert.Equal(original.Shadow!.Distance, effects.Shadow!.Distance, 3);
            Assert.Equal(original.Shadow.Angle, effects.Shadow.Angle, 3);
            Assert.Equal(original.Stroke!.Size, effects.Stroke!.Size, 3);
            Assert.Equal(original.Stroke.Position, effects.Stroke.Position);
            Assert.Equal(original.Glow!.Size, effects.Glow!.Size, 3);
        }
        finally
        {
            Release(document);
        }
    }

    [Fact]
    public void GrainIsLeftOutAndSaidSo()
    {
        CanvasDocument document = ProjectFixture.Document(
            ProjectFixture.Layer("바탕", RenderFixture.Solid(8, 6, 200, 100, 50)),
            new ImageLayer
            {
                Id = Guid.NewGuid(),
                Name = "그레인",
                Transform = new LayerTransform(Point.Zero, new Size(64, 48)),
                Adjustment = new LayerAdjustment(AdjustmentKind.Grain),
            });
        try
        {
            byte[] psd = Export(document, "grain.psd", out IReadOnlyDictionary<PsdNote, int> notes);
            using var back = new Opened(PsdImport.Read(psd));
            Assert.Equal(1, notes[PsdNote.GrainDropped]);
            Assert.Single(back.Document.Layers);
        }
        finally
        {
            Release(document);
        }
    }

    [Theory]
    [MemberData(nameof(Faithful))]
    public void PhotoshopsFilesSurviveGoingOutAgain(string name)
    {
        using var first = new Opened(PsdImport.Read(Fixture(name)));
        byte[] psd = Export(first.Document, "again-" + Path.GetFileNameWithoutExtension(name) + ".psd", out _);
        using var second = new Opened(PsdImport.Read(psd));

        using PixelBuffer before = Render(first.Document);
        using PixelBuffer after = Render(second.Document);
        (double mean, double off) = Difference(before, after);
        Assert.True(mean <= 1.5 && off <= 0.01, $"{name} after export: mean {mean:F2}, {off:P2} off");
    }

    [Fact]
    public void ANonPsdIsRefused()
    {
        Assert.False(PsdImport.IsPsd(Encoding.ASCII.GetBytes("\x89PNG\r\n")));
        Assert.True(PsdImport.IsPsd(Fixture("1layer.psd")));
        Assert.ThrowsAny<ProjectException>(() => PsdImport.Read(Encoding.ASCII.GetBytes("8BPS-not-really")));
    }

    // ---- Helpers ------------------------------------------------------------------------------

    /// <summary>
    /// A document with one of everything a PSD can carry: a rotated and scaled layer in Multiply
    /// at 80%, a folder with a mask holding a layer and one clipped to it, a masked layer, a hidden
    /// one, a blank one, all five shared adjustments and all three effects, with Korean names.
    /// </summary>
    private static CanvasDocument Rich()
    {
        PixelBuffer background = PixelBuffer.Allocate(64, 48);
        for (int y = 0; y < 48; y++)
        {
            Span<byte> row = background.Row(y);
            for (int x = 0; x < 64; x++)
            {
                row[x * 4] = (byte)(x * 4);
                row[x * 4 + 1] = (byte)(y * 5);
                row[x * 4 + 2] = 160;
                row[x * 4 + 3] = 255;
            }
        }

        Guid folder = Guid.NewGuid(), inside = Guid.NewGuid();
        var layers = new List<ImageLayer>
        {
            Placed("바탕", background, 0, 0, 64, 48),
            Placed("회전", RenderFixture.Solid(20, 10, 230, 40, 40), 10, 8, 30, 14) with
            {
                Transform = new LayerTransform(new Point(10, 8), new Size(30, 14)) { Rotation = 30 },
                BlendMode = LayerBlendMode.Multiply,
                Opacity = 0.8,
            },
            new()
            {
                Id = folder,
                Name = "폴더",
                Transform = new LayerTransform(Point.Zero, new Size(64, 48)),
                IsGroup = true,
                Mask = new LayerMask { Coverage = RenderFixture.Coverage(64, 48, (x, _) => x < 40 ? (byte)255 : (byte)0) },
            },
            Placed("안", RenderFixture.Solid(16, 16, 40, 60, 220), 30, 20, 16, 16, inside) with { ParentId = folder },
            Placed("클립", RenderFixture.Solid(40, 6, 250, 250, 0), 20, 26, 40, 6) with { ParentId = folder, MaskSourceId = inside },
            Placed("가림", RenderFixture.Solid(12, 12, 0, 200, 0), 4, 30, 12, 12) with
            {
                Mask = new LayerMask { Coverage = RenderFixture.Coverage(12, 12, (x, y) => (byte)(x * 20)) },
            },
            Placed("숨김", RenderFixture.Solid(10, 10, 255, 0, 255), 50, 2, 10, 10) with { IsVisible = false },
            new() { Id = Guid.NewGuid(), Name = "빈", Transform = new LayerTransform(Point.Zero, new Size(64, 48)) },
            Placed("효과", RenderFixture.Solid(10, 8, 255, 255, 255), 44, 30, 10, 8) with
            {
                Effects = new LayerEffects
                {
                    Shadow = new ShadowEffect { Distance = 4, Size = 3, Angle = 135 },
                    Glow = new GlowEffect { Size = 5, Spread = 0.2 },
                    Stroke = new StrokeEffect { Size = 2, Position = StrokePosition.Center, Red = 1 },
                },
            },
            Adjusted("레벨", new LayerAdjustment(AdjustmentKind.Levels)
            {
                Levels = new LevelsSettings
                {
                    Ranges = new EquatableList<LevelRange>([
                        new LevelRange { Black = 10, White = 240, Gamma = 1.2 },
                        new LevelRange(), new LevelRange { OutputBlack = 20 }, new LevelRange(),
                    ]),
                },
            }),
            Adjusted("커브", new LayerAdjustment(AdjustmentKind.Curves)
            {
                Curves = new CurvesSettings
                {
                    Channels = new EquatableList<EquatableList<CurvePoint>>([
                        new([new CurvePoint(0, 0), new CurvePoint(128, 150), new CurvePoint(255, 255)]),
                        new([new CurvePoint(0, 0), new CurvePoint(255, 255)]),
                        new([new CurvePoint(0, 10), new CurvePoint(255, 245)]),
                        new([new CurvePoint(0, 0), new CurvePoint(255, 255)]),
                    ]),
                },
            }),
            Adjusted("색조", new LayerAdjustment(AdjustmentKind.Hsv)
            {
                HsvSettings = new HueSaturationSettings
                {
                    Adjustments = new ColorRangeMap<RangeAdjustment>([
                        new(ColorRange.Master, new RangeAdjustment(15, -10, 5)),
                        new(ColorRange.Reds, new RangeAdjustment(-20, 30, 0)),
                    ]),
                },
            }),
            Adjusted("노출", new LayerAdjustment(AdjustmentKind.Exposure)
            {
                ExposureSettings = new ExposureSettings { Exposure = 0.5, Offset = 0.01, Gamma = 1.1 },
            }),
            Adjusted("그레이디언트", new LayerAdjustment(AdjustmentKind.GradientMap)
            {
                GradientMapSettings = new GradientMapSettings
                {
                    Shadows = new AdjustmentColor(0.1, 0, 0.3),
                    Highlights = new AdjustmentColor(1, 0.9, 0.6),
                },
            }) with { Opacity = 0.3 },
        };

        return new CanvasDocument
        {
            Id = Guid.NewGuid(),
            Width = 64,
            Height = 48,
            Resolution = 150,
            Layers = new EquatableList<ImageLayer>(layers),
        };

        static ImageLayer Placed(string name, PixelBuffer image, double x, double y, double width, double height, Guid? id = null) => new()
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            Transform = new LayerTransform(new Point(x, y), new Size(width, height)),
            Image = image,
        };

        static ImageLayer Adjusted(string name, LayerAdjustment adjustment) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Transform = new LayerTransform(Point.Zero, new Size(64, 48)),
            Adjustment = adjustment,
        };
    }

    /// <summary>Writes a PSD, keeping a copy under build/psd-out for CI's independent reader.</summary>
    private static byte[] Export(CanvasDocument document, string name, out IReadOnlyDictionary<PsdNote, int> notes)
    {
        using var memory = new MemoryStream();
        notes = PsdExport.Write(document, memory);
        byte[] bytes = memory.ToArray();

        string output = Path.Combine(RepositoryRoot(), "build", "psd-out");
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, name), bytes);
        return bytes;
    }

    private static PixelBuffer Render(CanvasDocument document)
    {
        using var backend = new SoftwareRenderBackend();
        return LayerCompositor.Render(document, backend);
    }

    /// <summary>
    /// How far apart two renderings are: the mean difference of a premultiplied channel, and the
    /// share of pixels where some channel is off by more than 16.
    /// </summary>
    private static (double Mean, double Off) Difference(PixelBuffer a, PixelBuffer b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);

        long total = 0, off = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<byte> rowA = a.Row(y), rowB = b.Row(y);
            for (int x = 0; x < a.Width; x++)
            {
                int worst = 0;
                for (int c = 0; c < 4; c++)
                {
                    int d = Math.Abs(rowA[x * 4 + c] - rowB[x * 4 + c]);
                    total += d;
                    worst = Math.Max(worst, d);
                }
                if (worst > 16) off++;
            }
        }
        long pixels = (long)a.Width * a.Height;
        return ((double)total / (pixels * 4), (double)off / pixels);
    }

    private readonly record struct PixelRectSize(int Width, int Height);

    private static PixelRectSize Dimensions(ImageLayer layer) => new(layer.Image!.Width, layer.Image.Height);

    private static ImageLayer Named(CanvasDocument document, string name) =>
        document.Layers.Single(layer => layer.Name == name);

    private static Opened Open(string name) => new(PsdImport.Read(Fixture(name)));

    private static void Release(CanvasDocument document)
    {
        foreach (ImageLayer layer in document.Layers)
        {
            layer.Image?.Release();
            layer.Mask?.Coverage.Release();
        }
    }

    /// <summary>An imported document whose pixels are released at the end of the test.</summary>
    private sealed class Opened(PsdImportResult result) : IDisposable
    {
        public CanvasDocument Document => result.Document;
        public IReadOnlyDictionary<PsdNote, int> Notes => result.Notes;
        public void Dispose() => Release(result.Document);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Compositor_korean_win.sln")))
                return directory.FullName;
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }
}
