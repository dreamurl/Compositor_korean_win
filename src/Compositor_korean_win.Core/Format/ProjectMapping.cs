namespace Compositor_korean_win.Core;

/// <summary>
/// Turns a live document into what gets stored, and back.
/// </summary>
/// <remarks>
/// The two shapes are deliberately not the same type. <see cref="CanvasDocument"/> is what the
/// editor works on, holding pixels; <see cref="ProjectManifest"/> is what sits on disk, naming
/// files. Keeping them apart is what lets the stored schema stay frozen — a record of it is what
/// macOS reads — while the in-memory model changes as the editor grows.
///
/// Undo history, the viewport and selection are session-only and never make the trip, so a project
/// always reopens with clean history, as upstream's format notes require.
/// </remarks>
public static class ProjectMapping
{
    /// <summary>The manifest and assets that stand for <paramref name="document"/>.</summary>
    public static ProjectSnapshot ToSnapshot(CanvasDocument document, Guid? activeLayerId,
                                             int version = ProjectManifest.CurrentVersion)
    {
        var images = new Dictionary<Guid, PixelBuffer>();
        var masks = new Dictionary<Guid, PixelBuffer>();
        var records = new List<ProjectLayerRecord>(document.Layers.Count);

        foreach (ImageLayer layer in document.Layers)
        {
            if (layer.Image is PixelBuffer image) images[layer.Id] = image;
            if (layer.Mask is LayerMask mask) masks[layer.Id] = mask.Coverage;

            records.Add(new ProjectLayerRecord
            {
                Id = layer.Id,
                Name = layer.Name,
                IsVisible = layer.IsVisible,
                Transform = layer.Transform,
                ImageFile = layer.Image is null ? null : ManifestValidator.ImageFileName(layer.Id),
                ParentId = layer.ParentId,
                // Written only when true: a false flag on every layer would be noise, and the
                // reader treats its absence as "not a folder" anyway.
                IsGroup = layer.IsGroup ? true : null,
                Opacity = layer.Opacity,
                BlendMode = layer.BlendMode,
                MaskFile = layer.Mask is null ? null : ManifestValidator.MaskFileName(layer.Id),
                MaskEnabled = layer.Mask?.IsEnabled,
                MaskSourceId = layer.MaskSourceId,
                Adjustment = layer.Adjustment,
                MaskPlacement = layer.Mask?.Placement,
                MaskLinked = layer.Mask?.IsLinked,
                Shape = layer.Shape,
            });
        }

        var manifest = new ProjectManifest
        {
            Version = version,
            Resolution = document.Resolution,
            DocumentId = document.Id,
            Width = document.Width,
            Height = document.Height,
            ActiveLayerId = activeLayerId,
            Layers = new EquatableList<ProjectLayerRecord>(records),
        };

        return new ProjectSnapshot { Manifest = manifest, Images = images, Masks = masks };
    }

    /// <summary>The document a loaded snapshot describes.</summary>
    /// <remarks>
    /// Assets are taken from the snapshot by id. A record naming a file the snapshot has no pixels
    /// for cannot happen after <see cref="ProjectStore.Load"/>, which reads every declared asset or
    /// rejects the project, but building a snapshot by hand can produce one — so it is checked.
    /// </remarks>
    public static CanvasDocument ToDocument(ProjectSnapshot snapshot)
    {
        ProjectManifest manifest = snapshot.Manifest;
        var layers = new List<ImageLayer>(manifest.Layers.Count);

        foreach (ProjectLayerRecord record in manifest.Layers)
        {
            PixelBuffer? image = null;
            if (record.ImageFile is not null && !snapshot.Images.TryGetValue(record.Id, out image))
                throw ProjectException.MissingImage($"layer {record.Id}'s image");

            LayerMask? mask = null;
            if (record.MaskFile is not null)
            {
                if (!snapshot.Masks.TryGetValue(record.Id, out PixelBuffer? coverage))
                    throw ProjectException.MissingImage($"layer {record.Id}'s mask");

                mask = new LayerMask
                {
                    Coverage = coverage,
                    // Absent means enabled, which is how a mask written before the flag existed
                    // must be read.
                    IsEnabled = record.MaskEnabled ?? true,
                    Placement = record.MaskPlacement,
                    IsLinked = record.MaskLinked ?? true,
                };
            }

            layers.Add(new ImageLayer
            {
                Id = record.Id,
                Name = record.Name,
                Transform = record.Transform,
                Image = image,
                IsVisible = record.IsVisible,
                ParentId = record.ParentId,
                IsGroup = record.IsGroup ?? false,
                Opacity = record.Opacity ?? 1,
                BlendMode = record.BlendMode ?? LayerBlendMode.Normal,
                MaskSourceId = record.MaskSourceId,
                Mask = mask,
                Adjustment = record.Adjustment,
                Shape = record.Shape,
            });
        }

        return new CanvasDocument
        {
            Id = manifest.DocumentId,
            Width = manifest.Width,
            Height = manifest.Height,
            // Version 1 predates the field; those projects were all 72 pixels per inch.
            Resolution = manifest.Resolution ?? 72,
            Layers = new EquatableList<ImageLayer>(layers),
        };
    }
}
