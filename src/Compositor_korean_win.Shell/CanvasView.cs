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
            LayerCompositor.DrawView(_document, _viewport, _surface, _backend);
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
                _tool = CanvasTool.Lasso;
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
            foreach (SelectionShape shape in selection.Shapes)
            {
                foreach (SelectionLoop loop in shape.Loops) Outline(loop.Points);
            }
        }

        if (_marqueeFrom is Point start) Outline(InProgress(start));

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
        _surface?.Dispose();
        _backend.Dispose();
    }
}
