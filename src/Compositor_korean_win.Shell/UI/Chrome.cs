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
    private const double TopBar = 42, TabStrip = 30, Rail = 48, RightPanel = 264, StatusBar = 26, RowHeight = 34;

    private static readonly (CanvasTool Tool, char Key)[] Tools =
    [
        (CanvasTool.Move, 'V'), (CanvasTool.Crop, 'C'), (CanvasTool.RectangleMarquee, 'M'), (CanvasTool.EllipseMarquee, 'M'),
        (CanvasTool.Lasso, 'L'), (CanvasTool.PolygonLasso, 'L'), (CanvasTool.MagicWand, 'W'),
        (CanvasTool.Brush, 'B'), (CanvasTool.Eraser, 'E'), (CanvasTool.CloneStamp, 'S'),
        (CanvasTool.Heal, 'J'), (CanvasTool.Blur, 'R'), (CanvasTool.Gradient, 'G'), (CanvasTool.Shape, 'U'),
        (CanvasTool.Eyedropper, 'I'), (CanvasTool.Hand, 'H'), (CanvasTool.Zoom, 'Z'),
    ];

    private static readonly FilterCommand[] AdjustmentKinds =
    [
        FilterCommand.Levels, FilterCommand.Curves, FilterCommand.HueSaturation,
        FilterCommand.Exposure, FilterCommand.GradientMap, FilterCommand.Grain,
    ];

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

    private readonly List<Sheet> _sheets = [];

    /// <summary>Whether the options bar's width and height keep the layer's proportions.</summary>
    private bool _lockRatio = true;
    private readonly List<Rect> _sheetAreas = [];
    private Rect _canvasArea;

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
        new(Rail * scale, (TopBar + TabStrip) * scale,
            Math.Max(1, width - (Rail + RightPanel) * scale), Math.Max(1, height - (TopBar + TabStrip + StatusBar) * scale));

    /// <summary>What a tab's close button does; the program points it at the File menu's Close, which asks first.</summary>
    public Action<int>? CloseTab { get; set; }

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
        _canvasArea = canvas;

        OptionsBar(new Rect(0, 0, width, TopBar * s));
        ToolRail(new Rect(0, TopBar * s, Rail * s, height - (TopBar + StatusBar) * s));
        LayersPanel(new Rect(width - RightPanel * s, TopBar * s, RightPanel * s, height - (TopBar + StatusBar) * s));
        Status(new Rect(0, height - StatusBar * s, width, StatusBar * s));
        Tabs(new Rect(canvas.X, TopBar * s, canvas.Width, TabStrip * s));
        if (!_canvas.HasDocument) Welcome(canvas);
        Sheets(canvas, width, height);

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

        // Floating pixels wait for Enter or Escape; the bar says so.
        if (_canvas.IsFloating)
        {
            string floating = Localizer.Text(TextKey.NoteFloating);
            float floatingWidth = _ui.Measure(floating) + _ui.P(18);
            _ui.Text(floating, new Rect(x, bar.Y, floatingWidth, bar.Height), Ui.Accent);
            x += floatingWidth;
        }

        // What the tools are working on, when it is the mask rather than the layer: upstream's
        // "Mask" beside the brush settings.
        if (_canvas.EditingMask && (tool.Paints() || tool == CanvasTool.Gradient || tool == CanvasTool.Move && _canvas.TransformsMask))
        {
            string mask = Localizer.Text(TextKey.LabelEditingMask);
            float maskWidth = _ui.Measure(mask) + _ui.P(18);
            _ui.Text(mask, new Rect(x, bar.Y, maskWidth, bar.Height), Ui.Accent);
            x += maskWidth;
        }

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

                if (tool == CanvasTool.Blur)
                {
                    BlurToolMode blurMode = _canvas.BlurMode;
                    Segment(ref x, y, h, Localizer.Text(TextKey.LabelType),
                    [
                        (Localizer.Text(TextKey.BlurModeBlur), blurMode == BlurToolMode.Blur,
                         () => _canvas.BlurMode = BlurToolMode.Blur),
                        (Localizer.Text(TextKey.ToolSmudge), blurMode == BlurToolMode.Smudge,
                         () => _canvas.BlurMode = BlurToolMode.Smudge),
                        (Localizer.Text(TextKey.ToolLiquify), blurMode == BlurToolMode.Liquify,
                         () => _canvas.BlurMode = BlurToolMode.Liquify),
                    ]);
                }

                double sizeFraction = Math.Log(Math.Max(1, brush.Diameter)) / Math.Log(2000);
                _ui.Slider(Next(210), Localizer.Text(TextKey.LabelSize), sizeFraction, Pixels(brush.Diameter),
                           f => _canvas.Brush = _canvas.Brush with { Diameter = Math.Round(Math.Clamp(Math.Exp(f * Math.Log(2000)), 1, 2000)) });

                if (tool == CanvasTool.Blur && _canvas.BlurMode != BlurToolMode.Blur)
                {
                    // Upstream's: how soft the edge of the push is, and how far it carries.
                    _ui.Slider(Next(170), Localizer.Text(TextKey.LabelHardness), brush.Hardness, Percent(brush.Hardness),
                               f => _canvas.Brush = _canvas.Brush with { Hardness = Math.Round(f, 2) });
                    _ui.Slider(Next(180), Localizer.Text(TextKey.LabelStrength), brush.Opacity, Percent(brush.Opacity),
                               f => _canvas.Brush = _canvas.Brush with { Opacity = Math.Max(0.01, Math.Round(f, 2)) });
                    if (_canvas.EditingMask)
                        _ui.Text(Localizer.Text(TextKey.NoteMaskTool), new Rect(x, bar.Y, _ui.P(320), bar.Height), Ui.Dim);
                }
                else if (tool == CanvasTool.Blur)
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
                {
                    _ui.Check(Next(90), Localizer.Text(TextKey.LabelAligned), _canvas.CloneAligned,
                              () => _canvas.CloneAligned = !_canvas.CloneAligned);
                    _ui.Check(Next(170), Localizer.Text(TextKey.LabelSampleAllLayers), _canvas.CloneSampleAll,
                              () => _canvas.CloneSampleAll = !_canvas.CloneSampleAll);
                }

                if (_canvas.EditingMask && tool is CanvasTool.CloneStamp or CanvasTool.Heal)
                    _ui.Text(Localizer.Text(TextKey.NoteMaskTool), new Rect(x, bar.Y, _ui.P(320), bar.Height), Ui.Dim);

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

            case CanvasTool.Eyedropper:
                _ui.Check(Next(110), Localizer.Text(TextKey.LabelSampleRing), _canvas.ShowSampleRing,
                          () => _canvas.ShowSampleRing = !_canvas.ShowSampleRing);
                break;

            case CanvasTool.Crop:
            {
                // Upstream's CropControls: the proportion, the frame's size, Cancel and Apply.
                CropRatio ratio = _canvas.CropRatio;
                Segment(ref x, y, h, Localizer.Text(TextKey.LabelAspectRatio),
                [
                    (Localizer.Text(TextKey.CropRatioFree), ratio == CropRatio.Free, () => _canvas.CropRatio = CropRatio.Free),
                    (Localizer.Text(TextKey.CropRatioOriginal), ratio == CropRatio.Original, () => _canvas.CropRatio = CropRatio.Original),
                    ("1:1", ratio == CropRatio.Square, () => _canvas.CropRatio = CropRatio.Square),
                    ("4:3", ratio == CropRatio.FourByThree, () => _canvas.CropRatio = CropRatio.FourByThree),
                    ("16:9", ratio == CropRatio.SixteenByNine, () => _canvas.CropRatio = CropRatio.SixteenByNine),
                ]);

                if (_canvas.CropFrame is Rect frame)
                {
                    string size = Localizer.Format(TextKey.NoteFrameSize, (int)frame.Width, (int)frame.Height);
                    float sizeWidth = _ui.Measure(size) + _ui.P(18);
                    _ui.Text(size, new Rect(x, bar.Y, sizeWidth, bar.Height), Ui.Ink);
                    x += sizeWidth;
                }

                string cancel = Localizer.Text(TextKey.DialogCancel), apply = Localizer.Text(TextKey.ButtonApplyCrop);
                Rect cancelArea = Next(_ui.Measure(cancel) / _ui.Scale + 24);
                _ui.Fill(cancelArea, Ui.Raised);
                _ui.Button(cancelArea, _canvas.CancelCrop, null, label: cancel);
                Rect applyArea = Next(_ui.Measure(apply) / _ui.Scale + 24);
                _ui.Fill(applyArea, Ui.Raised);
                _ui.Button(applyArea, _canvas.ApplyCrop, null, enabled: _canvas.CanApplyCrop, label: apply);
                break;
            }

            case CanvasTool.RectangleMarquee or CanvasTool.EllipseMarquee or CanvasTool.Lasso or CanvasTool.PolygonLasso:
                SelectionControls(ref x, y, h);
                _ui.Slider(Next(230), Localizer.Text(TextKey.LabelSelectionStep), (_canvas.SelectionStep - 1) / 99.0,
                           Pixels(_canvas.SelectionStep), f => _canvas.SelectionStep = (int)Math.Round(1 + f * 99));
                break;

            case CanvasTool.MagicWand:
            {
                SelectionControls(ref x, y, h);
                WandSettings wand = _canvas.Wand;
                _ui.Slider(Next(200), Localizer.Text(TextKey.LabelTolerance), wand.Tolerance / 255.0,
                           wand.Tolerance.ToString(System.Globalization.CultureInfo.InvariantCulture),
                           f => _canvas.Wand = _canvas.Wand with { Tolerance = (int)Math.Round(f * 255) });
                _ui.Check(Next(90), Localizer.Text(TextKey.LabelContiguous), wand.Contiguous,
                          () => _canvas.Wand = _canvas.Wand with { Contiguous = !_canvas.Wand.Contiguous });
                Segment(ref x, y, h, Localizer.Text(TextKey.LabelSampleSize),
                [
                    (Localizer.Text(TextKey.SamplePoint), wand.SampleRadius == 0,
                     () => _canvas.Wand = _canvas.Wand with { SampleRadius = 0 }),
                    (Localizer.Text(TextKey.Sample3By3), wand.SampleRadius == 1,
                     () => _canvas.Wand = _canvas.Wand with { SampleRadius = 1 }),
                    (Localizer.Text(TextKey.Sample5By5), wand.SampleRadius == 2,
                     () => _canvas.Wand = _canvas.Wand with { SampleRadius = 2 }),
                ]);
                _ui.Check(Next(110), Localizer.Text(TextKey.LabelAllLayers), wand.SampleAllLayers,
                          () => _canvas.Wand = _canvas.Wand with { SampleAllLayers = !_canvas.Wand.SampleAllLayers });
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
                Segment(ref x, y, h, "",
                [
                    (Localizer.Text(TextKey.GradientForegroundBackground),
                     gradient.Style == GradientStyle.ForegroundToBackground,
                     () => _canvas.Gradient = _canvas.Gradient with { Style = GradientStyle.ForegroundToBackground }),
                    (Localizer.Text(TextKey.GradientForegroundTransparent),
                     gradient.Style == GradientStyle.ForegroundToTransparent,
                     () => _canvas.Gradient = _canvas.Gradient with { Style = GradientStyle.ForegroundToTransparent }),
                ]);
                _ui.Check(Next(90), Localizer.Text(TextKey.LabelReverse), gradient.Reversed,
                          () => _canvas.Gradient = _canvas.Gradient with { Reversed = !_canvas.Gradient.Reversed });
                _ui.Slider(Next(180), Localizer.Text(TextKey.LabelOpacity), gradient.Opacity, Percent(gradient.Opacity),
                           f => _canvas.Gradient = _canvas.Gradient with { Opacity = Math.Max(0.01, Math.Round(f, 2)) });
                if (_canvas.HasPendingGradient)
                {
                    string cancel = Localizer.Text(TextKey.DialogCancel), apply = Localizer.Text(TextKey.ButtonApply);
                    Rect cancelArea = Next(_ui.Measure(cancel) / _ui.Scale + 24);
                    _ui.Fill(cancelArea, Ui.Raised);
                    _ui.Button(cancelArea, _canvas.CancelGradient, null, label: cancel);
                    Rect applyArea = Next(_ui.Measure(apply) / _ui.Scale + 24);
                    _ui.Fill(applyArea, Ui.Raised);
                    _ui.Button(applyArea, _canvas.CommitGradient, null, label: apply);
                }
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

            case CanvasTool.Move when _canvas.ActiveLayer is { } layer && (!layer.IsGroup || _canvas.TransformsMask)
                                      && _canvas.TransformTarget is LayerTransform t:
            {
                // Upstream's TransformInspector: the chosen layer's placement — or an unlinked mask's,
                // when that is the target — typed. Each number typed is one history step, as a drag is.
                _ui.Check(Next(110), Localizer.Text(TextKey.LabelAutoSelect), _canvas.AutoSelect,
                          () => _canvas.AutoSelect = !_canvas.AutoSelect);
                Core.Size pixelSize = _canvas.TransformTargetPixels;

                void Box(string name, bool user, double value, int points, Action<double> set, string suffix = "")
                {
                    float nameWidth = _ui.Measure(name) + _ui.P(6);
                    Rect area = Next(points);
                    _ui.Text(name, new Rect(area.X, area.Y, nameWidth, area.Height), Ui.Dim, user: user);
                    float suffixWidth = suffix.Length > 0 ? _ui.Measure(suffix) + _ui.P(4) : 0;
                    _ui.Field(new Rect(area.X + nameWidth, area.Y, area.Width - nameWidth - suffixWidth, area.Height),
                              "transform " + name, value, 2, set);
                    if (suffix.Length > 0)
                        _ui.Text(suffix, new Rect(area.MaxX - suffixWidth + _ui.P(4), area.Y, suffixWidth, area.Height), Ui.Dim, user: true);
                }

                void Resize(double value, bool width) => _canvas.ChangeTransform(current =>
                {
                    if (value < 1) return current;
                    Core.Size size = current.Size;
                    return current with
                    {
                        Size = width
                            ? new Core.Size(value, _lockRatio ? size.Height * value / size.Width : size.Height)
                            : new Core.Size(_lockRatio ? size.Width * value / size.Height : size.Width, value),
                    };
                });

                Box("X", true, t.Origin.X, 92, value => _canvas.ChangeTransform(c => c with { Origin = new Point(value, c.Origin.Y) }));
                Box("Y", true, t.Origin.Y, 92, value => _canvas.ChangeTransform(c => c with { Origin = new Point(c.Origin.X, value) }));
                Box(Localizer.Text(TextKey.LabelWidth), false, t.Size.Width, 108, value => Resize(value, width: true));
                Box(Localizer.Text(TextKey.LabelHeight), false, t.Size.Height, 108, value => Resize(value, width: false));

                Rect chain = Next(20);
                _ui.Button(chain, () => _lockRatio = !_lockRatio, Localizer.Text(TextKey.TooltipLockRatio), active: _lockRatio,
                           face: face => Icons.Chain(_ui, face, _lockRatio ? Ui.Ink : Ui.Dim));

                Box(Localizer.Text(TextKey.LabelScale), false, t.ScalePercent(pixelSize), 112,
                    value => _canvas.ChangeTransform(c => value > 0 ? c.ScaledToPercent(value, pixelSize) : c), "%");
                Box(Localizer.Text(TextKey.LabelAngle), false, t.Rotation, 100,
                    value => _canvas.ChangeTransform(c => c with { Rotation = value % 360 }), "°");

                foreach ((TextKey label, bool horizontal) in new[] { (TextKey.ButtonFlipHorizontal, true), (TextKey.ButtonFlipVertical, false) })
                {
                    string text = Localizer.Text(label);
                    Rect button = Next(_ui.Measure(text) / _ui.Scale + 20);
                    _ui.Fill(button, Ui.Raised);
                    _ui.Button(button, () => _canvas.ChangeTransform(c =>
                        horizontal ? c with { FlipX = !c.FlipX } : c with { FlipY = !c.FlipY }), null, label: text);
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

    private void SelectionControls(ref double x, double y, double h)
    {
        Segment(ref x, y, h, Localizer.Text(TextKey.LabelSelectionMode),
        [
            (Localizer.Text(TextKey.SelectionReplace), _canvas.SelectionMode == SelectionModeChoice.Replace,
             () => _canvas.SelectionMode = SelectionModeChoice.Replace),
            (Localizer.Text(TextKey.SelectionAdd), _canvas.SelectionMode == SelectionModeChoice.Add,
             () => _canvas.SelectionMode = SelectionModeChoice.Add),
            (Localizer.Text(TextKey.SelectionSubtract), _canvas.SelectionMode == SelectionModeChoice.Subtract,
             () => _canvas.SelectionMode = SelectionModeChoice.Subtract),
        ]);
        _ui.Check(new Rect(x, y, _ui.P(110), h), Localizer.Text(TextKey.LabelAntiAlias), _canvas.SelectionAntialiased,
                  () => _canvas.SelectionAntialiased = !_canvas.SelectionAntialiased);
        x += _ui.P(126);
    }

    // MARK: Sheets

    /// <summary>Whether a sheet is open, which holds the rest of the window still.</summary>
    public bool HasSheet
    {
        get
        {
            SyncFilterSheet();
            return _sheets.Count > 0;
        }
    }

    /// <summary>
    /// Whether a point is over the panels or a sheet rather than the picture — where the wheel
    /// scrolls a list instead of zooming.
    /// </summary>
    public bool OverPanels(Point at) =>
        !Contains(_canvasArea, at) || (_sheets.Count > 0 && _sheetAreas.Any(area => Contains(area, at)));

    private static bool Contains(Rect area, Point at) =>
        at.X >= area.X && at.Y >= area.Y && at.X < area.MaxX && at.Y < area.MaxY;

    /// <summary>Opens a sheet over whatever is open already.</summary>
    public void Open(Sheet sheet)
    {
        sheet.IsClosed = false;
        _sheets.Add(sheet);
    }

    public void Close(Sheet sheet)
    {
        _sheets.Remove(sheet);
        sheet.IsClosed = true;
    }

    /// <summary>
    /// The open adjustment or filter has a sheet exactly while the canvas has an edit open, however
    /// the edit was opened — a menu, a double click, the self-test.
    /// </summary>
    private void SyncFilterSheet()
    {
        FilterSheet? shown = _sheets.OfType<FilterSheet>().FirstOrDefault();
        if (_canvas.IsFiltering && shown is null) _sheets.Insert(0, new FilterSheet(_canvas, this));
        else if (!_canvas.IsFiltering && shown is not null) Close(shown);
    }

    private void Accept(Sheet sheet)
    {
        _ui.CommitFocus();
        if (!sheet.CanAccept) return;
        sheet.Accept();
        Close(sheet);
    }

    private void Cancel(Sheet sheet)
    {
        _ui.DropFocus();
        sheet.Cancel();
        Close(sheet);
    }

    /// <summary>
    /// A key while a sheet is open: Enter is OK and Escape is Cancel, a focused number box takes the
    /// rest, and nothing else in the window gets a key until the sheet closes — bar zooming, since
    /// looking closer at a preview is half of what a preview is for.
    /// </summary>
    public bool SheetKey(int key, bool control, bool shift, bool alt = false)
    {
        if (!HasSheet) return false;
        Sheet top = _sheets[^1];

        switch (key)
        {
            // Upstream's Option-P, which is Alt+P on Windows as it is in Photoshop's dialogs.
            case VK_P when alt && !control && top.Preview is bool previewing:
                top.Preview = !previewing;
                return true;
            case VK_RETURN:
                Accept(top);
                return true;
            case VK_ESCAPE when _ui.Typing:
                _ui.DropFocus();
                return true;
            case VK_ESCAPE:
                Cancel(top);
                return true;
        }

        if (_ui.Key(key, shift)) return true;
        return !(control && key is VK_0 or VK_1 or VK_OEM_PLUS or VK_OEM_MINUS or VK_ADD or VK_SUBTRACT);
    }

    /// <summary>
    /// A key while a number box outside any sheet has the keyboard — the status bar's zoom, the
    /// Move tool's inspector. It takes every key until Enter, Escape or a click lets it go, so a
    /// digit typed there is a digit and not an opacity shortcut.
    /// </summary>
    public bool FieldKey(int key, bool shift) => !HasSheet && _ui.Key(key, shift);

    /// <summary>A character typed into whichever number box has the keyboard, in a sheet or not.</summary>
    public bool SheetChar(char character) => _ui.Char(character);

    private void Sheets(Rect canvas, int width, int height)
    {
        SyncFilterSheet();
        if (_sheets.Count == 0) return;

        // Nothing round the canvas takes a click while a sheet is open, and the canvas itself only
        // for a sheet that samples from it.
        _ui.Block(new Rect(0, 0, width, canvas.Y));
        _ui.Block(new Rect(0, canvas.Y, canvas.X, height - canvas.Y));
        _ui.Block(new Rect(canvas.MaxX, canvas.Y, width - canvas.MaxX, height - canvas.Y));
        _ui.Block(new Rect(canvas.X, canvas.MaxY, canvas.Width, height - canvas.MaxY));

        Sheet top = _sheets[^1];
        if (top.UsesCanvas)
        {
            // The press, the drag and the release, in document points, for an eyedropper or a
            // targeted adjustment.
            Point? pressed = null;
            _ui.Drag(canvas, (point, finished) =>
            {
                bool control = IsKeyDown(VK_CONTROL);
                if (_canvas.DocumentAt(point) is Point document)
                {
                    if (pressed is not Point from)
                    {
                        pressed = point;
                        top.CanvasPress(document, control);
                    }
                    else
                    {
                        top.CanvasDrag(document, (point.X - from.X) / _ui.Scale, control);
                    }
                }
                if (finished)
                {
                    pressed = null;
                    top.CanvasRelease();
                }
            });
        }
        else
        {
            _ui.Block(canvas);
        }

        _sheetAreas.Clear();
        for (int i = 0; i < _sheets.Count; i++)
        {
            Rect area = DrawSheet(_sheets[i], canvas, i);
            _sheetAreas.Add(area);
            // Only the top sheet answers; the ones under it wait for it to close.
            if (i < _sheets.Count - 1) _ui.Block(area);
        }
    }

    private Rect DrawSheet(Sheet sheet, Rect canvas, int depth)
    {
        double s = _ui.Scale;
        double width = sheet.Width * s, pad = 18 * s, title = 22 * s, foot = 30 * s;
        double inner = width - pad * 2;

        var measure = new SheetLayout(_ui, new Rect(0, 0, inner, 0), measuring: true, labelWidth: 0);
        sheet.Content(measure);
        double body = measure.Height;
        double height = pad + title + 12 * s + body + (body > 0 ? 22 * s : 0) + foot + pad;

        // Beside the Layers panel, over the canvas's corner, where it covers least of the picture;
        // centred when there is no picture.
        double x, y;
        if (_canvas.HasDocument)
        {
            x = canvas.MaxX - width - (16 + depth * 24) * s;
            y = canvas.Y + (16 + depth * 24) * s;
        }
        else
        {
            x = canvas.X + (canvas.Width - width) / 2 + depth * 24 * s;
            y = canvas.Y + Math.Max(16 * s, (canvas.Height - height) / 2) + depth * 24 * s;
        }
        var area = new Rect(Math.Max(0, x), y, width, height);

        _ui.Sheet(area);
        _ui.Text(sheet.Title, new Rect(area.X + pad, area.Y + pad, inner, title), Ui.Ink, Ui.TextSize.Title);

        var layout = new SheetLayout(_ui, new Rect(area.X + pad, area.Y + pad + title + 12 * s, inner, body),
                                     measuring: false, labelWidth: measure.LabelWidth);
        sheet.Content(layout);

        double footY = area.MaxY - pad - foot;
        _ui.Rule(new Point(area.X, footY - 11 * s), new Point(area.MaxX, footY - 11 * s), Ui.Line);

        double left = area.X + pad;
        if (sheet.Preview is bool preview)
        {
            string label = Localizer.Text(TextKey.LabelPreview);
            var check = new Rect(left, footY, _ui.Measure(label) + 26 * s, foot);
            _ui.Check(check, label, preview, () => sheet.Preview = !preview);
            left = check.MaxX + 14 * s;
        }
        if (sheet.Reset is Action reset)
        {
            string label = Localizer.Text(TextKey.DialogReset);
            var button = new Rect(left, footY, _ui.Measure(label) + 24 * s, foot);
            _ui.Fill(button, Ui.Raised);
            _ui.Button(button, reset, null, label: label);
        }

        string ok = Localizer.Text(sheet.AcceptLabel), cancel = Localizer.Text(TextKey.DialogCancel);
        double okWidth = Math.Max(_ui.Measure(ok) + 28 * s, 76 * s), cancelWidth = Math.Max(_ui.Measure(cancel) + 28 * s, 76 * s);
        var accept = new Rect(area.MaxX - pad - okWidth, footY, okWidth, foot);
        var dismiss = new Rect(accept.X - 8 * s - cancelWidth, footY, cancelWidth, foot);
        _ui.Fill(dismiss, Ui.Raised);
        _ui.Button(dismiss, () => Cancel(sheet), null, label: cancel);
        _ui.Button(accept, () => Accept(sheet), null, enabled: sheet.CanAccept, label: ok, primary: true);

        return area;
    }

    // MARK: Tabs

    private readonly List<(Rect Area, int Index)> _tabAreas = [];
    private Rect _newTabDrop;

    public DropDestination DropDestinationAt(Point point)
    {
        foreach ((Rect area, int index) in _tabAreas)
            if (area.Contains(point)) return new DropDestination(index, false);
        return new DropDestination(null, _newTabDrop.Contains(point));
    }

    /// <summary>
    /// A tab for each open document above the canvas — upstream's <c>ProjectTabs</c>: its name, a star
    /// while it has unsaved changes, and a button to close it.
    /// </summary>
    private void Tabs(Rect strip)
    {
        _ui.Fill(strip, Ui.Panel);
        _ui.Block(strip);
        _ui.Rule(new Point(strip.X, strip.MaxY - 0.5), new Point(strip.MaxX, strip.MaxY - 0.5), Ui.Line);

        IReadOnlyList<DocumentTab> tabs = _canvas.Tabs;
        _tabAreas.Clear();
        double x = strip.X;
        for (int i = 0; i < tabs.Count; i++)
        {
            DocumentTab tab = tabs[i];
            // The name is the user's; the star is not a word.
            string shown = CanvasView.TabTitle(tab) + (tab.History.IsModified ? " *" : "");
            double width = Math.Clamp(_ui.Measure(shown) + _ui.P(52), _ui.P(96), _ui.P(220));
            if (x + width > strip.MaxX) break;

            var area = new Rect(x, strip.Y, width, strip.Height - 1);
            _tabAreas.Add((area, i));
            bool active = i == _canvas.ActiveTab;
            int index = i;

            _ui.Fill(area, active ? Ui.Window : Ui.Panel);
            if (active) _ui.Fill(new Rect(area.X, area.Y, area.Width, _ui.P(2)), Ui.Accent);
            _ui.Area(area, () => _canvas.SwitchTo(index));
            _ui.Text(shown, new Rect(area.X + _ui.P(12), area.Y, area.Width - _ui.P(40), area.Height),
                     active ? Ui.Ink : Ui.Dim, user: true);

            var close = new Rect(area.MaxX - _ui.P(26), area.Y + _ui.P(5), _ui.P(20), area.Height - _ui.P(10));
            _ui.Button(close, () =>
            {
                if (CloseTab is Action<int> closeTab) closeTab(index);
                else
                {
                    _canvas.SwitchTo(index);
                    _canvas.Close();
                }
            }, Localizer.Text(TextKey.TooltipCloseTab), face: face => _ui.Text("×", face, Ui.Dim, centred: true));

            _ui.Rule(new Point(area.MaxX - 0.5, area.Y + _ui.P(6)), new Point(area.MaxX - 0.5, area.MaxY - _ui.P(6)), Ui.Line);
            x += width;
        }
        _newTabDrop = new Rect(x, strip.Y, Math.Max(0, strip.MaxX - x), strip.Height - 1);
        if (_rowDragFrom is not null && _rowDragMoved)
        {
            foreach ((Rect area, int index) in _tabAreas)
                if (index != _canvas.ActiveTab && area.Contains(_rowDragAt)) _ui.Frame(area, Ui.Accent, _ui.P(2));
            if (_newTabDrop.Contains(_rowDragAt)) _ui.Frame(_newTabDrop, Ui.Accent, _ui.P(2));
        }
    }

    // MARK: The tool rail

    private void ToolRail(Rect rail)
    {
        _ui.Fill(rail, Ui.Panel);
        _ui.Block(rail);
        _ui.Rule(new Point(rail.MaxX - 0.5, rail.Y), new Point(rail.MaxX - 0.5, rail.MaxY), Ui.Line);

        double size = _ui.P(36);
        // Upstream's rail scrolls when the window is too short for every tool rather than losing the
        // last ones under the status bar. Only whole buttons are drawn, so none is half clickable.
        _railArea = rail;
        double content = Tools.Length * (size + _ui.P(2)) + _ui.P(8 + 12 + 40 + 8);
        _railScroll = Math.Clamp(_railScroll, 0, Math.Max(0, content - rail.Height));
        double x = rail.X + (rail.Width - size) / 2, y = rail.Y + _ui.P(8) - _railScroll;
        bool Shows(Rect area) => area.Y >= rail.Y && area.MaxY <= rail.MaxY;

        foreach ((CanvasTool tool, char key) in Tools)
        {
            var area = new Rect(x, y, size, size);
            CanvasTool chosen = tool;
            string tip = $"{Localizer.Text(CanvasView.Name(tool))} ({key})";
            if (Shows(area))
                _ui.Button(area, () => _canvas.SetTool(chosen), tip, active: _canvas.Tool == tool,
                           face: face => Icons.Tool(_ui, chosen, face, Ui.Ink));
            y += size + _ui.P(2);
        }

        // Foreground over background, as in Photoshop, with swap and reset beside them.
        y += _ui.P(12);
        if (!Shows(new Rect(x, y - _ui.P(4), size, _ui.P(40)))) return;
        double swatch = _ui.P(22);
        var foreground = new Rect(x + _ui.P(2), y, swatch, swatch);
        var background = new Rect(x + _ui.P(12), y + _ui.P(12), swatch, swatch);

        // On a mask the swatches are its white and black, and a click swaps them rather than
        // opening the picker: a mask has no other colours to pick (upstream's rule).
        Swatch(background, _canvas.ShownBackground, TextKey.TooltipBackground, colour => _canvas.BackgroundColor = colour);
        Swatch(foreground, _canvas.ShownForeground, TextKey.TooltipForeground, colour => _canvas.ForegroundColor = colour);

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
            if (_canvas.EditingMask) _canvas.SwapColors();
            else PickColour(tooltip, colour, set);
        }, Localizer.Text(tooltip));
    }

    /// <summary>
    /// The program's own colour picker on a tool colour. The colour changes as it is picked, and
    /// Cancel puts the old one back.
    /// </summary>
    public void PickColour(TextKey title, Rgba current, Action<Rgba> set)
    {
        static Rgba Bytes((double Red, double Green, double Blue) c) =>
            new((byte)Math.Round(c.Red * 255), (byte)Math.Round(c.Green * 255), (byte)Math.Round(c.Blue * 255));

        Open(new ColourSheet(title, (current.R / 255.0, current.G / 255.0, current.B / 255.0),
                             colour => set(Bytes(colour)), _canvas.CompositeColour));
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
        _rowAreas.Clear();
        IReadOnlyList<Guid> order = [.. rows.Select(row => row.Layer.Id)];

        double rowHeight = _ui.P(RowHeight);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, rows.Count * rowHeight - _layersList.Height));

        _ui.Context.PushAxisAlignedClip(Ui.Raw(_layersList), AntialiasMode.Aliased);
        double y = _layersList.Y - _scroll;
        _bottomRow = null;
        foreach ((ImageLayer layer, int depth) in rows)
        {
            var row = new Rect(_layersList.X, y, _layersList.Width, rowHeight);
            if (row.MaxY >= _layersList.Y && row.Y <= _layersList.MaxY) Row(row, layer, depth, order);
            if (depth == 0) _bottomRow = (row, layer.Id);
            y += rowHeight;
        }
        _rowsEnd = y;

        // Below the last row is the bottom of the stack, as upstream's table drops there.
        if (_rowDragFrom is not null && _rowDragMoved && BottomDrop(_rowDragAt) is (Rect last, _))
            _ui.Rule(new Point(last.X, last.MaxY - 1), new Point(last.MaxX, last.MaxY - 1), Ui.Accent, _ui.P(2));
        _ui.Context.PopAxisAlignedClip();
    }

    private void Row(Rect row, ImageLayer layer, int depth, IReadOnlyList<Guid> order)
    {
        bool chosen = _canvas.Chosen.Contains(layer.Id);
        _ui.Fill(row, chosen ? Ui.Selected : Ui.Panel);
        _ui.Rule(new Point(row.X, row.MaxY - 0.5), new Point(row.MaxX, row.MaxY - 0.5), Ui.Line);

        Rect visible = Clip(row, _layersList);
        Guid id = layer.Id;
        void Choose()
        {
            bool control = IsKeyDown(VK_CONTROL), shift = IsKeyDown(VK_SHIFT);
            _canvas.ClickLayer(id, control, shift, order);
        }

        // The row picks the layer on release, or is carried to another place in the list.
        _ui.Drag(visible, (point, finished) => RowDrag(layer, point, finished, order), null,
                 doubleClick: () => StartRename(id, row));

        // Where a dragged row would land: a line between rows, or a frame round a folder.
        if (_rowDragFrom is not null && _rowDragMoved && visible.Contains(_rowDragAt))
        {
            LayerDrop drop = DropAt(row, layer, _rowDragAt);
            if (drop == LayerDrop.Into) _ui.Frame(visible, Ui.Accent, _ui.P(2));
            else
            {
                double edge = drop == LayerDrop.Above ? row.Y + 1 : row.MaxY - 1;
                _ui.Rule(new Point(row.X, edge), new Point(row.MaxX, edge), Ui.Accent, _ui.P(2));
            }
        }

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
        _thumbAreas[id] = thumb;
        x = thumb.MaxX + _ui.P(4);

        // As in Photoshop: a double click on an adjustment layer's picture opens its settings, one on
        // its name renames it.
        if (layer.Adjustment is not null)
            _ui.Area(Clip(thumb, _layersList), Choose, doubleClick: () => _canvas.EditAdjustmentLayer(id));

        _rowAreas.Add((visible, id));

        // Where a mask being dragged would land, as upstream's table shows its drop row.
        if (_maskDragFrom is Guid dragged && _maskDragMoved && visible.Contains(_maskDragAt) && _canvas.CanCopyMaskTo(dragged, id))
            _ui.Frame(visible, Ui.Accent, _ui.P(2));

        if (layer.Mask is LayerMask mask)
        {
            // The link between them: a chain while they move together, a gap once unlinked.
            var link = new Rect(x - _ui.P(1), row.Y, _ui.P(12), row.Height);
            bool linked = mask.IsLinked;
            _ui.Button(Clip(link, _layersList), () => _canvas.ToggleMaskLink(id),
                       Localizer.Text(linked ? TextKey.TooltipMaskLink : TextKey.TooltipMaskUnlinked),
                       face: _ => { if (linked) Icons.Chain(_ui, link, Ui.Dim); });
            x = link.MaxX;

            var maskThumb = new Rect(x, thumb.Y, thumb.Width, thumb.Height);
            CanvasPicture(maskThumb, mask.Coverage, MaskEditing.PlacementOf(layer), EdgeTone(mask.Coverage));
            // A click makes the mask the target; a drag onto another layer's row copies it there.
            _ui.Drag(Clip(maskThumb, _layersList), (point, finished) => MaskDrag(id, point, finished),
                     Localizer.Text(TextKey.TooltipLayerMask));

            // Which of the two the tools work on, framed as Photoshop frames it.
            bool maskTargeted = chosen && _canvas.EditingMask;
            Rect targeted = maskTargeted ? maskThumb : thumb;
            if (chosen && (maskTargeted || !layer.IsGroup))
                _ui.Frame(new Rect(targeted.X - _ui.P(1.5f), targeted.Y - _ui.P(1.5f), targeted.Width + _ui.P(3), targeted.Height + _ui.P(3)), Ui.Ink);
            if (!mask.IsEnabled) _ui.Rule(new Point(maskThumb.X, maskThumb.MaxY), new Point(maskThumb.MaxX, maskThumb.Y), Ui.Accent, _ui.P(1.5f));
            x = maskThumb.MaxX + _ui.P(4);
        }

        // A layer's name is the user's own, whatever language it was made in.
        _ui.Text(layer.Name, new Rect(x + _ui.P(4), row.Y, row.MaxX - x - _ui.P(8), row.Height),
                 layer.IsVisible ? Ui.Ink : Ui.Dim, user: true);
    }

    private readonly List<(Rect Row, Guid Id)> _rowAreas = [];
    private (Rect Row, Guid Id)? _bottomRow;
    private double _rowsEnd;

    /// <summary>The bottom top-level row, when a drop at <paramref name="point"/> is in the empty list below the rows.</summary>
    private (Rect Row, Guid Id)? BottomDrop(Point point) =>
        _layersList.Contains(point) && point.Y >= _rowsEnd ? _bottomRow : null;
    private Guid? _maskDragFrom;
    private Point _maskDragStart;
    private Point _maskDragAt;
    private bool _maskDragMoved;

    /// <summary>
    /// A press on a mask thumbnail, followed until release: left where it was it is a click, which
    /// makes the mask the target; carried onto another layer's row it copies the mask there.
    /// </summary>
    private void MaskDrag(Guid id, Point point, bool finished)
    {
        if (_maskDragFrom is null)
        {
            _maskDragFrom = id;
            _maskDragStart = point;
            _maskDragMoved = false;
        }

        _maskDragAt = point;
        if (Math.Abs(point.X - _maskDragStart.X) + Math.Abs(point.Y - _maskDragStart.Y) > _ui.P(4)) _maskDragMoved = true;
        if (!finished) return;

        _maskDragFrom = null;
        if (!_maskDragMoved)
        {
            // Photoshop's: Ctrl loads the black areas (Shift adds, Alt takes away), Shift switches
            // the mask off and on, a plain click makes it the target.
            bool control = IsKeyDown(VK_CONTROL), shift = IsKeyDown(VK_SHIFT), alt = IsKeyDown(VK_MENU);
            if (control) _canvas.LoadMaskSelection(id, add: shift, subtract: alt);
            else if (shift) _canvas.ToggleMaskOf(id);
            else _canvas.ClickMask(id);
            return;
        }

        foreach ((Rect row, Guid target) in _rowAreas)
        {
            if (!row.Contains(point) || !_canvas.CanCopyMaskTo(id, target)) continue;
            _canvas.CopyMask(id, target);
            return;
        }
    }

    private readonly Dictionary<Guid, Rect> _thumbAreas = [];
    private Guid? _rowDragFrom;
    private Point _rowDragStart;
    private Point _rowDragAt;
    private bool _rowDragMoved;

    /// <summary>
    /// A press on a layer's row, followed until release. Left where it was, it is a click — with
    /// Ctrl on the thumbnail, the layer's pixels as the selection. Carried, it drops the layers above
    /// or below the row under the pointer, or into a folder; with Alt, copies of them.
    /// </summary>
    private void RowDrag(ImageLayer layer, Point point, bool finished, IReadOnlyList<Guid> order)
    {
        if (_rowDragFrom is null)
        {
            _rowDragFrom = layer.Id;
            _rowDragStart = point;
            _rowDragMoved = false;
        }

        _rowDragAt = point;
        if (Math.Abs(point.Y - _rowDragStart.Y) > _ui.P(5)) _rowDragMoved = true;
        if (!finished)
        {
            double edge = _ui.P(24);
            if (point.Y < _layersList.Y + edge) _scroll = Math.Max(0, _scroll - _ui.P(10));
            else if (point.Y > _layersList.MaxY - edge) _scroll += _ui.P(10);
            return;
        }
        _rowDragFrom = null;

        bool control = IsKeyDown(VK_CONTROL), shift = IsKeyDown(VK_SHIFT), alt = IsKeyDown(VK_MENU);
        if (!_rowDragMoved)
        {
            if (control && _thumbAreas.TryGetValue(layer.Id, out Rect thumb) && thumb.Contains(_rowDragStart) && layer.Image is not null)
                _canvas.LoadLayerSelection(layer.Id, add: shift, subtract: alt);
            else
                _canvas.ClickLayer(layer.Id, control, shift, order);
            return;
        }

        foreach ((Rect area, int index) in _tabAreas)
        {
            if (index == _canvas.ActiveTab || !area.Contains(point)) continue;
            _canvas.CopyLayersToTab(layer.Id, index);
            return;
        }
        if (_newTabDrop.Contains(point))
        {
            _canvas.CopyLayersToNewTab(layer.Id);
            return;
        }

        foreach ((Rect row, Guid target) in _rowAreas)
        {
            if (!row.Contains(point) || _canvas.Document?.Layer(target) is not ImageLayer under) continue;
            _canvas.PlaceLayers(layer.Id, target, DropAt(row, under, point), copy: alt);
            return;
        }

        if (BottomDrop(point) is (_, Guid bottom) && bottom != layer.Id)
            _canvas.PlaceLayers(layer.Id, bottom, LayerDrop.Below, copy: alt);
    }

    /// <summary>Above or below a row by which half the pointer is in; a folder's middle half is into it.</summary>
    private static LayerDrop DropAt(Rect row, ImageLayer under, Point point)
    {
        double along = (point.Y - row.Y) / Math.Max(1, row.Height);
        if (under.IsGroup && along is > 0.25 and < 0.75) return LayerDrop.Into;
        return along < 0.5 ? LayerDrop.Above : LayerDrop.Below;
    }

    /// <summary>
    /// A right click on a layer's row: that layer chosen and upstream's row menu — rename, duplicate,
    /// delete, the mask's commands, clipping, folders, merging. True when a row took it.
    /// </summary>
    public bool ContextMenu(Point at)
    {
        if (_canvas.Document is not CanvasDocument document || !_canvas.CanEdit) return false;

        foreach ((Rect row, Guid id) in _rowAreas)
        {
            if (!row.Contains(at) || document.Layer(id) is not ImageLayer layer) continue;
            if (!_canvas.Chosen.Contains(id)) _canvas.ClickLayer(id, control: false, shift: false, [id]);

            var entries = new List<(string Text, Action Run)>
            {
                (Localizer.Text(TextKey.CommandRenameLayer), () => StartRename(id, row)),
                (Localizer.Text(TextKey.CommandDuplicateLayer), () => _runCommand(CommandIds.DuplicateLayer)),
                (Localizer.Text(TextKey.CommandDeleteLayer), () => _runCommand(CommandIds.DeleteLayer)),
            };
            var separators = new List<int> { entries.Count - 1 };

            if (layer.Mask is LayerMask mask)
            {
                entries.Add((Localizer.Text(TextKey.CommandDeleteLayerMask), () => _runCommand(CommandIds.DeleteLayerMask)));
                entries.Add((Localizer.Text(mask.IsEnabled ? TextKey.CommandDisableLayerMask : TextKey.CommandEnableLayerMask),
                             () => _canvas.ToggleMaskOf(id)));
                entries.Add((Localizer.Text(mask.IsLinked ? TextKey.CommandUnlinkMask : TextKey.CommandLinkMask),
                             () => _canvas.ToggleMaskLink(id)));
            }
            else if (layer.Adjustment is null)
            {
                entries.Add((Localizer.Text(TextKey.CommandAddLayerMask), () => _runCommand(CommandIds.AddLayerMask)));
                entries.Add((Localizer.Text(TextKey.CommandAddHideMask), () => _runCommand(CommandIds.AddHideMask)));
            }
            separators.Add(entries.Count - 1);

            if (!layer.IsGroup)
                entries.Add((Localizer.Text(layer.MaskSourceId is null ? TextKey.CommandCreateClippingMask : TextKey.CommandReleaseClippingMask),
                             () => _runCommand(CommandIds.ToggleClipping)));
            entries.Add((Localizer.Text(TextKey.CommandGroupLayers), () => _runCommand(CommandIds.GroupLayers)));
            if (layer.ParentId is not null)
                entries.Add((Localizer.Text(TextKey.CommandMoveOutOfGroup), () => _runCommand(CommandIds.MoveOutOfGroup)));
            if (_canvas.MergePlan is LayerCommands.MergePlan plan)
            {
                separators.Add(entries.Count - 1);
                entries.Add((Localizer.Text(plan.Action), () => _runCommand(CommandIds.Merge)));
            }

            int picked = Popup([.. entries.Select(entry => (entry.Text, false))], [.. separators]);
            if (picked >= 0) entries[picked].Run();
            return true;
        }

        return false;
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
        CanvasPicture(area, layer.Image, layer.Transform, edge: null);
    }

    private readonly Dictionary<PixelBuffer, float> _edgeTones = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Upstream's <c>CanvasThumbnail</c>: a canvas-shaped picture with the pixels drawn where they sit
    /// on the canvas, whatever their own bounds — over the transparency checkerboard for a layer (an
    /// empty layer is an empty canvas), over the mask's own edge tone for a mask, since outside its
    /// layer a mask has no effect: a reveal-all mask reads white and a hide-all one black.
    /// </summary>
    private void CanvasPicture(Rect area, PixelBuffer? pixels, LayerTransform? placement, float? edge)
    {
        double canvasWidth = Math.Max(1, _canvas.Document?.Width ?? 1);
        double canvasHeight = Math.Max(1, _canvas.Document?.Height ?? 1);
        double fit = Math.Min(area.Width / canvasWidth, area.Height / canvasHeight);
        double w = Math.Max(1, Math.Round(canvasWidth * fit)), h = Math.Max(1, Math.Round(canvasHeight * fit));
        var frame = new Rect(area.X + Math.Round((area.Width - w) / 2), area.Y + Math.Round((area.Height - h) / 2), w, h);

        _ui.Context.PushAxisAlignedClip(Ui.Raw(frame), AntialiasMode.Aliased);
        if (edge is float tone)
        {
            _ui.Fill(frame, new Color4(tone, tone, tone, 1));
        }
        else
        {
            _ui.Fill(frame, new Color4(0.22f, 0.22f, 0.22f, 1));
            double tile = _ui.P(4);
            for (int row = 0; row * tile < h; row++)
                for (int column = row % 2; column * tile < w; column += 2)
                    _ui.Fill(new Rect(frame.X + column * tile, frame.Y + row * tile, tile, tile), new Color4(0.32f, 0.32f, 0.32f, 1));
        }

        if (pixels is not null && placement is not null)
        {
            ID2D1Bitmap1 bitmap = ThumbnailBitmap(pixels);
            Vortice.Mathematics.SizeI size = bitmap.PixelSize;
            // The bitmap's unit square onto the placement's corners, and the canvas onto the frame.
            Point origin = placement.PointAt(new Point(0, 0));
            Point across = placement.PointAt(new Point(1, 0));
            Point down = placement.PointAt(new Point(0, 1));
            double sx = frame.Width / canvasWidth, sy = frame.Height / canvasHeight;
            var toFrame = new System.Numerics.Matrix3x2(
                (float)((across.X - origin.X) * sx / size.Width), (float)((across.Y - origin.Y) * sy / size.Width),
                (float)((down.X - origin.X) * sx / size.Height), (float)((down.Y - origin.Y) * sy / size.Height),
                (float)(frame.X + origin.X * sx), (float)(frame.Y + origin.Y * sy));
            System.Numerics.Matrix3x2 before = _ui.Context.Transform;
            _ui.Context.Transform = toFrame * before;
            _ui.Context.DrawBitmap(bitmap, new Vortice.RawRectF(0, 0, size.Width, size.Height), 1f,
                                   BitmapInterpolationMode.Linear, null);
            _ui.Context.Transform = before;
        }
        _ui.Context.PopAxisAlignedClip();
        _ui.Frame(frame, Ui.Line);
    }

    /// <summary>The mean grey of a mask's outermost pixels, what it leaves the rest of the canvas at.</summary>
    private float EdgeTone(PixelBuffer coverage)
    {
        if (_edgeTones.TryGetValue(coverage, out float known)) return known;
        long sum = 0, count = 0;
        for (int y = 0; y < coverage.Height; y++)
        {
            ReadOnlySpan<byte> row = coverage.Row(y);
            bool edgeRow = y == 0 || y == coverage.Height - 1;
            for (int x = 0; x < coverage.Width; x += edgeRow ? 1 : Math.Max(1, coverage.Width - 1))
            {
                sum += row[x * 4];
                count++;
            }
        }
        float tone = count == 0 ? 1 : sum / (255f * count);
        if (_edgeTones.Count > 256) _edgeTones.Clear();
        _edgeTones[coverage] = tone;
        return tone;
    }

    /// <summary>The small bitmap a buffer's thumbnails draw, uploaded once per buffer.</summary>
    private ID2D1Bitmap1 ThumbnailBitmap(PixelBuffer pixels)
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
        return bitmap;
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
    private Rect _railArea;
    private double _railScroll;

    public bool Scroll(Point at, double notches)
    {
        if (_railArea.Contains(at))
        {
            _railScroll -= notches * _ui.P(38);
            return true;
        }
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
    internal int Popup((string Text, bool Checked)[] items, int[]? separatorsAfter = null)
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
        // An owned pop-up rather than a child: the window presents through a flip-model swap chain,
        // which covers child windows, so a child box took the typing while nothing of it showed.
        var corner = new POINTSTRUCT { X = (int)left, Y = (int)(row.Y + _ui.P(6)) };
        ClientToScreen(_window, ref corner);
        _renameBox = CreateWindowExW(WS_EX_TOOLWINDOW, "EDIT", layer.Name, WS_POPUP | WS_VISIBLE | WS_BORDER | ES_AUTOHSCROLL,
                                     corner.X, corner.Y, (int)(row.MaxX - left - _ui.P(8)),
                                     (int)(row.Height - _ui.P(12)), _window, 0, GetModuleHandleW(0), 0);
        SendMessageW(_renameBox, WM_SETFONT, (nuint)_renameFont, 1);
        SendMessageW(_renameBox, EM_SETSEL, 0, -1);
        SetFocus(_renameBox);
        WriteHangul(_renameBox, HangulMode);
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
        // A Han/Eng press in the box is the user's choice too.
        if (_renameBox != 0 && ReadHangul(_renameBox) is bool hangul) HangulMode = hangul;
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

        string size = Localizer.Format(TextKey.StatusDocumentSize, document.Width, document.Height);
        var zoomBox = new Rect(pad, bar.Y + _ui.P(2), _ui.P(62), bar.Height - _ui.P(4));
        _ui.Field(zoomBox, "status-zoom", _canvas.Viewport.Zoom * 100,
                  _canvas.Viewport.Zoom < 0.1 ? 1 : 0, _canvas.SetZoomPercent);
        _ui.Text("%", new Rect(zoomBox.MaxX + _ui.P(2), bar.Y, _ui.P(12), bar.Height), Ui.Dim, Ui.TextSize.Small);
        _ui.Text(size, new Rect(pad + _ui.P(80), bar.Y, _ui.P(200), bar.Height), Ui.Dim, Ui.TextSize.Small);
        _ui.Text(_canvas.Title, new Rect(pad + _ui.P(290), bar.Y, _ui.P(400), bar.Height), Ui.Dim, Ui.TextSize.Small,
                 // A file's or an image's name is the user's; only "Untitled" is the interface's own word.
                 user: _canvas.Title != Localizer.Text(TextKey.DocumentUntitled));
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
