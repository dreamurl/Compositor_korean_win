using Compositor_korean_win.Core;
using Vortice.DXGI;
using static Compositor_korean_win.Shell.Win32;
using Point = Compositor_korean_win.Core.Point;
using Size = Compositor_korean_win.Core.Size;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The menu bar in both languages, read back from Windows, and every command run once.
/// </summary>
/// <remarks>
/// <para>
/// M6.2 closes on this. The unit tests prove the table is complete; this proves the window uses
/// it — that what Windows actually holds in the menu is Korean when the language is Korean and
/// English when it is English, with no key names showing where a row was missing, and that the
/// language can be switched while the window is up.
/// </para>
/// <para>
/// Then every command that does not open a window of its own is run, twice over so that Undo and
/// Redo have something to do on the second pass, with a frame drawn after each and any preview it
/// opens committed. A command that throws, or that never became runnable, fails the run.
/// </para>
/// </remarks>
internal static class MenuCheck
{
    internal sealed record Result(
        int Items,
        IReadOnlyList<string> Untranslated,
        int CommandsRun,
        IReadOnlyList<string> NeverRan,
        IReadOnlyList<string> Errors,
        string KoreanUndo,
        string Jpeg,
        string Clipboard)
    {
        public bool Passed => Untranslated.Count == 0 && NeverRan.Count == 0 && Errors.Count == 0
                              && Jpeg == "ok" && Clipboard is "ok" or "unavailable";
    }

    public static Result Run(GraphicsDevice device, Format format, PixelBuffer image)
    {
        Language before = Localizer.Current;
        var untranslated = new List<string>();
        var errors = new List<string>();
        var ran = new HashSet<int>();
        int items = 0;
        string koreanUndo = string.Empty;
        string jpeg = "not run", clipboard = "not run";

        using var window = new MainWindow(device, format, 1280, 800, visible: false);
        using var canvas = new CanvasView(device);
        window.AttachCanvas(canvas);
        canvas.Open(DocumentFiles.FromImage(
            PixelRegion.Copy(image, new PixelRect(0, 0, image.Width, image.Height)), "check"));

        var files = new DocumentFiles(window.Handle, canvas, format);
        (List<Command> commands, List<MenuEntry.Submenu> layout) = AppCommands.Create(canvas, files, window.Handle);
        using var menu = new MenuBar(window.Handle, commands, layout);
        window.Menu = menu;

        try
        {
            foreach (Language language in Enum.GetValues<Language>())
            {
                // Switched while the window is up, as the Preferences menu does it.
                Localizer.Current = language;
                foreach (string label in menu.Labels())
                {
                    items++;
                    string text = label.Split('\t')[0];
                    if (!ReadsAs(text, language)) untranslated.Add($"{Localizer.Code(language)}: {text}");
                }
            }

            Localizer.Current = Language.Korean;

            for (int pass = 0; pass < 2; pass++)
            {
                foreach (Command command in commands.OrderBy(command => command.Id))
                {
                    if (command.Interactive) continue;

                    // A command needs the right layer chosen — a filter needs pixels, Merge Down a
                    // layer below, Move Out of Group a layer in a group — so, as a user would, this
                    // clicks through the layers until one will do.
                    // Some want a selection too; failing everything else, select all and look again.
                    if (!Ready(command, canvas))
                    {
                        canvas.SelectAll();
                        Ready(command, canvas);
                    }
                    if (!command.CanRun) continue;

                    try
                    {
                        command.Run();
                        ran.Add(command.Id);
                        window.Render();

                        if (canvas.IsFiltering)
                        {
                            canvas.Key(VK_RETURN, control: false);
                            window.Render();
                        }
                    }
                    catch (Exception exception)
                    {
                        errors.Add($"{Localizer.Text(command.Label, Language.English)}: {exception.Message}");
                    }
                }
            }

            // Once through the window procedure itself, the way a click on the menu arrives.
            SendMessageW(window.Handle, WM_COMMAND, CommandIds.FitOnScreen, 0);

            koreanUndo = canvas.UndoName;
            if (!ReadsAs(koreanUndo, Language.Korean)) untranslated.Add($"ko: undo \"{koreanUndo}\"");

            jpeg = JpegRoundTrip(canvas, format);
            clipboard = ClipboardRoundTrip(window.Handle, image);
        }
        finally
        {
            Localizer.Current = before;
        }

        List<string> neverRan =
        [
            .. commands.Where(command => !command.Interactive && !ran.Contains(command.Id))
                       .Select(command => Localizer.Text(command.Label, Language.English)),
        ];

        return new Result(items, untranslated, ran.Count, neverRan, errors, koreanUndo, jpeg, clipboard);
    }

    /// <summary>
    /// The document exported as a JPEG and read back: the export commands open a file dialog, so the
    /// writer is exercised here directly.
    /// </summary>
    private static string JpegRoundTrip(CanvasView canvas, Format format)
    {
        if (canvas.Document is not CanvasDocument document) return "no document";

        string path = Path.Combine(Path.GetTempPath(), $"compositor-check-{Guid.NewGuid():N}.jpg");
        try
        {
            using var backend = new SoftwareRenderBackend();
            using PixelBuffer pixels = LayerCompositor.Render(document, backend);
            ImageWriter.WriteJpeg(pixels, path);

            using var loader = new ImageLoader();
            using PixelBuffer back = loader.Load(path, FormatProbe.WicFormatFor(format));
            return back.Width == pixels.Width && back.Height == pixels.Height
                ? "ok"
                : $"read back {back.Width}x{back.Height} for {pixels.Width}x{pixels.Height}";
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Pixels onto the Windows clipboard and back. A runner's session may not have a clipboard to
    /// give, which is reported rather than failed; what comes back, if anything, must match.
    /// </summary>
    private static string ClipboardRoundTrip(nint owner, PixelBuffer image)
    {
        var clipboard = new Clipboard(owner);
        var placement = new LayerTransform(new Point(3, 4), new Size(image.Width, image.Height));

        clipboard.Put(image, placement);
        if (clipboard.Take() is not var (pixels, where)) return "unavailable";

        using (pixels)
        {
            if (pixels.Width != image.Width || pixels.Height != image.Height) return $"came back {pixels.Width}x{pixels.Height}";
            if (where != placement) return "the copy's placement was lost";
            return "ok";
        }
    }

    /// <summary>Clicks through the layers until one makes the command runnable.</summary>
    private static bool Ready(Command command, CanvasView canvas)
    {
        if (command.CanRun) return true;
        if (canvas.Document is not CanvasDocument document) return false;

        foreach (ImageLayer layer in document.Layers.Reverse())
        {
            canvas.Choose(layer.Id);
            if (command.CanRun) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a label is in <paramref name="language"/>: Korean has Korean in it, English has none,
    /// and neither is a key's own name — what the table returns for a row it does not have.
    /// </summary>
    private static bool ReadsAs(string text, Language language)
    {
        string plain = text.Replace("&", string.Empty).Trim();
        if (plain.Length == 0) return false;

        // The language picker names each language in itself, in either language.
        if (Enum.GetValues<Language>().Any(each => plain == Localizer.NativeName(each))) return true;
        if (Enum.GetNames<TextKey>().Contains(plain)) return false;

        bool korean = plain.Any(c => c is >= (char)0xAC00 and <= (char)0xD7A3);
        return language == Language.Korean ? korean : !korean;
    }
}
