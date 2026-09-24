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
            canvas.Gradient = canvas.Gradient with { Style = GradientStyle.ForegroundToBackground };
            Drag(0, w, h * 0.5);
            Expect(canvas.HasPendingGradient, "a gradient was committed before it could be adjusted");
            canvas.Key(Win32.VK_RETURN, control: false);
            Expect(!canvas.HasPendingGradient, "Enter did not apply the pending gradient");
            ImageLayer graded = canvas.Document!.Layer(id)!;
            Expect(Level(graded, 0.02, 0.5) < 40 && Level(graded, 0.98, 0.5) > 215, "a gradient on the mask did not run black to white");

            // A second gradient stays pending and Escape leaves the committed pixels untouched.
            PixelBuffer committedMask = graded.Mask!.Coverage;
            Drag(w, 0, h * 0.5);
            Expect(canvas.HasPendingGradient, "a replacement gradient did not remain pending");
            canvas.Key(Win32.VK_ESCAPE, control: false);
            Expect(!canvas.HasPendingGradient && ReferenceEquals(canvas.Document!.Layer(id)!.Mask!.Coverage, committedMask),
                   "Escape did not discard the pending gradient");

            // Filters are for pixels.
            Expect(!canvas.CanFilter, "a filter could start on a mask");

            // Copied as grey pixels when the mask is the target.
            if (canvas.CopyPixels(merged: false) is var (grey, _))
            {
                ReadOnlySpan<byte> pixel = grey.Row(grey.Height / 2).Slice(grey.Width / 2 * 4, 4);
                Expect(pixel[0] == pixel[1] && pixel[1] == pixel[2] && pixel[3] == 255, "Copy on a mask did not take its grey");
                grey.Release();
            }
            else
            {
                Expect(false, "Copy on a mask took nothing");
            }

            // Unlinked, the Move tool carries the mask alone.
            menu.Run(CommandIds.ToggleMaskLink);
            Expect(canvas.TransformsMask, "unlinking did not let the mask move on its own");
            LayerTransform layerAt = canvas.Document!.Layer(id)!.Transform;
            canvas.SetTool(CanvasTool.Move);
            Drag(w * 0.5, w * 0.5 + 24, h * 0.5);
            ImageLayer apart = canvas.Document!.Layer(id)!;
            Expect(apart.Transform == layerAt && apart.Mask?.Placement is LayerTransform maskAt && maskAt.Origin.X > layerAt.Origin.X,
                   "the Move tool did not move an unlinked mask on its own");
            LayerTransform? maskWas = apart.Mask?.Placement;

            // And with the layer as the target, the layer moves and the unlinked mask stays.
            // A different distance: a mask landing exactly on its layer goes back to following it.
            canvas.ClickLayer(id, control: false, shift: false, [id]);
            Drag(w * 0.5, w * 0.5 + 40, h * 0.5);
            ImageLayer movedLayer = canvas.Document!.Layer(id)!;
            Expect(movedLayer.Transform != layerAt && movedLayer.Mask?.Placement == maskWas,
                   "an unlinked mask moved with its layer");

            // Dropped on another layer, a copy of the mask lands there, where it sat, and is the target.
            menu.Run(CommandIds.NewLayer);
            Guid other = canvas.ActiveLayer!.Id;
            Expect(canvas.CanCopyMaskTo(id, other), "a mask could not be copied to a plain layer");
            canvas.CopyMask(id, other);
            ImageLayer receiver = canvas.Document!.Layer(other)!;
            Expect(receiver.Mask?.Placement == maskWas && canvas.EditingMask, "a copied mask did not land where it sat");
            canvas.ClickLayer(other, control: false, shift: false, [other]);
            menu.Run(CommandIds.DeleteLayer);
            canvas.ClickLayer(id, control: false, shift: false, [id]);
            canvas.ClickMask(id);

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

            // The Crop tool: a frame dragged inside the canvas, applied with Enter, becomes the canvas
            // as one step, the layer moved and nothing cut.
            canvas.SetTool(CanvasTool.Crop);
            Expect(canvas.CropFrame is { X: 0, Y: 0 } whole && whole.Width == w && whole.Height == h,
                   "the Crop tool did not start with the whole canvas framed");
            CanvasDocument uncropped = canvas.Document!;
            LayerTransform placedBefore = uncropped.Layer(id)!.Transform;
            Point CropView(double x, double y) => canvas.Viewport.ViewPoint(new Point(x, y), uncropped.Size);
            // Well inside, away from the canvas edges the frame would snap to.
            canvas.PointerDown(CropView(w * 0.2, h * 0.25), pan: false);
            canvas.PointerMoved(CropView(w * 0.5, h * 0.5), shift: false, alt: false, control: false);
            canvas.PointerMoved(CropView(w * 0.7, h * 0.75), shift: false, alt: false, control: false);
            canvas.PointerUp();
            Core.Rect frame = canvas.CropFrame!.Value;
            canvas.Key(Win32.VK_RETURN, control: false);
            CanvasDocument cropped = canvas.Document!;
            Expect(cropped.Width == (int)frame.Width && cropped.Height == (int)frame.Height,
                   $"cropping to {frame.Width}×{frame.Height} left a {cropped.Width}×{cropped.Height} canvas");
            ImageLayer croppedLayer = cropped.Layer(id)!;
            Expect(croppedLayer.Transform.Origin == new Point(placedBefore.Origin.X - frame.X, placedBefore.Origin.Y - frame.Y)
                   && ReferenceEquals(croppedLayer.Image, uncropped.Layer(id)!.Image), "cropping did not just move the layer");
            canvas.Undo();
            Expect(canvas.Document!.Width == w && canvas.Document.Height == h, "undo did not put the canvas back");

            // Transform Selection: the selected pixels lifted, scaled up twice, laid back with Enter —
            // one step, the layer grown to hold them, the selection gone with them.
            canvas.SetTool(CanvasTool.RectangleMarquee);
            Point At(double x, double y) => canvas.Viewport.ViewPoint(new Point(x, y), canvas.Document!.Size);
            canvas.PointerDown(At(w * 0.1, h * 0.1), pan: false);
            canvas.PointerMoved(At(w * 0.4, h * 0.4), shift: false, alt: false, control: false);
            canvas.PointerUp();
            double selectedWidth = canvas.Selection?.Bounds.Width ?? 0;
            int layerCount = canvas.Document!.Layers.Count;
            CanvasDocument unlifted = canvas.Document;
            menu.Run(CommandIds.Transform);
            Expect(canvas.IsFloating && canvas.Document!.Layers.Count == layerCount + 1, "Ctrl+T did not lift the selection");
            Expect(!canvas.CanEdit, "the menus stayed open while pixels floated");
            canvas.ChangeTransform(t => t with { Size = new Core.Size(t.Size.Width * 2, t.Size.Height * 2) });
            canvas.Key(Win32.VK_RETURN, control: false);
            Expect(!canvas.IsFloating && canvas.Document!.Layers.Count == layerCount, "Enter did not lay the pixels down");
            Expect(canvas.Selection is { } grownSelection && Math.Abs(grownSelection.Bounds.Width - selectedWidth * 2) < 2,
                   $"the selection did not follow the pixels ({selectedWidth} → {canvas.Selection?.Bounds.Width})");
            canvas.Undo();
            Expect(ReferenceEquals(canvas.Document, unlifted) || canvas.Document == unlifted, "Transform Selection was not one step");

            // Escape puts everything back.
            menu.Run(CommandIds.Transform);
            canvas.Key(Win32.VK_ESCAPE, control: false);
            Expect(!canvas.IsFloating && canvas.Document == unlifted && canvas.Selection is not null, "Escape did not put the pixels back");

            // Selection options persist without modifier keys, and their antialias choice reaches
            // the selection model. The wand exposes all three sample sizes and composited sampling.
            canvas.SelectionMode = SelectionModeChoice.Add;
            canvas.SelectionAntialiased = false;
            canvas.SetTool(CanvasTool.RectangleMarquee);
            canvas.PointerDown(At(w * 0.55, h * 0.55), pan: false);
            canvas.PointerMoved(At(w * 0.75, h * 0.75), shift: false, alt: false, control: false);
            canvas.PointerUp();
            Expect(canvas.Selection is { IsAntialiased: false, Shapes.Count: > 1 },
                   "the persistent Add or anti-alias selection option did not reach the selection");
            canvas.Wand = canvas.Wand with { SampleRadius = 2, SampleAllLayers = true };
            Expect(canvas.Wand is { SampleRadius: 2, SampleAllLayers: true },
                   "the Magic Wand did not retain its sample size and all-layers option");
            canvas.SelectionMode = SelectionModeChoice.Replace;
            canvas.SelectionAntialiased = true;

            canvas.AutoSelect = true;
            Expect(canvas.AutoSelect, "the Move tool did not retain Auto Select");
            canvas.AutoSelect = false;

            // The Layers panel: a row dropped below another, Alt copying it, a thumbnail's pixels as
            // the selection, a hide-all mask, and Shift-click switching a mask off.
            menu.Run(CommandIds.Deselect);
            canvas.ClickLayer(id, control: false, shift: false, [id]);
            menu.Run(CommandIds.NewLayer);
            Guid fresh = canvas.ActiveLayer!.Id;
            canvas.PlaceLayers(fresh, id, LayerDrop.Below, copy: false);
            List<Guid> order = [.. canvas.Document!.Layers.Select(layer => layer.Id)];
            Expect(order.IndexOf(fresh) < order.IndexOf(id), "a row dropped below another did not go below it");
            int counted = canvas.Document.Layers.Count;
            canvas.PlaceLayers(fresh, id, LayerDrop.Above, copy: true);
            Expect(canvas.Document!.Layers.Count == counted + 1, "Alt-dropping a row did not copy it");
            canvas.LoadLayerSelection(id, add: false, subtract: false);
            Expect(canvas.Selection is not null, "Ctrl-click on a thumbnail did not load the layer's pixels");
            canvas.ClickLayer(id, control: false, shift: false, [id]);
            menu.Run(CommandIds.Deselect);
            menu.Run(CommandIds.AddHideMask);
            Expect(canvas.Document!.Layer(id)!.Mask is { Coverage.Width: 1 } hidden && hidden.Coverage.Row(0)[0] == 0,
                   "Add Hide-All Mask did not add a black mask");
            canvas.ToggleMaskOf(id);
            Expect(canvas.Document!.Layer(id)!.Mask?.IsEnabled == false, "Shift-click did not switch the mask off");

            // The Zoom tool doubles on a click and halves with Alt; the Hand moves the view; the
            // Eyedropper takes the colour the document shows.
            canvas.FitOnScreen();
            Point Middle() => canvas.Viewport.ViewPoint(new Point(w / 2.0, h / 2.0), canvas.Document!.Size);
            canvas.SetTool(CanvasTool.Zoom);
            double fitted = canvas.Viewport.Zoom;
            canvas.PointerDown(Middle(), pan: false);
            canvas.PointerUp();
            Expect(Math.Abs(canvas.Viewport.Zoom - fitted * 2) < 1e-6, $"a Zoom click went from {fitted} to {canvas.Viewport.Zoom}");

            canvas.SetTool(CanvasTool.Hand);
            Point startView = Middle();
            canvas.PointerDown(startView, pan: false);
            canvas.PointerMoved(new Point(startView.X + 40, startView.Y), shift: false, alt: false, control: false);
            canvas.PointerUp();
            Expect(Math.Abs(Middle().X - (startView.X + 40)) < 1, "the Hand did not move the view");

            canvas.SetTool(CanvasTool.Eyedropper);
            canvas.ForegroundColor = new Rgba(1, 2, 3);
            // Disabling a mask does not stop targeting it. Sampling must leave the palette alone
            // until the layer's pixels are chosen, just as it does in the interactive canvas.
            Expect(canvas.EditingMask, "disabling the mask unexpectedly changed the edit target");
            canvas.PointerDown(Middle(), pan: false);
            canvas.PointerUp();
            Expect(canvas.ForegroundColor == new Rgba(1, 2, 3), "the Eyedropper changed the palette while targeting a mask");

            canvas.ClickLayer(id, control: false, shift: false, [id]);
            Expect(!canvas.EditingMask, "choosing the layer did not leave mask editing before sampling");
            if (canvas.CompositeColour(new Point(w / 2.0, h / 2.0)) is (double red, double green, double blue))
            {
                var expected = new Rgba((byte)Math.Round(red * 255), (byte)Math.Round(green * 255), (byte)Math.Round(blue * 255));
                canvas.ForegroundColor = new Rgba((byte)(expected.R ^ 255), expected.G, expected.B);
                canvas.PointerDown(Middle(), pan: false);
                canvas.PointerUp();
                Expect(canvas.ForegroundColor == expected, "the Eyedropper did not take the displayed colour");
            }
            else
            {
                Expect(false, "the Eyedropper check had no visible pixel to sample");
            }

            // A layer row can leave its project: dropping on another tab copies it there and a
            // drop on the empty tab strip makes a new project, with independent layer ids.
            int sourceTab = canvas.ActiveTab;
            Guid sourceLayer = canvas.ActiveLayer!.Id;
            using PixelBuffer targetPixels = PixelRegion.Copy(image, new PixelRect(0, 0, image.Width, image.Height));
            canvas.Open(DocumentFiles.FromImage(targetPixels.Retain(), "target"), path: null, "target");
            int targetTab = canvas.ActiveTab;
            int targetLayers = canvas.Document!.Layers.Count;
            canvas.SwitchTo(sourceTab);
            Expect(canvas.CopyLayersToTab(sourceLayer, targetTab), "a layer could not be copied to another project tab");
            Expect(canvas.ActiveTab == targetTab && canvas.Document!.Layers.Count > targetLayers,
                   "a cross-project layer drop did not select and update its target tab");
            Guid copiedLayer = canvas.ActiveLayer!.Id;
            int tabsBeforeNew = canvas.Tabs.Count;
            Expect(copiedLayer != sourceLayer && canvas.CopyLayersToNewTab(copiedLayer),
                   "a layer drop could not create a new project tab");
            Expect(canvas.Tabs.Count == tabsBeforeNew + 1 && canvas.Document!.Layers.Count > 0,
                   "the new project tab did not receive the dropped layer");

            // Browsers and Office can supply image bytes without a file path. The same path the
            // OLE target uses must decode those bytes and honour the empty part of the tab strip.
            int tabsBeforeImageDrop = canvas.Tabs.Count;
            files.DropImage(Png.Encode(image), png: true, new DropDestination(null, NewTab: true));
            Expect(canvas.Tabs.Count == tabsBeforeImageDrop + 1,
                   "an in-memory image drop did not create a project tab");
            Expect(canvas.Document!.Width == image.Width && canvas.Document.Height == image.Height,
                   "an in-memory image drop did not preserve its dimensions");

            // Keyboard-only controls and the right-drag gesture share the exact settings exposed
            // by the options bar, so neither path may keep a second hidden value.
            canvas.SetTool(CanvasTool.Brush);
            canvas.Key(Win32.VK_1 + 4, control: false);
            Expect(Math.Abs(canvas.Brush.Opacity - 0.5) < 0.001, "the numeric opacity shortcut did not set 50%");
            canvas.Brush = canvas.Brush with { Diameter = 40, Hardness = 0.5 };
            canvas.Key(Win32.VK_OEM_6, control: false, shift: true);
            Expect(canvas.Brush.Diameter == 40 && canvas.Brush.Hardness > 0.5,
                   "Shift+] did not change hardness without changing diameter");
            Expect(canvas.BeginBrushAdjust(new Point(10, 10)), "a brush right-drag could not begin");
            canvas.DragBrushAdjust(new Point(30, -5));
            canvas.EndBrushAdjust();
            Expect(canvas.Brush.Diameter > 40 && canvas.Brush.Hardness > 0.5,
                   "a brush right-drag did not change size and hardness");

            canvas.SetTool(CanvasTool.Shape);
            ShapeKind shapeBefore = canvas.Shape.Kind;
            canvas.Key(Win32.VK_U, control: false, shift: true);
            Expect(canvas.Shape.Kind != shapeBefore, "Shift+U did not cycle the shape kind");
            LayerBlendMode blendBefore = canvas.ActiveLayer!.BlendMode;
            canvas.Key(Win32.VK_OEM_PLUS, control: false, shift: true);
            Expect(canvas.ActiveLayer.BlendMode != blendBefore, "Shift+Plus did not cycle the layer blend mode");

            canvas.SetZoomPercent(125);
            Expect(Math.Abs(canvas.Viewport.Zoom - 1.25) < 0.001, "the exact zoom field did not apply its percentage");
            canvas.Key(Win32.VK_A, control: false);
            Expect(canvas.Tool == CanvasTool.Idle, "A did not select the inert inspection tool");
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
