using Compositor_korean_win.Core;
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
        using var canvas = new CanvasView(device);

        window.AttachCanvas(canvas);
        canvas.Open(Open(imagePath, format));
        window.Render();

        while (Win32.GetMessageW(out Win32.MSG message, 0, 0, 0) > 0)
        {
            Win32.TranslateMessage(message);
            Win32.DispatchMessageW(message);
        }

        return 0;
    }

    /// <summary>
    /// The document to open: the image given, or an empty canvas to look at.
    /// </summary>
    /// <remarks>
    /// Opening a <c>.comp</c> project belongs to the shell's file handling, which is M6. What M3
    /// needs is something on the canvas to move around.
    /// </remarks>
    private static CanvasDocument Open(string? imagePath, Format format)
    {
        if (imagePath is null || !File.Exists(imagePath))
        {
            return new CanvasDocument { Id = Guid.NewGuid(), Width = 1600, Height = 1000 };
        }

        using var loader = new ImageLoader();
        PixelBuffer pixels = loader.Load(imagePath, FormatProbe.WicFormatFor(format));

        var layer = new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = Path.GetFileNameWithoutExtension(imagePath),
            Image = pixels,
            Transform = new LayerTransform(Point.Zero, new Size(pixels.Width, pixels.Height)),
        };

        return new CanvasDocument
        {
            Id = Guid.NewGuid(),
            Width = pixels.Width,
            Height = pixels.Height,
            Layers = new EquatableList<ImageLayer>([layer]),
        };
    }

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
