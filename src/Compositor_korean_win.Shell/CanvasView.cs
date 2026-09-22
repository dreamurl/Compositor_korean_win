using System.Numerics;
using Compositor_korean_win.Core;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Rect = Compositor_korean_win.Core.Rect;

namespace Compositor_korean_win.Shell;

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

    private Guid? _selected;
    private TransformDrag? _drag;
    private Guid _dragging;
    private SnapGuides _targets = SnapGuides.None;
    private SnapResult _snap;
    private bool _panning;
    private Point _panFrom;

    public CanvasView(GraphicsDevice device)
    {
        _device = device;
        _backend = new Direct2DBackend(device);
    }

    public CanvasDocument? Document => _document;

    public CanvasViewport Viewport => _viewport;

    public Guid? Selected => _selected;

    /// <summary>Set whenever something changed that the window has not drawn yet.</summary>
    public bool NeedsRedraw { get; private set; } = true;

    public void Open(CanvasDocument document)
    {
        _document = document;
        _history.Reset();
        _selected = document.Layers.FirstOrDefault(layer => !layer.IsGroup && layer.Image is not null)?.Id;
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

        if (_selected is Guid id && _document.Layer(id) is ImageLayer chosen
            && HitHandle(chosen.Transform, view) is int handle)
        {
            Begin(chosen, handle == RotationHandle
                ? TransformDragMode.Rotate
                : TransformDragMode.Resize(handle), pixel);
            return;
        }

        ImageLayer? hit = Topmost(pixel);
        _selected = hit?.Id;
        NeedsRedraw = true;

        if (hit is not null) Begin(hit, TransformDragMode.Move, pixel);
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

        if (_drag is not TransformDrag drag || _document.Layer(_dragging) is not ImageLayer layer) return;

        // Dragging, scaling and rotating land on whole pixels and whole degrees; a value typed into
        // the inspector stays exactly as it was typed.
        Point pixel = _viewport.DocumentPoint(view, _document.Size);
        LayerTransform draft = drag.Updated(pixel, lockRatio: false, shift, alt).Rounded();

        // Moving snaps to the canvas and to the other layers; resizing and rotating are left alone,
        // and holding control drags free of the pull altogether.
        _snap = default;
        if (drag.Mode.Kind == TransformDragKind.Move && !control)
        {
            (draft, _snap) = TransformSnap.Move(draft, _targets, TransformSnap.ToleranceFor(_viewport));
        }

        _document = _document.Replacing(layer with { Transform = draft });
        NeedsRedraw = true;
    }

    public void PointerUp()
    {
        _panning = false;
        if (_drag is null)
        {
            _snap = default;
            return;
        }

        _drag = null;
        _snap = default;
        _history.End(_document, _selected);
        NeedsRedraw = true;
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

            case Win32.VK_Z when control:
                Apply(_history.Undo());
                break;

            case Win32.VK_Y when control:
                Apply(_history.Redo());
                break;

            default:
                return false;
        }

        NeedsRedraw = true;
        return true;

        void Apply(HistorySnapshot? snapshot)
        {
            if (snapshot is null) return;
            _document = snapshot.Document;
            _selected = snapshot.ActiveLayerId;
        }
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

    private void Begin(ImageLayer layer, TransformDragMode mode, Point pixel)
    {
        if (_document is null) return;

        _history.Begin("레이어 변형", _document, layer.Id);
        _dragging = layer.Id;
        _drag = new TransformDrag { Original = layer.Transform, Start = pixel, Mode = mode };
        _targets = TransformSnap.TargetsFor(_document, new HashSet<Guid> { layer.Id });
    }

    // MARK: The overlay

    private Rect CanvasRect(CanvasDocument document) =>
        _viewport.DeviceProjection(document.Size).Apply(new Rect(0, 0, document.Width, document.Height));

    /// <summary>The selection's outline and handles, and any guide a move just landed on.</summary>
    private void DrawOverlay(ID2D1DeviceContext context)
    {
        if (_document is null) return;
        if (_selected is not Guid id || _document.Layer(id) is not ImageLayer layer) return;

        // Device pixels, like the frame under it, so the overlay is crisp on a dense display.
        CanvasProjection projection = _viewport.DeviceProjection(_document.Size);
        float thickness = (float)_scale;
        float half = (float)(4 * _scale);

        context.BeginDraw();

        using ID2D1SolidColorBrush outline = context.CreateSolidColorBrush(new Color4(0.16f, 0.55f, 1f, 1f));
        using ID2D1SolidColorBrush fill = context.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
        using ID2D1SolidColorBrush guide = context.CreateSolidColorBrush(new Color4(1f, 0.25f, 0.5f, 0.9f));

        DrawGuides(context, guide, projection, thickness);

        Point[] corners = [.. TransformDrag.CornersOf(layer.Transform).Select(corner => projection.Apply(corner))];
        for (int i = 0; i < corners.Length; i++)
        {
            context.DrawLine(Vector(corners[i]), Vector(corners[(i + 1) % corners.Length]),
                             outline, thickness);
        }

        Point rotation = RotationPoint(layer.Transform, projection);
        Point top = projection.Apply(layer.Transform.PointAt(new Point(0.5, 0)));
        context.DrawLine(Vector(top), Vector(rotation), outline, thickness);
        Square(context, rotation, half, fill, outline, thickness);

        foreach (Point unit in TransformDrag.Handles)
        {
            Square(context, projection.Apply(layer.Transform.PointAt(unit)), half, fill, outline, thickness);
        }

        context.EndDraw().CheckError();
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
