using Compositor_korean_win.Core;
using Vortice.DXGI;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The whole window drawn in each language — every tool's options, the Layers panel with folders,
/// adjustment layers and masks in it, the status bar and the empty-window welcome — with every word
/// it drew checked, and a screenshot of each language kept.
/// </summary>
/// <remarks>
/// <para>
/// This is the check M6's goal comes down to: with the language set to Korean, nothing the window
/// shows is English. The panels write down each piece of text as they draw it (<see cref="Ui"/>),
/// tooltips included, so the check reads exactly what a user would see rather than what the text
/// table holds. Text with no letters in it — a number, a percentage — reads the same in both.
/// </para>
/// <para>
/// The screenshots go next to the report, where CI keeps them, so what the window looks like in each
/// language can be seen without a Windows machine.
/// </para>
/// </remarks>
internal static class UiCheck
{
    internal sealed record Result(int Strings, IReadOnlyList<string> Untranslated, IReadOnlyList<string> Screenshots)
    {
        public bool Passed => Untranslated.Count == 0;
    }

    public static Result Run(GraphicsDevice device, Format format, PixelBuffer image, string? folder)
    {
        Language before = Localizer.Current;
        var untranslated = new SortedSet<string>(StringComparer.Ordinal);
        var screenshots = new List<string>();
        int strings = 0;

        using var window = new MainWindow(device, format, 1280, 800, visible: false);
        using var canvas = new CanvasView(device);
        window.AttachCanvas(canvas);
        // Two documents, so the tabs show one on the canvas and one waiting.
        canvas.Open(DocumentFiles.FromImage(PixelRegion.Copy(image, new PixelRect(0, 0, image.Width, image.Height)), "photo"),
                    path: null, "photo");
        canvas.Open(DocumentFiles.FromImage(PixelRegion.Copy(image, new PixelRect(0, 0, image.Width, image.Height)), "second"),
                    path: null, "second");
        canvas.SwitchTo(0);

        var files = new DocumentFiles(window.Handle, canvas, format);
        (List<Command> commands, List<MenuEntry.Submenu> layout) = AppCommands.Create(canvas, files, window.Handle);
        using var menu = new MenuBar(window.Handle, commands, layout);
        window.Menu = menu;
        using var chrome = new Chrome(window.Handle, canvas, () => { }, menu.Run);
        window.AttachChrome(chrome);
        files.Chrome = chrome;
        menu.Blocked = () => chrome.HasSheet;

        try
        {
            // A document with something in every kind of row: a folder, a layer in it, a mask,
            // and an adjustment layer.
            Localizer.Current = Language.English;
            menu.Run(CommandIds.AddLayerMask);
            menu.Run(CommandIds.NewLayer);
            menu.Run(CommandIds.GroupLayers);
            canvas.ChooseTopImageLayer();
            canvas.StartFilter(FilterCommand.Levels, asLayer: true);
            canvas.Key(Win32.VK_RETURN, control: false);
            canvas.ChooseTopImageLayer();

            foreach (Language language in Enum.GetValues<Language>())
            {
                Localizer.Current = language;

                foreach (CanvasTool tool in Enum.GetValues<CanvasTool>())
                {
                    canvas.SetTool(tool);
                    window.Render(present: false);
                    strings += Check(chrome.Ui, language, untranslated);

                    if (tool == CanvasTool.Brush && folder is not null)
                        screenshots.Add(Screenshot(device, folder, $"ui-{Localizer.Code(language)}.png"));

                    device.Present();
                }

                // The blur tool's three modes and the clone stamp's sampling, which change the bar.
                canvas.SetTool(CanvasTool.Blur);
                foreach (BlurToolMode mode in Enum.GetValues<BlurToolMode>())
                {
                    canvas.BlurMode = mode;
                    window.Render(present: false);
                    strings += Check(chrome.Ui, language, untranslated);
                    device.Present();
                }
                canvas.BlurMode = BlurToolMode.Blur;

                // With the mask as the target: the bar says so, the swatches go black and white,
                // and the tools that cannot work on a mask say that.
                Guid masked = canvas.Document!.Layers.First(layer => layer.Mask is not null).Id;
                canvas.ClickMask(masked);
                if (!canvas.EditingMask) untranslated.Add($"{Localizer.Code(language)}: the mask did not become the target");
                foreach (CanvasTool tool in new[] { CanvasTool.Brush, CanvasTool.CloneStamp, CanvasTool.Gradient })
                {
                    canvas.SetTool(tool);
                    window.Render(present: false);
                    strings += Check(chrome.Ui, language, untranslated);
                    if (tool == CanvasTool.Brush && folder is not null)
                        screenshots.Add(Screenshot(device, folder, $"ui-{Localizer.Code(language)}-mask.png"));
                    device.Present();
                }
                canvas.ClickLayer(masked, control: false, shift: false, [masked]);
                canvas.ChooseTopImageLayer();
            }

            // Every adjustment's and filter's sheet, over the photo's pixels, and an adjustment layer's
            // own sheet, whose histogram is of the layers under it.
            foreach (Language language in Enum.GetValues<Language>())
            {
                Localizer.Current = language;

                foreach (FilterCommand command in Enum.GetValues<FilterCommand>())
                {
                    canvas.ChooseTopImageLayer();
                    canvas.StartFilter(command, asLayer: false);

                    // A colour range rather than Master, so the spectrum bars and eyedroppers show too.
                    if (command == FilterCommand.HueSaturation)
                        canvas.FilterAdjustment = canvas.FilterAdjustment with { HsvSettings = new HueSaturationSettings { Range = ColorRange.Reds } };
                    strings += Sheet(window, chrome, language, command.ToString(), untranslated);
                    if (folder is not null && (language == Language.Korean || command == FilterCommand.Levels))
                        screenshots.Add(Screenshot(device, folder, $"ui-{Localizer.Code(language)}-{command.ToString().ToLowerInvariant()}.png"));
                    canvas.Key(Win32.VK_ESCAPE, control: false);
                    device.Present();
                }

                // The document's own sheets, opened as their menu items open them.
                foreach ((int id, string name) in new[]
                {
                    (CommandIds.New, "new"), (CommandIds.CanvasSize, "canvassize"),
                    (CommandIds.ImageSize, "imagesize"), (CommandIds.ExportJpeg, "jpeg"),
                })
                {
                    canvas.ChooseTopImageLayer();
                    if (!menu.Run(id)) untranslated.Add($"{Localizer.Code(language)}: {name} did not run");
                    strings += Sheet(window, chrome, language, name, untranslated);
                    if (folder is not null) screenshots.Add(Screenshot(device, folder, $"ui-{Localizer.Code(language)}-{name}.png"));
                    chrome.SheetKey(Win32.VK_ESCAPE, control: false, shift: false);
                    device.Present();
                }

                chrome.PickColour(TextKey.TooltipForeground, new Rgba(200, 80, 40), _ => { });
                strings += Sheet(window, chrome, language, "colour picker", untranslated);
                if (folder is not null) screenshots.Add(Screenshot(device, folder, $"ui-{Localizer.Code(language)}-colour.png"));
                chrome.SheetKey(Win32.VK_ESCAPE, control: false, shift: false);
                device.Present();

                if (canvas.Document?.Layers.FirstOrDefault(layer => layer.Adjustment is not null) is ImageLayer adjustment)
                {
                    canvas.EditAdjustmentLayer(adjustment.Id);
                    strings += Sheet(window, chrome, language, "adjustment layer", untranslated);
                    canvas.Key(Win32.VK_ESCAPE, control: false);
                    device.Present();
                }
            }

            // The empty window, with its welcome.
            while (canvas.HasDocument) canvas.Close();
            foreach (Language language in Enum.GetValues<Language>())
            {
                Localizer.Current = language;
                window.Render(present: false);
                strings += Check(chrome.Ui, language, untranslated);
                if (folder is not null)
                    screenshots.Add(Screenshot(device, folder, $"ui-{Localizer.Code(language)}-empty.png"));
                device.Present();
            }
        }
        finally
        {
            Localizer.Current = before;
        }

        return new Result(strings, [.. untranslated], screenshots);
    }

    /// <summary>Draws a frame with a sheet open, and checks it is there and says everything in the language.</summary>
    private static int Sheet(MainWindow window, Chrome chrome, Language language, string what, ISet<string> untranslated)
    {
        if (!chrome.HasSheet)
        {
            untranslated.Add($"{Localizer.Code(language)}: no sheet opened for {what}");
            return 0;
        }
        window.Render(present: false);
        return Check(chrome.Ui, language, untranslated);
    }

    /// <summary>Checks everything the last frame drew and would show as a tooltip.</summary>
    private static int Check(Ui ui, Language language, ISet<string> untranslated)
    {
        int count = 0;
        foreach (string text in ui.Drawn.Concat(ui.Tooltips))
        {
            count++;
            if (!ReadsAs(text, language)) untranslated.Add($"{Localizer.Code(language)}: {text}");
        }
        return count;
    }

    /// <summary>
    /// Korean text has Korean in it; English has none; text with no letters at all reads the same in
    /// both; and nothing is a key's own name, which is what the table gives for a missing row.
    /// </summary>
    internal static bool ReadsAs(string text, Language language)
    {
        string plain = text.Replace("&", string.Empty).Trim();
        if (plain.Length == 0 || !plain.Any(char.IsLetter)) return true;
        if (Enum.GetValues<Language>().Any(each => plain == Localizer.NativeName(each))) return true;
        if (TextTable.Untranslated.Any(key => plain == TextTable.Lookup(key, language))) return true;
        if (Enum.GetNames<TextKey>().Contains(plain)) return false;

        bool korean = plain.Any(c => c is >= (char)0xAC00 and <= (char)0xD7A3);
        return language == Language.Korean ? korean : !korean;
    }

    private static string Screenshot(GraphicsDevice device, string folder, string name)
    {
        using PixelBuffer? frame = device.CaptureBackBuffer();
        if (frame is null) return name + " (no frame)";

        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Png.Encode(frame));
        return name;
    }
}
