namespace Compositor_korean_win.Core;

/// <summary>
/// Transformed selected pixels laid back into their layer — upstream's <c>FloatingMerge</c>,
/// which closes Layer › Transform Selection.
/// </summary>
/// <remarks>
/// <para>
/// The floating pixels are drawn into the layer's own grid, which grows where they now reach past
/// it, so nothing that was moved outside the layer is cut off. Each grid pixel looks up where it
/// falls in the floating pixels through both placements and takes a bilinear sample laid over what
/// is there — so a rotation or a scale is resampled once, here, and a plain move to whole pixels is
/// copied exactly.
/// </para>
/// <para>
/// A mask on the layer's grid grows with it, white in the new part, so what was moved out is shown;
/// a single-pixel mask already covers any grid and is left as it is.
/// </para>
/// </remarks>
public static class FloatingMerge
{
    /// <summary>
    /// <paramref name="source"/> with <paramref name="floating"/> drawn over it, as one layer; null
    /// when either has no pixels or the result would be larger than a layer may be.
    /// </summary>
    public static ImageLayer? Merge(ImageLayer source, ImageLayer floating)
    {
        if (source.Image is not PixelBuffer image || floating.Image is not PixelBuffer lifted) return null;
        int width = image.Width, height = image.Height;

        // Where the floating pixels land, in the layer's grid.
        Point[] corners = [.. TransformDrag.CornersOf(floating.Transform)
                                             .Select(point => LayerGeometry.ToPixels(source.Transform, point, width, height))];
        PixelRect reach = Rect.Around(corners).Enclosing();
        PixelRect extent = PixelRect.FromBounds(Math.Min(0, reach.X), Math.Min(0, reach.Y),
                                                Math.Max(width, reach.Right), Math.Max(height, reach.Bottom));
        if (extent.Width > 30_000 || extent.Height > 30_000 || (long)extent.Width * extent.Height > 100_000_000) return null;

        PixelBuffer result = PixelBuffer.Allocate(extent.Width, extent.Height);
        for (int y = 0; y < height; y++)
            image.Row(y).CopyTo(result.Row(y - extent.Y).Slice(-extent.X * 4, width * 4));

        Span<byte> sample = stackalloc byte[4];
        PixelRect area = reach.Intersect(extent);
        for (int y = area.Y; y < area.Bottom; y++)
        {
            Span<byte> row = result.Row(y - extent.Y);
            for (int x = area.X; x < area.Right; x++)
            {
                Point document = LayerGeometry.ToDocument(source.Transform, new Point(x + 0.5, y + 0.5), width, height);
                Point at = LayerGeometry.ToPixels(floating.Transform, document, lifted.Width, lifted.Height);
                if (!Sample(lifted, at.X, at.Y, sample)) continue;

                Span<byte> pixel = row.Slice((x - extent.X) * 4, 4);
                int keep = 255 - sample[3];
                for (int c = 0; c < 4; c++) pixel[c] = (byte)(sample[c] + (pixel[c] * keep + 127) / 255);
            }
        }

        bool grew = extent.X != 0 || extent.Y != 0 || extent.Width != width || extent.Height != height;
        return source with
        {
            Image = result,
            Transform = grew ? LayerGeometry.Place(source.Transform, extent, width, height) : source.Transform,
            Mask = grew ? Grown(source.Mask, width, height, extent) : source.Mask,
            Shape = null,
        };
    }

    /// <summary>The layer's mask over the grown grid: as it was where the layer was, white beyond.</summary>
    private static LayerMask? Grown(LayerMask? mask, int width, int height, PixelRect extent)
    {
        if (mask is not { Placement: null } owned || owned.Coverage is { Width: 1, Height: 1 }) return mask;

        using PixelBuffer old = owned.Coverage.Width == width && owned.Coverage.Height == height
            ? owned.Coverage.Retain()
            : BackgroundRemoval.Scaled(owned.Coverage, width, height);

        PixelBuffer grown = PixelBuffer.Allocate(extent.Width, extent.Height);
        for (int y = 0; y < extent.Height; y++) grown.Row(y).Fill(255);
        for (int y = 0; y < height; y++)
            old.Row(y).CopyTo(grown.Row(y - extent.Y).Slice(-extent.X * 4, width * 4));
        return owned with { Coverage = grown };
    }

    /// <summary>A bilinear sample of premultiplied pixels, nothing outside them. False when it comes to nothing.</summary>
    private static bool Sample(PixelBuffer pixels, double px, double py, Span<byte> result)
    {
        double x = px - 0.5, y = py - 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double tx = x - x0, ty = y - y0;
        if (x0 < -1 || y0 < -1 || x0 >= pixels.Width || y0 >= pixels.Height) return false;

        Span<double> sum = stackalloc double[4];
        for (int j = 0; j < 2; j++)
        {
            int sy = y0 + j;
            if (sy < 0 || sy >= pixels.Height) continue;
            ReadOnlySpan<byte> row = pixels.Row(sy);
            for (int i = 0; i < 2; i++)
            {
                int sx = x0 + i;
                if (sx < 0 || sx >= pixels.Width) continue;
                double weight = (i == 0 ? 1 - tx : tx) * (j == 0 ? 1 - ty : ty);
                if (weight <= 0) continue;
                for (int c = 0; c < 4; c++) sum[c] += row[sx * 4 + c] * weight;
            }
        }

        for (int c = 0; c < 4; c++) result[c] = (byte)Math.Clamp(Math.Round(sum[c]), 0, 255);
        return result[3] > 0;
    }
}
