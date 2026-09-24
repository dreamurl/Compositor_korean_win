using System.Numerics;
using Compositor_korean_win.Core;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Mathematics;
// Vortice has geometry types of its own, and none of them are the ones Core speaks in.
using Point = Compositor_korean_win.Core.Point;
using Rect = Compositor_korean_win.Core.Rect;
using Size = Compositor_korean_win.Core.Size;

namespace Compositor_korean_win.Shell;

/// <summary>What a drag on the canvas does.</summary>
internal enum CanvasTool
{
    /// <summary>Leaves the canvas inert while inspecting the document.</summary>
    Idle,

    /// <summary>Picks a layer up and puts it somewhere else.</summary>
    Move,

    RectangleMarquee,
    EllipseMarquee,
    Lasso,

    /// <summary>Clicks out a selection corner by corner.</summary>
    PolygonLasso,

    /// <summary>Paints the foreground colour.</summary>
    Brush,

    /// <summary>Takes pixels away.</summary>
    Eraser,

    /// <summary>Paints pixels sampled from somewhere else on the same layer.</summary>
    CloneStamp,

    /// <summary>Softens what is under it.</summary>
    Blur,

    /// <summary>Marks what to rebuild from the texture around it.</summary>
    Heal,

    /// <summary>Selects the pixels that look like the one clicked.</summary>
    MagicWand,

    /// <summary>Drags out a gradient.</summary>
    Gradient,

    /// <summary>Drags out a rectangle, a rounded rectangle or an ellipse.</summary>
    Shape,

    /// <summary>Frames what the canvas becomes.</summary>
    Crop,

    /// <summary>Takes the colour the document shows.</summary>
    Eyedropper,

    /// <summary>Moves the view.</summary>
    Hand,

    /// <summary>Zooms in on a click, out with Alt, continuously on a drag.</summary>
    Zoom,
}

/// <summary>How a selection tool combines its next outline with the current selection.</summary>
internal enum SelectionModeChoice
{
    Replace,
    Add,
    Subtract,
}

/// <summary>Which tools make a stroke rather than a drag or a click.</summary>
internal static class CanvasTools
{
    public static bool Paints(this CanvasTool tool) => tool
        is CanvasTool.Brush or CanvasTool.Eraser or CanvasTool.CloneStamp
        or CanvasTool.Blur or CanvasTool.Heal;

    /// <summary>Upstream's selection tools: the marquees, the lassos and the magic wand.</summary>
    public static bool Selects(this CanvasTool tool) => tool
        is CanvasTool.RectangleMarquee or CanvasTool.EllipseMarquee or CanvasTool.Lasso
        or CanvasTool.PolygonLasso or CanvasTool.MagicWand;

    public static bool DragsOutAShape(this CanvasTool tool) =>
        tool is CanvasTool.Gradient or CanvasTool.Shape;

    public static BrushMode ModeFor(this CanvasTool tool) => tool switch
    {
        CanvasTool.Eraser => BrushMode.Erase,
        CanvasTool.CloneStamp => BrushMode.Clone,
        CanvasTool.Blur => BrushMode.Blur,
        CanvasTool.Heal => BrushMode.Heal,
        _ => BrushMode.Paint,
    };
}

/// <summary>
/// The canvas: a document, where it is being looked at from, and what the pointer is doing to it.
/// </summary>
/// <remarks>
/// <para>
/// Everything worth testing about this lives in Core — the viewport arithmetic, the drag
/// arithmetic, the snapping, the projected compositing. What is left here is the part that only
/// means anything with a window in front of it: turning Win32 messages into those calls, and
/// drawing the overlay that shows where a layer is and what it just lined up with.
/// </para>
/// <para>
/// A frame is composited onto the window's back buffer through the ordinary compositor, with the
/// viewport's projection carrying every placement into the window's own pixels. Nothing here walks
/// the layer tree or knows what a blend mode is.
/// </para>
/// </remarks>
internal sealed partial class CanvasView : IDisposable
{
    /// <summary>Where the rotation handle sits above the top edge, in view points.</summary>
    private const double RotationReach = 28;

    /// <summary>The handle index the rotation handle answers to.</summary>
    private const int RotationHandle = 8;

    private readonly GraphicsDevice _device;
    private readonly Direct2DBackend _backend;
    // The canvas owns the pixels of its documents: a step the history drops frees what only it
    // held, and closing a document frees the rest.
    private DocumentHistory _history = new(ownsPixels: true);

    private IRenderSurface? _surface;
    private Rect _area;
    private Point _origin;
    private int _windowWidth = 1;
    private int _windowHeight = 1;
    private CanvasDocument? _document;
    private CanvasViewport _viewport = new();
    private double _scale = 1;

    private readonly HashSet<Guid> _chosen = [];
    private TransformDrag? _drag;
    private Dictionary<Guid, LayerTransform> _originals = [];
    private LayerTransform? _boxAtStart;
    private IReadOnlyList<Point>? _distorting;
    private DistortPreview? _distortPreview;
    private SnapGuides _targets = SnapGuides.None;
    private SnapResult _snap;
    private bool _panning;
    private Point _panFrom;

    private CanvasTool _tool = CanvasTool.Move;
    private DocumentSelection? _selection;

    private BrushStroke? _stroke;
    private PixelBuffer? _strokeBase;
    private Point? _strokeStart;
    private Point? _strokeEnd;
    private Point? _lastStrokeEnd;
    private Guid? _lastStrokeLayer;
    private bool _lastStrokeOnMask;
    private Guid _painting;
    private PixelRect _paintGrid;
    private Point? _cloneAnchor;
    private PixelBuffer? _cloneSample;
    private Point? _cloneOffset;
    private Point? _shapeFrom;
    private Point _shapeTo;
    private Point _shapeAnchor;
    private bool _marqueeSquareArmed;
    private Point? _outlineFrom;
    private DocumentSelection? _outlineBase;
    private List<Point>? _polygon;
    private Point? _movingFrom;
    private Point _movingTo;
    private bool _movingDuplicates;
    private Point _pointer;
    private Point? _marqueeFrom;
    private Point _marqueeTo;
    private List<Point>? _lasso;
    private bool _marqueeAdds;
    private bool _marqueeTakesAway;
    private Point? _brushAdjustFrom;
    private BrushSettings? _brushBeforeAdjust;

    public SelectionModeChoice SelectionMode { get; set; }

    public bool SelectionAntialiased { get; set; } = true;

    /// <summary>Whether the Move tool chooses the topmost layer under the pointer.</summary>
    public bool AutoSelect { get; set; }

    public CanvasView(GraphicsDevice device)
    {
        _device = device;
        _backend = new Direct2DBackend(device);
    }

    public CanvasDocument? Document => _document;

    public CanvasViewport Viewport => _viewport;

    /// <summary>The layers the handles belong to. More than one share a box.</summary>
    public IReadOnlyCollection<Guid> Chosen => _chosen;

    /// <summary>The one layer history and the inspector answer to, of however many are chosen.</summary>
    private Guid? Primary => _chosen.Count > 0 ? _chosen.First() : null;

    /// <summary>What is selected, or null for no selection — which means the whole canvas.</summary>
    public DocumentSelection? Selection => _selection;

    public CanvasTool Tool => _tool;

    /// <summary>What the brush and the tools built on it are set to.</summary>
    public BrushSettings Brush { get; set; } = new();

    public ShapeSettings Shape { get; set; } = new();

    private GradientSettings _gradient = new();
    public GradientSettings Gradient
    {
        get => _gradient;
        set
        {
            _gradient = value;
            if (_gradientFrom is not null) RefreshGradient();
        }
    }

    public WandSettings Wand { get; set; } = new();

    /// <summary>
    /// Whether the clone stamp keeps one offset between strokes, as Photoshop's default does.
    /// </summary>
    public bool CloneAligned { get; set; } = true;

    /// <summary>Set whenever something changed that the window has not drawn yet.</summary>
    public bool NeedsRedraw { get; private set; } = true;

    /// <summary>
    /// The window changed size, or the panels round the canvas did. <paramref name="area"/> is the
    /// canvas's part of the window in device pixels; <paramref name="scale"/> is device pixels per point.
    /// </summary>
    public void Resize(Rect area, int windowWidth, int windowHeight, double scale)
    {
        _scale = Math.Max(1, scale);
        _area = area;
        _origin = new Point(area.X, area.Y);
        _windowWidth = Math.Max(1, windowWidth);
        _windowHeight = Math.Max(1, windowHeight);

        // The back buffer is a new bitmap after a resize, so the surface borrowing it is stale.
        _surface?.Dispose();
        _surface = null;

        _viewport = _viewport.Resized(new Size(Math.Max(1, area.Width) / _scale, Math.Max(1, area.Height) / _scale),
                                      _scale, _document?.Size);
        NeedsRedraw = true;
    }

    /// <summary>The whole window as the canvas, for callers with no panels round it.</summary>
    public void Resize(int pixelWidth, int pixelHeight, double scale) =>
        Resize(new Rect(0, 0, pixelWidth, pixelHeight), pixelWidth, pixelHeight, scale);

    /// <summary>
    /// A point in the window's device pixels, as the mouse messages give it, in the canvas's own
    /// view points.
    /// </summary>
    public Point ToView(int x, int y) => new((x - _origin.X) / _scale, (y - _origin.Y) / _scale);

    /// <summary>Where the canvas sits in the window, in device pixels.</summary>
    public Rect Area => _area;

    /// <summary>Document to window pixels: the viewport's projection, moved over to the canvas.</summary>
    private CanvasProjection Projection(CanvasDocument document) =>
        LayerCompositor.Placed(_viewport.DeviceProjection(document.Size), _origin);

    /// <summary>
    /// Draws the canvas into its part of the window. The window presents; the panels draw over the
    /// rest first.
    /// </summary>
    public void Render()
    {
        ID2D1DeviceContext context = _device.D2DContext;

        // The desk the document lies on, and a checkerboard where the document is transparent.
        context.BeginDraw();
        context.PushAxisAlignedClip(Raw(_area), AntialiasMode.Aliased);
        context.Clear(new Color4(0.12f, 0.12f, 0.125f, 1.0f));

        if (_document is not null)
        {
            Rect sheet = CanvasRect(_document);
            ID2D1BitmapBrush checker = Checkerboard(context);
            checker.Transform = Matrix3x2.CreateTranslation((float)Math.Round(sheet.X), (float)Math.Round(sheet.Y));
            context.FillRectangle(Raw(sheet), checker);
        }

        context.PopAxisAlignedClip();
        context.EndDraw().CheckError();

        if (_document is not null)
        {
            _surface ??= _backend.CreateWindowSurface(_windowWidth, _windowHeight);
            LayerCompositor.DrawView(_document, _viewport, _surface, _backend, Live(_windowWidth, _windowHeight),
                                     _origin, _area);
            DrawOverlay(context);
        }

        NeedsRedraw = false;
    }

    private ID2D1Bitmap1? _checkerBitmap;
    private ID2D1BitmapBrush? _checker;

    /// <summary>
    /// The light and dark grey squares that stand for transparency, eight device pixels each,
    /// as a repeating brush.
    /// </summary>
    private ID2D1BitmapBrush Checkerboard(ID2D1DeviceContext context)
    {
        if (_checker is not null) return _checker;

        using PixelBuffer tile = PixelBuffer.Allocate(16, 16);
        for (int y = 0; y < 16; y++)
        {
            Span<byte> row = tile.Row(y);
            for (int x = 0; x < 16; x++)
            {
                byte level = ((x / 8) + (y / 8)) % 2 == 0 ? (byte)204 : (byte)153;
                row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = level;
                row[x * 4 + 3] = 255;
            }
        }

        _checkerBitmap = ImageLoader.Upload(context, tile, Vortice.DXGI.Format.R8G8B8A8_UNorm);
        _checker = context.CreateBitmapBrush(_checkerBitmap,
            new BitmapBrushProperties(ExtendMode.Wrap, ExtendMode.Wrap, BitmapInterpolationMode.NearestNeighbor));
        return _checker;
    }

    // MARK: Input

    /// <summary>A button went down at <paramref name="view"/>, in view points.</summary>
    public void PointerDown(Point view, bool pan, bool doubleClick = false)
    {
        if (_document is null) return;

        if (pan)
        {
            _panning = true;
            _panFrom = view;
            return;
        }

        // While a filter or an adjustment is being set, a click is an eyedropper's or nothing.
        if (FilterClick(view)) return;

        Point pixel = _viewport.DocumentPoint(view, _document.Size);

        if (BeginNavigation(view, pixel, Win32.IsKeyDown(Win32.VK_MENU))) return;

        if (_tool == CanvasTool.Idle) return;

        if (BeginSelectionDrag(pixel)) return;

        if (_tool == CanvasTool.PolygonLasso)
        {
            // The double-click's first press has already put its corner down; the second closes.
            if (doubleClick && _polygon is not null) ClosePolygon();
            else AddCorner(pixel);
            return;
        }

        if (_tool == CanvasTool.Crop)
        {
            BeginCrop(pixel, view, symmetric: Win32.IsKeyDown(Win32.VK_MENU));
            return;
        }

        if (_tool.Paints())
        {
            BeginStroke(pixel, alt: Win32.IsKeyDown(Win32.VK_MENU),
                        shift: Win32.IsKeyDown(Win32.VK_SHIFT));
            return;
        }

        if (_tool == CanvasTool.Gradient)
        {
            BeginGradient(pixel);
            return;
        }

        if (_tool.DragsOutAShape())
        {
            _shapeAnchor = pixel;
            _shapeFrom = pixel;
            _shapeTo = pixel;
            NeedsRedraw = true;
            return;
        }

        if (_tool == CanvasTool.MagicWand)
        {
            (bool add, bool subtract) = SelectionOperationForHeldKeys();
            Wave(pixel, add, subtract);
            return;
        }

        if (_tool != CanvasTool.Move)
        {
            _marqueeFrom = pixel;
            _marqueeTo = pixel;
            _lasso = _tool == CanvasTool.Lasso ? [pixel] : null;
            (_marqueeAdds, _marqueeTakesAway) = SelectionOperationForHeldKeys();
            // A Shift held at the press means Add; it squares the marquee only once pressed afresh.
            _marqueeSquareArmed = !Win32.IsKeyDown(Win32.VK_SHIFT);
            NeedsRedraw = true;
            return;
        }

        bool shift = Win32.IsKeyDown(Win32.VK_SHIFT);
        bool control = Win32.IsKeyDown(Win32.VK_CONTROL);
        bool alt = Win32.IsKeyDown(Win32.VK_MENU);

        // Floating pixels take the drag: their handles, or their box to move. Nothing else is
        // picked up until they are laid down.
        if (IsFloating)
        {
            if (Box() is LayerTransform floatingBox)
            {
                if (HitHandle(floatingBox, view) is int floatingHandle)
                {
                    TransformDragMode floatingMode = floatingHandle == RotationHandle ? TransformDragMode.Rotate
                        : control ? TransformDragMode.Distort(floatingHandle)
                        : TransformDragMode.Resize(floatingHandle);
                    Begin(floatingBox, floatingMode, pixel);
                }
                else if (floatingBox.Contains(pixel))
                {
                    Begin(floatingBox, TransformDragMode.Move, pixel);
                }
            }
            return;
        }

        // With a selection up, dragging inside it moves what is in it rather than the layer.
        if (_selection is DocumentSelection inside && inside.Contains(pixel))
        {
            _movingFrom = pixel;
            _movingTo = pixel;
            _movingDuplicates = alt;
            return;
        }

        // A handle takes the drag before anything under it does. Held with control it distorts:
        // the corner moves on its own, which no placement can hold, so the pixels are resampled
        // when the drag ends (QuadWarp).
        if (ShowTransformControls && Box() is LayerTransform box && HitHandle(box, view) is int handle)
        {
            TransformDragMode mode = handle == RotationHandle
                ? TransformDragMode.Rotate
                : TransformDragMode.Resize(handle);

            if (control && handle != RotationHandle && _chosen.Count == 1 && !TransformsMask)
            {
                Begin(box, TransformDragMode.Distort(handle), pixel);
                return;
            }

            Begin(box, mode, pixel);
            return;
        }

        // An unlinked mask is picked up by its own box, wherever the layer is.
        if (TransformsMask && Box() is LayerTransform maskBox && maskBox.Contains(pixel))
        {
            Begin(maskBox, TransformDragMode.Move, pixel);
            return;
        }

        // Auto Select follows the pointer. With it off the active layer moves, while Control is
        // the temporary upstream override that selects a layer under the pointer.
        ImageLayer? hit = AutoSelect || control ? Topmost(pixel) : ActiveLayer;
        NeedsRedraw = true;

        if (hit is null)
        {
            if (!shift) _chosen.Clear();
            return;
        }

        // Shift adds to the selection; anything else replaces it, unless the layer is already in it
        // — clicking one of several chosen layers picks them all up together.
        if (shift)
        {
            if (!_chosen.Add(hit.Id)) _chosen.Remove(hit.Id);
        }
        else if (!_chosen.Contains(hit.Id))
        {
            _chosen.Clear();
            _chosen.Add(hit.Id);
        }

        if (_chosen.Count == 0) return;
        if (alt) Duplicate();
        if (Box() is LayerTransform moving) Begin(moving, TransformDragMode.Move, pixel);
    }

    /// <summary>
    /// Copies the chosen layers and picks the copies up instead, which is what alt-dragging means.
    /// </summary>
    /// <remarks>
    /// The copy shares the original's pixels rather than duplicating four hundred megabytes: a
    /// buffer is immutable, so two layers holding one are two layers with the same pixels, and
    /// painting on either makes a new buffer anyway (docs/windows-port.md §2.2).
    /// </remarks>
    private void Duplicate()
    {
        if (_document is null) return;

        var copies = new List<ImageLayer>();
        var chosen = new List<Guid>(_chosen);

        foreach (Guid id in chosen)
        {
            if (_document.Layer(id) is not ImageLayer layer) continue;
            copies.Add(layer with { Id = Guid.NewGuid(), Name = Localizer.Format(TextKey.LayerCopyName, layer.Name) });
        }

        if (copies.Count == 0) return;

        _history.Begin(TextKey.HistoryDuplicateLayer, _document, copies[0].Id);
        _document = _document with { Layers = _document.Layers.Concat(copies).ToEquatableList() };
        _history.End(_document, copies[0].Id);

        _chosen.Clear();
        foreach (ImageLayer copy in copies) _chosen.Add(copy.Id);
    }

    /// <summary>The pointer moved, with the modifiers that were down as it did.</summary>
    public void PointerMoved(Point view, bool shift, bool alt, bool control)
    {
        if (_document is null) return;

        if (_panning)
        {
            _viewport = _viewport.Translated(new Point(view.X - _panFrom.X, view.Y - _panFrom.Y));
            _panFrom = view;
            NeedsRedraw = true;
            return;
        }

        _pointer = view;

        if (DragNavigation(view, _viewport.DocumentPoint(view, _document.Size))) return;

        if (_cropDragging)
        {
            DragCrop(_viewport.DocumentPoint(view, _document.Size), alt);
            return;
        }

        if (_stroke is not null || _warp is not null)
        {
            Point at = _viewport.DocumentPoint(view, _document.Size);
            if (shift) at = Straightened(at);

            // The grid the stroke began in: the layer's, or its mask's.
            Point inGrid = LayerGeometry.ToPixels(_strokePlacement, at, _paintGrid.Width, _paintGrid.Height);
            _stroke?.Append(inGrid);
            _warp?.Append(inGrid);
            _strokeEnd = at;
            if (_strokeOnMask) ShowMaskStroke();
            NeedsRedraw = true;
            return;
        }

        if (_movingFrom is not null)
        {
            _movingTo = _viewport.DocumentPoint(view, _document.Size);
            NeedsRedraw = true;
            return;
        }

        if (_shapeFrom is not null)
        {
            Point shapeAt = _viewport.DocumentPoint(view, _document.Size);
            if (_tool == CanvasTool.Gradient) DragGradient(shapeAt, shift);
            else (_shapeFrom, _shapeTo) = ShapeCorners(_shapeAnchor, shapeAt, square: shift, fromCentre: alt);
            NeedsRedraw = true;
            return;
        }

        if (_outlineFrom is Point grabbed && _outlineBase is DocumentSelection outline)
        {
            Point at = _viewport.DocumentPoint(view, _document.Size);
            double dx = at.X - grabbed.X, dy = at.Y - grabbed.Y;
            // Shift keeps the move on one axis: whichever way the drag has gone further.
            if (shift)
            {
                if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
                else dx = 0;
            }
            _selection = outline.Transformed(point => new Point(point.X + dx, point.Y + dy));
            NeedsRedraw = true;
            return;
        }

        if (_marqueeFrom is Point marqueeStart)
        {
            _marqueeTo = _viewport.DocumentPoint(view, _document.Size);
            if (!shift) _marqueeSquareArmed = true;
            if (_lasso is null && shift && _marqueeSquareArmed) _marqueeTo = Squared(marqueeStart, _marqueeTo);

            // A freehand outline keeps every point the pointer passed through, thinned so that a
            // slow drag does not pile up thousands of them a pixel apart.
            if (_lasso is List<Point> lasso)
            {
                Point last = lasso[^1];
                if (Math.Abs(last.X - _marqueeTo.X) + Math.Abs(last.Y - _marqueeTo.Y) >= 1)
                    lasso.Add(_marqueeTo);
            }

            NeedsRedraw = true;
            return;
        }

        if (_drag is not TransformDrag drag || _boxAtStart is not LayerTransform from) return;

        Point pixel = _viewport.DocumentPoint(view, _document.Size);

        // A distortion has no placement to hold it: the corners move now, the frame shows the
        // pixels resampled into them (Live), and the layer itself is resampled when the button
        // comes up.
        if (drag.Corners(pixel, shift) is IReadOnlyList<Point> corners)
        {
            if (QuadWarp.IsUsable(corners)) _distorting = corners;
            NeedsRedraw = true;
            return;
        }

        // Dragging, scaling and rotating land on whole pixels and whole degrees; a value typed into
        // the inspector stays exactly as it was typed.
        LayerTransform draft = drag.Updated(pixel, lockRatio: false, shift, alt).Rounded();

        // Moving snaps to the canvas and to the other layers; resizing and rotating are left alone,
        // and holding control drags free of the pull altogether.
        _snap = default;
        if (drag.Mode.Kind == TransformDragKind.Move && !control)
        {
            (draft, _snap) = TransformSnap.Move(draft, _targets, TransformSnap.ToleranceFor(_viewport));
        }

        // One layer is its own box, so it takes the draft as it is; several are carried along with
        // theirs. Going through the group arithmetic either way would put a multiply and a divide
        // between what the handle said and what the layer got.
        if (Primary is Guid only && _chosen.Count == 1 && _document.Layer(only) is ImageLayer single)
        {
            _document = _document.Replacing(TransformsMask
                ? MaskEditing.WithMaskPlacement(single, draft)
                : MaskEditing.WithTransform(single, draft));
        }
        else
        {
            Apply(TransformGroup.Follow(_originals, from, draft));
        }

        NeedsRedraw = true;
    }

    /// <summary>Puts a set of placements onto the document, one layer at a time.</summary>
    private void Apply(IReadOnlyDictionary<Guid, LayerTransform> placements)
    {
        if (_document is null) return;

        foreach ((Guid id, LayerTransform placement) in placements)
        {
            if (_document.Layer(id) is not ImageLayer layer) continue;
            _document = _document.Replacing(MaskEditing.WithTransform(layer, placement));
        }
    }

    public void PointerUp()
    {
        _panning = false;
        if (IsFiltering) return;

        if (EndNavigation()) return;

        if (_cropDragging)
        {
            EndCrop();
            return;
        }

        if (_warp is not null)
        {
            EndWarp();
            return;
        }

        if (_stroke is not null)
        {
            EndStroke();
            return;
        }

        if (_movingFrom is Point lifted)
        {
            MoveSelected(lifted);
            return;
        }

        if (_shapeFrom is Point corner)
        {
            EndShape(corner);
            return;
        }

        if (_outlineFrom is not null)
        {
            _outlineFrom = null;
            _outlineBase = null;
            NeedsRedraw = true;
            return;
        }

        if (_marqueeFrom is Point start)
        {
            Commit(start);
            return;
        }

        if (_drag is null)
        {
            _snap = default;
            return;
        }

        _drag = null;
        _snap = default;
        ApplyDistortion();
        _history.End(_document, Primary);
        NeedsRedraw = true;
    }

    /// <summary>
    /// Resamples the distorted layer into the corners the drag left it at.
    /// </summary>
    /// <remarks>
    /// This is the one transform that cannot stay in the six numbers, so it is also the one that
    /// spends pixels: what comes back is an upright layer whose image holds the distortion. A shape
    /// that folded over on itself is refused and the layer is left alone.
    /// </remarks>
    private void ApplyDistortion()
    {
        IReadOnlyList<Point>? corners = _distorting;
        _distorting = null;
        _distortPreview?.Dispose();
        _distortPreview = null;

        if (corners is null || _document is null) return;
        if (_chosen.Count != 1 || _document.Layer(_chosen.First()) is not ImageLayer layer) return;
        // The mask goes with the pixels as its link says (QuadWarp.Distort).
        if (QuadWarp.Distort(layer, corners) is ImageLayer distorted)
        {
            _document = _document.Replacing(distorted);
            FloatingMade(distorted.Image);
        }
    }

    // MARK: Painting

    /// <summary>
    /// The stroke in progress, as pixels the compositor can draw.
    /// </summary>
    /// <remarks>
    /// The layer's own pixels with the stroke's tiles over them, which is the whole point of
    /// keeping a stroke in tiles: the frame draws the tiles that changed, and the layer underneath
    /// is the buffer it always was.
    /// </remarks>
    /// <summary>The layer drawn into the corners a distortion drag has reached, if one is under way.</summary>
    private LiveEdit? Distorted(int width, int height)
    {
        if (_distorting is not IReadOnlyList<Point> corners || _document is null) return null;
        if (_chosen.Count != 1 || _document.Layer(_chosen.First()) is not { Image: not null } layer) return null;

        if (_distortPreview?.Layer.Id != layer.Id)
        {
            _distortPreview?.Dispose();
            _distortPreview = new DistortPreview(layer);
        }

        return _distortPreview!.Frame(corners, Projection(_document), width, height);
    }

    private LiveEdit? Live(int width, int height)
    {
        if (Previewing(width, height) is LiveEdit previewed) return previewed;
        if (GradientLive() is LiveEdit gradient) return gradient;
        if (Distorted(width, height) is LiveEdit distorted) return distorted;
        if (Warped() is LiveEdit warped) return warped;
        // A stroke on a mask is shown through the mask the document holds while it lasts.
        if (_strokeOnMask) return null;
        if (_stroke is not BrushStroke stroke || _document is null) return null;
        if (_document.Layer(_painting) is not ImageLayer layer) return null;

        PixelBuffer? under = layer.Image ?? _strokeBase;
        if (under is null) return null;

        return new LiveEdit(_painting, new LayerRaster(under, stroke.Patches));
    }

    /// <summary>
    /// Starts a stroke on the chosen layer, in that layer's own pixels.
    /// </summary>
    /// <remarks>
    /// A layer with no pixels yet gets the document's grid, which is what upstream gives a new
    /// blank layer: painting is the thing that decides a blank layer's raster, and until then there
    /// is nothing to decide it from.
    /// </remarks>
    private void BeginStroke(Point document, bool alt, bool shift)
    {
        if (_document is null || Primary is not Guid id
            || _document.Layer(id) is not ImageLayer layer) return;

        // A folder has no pixels of its own, but it can have a mask to paint.
        if (EditingMask)
        {
            BeginMaskStroke(layer, document, shift);
            return;
        }

        if (layer.IsGroup) return;

        if (_tool == CanvasTool.Blur && BlurMode != BlurToolMode.Blur)
        {
            BeginWarp(layer, document);
            return;
        }

        _paintGrid = layer.Image is PixelBuffer pixels
            ? new PixelRect(0, 0, pixels.Width, pixels.Height)
            : new PixelRect(0, 0, _document.Width, _document.Height);

        LayerTransform placement = layer.Image is null
            ? new LayerTransform(Point.Zero, new Size(_document.Width, _document.Height))
            : layer.Transform;

        Point start = LayerGeometry.ToPixels(placement, document, _paintGrid.Width, _paintGrid.Height);
        _strokePlacement = placement;

        // Alt sets where the clone stamp reads from rather than starting a stroke.
        if (_tool == CanvasTool.CloneStamp && alt)
        {
            _cloneAnchor = start;
            _cloneOffset = null;
            return;
        }

        BrushSettings settings = Brush with { Mode = _tool.ModeFor() };
        if (_tool == CanvasTool.CloneStamp)
        {
            if (_cloneAnchor is not Point anchor) return;

            // Taken once, when the stroke starts, as upstream does: what the stroke paints is not
            // something it goes on to copy from.
            PixelBuffer? sample = CloneSampleAll
                ? CloneSampling.AllLayers(_document, placement, _paintGrid.Width, _paintGrid.Height)
                : layer.Image?.Retain();
            if (sample is null) return;
            _cloneSample?.Release();
            _cloneSample = sample;

            // Aligned keeps the offset the first stroke established, so a second stroke carries on
            // copying the same thing; unaligned starts again from the anchor each time.
            Point offset = CloneAligned && _cloneOffset is Point kept
                ? kept
                : new Point(anchor.X - start.X, anchor.Y - start.Y);

            _cloneOffset = offset;
            settings = settings with { CloneFrom = new CloneSource(sample, offset) };
        }

        _history.Begin(Name(_tool), _document, id);
        _painting = id;

        // A blank layer has nothing to draw the stroke over, so it gets an empty grid to sit on
        // until the stroke is committed and becomes the layer's pixels.
        _strokeBase?.Release();
        _strokeBase = layer.Image is null
            ? PixelBuffer.Allocate(_paintGrid.Width, _paintGrid.Height)
            : null;

        _stroke = new BrushStroke(layer.Image, _paintGrid.Width, _paintGrid.Height, settings,
                                  Restricted(placement));
        _strokeStart = document;
        _strokeEnd = document;
        ContinueLastStroke(_stroke, shift, id, onMask: false, placement);
        _stroke.Append(start);
        NeedsRedraw = true;
    }

    /// <summary>
    /// Shift paints a straight line on from where the last stroke ended, while the same layer — and
    /// the same one of its pixels or its mask — is the target.
    /// </summary>
    /// <remarks>
    /// Upstream does not ask which brush tool made the last stroke, so neither does this: a line can
    /// be erased on from where the brush stopped.
    /// </remarks>
    private void ContinueLastStroke(BrushStroke stroke, bool shift, Guid layer, bool onMask, LayerTransform placement)
    {
        if (!shift || _lastStrokeEnd is not Point previous || _lastStrokeLayer != layer || _lastStrokeOnMask != onMask)
            return;
        stroke.Append(LayerGeometry.ToPixels(placement, previous, _paintGrid.Width, _paintGrid.Height));
    }

    /// <summary>
    /// A point pulled onto the nearest eighth-turn from where the stroke began.
    /// </summary>
    /// <remarks>
    /// What shift means everywhere: not "horizontal or vertical" but "one of the eight directions",
    /// which is what makes it usable for a diagonal as well as an edge.
    /// </remarks>
    private Point Straightened(Point at)
    {
        if (_strokeStart is not Point start) return at;

        double dx = at.X - start.X, dy = at.Y - start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0) return at;

        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4), MidpointRounding.AwayFromZero)
                       * (Math.PI / 4);

        return new Point(start.X + Math.Cos(angle) * length, start.Y + Math.Sin(angle) * length);
    }

    /// <summary>Adds a corner to the polygonal lasso, closing it when it comes back round.</summary>
    private void AddCorner(Point pixel)
    {
        _polygon ??= [];

        // Back near the first corner closes the outline, which is how every polygonal lasso ends.
        if (_polygon.Count >= 2)
        {
            Point first = _polygon[0];
            double reach = TransformSnap.ToleranceFor(_viewport);
            if (Math.Abs(first.X - pixel.X) <= reach && Math.Abs(first.Y - pixel.Y) <= reach)
            {
                ClosePolygon();
                return;
            }
        }

        _polygon.Add(pixel);
        NeedsRedraw = true;
    }

    /// <summary>Turns the clicked corners into a selection.</summary>
    private void ClosePolygon()
    {
        List<Point>? corners = _polygon;
        _polygon = null;
        NeedsRedraw = true;

        if (corners is not { Count: >= 3 }) return;

        DocumentSelection made = DocumentSelection.Lasso(corners) with { IsAntialiased = SelectionAntialiased };
        (bool adds, bool takesAway) = SelectionOperationForHeldKeys();

        _selection = (_selection is DocumentSelection existing && (adds || takesAway)
            ? takesAway ? existing.Subtracting(made) : existing.Adding(made)
            : made) with { IsAntialiased = SelectionAntialiased };
    }

    /// <summary>
    /// Moves the pixels inside the selection, and the selection with them.
    /// </summary>
    /// <remarks>
    /// One history entry at the end of the drag rather than a floating selection carried between
    /// them: floating is a state the format cannot store, and the result is the same.
    /// </remarks>
    /// <summary>
    /// A press inside the selection with a selection tool: upstream's drag that moves the outline in
    /// New mode, or — with Control, its Command — cuts the pixels and moves them, Alt leaving a copy.
    /// </summary>
    /// <remarks>
    /// With Shift or Alt alone the press still draws, adding or taking away, as a press outside
    /// would. A polygonal outline being clicked out owns its clicks.
    /// </remarks>
    private bool BeginSelectionDrag(Point pixel)
    {
        if (!_tool.Selects() || _polygon is not null) return false;
        if (_selection is not DocumentSelection current || !current.Contains(pixel)) return false;

        if (Win32.IsKeyDown(Win32.VK_CONTROL))
        {
            if (ActiveLayer is { Image: not null })
            {
                _movingFrom = pixel;
                _movingTo = pixel;
                _movingDuplicates = Win32.IsKeyDown(Win32.VK_MENU);
            }
            return true;
        }

        (bool add, bool subtract) = SelectionOperationForHeldKeys();
        if (add || subtract) return false;

        _outlineFrom = pixel;
        _outlineBase = current;
        return true;
    }

    /// <summary>A drag's far corner pulled out to a square round <paramref name="anchor"/>.</summary>
    internal static Point Squared(Point anchor, Point to)
    {
        double dx = to.X - anchor.X, dy = to.Y - anchor.Y;
        double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new Point(anchor.X + (dx < 0 ? -side : side), anchor.Y + (dy < 0 ? -side : side));
    }

    /// <summary>
    /// The shape tool's box: Shift makes it a square (a circle for the ellipse) and Alt draws it out
    /// from the press rather than from a corner, as upstream's and Photoshop's do.
    /// </summary>
    internal static (Point From, Point To) ShapeCorners(Point anchor, Point pointer, bool square, bool fromCentre)
    {
        Point to = square ? Squared(anchor, pointer) : pointer;
        Point from = fromCentre ? new Point(2 * anchor.X - to.X, 2 * anchor.Y - to.Y) : anchor;
        return (from, to);
    }

    /// <summary>
    /// Arrow keys with a selection up, as upstream reads them: Control moves the selected pixels in
    /// any tool, a selection tool moves the outline. False when the key is not theirs.
    /// </summary>
    private bool NudgeSelection(int key, bool control, double step)
    {
        if (_selection is not DocumentSelection selection || _polygon is not null) return false;

        double dx = key == Win32.VK_LEFT ? -step : key == Win32.VK_RIGHT ? step : 0;
        double dy = key == Win32.VK_UP ? -step : key == Win32.VK_DOWN ? step : 0;

        if (control)
        {
            // One history step per press, as a layer nudge is.
            _movingTo = new Point(dx, dy);
            _movingDuplicates = false;
            MoveSelected(Point.Zero);
            return true;
        }

        if (!_tool.Selects()) return false;
        _selection = selection.Transformed(point => new Point(point.X + dx, point.Y + dy));
        return true;
    }

    private void MoveSelected(Point lifted)
    {
        Point dropped = _movingTo;
        bool duplicates = _movingDuplicates;
        _movingFrom = null;
        NeedsRedraw = true;

        if (_document is null || _selection is not DocumentSelection selection) return;
        if (Primary is not Guid id || _document.Layer(id) is not ImageLayer layer) return;
        if (layer.Image is not PixelBuffer pixels) return;

        var offset = new Point(dropped.X - lifted.X, dropped.Y - lifted.Y);
        if (Math.Abs(offset.X) < 0.5 && Math.Abs(offset.Y) < 0.5) return;

        int width = pixels.Width, height = pixels.Height;
        DocumentSelection inLayer = selection.Transformed(
            point => LayerGeometry.ToPixels(layer.Transform, point, width, height));

        Point origin = LayerGeometry.ToPixels(layer.Transform, lifted, width, height);
        Point moved = LayerGeometry.ToPixels(layer.Transform, dropped, width, height);

        _history.Begin(duplicates ? TextKey.HistoryDuplicateSelection : TextKey.HistoryMoveSelection, _document, id);

        PixelBuffer result = SelectionPixels.Move(pixels, inLayer,
                                                  new Point(moved.X - origin.X, moved.Y - origin.Y),
                                                  duplicates);

        _document = _document.Replacing(layer with { Image = result });
        _selection = selection.Transformed(point => new Point(point.X + offset.X, point.Y + offset.Y));
        _history.End(_document, id);
    }

    /// <summary>
    /// Drops the stroke under way. The document is as it was before the press — a mask stroke's
    /// working copy is put back — so closing the history edit records nothing.
    /// </summary>
    private void CancelStroke()
    {
        if (_warp is WarpStroke warp)
        {
            _warp = null;
            warp.Dispose();
            _history.End(_document, _painting);
            NeedsRedraw = true;
            return;
        }

        if (_stroke is not BrushStroke stroke) return;
        _stroke = null;

        if (_strokeOnMask && _maskBefore is ImageLayer before && _document is not null)
            _document = _document.Replacing(before);
        _history.End(_document, _painting);

        stroke.Dispose();
        _strokeBase?.Release();
        _strokeBase = null;
        _cloneSample?.Release();
        _cloneSample = null;
        if (_strokeOnMask)
        {
            _strokeOnMask = false;
            _maskBefore = null;
            _maskShown?.Release();
            _maskShown = null;
            _maskWorking?.Release();
            _maskWorking = null;
        }
        NeedsRedraw = true;
    }

    /// <summary>Commits the stroke as one history entry, healing first if that is what it was.</summary>
    private void EndStroke()
    {
        BrushStroke? stroke = _stroke;
        _stroke = null;
        if (stroke is null) return;

        try
        {
            // Where a Shift-click carries on from, on pixels or mask alike.
            _lastStrokeEnd = _strokeEnd;
            _lastStrokeLayer = _painting;
            _lastStrokeOnMask = _strokeOnMask;

            if (_strokeOnMask)
            {
                EndMaskStroke(stroke);
                return;
            }

            if (_document is null || _document.Layer(_painting) is not ImageLayer layer) return;
            if (stroke.IsEmpty) return;

            if (_tool == CanvasTool.Heal) stroke.Heal((uint)Random.Shared.Next());

            PixelBuffer committed = stroke.Commit();
            _document = _document.Replacing(layer with
            {
                Image = committed,
                Transform = layer.Image is null
                    ? new LayerTransform(Point.Zero, new Size(_document.Width, _document.Height))
                    : layer.Transform,
            });

            _history.End(_document, _painting);
            NeedsRedraw = true;
        }
        finally
        {
            stroke.Dispose();
            _strokeBase?.Release();
            _strokeBase = null;
            _cloneSample?.Release();
            _cloneSample = null;
        }
    }

    /// <summary>Lays down the gradient or shape that was just dragged out.</summary>
    private void EndShape(Point corner)
    {
        Point end = _shapeTo;
        if (_tool == CanvasTool.Gradient)
        {
            EndGradientDrag();
            return;
        }
        _shapeFrom = null;
        NeedsRedraw = true;

        if (_document is null || Primary is not Guid id
            || _document.Layer(id) is not ImageLayer layer) return;

        // A shape is a layer's own pixels and cannot be drawn on a mask.
        if (EditingMask) return;

        if (layer.IsGroup) return;

        int width = layer.Image?.Width ?? _document.Width;
        int height = layer.Image?.Height ?? _document.Height;
        // The selection is carried into this grid, not whichever one the last stroke used.
        _paintGrid = new PixelRect(0, 0, width, height);

        LayerTransform placement = layer.Image is null
            ? new LayerTransform(Point.Zero, new Size(_document.Width, _document.Height))
            : layer.Transform;

        Point from = LayerGeometry.ToPixels(placement, corner, width, height);
        Point to = LayerGeometry.ToPixels(placement, end, width, height);
        DocumentSelection? restricted = Restricted(placement);

        var box = Rect.FromBounds(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y),
                                  Math.Max(from.X, to.X), Math.Max(from.Y, to.Y));
        if (box.IsEmpty) return;
        PixelBuffer drawn = ShapeTool.Draw(layer.Image, width, height, box, Shape, restricted);

        _history.Begin(Name(_tool), _document, id);
        _document = _document.Replacing(layer with { Image = drawn, Transform = placement });
        _history.End(_document, id);
    }

    /// <summary>
    /// Selects what the wand matched, in document space.
    /// </summary>
    /// <remarks>
    /// The kernel works in the layer's pixels and a selection lives on the document, so the outline
    /// crosses over on the way out. It is exact: a placement is affine, and an affine map takes a
    /// straight edge to a straight edge.
    /// </remarks>
    private void Wave(Point document, bool add, bool subtract)
    {
        if (_document is null || Primary is not Guid id
            || _document.Layer(id) is not ImageLayer layer || layer.Image is not PixelBuffer pixels) return;

        DocumentSelection? matched;
        WandOutcome outcome;
        bool alreadyInDocument = Wand.SampleAllLayers;

        if (alreadyInDocument)
        {
            using var backend = new SoftwareRenderBackend();
            using PixelBuffer composite = LayerCompositor.Render(_document, backend);
            (matched, outcome) = MagicWand.Select(composite, document, Wand);
        }
        else
        {
            Point pixel = LayerGeometry.ToPixels(layer.Transform, document, pixels.Width, pixels.Height);
            (matched, outcome) = MagicWand.Select(pixels, pixel, Wand);
        }

        LastWandOutcome = outcome;
        if (matched is null) return;

        DocumentSelection inDocument = (alreadyInDocument
            ? matched
            : matched.Transformed(point => LayerGeometry.ToDocument(layer.Transform, point, pixels.Width, pixels.Height)))
            with { IsAntialiased = SelectionAntialiased };

        _selection = (_selection is DocumentSelection existing && (add || subtract)
            ? subtract ? existing.Subtracting(inDocument) : existing.Adding(inDocument)
            : inDocument) with { IsAntialiased = SelectionAntialiased };

        NeedsRedraw = true;
    }

    /// <summary>What the last wand click came to, for the shell to report.</summary>
    public WandOutcome LastWandOutcome { get; private set; } = WandOutcome.Selected;

    /// <summary>The selection in the layer's own pixels, which is what a tool is held to.</summary>
    private DocumentSelection? Restricted(LayerTransform placement) =>
        _selection?.Transformed(point => LayerGeometry.ToPixels(placement, point,
                                                                _paintGrid.Width > 0 ? _paintGrid.Width : 1,
                                                                _paintGrid.Height > 0 ? _paintGrid.Height : 1));

    /// <summary>What a tool is called, in menus and in the history its edits leave.</summary>
    internal static TextKey Name(CanvasTool tool) => tool switch
    {
        CanvasTool.Move => TextKey.ToolMove,
        CanvasTool.RectangleMarquee => TextKey.ToolRectangleMarquee,
        CanvasTool.EllipseMarquee => TextKey.ToolEllipseMarquee,
        CanvasTool.Lasso => TextKey.ToolLasso,
        CanvasTool.PolygonLasso => TextKey.ToolPolygonLasso,
        CanvasTool.Brush => TextKey.ToolBrush,
        CanvasTool.Eraser => TextKey.ToolEraser,
        CanvasTool.CloneStamp => TextKey.ToolCloneStamp,
        CanvasTool.Blur => TextKey.ToolBlur,
        CanvasTool.Heal => TextKey.ToolHeal,
        CanvasTool.MagicWand => TextKey.ToolMagicWand,
        CanvasTool.Gradient => TextKey.ToolGradient,
        CanvasTool.Shape => TextKey.ToolShape,
        CanvasTool.Crop => TextKey.ToolCrop,
        CanvasTool.Eyedropper => TextKey.ToolEyedropper,
        CanvasTool.Hand => TextKey.ToolHand,
        CanvasTool.Zoom => TextKey.ToolZoom,
        CanvasTool.Idle => TextKey.ToolIdle,
        _ => TextKey.HistoryEdit,
    };

    /// <summary>
    /// Turns the drag that just ended into a selection, joined to whatever was selected already.
    /// </summary>
    /// <remarks>
    /// A click that went nowhere deselects, which is what every editor does and what stops a
    /// selection from becoming something the user cannot get rid of.
    /// </remarks>
    private void Commit(Point start)
    {
        List<Point>? lasso = _lasso;
        Point end = _marqueeTo;
        bool adds = _marqueeAdds, takesAway = _marqueeTakesAway;

        _marqueeFrom = null;
        _lasso = null;
        NeedsRedraw = true;

        var box = Rect.FromBounds(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
                                  Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));

        DocumentSelection? made = _tool switch
        {
            CanvasTool.RectangleMarquee when !box.IsEmpty => DocumentSelection.Rectangle(box),
            CanvasTool.EllipseMarquee when !box.IsEmpty => DocumentSelection.Ellipse(box),
            CanvasTool.Lasso when lasso is { Count: >= 3 } => DocumentSelection.Lasso(lasso),
            _ => null,
        };

        if (made is not null) made = made with { IsAntialiased = SelectionAntialiased };

        if (made is null)
        {
            if (!adds && !takesAway) _selection = null;
            return;
        }

        _selection = (_selection is DocumentSelection existing && (adds || takesAway)
            ? takesAway ? existing.Subtracting(made) : existing.Adding(made)
            : made) with { IsAntialiased = SelectionAntialiased };
    }

    /// <summary>The persistent option, temporarily overridden by Shift and Alt.</summary>
    private (bool Add, bool Subtract) SelectionOperationForHeldKeys()
    {
        if (Win32.IsKeyDown(Win32.VK_MENU)) return (false, true);
        if (Win32.IsKeyDown(Win32.VK_SHIFT)) return (true, false);
        return (SelectionMode == SelectionModeChoice.Add, SelectionMode == SelectionModeChoice.Subtract);
    }

    /// <summary>The wheel turned by <paramref name="notches"/> of its own unit at a point.</summary>
    public void Wheel(Point view, double notches)
    {
        if (_document is null) return;

        // A fifth either way per notch, which is close enough to upstream's feel and lands on whole
        // sizes often enough not to look arbitrary.
        _viewport = _viewport.ZoomedTo(_viewport.Zoom * Math.Pow(1.2, notches), view, _document.Size);
        NeedsRedraw = true;
    }

    /// <summary>A key went down. Returns true when the canvas took it.</summary>
    public bool Key(int key, bool control, bool shift = false, bool alt = false)
    {
        if (_document is null) return false;

        if (FilterKey(key, control))
        {
            NeedsRedraw = true;
            return true;
        }

        // Mid-stroke, Escape drops the stroke and every other key waits, as upstream's canvas does:
        // a tool key taken half-way through would change what the stroke is committed as.
        if (_stroke is not null || _warp is not null)
        {
            if (key == Win32.VK_ESCAPE) CancelStroke();
            return true;
        }

        if (key == Win32.VK_ESCAPE && _tool == CanvasTool.Shape && _shapeFrom is not null)
        {
            _shapeFrom = null;
            NeedsRedraw = true;
            return true;
        }

        if (HasPendingGradient)
        {
            if (key == Win32.VK_RETURN)
            {
                CommitGradient();
                return true;
            }
            if (key == Win32.VK_ESCAPE)
            {
                CancelGradient();
                return true;
            }
        }

        // Floating pixels: Enter lays them down, Escape puts them back, the arrows nudge them; any
        // other key lays them down first and then does what it does.
        if (IsFloating)
        {
            if (key == Win32.VK_RETURN)
            {
                CommitFloating();
                return true;
            }
            if (key == Win32.VK_ESCAPE)
            {
                CancelFloating();
                return true;
            }
            if (key is not (Win32.VK_LEFT or Win32.VK_RIGHT or Win32.VK_UP or Win32.VK_DOWN)) CommitFloating();
        }

        switch (key)
        {
            // Upstream's opacity keys: 1 is 10% ... 9 is 90%, 0 is 100%, two quick digits an exact
            // value. Only where upstream has them — the brushes, the gradient, and Move, where they
            // set the chosen layers' opacity. Shift turns the row into symbols, so it is not a digit.
            case (>= Win32.VK_0 and <= 0x39) or (>= Win32.VK_NUMPAD0 and <= Win32.VK_NUMPAD9)
                when !control && !alt && !shift && UsesOpacityKeys:
                TypeOpacityDigit(key >= Win32.VK_NUMPAD0 ? key - Win32.VK_NUMPAD0 : key - Win32.VK_0,
                                 Environment.TickCount64);
                break;

            case Win32.VK_OEM_PLUS or Win32.VK_ADD when shift && !control:
                CycleBlendMode(1);
                break;

            case Win32.VK_OEM_MINUS or Win32.VK_SUBTRACT when shift && !control:
                CycleBlendMode(-1);
                break;

            case Win32.VK_A when !control:
                _tool = CanvasTool.Idle;
                break;

            // Photoshop's colour keys: X swaps foreground and background, D puts back black and white.
            case Win32.VK_X when !control:
                SwapColors();
                break;

            case Win32.VK_D when !control:
                DefaultColors();
                break;

            case Win32.VK_V when !control:
                _tool = CanvasTool.Move;
                break;

            case Win32.VK_M when !control:
                // As in Photoshop, the marquee key cycles between its two shapes.
                _tool = _tool == CanvasTool.RectangleMarquee
                    ? CanvasTool.EllipseMarquee
                    : CanvasTool.RectangleMarquee;
                break;

            case Win32.VK_L when !control:
                // As with the marquee key, pressing it again offers the other lasso.
                _tool = _tool == CanvasTool.Lasso ? CanvasTool.PolygonLasso : CanvasTool.Lasso;
                break;

            case Win32.VK_RETURN when _polygon is not null:
                ClosePolygon();
                break;

            case Win32.VK_ESCAPE when _polygon is not null:
                _polygon = null;
                break;

            // Backspace takes back the last corner; taking back the only one drops the outline.
            case Win32.VK_BACK when _polygon is not null:
                _polygon.RemoveAt(_polygon.Count - 1);
                if (_polygon.Count == 0) _polygon = null;
                break;

            case Win32.VK_RETURN when _tool == CanvasTool.Crop:
                ApplyCrop();
                break;

            case Win32.VK_ESCAPE when _tool == CanvasTool.Crop && _cropFrame is not null:
                CancelCrop();
                break;

            case Win32.VK_C when !control:
                _tool = CanvasTool.Crop;
                break;

            case Win32.VK_I when !control:
                _tool = CanvasTool.Eyedropper;
                break;

            case Win32.VK_H when !control:
                _tool = CanvasTool.Hand;
                break;

            case Win32.VK_Z when !control:
                _tool = CanvasTool.Zoom;
                break;

            case Win32.VK_B when !control:
                _tool = CanvasTool.Brush;
                break;

            case Win32.VK_E when !control:
                _tool = CanvasTool.Eraser;
                break;

            case Win32.VK_S when !control:
                _tool = CanvasTool.CloneStamp;
                break;

            case Win32.VK_R when !control:
                _tool = CanvasTool.Blur;
                break;

            case Win32.VK_J when !control:
                _tool = CanvasTool.Heal;
                break;

            case Win32.VK_W when !control:
                _tool = CanvasTool.MagicWand;
                break;

            case Win32.VK_G when !control:
                _tool = CanvasTool.Gradient;
                break;

            // Shift+U switches Rectangle and Ellipse only once the Shape tool is up; from any other
            // tool it picks the Shape tool, as plain U does.
            case Win32.VK_U when !control:
                if (shift && _tool == CanvasTool.Shape)
                    Shape = Shape with { Kind = Shape.Kind == ShapeKind.Rectangle ? ShapeKind.Ellipse : ShapeKind.Rectangle };
                else _tool = CanvasTool.Shape;
                break;

            // The bracket keys size the brush tools and, with Shift, step their hardness. Other
            // tools leave them alone, as upstream's do.
            case Win32.VK_OEM_4 or Win32.VK_OEM_6 when _tool.Paints() && _stroke is null && _warp is null:
                Brush = shift
                    ? Brush with { Hardness = SteppedHardness(Brush.Hardness, key == Win32.VK_OEM_6) }
                    : Brush with { Diameter = SteppedDiameter(Brush.Diameter, key == Win32.VK_OEM_6) };
                break;

            // Upstream's arrows: floating pixels and the Move tool nudge layers, a selection moves
            // by its outline or (with Control) its pixels, and in the other tools they do nothing.
            case Win32.VK_LEFT or Win32.VK_RIGHT or Win32.VK_UP or Win32.VK_DOWN when !alt:
            {
                double step = shift ? 10 : 1;
                if (IsFloating) Nudge(key, step);
                else if (NudgeSelection(key, control, step)) { }
                else if (_tool == CanvasTool.Move && !control) Nudge(key, step);
                else return false;
                break;
            }

            default:
                return false;
        }

        // A frame belongs to the Crop tool; leaving the tool drops it, as upstream's does.
        if (_tool != CanvasTool.Crop) _cropFrame = null;
        NeedsRedraw = true;
        return true;
    }

    /// <summary>The first of two quickly typed opacity digits, and when it was typed.</summary>
    private (int Digit, long At)? _pendingOpacityDigit;

    /// <summary>Tools whose number keys set an opacity: upstream's <c>usesOpacityKeys</c>.</summary>
    private bool UsesOpacityKeys => _tool.Paints() || _tool is CanvasTool.Gradient or CanvasTool.Move;

    /// <summary>
    /// One opacity digit. A second digit within 600 ms makes an exact value — 4 then 5 is 45%,
    /// 0 then 5 is 5% — as upstream and Photoshop read them.
    /// </summary>
    internal void TypeOpacityDigit(int digit, long milliseconds)
    {
        if (!UsesOpacityKeys || _stroke is not null || _warp is not null || digit is < 0 or > 9) return;

        int percent = digit == 0 ? 100 : digit * 10;
        if (_pendingOpacityDigit is (int first, long at) && milliseconds - at < 600)
        {
            percent = Math.Max(1, first * 10 + digit);
            _pendingOpacityDigit = null;
        }
        else
        {
            _pendingOpacityDigit = (digit, milliseconds);
        }

        double opacity = percent / 100.0;
        if (_tool == CanvasTool.Gradient) Gradient = Gradient with { Opacity = opacity };
        else if (_tool == CanvasTool.Move) SetChosenOpacity(opacity);
        else Brush = Brush with { Opacity = opacity };
    }

    /// <summary>The chosen layers' opacity, as one history step — and none when nothing changes.</summary>
    private void SetChosenOpacity(double opacity)
    {
        if (_document is not CanvasDocument document) return;
        bool changes = _chosen.Any(id => document.Layer(id) is ImageLayer layer && layer.Opacity != opacity);
        if (!changes) return;

        BeginOpacity();
        SetOpacity(opacity);
        EndOpacity();
    }

    /// <summary>A fifth bigger or smaller, but always at least a pixel, so the smallest sizes stay in reach.</summary>
    internal static double SteppedDiameter(double diameter, bool increase) => Math.Clamp(
        increase ? Math.Max(diameter + 1, Math.Round(diameter * 1.2))
                 : Math.Min(diameter - 1, Math.Round(diameter / 1.2)), 1, 2000);

    /// <summary>Photoshop's 25% hardness steps; 80% goes to 100% or 75%, not 90% or 70%.</summary>
    internal static double SteppedHardness(double hardness, bool increase)
    {
        double quarter = hardness * 4;
        double step = increase ? Math.Floor(quarter + 0.001) + 1 : Math.Ceiling(quarter - 0.001) - 1;
        return Math.Clamp(step, 0, 4) / 4;
    }

    private void CycleBlendMode(int direction)
    {
        if (ActiveLayer is not ImageLayer layer) return;
        LayerBlendMode[] modes = Enum.GetValues<LayerBlendMode>();
        int current = Array.IndexOf(modes, layer.BlendMode);
        SetBlendMode(modes[(current + direction + modes.Length) % modes.Length]);
    }

    /// <summary>
    /// Starts upstream's right-drag on a brush tool: sideways sizes the tip, and with Shift sets its
    /// hardness instead. Not mid-stroke, where the settings are already in the stroke.
    /// </summary>
    public bool BeginBrushAdjust(Point view)
    {
        if (!_tool.Paints() || IsFiltering || _stroke is not null || _warp is not null) return false;
        _brushAdjustFrom = view;
        _brushBeforeAdjust = Brush;
        return true;
    }

    /// <remarks>
    /// Only the horizontal distance counts, as upstream's. Without Shift the circle's edge follows the
    /// pointer — each point moved widens the radius by a point on screen, whatever the zoom. With
    /// Shift the full hardness range is two hundred points. The other value stays as the drag found it.
    /// </remarks>
    public void DragBrushAdjust(Point view, bool shift)
    {
        if (_brushAdjustFrom is not Point from || _brushBeforeAdjust is not BrushSettings before) return;
        double dx = view.X - from.X;
        double perPixel = Math.Max(0.0001, _viewport.PointsPerPixel);
        Brush = shift
            ? before with { Hardness = Math.Clamp(before.Hardness + dx / 200, 0, 1) }
            : before with { Diameter = Math.Clamp(Math.Round(before.Diameter + 2 * dx / perPixel), 1, 2000) };
        NeedsRedraw = true;
    }

    /// <summary>
    /// Whether a drag is on that the view should follow past its edge: the marquee, a moving
    /// selection, the crop frame and the transform handles.
    /// </summary>
    /// <remarks>
    /// Upstream scrolls for the marquee and a moving selection. Crop and transform are added here
    /// because their frames are as often dragged past the edge. Strokes, shapes and the freehand
    /// lasso are left out, as upstream leaves them: a stroke that ran into the edge would keep
    /// painting as the document slid under a still pointer.
    /// </remarks>
    private bool AutoScrolls => _cropDragging || _movingFrom is not null || _drag is not null || _outlineFrom is not null
                                || _marqueeFrom is not null && _lasso is null;

    /// <summary>
    /// How far the view pans this tick for a pointer at <paramref name="view"/>: nothing well inside,
    /// then from two points a tick at the edge margin up to forty for a pointer far past it.
    /// </summary>
    internal Point AutoScrollDelta(Point view)
    {
        const double margin = 12;
        static double Speed(double past) => past <= 0 ? 0 : Math.Min(40, 2 + past * 0.4);
        Size size = _viewport.ViewSize;
        double left = Speed(margin - view.X), right = Speed(view.X - (size.Width - margin));
        double top = Speed(margin - view.Y), bottom = Speed(view.Y - (size.Height - margin));
        // Pointer past the right edge: the document slides left to bring what is beyond into view.
        return new Point(left - right, top - bottom);
    }

    /// <summary>Whether the window should keep a scroll timer running for the drag under way.</summary>
    public bool WantsAutoScroll
    {
        get
        {
            if (_document is null || !AutoScrolls) return false;
            Point delta = AutoScrollDelta(_pointer);
            return delta.X != 0 || delta.Y != 0;
        }
    }

    /// <summary>
    /// One tick of the edge scroll: the view moves, and the drag is replayed at the same pointer so
    /// the corner or frame it holds follows the document. False when there is nothing left to do.
    /// </summary>
    public bool AutoScrollTick(bool shift, bool alt, bool control)
    {
        if (!WantsAutoScroll) return false;
        _viewport = _viewport.Translated(AutoScrollDelta(_pointer));
        PointerMoved(_pointer, shift, alt, control);
        NeedsRedraw = true;
        return true;
    }

    public void EndBrushAdjust()
    {
        _brushAdjustFrom = null;
        _brushBeforeAdjust = null;
    }

    /// <summary>Applies an exact percentage around the centre of the visible canvas.</summary>
    public void SetZoomPercent(double percent)
    {
        if (_document is null || !double.IsFinite(percent)) return;
        _viewport = _viewport.ZoomedTo(percent / 100, _viewport.Center, _document.Size);
        NeedsRedraw = true;
    }

    /// <summary>
    /// Moves the chosen layers a pixel at a time, ten with shift held.
    /// </summary>
    /// <remarks>
    /// Recorded as one history entry per press rather than one per run of presses. Upstream does
    /// the same; holding an arrow down and undoing once would otherwise put back an arbitrary
    /// amount of movement.
    /// </remarks>
    private void Nudge(int key, double step)
    {
        if (_document is null || _chosen.Count == 0) return;

        double dx = key == Win32.VK_LEFT ? -step : key == Win32.VK_RIGHT ? step : 0;
        double dy = key == Win32.VK_UP ? -step : key == Win32.VK_DOWN ? step : 0;

        if (TransformsMask && ActiveLayer is ImageLayer masked && MaskEditing.PlacementOf(masked) is LayerTransform at)
        {
            _history.Begin(TextKey.HistoryTransformMask, _document, Primary);
            _document = _document.Replacing(MaskEditing.WithMaskPlacement(masked,
                at with { Origin = new Point(at.Origin.X + dx, at.Origin.Y + dy) }));
            _history.End(_document, Primary);
            return;
        }

        _history.Begin(TextKey.HistoryMoveLayer, _document, Primary);

        foreach (Guid id in _chosen)
        {
            if (_document.Layer(id) is not ImageLayer layer) continue;
            LayerTransform moved = layer.Transform with
            {
                Origin = new Point(layer.Transform.Origin.X + dx, layer.Transform.Origin.Y + dy),
            };

            if (moved.IsValid) _document = _document.Replacing(MaskEditing.WithTransform(layer, moved));
        }

        _history.End(_document, Primary);
    }

    // MARK: Hit testing

    /// <summary>The topmost drawn layer under a document point.</summary>
    private ImageLayer? Topmost(Point pixel)
    {
        if (_document is null) return null;

        for (int i = _document.Layers.Count - 1; i >= 0; i--)
        {
            ImageLayer layer = _document.Layers[i];
            if (layer.IsGroup || layer.Image is null || !layer.IsVisible) continue;
            if (layer.Transform.Contains(pixel)) return layer;
        }

        return null;
    }

    /// <summary>Which handle is under <paramref name="view"/>, or null for none.</summary>
    private int? HitHandle(LayerTransform transform, Point view)
    {
        if (_document is null) return null;

        CanvasProjection projection = _viewport.Projection(_document.Size);
        double reach = TransformDrag.HandleRadius;

        if (Near(RotationPoint(transform, projection), view, reach)) return RotationHandle;

        for (int i = 0; i < TransformDrag.Handles.Count; i++)
        {
            if (Near(projection.Apply(transform.PointAt(TransformDrag.Handles[i])), view, reach)) return i;
        }

        return null;

        static bool Near(Point a, Point b, double reach) =>
            Math.Abs(a.X - b.X) <= reach && Math.Abs(a.Y - b.Y) <= reach;
    }

    /// <summary>Where the rotation handle sits: off the top edge, along the layer's own up.</summary>
    private static Point RotationPoint(LayerTransform transform, CanvasProjection projection)
    {
        Point top = projection.Apply(transform.PointAt(new Point(0.5, 0)));
        return new Point(top.X + Math.Sin(transform.Radians) * RotationReach,
                         top.Y - Math.Cos(transform.Radians) * RotationReach);
    }

    /// <summary>
    /// The box the handles belong to: one layer's own placement, or the upright box around several.
    /// </summary>
    /// <remarks>
    /// A single layer keeps its own rotation, so its handles turn with it. Several layers share an
    /// upright box and are carried along with it (<see cref="TransformGroup"/>), because rotating
    /// each about its own centre would scatter them.
    /// </remarks>
    private LayerTransform? Box()
    {
        if (_document is null || _chosen.Count == 0) return null;
        if (TransformsMask && ActiveLayer is ImageLayer masked) return MaskEditing.PlacementOf(masked);

        var placements = new List<LayerTransform>(_chosen.Count);
        foreach (Guid id in _chosen)
        {
            if (_document.Layer(id) is ImageLayer layer) placements.Add(layer.Transform);
        }

        if (placements.Count == 0) return null;
        return placements.Count == 1 ? placements[0] : TransformGroup.BoxAround(placements);
    }

    private void Begin(LayerTransform box, TransformDragMode mode, Point pixel)
    {
        if (_document is null) return;

        _originals = [];
        foreach (Guid id in _chosen)
        {
            if (_document.Layer(id) is ImageLayer layer) _originals[id] = layer.Transform;
        }

        _history.Begin(TransformsMask ? TextKey.HistoryTransformMask : TextKey.HistoryTransformLayer, _document, Primary);
        _boxAtStart = box;
        _distorting = null;
        _drag = new TransformDrag
        {
            Original = box,
            Start = pixel,
            Mode = mode,
            OriginalCorners = mode.Kind == TransformDragKind.Distort ? TransformDrag.CornersOf(box) : null,
        };

        _targets = TransformSnap.TargetsFor(_document, _chosen);
    }

    // MARK: The overlay

    private Rect CanvasRect(CanvasDocument document) =>
        Projection(document).Apply(new Rect(0, 0, document.Width, document.Height));

    /// <summary>The selection's outline and handles, and any guide a move just landed on.</summary>
    private void DrawOverlay(ID2D1DeviceContext context)
    {
        if (_document is null) return;

        // Device pixels, like the frame under it, so the overlay is crisp on a dense display.
        CanvasProjection projection = Projection(_document);
        float thickness = (float)_scale;
        float half = (float)(4 * _scale);

        context.BeginDraw();
        context.PushAxisAlignedClip(Raw(_area), AntialiasMode.Aliased);

        using ID2D1SolidColorBrush outline = context.CreateSolidColorBrush(new Color4(0.16f, 0.55f, 1f, 1f));
        using ID2D1SolidColorBrush fill = context.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
        using ID2D1SolidColorBrush guide = context.CreateSolidColorBrush(new Color4(1f, 0.25f, 0.5f, 0.9f));
        using ID2D1SolidColorBrush shadow = context.CreateSolidColorBrush(new Color4(0f, 0f, 0f, 0.65f));

        DrawPixelGrid(context, projection);
        DrawSelection(context, projection, fill, shadow, thickness);
        DrawCrop(context, projection, fill, outline, thickness, half);
        DrawSampleRing(context);

        // Floating pixels always show their handles: they are there to be transformed.
        if (_tool != CanvasTool.Move || Box() is not LayerTransform box || !ShowTransformControls && !IsFloating)
        {
            context.PopAxisAlignedClip();
        context.EndDraw().CheckError();
            return;
        }

        DrawGuides(context, guide, projection, thickness);

        // While a corner is being dragged free the shape is no longer a placement, so the outline
        // follows the corners themselves. The handles stay on the box they started from.
        IReadOnlyList<Point> shape = _distorting ?? TransformDrag.CornersOf(box);
        Point[] corners = [.. shape.Select(corner => projection.Apply(corner))];
        for (int i = 0; i < corners.Length; i++)
        {
            context.DrawLine(Vector(corners[i]), Vector(corners[(i + 1) % corners.Length]),
                             outline, thickness);
        }

        Point rotation = RotationPoint(box, projection);
        Point top = projection.Apply(box.PointAt(new Point(0.5, 0)));
        context.DrawLine(Vector(top), Vector(rotation), outline, thickness);
        Square(context, rotation, half, fill, outline, thickness);

        foreach (Point unit in TransformDrag.Handles)
        {
            Square(context, projection.Apply(box.PointAt(unit)), half, fill, outline, thickness);
        }

        context.PopAxisAlignedClip();
            context.EndDraw().CheckError();
    }

    /// <summary>
    /// Upstream's sample ring round the pointer: a grey band, the colour just taken in its upper half
    /// and the one the press started from in its lower half.
    /// </summary>
    /// <remarks>
    /// Sized in points as upstream's overlay is — a 116-point frame, the band's centre line 43 points
    /// out, 24 wide in grey with the two colours 16 wide inside it — so it reads the same at any zoom.
    /// It is drawn over the frame only and never reaches the document or its history.
    /// </remarks>
    private void DrawSampleRing(ID2D1DeviceContext context)
    {
        if (!_sampling || !ShowSampleRing || _sampleRingAt is not Point at) return;

        var centre = new Vector2((float)(_origin.X + at.X * _scale), (float)(_origin.Y + at.Y * _scale));
        float radius = (float)(43 * _scale);
        var ring = new Ellipse(centre, radius, radius);

        using ID2D1SolidColorBrush grey = context.CreateSolidColorBrush(new Color4(0.45f, 0.45f, 0.45f, 1));
        context.DrawEllipse(ring, grey, (float)(24 * _scale));

        Rgba taken = _samplingBackground ? BackgroundColor : ForegroundColor;
        float reach = (float)(58 * _scale);
        foreach ((Rgba colour, bool upper) in new[] { (taken, true), (_samplingOriginal, false) })
        {
            using ID2D1SolidColorBrush brush = context.CreateSolidColorBrush(
                new Color4(colour.R / 255f, colour.G / 255f, colour.B / 255f, 1));
            context.PushAxisAlignedClip(new Vortice.RawRectF(centre.X - reach, upper ? centre.Y - reach : centre.Y,
                                                             centre.X + reach, upper ? centre.Y : centre.Y + reach),
                                        AntialiasMode.Aliased);
            context.DrawEllipse(ring, brush, (float)(16 * _scale));
            context.PopAxisAlignedClip();
        }
    }

    /// <summary>
    /// The selection's outline, and the one being dragged out right now.
    /// </summary>
    /// <remarks>
    /// Two passes, dark under light, so the outline reads on a white sheet and on a dark photograph
    /// alike. Upstream animates it; a still outline says the same thing and costs no timer.
    /// </remarks>
    private void DrawSelection(ID2D1DeviceContext context, CanvasProjection projection,
                               ID2D1Brush light, ID2D1Brush dark, float thickness)
    {
        if (_selection is DocumentSelection selection)
        {
            // While the pixels inside are being dragged, the outline goes with them.
            double dx = _movingFrom is Point from ? _movingTo.X - from.X : 0;
            double dy = _movingFrom is Point at ? _movingTo.Y - at.Y : 0;

            foreach (SelectionShape shape in selection.Shapes)
            {
                foreach (SelectionLoop loop in shape.Loops)
                {
                    Outline([.. loop.Points.Select(point => new Point(point.X + dx, point.Y + dy))]);
                }
            }
        }

        // The corners clicked so far, which are not a selection until the outline closes.
        if (_polygon is { Count: >= 2 } corners) Outline(corners);

        // The brush, shown where it would land and at the size it would be.
        if (_tool.Paints() && _document is not null && Primary is Guid id
            && _document.Layer(id) is ImageLayer target)
        {
            int width = target.Image?.Width ?? _document.Width;
            int height = target.Image?.Height ?? _document.Height;
            LayerTransform placement = target.Image is null
                ? new LayerTransform(Point.Zero, new Size(_document.Width, _document.Height))
                : target.Transform;

            Point centre = LayerGeometry.ToPixels(placement,
                                                  _viewport.DocumentPoint(_pointer, _document.Size),
                                                  width, height);

            var ring = new List<Point>(24);
            for (int i = 0; i < 24; i++)
            {
                double angle = i * 2 * Math.PI / 24;
                ring.Add(LayerGeometry.ToDocument(placement,
                    new Point(centre.X + Math.Cos(angle) * Brush.Radius,
                              centre.Y + Math.Sin(angle) * Brush.Radius), width, height));
            }

            Outline(ring);
        }

        if (_marqueeFrom is Point start) Outline(InProgress(start));

        // The gradient's line and the shape's outline, while they are being dragged out.
        if (_gradientFrom is Point gradientStart)
        {
            Outline([gradientStart, _gradientTo]);
            Handle(gradientStart);
            Handle(_gradientTo);
        }
        else if (_shapeFrom is Point corner)
        {
            var box = Rect.FromBounds(Math.Min(corner.X, _shapeTo.X), Math.Min(corner.Y, _shapeTo.Y),
                                      Math.Max(corner.X, _shapeTo.X), Math.Max(corner.Y, _shapeTo.Y));
            if (!box.IsEmpty) Outline(ShapeTool.Outline(box, Shape).Shapes[0].Loops[0].Points);
        }

        void Handle(Point point) => Square(context, projection.Apply(point), (float)(4 * _scale), light, dark, thickness);

        void Outline(IReadOnlyList<Point> points)
        {
            if (points.Count < 2) return;

            for (int pass = 0; pass < 2; pass++)
            {
                ID2D1Brush brush = pass == 0 ? dark : light;
                float width = pass == 0 ? thickness * 3 : thickness;

                for (int i = 0; i < points.Count; i++)
                {
                    Point a = projection.Apply(points[i]);
                    Point b = projection.Apply(points[(i + 1) % points.Count]);
                    context.DrawLine(Vector(a), Vector(b), brush, width);
                }
            }
        }
    }

    /// <summary>The outline of the drag in progress, in document pixels.</summary>
    private IReadOnlyList<Point> InProgress(Point start)
    {
        if (_lasso is List<Point> lasso) return lasso;

        var box = Rect.FromBounds(Math.Min(start.X, _marqueeTo.X), Math.Min(start.Y, _marqueeTo.Y),
                                  Math.Max(start.X, _marqueeTo.X), Math.Max(start.Y, _marqueeTo.Y));

        return _tool == CanvasTool.EllipseMarquee
            ? DocumentSelection.EllipseLoop(box).Points
            : DocumentSelection.RectangleLoop(box).Points;
    }

    private void DrawGuides(ID2D1DeviceContext context, ID2D1Brush brush,
                            CanvasProjection projection, float thickness)
    {
        if (_document is null) return;
        Rect canvas = CanvasRect(_document);

        if (_snap.X is double x)
        {
            float at = (float)projection.Apply(new Point(x, 0)).X;
            context.DrawLine(new Vector2(at, (float)canvas.MinY), new Vector2(at, (float)canvas.MaxY),
                             brush, thickness);
        }

        if (_snap.Y is double y)
        {
            float at = (float)projection.Apply(new Point(0, y)).Y;
            context.DrawLine(new Vector2((float)canvas.MinX, at), new Vector2((float)canvas.MaxX, at),
                             brush, thickness);
        }
    }

    private static void Square(ID2D1DeviceContext context, Point centre, float half,
                               ID2D1Brush fill, ID2D1Brush outline, float thickness)
    {
        var box = new Vortice.RawRectF((float)centre.X - half, (float)centre.Y - half,
                                       (float)centre.X + half, (float)centre.Y + half);
        context.FillRectangle(box, fill);
        context.DrawRectangle(box, outline, thickness);
    }

    private static Vector2 Vector(Point point) => new((float)point.X, (float)point.Y);

    private static Vortice.RawRectF Raw(Rect rect) =>
        new((float)rect.MinX, (float)rect.MinY, (float)rect.MaxX, (float)rect.MaxY);

    public void Dispose()
    {
        CancelGradient();
        _stroke?.Dispose();
        _strokeBase?.Release();
        _warp?.Dispose();
        _maskShown?.Release();
        _maskWorking?.Release();
        _cloneSample?.Release();
        _preview?.Dispose();
        ReleaseSource();
        ReleaseComposite();
        CloseAllTabs();
        _distortPreview?.Dispose();
        _checker?.Dispose();
        _checkerBitmap?.Dispose();
        _surface?.Dispose();
        _backend.Dispose();
    }
}
