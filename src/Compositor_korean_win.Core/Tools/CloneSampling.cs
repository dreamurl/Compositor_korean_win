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
    /// <summary>Every visible layer as the document shows them, in a layer's grid. The caller owns the result.</summary>
    public static PixelBuffer AllLayers(CanvasDocument document, LayerTransform placement, int width, int height)
    {
        using var backend = new SoftwareRenderBackend();
        PixelBuffer composite = LayerCompositor.Render(document, backend);

        bool square = placement.Origin == Point.Zero && placement.Rotation % 360 == 0
                      && !placement.FlipX && !placement.FlipY
                      && placement.Size.Width == width && placement.Size.Height == height
                      && width == composite.Width && height == composite.Height;
        if (square) return composite;

        try
        {
            PixelBuffer result = PixelBuffer.Allocate(width, height);
            for (int y = 0; y < height; y++)
            {
                Span<byte> row = result.Row(y);
                for (int x = 0; x < width; x++)
                {
                    Point at = LayerGeometry.ToDocument(placement, new Point(x + 0.5, y + 0.5), width, height);
                    int sx = (int)Math.Floor(at.X), sy = (int)Math.Floor(at.Y);
                    if (sx < 0 || sy < 0 || sx >= composite.Width || sy >= composite.Height) continue;
                    composite.Row(sy).Slice(sx * 4, 4).CopyTo(row.Slice(x * 4, 4));
                }
            }
            return result;
        }
        finally
        {
            composite.Release();
        }
    }
}
