using Compositor_korean_win.Core;
using Vortice.DXGI;
using Point = Compositor_korean_win.Core.Point;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The check for what M6 picked up from M4: strokes on a layer's mask, Smudge and Liquify, driven
/// through the canvas the way the mouse drives it.
/// </summary>
/// <remarks>
/// The arithmetic is tested in Core. What only the canvas can get wrong is the wiring: that a
/// stroke lands in the mask and not the layer, in the mask's own grid, as one undo step, and that a
/// warp ends up in the layer's pixels.
/// </remarks>
internal static class ToolsCheck
{
    internal sealed record Result(int Checks, IReadOnlyList<string> Errors)
    {
        public bool Passed => Errors.Count == 0;
    }

    public static Result Run(GraphicsDevice device, Format format, PixelBuffer image)
    {
        var errors = new List<string>();
        int checks = 0;

        using var window = new MainWindow(device, format, 1024, 700, visible: false);
        using var canvas = new CanvasView(device);
        window.AttachCanvas(canvas);
        var files = new DocumentFiles(window.Handle, canvas, format) { Quiet = true };
        (List<Command> commands, List<MenuEntry.Submenu> layout) = AppCommands.Create(canvas, files, window.Handle);
        using var menu = new MenuBar(window.Handle, commands, layout);

        void Expect(bool condition, string what)
        {
            checks++;
            if (!condition) errors.Add(what);
        }

        // A drag across the middle of the document, in document pixels.
        void Drag(double fromX, double toX, double y)
        {
            CanvasDocument document = canvas.Document!;
            Point View(double x) => canvas.Viewport.ViewPoint(new Point(x, y), document.Size);
            canvas.PointerDown(View(fromX), pan: false);
            for (int step = 1; step <= 20; step++)
                canvas.PointerMoved(View(fromX + (toX - fromX) * step / 20), shift: false, alt: false, control: false);
            canvas.PointerUp();
        }

        try
        {
            canvas.Open(DocumentFiles.FromImage(PixelRegion.Copy(image, new PixelRect(0, 0, image.Width, image.Height)), "photo"),
                        path: null, "photo");
            canvas.ChooseTopImageLayer();
            Guid id = canvas.ActiveLayer!.Id;
            PixelBuffer pixels = canvas.ActiveLayer.Image!;
            int w = pixels.Width, h = pixels.Height;

            // Adding a mask makes it the target.
            menu.Run(CommandIds.AddLayerMask);
            Expect(canvas.EditingMask, "a new mask did not become the target");
            Expect(canvas.ShownForeground == Rgba.Black && canvas.ShownBackground == Rgba.White,
                   "the swatches did not show the mask's black and white");

            // A brush on the mask paints the mask black and leaves the layer's pixels alone.
            canvas.SetTool(CanvasTool.Brush);
            canvas.Brush = canvas.Brush with { Diameter = Math.Max(8, h / 8), Hardness = 1, Opacity = 1 };
            Drag(w * 0.25, w * 0.75, h * 0.5);
            ImageLayer painted = canvas.Document!.Layer(id)!;
            Expect(ReferenceEquals(painted.Image, pixels), "a stroke on the mask changed the layer's pixels");
            Expect(painted.Mask is { Coverage.Width: > 1 } && Level(painted, 0.5, 0.5) == 0,
                   "a stroke on the mask did not paint it black");
            Expect(painted.Mask is not null && Level(painted, 0.5, 0.05) == 255, "the mask was painted outside the stroke");

            // One undo step puts the mask back.
            canvas.Undo();
            Expect(canvas.Document!.Layer(id)!.Mask is { Coverage.Width: 1 }, "undo did not take the mask stroke back");
            canvas.Redo();

            // The eraser lays the other grey.
            canvas.SetTool(CanvasTool.Eraser);
            Drag(w * 0.4, w * 0.6, h * 0.5);
            Expect(Level(canvas.Document!.Layer(id)!, 0.5, 0.5) == 255, "the eraser on a mask did not paint it white");

            // Invert Mask, and the mask's black areas as a selection.
            menu.Run(CommandIds.Invert);
            ImageLayer inverted = canvas.Document!.Layer(id)!;
            Expect(Level(inverted, 0.5, 0.05) == 0 && ReferenceEquals(inverted.Image, pixels),
                   "Invert did not invert the mask alone");
            menu.Run(CommandIds.SelectMask);
            Expect(canvas.Selection?.Contains(new Point(w * 0.5, h * 0.05)) == true, "the mask's black areas did not load as a selection");
            menu.Run(CommandIds.Deselect);

            // A gradient on the mask runs from its black to its white.
            canvas.SetTool(CanvasTool.Gradient);
            Drag(0, w, h * 0.5);
            ImageLayer graded = canvas.Document!.Layer(id)!;
            Expect(Level(graded, 0.02, 0.5) < 40 && Level(graded, 0.98, 0.5) > 215, "a gradient on the mask did not run black to white");

            // Filters are for pixels.
            Expect(!canvas.CanFilter, "a filter could start on a mask");

            // Choosing the layer's row puts the pixels back as the target.
            canvas.ClickLayer(id, control: false, shift: false, [id]);
            Expect(!canvas.EditingMask, "choosing the layer left the mask as the target");

            // Smudge and Liquify change the layer's pixels, each as one step.
            menu.Run(CommandIds.DeleteLayerMask);
            foreach (BlurToolMode mode in new[] { BlurToolMode.Smudge, BlurToolMode.Liquify })
            {
                canvas.SetTool(CanvasTool.Blur);
                canvas.BlurMode = mode;
                canvas.Brush = canvas.Brush with { Diameter = Math.Max(8, h / 6), Hardness = 0.5, Opacity = 1 };
                PixelBuffer before = canvas.Document!.Layer(id)!.Image!;
                string undoBefore = canvas.UndoName;
                Drag(w * 0.3, w * 0.7, h * 0.5);
                PixelBuffer after = canvas.Document!.Layer(id)!.Image!;
                Expect(!ReferenceEquals(before, after), $"{mode} did not change the layer");
                Expect(canvas.UndoName != undoBefore, $"{mode} left no history step");
                Expect(canvas.CanEdit, $"{mode} was still under way after the pointer came up");
            }
        }
        catch (Exception exception)
        {
            errors.Add("threw: " + exception.Message);
            Console.Error.WriteLine(exception);
        }

        return new Result(checks, errors);
    }

    /// <summary>The mask's level at a fraction of its width and height.</summary>
    private static int Level(ImageLayer layer, double u, double v)
    {
        if (layer.Mask is not LayerMask mask) return -1;
        PixelBuffer coverage = mask.Coverage;
        int x = Math.Clamp((int)(u * coverage.Width), 0, coverage.Width - 1);
        int y = Math.Clamp((int)(v * coverage.Height), 0, coverage.Height - 1);
        return coverage.Row(y)[x * 4];
    }
}
