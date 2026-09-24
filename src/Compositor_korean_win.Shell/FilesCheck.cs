using Compositor_korean_win.Core;
using Vortice.DXGI;
using static Compositor_korean_win.Shell.Win32;

namespace Compositor_korean_win.Shell;

/// <summary>
/// M6.6's check: a document saved and opened again is the same document, several documents keep
/// apart in their tabs, and files dropped on the window land where they should.
/// </summary>
/// <remarks>
/// The round trip goes through the same code the File menu does — <see cref="DocumentFiles"/> —
/// with only the file dialog left out, and compares both what the project holds (layers, their
/// kinds, placements, masks and adjustments) and what it looks like, pixel for pixel.
/// </remarks>
internal static class FilesCheck
{
    internal sealed record Result(int Layers, int MaximumDifference, int Tabs, IReadOnlyList<string> Errors)
    {
        public bool Passed => Errors.Count == 0;
    }

    public static Result Run(GraphicsDevice device, Format format, PixelBuffer image)
    {
        var errors = new List<string>();
        string folder = Path.Combine(Path.GetTempPath(), "compositor-files-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        int layers = 0, difference = -1, tabs = 0;

        using var window = new MainWindow(device, format, 1024, 700, visible: false);
        using var canvas = new CanvasView(device);
        window.AttachCanvas(canvas);
        var files = new DocumentFiles(window.Handle, canvas, format) { Quiet = true };
        (List<Command> commands, List<MenuEntry.Submenu> layout) = AppCommands.Create(canvas, files, window.Handle);
        using var menu = new MenuBar(window.Handle, commands, layout);

        void Expect(bool condition, string what)
        {
            if (!condition) errors.Add(what);
        }

        try
        {
            // A document with a bit of everything a project stores.
            canvas.Open(DocumentFiles.FromImage(PixelRegion.Copy(image, new PixelRect(0, 0, image.Width, image.Height)), "photo"),
                        path: null, "photo");
            menu.Run(CommandIds.AddLayerMask);
            menu.Run(CommandIds.NewLayer);
            menu.Run(CommandIds.GroupLayers);
            canvas.ChooseTopImageLayer();
            canvas.StartFilter(FilterCommand.Levels, asLayer: true);
            canvas.FilterAdjustment = canvas.FilterAdjustment with
            {
                Levels = new LevelsSettings { Ranges = new EquatableList<LevelRange>([new LevelRange { Gamma = 1.6 }, new(), new(), new()]) },
            };
            canvas.FinishFilter(keep: true);
            CanvasDocument original = canvas.Document!;
            layers = original.Layers.Count;

            // Saved and opened again, into a tab of its own.
            string project = Path.Combine(folder, "round trip.comp");
            files.SaveTo(project);
            Expect(!canvas.IsModified, "saving left the document marked changed");
            Expect(files.OpenPath(project), "the saved project did not open");
            Expect(canvas.Tabs.Count == 2 && canvas.ActiveTab == 1, $"the project opened in tab {canvas.ActiveTab} of {canvas.Tabs.Count}");
            CanvasDocument reopened = canvas.Document!;

            Expect(reopened.Width == original.Width && reopened.Height == original.Height, "the canvas size changed");
            Expect(reopened.Layers.Count == original.Layers.Count, $"{original.Layers.Count} layers came back as {reopened.Layers.Count}");
            foreach ((ImageLayer before, ImageLayer after) in original.Layers.Zip(reopened.Layers))
            {
                string name = before.Name;
                Expect(before.Name == after.Name, $"layer \"{name}\" came back as \"{after.Name}\"");
                Expect(before.IsGroup == after.IsGroup, $"layer \"{name}\" changed whether it is a folder");
                Expect(before.Transform == after.Transform, $"layer \"{name}\" moved");
                Expect(before.Adjustment == after.Adjustment, $"layer \"{name}\"'s adjustment changed");
                Expect((before.Mask is null) == (after.Mask is null), $"layer \"{name}\"'s mask was lost or gained");
                Expect((before.Image is null) == (after.Image is null), $"layer \"{name}\"'s pixels were lost or gained");
                Expect((before.ParentId is null) == (after.ParentId is null), $"layer \"{name}\" left or joined a folder");
            }

            difference = MaximumDifference(original, reopened);
            Expect(difference <= 1, $"the reopened document draws differently, by up to {difference}");

            // Two documents, each with its own history.
            Expect(!canvas.CanUndo, "the reopened document has history it did not make");
            canvas.SwitchTo(0);
            Expect(canvas.ActiveTab == 0 && ReferenceEquals(canvas.Document, original), "switching back did not bring the first document back");
            Expect(canvas.CanUndo, "the first document lost its history in the switch");
            canvas.CycleTabs(1);
            Expect(canvas.ActiveTab == 1 && ReferenceEquals(canvas.Document, reopened), "Next Document did not go to the other tab");

            // Dropped files: an image joins the open document, a project opens a tab.
            string picture = Path.Combine(folder, "dropped.png");
            File.WriteAllBytes(picture, Png.Encode(image));
            int layerCount = canvas.Document!.Layers.Count;
            files.Drop([picture]);
            Expect(canvas.Document!.Layers.Count == layerCount + 1, "a dropped image did not become a layer");
            Expect(canvas.IsModified, "a dropped image did not mark the document changed");
            files.Drop([project]);
            Expect(canvas.Tabs.Count == 3 && canvas.ActiveTab == 2, "a dropped project did not open in a new tab");
            tabs = canvas.Tabs.Count;

            // Closing goes to the neighbour, and the last close leaves the window empty.
            canvas.Close();
            Expect(canvas.Tabs.Count == 2 && canvas.ActiveTab == 1, "closing a tab did not show its neighbour");
            canvas.Close();
            canvas.Close();
            Expect(!canvas.HasDocument && canvas.Tabs.Count == 0, "closing every tab left a document open");

            // With nothing open, a dropped image opens as a document.
            files.Drop([picture]);
            Expect(canvas.HasDocument && canvas.Title == "dropped", $"a dropped image opened as \"{canvas.Title}\"");
            canvas.Close();

            // A PSD: the project saved as one, which then opens — and opens when dropped — as a
            // document of its own that looks the same and saves back to itself.
            Expect(files.OpenPath(project), "the project did not open again for the PSD check");
            CanvasDocument source = canvas.Document!;
            string psd = Path.Combine(folder, "round trip.psd");
            files.SaveTo(psd);
            Expect(!canvas.IsModified && canvas.FilePath == psd, "saving as a PSD did not leave the document saved there");
            Expect(files.OpenPath(psd), "the saved PSD did not open");
            CanvasDocument fromPsd = canvas.Document!;
            Expect(canvas.Tabs.Count == 2, $"the PSD opened with {canvas.Tabs.Count} tabs open");
            Expect(canvas.FilePath == psd, "the opened PSD would not save back to itself");
            Expect(fromPsd.Layers.Count == source.Layers.Count, $"{source.Layers.Count} layers came back from the PSD as {fromPsd.Layers.Count}");
            Expect(fromPsd.Layers.Select(layer => layer.Name).SequenceEqual(source.Layers.Select(layer => layer.Name)),
                   "layer names changed through the PSD");
            int psdDifference = MaximumDifference(source, fromPsd);
            Expect(psdDifference <= 2, $"the document draws differently after the PSD, by up to {psdDifference}");
            files.Drop([psd]);
            Expect(canvas.Tabs.Count == 3 && canvas.ActiveTab == 2, "a dropped PSD did not open in a new tab");
            while (canvas.HasDocument) canvas.Close();
        }
        catch (Exception exception)
        {
            errors.Add("threw: " + exception.Message);
            Console.Error.WriteLine(exception);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // Only a temporary folder is left behind.
            }
        }

        return new Result(layers, difference, tabs, errors);
    }

    /// <summary>The largest difference in any channel of any pixel between two documents drawn.</summary>
    private static int MaximumDifference(CanvasDocument a, CanvasDocument b)
    {
        using var backend = new SoftwareRenderBackend();
        using PixelBuffer first = LayerCompositor.Render(a, backend);
        using PixelBuffer second = LayerCompositor.Render(b, backend);
        if (first.Width != second.Width || first.Height != second.Height) return 255;

        int largest = 0;
        for (int y = 0; y < first.Height; y++)
        {
            ReadOnlySpan<byte> one = first.Row(y), two = second.Row(y);
            for (int i = 0; i < first.Width * 4; i++) largest = Math.Max(largest, Math.Abs(one[i] - two[i]));
        }
        return largest;
    }
}
