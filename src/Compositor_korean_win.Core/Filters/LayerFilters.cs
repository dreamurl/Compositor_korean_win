namespace Compositor_korean_win.Core;

/// <summary>
/// Running a filter over a layer for good: one edit, one history step.
/// </summary>
/// <remarks>
/// <para>
/// A blur spreads, so the layer is first padded by the filter's reach in its own pixels and the
/// placement grown to match — otherwise the blur would stop dead at the layer's old edge. What the
/// blur leaves empty is trimmed off again afterwards, which is upstream's order.
/// </para>
/// <para>
/// A layer whose own mask shares its pixel grid is not grown: the mask covers the layer's extent,
/// so growing the grid would stretch the mask across the new one. The blur then stays inside the
/// layer as it was, which is what the mask was going to show anyway.
/// </para>
/// </remarks>
public static class LayerFilters
{
    /// <summary>Upstream's ceiling on a grown layer, per side and in total.</summary>
    public const int MaximumSide = 30_000;
    public const long MaximumPixels = 100_000_000;

    /// <summary>
    /// <paramref name="layer"/> with the filter applied to its pixels, inside
    /// <paramref name="selection"/> if there is one. A layer with no pixels comes back as it was.
    /// </summary>
    public static ImageLayer Apply(ImageLayer layer, FilterKind kind, FilterSettings settings,
                                   DocumentSelection? selection)
    {
        if (layer.Image is not PixelBuffer image || layer.IsGroup) return layer;

        int width = image.Width, height = image.Height;
        int margin = Margin(layer, kind, settings);

        var grid = new PixelRect(-margin, -margin, width + margin * 2, height + margin * 2);
        using PixelBuffer grown = PixelRegion.Copy(image, grid);
        PixelBuffer filtered = PixelFilters.Run(grown, kind, settings);

        try
        {
            if (selection is not null)
            {
                byte[] levels = SelectionLevels(selection, layer.Transform, width, height, unit: 1, grid);
                PixelFilters.Confine(grown, filtered, levels);
            }

            PixelRect kept = margin > 0 ? Trim(filtered) : new PixelRect(0, 0, grid.Width, grid.Height);

            // Nothing left at all: keep the layer's own extent rather than a layer of no size.
            if (kept.IsEmpty) kept = new PixelRect(margin, margin, width, height);

            PixelBuffer result = kept.Width == filtered.Width && kept.Height == filtered.Height
                ? filtered.Retain()
                : PixelRegion.Copy(filtered, kept);

            var region = new PixelRect(grid.X + kept.X, grid.Y + kept.Y, kept.Width, kept.Height);

            return layer with
            {
                Image = result,
                Transform = region == new PixelRect(0, 0, width, height)
                    ? layer.Transform
                    : LayerGeometry.Place(layer.Transform, region, width, height),

                // Whatever drew this shape, the pixels are no longer what it drew.
                Shape = null,
            };
        }
        finally
        {
            filtered.Release();
        }
    }

    /// <summary>How far a layer is grown before a filter runs over it, in its own pixels.</summary>
    public static int Margin(ImageLayer layer, FilterKind kind, FilterSettings settings)
    {
        if (layer.Image is not PixelBuffer image) return 0;
        if (layer.Mask is { Placement: null }) return 0;

        int reach = PixelFilters.Reach(kind, settings);
        if (reach <= 0) return 0;

        // Grow as far as the ceiling allows rather than refusing the filter.
        int bySide = (MaximumSide - Math.Max(image.Width, image.Height)) / 2;
        long area = (long)image.Width * image.Height;
        int byArea = 0;
        while (byArea < reach
               && (long)(image.Width + (byArea + 1) * 2) * (image.Height + (byArea + 1) * 2) <= MaximumPixels
               && area <= MaximumPixels)
        {
            byArea++;
        }

        return Math.Max(0, Math.Min(reach, Math.Min(bySide, byArea)));
    }

    /// <summary>
    /// A selection's coverage over <paramref name="region"/> of a grid laid over a layer, where one
    /// grid pixel is <paramref name="unit"/> layer pixels.
    /// </summary>
    /// <remarks>
    /// The selection lives on the document; the layer may be moved, scaled, turned or flipped. The
    /// outline goes across by the placement, which is affine, so it lands exactly.
    /// </remarks>
    public static byte[] SelectionLevels(DocumentSelection selection, LayerTransform placement,
                                         int width, int height, double unit, PixelRect region)
    {
        DocumentSelection inGrid = selection.Transformed(point =>
        {
            Point pixel = LayerGeometry.ToPixels(placement, point, width, height);
            return new Point(pixel.X / unit, pixel.Y / unit);
        });
        return inGrid.Levels(region);
    }

    /// <summary>The smallest rectangle holding every pixel that is not fully transparent.</summary>
    public static PixelRect Trim(PixelBuffer pixels)
    {
        unsafe
        {
            nuint* bounds = stackalloc nuint[4];
            Kernels.BrushAlphaBounds(pixels.Scan0, (nuint)pixels.Width, (nuint)pixels.Height,
                                     (nuint)pixels.Stride, (nint)bounds);
            return PixelRect.FromBounds((int)bounds[0], (int)bounds[1], (int)bounds[2], (int)bounds[3]);
        }
    }
}
