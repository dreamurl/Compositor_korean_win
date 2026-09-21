using System.Text;

namespace Compositor_korean_win.Core;

/// <summary>Why a project could not be opened or saved.</summary>
public enum ProjectErrorKind
{
    /// <summary>Not a Compositor project, or its metadata is damaged.</summary>
    Invalid,

    /// <summary>A format version this build does not know.</summary>
    Version,

    /// <summary>An image inside the project is missing or damaged.</summary>
    MissingImage,

    /// <summary>Past the canvas, layer, file-size or 100-megapixel limit.</summary>
    TooLarge,

    /// <summary>An image could not be encoded.</summary>
    Encode,
}

/// <summary>
/// A project was rejected. The open document is never replaced when one of these is thrown.
/// </summary>
public sealed class ProjectException : Exception
{
    private ProjectException(ProjectErrorKind kind, string message, int version = 0) : base(message)
    {
        Kind = kind;
        Version = version;
    }

    public ProjectErrorKind Kind { get; }

    /// <summary>The unsupported version, for <see cref="ProjectErrorKind.Version"/>.</summary>
    public int Version { get; }

    public static ProjectException Invalid(string detail) =>
        new(ProjectErrorKind.Invalid,
            $"This is not a valid Compositor project, or its metadata is damaged ({detail}).");

    public static ProjectException UnsupportedVersion(int version) =>
        new(ProjectErrorKind.Version,
            $"This project uses format version {version}. This app supports versions "
            + $"{ProjectManifest.OldestVersion}–{ProjectManifest.CurrentVersion}.", version);

    public static ProjectException MissingImage(string detail) =>
        new(ProjectErrorKind.MissingImage,
            $"An image inside the project is missing or damaged ({detail}). "
            + "The current document has not been replaced.");

    public static ProjectException TooLarge(string detail) =>
        new(ProjectErrorKind.TooLarge,
            $"This project exceeds the supported canvas, layer, file-size, or 100-megapixel limit ({detail}).");

    public static ProjectException Encode(string detail) =>
        new(ProjectErrorKind.Encode,
            $"An image could not be saved ({detail}). The previous project has not been replaced.");
}

/// <summary>The limits the format imposes. Upstream's <c>ProjectStore</c> is the source of these.</summary>
public static class ProjectLimits
{
    public const int MaximumSide = 30_000;
    public const long MaximumPixels = 100_000_000;
    public const int MaximumLayers = 10_000;
    public const int MaximumManifestBytes = 4 * 1024 * 1024;
    public const long MaximumAssetBytes = 512L * 1024 * 1024;
    public const int MaximumNameBytes = 16_384;

    /// <summary>How deep folders may nest before a project is rejected.</summary>
    public const int MaximumNesting = 64;

    /// <summary>How long a chain of clipping masks may get.</summary>
    public const int MaximumMaskChain = 256;
}

/// <summary>Checks that folders form a tree, not a tangle.</summary>
public static class LayerHierarchy
{
    /// <summary>
    /// Rejects duplicate ids, folders carrying an image, a parent that is missing or is not a
    /// folder, a cycle, and nesting past <see cref="ProjectLimits.MaximumNesting"/>.
    /// </summary>
    /// <remarks>
    /// The depth limit counts a node's ancestors, and folders are held to the same limit with room
    /// left for a leaf below them — so a chain of 64 folders is fine and one of 65 is not.
    /// </remarks>
    public static void Validate(IReadOnlyList<ProjectLayerRecord> layers)
    {
        var byId = new Dictionary<Guid, ProjectLayerRecord>();
        foreach (ProjectLayerRecord layer in layers)
        {
            if (!byId.TryAdd(layer.Id, layer)) throw ProjectException.Invalid($"layer {layer.Id} appears twice");
            if (layer.IsGroup == true && layer.ImageFile is not null)
                throw ProjectException.Invalid($"folder {layer.Id} carries an image");
        }

        foreach (ProjectLayerRecord layer in layers)
        {
            var seen = new HashSet<Guid> { layer.Id };
            Guid? parent = layer.ParentId;
            while (parent is Guid id)
            {
                if (seen.Count > ProjectLimits.MaximumNesting)
                    throw ProjectException.Invalid($"layer {layer.Id} nests deeper than {ProjectLimits.MaximumNesting}");
                if (!seen.Add(id))
                    throw ProjectException.Invalid($"layer {layer.Id} is inside itself");
                if (!byId.TryGetValue(id, out ProjectLayerRecord? node) || node.IsGroup != true)
                    throw ProjectException.Invalid($"layer {layer.Id}'s parent {id} is missing or is not a folder");
                parent = node.ParentId;
            }

            if (layer.IsGroup == true && seen.Count > ProjectLimits.MaximumNesting)
                throw ProjectException.Invalid($"folder {layer.Id} nests deeper than {ProjectLimits.MaximumNesting}");
        }
    }
}

/// <summary>Checks the clipping-mask links — what the format calls <c>maskSourceID</c>.</summary>
public static class LiveMaskGraph
{
    /// <summary>
    /// Rejects a missing reference, a self-link, a cycle, a folder or adjustment at either end, and
    /// a chain longer than <see cref="ProjectLimits.MaximumMaskChain"/>.
    /// </summary>
    public static void Validate(IReadOnlyList<ProjectLayerRecord> layers)
    {
        var records = new Dictionary<Guid, ProjectLayerRecord>();
        foreach (ProjectLayerRecord layer in layers)
            if (!records.TryAdd(layer.Id, layer)) throw ProjectException.Invalid($"layer {layer.Id} appears twice");

        foreach (ProjectLayerRecord layer in layers)
        {
            var path = new HashSet<Guid>();
            Guid? current = layer.Id;
            while (current is Guid id)
            {
                if (path.Count >= ProjectLimits.MaximumMaskChain)
                    throw ProjectException.Invalid($"clipping-mask chain from {layer.Id} is longer than {ProjectLimits.MaximumMaskChain}");
                if (!path.Add(id))
                    throw ProjectException.Invalid($"clipping-mask chain from {layer.Id} loops");
                if (!records.TryGetValue(id, out ProjectLayerRecord? record))
                    throw ProjectException.Invalid($"clipping mask refers to missing layer {id}");

                if (record.MaskSourceId is Guid source)
                {
                    if (record.IsGroup == true)
                        throw ProjectException.Invalid($"folder {record.Id} cannot be clipped");
                    if (!records.TryGetValue(source, out ProjectLayerRecord? node))
                        throw ProjectException.Invalid($"clipping mask refers to missing layer {source}");
                    if (node.IsGroup == true)
                        throw ProjectException.Invalid($"folder {source} cannot be a clipping-mask base");
                    if (node.Adjustment is not null)
                        throw ProjectException.Invalid($"adjustment {source} cannot be a clipping-mask base");
                }

                current = record.MaskSourceId;
            }
        }
    }
}

/// <summary>
/// Everything a manifest must satisfy before it may replace the open document.
/// </summary>
/// <remarks>
/// This runs on save as well as on load. Upstream checks both ends for the same reason: a manifest
/// that cannot be read back is not worth writing, and the checks are what keep a damaged or hostile
/// file from getting as far as the renderer.
/// </remarks>
public static class ManifestValidator
{
    public static void Validate(ProjectManifest manifest)
    {
        if (manifest.Format != ProjectManifest.FormatIdentifier)
            throw ProjectException.Invalid($"format is \"{manifest.Format}\"");

        if (manifest.Version < ProjectManifest.OldestVersion || manifest.Version > ProjectManifest.CurrentVersion)
            throw ProjectException.UnsupportedVersion(manifest.Version);

        if (manifest.ColorSpace != "sRGB")
            throw ProjectException.Invalid($"colour space is \"{manifest.ColorSpace}\"");

        if (manifest.Resolution is double resolution
            && (!double.IsFinite(resolution) || resolution is < 1 or > 9600))
            throw ProjectException.Invalid($"resolution is {resolution}");

        if (manifest.Width is < 1 or > ProjectLimits.MaximumSide
            || manifest.Height is < 1 or > ProjectLimits.MaximumSide
            || manifest.Layers.Count > ProjectLimits.MaximumLayers)
            throw ProjectException.TooLarge($"{manifest.Width}×{manifest.Height}, {manifest.Layers.Count} layers");

        foreach (ProjectLayerRecord layer in manifest.Layers) ValidateLayer(manifest, layer);

        LayerHierarchy.Validate(manifest.Layers);
        LiveMaskGraph.Validate(manifest.Layers);

        if (manifest.Version < 5 && manifest.Layers.Any(layer => layer.MaskSourceId is not null))
            throw ProjectException.Invalid($"version {manifest.Version} cannot carry clipping masks");

        if (manifest.Version == 1
            && manifest.Layers.Any(layer => layer.ParentId is not null || layer.IsGroup == true))
            throw ProjectException.Invalid("version 1 cannot carry folders");

        var ids = new HashSet<Guid>();
        foreach (ProjectLayerRecord layer in manifest.Layers)
        {
            if (!ids.Add(layer.Id)) throw ProjectException.Invalid($"layer {layer.Id} appears twice");
            if (!layer.Transform.IsValid) throw ProjectException.Invalid($"layer {layer.Id} has an out-of-range transform");
            if (string.IsNullOrWhiteSpace(layer.Name)) throw ProjectException.Invalid($"layer {layer.Id} has a blank name");
            if (Encoding.UTF8.GetByteCount(layer.Name) > ProjectLimits.MaximumNameBytes)
                throw ProjectException.Invalid($"layer {layer.Id}'s name is too long");
            if (layer.ImageFile is not null && layer.ImageFile != ImageFileName(layer.Id))
                throw ProjectException.Invalid($"layer {layer.Id}'s image is named \"{layer.ImageFile}\"");
        }

        if (manifest.ActiveLayerId is Guid active && !ids.Contains(active))
            throw ProjectException.Invalid($"the active layer {active} is not in this project");
    }

    private static void ValidateLayer(ProjectManifest manifest, ProjectLayerRecord layer)
    {
        if (layer.Adjustment is LayerAdjustment adjustment)
        {
            if (manifest.Version < 7)
                throw ProjectException.Invalid($"version {manifest.Version} cannot carry adjustment layers");
            if (layer.IsGroup == true) throw ProjectException.Invalid($"folder {layer.Id} cannot be an adjustment");
            if (layer.ImageFile is not null)
                throw ProjectException.Invalid($"adjustment {layer.Id} cannot carry an image");
            if (!adjustment.IsValid)
                throw ProjectException.Invalid($"adjustment {layer.Id} has out-of-range settings");
        }

        if (layer.MaskFile is not null)
        {
            // Layer masks arrived in version 4, folder masks in version 6.
            int required = layer.IsGroup == true ? 6 : 4;
            if (manifest.Version < required)
                throw ProjectException.Invalid($"version {manifest.Version} cannot give layer {layer.Id} a mask");
            if (layer.MaskFile != MaskFileName(layer.Id))
                throw ProjectException.Invalid($"layer {layer.Id}'s mask is named \"{layer.MaskFile}\"");
        }
        else
        {
            if (layer.MaskEnabled is not null)
                throw ProjectException.Invalid($"layer {layer.Id} has no mask to enable");
            if (layer.MaskPlacement is not null)
                throw ProjectException.Invalid($"layer {layer.Id} has no mask to place");
        }

        if (layer.MaskPlacement is LayerTransform placement && !placement.IsValid)
            throw ProjectException.Invalid($"layer {layer.Id}'s mask placement is out of range");

        double opacity = layer.Opacity ?? 1;
        LayerBlendMode blend = layer.BlendMode ?? LayerBlendMode.Normal;
        bool plain = opacity == 1 && blend == LayerBlendMode.Normal;

        if (!double.IsFinite(opacity) || opacity is < 0 or > 1)
            throw ProjectException.Invalid($"layer {layer.Id}'s opacity is {opacity}");
        if (manifest.Version < 3 && !plain)
            throw ProjectException.Invalid($"version {manifest.Version} cannot carry opacity or blend modes");
        if (layer.IsGroup == true && !plain)
            throw ProjectException.Invalid($"folder {layer.Id} cannot carry opacity or a blend mode");
    }

    public static string ImageFileName(Guid id) => $"{id.ToString("D").ToUpperInvariant()}.png";

    public static string MaskFileName(Guid id) => $"{id.ToString("D").ToUpperInvariant()}.mask.png";
}
