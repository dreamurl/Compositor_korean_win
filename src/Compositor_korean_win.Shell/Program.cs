using Vortice.DXGI;

namespace Compositor_korean_win.Shell;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string? image = ValueOf(args, "--image");

            if (args.Contains("--selftest"))
                return SelfTest.Run(image, ValueOf(args, "--report"));

            return RunWindow(image);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int RunWindow(string? imagePath)
    {
        using GraphicsDevice device = GraphicsDevice.Create();

        Format format = FormatProbe.Choose(device);
        using var window = new MainWindow(device, format, 1280, 800, visible: true);

        if (imagePath is not null)
        {
            using var loader = new ImageLoader();
            var buffer = loader.Load(imagePath, FormatProbe.WicFormatFor(format));
            window.SetImage(buffer);
            buffer.Release();
        }

        window.Render();

        while (Win32.GetMessageW(out Win32.MSG message, 0, 0, 0) > 0)
        {
            Win32.TranslateMessage(message);
            Win32.DispatchMessageW(message);
        }

        return 0;
    }

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
