using System.Diagnostics;
using Compositor_korean_win.Core;
using Vortice.DXGI;

namespace Compositor_korean_win.Shell;

/// <summary>
/// M7's check: in the AI build, the model loads, runs, and finds the subject of a picture that has
/// an obvious one; in the plain build, it reports the model absent and nothing fails.
/// </summary>
/// <remarks>
/// The picture is made here rather than shipped: a dark disc on a pale ground, which any
/// segmentation model calls a subject. The logits go through the same <see cref="SubjectMatte"/>
/// the filter uses, so a model whose output needs no sigmoid, or planes in the wrong order, shows
/// up as a disc that is not white.
/// </remarks>
internal static class AiCheck
{
    internal sealed record Result(bool Installed, string Device, double LoadMs, double RunMs,
                                  double Inside, double Outside, string? Error)
    {
        public bool Passed => !Installed || Error is null && Inside > 0.6 && Outside < 0.4;

        /// <summary>Why DirectML was not used, when it was not.</summary>
        public string? GpuError { get; init; }

        /// <summary>What the canvas's Remove Background got wrong, or null; "not run" in the plain build.</summary>
        public string? Canvas { get; init; }
    }

    public static Result Run(GraphicsDevice device, Format format)
    {
        Result model = RunModel();
        return model with { GpuError = SubjectModels.GpuError, Canvas = RunCanvas(device, format, model.Installed) };
    }

    /// <summary>
    /// Filter › Remove Background through the canvas, the way the menu runs it: in the AI build the
    /// disc's layer ends with a mask white over the disc and black around it, as one history step,
    /// with the mask as the target; in the plain build the command is there and cannot run.
    /// </summary>
    private static string? RunCanvas(GraphicsDevice device, Format format, bool installed)
    {
        using var window = new MainWindow(device, format, 1024, 700, visible: false);
        using var canvas = new CanvasView(device);
        window.AttachCanvas(canvas);
        var files = new DocumentFiles(window.Handle, canvas, format) { Quiet = true };
        (List<Command> commands, List<MenuEntry.Submenu> layout) = AppCommands.Create(canvas, files, window.Handle);
        using var menu = new MenuBar(window.Handle, commands, layout);

        canvas.Open(DocumentFiles.FromImage(Disc(640, 480, 130), "disc"), path: null, "disc");
        canvas.ChooseTopImageLayer();
        Guid id = canvas.ActiveLayer!.Id;

        bool ran = menu.Run(CommandIds.FilterFirst + (int)FilterCommand.RemoveBackground);
        if (!installed) return ran ? "the plain build ran Remove Background" : null;
        if (!ran) return "Remove Background did not run";

        for (int wait = 0; wait < 2400 && canvas.BackgroundWorking; wait++)
        {
            Thread.Sleep(50);
            canvas.Tick();
        }
        if (!canvas.BackgroundReady) return "the model did not answer: " + (canvas.BackgroundFailure ?? "timed out");

        canvas.FilterSettings = canvas.FilterSettings with
        {
            Background = new BackgroundSettings { Quality = BackgroundQuality.Advanced, ShiftEdge = -2 },
        };
        string before = canvas.UndoName;
        canvas.FinishFilter(keep: true);

        ImageLayer layer = canvas.Document!.Layer(id)!;
        if (layer.Mask is not LayerMask mask) return "no mask was laid down";
        if (mask.Coverage.Width != 640 || mask.Coverage.Height != 480) return $"the mask is {mask.Coverage.Width}×{mask.Coverage.Height}";
        int centre = mask.Coverage.Row(240)[320 * 4], corner = mask.Coverage.Row(10)[10 * 4];
        if (centre < 200 || corner > 55) return $"the mask is {centre} over the disc and {corner} in the corner";
        if (canvas.UndoName == before) return "no history step";
        if (!canvas.EditingMask) return "the new mask is not the target";
        return null;
    }

    private static Result RunModel()
    {
        if (!SubjectModels.Installed) return new Result(false, "none", 0, 0, 0, 0, null);

        var clock = Stopwatch.StartNew();
        ISubjectModel? model = SubjectModels.Shared(out string? error);
        double loadMs = clock.Elapsed.TotalMilliseconds;
        if (model is null) return new Result(true, "none", loadMs, 0, 0, 0, error ?? "did not load");

        const int Width = 640, Height = 480, Radius = 130;
        using PixelBuffer picture = Disc(Width, Height, Radius);

        clock.Restart();
        float[] logits = model.Predict(SubjectMatte.Input(picture, model.Side));
        double runMs = clock.Elapsed.TotalMilliseconds;

        using PixelBuffer mask = SubjectMatte.Mask(logits, model.Side, Width, Height);
        double inside = 0, outside = 0;
        int insideCount = 0, outsideCount = 0;
        for (int y = 0; y < Height; y++)
        {
            ReadOnlySpan<byte> row = mask.Row(y);
            for (int x = 0; x < Width; x++)
            {
                double distance = Math.Sqrt(Math.Pow(x - Width / 2.0, 2) + Math.Pow(y - Height / 2.0, 2));
                // A margin either side of the rim, where any model is allowed to be unsure.
                if (distance < Radius - 12) { inside += row[x * 4] / 255.0; insideCount++; }
                else if (distance > Radius + 12) { outside += row[x * 4] / 255.0; outsideCount++; }
            }
        }

        return new Result(true, model.Device, loadMs, runMs, inside / insideCount, outside / outsideCount, null);
    }

    private static PixelBuffer Disc(int width, int height, int radius)
    {
        PixelBuffer picture = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = picture.Row(y);
            for (int x = 0; x < width; x++)
            {
                double distance = Math.Sqrt(Math.Pow(x - width / 2.0, 2) + Math.Pow(y - height / 2.0, 2));
                bool disc = distance <= radius;
                row[x * 4] = disc ? (byte)150 : (byte)225;
                row[x * 4 + 1] = disc ? (byte)30 : (byte)228;
                row[x * 4 + 2] = disc ? (byte)40 : (byte)232;
                row[x * 4 + 3] = 255;
            }
        }
        return picture;
    }
}
