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
}

/// <summary>Which tools make a stroke rather than a drag or a click.</summary>
internal static class CanvasTools
{
    public static bool Paints(this CanvasTool tool) => tool
        is CanvasTool.Brush or CanvasTool.Eraser or CanvasTool.CloneStamp
        or CanvasTool.Blur or CanvasTool.Heal;

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
internal sealed class CanvasView : IDisposable
{
    /// <summary>Where the rotation handle sits above the top edge, in view points.</summary>
    private const double RotationReach = 28;

    /// <summary>The handle index the rotation handle answers to.</summary>
    private const int RotationHandle = 8;

    private readonly GraphicsDevice _device;
    private readonly Direct2DBackend _backend;
    private readonly DocumentHistory _history = new();

    private IRenderSurface? _surface;
    private CanvasDocument? _document;
    private CanvasViewport _viewport = new();
    private double _scale = 1;

    private readonly HashSet<Guid> _chosen = [];
    private TransformDrag? _drag;
    private Dictionary<Guid, LayerTransform> _originals = [];
    private LayerTransform? _boxAtStart;
    private IReadOnlyList<Point>? _distorting;
    private SnapGuides _targets = SnapGuides.None;
    private SnapResult _snap;
    private bool _panning;
    private Point _panFrom;

    private CanvasTool _tool = CanvasTool.Move;
    private DocumentSelection? _selection;

    private BrushStroke? _stroke;
    private PixelBuffer? _strokeBase;
    private Point? _strokeStart;
    private Guid _painting;
    private PixelRect _paintGrid;
    private Point? _cloneAnchor;
    private Point? _cloneOffset;
    private Point? _shapeFrom;
    private Point _shapeTo;
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

    public GradientSettings Gradient { get; set; } = new();

    public WandSettings Wand { get; set; } = new();

    /// <summary>
    /// Whether the clone stamp keeps one offset between strokes, as Photoshop's default does.
    /// </summary>
    public bool CloneAligned { get; set; } = true;

    /// <summary>Set whenever something changed that the window has not drawn yet.</summary>
    public bool NeedsRedraw { get; private set; } = true;

    public void Open(CanvasDocument document)
    {
        _document = document;
        _history.Reset();
        _chosen.Clear();
        if (document.Layers.LastOrDefault(layer => !layer.IsGroup && layer.Image is not null) is ImageLayer top)
            _chosen.Add(top.Id);
        _viewport = _viewport.Fit(document.Size);
        NeedsRedraw = true;
    }

    /// <summary>
    /// The window changed size. <paramref name="scale"/> is device pixels per point.
    /// </summary>
    public void Resize(int pixelWidth, int pixelHeight, double scale)
    {
        _scale = Math.Max(1, scale);

        // The back buffer is a new bitmap after a resize, so the surface borrowing it is stale.
        _surface?.Dispose();
        _surface = null;

        _viewport = _viewport.Resized(new Size(pixelWidth / _scale, pixelHeight / _scale),
                                      _scale, _document?.Size);
        NeedsRedraw = true;
    }

    /// <summary>A point in device pixels, as the mouse messages give it, in view points.</summary>
    public Point ToView(int x, int y) => new(x / _scale, y / _scale);

    public void Render()
    {
        ID2D1DeviceContext context = _device.D2DContext;
        int width = Math.Max(1, (int)Math.Round(_viewport.DeviceViewSize.Width, MidpointRounding.AwayFromZero));
        int height = Math.Max(1, (int)Math.Round(_viewport.DeviceViewSize.Height, MidpointRounding.AwayFromZero));

        // The desk the document lies on, and the sheet itself. A transparent layer shows the sheet
        // rather than a checkerboard for now; the checkerboard is UI work and belongs with M6.
        context.BeginDraw();
        context.Clear(new Color4(0.15f, 0.15f, 0.16f, 1.0f));

        if (_document is not null)
        {
            using ID2D1SolidColorBrush paper = context.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
            context.FillRectangle(Raw(CanvasRect(_document)), paper);
        }

        context.EndDraw().CheckError();

        if (_document is not null)
        {
            _surface ??= _backend.CreateWindowSurface(width, height);
            LayerCompositor.DrawView(_document, _viewport, _surface, _backend, Live());
            DrawOverlay(context);
        }

        _device.Present();
        NeedsRedraw = false;
    }

    // MARK: Input

    /// <summary>A button went down at <paramref name="view"/>, in view points.</summary>
    public void PointerDown(Point view, bool pan)
    {
        if (_document is null) return;

        if (pan)
        {
            _panning = true;
            _panFrom = view;
            return;
        }

        Point pixel = _viewport.DocumentPoint(view, _document.Size);

        if (_tool == CanvasTool.PolygonLasso)
        {
            AddCorner(pixel);
            return;
        }

        if (_tool.Paints())
        {
            BeginStroke(pixel, alt: Win32.IsKeyDown(Win32.VK_MENU));
            return;
        }

        if (_tool.DragsOutAShape())
        {
            _shapeFrom = pixel;
            _shapeTo = pixel;
            NeedsRedraw = true;
            return;
        }

        if (_tool == CanvasTool.MagicWand)
        {
            Wave(pixel, add: Win32.IsKeyDown(Win32.VK_SHIFT), subtract: Win32.IsKeyDown(Win32.VK_MENU));
            return;
        }

        if (_tool != CanvasTool.Move)
        {
            _marqueeFrom = pixel;
            _marqueeTo = pixel;
            _lasso = _tool == CanvasTool.Lasso ? [pixel] : null;
            _marqueeAdds = Win32.IsKeyDown(Win32.VK_SHIFT);
            _marqueeTakesAway = Win32.IsKeyDown(Win32.VK_MENU);
            NeedsRedraw = true;
            return;
        }

        bool shift = Win32.IsKeyDown(Win32.VK_SHIFT);
        bool control = Win32.IsKeyDown(Win32.VK_CONTROL);
        bool alt = Win32.IsKeyDown(Win32.VK_MENU);

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
        if (Box() is LayerTransform box && HitHandle(box, view) is int handle)
        {
            TransformDragMode mode = handle == RotationHandle
                ? TransformDragMode.Rotate
                : TransformDragMode.Resize(handle);

            if (control && handle != RotationHandle && _chosen.Count == 1)
            {
                Begin(box, TransformDragMode.Distort(handle), pixel);
                return;
            }

            Begin(box, mode, pixel);
            return;
        }

        ImageLayer? hit = Topmost(pixel);
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
            copies.Add(layer with { Id = Guid.NewGuid(), Name = layer.Name + " 복사" });
        }

        if (copies.Count == 0) return;

        _history.Begin("레이어 복제", _document, copies[0].Id);
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

        if (_stroke is BrushStroke stroke && _document.Layer(_painting) is ImageLayer painted)
        {
            Point at = _viewport.DocumentPoint(view, _document.Size);
            if (shift) at = Straightened(at);

            stroke.Append(LayerGeometry.ToPixels(painted.Transform, at,
                                                 _paintGrid.Width, _paintGrid.Height));
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
            _shapeTo = _viewport.DocumentPoint(view, _document.Size);
            NeedsRedraw = true;
            return;
        }

        if (_marqueeFrom is not null)
        {
            _marqueeTo = _viewport.DocumentPoint(view, _document.Size);

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

        // A distortion has no placement to show yet: the corners move now and the pixels are
        // resampled into them when the button comes up.
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
            _document = _document.Replacing(single with { Transform = draft });
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
            _document = _document.Replacing(layer with { Transform = placement });
        }
    }

    public void PointerUp()
    {
        _panning = false;

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

        if (corners is null || _document is null) return;
        if (_chosen.Count != 1 || _document.Layer(_chosen.First()) is not ImageLayer layer) return;
        if (layer.Image is not PixelBuffer image) return;

        (PixelBuffer Pixels, LayerTransform Placement)? warped = QuadWarp.Resample(image, corners);
        if (warped is null) return;

        _document = _document.Replacing(
            layer with { Image = warped.Value.Pixels, Transform = warped.Value.Placement });
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
    private LiveEdit? Live()
    {
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
    private void BeginStroke(Point document, bool alt)
    {
        if (_document is null || Primary is not Guid id
            || _document.Layer(id) is not ImageLayer layer || layer.IsGroup) return;

        _paintGrid = layer.Image is PixelBuffer pixels
            ? new PixelRect(0, 0, pixels.Width, pixels.Height)
            : new PixelRect(0, 0, _document.Width, _document.Height);

        LayerTransform placement = layer.Image is null
            ? new LayerTransform(Point.Zero, new Size(_document.Width, _document.Height))
            : layer.Transform;

        Point start = LayerGeometry.ToPixels(placement, document, _paintGrid.Width, _paintGrid.Height);

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
            if (_cloneAnchor is not Point anchor || layer.Image is not PixelBuffer sample) return;

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
        _stroke.Append(start);
        NeedsRedraw = true;
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

        DocumentSelection made = DocumentSelection.Lasso(corners);
        bool adds = Win32.IsKeyDown(Win32.VK_SHIFT), takesAway = Win32.IsKeyDown(Win32.VK_MENU);

        _selection = _selection is DocumentSelection existing && (adds || takesAway)
            ? takesAway ? existing.Subtracting(made) : existing.Adding(made)
            : made;
    }

    /// <summary>
    /// Moves the pixels inside the selection, and the selection with them.
    /// </summary>
    /// <remarks>
    /// One history entry at the end of the drag rather than a floating selection carried between
    /// them: floating is a state the format cannot store, and the result is the same.
    /// </remarks>
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

        _history.Begin(duplicates ? "선택 영역 복제" : "선택 영역 이동", _document, id);

        PixelBuffer result = SelectionPixels.Move(pixels, inLayer,
                                                  new Point(moved.X - origin.X, moved.Y - origin.Y),
                                                  duplicates);

        _document = _document.Replacing(layer with { Image = result });
        _selection = selection.Transformed(point => new Point(point.X + offset.X, point.Y + offset.Y));
        _history.End(_document, id);
    }

    /// <summary>Commits the stroke as one history entry, healing first if that is what it was.</summary>
    private void EndStroke()
    {
        BrushStroke? stroke = _stroke;
        _stroke = null;
        if (stroke is null) return;

        try
        {
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
        }
    }

    /// <summary>Lays down the gradient or shape that was just dragged out.</summary>
    private void EndShape(Point corner)
    {
        Point end = _shapeTo;
        _shapeFrom = null;
        NeedsRedraw = true;

        if (_document is null || Primary is not Guid id
            || _document.Layer(id) is not ImageLayer layer || layer.IsGroup) return;

        int width = layer.Image?.Width ?? _document.Width;
        int height = layer.Image?.Height ?? _document.Height;

        LayerTransform placement = layer.Image is null
            ? new LayerTransform(Point.Zero, new Size(_document.Width, _document.Height))
            : layer.Transform;

        Point from = LayerGeometry.ToPixels(placement, corner, width, height);
        Point to = LayerGeometry.ToPixels(placement, end, width, height);
        DocumentSelection? restricted = Restricted(placement);

        PixelBuffer drawn;
        if (_tool == CanvasTool.Gradient)
        {
            drawn = GradientTool.Draw(layer.Image, width, height, from, to, Gradient, restricted);
        }
        else
        {
            var box = Rect.FromBounds(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y),
                                      Math.Max(from.X, to.X), Math.Max(from.Y, to.Y));
            if (box.IsEmpty) return;
            drawn = ShapeTool.Draw(layer.Image, width, height, box, Shape, restricted);
        }

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

        Point pixel = LayerGeometry.ToPixels(layer.Transform, document, pixels.Width, pixels.Height);
        (DocumentSelection? matched, WandOutcome outcome) = MagicWand.Select(pixels, pixel, Wand);

        LastWandOutcome = outcome;
        if (matched is null) return;

        DocumentSelection inDocument = matched.Transformed(
            point => LayerGeometry.ToDocument(layer.Transform, point, pixels.Width, pixels.Height));

        _selection = _selection is DocumentSelection existing && (add || subtract)
            ? subtract ? existing.Subtracting(inDocument) : existing.Adding(inDocument)
            : inDocument;

        NeedsRedraw = true;
    }

    /// <summary>What the last wand click came to, for the shell to report.</summary>
    public WandOutcome LastWandOutcome { get; private set; } = WandOutcome.Selected;

    /// <summary>The selection in the layer's own pixels, which is what a tool is held to.</summary>
    private DocumentSelection? Restricted(LayerTransform placement) =>
        _selection?.Transformed(point => LayerGeometry.ToPixels(placement, point,
                                                                _paintGrid.Width > 0 ? _paintGrid.Width : 1,
                                                                _paintGrid.Height > 0 ? _paintGrid.Height : 1));

    private static string Name(CanvasTool tool) => tool switch
    {
        CanvasTool.Brush => "브러시",
        CanvasTool.Eraser => "지우개",
        CanvasTool.CloneStamp => "복제 도장",
        CanvasTool.Blur => "흐리게",
        CanvasTool.Heal => "스팟 힐링",
        CanvasTool.Gradient => "그라디언트",
        CanvasTool.Shape => "셰이프",
        _ => "편집",
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

        if (made is null)
        {
            if (!adds && !takesAway) _selection = null;
            return;
        }

        _selection = _selection is DocumentSelection existing && (adds || takesAway)
            ? takesAway ? existing.Subtracting(made) : existing.Adding(made)
            : made;
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
    public bool Key(int key, bool control)
    {
        if (_document is null) return false;

        switch (key)
        {
            case Win32.VK_0 when control:
                _viewport = _viewport.Fit(_document.Size);
                break;

            case Win32.VK_1 when control:
                _viewport = _viewport.ZoomedTo(1, _viewport.Center, _document.Size);
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

            case Win32.VK_U when !control:
                _tool = CanvasTool.Shape;
                break;

            // The bracket keys size the brush, as they do everywhere else.
            case Win32.VK_OEM_4 or Win32.VK_OEM_6:
                Brush = Brush with
                {
                    Diameter = Math.Clamp(key == Win32.VK_OEM_4 ? Brush.Diameter / 1.25
                                                                : Brush.Diameter * 1.25, 1, 2000),
                };
                break;

            case Win32.VK_A when control:
                _selection = DocumentSelection.Rectangle(new Rect(0, 0, _document.Width, _document.Height));
                break;

            case Win32.VK_D when control:
                _selection = null;
                break;

            case Win32.VK_LEFT or Win32.VK_RIGHT or Win32.VK_UP or Win32.VK_DOWN:
                Nudge(key, Win32.IsKeyDown(Win32.VK_SHIFT) ? 10 : 1);
                break;

            case Win32.VK_Z when control:
                Step(_history.Undo());
                break;

            case Win32.VK_Y when control:
                Step(_history.Redo());
                break;

            default:
                return false;
        }

        NeedsRedraw = true;
        return true;

        void Step(HistorySnapshot? snapshot)
        {
            if (snapshot is null) return;
            _document = snapshot.Document;
            _chosen.Clear();
            if (snapshot.ActiveLayerId is Guid active) _chosen.Add(active);
        }
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

        _history.Begin("레이어 이동", _document, Primary);

        foreach (Guid id in _chosen)
        {
            if (_document.Layer(id) is not ImageLayer layer) continue;
            LayerTransform moved = layer.Transform with
            {
                Origin = new Point(layer.Transform.Origin.X + dx, layer.Transform.Origin.Y + dy),
            };

            if (moved.IsValid) _document = _document.Replacing(layer with { Transform = moved });
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

        _history.Begin("레이어 변형", _document, Primary);
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
        _viewport.DeviceProjection(document.Size).Apply(new Rect(0, 0, document.Width, document.Height));

    /// <summary>The selection's outline and handles, and any guide a move just landed on.</summary>
    private void DrawOverlay(ID2D1DeviceContext context)
    {
        if (_document is null) return;

        // Device pixels, like the frame under it, so the overlay is crisp on a dense display.
        CanvasProjection projection = _viewport.DeviceProjection(_document.Size);
        float thickness = (float)_scale;
        float half = (float)(4 * _scale);

        context.BeginDraw();

        using ID2D1SolidColorBrush outline = context.CreateSolidColorBrush(new Color4(0.16f, 0.55f, 1f, 1f));
        using ID2D1SolidColorBrush fill = context.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
        using ID2D1SolidColorBrush guide = context.CreateSolidColorBrush(new Color4(1f, 0.25f, 0.5f, 0.9f));
        using ID2D1SolidColorBrush shadow = context.CreateSolidColorBrush(new Color4(0f, 0f, 0f, 0.65f));

        DrawSelection(context, projection, fill, shadow, thickness);

        if (_tool != CanvasTool.Move || Box() is not LayerTransform box)
        {
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

        context.EndDraw().CheckError();
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
        if (_shapeFrom is Point corner)
        {
            if (_tool == CanvasTool.Gradient)
            {
                Outline([corner, _shapeTo]);
            }
            else
            {
                var box = Rect.FromBounds(Math.Min(corner.X, _shapeTo.X), Math.Min(corner.Y, _shapeTo.Y),
                                          Math.Max(corner.X, _shapeTo.X), Math.Max(corner.Y, _shapeTo.Y));
                if (!box.IsEmpty) Outline(ShapeTool.Outline(box, Shape).Shapes[0].Loops[0].Points);
            }
        }

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
        _stroke?.Dispose();
        _strokeBase?.Release();
        _surface?.Dispose();
        _backend.Dispose();
    }
}
