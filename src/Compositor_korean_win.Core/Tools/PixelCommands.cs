namespace Compositor_korean_win.Core;

/// <summary>
/// Edits that change a layer's pixels in one go: invert, fill, clear, content-aware fill, copying
/// into a new layer, and the layer mask.
/// </summary>
/// <remarks>
/// <para>
/// Every one works in the layer's own pixel grid, the same as the tools (docs/progress.md 5.1), with
/// the selection carried across by the placement. With no selection an edit covers the whole layer,
/// as in Photoshop. A blank layer that is filled becomes a layer the size of the canvas, which is
/// what upstream gives a blank layer the first time anything lands on it.
/// </para>
/// <para>
/// Each returns a new layer, or null when there was nothing to do; the old pixels stay with the
/// history.
/// </para>
/// </remarks>
public static class PixelCommands
{
    /// <summary>The colours flipped, alpha kept — within the selection when there is one.</summary>
    public static ImageLayer? Invert(ImageLayer layer, DocumentSelection? selection)
    {
        if (layer.Image is not PixelBuffer image) return null;

        PixelBuffer result = PixelFilters.Copy(image);
        for (int y = 0; y < result.Height; y++)
        {
            Span<byte> row = result.Row(y);
            for (int x = 0; x < result.Width; x++)
            {
                // Premultiplied, the inverse of c is a − c: straight 1 − c, multiplied by a again.
                byte alpha = row[x * 4 + 3];
                row[x * 4] = (byte)(alpha - row[x * 4]);
                row[x * 4 + 1] = (byte)(alpha - row[x * 4 + 1]);
                row[x * 4 + 2] = (byte)(alpha - row[x * 4 + 2]);
            }
        }

        Confine(layer, image, result, selection);
        return layer with { Image = result, Shape = null };
    }

    /// <summary>A colour laid over the selection, or over the whole layer.</summary>
    public static ImageLayer? Fill(CanvasDocument document, ImageLayer layer, Rgba colour, DocumentSelection? selection)
    {
        if (layer.IsGroup || layer.Adjustment is not null) return null;

        (ImageLayer target, PixelBuffer pixels) = Editable(document, layer);
        var region = new PixelRect(0, 0, pixels.Width, pixels.Height);
        byte[] coverage = Coverage(target, pixels, selection);

        PixelBuffer result = PixelFilters.Copy(pixels);
        Painting.Fill(result, region, coverage, colour);
        if (!ReferenceEquals(pixels, layer.Image)) pixels.Release();

        return target with { Image = result, Shape = null };
    }

    /// <summary>The selected pixels made transparent.</summary>
    public static ImageLayer? Clear(ImageLayer layer, DocumentSelection selection)
    {
        if (layer.Image is not PixelBuffer image) return null;

        byte[] coverage = Coverage(layer, image, selection);
        PixelBuffer result = PixelFilters.Copy(image);

        for (int y = 0; y < result.Height; y++)
        {
            Span<byte> row = result.Row(y);
            for (int x = 0; x < result.Width; x++)
            {
                int keep = 255 - coverage[y * result.Width + x];
                if (keep == 255) continue;
                for (int c = 0; c < 4; c++) row[x * 4 + c] = (byte)((row[x * 4 + c] * keep + 127) / 255);
            }
        }

        return layer with { Image = result, Shape = null };
    }

    /// <summary>The selection rebuilt from the rest of the layer; null when there is nothing to copy from.</summary>
    public static ImageLayer? ContentAwareFill(ImageLayer layer, DocumentSelection selection)
    {
        if (layer.Image is not PixelBuffer image) return null;

        byte[] coverage = Coverage(layer, image, selection);
        PixelBuffer result = PixelFilters.Copy(image);
        if (SpotHeal.ContentAwareFill(result, coverage)) return layer with { Image = result, Shape = null };

        result.Release();
        return null;
    }

    /// <summary>
    /// The selected pixels of a layer, trimmed to what is there, and where they sit on the document.
    /// Null when the selection covers none of the layer's pixels.
    /// </summary>
    public static (PixelBuffer Pixels, LayerTransform Placement)? Copy(ImageLayer layer, DocumentSelection? selection)
    {
        if (layer.Image is not PixelBuffer image) return null;

        byte[] coverage = Coverage(layer, image, selection);
        using PixelBuffer masked = PixelFilters.Copy(image);
        for (int y = 0; y < masked.Height; y++)
        {
            Span<byte> row = masked.Row(y);
            for (int x = 0; x < masked.Width; x++)
            {
                int keep = coverage[y * masked.Width + x];
                if (keep == 255) continue;
                for (int c = 0; c < 4; c++) row[x * 4 + c] = (byte)((row[x * 4 + c] * keep + 127) / 255);
            }
        }

        PixelRect trim = LayerFilters.Trim(masked);
        if (trim.IsEmpty) return null;

        return (PixelRegion.Copy(masked, trim), LayerGeometry.Place(layer.Transform, trim, image.Width, image.Height));
    }

    /// <summary>
    /// A new layer above <paramref name="source"/> holding its selected pixels — or all of them —
    /// and, for a cut, the source with those pixels cleared.
    /// </summary>
    public static (CanvasDocument Document, Guid Layer)? LayerVia(CanvasDocument document, Guid source,
                                                                 DocumentSelection? selection, bool cut)
    {
        if (document.Layer(source) is not ImageLayer layer
            || Copy(layer, selection) is not (PixelBuffer pixels, LayerTransform placement)) return null;

        var copy = new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = LayerCommands.NextName(document, TextKey.LayerNameNumbered),
            Image = pixels,
            Transform = placement,
            ParentId = layer.ParentId,
        };

        CanvasDocument next = document;
        if (cut && selection is not null && Clear(layer, selection) is ImageLayer cleared) next = next.Replacing(cleared);

        var layers = next.Layers.ToList();
        layers.Insert(next.IndexOf(source) + 1, copy);
        return (next with { Layers = layers.ToEquatableList() }, copy.Id);
    }

    /// <summary>
    /// A layer mask revealing the selection — or everything, with no selection — as Photoshop's
    /// "Reveal Selection" and "Reveal All".
    /// </summary>
    /// <remarks>
    /// With <paramref name="revealing"/> false it is "Hide Selection" and "Hide All" instead: black,
    /// or white with the selection black — upstream's Add Black Mask.
    /// </remarks>
    public static ImageLayer? AddMask(CanvasDocument document, ImageLayer layer, DocumentSelection? selection,
                                      bool revealing = true)
    {
        if (layer.Mask is not null || layer.IsGroup && selection is not null) return null;
        if (selection is null) return layer with { Mask = LayerMask.Solid(revealing) };

        // The mask shares the layer's grid; a blank layer's grid is the canvas.
        int width = layer.Image?.Width ?? document.Width, height = layer.Image?.Height ?? document.Height;
        byte[] levels = LayerFilters.SelectionLevels(selection, layer.Transform, width, height, 1,
                                                     new PixelRect(0, 0, width, height));

        PixelBuffer coverage = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = coverage.Row(y);
            for (int x = 0; x < width; x++)
            {
                byte level = revealing ? levels[y * width + x] : (byte)(255 - levels[y * width + x]);
                row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = level;
                row[x * 4 + 3] = 255;
            }
        }

        return layer with { Mask = new LayerMask { Coverage = coverage } };
    }

    public static ImageLayer? DeleteMask(ImageLayer layer) => layer.Mask is null ? null : layer with { Mask = null };

    public static ImageLayer? ToggleMask(ImageLayer layer) =>
        layer.Mask is LayerMask mask ? layer with { Mask = mask with { IsEnabled = !mask.IsEnabled } } : null;

    /// <summary>
    /// The layer's pixels to paint into: its own, or for a blank layer a transparent canvas-sized grid,
    /// placed over the canvas.
    /// </summary>
    private static (ImageLayer Layer, PixelBuffer Pixels) Editable(CanvasDocument document, ImageLayer layer) =>
        layer.Image is PixelBuffer image
            ? (layer, image)
            : (layer with { Transform = new LayerTransform(Point.Zero, document.Size) },
               PixelBuffer.Allocate(document.Width, document.Height));

    /// <summary>One byte per layer pixel: the selection there, or everything when there is none.</summary>
    private static byte[] Coverage(ImageLayer layer, PixelBuffer pixels, DocumentSelection? selection)
    {
        if (selection is null)
        {
            var all = new byte[pixels.Width * pixels.Height];
            Array.Fill(all, (byte)255);
            return all;
        }

        return LayerFilters.SelectionLevels(selection, layer.Transform, pixels.Width, pixels.Height, 1,
                                            new PixelRect(0, 0, pixels.Width, pixels.Height));
    }

    private static void Confine(ImageLayer layer, PixelBuffer original, PixelBuffer edited, DocumentSelection? selection)
    {
        if (selection is not null) PixelFilters.Confine(original, edited, Coverage(layer, original, selection));
    }
}
