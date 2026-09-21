using System.IO.Compression;
using System.Text;
using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// Saving a project and opening it again has to give back what went in.
/// </summary>
/// <remarks>
/// This is the port of upstream's <c>projectRoundTripSurvivesSourceRemovalAndPackageMove</c>, minus
/// the parts that belong to a running editor. What it keeps is the claim that matters: a project is
/// self-contained, so moving the file or deleting the photo it was made from changes nothing.
/// </remarks>
public class ProjectRoundTripTests
{
    [Fact]
    public void SavingAndReopeningKeepsEveryStoredField()
    {
        using var folder = new ProjectFixture.TemporaryFolder();
        PixelBuffer pixels = ProjectFixture.Image();
        PixelBuffer coverage = ProjectFixture.Mask();

        Guid imageId = Guid.NewGuid();
        var layer = ProjectFixture.Layer("Paint & sky 🌤", pixels, imageId) with
        {
            Transform = new LayerTransform
            {
                Origin = new Point(-27.5, 88.25),
                Size = new Size(123, 47),
                Rotation = 38,
                FlipX = true,
                FlipY = true,
                Sampling = LayerSampling.Nearest,
            },
            IsVisible = false,
            Opacity = 0.4,
            BlendMode = LayerBlendMode.ColorBurn,
            Mask = new LayerMask { Coverage = coverage, IsEnabled = false, IsLinked = false },
        };
        ImageLayer blank = ProjectFixture.Layer("Layer 2");

        CanvasDocument document = ProjectFixture.Document(layer, blank);
        ProjectSnapshot snapshot = ProjectMapping.ToSnapshot(document, imageId);

        string original = folder.File("Original.comp");
        string moved = folder.File("Moved.comp");
        ProjectStore.Save(snapshot, original);

        // Moving the file must change nothing: a project names its own assets, never the photo it
        // was imported from.
        File.Move(original, moved);

        ProjectSnapshot loaded = ProjectStore.Load(moved);
        CanvasDocument reopened = ProjectMapping.ToDocument(loaded);

        Assert.Equal(document.Id, reopened.Id);
        Assert.Equal(document.Width, reopened.Width);
        Assert.Equal(document.Height, reopened.Height);
        Assert.Equal(document.Resolution, reopened.Resolution);
        Assert.Equal(imageId, loaded.Manifest.ActiveLayerId);

        Assert.Equal(document.Layers.Select(l => l.Id), reopened.Layers.Select(l => l.Id));
        Assert.Equal(document.Layers.Select(l => l.Name), reopened.Layers.Select(l => l.Name));
        Assert.Equal(document.Layers.Select(l => l.IsVisible), reopened.Layers.Select(l => l.IsVisible));
        Assert.Equal(document.Layers.Select(l => l.Transform), reopened.Layers.Select(l => l.Transform));
        Assert.Equal(document.Layers.Select(l => l.Opacity), reopened.Layers.Select(l => l.Opacity));
        Assert.Equal(document.Layers.Select(l => l.BlendMode), reopened.Layers.Select(l => l.BlendMode));

        ImageLayer restored = reopened.Layers[0];
        Assert.NotNull(restored.Image);
        Assert.False(restored.Mask!.IsEnabled);
        Assert.False(restored.Mask.IsLinked);

        // A blank layer stays blank rather than gaining an empty raster.
        Assert.Null(reopened.Layers[1].Image);

        AssertSamePixels(pixels, restored.Image!);
        AssertSameCoverage(coverage, restored.Mask.Coverage);

        Release(pixels, coverage, restored.Image!, restored.Mask.Coverage);
    }

    [Fact]
    public void AProjectSavedHereIsOneFile()
    {
        using var folder = new ProjectFixture.TemporaryFolder();
        PixelBuffer pixels = ProjectFixture.Image();
        Guid id = Guid.NewGuid();
        CanvasDocument document = ProjectFixture.Document(ProjectFixture.Layer("Layer 1", pixels, id));

        string path = folder.File("One.comp");
        ProjectStore.Save(ProjectMapping.ToSnapshot(document, id), path);

        // Not a directory: Windows Explorer has no package concept, so a folder would be something
        // the user can walk into and break.
        Assert.True(File.Exists(path));
        Assert.False(Directory.Exists(path));

        using ZipArchive archive = ZipFile.OpenRead(path);
        Assert.NotNull(archive.GetEntry("manifest.json"));
        Assert.NotNull(archive.GetEntry($"images/{ManifestValidator.ImageFileName(id)}"));

        pixels.Release();
    }

    [Fact]
    public void ADirectoryPackageFromMacOsOpens()
    {
        using var folder = new ProjectFixture.TemporaryFolder();
        PixelBuffer pixels = ProjectFixture.Image();
        Guid id = Guid.NewGuid();
        CanvasDocument document = ProjectFixture.Document(ProjectFixture.Layer("Layer 1", pixels, id));
        ProjectSnapshot snapshot = ProjectMapping.ToSnapshot(document, id);

        // Laid out by hand exactly as the macOS build writes it: a directory holding manifest.json
        // and an images folder.
        string package = folder.File("FromMac.comp");
        Directory.CreateDirectory(Path.Combine(package, "images"));
        File.WriteAllText(Path.Combine(package, "manifest.json"),
                          ProjectStore.ToJson(snapshot.Manifest), Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(package, "images", ManifestValidator.ImageFileName(id)),
                           Png.Encode(pixels));

        ProjectSnapshot loaded = ProjectStore.Load(package);
        CanvasDocument reopened = ProjectMapping.ToDocument(loaded);

        Assert.Equal(document.Id, reopened.Id);
        Assert.Single(reopened.Layers);
        AssertSamePixels(pixels, reopened.Layers[0].Image!);

        Release(pixels, reopened.Layers[0].Image!);
    }

    [Fact]
    public void UuidsAreWrittenUppercase()
    {
        // Upstream requires an asset to be named exactly "<uuidString>.png", and Foundation's
        // uuidString is uppercase. Lowercase would be rejected by the macOS build.
        ProjectManifest manifest = ProjectFixture.Manifest();
        string json = ProjectStore.ToJson(manifest);

        Assert.Contains(manifest.DocumentId.ToString("D").ToUpperInvariant(), json, StringComparison.Ordinal);
        Assert.DoesNotContain(manifest.DocumentId.ToString("D").ToLowerInvariant(), json, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdjustmentLayerRoundTripsThroughTheManifest()
    {
        var adjustment = new LayerAdjustment(AdjustmentKind.GradientMap)
        {
            GradientMapSettings = new GradientMapSettings
            {
                Shadows = new AdjustmentColor(0.1, 0.2, 0.3),
                Highlights = new AdjustmentColor(0.9, 0.8, 0.7),
                Reversed = true,
            },
            HsvSettings = HueSaturationSettings.From(12, -30, 45, colorize: true, ColorRange.Cyans),
        };

        ProjectManifest manifest = ProjectFixture.Manifest(
            layers: [ProjectFixture.Record("Gradient Map") with { Adjustment = adjustment }]);

        ProjectManifest reparsed = ProjectStore.FromJson(ProjectStore.ToJson(manifest));
        LayerAdjustment restored = reparsed.Layers[0].Adjustment!;

        Assert.Equal(adjustment, restored);
        Assert.Equal(ColorRange.Cyans, restored.ResolvedHsv.Range);
        Assert.Equal(12, restored.ResolvedHsv.Hue);
        Assert.Equal(-30, restored.ResolvedHsv.Saturation);
    }

    [Fact]
    public void HueSaturationRangesAreStoredAsSwiftWritesThem()
    {
        // Swift encodes a dictionary as a JSON object only for String and Int keys. ColorRange is
        // a string-valued enum, which is neither, so upstream's [ColorRange: RangeAdjustment]
        // lands on disk as a flat array of alternating keys and values.
        var settings = HueSaturationSettings.From(5, 10, 15, colorize: false, ColorRange.Reds);
        ProjectManifest manifest = ProjectFixture.Manifest(
            layers:
            [
                ProjectFixture.Record("Hue/Saturation") with
                {
                    Adjustment = new LayerAdjustment(AdjustmentKind.Hsv) { HsvSettings = settings },
                },
            ]);

        string json = ProjectStore.ToJson(manifest);

        Assert.Contains("\"adjustments\": [", json, StringComparison.Ordinal);
        Assert.Contains("\"Reds\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"adjustments\": {", json, StringComparison.Ordinal);

        ProjectManifest reparsed = ProjectStore.FromJson(json);
        Assert.Equal(settings, reparsed.Layers[0].Adjustment!.HsvSettings);
    }

    [Fact]
    public void AnInterruptedSaveLeavesNoStrayFiles()
    {
        using var folder = new ProjectFixture.TemporaryFolder();

        // A layer declaring an image the snapshot does not carry fails partway through the save.
        Guid id = Guid.NewGuid();
        ProjectManifest manifest = ProjectFixture.Manifest(
            layers:
            [
                ProjectFixture.Record("Layer 1", id) with { ImageFile = ManifestValidator.ImageFileName(id) },
            ]);

        string path = folder.File("Broken.comp");
        Assert.Throws<ProjectException>(() =>
            ProjectStore.Save(new ProjectSnapshot { Manifest = manifest }, path));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void OverwritingAProjectReplacesItWhole()
    {
        using var folder = new ProjectFixture.TemporaryFolder();
        string path = folder.File("Same.comp");

        PixelBuffer first = ProjectFixture.Image(8, 6);
        Guid firstId = Guid.NewGuid();
        ProjectStore.Save(
            ProjectMapping.ToSnapshot(ProjectFixture.Document(ProjectFixture.Layer("One", first, firstId)), firstId),
            path);

        PixelBuffer second = ProjectFixture.Image(16, 12);
        Guid secondId = Guid.NewGuid();
        ProjectStore.Save(
            ProjectMapping.ToSnapshot(ProjectFixture.Document(ProjectFixture.Layer("Two", second, secondId)), secondId),
            path);

        ProjectSnapshot loaded = ProjectStore.Load(path);
        Assert.Single(loaded.Manifest.Layers);
        Assert.Equal("Two", loaded.Manifest.Layers[0].Name);

        // The first save's asset must not still be sitting in the container.
        using ZipArchive archive = ZipFile.OpenRead(path);
        Assert.Null(archive.GetEntry($"images/{ManifestValidator.ImageFileName(firstId)}"));

        first.Release();
        second.Release();
    }

    /// <summary>
    /// Compares pixels, allowing colour channels to move by one.
    /// </summary>
    /// <remarks>
    /// PNG stores straight alpha and a PixelBuffer holds premultiplied, so a save divides the alpha
    /// out and a load multiplies it back in. Both steps round to a byte, so a partly transparent
    /// pixel can come back one off — that is a property of premultiplied storage, not a defect, and
    /// the point of asserting it here is that it stays within one. Alpha itself is exact.
    /// </remarks>
    private static void AssertSamePixels(PixelBuffer expected, PixelBuffer actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);

        for (int y = 0; y < expected.Height; y++)
        {
            Span<byte> before = expected.Row(y), after = actual.Row(y);
            for (int x = 0; x < expected.Width; x++)
            {
                Assert.Equal(before[x * 4 + 3], after[x * 4 + 3]);
                for (int channel = 0; channel < 3; channel++)
                {
                    int difference = Math.Abs(before[x * 4 + channel] - after[x * 4 + channel]);
                    Assert.True(difference <= 1,
                        $"pixel ({x}, {y}) channel {channel}: {before[x * 4 + channel]} became {after[x * 4 + channel]}");
                }
            }
        }
    }

    private static void AssertSameCoverage(PixelBuffer expected, PixelBuffer actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            Span<byte> before = expected.Row(y), after = actual.Row(y);
            for (int x = 0; x < expected.Width; x++)
                Assert.Equal(before[x * 4], after[x * 4]);
        }
    }

    private static void Release(params PixelBuffer[] buffers)
    {
        foreach (PixelBuffer buffer in buffers) buffer.Release();
    }
}
