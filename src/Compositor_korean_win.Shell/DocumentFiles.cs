using Compositor_korean_win.Core;
using Vortice.DXGI;
using static Compositor_korean_win.Shell.Win32;
using Point = Compositor_korean_win.Core.Point;
using Size = Compositor_korean_win.Core.Size;

namespace Compositor_korean_win.Shell;

/// <summary>
/// Opening, saving and exporting — the File menu's half that talks to the disk.
/// </summary>
/// <remarks>
/// <para>
/// The dialogs are the system's own common dialogs, so they come in the language Windows is in
/// rather than the one chosen here; what this program puts in them — the file type names — follows
/// the chosen one. That is the same split every Windows program has, and the alternative, drawing a
/// file browser, would be a great deal of work to be worse than the system's.
/// </para>
/// <para>
/// Every failure is reported by what kind of failure it is (<see cref="ProjectErrorKind"/>) and
/// said in the chosen language. The exception's own message is for the log; it is English and
/// written for whoever reads the code.
/// </para>
/// </remarks>
internal sealed unsafe class DocumentFiles(nint owner, CanvasView canvas, Format format)
{
    private const string ImagePatterns = "*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.gif;*.heic";

    /// <summary>
    /// Asks whether to save a changed document before it goes. False when the user cancels, or asked
    /// to save and then did not.
    /// </summary>
    public bool ConfirmDiscard()
    {
        // Pixels still floating are part of the document the user sees; lay them down first.
        canvas.SettleFloating();
        if (!canvas.HasDocument || !canvas.IsModified) return true;

        int answer = MessageBoxW(owner, Localizer.Format(TextKey.PromptSaveChanges, canvas.Title),
                                 Localizer.Text(TextKey.AppTitle), MB_YESNOCANCEL | MB_ICONWARNING);
        if (answer == IDNO) return true;
        if (answer != IDYES) return false;

        Save();
        return !canvas.IsModified;
    }

    /// <summary>File › Close: the document goes, once any changes are dealt with.</summary>
    public void Close()
    {
        if (ConfirmDiscard()) canvas.Close();
    }

    /// <summary>A tab's close button: that tab is brought forward, so the question is asked about what can be seen.</summary>
    public void CloseTab(int index)
    {
        canvas.SwitchTo(index);
        if (canvas.ActiveTab == index) Close();
    }

    /// <summary>
    /// Before the program ends: every changed document in turn, each shown as it is asked about.
    /// False as soon as one is cancelled.
    /// </summary>
    public bool ConfirmDiscardAll()
    {
        canvas.SettleFloating();
        for (int index = 0; index < canvas.Tabs.Count; index++)
        {
            if (!canvas.Tabs[index].History.IsModified) continue;
            canvas.SwitchTo(index);
            if (canvas.ActiveTab != index || !ConfirmDiscard()) return false;
        }
        return true;
    }

    /// <summary>Asks for images and adds each as a layer in the middle of the canvas.</summary>
    public void ImportImages()
    {
        if (!canvas.HasDocument) return;

        string filter = Filter((TextKey.FileTypeImages, ImagePatterns));
        List<string> paths = PickMany(filter);
        if (paths.Count == 0) return;
        AddImages(paths);
    }

    /// <summary>Images from disk added to the open document as layers; the ones that will not read are reported.</summary>
    private void AddImages(IEnumerable<string> paths)
    {
        var images = new List<(PixelBuffer, string)>();
        using var loader = new ImageLoader();
        foreach (string path in paths)
        {
            try
            {
                images.Add((loader.Load(path, FormatProbe.WicFormatFor(format)), Path.GetFileNameWithoutExtension(path)));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                Report(TextKey.ErrorCannotOpen, path, exception);
            }
        }

        canvas.AddImages(images);
    }

    /// <summary>Asks for a project or an image and opens it.</summary>
    public void Open()
    {
        string filter = Filter(
            (TextKey.FileTypeProject, "*.comp"),
            (TextKey.FileTypeImages, ImagePatterns));

        if (Pick(save: false, filter, defaultExtension: null, suggested: null) is string path) OpenPath(path);
    }

    private static bool IsProject(string path) =>
        string.Equals(Path.GetExtension(path), ".comp", StringComparison.OrdinalIgnoreCase);

    /// <summary>A project or an image opened in a tab of its own. False, and reported, when it will not read.</summary>
    public bool OpenPath(string path)
    {
        try
        {
            if (IsProject(path))
            {
                ProjectSnapshot snapshot = ProjectStore.Load(path);
                canvas.Open(ProjectMapping.ToDocument(snapshot), path);
            }
            else
            {
                using var loader = new ImageLoader();
                PixelBuffer pixels = loader.Load(path, FormatProbe.WicFormatFor(format));
                string name = Path.GetFileNameWithoutExtension(path);
                canvas.Open(FromImage(pixels, name), path: null, name);
            }
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Report(TextKey.ErrorCannotOpen, path, exception);
            return false;
        }
    }

    /// <summary>
    /// Files dropped on the window, as upstream's <c>ImageFileDrop</c> takes them: projects open in
    /// tabs of their own; images join the open document as layers, or — with nothing open — the
    /// first opens as a document and the rest join it.
    /// </summary>
    public void Drop(IReadOnlyList<string> paths)
    {
        foreach (string project in paths.Where(IsProject)) OpenPath(project);

        List<string> images = [.. paths.Where(path => !IsProject(path))];
        if (images.Count == 0) return;
        if (!canvas.HasDocument)
        {
            if (!OpenPath(images[0])) return;
            images.RemoveAt(0);
        }
        AddImages(images);
    }

    /// <summary>Saves to a path without asking — for the self-test's round trip.</summary>
    internal void SaveTo(string path) => Write(path);

    /// <summary>Saves to the project the document came from, or asks where when there is none.</summary>
    public void Save()
    {
        if (canvas.FilePath is string path) Write(path);
        else SaveAs();
    }

    /// <summary>Asks where, then saves there.</summary>
    public void SaveAs()
    {
        string suggested = canvas.FilePath is string path
            ? Path.GetFileNameWithoutExtension(path)
            : canvas.Title;

        if (Pick(save: true, Filter((TextKey.FileTypeProject, "*.comp")), "comp", suggested) is string chosen)
            Write(chosen);
    }

    private void Write(string path)
    {
        if (canvas.Document is not CanvasDocument document) return;

        try
        {
            ProjectStore.Save(ProjectMapping.ToSnapshot(document, canvas.ActiveLayerId), path);
            canvas.MarkSaved(path);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Report(TextKey.ErrorCannotSave, path, exception);
        }
    }

    /// <summary>Asks where, then writes the composited document there as a PNG.</summary>
    public void ExportPng()
    {
        if (canvas.Document is not CanvasDocument document) return;
        if (Pick(save: true, Filter((TextKey.FileTypePng, "*.png")), "png", canvas.Title) is not string path) return;

        try
        {
            // The reference rasteriser, not the GPU: an export is read back whole anyway, and this
            // way it is the same pixels on every machine.
            using var backend = new SoftwareRenderBackend();
            using PixelBuffer pixels = LayerCompositor.Render(document, backend);
            File.WriteAllBytes(path, Png.Encode(pixels));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Report(TextKey.ErrorCannotSave, path, exception);
        }
    }

    /// <summary>
    /// Asks where, then writes the composited document there as a JPEG, over white — JPEG has no
    /// transparency, and white is what the canvas shows under it.
    /// </summary>
    public void ExportJpeg()
    {
        if (canvas.Document is not CanvasDocument document) return;

        PixelBuffer pixels;
        using (var backend = new SoftwareRenderBackend()) pixels = LayerCompositor.Render(document, backend);

        // The quality and matte are chosen on a sheet, which owns the composite from here; without
        // the panels — the self-test's menus — it goes straight to the file at the default quality.
        if (Chrome is Chrome chrome)
        {
            chrome.Open(new JpegSheet(pixels, SaveJpeg, chrome));
            return;
        }

        try
        {
            SaveJpeg(ImageWriter.EncodeJpeg(pixels, ImageWriter.DefaultQuality, (1, 1, 1)));
        }
        finally
        {
            pixels.Release();
        }
    }

    /// <summary>Asks where, and writes an encoded JPEG there. False when the user cancels or it fails.</summary>
    private bool SaveJpeg(byte[] encoded)
    {
        if (Pick(save: true, Filter((TextKey.FileTypeJpeg, "*.jpg;*.jpeg")), "jpg", canvas.Title) is not string path) return false;

        try
        {
            File.WriteAllBytes(path, encoded);
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Report(TextKey.ErrorCannotSave, path, exception);
            return false;
        }
    }

    /// <summary>Failures go to the log only, not to a message box — for the self-test, which cannot answer one.</summary>
    internal bool Quiet { get; set; }

    /// <summary>The panels, for the sheets File's commands open. Set once the panels exist.</summary>
    public Chrome? Chrome { get; set; }

    /// <summary>File › New: the size sheet, then — once any changes to the open document are dealt with — a blank canvas.</summary>
    public void NewCanvas() =>
        Chrome?.Open(new NewCanvasSheet((width, height) =>
            canvas.Open(DocumentCommands.New(width, height, 72, background: null))));

    /// <summary>A document holding one image as its only layer, the canvas its size.</summary>
    public static CanvasDocument FromImage(PixelBuffer pixels, string name)
    {
        var layer = new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = name,
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

    /// <summary>Says what went wrong, in the chosen language.</summary>
    private void Report(TextKey what, string path, Exception exception)
    {
        string reason = exception switch
        {
            ProjectException { Kind: ProjectErrorKind.Version } project =>
                Localizer.Format(TextKey.ErrorProjectVersion, project.Version),
            ProjectException { Kind: ProjectErrorKind.MissingImage } => Localizer.Text(TextKey.ErrorProjectMissingImage),
            ProjectException { Kind: ProjectErrorKind.TooLarge } => Localizer.Text(TextKey.ErrorProjectTooLarge),
            ProjectException { Kind: ProjectErrorKind.Encode } => Localizer.Text(TextKey.ErrorProjectEncode),
            ProjectException => Localizer.Text(TextKey.ErrorProjectInvalid),
            _ when what == TextKey.ErrorCannotOpen => Localizer.Text(TextKey.ErrorImageUnreadable),
            _ => string.Empty,
        };

        string message = Localizer.Format(what, Path.GetFileName(path));
        if (reason.Length > 0) message += "\n\n" + reason;

        // The self-test has no one to click OK; the log has the message already.
        if (!Quiet) MessageBoxW(owner, message, Localizer.Text(TextKey.AppTitle), MB_OK | MB_ICONERROR);
    }

    /// <summary>The common dialogs' filter format: name, pattern, name, pattern, ending in two nulls.</summary>
    private static string Filter(params (TextKey Name, string Pattern)[] types) =>
        string.Concat(types.Select(type => $"{Localizer.Text(type.Name)} ({type.Pattern})\0{type.Pattern}\0")) + "\0";

    /// <summary>Shows an open or save dialog. Null when the user cancels.</summary>
    private string? Pick(bool save, string filter, string? defaultExtension, string? suggested)
    {
        const int Capacity = 32768;
        char* file = stackalloc char[Capacity];
        file[0] = '\0';

        if (suggested is not null)
        {
            // Characters Windows will not have in a file name are dropped from the suggestion.
            string clean = string.Concat(suggested.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
            clean = clean[..Math.Min(clean.Length, 255)];
            clean.AsSpan().CopyTo(new Span<char>(file, Capacity));
            file[clean.Length] = '\0';
        }

        fixed (char* filters = filter)
        fixed (char* extension = defaultExtension)
        {
            var dialog = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW),
                hwndOwner = owner,
                lpstrFilter = filters,
                nFilterIndex = 1,
                lpstrFile = file,
                nMaxFile = Capacity,
                lpstrDefExt = extension,
                Flags = OFN_EXPLORER | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
                        | (save ? OFN_OVERWRITEPROMPT : OFN_FILEMUSTEXIST),
            };

            bool chosen = save ? GetSaveFileNameW(ref dialog) : GetOpenFileNameW(ref dialog);
            return chosen ? new string(file) : null;
        }
    }

    /// <summary>An open dialog that takes several files. Empty when the user cancels.</summary>
    private List<string> PickMany(string filter)
    {
        const int Capacity = 65536;
        char* files = stackalloc char[Capacity];
        files[0] = '\0';

        fixed (char* filters = filter)
        {
            var dialog = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW),
                hwndOwner = owner,
                lpstrFilter = filters,
                nFilterIndex = 1,
                lpstrFile = files,
                nMaxFile = Capacity,
                Flags = OFN_EXPLORER | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_FILEMUSTEXIST | OFN_ALLOWMULTISELECT,
            };

            if (!GetOpenFileNameW(ref dialog)) return [];
        }

        // One file comes back as a full path; several as the folder, then each name, each ended by a
        // null and the whole by two.
        var parts = new List<string>();
        for (char* part = files; *part != '\0'; part += parts[^1].Length + 1) parts.Add(new string(part));

        return parts.Count <= 1 ? parts : [.. parts.Skip(1).Select(name => Path.Combine(parts[0], name))];
    }
}
