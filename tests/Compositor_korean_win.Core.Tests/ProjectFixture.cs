using Compositor_korean_win.Core;

namespace Compositor_korean_win.Core.Tests;

/// <summary>Small documents to test the format against, and a place to put them.</summary>
internal static class ProjectFixture
{
    /// <summary>A buffer whose pixels depend on their position, so a mix-up is visible.</summary>
    public static PixelBuffer Image(int width = 8, int height = 6)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < width; x++)
            {
                // The left column is transparent, so premultiplication is exercised both ways.
                byte alpha = x == 0 ? (byte)0 : (byte)(64 + x * 16);
                row[x * 4 + 0] = Premultiplied((byte)(x * 8), alpha);
                row[x * 4 + 1] = Premultiplied((byte)(y * 16), alpha);
                row[x * 4 + 2] = Premultiplied(200, alpha);
                row[x * 4 + 3] = alpha;
            }
        }
        return buffer;

        static byte Premultiplied(byte value, byte alpha) => (byte)((value * alpha + 127) / 255);
    }

    /// <summary>A mask: grey coverage repeated across every channel, fully opaque.</summary>
    public static PixelBuffer Mask(int width = 4, int height = 4)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < width; x++)
            {
                byte coverage = (byte)(x * 60 + y * 5);
                row[x * 4 + 0] = coverage;
                row[x * 4 + 1] = coverage;
                row[x * 4 + 2] = coverage;
                row[x * 4 + 3] = 255;
            }
        }
        return buffer;
    }

    public static ImageLayer Layer(string name, PixelBuffer? image = null, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name,
        Transform = new LayerTransform(new Point(0, 0), new Size(8, 6)),
        Image = image,
    };

    public static CanvasDocument Document(params ImageLayer[] layers) => new()
    {
        Id = Guid.NewGuid(),
        Width = 64,
        Height = 32,
        Resolution = 144,
        Layers = new EquatableList<ImageLayer>(layers),
    };

    /// <summary>A manifest for a document with one plain, imageless layer.</summary>
    public static ProjectManifest Manifest(int version = ProjectManifest.CurrentVersion,
                                           params ProjectLayerRecord[] layers) => new()
    {
        Version = version,
        DocumentId = Guid.NewGuid(),
        Width = 64,
        Height = 32,
        Layers = new EquatableList<ProjectLayerRecord>(
            layers.Length > 0 ? layers : [Record("Layer 1")]),
    };

    public static ProjectLayerRecord Record(string name, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name,
        IsVisible = true,
        Transform = new LayerTransform(new Point(0, 0), new Size(8, 6)),
    };

    /// <summary>A folder that goes away when the test does.</summary>
    public sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"compositor-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A file left open by a failing test is not worth failing the run over.
            }
        }
    }
}
