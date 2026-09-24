using System.Globalization;
using System.Text;
using Compositor_korean_win.Core;
using Vortice.DXGI;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The M0 measurement run: everything the milestone has to prove, in one pass, reported as JSON.
/// </summary>
/// <remarks>
/// docs/windows-port.md closes M0 on figures, not on a screenshot — bundle size, start-up time and
/// memory, plus a decision on the shell and on channel order. The window stays hidden so this runs
/// on a machine with no desktop session, but nothing else is stubbed: it goes through a real Win32
/// window, a DXGI swap chain, a Direct2D device context, a WIC decode and a call into the C
/// kernels, and reports which driver it got, since the numbers mean different things on a GPU and
/// on WARP.
/// </remarks>
internal static class SelfTest
{
    /// <summary>
    /// How far Direct2D may sit from the reference where nothing is resampled.
    /// </summary>
    /// <remarks>
    /// Not zero: Direct2D composites in floating point and rounds once, while the reference rounds
    /// at every step, so a few pixels land several levels apart. The average is the real guard —
    /// a blend mode wired to the wrong one, or a mask read from the wrong channel, moves the mean
    /// by whole numbers rather than by hundredths.
    /// </remarks>
    private const int ExactMaxTolerance = 8;

    private const double ExactMeanTolerance = 0.2;

    /// <summary>
    /// How far the two may sit apart away from an edge, once resampling is in play.
    /// </summary>
    /// <remarks>
    /// Near zero on purpose. Two filters disagree at edges and nowhere else, so anything here is
    /// the placement being wrong — an offset, a transposed matrix, a centre taken from the wrong
    /// corner — which a mean over the whole image would hide.
    /// </remarks>
    private const int FlatTolerance = 2;

    /// <summary>
    /// How far the two may sit apart once adjustment layers run over the exact scene.
    /// </summary>
    /// <remarks>
    /// Both backends run the same adjustment code over their own composite, so this is the exact
    /// pass's allowance stretched by what the adjustments do to a level of difference — a black
    /// point and a bend in a curve each widen it a little. A wrong read, a mask weighted from the
    /// wrong channel or a frame adjusted twice would move the mean by whole numbers.
    /// </remarks>
    private const int AdjustedMaxTolerance = 16;
    private const double AdjustedMeanTolerance = 0.5;

    public static int Run(string? imagePath, string? reportPath)
    {
        var report = new StringBuilder();
        var failures = new List<string>();

        using GraphicsDevice device = GraphicsDevice.Create();

        FormatProbe.Support rgba = FormatProbe.Probe(device, Format.R8G8B8A8_UNorm);
        FormatProbe.Support bgra = FormatProbe.Probe(device, Format.B8G8R8A8_UNorm);
        Format chosen = FormatProbe.Choose(device);

        // The kernels are the one piece of upstream carried over unchanged. If this call does not
        // land, nothing else in the port matters.
        int abi;
        try
        {
            abi = Kernels.AbiVersion();
            if (abi != 1) failures.Add($"kernel ABI {abi}, expected 1");
        }
        catch (Exception exception)
        {
            abi = -1;
            failures.Add("kernel P/Invoke failed: " + exception.Message);
        }

        PixelBuffer image;
        bool decoded = false;
        if (imagePath is not null && File.Exists(imagePath))
        {
            using var loader = new ImageLoader();
            image = loader.Load(imagePath, FormatProbe.WicFormatFor(chosen));
            decoded = true;
        }
        else
        {
            image = Synthesize(256, 256);
            failures.Add("no image given, WIC decode not exercised");
        }

        // Measuring the histogram proves the managed buffer and the native kernel agree on layout:
        // a wrong stride or a wrong channel count shows up here as a bin total that does not match.
        // The kernel weights each pixel by its alpha, so the total to expect is the summed alpha,
        // not the pixel count — a half-transparent pixel is half a pixel's worth of histogram.
        Histogram histogram = Histogram.Measure(image);
        double binTotal = 0;
        foreach (double bin in histogram.Channel(0)) binTotal += bin;
        double expected = TotalAlpha(image);
        if (Math.Abs(binTotal - expected) > Math.Max(0.5, expected * 1e-6))
            failures.Add($"histogram total {binTotal:F1} does not match summed alpha {expected:F1}");

        TimeSpan? firstFrame = null;
        bool windowed = false;
        try
        {
            using var window = new MainWindow(device, chosen, 1280, 800, visible: false);
            window.SetImage(image);
            window.Render();
            MainWindow.PumpMessages();
            firstFrame = window.TimeToFirstFrame;
            windowed = true;
        }
        catch (Exception exception)
        {
            failures.Add("window/present failed: " + exception.Message);
        }

        // M6.2 closes on the menu: both languages read back from Windows, every command run.
        MenuCheck.Result? menus = null;
        try
        {
            menus = MenuCheck.Run(device, chosen, image);
            foreach (string label in menus.Untranslated) failures.Add("menu not translated: " + label);
            foreach (string command in menus.NeverRan) failures.Add("command never became runnable: " + command);
            foreach (string error in menus.Errors) failures.Add("command failed: " + error);
            if (menus.Jpeg != "ok") failures.Add("JPEG export: " + menus.Jpeg);
            if (menus.Clipboard is not ("ok" or "unavailable")) failures.Add("clipboard: " + menus.Clipboard);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            failures.Add("menu check failed: " + exception.Message);
        }

        // M6.4 closes on the window itself: every word the panels draw, in both languages.
        UiCheck.Result? panels = null;
        try
        {
            panels = UiCheck.Run(device, chosen, image, reportPath is null ? null : Path.GetDirectoryName(reportPath));
            foreach (string text in panels.Untranslated) failures.Add("panel text not translated: " + text);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            failures.Add("panel check failed: " + exception.Message);
        }

        // M6.6 closes on files: saved and opened again the same, documents apart in their tabs, drops landing.
        FilesCheck.Result? filesCheck = null;
        try
        {
            filesCheck = FilesCheck.Run(device, chosen, image);
            foreach (string error in filesCheck.Errors) failures.Add("files: " + error);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            failures.Add("files check failed: " + exception.Message);
        }

        // What M6 picked up from M4: strokes on a mask, Smudge and Liquify, through the canvas.
        ToolsCheck.Result? toolsCheck = null;
        try
        {
            toolsCheck = ToolsCheck.Run(device, chosen, image);
            foreach (string error in toolsCheck.Errors) failures.Add("tools: " + error);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            failures.Add("tools check failed: " + exception.Message);
        }

        // M7: the model in the AI build finds an obvious subject; the plain build reports it absent.
        AiCheck.Result? aiCheck = null;
        try
        {
            aiCheck = AiCheck.Run(device, chosen);
            if (!aiCheck.Passed)
                failures.Add($"ai: {aiCheck.Error ?? "subject not found"} (inside {aiCheck.Inside:F2}, outside {aiCheck.Outside:F2})");
            if (aiCheck.Canvas is string wrong) failures.Add("ai canvas: " + wrong);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            failures.Add("ai check failed: " + exception.Message);
        }

        // M2 closes on a pixel comparison: the same document through Direct2D and through the
        // reference rasteriser. Exact means 1:1 with Nearest, where nothing is resampled and the
        // two should agree on arithmetic alone.
        RenderCheck.Result? render = null;
        try
        {
            render = RenderCheck.Run(device, reportPath is null ? null : Path.GetDirectoryName(reportPath));
            if (render.Exact.Max > ExactMaxTolerance || render.Exact.Mean > ExactMeanTolerance)
                failures.Add($"Direct2D differs from the reference by {render.Exact} at 1:1");

            // Resampled, the two must still agree away from edges. That is the geometry, and it is
            // not allowed to drift; the filters differ at edges by design and are only reported.
            if (render.Placement.FlatMax > FlatTolerance)
                failures.Add($"Direct2D is placed differently from the reference: {render.Placement}");

            if (render.Adjusted.Max > AdjustedMaxTolerance || render.Adjusted.Mean > AdjustedMeanTolerance)
                failures.Add($"adjustment layers differ between the backends by {render.Adjusted}");

            if (render.Previewed.Max > ExactMaxTolerance || render.Previewed.Mean > ExactMeanTolerance)
                failures.Add($"a live preview draws differently on the two backends: {render.Previewed}");
        }
        catch (Exception exception)
        {
            failures.Add("render check failed: " + exception.Message);
        }

        // Read before the canvas bench, which allocates a hundred megapixels on purpose. The
        // counter is a high-water mark for the whole process, so M0's figure has to be taken while
        // it still means what M0 measured.
        long peakWorkingSet = Win32.PeakWorkingSet();

        // M3 closes on what a frame of a hundred-megapixel document costs — recorded, not judged,
        // because this is WARP (docs/windows-port.md §10.4) — and on the invariant underneath it.
        CanvasBench.Result? canvas = null;
        try
        {
            canvas = CanvasBench.Run(device, side: 10_000, width: 1280, height: 800);
            if (!canvas.WithinBudget)
            {
                failures.Add($"a frame reads {canvas.PixelsRead} pixels for a window of "
                             + $"{canvas.ViewPixels}: the cost is following the document");
            }
        }
        catch (Exception exception)
        {
            failures.Add("canvas bench failed: " + exception.Message);
        }

        // M4 closes on stroke latency, and on the comparison underneath it: a stroke costs the
        // tiles it touched, so a hundred-megapixel layer paints like a megapixel one.
        StrokeBench.Result? stroke = null;
        try
        {
            stroke = StrokeBench.Run(device, side: 10_000, width: 1280, height: 800);
            if (!stroke.WithinBudget)
            {
                failures.Add($"a stroke takes {stroke.LargeAppendMs:F2} ms on a large layer against "
                             + $"{stroke.SmallAppendMs:F2} on a small one: the cost is following the layer");
            }
        }
        catch (Exception exception)
        {
            failures.Add("stroke bench failed: " + exception.Message);
        }

        // M5 closes on the live preview: an adjustment layer touches the window's pixels once a
        // frame, and a filter tried on a layer filters what is in view, not the layer.
        AdjustBench.Result? adjust = null;
        try
        {
            adjust = AdjustBench.Run(device, side: 10_000, width: 1280, height: 800);
            if (!adjust.WithinBudget)
            {
                failures.Add($"a preview frame adjusts {adjust.PixelsAdjustedPerFrame} pixels for "
                             + $"{adjust.Adjustments} adjustments and filters {adjust.PreviewFittedPixels} / "
                             + $"{adjust.PreviewFullSizePixels} for a window of {adjust.ViewPixels}: "
                             + "the cost is following the document");
            }
        }
        catch (Exception exception)
        {
            failures.Add("adjust bench failed: " + exception.Message);
        }

        long exeBytes = 0;
        string? exePath = Environment.ProcessPath;
        if (exePath is not null && File.Exists(exePath)) exeBytes = new FileInfo(exePath).Length;

        image.Release();
        // Layer effects keep what they drew in a process-wide cache so a redraw does not rebuild
        // them; those buffers are held on purpose, not leaked, so they go before the count.
        EffectRendering.ClearCache();
        if (PixelBuffer.LiveCount != 0)
            failures.Add($"{PixelBuffer.LiveCount} pixel buffers leaked");

        report.Append("{\n");
        Line(report, "milestone", "M6");
        Line(report, "shell", "win32-direct2d");
        Line(report, "driver", device.IsWarp ? "warp" : "hardware");
        Line(report, "adapter", device.Adapter);
        Line(report, "featureLevel", device.FeatureLevel.ToString());
        Line(report, "chosenFormat", chosen.ToString());
        Line(report, "wicDecoded", decoded);
        Line(report, "windowPresented", windowed);
        Line(report, "kernelAbi", abi);
        Line(report, "imageWidth", image.Width);
        Line(report, "imageHeight", image.Height);
        Line(report, "exeBytes", exeBytes);
        Line(report, "timeToFirstFrameMs", firstFrame?.TotalMilliseconds ?? -1);
        Line(report, "peakWorkingSetBytes", peakWorkingSet);
        Line(report, "managedHeapBytes", GC.GetTotalMemory(forceFullCollection: false));
        Line(report, "pixelBufferLiveBytes", PixelBuffer.LiveBytes);
        Line(report, "pixelBufferLiveCount", PixelBuffer.LiveCount);
        if (render is not null)
        {
            Line(report, "renderBackend", render.BackendName);
            Line(report, "renderExactMax", render.Exact.Max);
            Line(report, "renderExactMean", render.Exact.Mean);
            Line(report, "renderPlacementMax", render.Placement.Max);
            Line(report, "renderPlacementMean", render.Placement.Mean);
            Line(report, "renderPlacementFlatMax", render.Placement.FlatMax);
            Line(report, "renderPlacementEdgeMean", render.Placement.EdgeMean);
            Line(report, "renderResampledMax", render.Resampled.Max);
            Line(report, "renderResampledMean", render.Resampled.Mean);
            Line(report, "renderAdjustedMax", render.Adjusted.Max);
            Line(report, "renderAdjustedMean", render.Adjusted.Mean);
            Line(report, "renderPreviewedMax", render.Previewed.Max);
            Line(report, "renderPreviewedMean", render.Previewed.Mean);
        }
        if (canvas is not null)
        {
            Line(report, "canvasDocumentPixels", canvas.DocumentPixels);
            Line(report, "canvasViewPixels", canvas.ViewPixels);
            Line(report, "canvasPixelsRead", canvas.PixelsRead);
            Line(report, "canvasWithinBudget", canvas.WithinBudget);
            Line(report, "canvasFittedFrameMs", canvas.FittedMs);
            Line(report, "canvasFullSizeFrameMs", canvas.FullSizeMs);
            Line(report, "canvasPeakWorkingSetBytes", canvas.PeakWorkingSetBytes);
        }
        if (stroke is not null)
        {
            Line(report, "strokeSmallAppendMs", stroke.SmallAppendMs);
            Line(report, "strokeLargeAppendMs", stroke.LargeAppendMs);
            Line(report, "strokeLiveFrameMs", stroke.LiveFrameMs);
            Line(report, "strokeTilesTouched", stroke.TilesTouched);
            Line(report, "strokeLargeLayerPixels", stroke.LargeLayerPixels);
            Line(report, "strokeWithinBudget", stroke.WithinBudget);
        }
        if (menus is not null)
        {
            Line(report, "menuItems", menus.Items);
            Line(report, "menuUntranslated", menus.Untranslated.Count);
            Line(report, "menuCommandsRun", menus.CommandsRun);
            Line(report, "menuKoreanUndo", menus.KoreanUndo);
            Line(report, "menuJpeg", menus.Jpeg);
            Line(report, "menuClipboard", menus.Clipboard);
            Line(report, "menuPassed", menus.Passed);
        }

        if (panels is not null)
        {
            Line(report, "panelStrings", panels.Strings);
            Line(report, "panelUntranslated", panels.Untranslated.Count);
            Line(report, "panelScreenshots", string.Join(" ", panels.Screenshots));
            Line(report, "panelPassed", panels.Passed);
        }

        if (filesCheck is not null)
        {
            Line(report, "filesLayers", filesCheck.Layers);
            Line(report, "filesMaximumDifference", filesCheck.MaximumDifference);
            Line(report, "filesTabs", filesCheck.Tabs);
            Line(report, "filesPassed", filesCheck.Passed);
        }

        if (aiCheck is not null)
        {
            Line(report, "aiInstalled", aiCheck.Installed);
            Line(report, "aiDevice", aiCheck.Device);
            Line(report, "aiLoadMs", aiCheck.LoadMs);
            Line(report, "aiRunMs", aiCheck.RunMs);
            Line(report, "aiInside", aiCheck.Inside);
            Line(report, "aiOutside", aiCheck.Outside);
            Line(report, "aiPassed", aiCheck.Passed && aiCheck.Canvas is null);
            // The runtime's own words, so kept out of the JSON's way.
            Line(report, "aiGpuError", (aiCheck.GpuError ?? "").Replace('\\', '/').Replace('"', '\'').ReplaceLineEndings(" "));
        }

        if (toolsCheck is not null)
        {
            Line(report, "toolsChecks", toolsCheck.Checks);
            Line(report, "toolsPassed", toolsCheck.Passed);
        }

        if (adjust is not null)
        {
            Line(report, "adjustDocumentPixels", adjust.DocumentPixels);
            Line(report, "adjustViewPixels", adjust.ViewPixels);
            Line(report, "adjustLayers", adjust.Adjustments);
            Line(report, "adjustPixelsPerFrame", adjust.PixelsAdjustedPerFrame);
            Line(report, "adjustPlainFrameMs", adjust.PlainFrameMs);
            Line(report, "adjustFrameMs", adjust.AdjustedFrameMs);
            Line(report, "adjustChangeFrameMs", adjust.SettingsChangeFrameMs);
            Line(report, "previewOpenMs", adjust.PreviewOpenMs);
            Line(report, "previewFittedMs", adjust.PreviewFittedMs);
            Line(report, "previewFittedPixels", adjust.PreviewFittedPixels);
            Line(report, "previewFullSizeMs", adjust.PreviewFullSizeMs);
            Line(report, "previewFullSizePixels", adjust.PreviewFullSizePixels);
            Line(report, "adjustWithinBudget", adjust.WithinBudget);
            foreach ((AdjustmentKind kind, double ms) in adjust.PerKindMs)
                Line(report, "adjustMs" + kind, ms);
        }

        Support(report, "r8g8b8a8", rgba);
        Support(report, "b8g8r8a8", bgra);
        Failures(report, failures);
        report.Append("}\n");

        string json = report.ToString();
        Console.Out.Write(json);
        if (reportPath is not null) File.WriteAllText(reportPath, json);

        foreach (string failure in failures) Console.Error.WriteLine("FAIL: " + failure);
        return failures.Count == 0 ? 0 : 1;
    }

    private static void Failures(StringBuilder report, List<string> failures)
    {
        report.Append("  \"failures\": [");
        for (int i = 0; i < failures.Count; i++)
        {
            if (i > 0) report.Append(", ");
            string escaped = failures[i].Replace("\\", "\\\\").Replace("\"", "\\\"");
            report.Append('"').Append(escaped).Append('"');
        }
        report.Append("]\n");
    }

    private static void Support(StringBuilder report, string name, FormatProbe.Support support)
    {
        report.Append("  \"").Append(name).Append("\": { \"texture2d\": ").Append(Bool(support.Texture2D))
              .Append(", \"renderTarget\": ").Append(Bool(support.RenderTarget))
              .Append(", \"display\": ").Append(Bool(support.Display))
              .Append(", \"swapChain\": ").Append(Bool(support.SwapChain))
              .Append(" },\n");
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static void Line(StringBuilder report, string name, string value) =>
        report.Append("  \"").Append(name).Append("\": \"").Append(value).Append("\",\n");

    private static void Line(StringBuilder report, string name, bool value) =>
        report.Append("  \"").Append(name).Append("\": ").Append(Bool(value)).Append(",\n");

    private static void Line(StringBuilder report, string name, long value) =>
        report.Append("  \"").Append(name).Append("\": ")
              .Append(value.ToString(CultureInfo.InvariantCulture)).Append(",\n");

    private static void Line(StringBuilder report, string name, double value) =>
        report.Append("  \"").Append(name).Append("\": ")
              .Append(value.ToString("F3", CultureInfo.InvariantCulture)).Append(",\n");

    /// <summary>A gradient with a transparent corner, so alpha handling is exercised too.</summary>
    private static PixelBuffer Synthesize(int width, int height)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < width; x++)
            {
                byte alpha = (byte)(x < width / 4 && y < height / 4 ? 0 : 255);
                byte value = (byte)(x * 255 / Math.Max(1, width - 1));
                byte premultiplied = (byte)(value * alpha / 255);
                row[x * 4 + 0] = premultiplied;
                row[x * 4 + 1] = premultiplied;
                row[x * 4 + 2] = premultiplied;
                row[x * 4 + 3] = alpha;
            }
        }
        return buffer;
    }

    /// <summary>Summed alpha as a fraction of full opacity — what the kernel's bins add up to.</summary>
    private static double TotalAlpha(PixelBuffer buffer)
    {
        long sum = 0;
        for (int y = 0; y < buffer.Height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < buffer.Width; x++) sum += row[x * 4 + 3];
        }
        return sum / 255.0;
    }
}
