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
        AppSettings.Apply();

        using GraphicsDevice device = GraphicsDevice.Create();

        Format format = FormatProbe.Choose(device);
        using var window = new MainWindow(device, format, 1280, 800, visible: true);
        using var canvas = new CanvasView(device);

        window.AttachCanvas(canvas);
        canvas.Open(Open(imagePath, format));

        var files = new DocumentFiles(window.Handle, canvas, format);
        (List<Command> commands, List<MenuEntry.Submenu> layout) = AppCommands.Create(canvas, files, window.Handle);
        using var menu = new MenuBar(window.Handle, commands, layout);
        window.Menu = menu;
        window.CanClose = files.ConfirmDiscardAll;
        window.FilesDropped = files.Drop;

        using var chrome = new Chrome(window.Handle, canvas, files.Open, menu.Run);
        window.AttachChrome(chrome);
        files.Chrome = chrome;
        chrome.CloseTab = files.CloseTab;
        menu.Blocked = () => chrome.HasSheet;
        window.OleFilesDropped = (paths, destination) => files.Drop(paths, destination);
        window.ImageDataDropped = (data, png, name, destination) => files.DropImage(data, png, destination, name);
        window.EnableOleDrops();

        window.Render();

        while (Win32.GetMessageW(out Win32.MSG message, 0, 0, 0) > 0)
        {
            if (window.PreTranslate(message))
            {
                window.Invalidate();
                continue;
            }

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
        return DocumentFiles.FromImage(pixels, Path.GetFileNameWithoutExtension(imagePath));
    }

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
