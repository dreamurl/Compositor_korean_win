using System.Numerics;
using Compositor_korean_win.Core;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Point = Compositor_korean_win.Core.Point;
using Size = Compositor_korean_win.Core.Size;

namespace Compositor_korean_win.Shell;

/// <summary>What the Move tool's handles do — Edit › Transform's modes.</summary>
internal enum TransformHandleMode
{
    /// <summary>Resize and rotate, with Ctrl for a free corner: upstream's handles.</summary>
    Free,
    Skew,
    Distort,
    Perspective,

    /// <summary>The warp grid is up (<see cref="CanvasView.MeshWarping"/>).</summary>
    Warp,
}

/// <summary>
/// Photoshop's Edit › Transform modes, its Warp grid and Filter › Liquify: tools upstream does not
/// have, added at a user's request.
/// </summary>
/// <remarks>
/// <para>
/// Skew, Distort and Perspective are a mode of the Move tool's handles, so a mouse alone reaches
/// what Ctrl-dragging a handle did before. Each drag resamples the layer when it ends, as a
/// distortion always has (QuadWarp). The mode lasts until Enter, Escape or another tool.
/// </para>
/// <para>
/// Warp and Liquify are sessions, like a filter being set: the document holds still — the menus
/// that would change it grey out through <see cref="CanEdit"/> — while the canvas shows the result
/// (WarpPreview, LiquifyPreview), and Enter or OK lays it down as one step while Escape or Cancel
/// leaves the layer as it was.
/// </para>
/// </remarks>
internal sealed partial class CanvasView
{
    private TransformHandleMode _handleMode;

    /// <summary>What a drag on a handle does now.</summary>
    public TransformHandleMode HandleMode => _tool == CanvasTool.Move ? _handleMode : TransformHandleMode.Free;

    /// <summary>Whether Skew, Distort, Perspective or Warp can start: one layer of pixels, or floating pixels.</summary>
    public bool CanChangeShape =>
        _document is not null && !IsFiltering && _meshWarp is null && _liquify is null && _stroke is null && _drag is null
        && EditingText is null && !TransformsMask && _chosen.Count == 1
        && ActiveLayer is { IsGroup: false, Adjustment: null, Image: not null };

    /// <summary>Edit › Transform's Scale and Rotate: the layer's handles, as Ctrl+T has them.</summary>
    public void StartFreeTransform()
    {
        Transform();
        _handleMode = TransformHandleMode.Free;
        NeedsRedraw = true;
    }

    /// <summary>Edit › Transform's Skew, Distort, Perspective or Warp on the chosen layer.</summary>
    public void StartTransformMode(TransformHandleMode mode)
    {
        if (mode == TransformHandleMode.Free)
        {
            _handleMode = mode;
            NeedsRedraw = true;
            return;
        }
        if (!CanChangeShape) return;
        if (_tool != CanvasTool.Move) SetTool(CanvasTool.Move);
        _handleMode = mode;
        if (mode == TransformHandleMode.Warp) BeginMeshWarp();
        NeedsRedraw = true;
    }

    /// <summary>The corner-moving drag a handle starts in the current mode, or null for upstream's resize.</summary>
    private TransformDragMode? CornerDrag(int handle, bool control) => HandleMode switch
    {
        TransformHandleMode.Skew => TransformDragMode.Skew(handle),
        TransformHandleMode.Distort => TransformDragMode.Distort(handle),
        TransformHandleMode.Perspective => TransformDragMode.Perspective(handle),
        _ => control ? TransformDragMode.Distort(handle) : null,
    };

    /// <summary>Back to upstream's handles; called as a tool changes.</summary>
    private void LeaveTransformMode()
    {
        if (_meshWarp is not null) CommitMeshWarp();
        _handleMode = TransformHandleMode.Free;
    }

    // MARK: Rotate by quarters

    public bool CanRotateChosen => (CanEdit || IsFloating) && !TransformsMask && _chosen.Count > 0
                                   && _document is not null && _chosen.All(id => _document.Layer(id) is { IsGroup: false });

    /// <summary>
    /// Edit › Transform's 180°, 90° clockwise and 90° counterclockwise: the chosen layers turned
    /// about the middle of their shared box, which six numbers can hold, so nothing is resampled.
    /// </summary>
    public void RotateChosen(double degrees)
    {
        if (!CanRotateChosen || _document is null) return;

        if (_chosen.Count == 1)
        {
            ChangeTransform(c => c with { Rotation = Normalized(c.Rotation + degrees) });
            return;
        }

        Edit(TextKey.HistoryTransformLayer, document =>
        {
            var originals = new Dictionary<Guid, LayerTransform>();
            foreach (Guid id in _chosen)
                if (document.Layer(id) is ImageLayer layer) originals[id] = layer.Transform;
            if (originals.Count == 0) return null;

            LayerTransform box = TransformGroup.BoxAround([.. originals.Values]);
            LayerTransform turned = box with { Rotation = Normalized(box.Rotation + degrees) };
            CanvasDocument next = document;
            foreach ((Guid id, LayerTransform placement) in TransformGroup.Follow(originals, box, turned))
                if (next.Layer(id) is ImageLayer layer) next = next.Replacing(MaskEditing.WithTransform(layer, placement));
            return (next, Primary);
        });

        static double Normalized(double angle) => ((angle % 360) + 540) % 360 - 180;
    }

    // MARK: Warp

    private WarpMesh? _meshWarp;
    private WarpMesh? _meshAtStart;
    private Guid _meshLayer;
    private WarpPreview? _meshPreview;
    private (int? Point, (double U, double V)? Surface, Point From, WarpMesh Mesh)? _meshDrag;

    /// <summary>Whether the warp grid is up.</summary>
    public bool MeshWarping => _meshWarp is not null;

    /// <summary>The preset the grid was last set to, for the options bar; None once it has been dragged.</summary>
    public TextWarp MeshStyle { get; private set; } = new();

    private void BeginMeshWarp()
    {
        if (_document is null || ActiveLayer is not { Image: not null } layer) return;
        _history.Begin(TextKey.HistoryWarp, _document, layer.Id);
        _meshLayer = layer.Id;
        _meshWarp = WarpMesh.Flat(layer.Transform);
        _meshAtStart = _meshWarp;
        MeshStyle = new TextWarp();
    }

    /// <summary>The grid set to one of the text warp's shapes, from the options bar.</summary>
    public void SetMeshStyle(TextWarp style)
    {
        if (_meshWarp is null || _document?.Layer(_meshLayer) is not ImageLayer layer) return;
        MeshStyle = style;
        _meshWarp = WarpMesh.Preset(layer.Transform, style);
        NeedsRedraw = true;
    }

    /// <summary>Enter or OK: the layer resampled over the grid, as one step.</summary>
    public void CommitMeshWarp()
    {
        if (_meshWarp is not WarpMesh mesh || _document is null) return;
        _meshWarp = null;
        _meshDrag = null;
        _meshPreview?.Dispose();
        _meshPreview = null;

        if (!ReferenceEquals(mesh, _meshAtStart) && _document.Layer(_meshLayer) is ImageLayer layer
            && !mesh.Points.SequenceEqual(WarpMesh.Flat(layer.Transform).Points)
            && WarpMesh.Warp(layer, mesh) is ImageLayer warped)
        {
            _document = _document.Replacing(warped);
            FloatingMade(warped.Image);
        }

        _history.End(_document, _meshLayer);
        _handleMode = TransformHandleMode.Free;
        NeedsRedraw = true;
    }

    /// <summary>Escape or Cancel: the layer as it was, and no step.</summary>
    public void CancelMeshWarp()
    {
        if (_meshWarp is null) return;
        _meshWarp = null;
        _meshDrag = null;
        _meshPreview?.Dispose();
        _meshPreview = null;
        _history.End(_document, _meshLayer);
        _handleMode = TransformHandleMode.Free;
        NeedsRedraw = true;
    }

    /// <summary>How near, in view points, a grid point has to be to take the drag.</summary>
    private const double MeshReach = 9;

    private bool MeshPointerDown(Point view, Point pixel)
    {
        if (_meshWarp is not WarpMesh mesh || _document is null) return false;

        CanvasProjection projection = _viewport.Projection(_document.Size);
        int? nearest = null;
        double best = MeshReach;
        for (int i = 0; i < mesh.Points.Count; i++)
        {
            Point at = projection.Apply(mesh.Points[i]);
            double distance = Math.Max(Math.Abs(at.X - view.X), Math.Abs(at.Y - view.Y));
            if (distance <= best)
            {
                best = distance;
                nearest = i;
            }
        }

        if (nearest is int point) _meshDrag = (point, null, pixel, mesh);
        else if (mesh.Find(pixel) is (double u, double v)) _meshDrag = (null, (u, v), pixel, mesh);
        return true;
    }

    private bool MeshPointerMoved(Point pixel)
    {
        if (_meshWarp is null) return false;
        if (_meshDrag is not { } drag) return true;
        (int? point, (double U, double V)? surface, Point from, WarpMesh start) = drag;

        double dx = pixel.X - from.X, dy = pixel.Y - from.Y;
        _meshWarp = point is int index ? start.Moved(index, dx, dy)
            : surface is (double u, double v) ? start.Dragged(u, v, dx, dy)
            : start;
        MeshStyle = new TextWarp();
        NeedsRedraw = true;
        return true;
    }

    private bool MeshPointerUp()
    {
        if (_meshWarp is null) return false;
        _meshDrag = null;
        NeedsRedraw = true;
        return true;
    }

    private LiveEdit? MeshWarped(int width, int height)
    {
        if (_meshWarp is not WarpMesh mesh || _document?.Layer(_meshLayer) is not { Image: not null } layer) return null;
        if (_meshPreview?.Layer.Id != layer.Id)
        {
            _meshPreview?.Dispose();
            _meshPreview = new WarpPreview(layer);
        }
        return _meshPreview.Frame(mesh, Projection(_document), width, height);
    }

    /// <summary>The grid over the picture: its lines at the thirds, the handles, and the points.</summary>
    private void DrawMesh(ID2D1DeviceContext context, CanvasProjection projection, ID2D1Brush light, ID2D1Brush dark,
                          float thickness)
    {
        if (_meshWarp is not WarpMesh mesh) return;

        const int steps = 32;
        var lines = new List<Point[]>();
        for (int k = 0; k <= 3; k++)
        {
            double t = k / 3.0;
            lines.Add([.. Enumerable.Range(0, steps + 1).Select(s => projection.Apply(mesh.At(t, (double)s / steps)))]);
            lines.Add([.. Enumerable.Range(0, steps + 1).Select(s => projection.Apply(mesh.At((double)s / steps, t)))]);
        }

        // The handles hang off their corners, as Photoshop draws them.
        foreach ((int corner, int handle) in new[] { (0, 1), (0, 4), (3, 2), (3, 7), (12, 13), (12, 8), (15, 14), (15, 11) })
            lines.Add([projection.Apply(mesh.Points[corner]), projection.Apply(mesh.Points[handle])]);

        for (int pass = 0; pass < 2; pass++)
        {
            ID2D1Brush brush = pass == 0 ? dark : light;
            float width = pass == 0 ? thickness * 3 : thickness;
            foreach (Point[] line in lines)
                for (int i = 0; i + 1 < line.Length; i++)
                    context.DrawLine(Vector(line[i]), Vector(line[i + 1]), brush, width);
        }

        float half = (float)(4 * _scale);
        for (int i = 0; i < mesh.Points.Count; i++)
        {
            Point at = projection.Apply(mesh.Points[i]);
            if (WarpMesh.IsCorner(i)) Square(context, at, half, light, dark, thickness);
            else context.FillEllipse(new Ellipse(Vector(at), half * 0.8f, half * 0.8f), light);
        }
    }

    // MARK: Liquify

    private LiquifyField? _liquify;
    private Guid _liquifyLayer;
    private LiquifyPreview? _liquifyPreview;
    private Point? _liquifyLast;

    /// <summary>Whether Filter › Liquify is open.</summary>
    public bool Liquifying => _liquify is not null;

    public LiquifyTool LiquifyBrush { get; set; } = LiquifyTool.Forward;

    /// <summary>The brush's diameter in layer pixels, 1–2000.</summary>
    public double LiquifySize { get; set; } = 100;

    /// <summary>How hard each dab pushes, 0.01–1.</summary>
    public double LiquifyPressure { get; set; } = 0.5;

    public bool CanLiquify => CanFilter && _meshWarp is null && _liquify is null && CanEdit;

    /// <summary>Filter › Liquify: the field starts flat over the chosen layer.</summary>
    public void StartLiquify()
    {
        if (!CanLiquify || _document is null || ActiveLayer is not { Image: PixelBuffer image } layer) return;
        LeaveTransformMode();
        _history.Begin(TextKey.HistoryLiquify, _document, layer.Id);
        _liquifyLayer = layer.Id;
        _liquify = new LiquifyField(image.Width, image.Height);
        NeedsRedraw = true;
    }

    /// <summary>OK keeps what the brushes did as one step; Cancel leaves the layer as it was.</summary>
    public void FinishLiquify(bool keep)
    {
        if (_liquify is not LiquifyField field || _document is null) return;
        _liquify = null;
        _liquifyLast = null;
        _liquifyPreview?.Dispose();
        _liquifyPreview = null;

        if (keep && _document.Layer(_liquifyLayer) is ImageLayer layer)
        {
            ImageLayer liquified = field.Apply(layer);
            if (!ReferenceEquals(liquified, layer)) _document = _document.Replacing(liquified);
        }

        _history.End(_document, _liquifyLayer);
        NeedsRedraw = true;
    }

    /// <summary>Restore All: every brush's work undone, frozen parts kept.</summary>
    public void ResetLiquify()
    {
        _liquify?.Reset();
        NeedsRedraw = true;
    }

    /// <summary>The brush's centre in the layer's pixels, from a view point.</summary>
    private Point? LiquifyPoint(Point view)
    {
        if (_document?.Layer(_liquifyLayer) is not { Image: PixelBuffer image } layer) return null;
        return LayerGeometry.ToPixels(layer.Transform, _viewport.DocumentPoint(view, _document.Size), image.Width, image.Height);
    }

    /// <summary>The brush's radius in the layer's pixels.</summary>
    private double LiquifyRadius => Math.Clamp(LiquifySize, 1, 2000) / 2;

    private bool LiquifyPointerDown(Point view)
    {
        if (_liquify is not LiquifyField field) return false;
        if (LiquifyPoint(view) is not Point at) return true;
        _liquifyLast = at;
        // Brushes that act where they stand take their first dab at once; the pushing ones need a move.
        if (LiquifyBrush is not (LiquifyTool.Forward or LiquifyTool.PushLeft)) Dab(field, at, default);
        NeedsRedraw = true;
        return true;
    }

    private bool LiquifyPointerMoved(Point view)
    {
        if (_liquify is not LiquifyField field) return false;
        NeedsRedraw = true; // the brush circle follows the pointer
        if (_liquifyLast is not Point from || LiquifyPoint(view) is not Point to) return true;

        // Dabs a quarter of the brush apart, each pushing by the way it came.
        double distance = Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y));
        double spacing = Math.Max(1, LiquifyRadius * 0.25);
        if (distance < spacing) return true;

        int steps = (int)Math.Ceiling(distance / spacing);
        Point previous = from;
        for (int step = 1; step <= steps; step++)
        {
            double t = (double)step / steps;
            var next = new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
            Dab(field, next, new Point(next.X - previous.X, next.Y - previous.Y));
            previous = next;
        }
        _liquifyLast = to;
        NeedsRedraw = true;
        return true;
    }

    private bool LiquifyPointerUp()
    {
        if (_liquify is null) return false;
        _liquifyLast = null;
        return true;
    }

    private void Dab(LiquifyField field, Point at, Point motion) =>
        field.Dab(LiquifyBrush, at, LiquifyRadius, LiquifyPressure, motion, reverse: Win32.IsKeyDown(Win32.VK_MENU));

    /// <summary>Whether the button is held on a brush that works while still — the tick keeps it going.</summary>
    private bool LiquifyHolding => _liquify is not null && _liquifyLast is not null
                                   && LiquifyBrush is LiquifyTool.Twirl or LiquifyTool.Pucker or LiquifyTool.Bloat
                                       or LiquifyTool.Reconstruct or LiquifyTool.Freeze or LiquifyTool.Thaw;

    /// <summary>One tick of a held brush, at the pointer.</summary>
    private void LiquifyTick()
    {
        if (_liquify is not LiquifyField field || LiquifyPoint(_pointer) is not Point at) return;
        Dab(field, at, default);
        _liquifyLast = at;
        NeedsRedraw = true;
    }

    private LiveEdit? Liquified(int width, int height)
    {
        if (_liquify is not LiquifyField field || _document?.Layer(_liquifyLayer) is not { Image: not null } layer) return null;
        if (_liquifyPreview?.Layer.Id != layer.Id)
        {
            _liquifyPreview?.Dispose();
            _liquifyPreview = new LiquifyPreview(layer);
        }
        return _liquifyPreview.Frame(field, Projection(_document), width, height);
    }

    /// <summary>The Liquify brush's circle, at its size in the layer.</summary>
    private void DrawLiquifyBrush(ID2D1DeviceContext context, CanvasProjection projection, ID2D1Brush light, ID2D1Brush dark,
                                  float thickness)
    {
        if (_liquify is null || !_pointerOverCanvas || _document?.Layer(_liquifyLayer) is not { Image: PixelBuffer image } layer) return;
        if (LiquifyPoint(_pointer) is not Point centre) return;

        var ring = new Point[33];
        for (int i = 0; i <= 32; i++)
        {
            double angle = i * 2 * Math.PI / 32;
            ring[i] = projection.Apply(LayerGeometry.ToDocument(layer.Transform,
                new Point(centre.X + Math.Cos(angle) * LiquifyRadius, centre.Y + Math.Sin(angle) * LiquifyRadius),
                image.Width, image.Height));
        }

        for (int pass = 0; pass < 2; pass++)
        {
            ID2D1Brush brush = pass == 0 ? dark : light;
            float width = pass == 0 ? thickness * 3 : thickness;
            for (int i = 0; i < 32; i++) context.DrawLine(Vector(ring[i]), Vector(ring[i + 1]), brush, width);
        }
    }

    /// <summary>Photoshop's Liquify keys: the brushes by letter, the brackets for size, Enter and Escape.</summary>
    private bool LiquifyKey(int key)
    {
        if (_liquify is null) return false;
        switch (key)
        {
            case Win32.VK_RETURN: FinishLiquify(keep: true); break;
            case Win32.VK_ESCAPE: FinishLiquify(keep: false); break;
            case Win32.VK_W: LiquifyBrush = LiquifyTool.Forward; break;
            case Win32.VK_R: LiquifyBrush = LiquifyTool.Reconstruct; break;
            case 0x43: LiquifyBrush = LiquifyTool.Twirl; break; // C
            case Win32.VK_S: LiquifyBrush = LiquifyTool.Pucker; break;
            case Win32.VK_B: LiquifyBrush = LiquifyTool.Bloat; break;
            case Win32.VK_O: LiquifyBrush = LiquifyTool.PushLeft; break;
            case 0x46: LiquifyBrush = LiquifyTool.Freeze; break; // F
            case Win32.VK_D: LiquifyBrush = LiquifyTool.Thaw; break;
            case Win32.VK_OEM_4: LiquifySize = Math.Max(1, Math.Round(LiquifySize / 1.2)); break;
            case Win32.VK_OEM_6: LiquifySize = Math.Min(2000, Math.Round(LiquifySize * 1.2 + 1)); break;
        }
        // Everything else waits: no tool or command changes the layer under an open Liquify.
        return true;
    }

    /// <summary>The warp grid's keys: Enter lays it down, Escape drops it, the rest wait.</summary>
    private bool MeshKey(int key)
    {
        if (_meshWarp is null) return false;
        if (key == Win32.VK_RETURN) CommitMeshWarp();
        else if (key == Win32.VK_ESCAPE) CancelMeshWarp();
        return true;
    }

    /// <summary>Ends whatever of these is open without keeping it — the document is being left.</summary>
    private void ForgetTransformTools()
    {
        CancelMeshWarp();
        FinishLiquify(keep: false);
        _handleMode = TransformHandleMode.Free;
    }
}
