using System.Diagnostics;
using Compositor_korean_win.Core;

namespace Compositor_korean_win.Shell;

/// <summary>
/// What a frame of a hundred-megapixel document costs.
/// </summary>
/// <remarks>
/// <para>
/// M3's original completion criterion was sixty frames a second on a hundred megapixels, which
/// cannot be shown here: the runner has no GPU, so this is WARP, and nothing is built locally
/// (docs/windows-port.md §10.4). What sixty frames a second actually asks for is that a frame's
/// work follow the window rather than the document, and that is what this measures — the same
/// window, the same zooms, a document a hundred times the size of the one beside it.
/// </para>
/// <para>
/// The figures are recorded, not judged. What is judged is the invariant: the pixels a frame reads
/// stay inside four times the window's own, whatever the document.
/// </para>
/// </remarks>
internal static class CanvasBench
{
    /// <summary>Frames timed at each zoom, after the warm-up.</summary>
    private const int Frames = 12;

    private const int WarmUp = 3;

    internal sealed record Result
    {
        public required long DocumentPixels { get; init; }
        public required long ViewPixels { get; init; }
        public required long PixelsRead { get; init; }
        public required double FittedMs { get; init; }
        public required double FullSizeMs { get; init; }
        public required long PeakWorkingSetBytes { get; init; }

        /// <summary>The bound M3 closes on: a frame follows the window, not the document.</summary>
        public bool WithinBudget => PixelsRead <= ViewPixels * 4;
    }

    /// <summary>
    /// Times a window's worth of frames over <paramref name="side"/>² pixels of layer.
    /// </summary>
    public static Result Run(GraphicsDevice device, int side, int width, int height)
    {
        PixelBuffer pixels = Fill(side, side);

        var layer = new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = "bench",
            Image = pixels,
            Transform = new LayerTransform(Point.Zero, new Size(side, side)),
        };

        var document = new CanvasDocument
        {
            Id = Guid.NewGuid(),
            Width = side,
            Height = side,
            Layers = new EquatableList<ImageLayer>([layer]),
        };

        // An offscreen target of the window's size rather than the window's own back buffer: the
        // drawing is identical, and the device holds one swap chain, which the self-test's window
        // already has.
        using var backend = new Direct2DBackend(device);
        using IRenderSurface surface = backend.CreateSurface(width, height);

        var viewport = new CanvasViewport { ViewSize = new Size(width, height) }.Fit(document.Size);
        double fitted = Time(document, viewport, surface, backend);

        CanvasViewport close = viewport.ZoomedTo(1, viewport.Center, document.Size);
        double fullSize = Time(document, close, surface, backend);

        long peak = Win32.PeakWorkingSet();
        pixels.Release();

        return new Result
        {
            DocumentPixels = (long)side * side,
            ViewPixels = (long)width * height,
            PixelsRead = viewport.PixelsToRead(document.Size),
            FittedMs = fitted,
            FullSizeMs = fullSize,
            PeakWorkingSetBytes = peak,
        };
    }

    /// <summary>
    /// Milliseconds a frame takes, panning between them so nothing is cached into a free ride.
    /// </summary>
    /// <remarks>
    /// A still frame would re-use every upload and measure the compositor at its easiest. Panning a
    /// point at a time is the ordinary worst case: the same work, and a new region to send up.
    /// </remarks>
    private static double Time(CanvasDocument document, CanvasViewport viewport,
                               IRenderSurface surface, IRenderBackend backend)
    {
        for (int i = 0; i < WarmUp; i++)
        {
            surface.Clear();
            LayerCompositor.DrawView(document, viewport, surface, backend);
        }

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < Frames; i++)
        {
            viewport = viewport.Translated(new Point(1, 0));
            surface.Clear();
            LayerCompositor.DrawView(document, viewport, surface, backend);
        }

        // Direct2D hands work to the driver and returns, so the clock has to be stopped after
        // something has forced it through. Reading the surface does that. One window's worth of
        // pixels copied back is counted in with the frames, which makes the figure an upper bound
        // — the honest direction for a number nobody is allowed to pass or fail on.
        surface.Read().Release();
        clock.Stop();

        return clock.Elapsed.TotalMilliseconds / Frames;
    }

    /// <summary>
    /// A layer of the given size, filled a row at a time.
    /// </summary>
    /// <remarks>
    /// Four hundred megabytes of it, so the fill is one row built by hand and copied down the
    /// buffer: what is being measured is the drawing, not this.
    /// </remarks>
    private static PixelBuffer Fill(int width, int height)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);

        Span<byte> first = buffer.Row(0);
        for (int x = 0; x < width; x++)
        {
            byte value = (byte)(x * 255 / Math.Max(1, width - 1));
            first[x * 4 + 0] = value;
            first[x * 4 + 1] = (byte)(255 - value);
            first[x * 4 + 2] = 128;
            first[x * 4 + 3] = 255;
        }

        for (int y = 1; y < height; y++) first.CopyTo(buffer.Row(y));
        return buffer;
    }
}
