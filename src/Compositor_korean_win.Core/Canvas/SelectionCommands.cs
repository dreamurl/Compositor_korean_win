namespace Compositor_korean_win.Core;

/// <summary>
/// The Select menu's edits: inverse, a layer's pixels, and growing or shrinking by a distance.
/// </summary>
/// <remarks>
/// <para>
/// A selection is outlines, not pixels (<see cref="DocumentSelection"/>), so inverting one is just
/// another step — the canvas minus what was selected — and stays exact. Growing and shrinking have
/// no outline form; they go through pixels and come back as outlines traced along pixel edges by
/// the wand's kernel, which is what upstream does as well.
/// </para>
/// <para>
/// Distance is Euclidean, from an exact distance transform, so an expanded corner is rounded the
/// way Photoshop rounds it rather than squared off. The canvas edge does not count as unselected
/// when shrinking — Photoshop's default — so a selection of the whole canvas contracted is still
/// the whole canvas.
/// </para>
/// </remarks>
public static class SelectionCommands
{
    /// <summary>Everything on the canvas the selection did not cover; null when there is none.</summary>
    public static DocumentSelection? Inverse(DocumentSelection? selection, int width, int height) =>
        selection is null
            ? null
            : DocumentSelection.Rectangle(new Rect(0, 0, width, height)).Subtracting(selection);

    /// <summary>
    /// The pixels a layer covers, its own mask included, as a selection — what Photoshop's
    /// Ctrl-click on a thumbnail gives. Null when the layer covers nothing.
    /// </summary>
    public static DocumentSelection? FromLayer(CanvasDocument document, ImageLayer layer)
    {
        if (layer.Image is null || layer.IsGroup) return null;

        // The layer on its own, as it is placed: shown whatever its eye says, clipped to nothing,
        // at full opacity — a selection is about where the pixels are, not how they are drawn.
        CanvasDocument alone = document with
        {
            Layers = new EquatableList<ImageLayer>(
            [
                layer with { IsVisible = true, ParentId = null, MaskSourceId = null, Opacity = 1, BlendMode = LayerBlendMode.Normal },
            ]),
        };

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer pixels = LayerCompositor.Render(alone, backend);

        var mask = new byte[document.Width * document.Height];
        for (int y = 0; y < document.Height; y++)
        {
            ReadOnlySpan<byte> row = pixels.Row(y);
            for (int x = 0; x < document.Width; x++)
                mask[y * document.Width + x] = row[x * 4 + 3] >= 128 ? (byte)1 : (byte)0;
        }

        return MagicWand.Outline(mask, document.Width, document.Height).Selection;
    }

    /// <summary>The selection grown outwards by <paramref name="amount"/> pixels.</summary>
    public static DocumentSelection? Expand(DocumentSelection selection, int width, int height, int amount) =>
        Grow(selection, width, height, Math.Max(0, amount));

    /// <summary>The selection pulled inwards by <paramref name="amount"/> pixels.</summary>
    public static DocumentSelection? Contract(DocumentSelection selection, int width, int height, int amount) =>
        Grow(selection, width, height, -Math.Max(0, amount));

    private static DocumentSelection? Grow(DocumentSelection selection, int width, int height, int amount)
    {
        if (amount == 0) return selection;

        // Only the part of the canvas the change can reach is worked on.
        PixelRect canvas = new(0, 0, width, height);
        PixelRect region = selection.Bounds.Inflate(Math.Abs(amount) + 2).Enclosing().Intersect(canvas);
        if (region.IsEmpty) return null;

        byte[] levels = selection.Levels(region);
        int w = region.Width, h = region.Height;
        bool expanding = amount > 0;

        // The squared distance from each pixel to the nearest pixel of the other kind: to the
        // nearest selected one when expanding, the nearest unselected one when contracting.
        var distance = new float[w * h];
        for (int i = 0; i < distance.Length; i++)
        {
            bool selected = levels[i] >= 128;
            distance[i] = selected == expanding ? 0 : float.PositiveInfinity;
        }

        // Just past the region: unselected canvas — a zero when contracting — except past the
        // canvas itself, which does not count as unselected (Photoshop's default).
        bool OffCanvas(int x, int y) =>
            region.X + x < 0 || region.Y + y < 0 || region.X + x >= width || region.Y + y >= height;

        DistanceTransform(distance, w, h, (x, y) => !expanding && !OffCanvas(x, y));

        float limit = (float)amount * amount;
        var mask = new byte[w * h];
        for (int i = 0; i < mask.Length; i++)
        {
            bool selected = levels[i] >= 128;
            mask[i] = expanding
                ? (byte)(selected || distance[i] <= limit ? 1 : 0)
                : (byte)(selected && distance[i] > limit ? 1 : 0);
        }

        DocumentSelection? traced = MagicWand.Outline(mask, w, h).Selection;
        return traced?.Transformed(point => new Point(point.X + region.X, point.Y + region.Y));
    }

    /// <summary>
    /// Squared Euclidean distance to the nearest zero, in place (Felzenszwalb and Huttenlocher's
    /// separable transform: exact, and linear in the number of pixels).
    /// </summary>
    /// <param name="outsideIsFeature">
    /// Whether pixels one step past the grid on a given side count as zeros; asked of the row or
    /// column just past each edge.
    /// </param>
    internal static void DistanceTransform(float[] grid, int width, int height, Func<int, int, bool> outsideIsFeature)
    {
        int longest = Math.Max(width, height) + 2;
        var line = new float[longest];
        var output = new float[longest];
        var hull = new int[longest];
        var boundary = new float[longest + 1];

        // Columns, with one extra sample past each end standing for what lies outside.
        for (int x = 0; x < width; x++)
        {
            line[0] = outsideIsFeature(x, -1) ? 0 : float.PositiveInfinity;
            for (int y = 0; y < height; y++) line[y + 1] = grid[y * width + x];
            line[height + 1] = outsideIsFeature(x, height) ? 0 : float.PositiveInfinity;

            Transform1D(line, height + 2, output, hull, boundary);
            for (int y = 0; y < height; y++) grid[y * width + x] = output[y + 1];
        }

        for (int y = 0; y < height; y++)
        {
            line[0] = outsideIsFeature(-1, y) ? 0 : float.PositiveInfinity;
            for (int x = 0; x < width; x++) line[x + 1] = grid[y * width + x];
            line[width + 1] = outsideIsFeature(width, y) ? 0 : float.PositiveInfinity;

            Transform1D(line, width + 2, output, hull, boundary);
            for (int x = 0; x < width; x++) grid[y * width + x] = output[x + 1];
        }
    }

    /// <summary>The one-dimensional pass: lower envelope of parabolas rooted at each sample.</summary>
    private static void Transform1D(float[] f, int n, float[] d, int[] v, float[] z)
    {
        int k = -1;

        for (int q = 0; q < n; q++)
        {
            if (float.IsPositiveInfinity(f[q])) continue;

            if (k < 0)
            {
                k = 0;
                v[0] = q;
                z[0] = float.NegativeInfinity;
                z[1] = float.PositiveInfinity;
                continue;
            }

            // Drop the parabolas the new one hides; z[0] is minus infinity, so this stops at the first.
            float s = Intersection(f, v[k], q);
            while (s <= z[k])
            {
                k--;
                s = Intersection(f, v[k], q);
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }

        if (k < 0)
        {
            Array.Fill(d, float.PositiveInfinity, 0, n);
            return;
        }

        int j = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[j + 1] < q) j++;
            float offset = q - v[j];
            d[q] = offset * offset + f[v[j]];
        }

        static float Intersection(float[] f, int p, int q) =>
            ((f[q] + (float)q * q) - (f[p] + (float)p * p)) / (2f * (q - p));
    }
}
