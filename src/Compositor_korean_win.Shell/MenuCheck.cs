using Compositor_korean_win.Core;
using Vortice.DXGI;
using static Compositor_korean_win.Shell.Win32;

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
        string KoreanUndo)
    {
        public bool Passed => Untranslated.Count == 0 && NeverRan.Count == 0 && Errors.Count == 0;
    }

    public static Result Run(GraphicsDevice device, Format format, PixelBuffer image)
    {
        Language before = Localizer.Current;
        var untranslated = new List<string>();
        var errors = new List<string>();
        var ran = new HashSet<int>();
        int items = 0;
        string koreanUndo = string.Empty;

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

                    // Adding an adjustment layer chooses it, as it should, and an adjustment layer
                    // has no pixels to filter; a user would click back on the picture, and so does this.
                    if (!command.CanRun) canvas.ChooseTopImageLayer();
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

        return new Result(items, untranslated, ran.Count, neverRan, errors, koreanUndo);
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
