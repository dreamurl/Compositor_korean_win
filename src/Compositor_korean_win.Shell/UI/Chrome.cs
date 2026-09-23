using Compositor_korean_win.Core;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using static Compositor_korean_win.Shell.Win32;
using Point = Compositor_korean_win.Core.Point;
using Rect = Compositor_korean_win.Core.Rect;

namespace Compositor_korean_win.Shell;

/// <summary>
/// Everything in the window round the canvas: the options bar along the top, the tool rail down the
/// left, the Layers panel on the right and the status bar along the bottom.
/// </summary>
/// <remarks>
/// <para>
/// The layout is upstream's <c>ContentView</c>: a header that changes with the tool, a rail of tools,
/// the canvas, the Layers panel beside it and a status line under everything, in upstream's dark
/// greys. The canvas gets what is left over, and keeps its own coordinates — the window only moves it
/// over (<see cref="CanvasView.Resize(Rect, int, int, double)"/>).
/// </para>
/// <para>
/// Everything here is drawn fresh from the document and the settings each frame (see <see cref="Ui"/>),
/// so a panel cannot show a state the program is no longer in.
/// </para>
/// </remarks>
internal sealed unsafe class Chrome : IDisposable
{
    private const double TopBar = 42, Rail = 48, RightPanel = 264, StatusBar = 26, RowHeight = 34;

    private static readonly (CanvasTool Tool, char Key)[] Tools =
    [
        (CanvasTool.Move, 'V'), (CanvasTool.RectangleMarquee, 'M'), (CanvasTool.EllipseMarquee, 'M'),
        (CanvasTool.Lasso, 'L'), (CanvasTool.PolygonLasso, 'L'), (CanvasTool.MagicWand, 'W'),
        (CanvasTool.Brush, 'B'), (CanvasTool.Eraser, 'E'), (CanvasTool.CloneStamp, 'S'),
        (CanvasTool.Heal, 'J'), (CanvasTool.Blur, 'R'), (CanvasTool.Gradient, 'G'), (CanvasTool.Shape, 'U'),
    ];

    private static readonly FilterCommand[] AdjustmentKinds =
    [
        FilterCommand.Levels, FilterCommand.Curves, FilterCommand.HueSaturation,
        FilterCommand.Exposure, FilterCommand.GradientMap, FilterCommand.Grain,
    ];

    private static readonly uint[] s_customColours = new uint[16];

    private readonly nint _window;
    private readonly CanvasView _canvas;
    private readonly Action _open;
    private readonly Func<int, bool> _runCommand;
    private readonly Ui _ui = new();
    private readonly HashSet<Guid> _collapsed = [];
    private readonly Dictionary<PixelBuffer, ID2D1Bitmap1> _thumbnails = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PixelBuffer> _thumbnailsUsed = new(ReferenceEqualityComparer.Instance);
    private double _scroll;
    private Rect _layersList;

    private nint _renameBox;
    private nint _renameFont;
    private Guid _renaming;

    public Chrome(nint window, CanvasView canvas, Action open, Func<int, bool> runCommand)
    {
        _window = window;
        _canvas = canvas;
        _open = open;
        _runCommand = runCommand;
    }

    public Ui Ui => _ui;

    /// <summary>The canvas's part of a window of this size, in device pixels.</summary>
    public Rect CanvasArea(int width, int height, double scale) =>
        new(Rail * scale, TopBar * scale,
            Math.Max(1, width - (Rail + RightPanel) * scale), Math.Max(1, height - (TopBar + StatusBar) * scale));

    public void Draw(ID2D1DeviceContext context, int width, int height, double scale)
    {
        // A window being torn down can still be asked to paint after its panels are gone; the
        // DirectWrite factory they drew text with is released by then.
        if (_disposed) return;

        context.BeginDraw();
        _ui.Begin(context, scale);
        _thumbnailsUsed.Clear();

        double s = scale;
        Rect canvas = CanvasArea(width, height, s);

        OptionsBar(new Rect(0, 0, width, TopBar * s));
        ToolRail(new Rect(0, TopBar * s, Rail * s, height - (TopBar + StatusBar) * s));
        LayersPanel(new Rect(width - RightPanel * s, TopBar * s, RightPanel * s, height - (TopBar + StatusBar) * s));
        Status(new Rect(0, height - StatusBar * s, width, StatusBar * s));
        if (!_canvas.HasDocument) Welcome(canvas);

        _ui.End();
        context.EndDraw();

        foreach (PixelBuffer stale in _thumbnails.Keys.Where(key => !_thumbnailsUsed.Contains(key)).ToList())
        {
            _thumbnails[stale].Dispose();
            _thumbnails.Remove(stale);
        }
    }

    // MARK: The options bar

    private void OptionsBar(Rect bar)
    {
        _ui.Fill(bar, Ui.Panel);
        _ui.Block(bar);
        _ui.Rule(new Point(0, bar.MaxY - 0.5), new Point(bar.MaxX, bar.MaxY - 0.5), Ui.Line);

        CanvasTool tool = _canvas.Tool;
        string title = Localizer.Text(CanvasView.Name(tool));
        float titleWidth = _ui.Measure(title, Ui.TextSize.Title) + _ui.P(24);
        _ui.Text(title, new Rect(bar.X + _ui.P(14), bar.Y, titleWidth, bar.Height), Ui.Ink, Ui.TextSize.Title);

        double x = bar.X + _ui.P(14) + titleWidth;
        double y = bar.Y + _ui.P(8), h = bar.Height - _ui.P(16);

        Rect Next(double points)
        {
            var area = new Rect(x, y, _ui.P(points), h);
            x += _ui.P(points + 18);
            return area;
        }

        switch (tool)
        {
            case CanvasTool.Brush or CanvasTool.Eraser or CanvasTool.CloneStamp or CanvasTool.Heal or CanvasTool.Blur:
            {
                BrushSettings brush = _canvas.Brush;
                double sizeFraction = Math.Log(Math.Max(1, brush.Diameter)) / Math.Log(2000);
                _ui.Slider(Next(210), Localizer.Text(TextKey.LabelSize), sizeFraction, Pixels(brush.Diameter),
                           f => _canvas.Brush = _canvas.Brush with { Diameter = Math.Round(Math.Clamp(Math.Exp(f * Math.Log(2000)), 1, 2000)) });

                if (tool == CanvasTool.Blur)
                {
                    _ui.Slider(Next(190), Localizer.Text(TextKey.LabelStrength), (brush.BlurRadius - 1) / 49, Pixels(brush.BlurRadius),
                               f => _canvas.Brush = _canvas.Brush with { BlurRadius = Math.Round(1 + f * 49) });
                }
                else
                {
                    _ui.Slider(Next(170), Localizer.Text(TextKey.LabelHardness), brush.Hardness, Percent(brush.Hardness),
                               f => _canvas.Brush = _canvas.Brush with { Hardness = Math.Round(f, 2) });
                    _ui.Slider(Next(180), Localizer.Text(TextKey.LabelOpacity), brush.Opacity, Percent(brush.Opacity),
                               f => _canvas.Brush = _canvas.Brush with { Opacity = Math.Max(0.01, Math.Round(f, 2)) });
                }

                if (tool == CanvasTool.CloneStamp)
                    _ui.Check(Next(90), Localizer.Text(TextKey.LabelAligned), _canvas.CloneAligned,
                              () => _canvas.CloneAligned = !_canvas.CloneAligned);

                if (tool == CanvasTool.Heal)
                {
                    Segment(ref x, y, h, Localizer.Text(TextKey.LabelType),
                    [
                        (Localizer.Text(TextKey.HealContentAware), brush.HealingMode == SpotHealingMode.ContentAware,
                         () => _canvas.Brush = _canvas.Brush with { HealingMode = SpotHealingMode.ContentAware }),
                        (Localizer.Text(TextKey.HealCreateTexture), brush.HealingMode == SpotHealingMode.CreateTexture,
                         () => _canvas.Brush = _canvas.Brush with { HealingMode = SpotHealingMode.CreateTexture }),
                        (Localizer.Text(TextKey.HealProximityMatch), brush.HealingMode == SpotHealingMode.ProximityMatch,
                         () => _canvas.Brush = _canvas.Brush with { HealingMode = SpotHealingMode.ProximityMatch }),
                    ]);
                }
                break;
            }

            case CanvasTool.RectangleMarquee or CanvasTool.EllipseMarquee or CanvasTool.Lasso or CanvasTool.PolygonLasso:
                _ui.Slider(Next(230), Localizer.Text(TextKey.LabelSelectionStep), (_canvas.SelectionStep - 1) / 99.0,
                           Pixels(_canvas.SelectionStep), f => _canvas.SelectionStep = (int)Math.Round(1 + f * 99));
                break;

            case CanvasTool.MagicWand:
            {
                WandSettings wand = _canvas.Wand;
                _ui.Slider(Next(200), Localizer.Text(TextKey.LabelTolerance), wand.Tolerance / 255.0,
                           wand.Tolerance.ToString(System.Globalization.CultureInfo.InvariantCulture),
                           f => _canvas.Wand = _canvas.Wand with { Tolerance = (int)Math.Round(f * 255) });
                _ui.Check(Next(90), Localizer.Text(TextKey.LabelContiguous), wand.Contiguous,
                          () => _canvas.Wand = _canvas.Wand with { Contiguous = !_canvas.Wand.Contiguous });
                break;
            }

            case CanvasTool.Gradient:
            {
                GradientSettings gradient = _canvas.Gradient;
                Segment(ref x, y, h, Localizer.Text(TextKey.LabelType),
                [
                    (Localizer.Text(TextKey.GradientLinear), gradient.Kind == GradientKind.Linear,
                     () => _canvas.Gradient = _canvas.Gradient with { Kind = GradientKind.Linear }),
                    (Localizer.Text(TextKey.GradientRadial), gradient.Kind == GradientKind.Radial,
                     () => _canvas.Gradient = _canvas.Gradient with { Kind = GradientKind.Radial }),
                ]);
                _ui.Slider(Next(180), Localizer.Text(TextKey.LabelOpacity), gradient.Opacity, Percent(gradient.Opacity),
                           f => _canvas.Gradient = _canvas.Gradient with { Opacity = Math.Max(0.01, Math.Round(f, 2)) });
                break;
            }

            case CanvasTool.Shape:
            {
                ShapeSettings shape = _canvas.Shape;
                Segment(ref x, y, h, Localizer.Text(TextKey.LabelType),
                [
                    (Localizer.Text(TextKey.ShapeRectangle), shape.Kind == ShapeKind.Rectangle,
                     () => _canvas.Shape = _canvas.Shape with { Kind = ShapeKind.Rectangle }),
                    (Localizer.Text(TextKey.ShapeEllipse), shape.Kind == ShapeKind.Ellipse,
                     () => _canvas.Shape = _canvas.Shape with { Kind = ShapeKind.Ellipse }),
                ]);
                if (shape.Kind == ShapeKind.Rectangle)
                    _ui.Slider(Next(220), Localizer.Text(TextKey.LabelCornerRadius), shape.CornerRadius / 200, Pixels(shape.CornerRadius),
                               f => _canvas.Shape = _canvas.Shape with { CornerRadius = Math.Round(f * 200) });
                _ui.Slider(Next(180), Localizer.Text(TextKey.LabelOpacity), shape.Opacity, Percent(shape.Opacity),
                           f => _canvas.Shape = _canvas.Shape with { Opacity = Math.Max(0.01, Math.Round(f, 2)) });
                break;
            }

            case CanvasTool.Move when _canvas.ActiveLayer is ImageLayer layer:
            {
                LayerTransform t = layer.Transform;
                (TextKey? Label, string Letter, string Value)[] fields =
                [
                    (null, "X", Number(t.Origin.X)), (null, "Y", Number(t.Origin.Y)),
                    (TextKey.LabelWidth, "", Number(t.Size.Width)), (TextKey.LabelHeight, "", Number(t.Size.Height)),
                    (TextKey.LabelAngle, "", Number(t.Rotation) + "°"),
                ];
                foreach ((TextKey? label, string letter, string value) in fields)
                {
                    string name = label is TextKey key ? Localizer.Text(key) : letter;
                    float nameWidth = _ui.Measure(name) + _ui.P(6);
                    Rect area = Next(110);
                    _ui.Text(name, new Rect(area.X, area.Y, nameWidth, area.Height), Ui.Dim, user: label is null);
                    _ui.Text(value, new Rect(area.X + nameWidth, area.Y, area.Width - nameWidth, area.Height), Ui.Ink, user: true);
                }
                break;
            }
        }
    }

    /// <summary>A labelled row of mutually exclusive buttons.</summary>
    private void Segment(ref double x, double y, double h, string label, (string Text, bool On, Action Pick)[] choices)
    {
        float labelWidth = _ui.Measure(label) + _ui.P(8);
        _ui.Text(label, new Rect(x, y, labelWidth, h), Ui.Dim);
        x += labelWidth;

        foreach ((string text, bool on, Action pick) in choices)
        {
            float width = _ui.Measure(text) + _ui.P(18);
            var area = new Rect(x, y, width, h);
            _ui.Fill(area, on ? Ui.Selected : Ui.Raised);
            _ui.Button(area, pick, null, active: on, label: text);
            x += width + _ui.P(2);
        }
        x += _ui.P(16);
    }

    // MARK: The tool rail

    private void ToolRail(Rect rail)
    {
        _ui.Fill(rail, Ui.Panel);
        _ui.Block(rail);
        _ui.Rule(new Point(rail.MaxX - 0.5, rail.Y), new Point(rail.MaxX - 0.5, rail.MaxY), Ui.Line);

        double size = _ui.P(36);
        double x = rail.X + (rail.Width - size) / 2, y = rail.Y + _ui.P(8);

        foreach ((CanvasTool tool, char key) in Tools)
        {
            var area = new Rect(x, y, size, size);
            CanvasTool chosen = tool;
            string tip = $"{Localizer.Text(CanvasView.Name(tool))} ({key})";
            _ui.Button(area, () => _canvas.SetTool(chosen), tip, active: _canvas.Tool == tool,
                       face: face => Icons.Tool(_ui, chosen, face, Ui.Ink));
            y += size + _ui.P(2);
        }

        // Foreground over background, as in Photoshop, with swap and reset beside them.
        y += _ui.P(12);
        double swatch = _ui.P(22);
        var foreground = new Rect(x + _ui.P(2), y, swatch, swatch);
        var background = new Rect(x + _ui.P(12), y + _ui.P(12), swatch, swatch);

        Swatch(background, _canvas.BackgroundColor, TextKey.TooltipBackground, colour => _canvas.BackgroundColor = colour);
        Swatch(foreground, _canvas.ForegroundColor, TextKey.TooltipForeground, colour => _canvas.ForegroundColor = colour);

        var swap = new Rect(x + _ui.P(24), y - _ui.P(4), _ui.P(14), _ui.P(14));
        _ui.Button(swap, _canvas.SwapColors, Localizer.Text(TextKey.TooltipSwapColors),
                   face: face => Icons.Swap(_ui, Shrink(face, 0.7), Ui.Dim));

        var reset = new Rect(x - _ui.P(2), y + _ui.P(24), _ui.P(12), _ui.P(12));
        _ui.Button(reset, _canvas.DefaultColors, Localizer.Text(TextKey.TooltipDefaultColors), face: face =>
        {
            _ui.Fill(new Rect(face.X + _ui.P(4), face.Y + _ui.P(4), _ui.P(7), _ui.P(7)), new Color4(1, 1, 1, 1));
            _ui.Fill(new Rect(face.X, face.Y, _ui.P(7), _ui.P(7)), new Color4(0, 0, 0, 1));
        });
    }

    private void Swatch(Rect area, Rgba colour, TextKey tooltip, Action<Rgba> set)
    {
        _ui.Fill(area, new Color4(colour.R / 255f, colour.G / 255f, colour.B / 255f, 1f));
        _ui.Frame(area, Ui.Ink);
        _ui.Area(area, () =>
        {
            if (PickColour(colour) is Rgba picked) set(picked);
        }, Localizer.Text(tooltip));
    }

    /// <summary>
    /// The system colour dialog. It speaks the language Windows is in, as the file dialogs do; the
    /// program's own colour picker is M6.5's.
    /// </summary>
    private Rgba? PickColour(Rgba current)
    {
        fixed (uint* custom = s_customColours)
        {
            var dialog = new CHOOSECOLORW
            {
                lStructSize = (uint)sizeof(CHOOSECOLORW),
                hwndOwner = _window,
                rgbResult = (uint)(current.R | current.G << 8 | current.B << 16),
                lpCustColors = custom,
                Flags = CC_RGBINIT | CC_FULLOPEN,
            };

            if (!ChooseColorW(ref dialog)) return null;
            uint value = dialog.rgbResult;
            return new Rgba((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));
        }
    }

    // MARK: The Layers panel

    private void LayersPanel(Rect panel)
    {
        _ui.Fill(panel, Ui.Panel);
        _ui.Block(panel);
        _ui.Rule(new Point(panel.X + 0.5, panel.Y), new Point(panel.X + 0.5, panel.MaxY), Ui.Line);

        double pad = _ui.P(10);
        var header = new Rect(panel.X + pad, panel.Y, panel.Width - pad * 2, _ui.P(30));
        _ui.Text(Localizer.Text(TextKey.PanelLayers), header, Ui.Ink, Ui.TextSize.Title);

        CanvasDocument? document = _canvas.Document;
        ImageLayer? active = _canvas.ActiveLayer;

        // Blend mode and opacity for the active layer.
        var modeRow = new Rect(panel.X + pad, header.MaxY, panel.Width - pad * 2, _ui.P(28));
        var mode = new Rect(modeRow.X, modeRow.Y + _ui.P(2), _ui.P(104), modeRow.Height - _ui.P(4));
        _ui.Fill(mode, Ui.Raised);
        LayerBlendMode blend = active?.BlendMode ?? LayerBlendMode.Normal;
        _ui.Button(mode, PickBlendMode, Localizer.Text(TextKey.LabelBlendMode), enabled: active is not null && _canvas.CanEdit,
                   label: Localizer.Text(BlendName(blend)) + " ▾");

        double opacity = active?.Opacity ?? 1;
        _ui.Slider(new Rect(mode.MaxX + _ui.P(10), modeRow.Y, modeRow.MaxX - mode.MaxX - _ui.P(10), modeRow.Height),
                   Localizer.Text(TextKey.LabelOpacity), opacity, Percent(opacity),
                   f => _canvas.SetOpacity(f), _canvas.BeginOpacity, _canvas.EndOpacity);

        // The buttons along the bottom.
        double buttons = _ui.P(32);
        var bottom = new Rect(panel.X, panel.MaxY - buttons, panel.Width, buttons);
        _ui.Rule(new Point(panel.X, bottom.Y + 0.5), new Point(panel.MaxX, bottom.Y + 0.5), Ui.Line);
        BottomButtons(bottom);

        // The rows, topmost layer first.
        _layersList = new Rect(panel.X, modeRow.MaxY + _ui.P(6), panel.Width, bottom.Y - modeRow.MaxY - _ui.P(6));
        _ui.Rule(new Point(panel.X, _layersList.Y - 0.5), new Point(panel.MaxX, _layersList.Y - 0.5), Ui.Line);
        if (document is null) return;

        List<(ImageLayer Layer, int Depth)> rows = Rows(document);
        IReadOnlyList<Guid> order = [.. rows.Select(row => row.Layer.Id)];

        double rowHeight = _ui.P(RowHeight);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, rows.Count * rowHeight - _layersList.Height));

        _ui.Context.PushAxisAlignedClip(Ui.Raw(_layersList), AntialiasMode.Aliased);
        double y = _layersList.Y - _scroll;
        foreach ((ImageLayer layer, int depth) in rows)
        {
            var row = new Rect(_layersList.X, y, _layersList.Width, rowHeight);
            if (row.MaxY >= _layersList.Y && row.Y <= _layersList.MaxY) Row(row, layer, depth, order);
            y += rowHeight;
        }
        _ui.Context.PopAxisAlignedClip();
    }

    private void Row(Rect row, ImageLayer layer, int depth, IReadOnlyList<Guid> order)
    {
        bool chosen = _canvas.Chosen.Contains(layer.Id);
        _ui.Fill(row, chosen ? Ui.Selected : Ui.Panel);
        _ui.Rule(new Point(row.X, row.MaxY - 0.5), new Point(row.MaxX, row.MaxY - 0.5), Ui.Line);

        Rect visible = Clip(row, _layersList);
        Guid id = layer.Id;
        _ui.Area(visible, () =>
        {
            bool control = IsKeyDown(VK_CONTROL), shift = IsKeyDown(VK_SHIFT);
            _canvas.ClickLayer(id, control, shift, order);
        }, doubleClick: () => StartRename(id, row));

        double x = row.X;
        var eye = new Rect(x, row.Y, _ui.P(28), row.Height);
        _ui.Button(Clip(eye, _layersList), () => _canvas.ToggleVisibility(id), Localizer.Text(TextKey.TooltipVisibility),
                   face: _ => Icons.Eye(_ui, eye, layer.IsVisible ? Ui.Ink : Ui.Dim, layer.IsVisible));
        x = eye.MaxX + _ui.P(depth * 14);

        if (layer.IsGroup)
        {
            bool open = !_collapsed.Contains(id);
            var disclosure = new Rect(x, row.Y, _ui.P(16), row.Height);
            _ui.Button(Clip(disclosure, _layersList), () =>
            {
                if (!_collapsed.Remove(id)) _collapsed.Add(id);
            }, null, face: _ => Icons.Disclosure(_ui, disclosure, Ui.Dim, open));
            x = disclosure.MaxX;
        }
        else if (layer.MaskSourceId is not null)
        {
            Icons.Chain(_ui, new Rect(x, row.Y, _ui.P(14), row.Height), Ui.Dim);
            x += _ui.P(14);
        }

        var thumb = new Rect(x + _ui.P(2), row.Y + _ui.P(4), _ui.P(26), row.Height - _ui.P(8));
        Thumbnail(thumb, layer);
        x = thumb.MaxX + _ui.P(4);

        if (layer.Mask is LayerMask mask)
        {
            var maskThumb = new Rect(x, thumb.Y, thumb.Width, thumb.Height);
            Picture(maskThumb, mask.Coverage, checker: false);
            if (!mask.IsEnabled) _ui.Rule(new Point(maskThumb.X, maskThumb.MaxY), new Point(maskThumb.MaxX, maskThumb.Y), Ui.Accent, _ui.P(1.5f));
            x = maskThumb.MaxX + _ui.P(4);
        }

        // A layer's name is the user's own, whatever language it was made in.
        _ui.Text(layer.Name, new Rect(x + _ui.P(4), row.Y, row.MaxX - x - _ui.P(8), row.Height),
                 layer.IsVisible ? Ui.Ink : Ui.Dim, user: true);
    }

    private void Thumbnail(Rect area, ImageLayer layer)
    {
        if (layer.IsGroup)
        {
            Icons.Folder(_ui, area, Ui.Dim);
            return;
        }
        if (layer.Adjustment is not null)
        {
            Icons.Adjustment(_ui, area, Ui.Dim);
            return;
        }
        if (layer.Image is PixelBuffer image) Picture(area, image, checker: true);
        else
        {
            _ui.Fill(area, new Color4(1, 1, 1, 1));
            _ui.Frame(area, Ui.Line);
        }
    }

    /// <summary>A small picture of some pixels, fitted to <paramref name="area"/>, cached per buffer.</summary>
    private void Picture(Rect area, PixelBuffer pixels, bool checker)
    {
        _thumbnailsUsed.Add(pixels);
        if (!_thumbnails.TryGetValue(pixels, out ID2D1Bitmap1? bitmap))
        {
            // Halved until it is no larger than it will be drawn, so the upload is small and the
            // reduction is the same box filter the canvas uses.
            PixelBuffer reduced = pixels.Retain();
            while (Math.Max(reduced.Width, reduced.Height) > 64)
            {
                PixelBuffer next = DownsamplePyramid.Halve(reduced);
                reduced.Release();
                reduced = next;
            }
            bitmap = ImageLoader.Upload(_ui.Context, reduced, Vortice.DXGI.Format.R8G8B8A8_UNorm);
            reduced.Release();
            _thumbnails[pixels] = bitmap;
        }

        double scale = Math.Min(area.Width / pixels.Width, area.Height / pixels.Height);
        double w = Math.Max(1, pixels.Width * scale), h = Math.Max(1, pixels.Height * scale);
        var fitted = new Rect(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);

        if (checker)
        {
            _ui.Fill(fitted, new Color4(0.8f, 0.8f, 0.8f, 1));
            _ui.Fill(new Rect(fitted.X, fitted.Y, fitted.Width / 2, fitted.Height / 2), new Color4(0.6f, 0.6f, 0.6f, 1));
            _ui.Fill(new Rect(fitted.MidX, fitted.MidY, fitted.Width / 2, fitted.Height / 2), new Color4(0.6f, 0.6f, 0.6f, 1));
        }
        _ui.Context.DrawBitmap(bitmap, Ui.Raw(fitted), 1f, BitmapInterpolationMode.Linear, null);
        _ui.Frame(fitted, Ui.Line);
    }

    private void BottomButtons(Rect bar)
    {
        double size = _ui.P(30);
        double x = bar.MaxX - size * 5 - _ui.P(8);

        void Button(int command, TextKey label, Action<Rect> face)
        {
            var area = new Rect(x, bar.Y + (bar.Height - size) / 2, size, size);
            _ui.Button(area, () => _runCommand(command), Localizer.Plain(Localizer.Text(label)), face: face);
            x += size;
        }

        var adjustment = new Rect(x, bar.Y + (bar.Height - size) / 2, size, size);
        _ui.Button(adjustment, PickAdjustmentLayer, Localizer.Text(TextKey.TooltipNewAdjustmentLayer),
                   enabled: _canvas.CanAddAdjustmentLayer, face: face => Icons.Adjustment(_ui, face, Ui.Ink));
        x += size;

        Button(CommandIds.AddLayerMask, TextKey.CommandAddLayerMask, face => Icons.Mask(_ui, face, Ui.Ink));
        Button(CommandIds.GroupLayers, TextKey.CommandGroupLayers, face => Icons.Folder(_ui, face, Ui.Ink));
        Button(CommandIds.NewLayer, TextKey.CommandNewLayer, face => Icons.Plus(_ui, face, Ui.Ink));
        Button(CommandIds.DeleteLayer, TextKey.CommandDeleteLayer, face => Icons.Bin(_ui, face, Ui.Ink));
    }

    /// <summary>The layers as the panel lists them: topmost first, folders' contents under them, indented.</summary>
    public List<(ImageLayer Layer, int Depth)> Rows(CanvasDocument document)
    {
        ILookup<Guid?, ImageLayer> children = document.Layers.ToLookup(layer => layer.ParentId);
        var rows = new List<(ImageLayer, int)>();

        void Visit(Guid? parent, int depth)
        {
            if (depth > ProjectLimits.MaximumNesting) return;
            foreach (ImageLayer layer in children[parent].Reverse())
            {
                rows.Add((layer, depth));
                if (layer.IsGroup && !_collapsed.Contains(layer.Id)) Visit(layer.Id, depth + 1);
            }
        }

        Visit(null, 0);
        return rows;
    }

    /// <summary>The wheel over the Layers panel scrolls its rows.</summary>
    public bool Scroll(Point at, double notches)
    {
        if (!_layersList.Contains(at)) return false;
        _scroll -= notches * _ui.P(RowHeight) * 2;
        return true;
    }

    private void PickBlendMode()
    {
        LayerBlendMode current = _canvas.ActiveLayer?.BlendMode ?? LayerBlendMode.Normal;
        LayerBlendMode[] modes = Enum.GetValues<LayerBlendMode>();
        int picked = Popup(modes.Select(mode => (Localizer.Text(BlendName(mode)), mode == current)).ToArray(),
                           separatorsAfter: [0, 2, 5, 6, 8]);
        if (picked >= 0) _canvas.SetBlendMode(modes[picked]);
    }

    private void PickAdjustmentLayer()
    {
        int picked = Popup([.. AdjustmentKinds.Select(kind => (Localizer.Text(CanvasView.FilterTitle(kind)) + "…", false))]);
        if (picked >= 0) _canvas.StartFilter(AdjustmentKinds[picked], asLayer: true);
    }

    /// <summary>A pop-up menu at the pointer. Returns the index chosen, or −1.</summary>
    private int Popup((string Text, bool Checked)[] items, int[]? separatorsAfter = null)
    {
        nint menu = CreatePopupMenu();
        try
        {
            for (int i = 0; i < items.Length; i++)
            {
                AppendMenuW(menu, MF_STRING | (items[i].Checked ? MF_CHECKED : 0), (nuint)(i + 1), items[i].Text);
                if (separatorsAfter?.Contains(i) == true) AppendMenuW(menu, MF_SEPARATOR, 0, null);
            }

            GetCursorPos(out POINTSTRUCT at);
            int chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY, at.X, at.Y, _window, 0);
            return chosen - 1;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    // MARK: Renaming

    /// <summary>
    /// A native edit box over the layer's name. Native, so Korean typing goes through the IME exactly
    /// as it does anywhere else in Windows — the one place docs/windows-port.md §5.1 said a
    /// self-drawn interface would need a real text field.
    /// </summary>
    private void StartRename(Guid id, Rect row)
    {
        FinishRename(commit: true);
        if (_canvas.Document?.Layer(id) is not ImageLayer layer) return;

        _renameFont = CreateFontW(-(int)_ui.P(13), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0,
                                  Localizer.Current == Language.Korean ? "Malgun Gothic" : "Segoe UI");
        double left = row.X + _ui.P(64);
        _renameBox = CreateWindowExW(0, "EDIT", layer.Name, WS_CHILD | WS_VISIBLE | WS_BORDER | ES_AUTOHSCROLL,
                                     (int)left, (int)(row.Y + _ui.P(6)), (int)(row.MaxX - left - _ui.P(8)),
                                     (int)(row.Height - _ui.P(12)), _window, 0, GetModuleHandleW(0), 0);
        SendMessageW(_renameBox, WM_SETFONT, (nuint)_renameFont, 1);
        SendMessageW(_renameBox, EM_SETSEL, 0, -1);
        SetFocus(_renameBox);
        _renaming = id;
    }

    /// <summary>Enter and Escape in the rename box. True when the message was the box's.</summary>
    public bool RenameKey(in MSG message)
    {
        if (_renameBox == 0 || message.hwnd != _renameBox || message.message != WM_KEYDOWN) return false;
        if ((int)message.wParam == VK_RETURN) FinishRename(commit: true);
        else if ((int)message.wParam == VK_ESCAPE) FinishRename(commit: false);
        else return false;
        return true;
    }

    public void FinishRename(bool commit)
    {
        if (_renameBox == 0) return;

        if (commit)
        {
            char* text = stackalloc char[256];
            int length = GetWindowTextW(_renameBox, text, 256);
            _canvas.Rename(_renaming, new string(text, 0, Math.Max(0, length)));
        }

        DestroyWindow(_renameBox);
        DeleteObject(_renameFont);
        _renameBox = 0;
        _renameFont = 0;
        SetFocus(_window);
        InvalidateRect(_window, 0, false);
    }

    // MARK: The status bar and the welcome

    private void Status(Rect bar)
    {
        _ui.Fill(bar, Ui.Panel);
        _ui.Block(bar);
        _ui.Rule(new Point(0, bar.Y + 0.5), new Point(bar.MaxX, bar.Y + 0.5), Ui.Line);

        double pad = _ui.P(12);
        if (_canvas.Document is not CanvasDocument document)
        {
            _ui.Text(Localizer.Text(TextKey.StatusNoDocument), new Rect(pad, bar.Y, _ui.P(300), bar.Height), Ui.Dim, Ui.TextSize.Small);
            return;
        }

        string zoom = Localizer.Format(TextKey.UnitPercent, Math.Round(_canvas.Viewport.Zoom * 100, _canvas.Viewport.Zoom < 0.1 ? 1 : 0));
        string size = Localizer.Format(TextKey.StatusDocumentSize, document.Width, document.Height);
        _ui.Text(zoom, new Rect(pad, bar.Y, _ui.P(70), bar.Height), Ui.Ink, Ui.TextSize.Small);
        _ui.Text(size, new Rect(pad + _ui.P(80), bar.Y, _ui.P(200), bar.Height), Ui.Dim, Ui.TextSize.Small);
        _ui.Text(_canvas.Title, new Rect(pad + _ui.P(290), bar.Y, _ui.P(400), bar.Height), Ui.Dim, Ui.TextSize.Small,
                 user: _canvas.FilePath is not null);
    }

    private void Welcome(Rect area)
    {
        string title = Localizer.Text(TextKey.WelcomeTitle);
        var line = new Rect(area.X, area.MidY - _ui.P(40), area.Width, _ui.P(30));
        _ui.Text(title, line, Ui.Dim, centred: true);

        string open = Localizer.Plain(Localizer.Text(TextKey.CommandOpen)) + "…";
        float width = _ui.Measure(open) + _ui.P(32);
        var button = new Rect(area.MidX - width / 2, line.MaxY + _ui.P(8), width, _ui.P(30));
        _ui.Fill(button, Ui.Raised);
        _ui.Button(button, _open, null, label: open);
    }

    // MARK: Helpers

    public static TextKey BlendName(LayerBlendMode mode) => mode switch
    {
        LayerBlendMode.Multiply => TextKey.BlendMultiply,
        LayerBlendMode.Screen => TextKey.BlendScreen,
        LayerBlendMode.Overlay => TextKey.BlendOverlay,
        LayerBlendMode.Darken => TextKey.BlendDarken,
        LayerBlendMode.Lighten => TextKey.BlendLighten,
        LayerBlendMode.Difference => TextKey.BlendDifference,
        LayerBlendMode.ColorDodge => TextKey.BlendColorDodge,
        LayerBlendMode.ColorBurn => TextKey.BlendColorBurn,
        LayerBlendMode.Hue => TextKey.BlendHue,
        LayerBlendMode.Saturation => TextKey.BlendSaturation,
        LayerBlendMode.Color => TextKey.BlendColor,
        LayerBlendMode.Luminosity => TextKey.BlendLuminosity,
        _ => TextKey.BlendNormal,
    };

    private static string Percent(double fraction) =>
        Localizer.Format(TextKey.UnitPercent, Math.Round(fraction * 100));

    private static string Pixels(double value) => Localizer.Format(TextKey.UnitPixels, Math.Round(value, 1));

    private static string Number(double value) =>
        Math.Round(value, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static Rect Clip(Rect area, Rect within) => area.Intersect(within);

    private static Rect Shrink(Rect area, double factor) =>
        new(area.X + area.Width * (1 - factor) / 2, area.Y + area.Height * (1 - factor) / 2, area.Width * factor, area.Height * factor);

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FinishRename(commit: false);
        foreach (ID2D1Bitmap1 bitmap in _thumbnails.Values) bitmap.Dispose();
        _thumbnails.Clear();
        _ui.Dispose();
    }
}
