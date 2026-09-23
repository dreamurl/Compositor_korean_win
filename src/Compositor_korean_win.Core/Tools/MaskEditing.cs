namespace Compositor_korean_win.Core;

/// <summary>
/// Working on a layer's mask rather than its pixels: the grid a mask is painted in, the greys it is
/// painted with, and the pixel edits that make sense on one.
/// </summary>
/// <remarks>
/// <para>
/// A mask is grey in the colour channels with alpha full (<see cref="LayerMask.Solid"/>), so every
/// tool that paints a layer can paint a mask unchanged — a grey laid at full alpha stays at full
/// alpha. What differs is only where the grid is and what colour goes on: upstream paints a mask
/// in white or black, never in the palette's colours, because a mask has no colour to take.
/// </para>
/// <para>
/// A new mask with no selection is a single pixel stretched over the layer. It is spread out to a
/// real grid the first time anything is painted into it — the layer's own grid while the mask
/// follows the layer, or its own box once it has been placed apart.
/// </para>
/// </remarks>
public static class MaskEditing
{
    /// <summary>The largest mask a single pixel is spread out to, as with every other raster.</summary>
    private const long PixelLimit = 100_000_000;

    /// <summary>The grey a mask is painted with: white shows the layer, black hides it.</summary>
    public static Rgba Grey(bool white) => white ? Rgba.White : Rgba.Black;

    /// <summary>
    /// A copy of the layer's mask to paint into, at its full grid, and where that grid sits on the
    /// document. Null when the layer has no mask. The caller owns the pixels.
    /// </summary>
    public static (PixelBuffer Pixels, LayerTransform Placement)? Canvas(ImageLayer layer)
    {
        if (layer.Mask is not LayerMask mask) return null;

        LayerTransform placement = mask.Placement ?? layer.Transform;
        PixelBuffer coverage = mask.Coverage;
        if (coverage.Width > 1 || coverage.Height > 1) return (PixelFilters.Copy(coverage), placement);

        // A single pixel: the grid it stands for.
        (int width, int height) = mask.Placement is null && layer.Image is PixelBuffer image
            ? (image.Width, image.Height)
            : ((int)Math.Max(1, Math.Round(placement.Size.Width)), (int)Math.Max(1, Math.Round(placement.Size.Height)));
        if ((long)width * height > PixelLimit) return null;

        PixelBuffer spread = PixelBuffer.Allocate(width, height);
        ReadOnlySpan<byte> level = coverage.Row(0)[..4];
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = spread.Row(y);
            for (int x = 0; x < width; x++) level.CopyTo(row.Slice(x * 4, 4));
        }

        return (spread, placement);
    }

    /// <summary>
    /// The layer moved to <paramref name="to"/>, its mask with it or not — upstream's
    /// <c>placement(movingLayer:to:)</c>.
    /// </summary>
    /// <remarks>
    /// Linked, a mask covering the layer's grid goes on covering it, and one placed apart moves the
    /// same way the layer did. Unlinked, the mask stays where it was on the document, which for one
    /// that covered the layer means it is placed apart for the first time, at the layer's old box.
    /// A mask that lands exactly on the layer again goes back to following its grid.
    /// </remarks>
    public static ImageLayer WithTransform(ImageLayer layer, LayerTransform to)
    {
        if (layer.Mask is not LayerMask mask || layer.Transform == to) return layer with { Transform = to };

        LayerTransform? placement = mask.IsLinked
            ? mask.Placement?.Following(layer.Transform, to)
            : mask.Placement ?? layer.Transform;
        if (placement == to) placement = null;

        return layer with { Transform = to, Mask = mask with { Placement = placement } };
    }

    /// <summary>Where the layer's mask sits on the document.</summary>
    public static LayerTransform? PlacementOf(ImageLayer layer) =>
        layer.Mask is LayerMask mask ? mask.Placement ?? layer.Transform : null;

    /// <summary>The mask alone moved to <paramref name="to"/>, the layer left where it is.</summary>
    public static ImageLayer WithMaskPlacement(ImageLayer layer, LayerTransform to) =>
        layer.Mask is LayerMask mask
            ? layer with { Mask = mask with { Placement = to == layer.Transform ? null : to } }
            : layer;

    /// <summary>Linked becomes unlinked and the other way round; the mask does not move.</summary>
    public static ImageLayer? ToggleLink(ImageLayer layer) =>
        layer.Mask is LayerMask mask ? layer with { Mask = mask with { IsLinked = !mask.IsLinked } } : null;

    /// <summary>
    /// A copy of <paramref name="source"/>'s mask on <paramref name="target"/>, sitting where it
    /// sat on the document and replacing any mask the target had — upstream's <c>copyMask</c>.
    /// Null when there is nothing to copy or nowhere to put it.
    /// </summary>
    public static ImageLayer? CopyTo(ImageLayer source, ImageLayer target)
    {
        if (source.Mask is not LayerMask mask || source.Id == target.Id || target.IsGroup) return null;

        LayerTransform where = mask.Placement ?? source.Transform;
        return target with
        {
            Mask = mask with { Placement = where == target.Transform ? null : where },
        };
    }

    /// <summary>
    /// The mask as grey pixels, placed where it sits — what Copy takes from a mask. The caller owns
    /// the pixels; null with no mask.
    /// </summary>
    public static ImageLayer? AsPixels(ImageLayer layer)
    {
        if (Canvas(layer) is not (PixelBuffer pixels, LayerTransform placement)) return null;
        return new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = layer.Name,
            Image = pixels,
            Transform = placement,
        };
    }

    /// <summary>The layer with <paramref name="coverage"/> as its mask, kept where the mask was.</summary>
    public static ImageLayer WithMask(ImageLayer layer, PixelBuffer coverage) =>
        layer.Mask is LayerMask mask ? layer with { Mask = mask with { Coverage = coverage } } : layer;

    /// <summary>The mask inverted — what it showed it hides — within the selection when there is one.</summary>
    public static ImageLayer? Invert(ImageLayer layer, DocumentSelection? selection)
    {
        if (Canvas(layer) is not (PixelBuffer pixels, LayerTransform placement)) return null;

        using PixelBuffer original = PixelFilters.Copy(pixels);
        for (int y = 0; y < pixels.Height; y++)
        {
            Span<byte> row = pixels.Row(y);
            for (int x = 0; x < pixels.Width; x++)
            {
                int i = x * 4;
                row[i] = row[i + 1] = row[i + 2] = (byte)(255 - row[i]);
                row[i + 3] = 255;
            }
        }

        if (selection is not null) PixelFilters.Confine(original, pixels, Levels(selection, placement, pixels));
        return WithMask(layer, pixels);
    }

    /// <summary>White or black laid over the selection of the mask, or over all of it.</summary>
    public static ImageLayer? Fill(ImageLayer layer, bool white, DocumentSelection? selection)
    {
        if (Canvas(layer) is not (PixelBuffer pixels, LayerTransform placement)) return null;

        byte[] coverage = selection is null ? Everything(pixels) : Levels(selection, placement, pixels);
        Painting.Fill(pixels, new PixelRect(0, 0, pixels.Width, pixels.Height), coverage, Grey(white));
        return WithMask(layer, pixels);
    }

    /// <summary>
    /// The mask's dark half — what it hides — as a selection on the document, upstream's "Mask's
    /// Black Areas". Null when the mask hides nothing.
    /// </summary>
    public static DocumentSelection? Selection(ImageLayer layer)
    {
        if (layer.Mask is not LayerMask mask) return null;

        LayerTransform placement = mask.Placement ?? layer.Transform;
        PixelBuffer coverage = mask.Coverage;
        var dark = new byte[coverage.Width * coverage.Height];
        bool any = false;
        for (int y = 0; y < coverage.Height; y++)
        {
            ReadOnlySpan<byte> row = coverage.Row(y);
            for (int x = 0; x < coverage.Width; x++)
            {
                if (row[x * 4] >= 128) continue;
                dark[y * coverage.Width + x] = 1;
                any = true;
            }
        }
        if (!any) return null;

        DocumentSelection? traced = MagicWand.Outline(dark, coverage.Width, coverage.Height).Selection;
        return traced?.Transformed(point => LayerGeometry.ToDocument(placement, point, coverage.Width, coverage.Height));
    }

    private static byte[] Levels(DocumentSelection selection, LayerTransform placement, PixelBuffer pixels) =>
        LayerFilters.SelectionLevels(selection, placement, pixels.Width, pixels.Height, 1,
                                     new PixelRect(0, 0, pixels.Width, pixels.Height));

    private static byte[] Everything(PixelBuffer pixels)
    {
        var all = new byte[pixels.Width * pixels.Height];
        Array.Fill(all, (byte)255);
        return all;
    }
}
