using Compositor_korean_win.Core;
using Point = Compositor_korean_win.Core.Point;
using Size = Compositor_korean_win.Core.Size;

namespace Compositor_korean_win.Shell;

/// <summary>A gradient that remains adjustable until Enter, Apply, or a tool change.</summary>
internal sealed partial class CanvasView
{
    private Point? _gradientFrom;
    private Point _gradientTo;
    private int _gradientHandle = 1;
    private Guid _gradientTarget;
    private bool _gradientOnMask;
    private PixelBuffer? _gradientPreview;

    public bool HasPendingGradient => _gradientFrom is not null && _gradientPreview is not null;

    private void BeginGradient(Point point)
    {
        if (_document is null || Primary is not Guid id || _document.Layer(id) is not ImageLayer layer) return;
        if (layer.IsGroup && !EditingMask) return;

        double reach = TransformSnap.ToleranceFor(_viewport) * 1.5;
        if (_gradientFrom is Point start && id == _gradientTarget && EditingMask == _gradientOnMask)
        {
            if (NearDocument(point, start, reach)) _gradientHandle = 0;
            else if (NearDocument(point, _gradientTo, reach)) _gradientHandle = 1;
            else
            {
                _gradientFrom = point;
                _gradientTo = point;
                _gradientHandle = 1;
            }
        }
        else
        {
            CommitGradient();
            _gradientTarget = id;
            _gradientOnMask = EditingMask;
            _gradientFrom = point;
            _gradientTo = point;
            _gradientHandle = 1;
        }

        _shapeFrom = point;
        _shapeTo = point;
        NeedsRedraw = true;
    }

    private static bool NearDocument(Point a, Point b, double reach) =>
        Math.Abs(a.X - b.X) <= reach && Math.Abs(a.Y - b.Y) <= reach;

    private void DragGradient(Point point, bool shift = false)
    {
        if (_gradientFrom is not Point start) return;
        // Shift holds the line to 45° steps round the other end, as in Photoshop.
        if (shift) point = SnappedToEighths(point, _gradientHandle == 0 ? _gradientTo : start);
        if (_gradientHandle == 0) _gradientFrom = point;
        else _gradientTo = point;
        _shapeFrom = _gradientFrom;
        _shapeTo = _gradientTo;
        RefreshGradient();
    }

    /// <summary><paramref name="point"/> turned onto the nearest eighth-turn round <paramref name="anchor"/>, its length kept.</summary>
    internal static Point SnappedToEighths(Point point, Point anchor)
    {
        double dx = point.X - anchor.X, dy = point.Y - anchor.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        return new Point(anchor.X + Math.Cos(angle) * length, anchor.Y + Math.Sin(angle) * length);
    }

    private void EndGradientDrag()
    {
        _shapeFrom = null;
        if (_gradientFrom is not Point start || NearDocument(start, _gradientTo, 0.5))
        {
            CancelGradient();
            return;
        }
        RefreshGradient();
    }

    private void RefreshGradient()
    {
        _gradientPreview?.Release();
        _gradientPreview = null;
        if (_document is null || _gradientFrom is not Point from
            || _document.Layer(_gradientTarget) is not ImageLayer layer) return;

        if (_gradientOnMask)
        {
            if (MaskEditing.Canvas(layer) is not (PixelBuffer working, LayerTransform placement)) return;
            try
            {
                Point start = LayerGeometry.ToPixels(placement, from, working.Width, working.Height);
                Point end = LayerGeometry.ToPixels(placement, _gradientTo, working.Width, working.Height);
                GradientSettings greys = Gradient with
                {
                    From = MaskEditing.Grey(MaskPaintsWhite),
                    To = MaskEditing.Grey(!MaskPaintsWhite),
                };
                _paintGrid = new PixelRect(0, 0, working.Width, working.Height);
                _gradientPreview = GradientTool.Draw(working, working.Width, working.Height, start, end, greys,
                                                       Restricted(placement));
            }
            finally
            {
                working.Release();
            }
        }
        else
        {
            int width = layer.Image?.Width ?? _document.Width;
            int height = layer.Image?.Height ?? _document.Height;
            LayerTransform placement = layer.Image is null
                ? new LayerTransform(Point.Zero, new Size(_document.Width, _document.Height))
                : layer.Transform;
            _paintGrid = new PixelRect(0, 0, width, height);
            Point start = LayerGeometry.ToPixels(placement, from, width, height);
            Point end = LayerGeometry.ToPixels(placement, _gradientTo, width, height);
            _gradientPreview = GradientTool.Draw(layer.Image, width, height, start, end, Gradient,
                                                   Restricted(placement));
        }

        NeedsRedraw = true;
    }

    private LiveEdit? GradientLive()
    {
        if (_gradientPreview is not PixelBuffer preview || _document?.Layer(_gradientTarget) is not ImageLayer layer)
            return null;

        if (_gradientOnMask)
        {
            if (layer.Image is not PixelBuffer image) return null;
            return new LiveEdit(layer.Id, new BufferSource(image)) { Mask = preview };
        }
        return new LiveEdit(layer.Id, new BufferSource(preview) { Cacheable = false });
    }

    public void CommitGradient()
    {
        PixelBuffer? preview = _gradientPreview;
        _gradientPreview = null;
        Point? from = _gradientFrom;
        _gradientFrom = null;
        _shapeFrom = null;

        if (preview is null || from is null || _document is null
            || _document.Layer(_gradientTarget) is not ImageLayer layer) return;

        try
        {
            _history.Begin(Name(CanvasTool.Gradient), _document, layer.Id);
            if (_gradientOnMask)
                _document = _document.Replacing(MaskEditing.WithMask(layer, preview.Retain()));
            else
            {
                LayerTransform placement = layer.Image is null
                    ? new LayerTransform(Point.Zero, new Size(_document.Width, _document.Height))
                    : layer.Transform;
                _document = _document.Replacing(layer with { Image = preview.Retain(), Transform = placement });
            }
            _history.End(_document, layer.Id);
        }
        finally
        {
            preview.Release();
        }
        NeedsRedraw = true;
    }

    public void CancelGradient()
    {
        _gradientFrom = null;
        _shapeFrom = null;
        _gradientPreview?.Release();
        _gradientPreview = null;
        NeedsRedraw = true;
    }
}
