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
    /// <summary>Asks for a project or an image and opens it.</summary>
    public void Open()
    {
        string filter = Filter(
            (TextKey.FileTypeProject, "*.comp"),
            (TextKey.FileTypeImages, "*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.gif;*.heic"));

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
}
