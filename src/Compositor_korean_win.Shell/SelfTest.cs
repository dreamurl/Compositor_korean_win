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

        long exeBytes = 0;
        string? exePath = Environment.ProcessPath;
        if (exePath is not null && File.Exists(exePath)) exeBytes = new FileInfo(exePath).Length;

        image.Release();
        if (PixelBuffer.LiveCount != 0)
            failures.Add($"{PixelBuffer.LiveCount} pixel buffers leaked");

        report.Append("{\n");
        Line(report, "milestone", "M0");
        Line(report, "shell", "win32-direct2d");
        Line(report, "driver", device.IsWarp ? "warp" : "hardware");
        Line(report, "featureLevel", device.FeatureLevel.ToString());
        Line(report, "chosenFormat", chosen.ToString());
        Line(report, "wicDecoded", decoded);
        Line(report, "windowPresented", windowed);
        Line(report, "kernelAbi", abi);
        Line(report, "imageWidth", image.Width);
        Line(report, "imageHeight", image.Height);
        Line(report, "exeBytes", exeBytes);
        Line(report, "timeToFirstFrameMs", firstFrame?.TotalMilliseconds ?? -1);
        Line(report, "peakWorkingSetBytes", Win32.PeakWorkingSet());
        Line(report, "managedHeapBytes", GC.GetTotalMemory(forceFullCollection: false));
        Line(report, "pixelBufferLiveBytes", PixelBuffer.LiveBytes);
        Line(report, "pixelBufferLiveCount", PixelBuffer.LiveCount);
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
