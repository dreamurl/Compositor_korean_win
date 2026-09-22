using System.Diagnostics;
using Compositor_korean_win.Core;

namespace Compositor_korean_win.Shell;

/// <summary>
/// What a brush stroke costs, and whether it costs more on a larger layer.
/// </summary>
/// <remarks>
/// <para>
/// M4 closes on stroke latency, which is the delay between the pointer moving and the paint
/// appearing. Two things make it up and both are measured here: the time to take the stroke on to
/// the next point, which is tiles being stamped and rebuilt, and the time to draw the frame that
/// shows it. The absolute figures belong to whatever adapter drew them, which the report names.
/// </para>
/// <para>
/// The figure that matters is neither of those on its own but the comparison between them on a
/// megapixel layer and on a hundred-megapixel one. Tile replacement's whole claim
/// (docs/windows-port.md §2.3) is that a stroke costs what it touched; if that holds, the two are
/// the same number, and if it does not, no amount of tuning will save a large document.
/// </para>
/// </remarks>
internal static class StrokeBench
{
    private const int Moves = 90;

    private const double PixelsPerMove = 3;

    internal sealed record Result
    {
        public required double SmallAppendMs { get; init; }
        public required double LargeAppendMs { get; init; }
        public required double LiveFrameMs { get; init; }
        public required int TilesTouched { get; init; }
        public required long LargeLayerPixels { get; init; }

        /// <summary>
        /// The claim M4 rests on: painting a hundred-megapixel layer costs what painting a
        /// megapixel one does, because both touched the same tiles.
        /// </summary>
        public bool WithinBudget => LargeAppendMs <= Math.Max(0.5, SmallAppendMs * 4);
    }

    public static Result Run(GraphicsDevice device, int side, int width, int height)
    {
        var settings = new BrushSettings { Diameter = 40, Hardness = 0.8, Color = new Rgba(20, 30, 40) };

        using (PixelBuffer small = PixelBuffer.Allocate(1024, 1024))
        {
            double smallCost = Time(small, 1024, settings);

            PixelBuffer large = PixelBuffer.Allocate(side, side);
            try
            {
                double largeCost = Time(large, side, settings);
                (double frame, int tiles) = LiveFrame(device, large, side, width, height, settings);

                return new Result
                {
                    SmallAppendMs = smallCost,
                    LargeAppendMs = largeCost,
                    LiveFrameMs = frame,
                    TilesTouched = tiles,
                    LargeLayerPixels = (long)side * side,
                };
            }
            finally
            {
                large.Release();
            }
        }
    }

    /// <summary>Milliseconds to carry a stroke on to one more point.</summary>
    private static double Time(PixelBuffer layer, int side, BrushSettings settings)
    {
        using var stroke = new BrushStroke(layer, side, side, settings);
        stroke.Append(new Point(side / 4.0, side / 2.0));

        var clock = Stopwatch.StartNew();
        for (int i = 1; i <= Moves; i++)
        {
            stroke.Append(new Point(side / 4.0 + i * PixelsPerMove, side / 2.0));
        }
        clock.Stop();

        return clock.Elapsed.TotalMilliseconds / Moves;
    }

    /// <summary>
    /// Milliseconds to draw a frame with a stroke live on it, and how many tiles that stroke holds.
    /// </summary>
    private static (double FrameMs, int Tiles) LiveFrame(GraphicsDevice device, PixelBuffer layer, int side,
                                                         int width, int height, BrushSettings settings)
    {
        var image = new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = "paint",
            Image = layer,
            Transform = new LayerTransform(Point.Zero, new Size(side, side)),
        };

        var document = new CanvasDocument
        {
            Id = Guid.NewGuid(),
            Width = side,
            Height = side,
            Layers = new EquatableList<ImageLayer>([image]),
        };

        using var stroke = new BrushStroke(layer, side, side, settings);
        for (int i = 0; i <= Moves; i++) stroke.Append(new Point(side / 4.0 + i * PixelsPerMove, side / 2.0));

        using var backend = new Direct2DBackend(device);
        using IRenderSurface surface = backend.CreateSurface(width, height);

        var viewport = new CanvasViewport { ViewSize = new Size(width, height) }
            .Fit(document.Size)
            .ZoomedTo(1, new Point(width / 2.0, height / 2.0), document.Size);

        var live = new LiveEdit(image.Id, new LayerRaster(layer, stroke.Patches));

        for (int i = 0; i < 3; i++)
        {
            surface.Clear();
            LayerCompositor.DrawView(document, viewport, surface, backend, live);
        }

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 8; i++)
        {
            surface.Clear();
            LayerCompositor.DrawView(document, viewport, surface, backend, live);
        }

        // Forces the frames through before the clock stops, as the canvas bench does.
        surface.Read().Release();
        clock.Stop();

        return (clock.Elapsed.TotalMilliseconds / 8, stroke.Patches.Count);
    }
}
