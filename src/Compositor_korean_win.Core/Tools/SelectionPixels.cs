namespace Compositor_korean_win.Core;

/// <summary>
/// Moving the pixels inside a selection, rather than the outline around them.
/// </summary>
/// <remarks>
/// <para>
/// Upstream floats them: the pixels are lifted, dragged about, and merged back when the selection
/// is dropped. This does the same thing in one step at the end of the drag, which is the same
/// result and keeps a partly-moved document out of the history — a floating selection is a state
/// the format cannot store, and M4 has no reason to invent one.
/// </para>
/// <para>
/// The soft edge of a selection is what makes this more than a copy. A pixel half inside comes
/// away at half strength and lands at half strength, so a feathered selection arrives with the
/// same soft edge it left with rather than a cut one.
/// </para>
/// </remarks>
public static class SelectionPixels
{
    /// <summary>
    /// <paramref name="layer"/> with what <paramref name="selection"/> covers moved by
    /// <paramref name="offset"/>, in layer pixels.
    /// </summary>
    /// <param name="duplicate">Leaves the pixels where they were as well as putting them down.</param>
    /// <remarks>The caller owns the result and releases it.</remarks>
    public static PixelBuffer Move(PixelBuffer layer, DocumentSelection selection, Point offset,
                                   bool duplicate = false)
    {
        var whole = new PixelRect(0, 0, layer.Width, layer.Height);
        (PixelRect source, byte[] coverage) = selection.Clip(layer.Width, layer.Height);

        PixelBuffer result = PixelRegion.Copy(layer, whole);
        if (source.IsEmpty) return result;

        int dx = (int)Math.Round(offset.X, MidpointRounding.AwayFromZero);
        int dy = (int)Math.Round(offset.Y, MidpointRounding.AwayFromZero);

        // What the selection holds, at the strength it holds it: a pixel half inside comes away
        // half there, which is what a feathered edge means.
        PixelBuffer floated = PixelBuffer.Allocate(source.Width, source.Height);

        try
        {
            for (int y = 0; y < source.Height; y++)
            {
                ReadOnlySpan<byte> from = layer.Row(source.Y + y);
                Span<byte> to = floated.Row(y);

                for (int x = 0; x < source.Width; x++)
                {
                    int level = coverage[y * source.Width + x];
                    if (level == 0) continue;

                    for (int channel = 0; channel < 4; channel++)
                    {
                        to[x * 4 + channel] = (byte)((from[(source.X + x) * 4 + channel] * level + 127) / 255);
                    }
                }
            }

            if (!duplicate) Erase(result, source, coverage);
            Stamp(result, floated, source.X + dx, source.Y + dy);

            return result;
        }
        finally
        {
            floated.Release();
        }
    }

    /// <summary>Takes the selected strength out of where the pixels were.</summary>
    private static void Erase(PixelBuffer target, PixelRect region, byte[] coverage)
    {
        for (int y = 0; y < region.Height; y++)
        {
            Span<byte> row = target.Row(region.Y + y);

            for (int x = 0; x < region.Width; x++)
            {
                int level = coverage[y * region.Width + x];
                if (level == 0) continue;

                for (int channel = 0; channel < 4; channel++)
                {
                    int at = (region.X + x) * 4 + channel;
                    row[at] = (byte)(row[at] * (255 - level) / 255);
                }
            }
        }
    }

    /// <summary>
    /// Composites the floated pixels down at their new place.
    /// </summary>
    /// <remarks>
    /// Source-over on the floated pixel's own alpha, not on the coverage. They are the same where
    /// the layer was opaque and the selection full, and they are not where either was partial —
    /// and it is the pixel's alpha that says how much of what is underneath should still show.
    /// </remarks>
    private static void Stamp(PixelBuffer target, PixelBuffer floated, int left, int top)
    {
        for (int y = 0; y < floated.Height; y++)
        {
            int targetY = top + y;
            if (targetY < 0 || targetY >= target.Height) continue;

            ReadOnlySpan<byte> from = floated.Row(y);
            Span<byte> row = target.Row(targetY);

            for (int x = 0; x < floated.Width; x++)
            {
                int targetX = left + x;
                if (targetX < 0 || targetX >= target.Width) continue;

                int alpha = from[x * 4 + 3];
                if (alpha == 0) continue;

                double keep = 1 - alpha / 255.0;
                for (int channel = 0; channel < 4; channel++)
                {
                    int at = targetX * 4 + channel;
                    row[at] = (byte)Math.Clamp(
                        Math.Round(from[x * 4 + channel] + row[at] * keep,
                                   MidpointRounding.AwayFromZero), 0, 255);
                }
            }
        }
    }
}
