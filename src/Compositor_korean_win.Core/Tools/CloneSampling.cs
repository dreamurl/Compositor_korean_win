namespace Compositor_korean_win.Core;

/// <summary>
/// What the clone stamp copies from when it samples every layer — upstream's
/// <c>CloneSettings.sampleAllLayers</c>.
/// </summary>
/// <remarks>
/// <para>
/// Upstream draws the document as the canvas shows it and copies from that. The stroke here works
/// in the painted layer's own grid (<see cref="BrushStroke"/>), so the composite is carried over to
/// that grid: for each layer pixel, the document pixel under its centre. A layer that sits square
/// on the canvas at its own size — the usual case, a photo or a blank layer — needs no carrying at
/// all and gets the composite as it is.
/// </para>
/// <para>
/// Nearest, not filtered: the clone offset is whole pixels too, and a blurred copy of the
/// document would show where the stamp had been.
/// </para>
/// </remarks>
public static class CloneSampling
{
    /// <summary>
    /// Every visible layer as a lazy source in the target layer's grid. Each requested tile renders
    /// only the document rectangle that can feed it, which is the CPU fallback for Clone Stamp.
    /// </summary>
    public static IPixelSource AllLayersSource(CanvasDocument document, LayerTransform placement, int width, int height) =>
        new DocumentSource(document, placement, width, height);

    /// <summary>Every visible layer as the document shows them, in a layer's grid. The caller owns the result.</summary>
    public static PixelBuffer AllLayers(CanvasDocument document, LayerTransform placement, int width, int height)
    {
        return AllLayersSource(document, placement, width, height)
            .Materialize(new PixelRect(0, 0, width, height));
    }

    private sealed class DocumentSource(CanvasDocument document, LayerTransform placement, int width, int height)
        : IPixelSource
    {
        public int Width => width;
        public int Height => height;

        public PixelBuffer Materialize(PixelRect region)
        {
            if (region.IsEmpty) throw new ArgumentException("an empty region", nameof(region));
            PixelBuffer result = PixelBuffer.Allocate(region.Width, region.Height);
            PixelRect target = region.Intersect(new PixelRect(0, 0, width, height));
            if (target.IsEmpty) return result;

            Point[] corners =
            [
                LayerGeometry.ToDocument(placement, new Point(target.X, target.Y), width, height),
                LayerGeometry.ToDocument(placement, new Point(target.Right, target.Y), width, height),
                LayerGeometry.ToDocument(placement, new Point(target.Right, target.Bottom), width, height),
                LayerGeometry.ToDocument(placement, new Point(target.X, target.Bottom), width, height),
            ];
            double left = corners.Min(point => point.X), top = corners.Min(point => point.Y);
            double right = corners.Max(point => point.X), bottom = corners.Max(point => point.Y);
            PixelRect documentRegion = Rect.FromBounds(left, top, right, bottom).Enclosing().Inflate(1)
                .Intersect(new PixelRect(0, 0, document.Width, document.Height));
            if (documentRegion.IsEmpty) return result;

            using var backend = new SoftwareRenderBackend();
            using IRenderSurface surface = backend.CreateSurface(documentRegion.Width, documentRegion.Height);
            surface.Clear();
            var projection = new CanvasProjection(1, new Point(-documentRegion.X, -documentRegion.Y));
            LayerCompositor.Draw(document, surface, backend, projection);
            using PixelBuffer composite = surface.Read();

            Parallel.For(target.Y, target.Bottom, y =>
            {
                Span<byte> row = result.Row(y - region.Y);
                for (int x = target.X; x < target.Right; x++)
                {
                    Point at = LayerGeometry.ToDocument(placement, new Point(x + 0.5, y + 0.5), width, height);
                    int sx = (int)Math.Floor(at.X) - documentRegion.X;
                    int sy = (int)Math.Floor(at.Y) - documentRegion.Y;
                    if (sx < 0 || sy < 0 || sx >= composite.Width || sy >= composite.Height) continue;
                    composite.Row(sy).Slice(sx * 4, 4).CopyTo(row.Slice((x - region.X) * 4, 4));
                }
            });
            return result;
        }
    }
}
