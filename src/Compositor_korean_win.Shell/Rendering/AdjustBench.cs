using System.Diagnostics;
using Compositor_korean_win.Core;

namespace Compositor_korean_win.Shell;

/// <summary>
/// What a live preview costs: adjustment layers being drawn and changed, and a filter being tried.
/// </summary>
/// <remarks>
/// <para>
/// M5 closes on "the live preview works", which has to be turned into something a run can fail.
/// What makes a preview live is not the speed of this machine but where the work goes: if changing
/// a setting costs the frame, a preview keeps up on any document; if it costs the layer, no machine
/// keeps up on a large one. So, as for M3 and M4, the judged figures are pixel counts and the
/// times are recorded beside them (docs/progress.md 1).
/// </para>
/// <para>
/// Two paths are measured. An adjustment layer runs over the composited frame, so each one should
/// touch exactly the window's pixels per frame. A filter tried on a layer runs over the layer's
/// reduced, cropped pixels, which should stay within the four-times-the-window bound M3 set for
/// reading — at "fit" and at 100%.
/// </para>
/// </remarks>
internal static class AdjustBench
{
    private const int Frames = 6;

    private const int WarmUp = 2;

    internal sealed record Result
    {
        public required long DocumentPixels { get; init; }
        public required long ViewPixels { get; init; }
        public required int Adjustments { get; init; }
        public required long PixelsAdjustedPerFrame { get; init; }
        public required double PlainFrameMs { get; init; }
        public required double AdjustedFrameMs { get; init; }
        public required double SettingsChangeFrameMs { get; init; }
        public required double PreviewOpenMs { get; init; }
        public required double PreviewFittedMs { get; init; }
        public required long PreviewFittedPixels { get; init; }
        public required double PreviewFullSizeMs { get; init; }
        public required long PreviewFullSizePixels { get; init; }

        /// <summary>Milliseconds each adjustment takes over one window of pixels on its own.</summary>
        public required IReadOnlyList<(AdjustmentKind Kind, double Ms)> PerKindMs { get; init; }

        /// <summary>
        /// The bound M5 closes on: each adjustment touches the window once a frame, and a filter
        /// preview filters no more than four windows' worth, however large the layer.
        /// </summary>
        public bool WithinBudget =>
            PixelsAdjustedPerFrame <= ViewPixels * Adjustments
            && PreviewFittedPixels <= ViewPixels * 4
            && PreviewFullSizePixels <= ViewPixels * 4;
    }

    public static Result Run(GraphicsDevice device, int side, int width, int height)
    {
        PixelBuffer pixels = CanvasBench.Fill(side, side);
        try
        {
            var image = new ImageLayer
            {
                Id = Guid.NewGuid(),
                Name = "bench",
                Image = pixels,
                Transform = new LayerTransform(Point.Zero, new Size(side, side)),
            };

            List<ImageLayer> adjustments = Stack(side);
            var plain = new CanvasDocument
            {
                Id = Guid.NewGuid(),
                Width = side,
                Height = side,
                Layers = new EquatableList<ImageLayer>([image]),
            };
            CanvasDocument adjusted = plain with { Layers = new EquatableList<ImageLayer>([image, .. adjustments]) };

            using var backend = new Direct2DBackend(device);
            using IRenderSurface surface = backend.CreateSurface(width, height);
            var viewport = new CanvasViewport { ViewSize = new Size(width, height) }.Fit(plain.Size);

            double plainMs = Time(plain, viewport, surface, backend, change: null);

            long before = AdjustmentRendering.PixelsAdjusted;
            double adjustedMs = Time(adjusted, viewport, surface, backend, change: null);
            long perFrame = (AdjustmentRendering.PixelsAdjusted - before) / (WarmUp + Frames);

            // A slider being dragged: every frame has a Levels gamma it has not drawn before.
            ImageLayer levels = adjustments.First(layer => layer.Adjustment!.Kind == AdjustmentKind.Levels);
            double changeMs = Time(adjusted, viewport, surface, backend, change: (document, frame) =>
                document.Replacing(levels with
                {
                    Adjustment = levels.Adjustment! with
                    {
                        Levels = Levels(black: 20, gamma: 1.1 + frame * 0.05),
                    },
                }));

            // A filter tried on the layer: opened once, then dragged.
            var clock = Stopwatch.StartNew();
            using var preview = new FilterPreview(image, FilterKind.GaussianBlur, new FilterSettings { Radius = 20 });
            DrawPreview(plain, viewport, surface, backend, preview);
            surface.Read().Release();
            clock.Stop();

            (double fittedMs, long fittedPixels) = TimePreview(plain, viewport, surface, backend, preview);

            CanvasViewport close = viewport.ZoomedTo(1, viewport.Center, plain.Size);
            (double closeMs, long closePixels) = TimePreview(plain, close, surface, backend, preview);

            var perKind = new List<(AdjustmentKind, double)>();
            using (PixelBuffer window = CanvasBench.Fill(width, height))
            {
                foreach (ImageLayer layer in adjustments)
                    perKind.Add((layer.Adjustment!.Kind, TimeKind(layer.Adjustment, window)));
            }

            return new Result
            {
                PerKindMs = perKind,
                DocumentPixels = (long)side * side,
                ViewPixels = (long)width * height,
                Adjustments = adjustments.Count,
                PixelsAdjustedPerFrame = perFrame,
                PlainFrameMs = plainMs,
                AdjustedFrameMs = adjustedMs,
                SettingsChangeFrameMs = changeMs,
                PreviewOpenMs = clock.Elapsed.TotalMilliseconds,
                PreviewFittedMs = fittedMs,
                PreviewFittedPixels = fittedPixels,
                PreviewFullSizeMs = closeMs,
                PreviewFullSizePixels = closePixels,
            };
        }
        finally
        {
            pixels.Release();
        }
    }

    /// <summary>One of each adjustment, as a stack over the layer.</summary>
    private static List<ImageLayer> Stack(int side)
    {
        LayerAdjustment[] settings =
        [
            new(AdjustmentKind.Hsv) { HsvSettings = HueSaturationSettings.From(30, 10, 0, colorize: false) },
            new(AdjustmentKind.Levels) { Levels = Levels(black: 20, gamma: 1.2) },
            new(AdjustmentKind.Curves)
            {
                Curves = new CurvesSettings
                {
                    Channels = new EquatableList<EquatableList<CurvePoint>>(
                    [
                        new([new CurvePoint(0, 0), new CurvePoint(128, 150), new CurvePoint(255, 255)]),
                        new([new CurvePoint(0, 0), new CurvePoint(255, 255)]),
                        new([new CurvePoint(0, 0), new CurvePoint(255, 255)]),
                        new([new CurvePoint(0, 0), new CurvePoint(255, 255)]),
                    ]),
                },
            },
            new(AdjustmentKind.Exposure) { ExposureSettings = new ExposureSettings { Exposure = 0.5 } },
            new(AdjustmentKind.GradientMap)
            {
                GradientMapSettings = new GradientMapSettings { Shadows = new AdjustmentColor(0.1, 0.1, 0.4) },
            },
            new(AdjustmentKind.Grain) { GrainSettings = new GrainSettings { Amount = 30, Seed = 7 } },
        ];

        return
        [
            .. settings.Select(adjustment => new ImageLayer
            {
                Id = Guid.NewGuid(),
                Name = adjustment.Kind.ToString(),
                Transform = new LayerTransform(Point.Zero, new Size(side, side)),
                Adjustment = adjustment,
                Opacity = adjustment.Kind == AdjustmentKind.GradientMap ? 0.5 : 1,
            }),
        ];
    }

    /// <summary>Milliseconds one adjustment takes over a copy of <paramref name="window"/>.</summary>
    private static double TimeKind(LayerAdjustment adjustment, PixelBuffer window)
    {
        var clock = new Stopwatch();
        for (int i = 0; i < 1 + Frames; i++)
        {
            PixelBuffer copy = PixelRegion.Copy(window, new PixelRect(0, 0, window.Width, window.Height));
            if (i > 0) clock.Start();
            AdjustmentRendering.Apply(adjustment, copy, PixelPlacement.Document);
            clock.Stop();
            copy.Release();
        }
        return clock.Elapsed.TotalMilliseconds / Frames;
    }

    private static LevelsSettings Levels(double black, double gamma) => new()
    {
        Ranges = new EquatableList<LevelRange>([new LevelRange { Black = black, Gamma = gamma }, new(), new(), new()]),
    };

    /// <summary>Milliseconds a frame takes, optionally changing the document before each.</summary>
    private static double Time(CanvasDocument document, CanvasViewport viewport, IRenderSurface surface,
                               IRenderBackend backend, Func<CanvasDocument, int, CanvasDocument>? change)
    {
        for (int i = 0; i < WarmUp; i++)
        {
            surface.Clear();
            LayerCompositor.DrawView(change?.Invoke(document, -1 - i) ?? document, viewport, surface, backend);
        }

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < Frames; i++)
        {
            surface.Clear();
            LayerCompositor.DrawView(change?.Invoke(document, i) ?? document, viewport, surface, backend);
        }

        // Forces the frames through before the clock stops, as the canvas bench does.
        surface.Read().Release();
        clock.Stop();

        return clock.Elapsed.TotalMilliseconds / Frames;
    }

    private static void DrawPreview(CanvasDocument document, CanvasViewport viewport, IRenderSurface surface,
                                    IRenderBackend backend, FilterPreview preview)
    {
        LiveEdit live = preview.Frame(viewport.DeviceProjection(document.Size),
                                      surface.Width, surface.Height);
        surface.Clear();
        LayerCompositor.DrawView(document, viewport, surface, backend, live);
    }

    /// <summary>
    /// Milliseconds from a filter setting changing to the frame showing it, and the pixels filtered.
    /// </summary>
    private static (double Ms, long Pixels) TimePreview(CanvasDocument document, CanvasViewport viewport,
                                                        IRenderSurface surface, IRenderBackend backend,
                                                        FilterPreview preview)
    {
        DrawPreview(document, viewport, surface, backend, preview);

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < Frames; i++)
        {
            preview.Settings = preview.Settings with { Radius = 20 + i };
            DrawPreview(document, viewport, surface, backend, preview);
        }

        surface.Read().Release();
        clock.Stop();

        return (clock.Elapsed.TotalMilliseconds / Frames, preview.LastPixelsFiltered);
    }
}
