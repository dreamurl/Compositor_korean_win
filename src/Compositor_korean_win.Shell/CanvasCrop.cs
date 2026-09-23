using Compositor_korean_win.Core;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Point = Compositor_korean_win.Core.Point;
using Rect = Compositor_korean_win.Core.Rect;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The Crop tool — upstream's <c>Crop.swift</c> and <c>CropControls</c>.
/// </summary>
/// <remarks>
/// <para>
/// Choosing the tool puts a frame round the whole canvas. A drag inside it moves it, a handle
/// resizes it, and a drag outside draws a new one; its edges snap to the canvas and to the layers.
/// Enter, or Apply on the options bar, makes the frame the canvas; Escape puts the frame back round
/// the canvas. Leaving the tool drops the frame, as upstream does.
/// </para>
/// <para>
/// The crop cuts nothing (<see cref="DocumentCommands.Crop"/>): the canvas moves and every layer
/// keeps its pixels, so what fell outside can be brought back in.
/// </para>
/// </remarks>
internal sealed partial class CanvasView
{
    private Rect? _cropFrame;
    private CropDragKind _cropKind;
    private int _cropHandle;
    private Point _cropStart;
    private Rect _cropOriginal;
    private bool _cropDragging;
    private CropRatio _cropRatio = CropRatio.Free;

    /// <summary>The frame, while the Crop tool is chosen: the whole canvas until it is dragged.</summary>
    public Rect? CropFrame =>
        _tool == CanvasTool.Crop && _document is not null
            ? _cropFrame ?? new Rect(0, 0, _document.Width, _document.Height)
            : null;

    /// <summary>The fixed proportion, if any. Choosing one fits the frame to it at once.</summary>
    public CropRatio CropRatio
    {
        get => _cropRatio;
        set
        {
            _cropRatio = value;
            if (CropFrame is Rect frame && Proportion is double proportion)
            {
                Rect fitted = CropGeometry.WithProportion(frame, proportion);
                if (CropGeometry.Valid(fitted)) _cropFrame = fitted;
            }
            NeedsRedraw = true;
        }
    }

    /// <summary>Whether Apply would change anything.</summary>
    public bool CanApplyCrop =>
        CanEdit && CropFrame is Rect frame && CropGeometry.Valid(frame)
        && (frame.X != 0 || frame.Y != 0 || frame.Width != _document!.Width || frame.Height != _document.Height);

    private double? Proportion =>
        _document is null ? null : CropGeometry.Proportion(_cropRatio, _document.Width, _document.Height);

    private void BeginCrop(Point pixel, Point view, bool symmetric)
    {
        if (CropFrame is not Rect frame) return;

        var box = new LayerTransform(frame.Origin, frame.Size);
        if (HitHandle(box, view) is int handle and not RotationHandle)
        {
            _cropKind = CropDragKind.Resize;
            _cropHandle = handle;
        }
        else
        {
            _cropKind = frame.Contains(pixel) ? CropDragKind.Move : CropDragKind.Create;
        }

        _cropStart = pixel;
        _cropOriginal = frame;
        _cropDragging = true;
        _cropSymmetric = symmetric;
        NeedsRedraw = true;
    }

    private bool _cropSymmetric;

    private void DragCrop(Point pixel, bool alt)
    {
        if (!_cropDragging || _document is null) return;

        double? proportion = Proportion;
        bool symmetric = alt || _cropSymmetric;
        Rect next = CropGeometry.Dragged(_cropOriginal, _cropKind, _cropHandle, _cropStart, pixel, proportion, symmetric);
        next = CropGeometry.Snap(next, _cropKind, _cropHandle, pixel, proportion, CropGeometry.Targets(_document),
                                 TransformSnap.ToleranceFor(_viewport));
        if (CropGeometry.Valid(next)) _cropFrame = next;
        NeedsRedraw = true;
    }

    private void EndCrop() => _cropDragging = false;

    /// <summary>Makes the frame the canvas, as one history step, the selection moving with the content.</summary>
    public void ApplyCrop()
    {
        if (!CanApplyCrop || CropFrame is not Rect frame) return;
        PixelRect pixels = frame.Rounded();

        Edit(TextKey.ToolCrop, document => DocumentCommands.Crop(document, pixels) is CanvasDocument cropped ? (cropped, null) : null);
        _selection = _selection?.Transformed(point => new Point(point.X - pixels.X, point.Y - pixels.Y));
        _cropFrame = null;
        FitOnScreen();
    }

    /// <summary>Puts the frame back round the whole canvas.</summary>
    public void CancelCrop()
    {
        _cropFrame = null;
        _cropDragging = false;
        NeedsRedraw = true;
    }

    /// <summary>
    /// The frame on the canvas: everything outside it dimmed, a white edge and its handles.
    /// </summary>
    private void DrawCrop(ID2D1DeviceContext context, CanvasProjection projection, ID2D1Brush fill, ID2D1Brush outline,
                          float thickness, float half)
    {
        if (CropFrame is not Rect frame) return;

        Rect onScreen = projection.Apply(frame);
        using ID2D1SolidColorBrush dim = context.CreateSolidColorBrush(new Color4(0f, 0f, 0f, 0.5f));
        Rect area = _area;
        // Four bands round the frame, so the frame's own inside is left untouched.
        context.FillRectangle(Raw(Rect.FromBounds(area.MinX, area.MinY, area.MaxX, onScreen.MinY)), dim);
        context.FillRectangle(Raw(Rect.FromBounds(area.MinX, onScreen.MaxY, area.MaxX, area.MaxY)), dim);
        context.FillRectangle(Raw(Rect.FromBounds(area.MinX, onScreen.MinY, onScreen.MinX, onScreen.MaxY)), dim);
        context.FillRectangle(Raw(Rect.FromBounds(onScreen.MaxX, onScreen.MinY, area.MaxX, onScreen.MaxY)), dim);

        context.DrawRectangle(Raw(onScreen), fill, thickness * 1.5f);

        var box = new LayerTransform(frame.Origin, frame.Size);
        foreach (Point unit in TransformDrag.Handles)
            Square(context, projection.Apply(box.PointAt(unit)), half, fill, outline, thickness);
    }
}
