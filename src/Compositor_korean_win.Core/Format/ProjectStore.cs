using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Compositor_korean_win.Core;

/// <summary>Source-generated metadata, so serialising needs no reflection under NativeAOT.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProjectManifest))]
[JsonSerializable(typeof(ProjectHeader))]
// The converters for EquatableList and ColorRangeMap look their element types up in this context,
// so each one is listed here rather than left to be found through the property that uses it.
[JsonSerializable(typeof(ProjectLayerRecord))]
[JsonSerializable(typeof(LevelRange))]
[JsonSerializable(typeof(CurvePoint))]
[JsonSerializable(typeof(ColorRange))]
[JsonSerializable(typeof(RangeAdjustment))]
[JsonSerializable(typeof(HueBand))]
internal sealed partial class ProjectJson : JsonSerializerContext;

/// <summary>A project as it sits in memory: its manifest and the pixels the manifest names.</summary>
public sealed record ProjectSnapshot
{
    public required ProjectManifest Manifest { get; init; }

    /// <summary>Layer pixels, by layer id. A blank layer, folder or adjustment has none.</summary>
    public IReadOnlyDictionary<Guid, PixelBuffer> Images { get; init; } = new Dictionary<Guid, PixelBuffer>();

    /// <summary>Mask coverage, by layer id.</summary>
    public IReadOnlyDictionary<Guid, PixelBuffer> Masks { get; init; } = new Dictionary<Guid, PixelBuffer>();
}

/// <summary>
/// Reads and writes <c>.comp</c> projects.
/// </summary>
/// <remarks>
/// <para>
/// <b>The container.</b> On macOS a <c>.comp</c> is a document package — a directory holding
/// <c>manifest.json</c> and an <c>images/</c> folder — which Finder presents as one file. Windows
/// Explorer has no such concept, so the same layout would show up as a folder the user can walk
/// into and break. docs/windows-port.md §4.4 leaves how to resolve that open; this reads both and
/// writes a zip. A project saved here is one file, as a user expects, and a project saved on macOS
/// still opens, which is the half of compatibility that actually gets exercised. The macOS build
/// cannot read a zip, so a file going the other way has to be unpacked first — a real cost, and the
/// reason the reader keeps both paths rather than treating the directory form as legacy.
/// </para>
/// <para>
/// <b>Nothing is trusted.</b> Every check upstream makes before it lets a project replace the open
/// document is made here: the version, the metadata, the declared limits, the entry paths and the
/// running pixel total. A project that fails any of them is rejected whole.
/// </para>
/// </remarks>
public static class ProjectStore
{
    private const string ManifestEntry = "manifest.json";
    private const string ImagesFolder = "images";

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Writes <paramref name="snapshot"/> to <paramref name="path"/> as a zip container.</summary>
    /// <remarks>
    /// The file is staged beside its destination and moved into place, so an interrupted save
    /// cannot leave a half-written project where a whole one used to be.
    /// </remarks>
    public static void Save(ProjectSnapshot snapshot, string path)
    {
        ManifestValidator.Validate(snapshot.Manifest);

        var assets = new List<(string Name, byte[] Data)>();
        long pixels = 0, maskPixels = 0;

        foreach (ProjectLayerRecord layer in snapshot.Manifest.Layers)
        {
            if (layer.ImageFile is not null)
            {
                PixelBuffer image = Require(snapshot.Images, layer.Id, "image");
                CheckSize(image.Width, image.Height, ref pixels);
                assets.Add((layer.ImageFile, Png.Encode(image)));
            }

            if (layer.MaskFile is not null)
            {
                PixelBuffer mask = Require(snapshot.Masks, layer.Id, "mask");
                CheckSize(mask.Width, mask.Height, ref maskPixels);
                assets.Add((layer.MaskFile, Png.EncodeMask(mask)));
            }
        }

        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(snapshot.Manifest, ProjectJson.Default.ProjectManifest);
        if (metadata.Length > ProjectLimits.MaximumManifestBytes)
            throw ProjectException.TooLarge($"the manifest is {metadata.Length} bytes");

        foreach ((string name, byte[] data) in assets)
            if (data.LongLength > ProjectLimits.MaximumAssetBytes)
                throw ProjectException.TooLarge($"{name} is {data.LongLength} bytes");

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        string staging = Path.Combine(directory ?? ".", $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                Write(archive, ManifestEntry, metadata);
                foreach ((string name, byte[] data) in assets) Write(archive, $"{ImagesFolder}/{name}", data);
            }

            File.Move(staging, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(staging)) File.Delete(staging);
            throw;
        }

        static void Write(ZipArchive archive, string name, byte[] data)
        {
            using Stream entry = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
            entry.Write(data);
        }
    }

    /// <summary>Reads the project at <paramref name="path"/>, whether zip or directory package.</summary>
    public static ProjectSnapshot Load(string path)
    {
        if (Directory.Exists(path)) return Read(new DirectoryContainer(path));
        if (File.Exists(path)) return Read(new ZipContainer(path));
        throw ProjectException.Invalid($"there is nothing at {path}");
    }

    private static ProjectSnapshot Read(IProjectContainer container)
    {
        using (container)
        {
            byte[] stored = container.Read(ManifestEntry, ProjectLimits.MaximumManifestBytes)
                ?? throw ProjectException.Invalid("no manifest.json");

            // Foundation writes no byte-order mark, but plenty of editors and tools add one and a
            // manifest is a text file people do open. Skipping it costs nothing.
            ReadOnlySpan<byte> metadata = stored.AsSpan();
            if (metadata.StartsWith(Utf8Bom)) metadata = metadata[Utf8Bom.Length..];

            // The header is read on its own first so an unreadable newer project reports its
            // version rather than a decoding failure.
            ProjectHeader header;
            try
            {
                header = JsonSerializer.Deserialize(metadata, ProjectJson.Default.ProjectHeader)
                    ?? throw ProjectException.Invalid("the manifest is empty");
            }
            catch (JsonException exception)
            {
                throw ProjectException.Invalid(exception.Message);
            }

            if (header.Format != ProjectManifest.FormatIdentifier)
                throw ProjectException.Invalid($"format is \"{header.Format}\"");
            if (header.Version < ProjectManifest.OldestVersion || header.Version > ProjectManifest.CurrentVersion)
                throw ProjectException.UnsupportedVersion(header.Version);

            ProjectManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize(metadata, ProjectJson.Default.ProjectManifest)
                    ?? throw ProjectException.Invalid("the manifest is empty");
            }
            catch (JsonException exception)
            {
                throw ProjectException.Invalid(exception.Message);
            }

            ManifestValidator.Validate(manifest);

            var images = new Dictionary<Guid, PixelBuffer>();
            var masks = new Dictionary<Guid, PixelBuffer>();
            long pixels = 0, maskPixels = 0;

            foreach (ProjectLayerRecord layer in manifest.Layers)
            {
                if (layer.ImageFile is not null)
                    images[layer.Id] = ReadAsset(container, layer.ImageFile, ref pixels, mask: false);

                if (layer.MaskFile is not null)
                    masks[layer.Id] = ReadAsset(container, layer.MaskFile, ref maskPixels, mask: true);
            }

            return new ProjectSnapshot { Manifest = manifest, Images = images, Masks = masks };
        }
    }

    private static PixelBuffer ReadAsset(IProjectContainer container, string name, ref long used, bool mask)
    {
        byte[] data = container.Read($"{ImagesFolder}/{name}", ProjectLimits.MaximumAssetBytes)
            ?? throw ProjectException.MissingImage(name);

        Png.Header header = Png.ReadHeader(data);

        // A mask is coverage, not colour: upstream stores 8-bit grey with no alpha and rejects
        // anything else rather than guessing which channel was meant.
        if (mask && !header.IsGreyscaleWithoutAlpha)
            throw ProjectException.Invalid($"{name} is not an 8-bit greyscale mask");

        CheckSize(header.Width, header.Height, ref used);
        return Png.Decode(data);
    }

    private static PixelBuffer Require(IReadOnlyDictionary<Guid, PixelBuffer> assets, Guid id, string what) =>
        assets.TryGetValue(id, out PixelBuffer? buffer)
            ? buffer
            : throw ProjectException.MissingImage($"layer {id} declares a {what} but none was given");

    /// <summary>
    /// Holds each side inside its limit and the running total under a hundred million pixels.
    /// </summary>
    /// <remarks>
    /// The total is what actually matters: a project of ten thousand small layers can exceed any
    /// memory budget while every one of them passes a per-image check.
    /// </remarks>
    private static void CheckSize(int width, int height, ref long used)
    {
        if (width is < 1 or > ProjectLimits.MaximumSide || height is < 1 or > ProjectLimits.MaximumSide
            || (long)width * height > ProjectLimits.MaximumPixels - used)
            throw ProjectException.TooLarge($"{width}×{height} past {ProjectLimits.MaximumPixels} pixels");

        used += (long)width * height;
    }

    private interface IProjectContainer : IDisposable
    {
        /// <summary>The entry's bytes, or null when it is not there.</summary>
        byte[]? Read(string name, long maximumBytes);
    }

    /// <summary>A project saved here: one zip file.</summary>
    private sealed class ZipContainer(string path) : IProjectContainer
    {
        private readonly ZipArchive _archive = Open(path);

        private static ZipArchive Open(string path)
        {
            try
            {
                return ZipFile.OpenRead(path);
            }
            catch (InvalidDataException)
            {
                throw ProjectException.Invalid("not a Compositor project");
            }
        }

        public byte[]? Read(string name, long maximumBytes)
        {
            // GetEntry matches the stored name exactly, so a traversal like "../x" or an absolute
            // path simply does not match what is asked for and never reaches the file system.
            ZipArchiveEntry? entry = _archive.GetEntry(name);
            if (entry is null) return null;

            if (entry.Length > maximumBytes)
                throw ProjectException.TooLarge($"{name} is {entry.Length} bytes");

            byte[] data = new byte[entry.Length];
            using Stream stream = entry.Open();
            stream.ReadExactly(data);
            return data;
        }

        public void Dispose() => _archive.Dispose();
    }

    /// <summary>A project saved by the macOS build: a directory package.</summary>
    private sealed class DirectoryContainer(string root) : IProjectContainer
    {
        private readonly string _root = Path.GetFullPath(root);

        public byte[]? Read(string name, long maximumBytes)
        {
            string full = Path.GetFullPath(Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar)));

            // Upstream checks the resolved path is still inside the package; the same applies here,
            // and a name that escapes is a damaged or hostile project rather than a missing file.
            if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw ProjectException.Invalid($"\"{name}\" points outside the project");

            var file = new FileInfo(full);
            if (!file.Exists) return null;
            if (file.LinkTarget is not null) throw ProjectException.Invalid($"\"{name}\" is a link");
            if (file.Length > maximumBytes) throw ProjectException.TooLarge($"{name} is {file.Length} bytes");

            return File.ReadAllBytes(full);
        }

        public void Dispose() { }
    }

    /// <summary>The manifest as it would be written, for tests and diagnostics.</summary>
    public static string ToJson(ProjectManifest manifest) =>
        Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(manifest, ProjectJson.Default.ProjectManifest));

    /// <summary>Parses a manifest without touching any assets.</summary>
    public static ProjectManifest FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ProjectJson.Default.ProjectManifest)
                ?? throw ProjectException.Invalid("the manifest is empty");
        }
        catch (JsonException exception)
        {
            throw ProjectException.Invalid(exception.Message);
        }
    }
}
