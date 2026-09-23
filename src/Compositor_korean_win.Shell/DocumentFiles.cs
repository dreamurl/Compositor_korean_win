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

    /// <summary>Asks for images and adds each as a layer in the middle of the canvas.</summary>
    public void ImportImages()
    {
        if (!canvas.HasDocument) return;

        string filter = Filter((TextKey.FileTypeImages, ImagePatterns));
        List<string> paths = PickMany(filter);
        if (paths.Count == 0) return;

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
        if (!ConfirmDiscard()) return;

        string filter = Filter(
            (TextKey.FileTypeProject, "*.comp"),
            (TextKey.FileTypeImages, ImagePatterns));

        if (Pick(save: false, filter, defaultExtension: null, suggested: null) is not string path) return;

        try
        {
            if (string.Equals(Path.GetExtension(path), ".comp", StringComparison.OrdinalIgnoreCase))
            {
                ProjectSnapshot snapshot = ProjectStore.Load(path);
                canvas.Open(ProjectMapping.ToDocument(snapshot), path);
            }
            else
            {
                using var loader = new ImageLoader();
                PixelBuffer pixels = loader.Load(path, FormatProbe.WicFormatFor(format));
                canvas.Open(FromImage(pixels, Path.GetFileNameWithoutExtension(path)), path: null);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Report(TextKey.ErrorCannotOpen, path, exception);
        }
    }

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
        if (Pick(save: true, Filter((TextKey.FileTypeJpeg, "*.jpg;*.jpeg")), "jpg", canvas.Title) is not string path) return;

        try
        {
            using var backend = new SoftwareRenderBackend();
            using PixelBuffer pixels = LayerCompositor.Render(document, backend);
            ImageWriter.WriteJpeg(pixels, path);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Report(TextKey.ErrorCannotSave, path, exception);
        }
    }

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

        MessageBoxW(owner, message, Localizer.Text(TextKey.AppTitle), MB_OK | MB_ICONERROR);
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
