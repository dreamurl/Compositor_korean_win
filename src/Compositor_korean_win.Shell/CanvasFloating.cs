using Compositor_korean_win.Core;
using Point = Compositor_korean_win.Core.Point;

namespace Compositor_korean_win.Shell;

/// <summary>
/// Layer › Transform (Ctrl+T) — upstream's <c>FloatingSelection</c>: with a selection, the selected
/// pixels are lifted onto a layer of their own for the Move tool's handles to scale, rotate and
/// distort, then laid back into their layer.
/// </summary>
/// <remarks>
/// <para>
/// Lifting clears the selection in the source layer and puts the pixels on a "Floating Selection"
/// layer just above it; Enter merges them back (<see cref="FloatingMerge"/>) and Escape puts
/// everything as it was. The whole of it is one history step: it is opened when the pixels are
/// lifted and closed by the merge, and the drags in between nest inside it.
/// </para>
/// <para>
/// While the pixels float the rest of the window waits, as it does for a filter: the menus, the
/// Layers panel and the other tabs. Choosing another tool, or a key the Move tool does not use,
/// merges first, as upstream commits a transform before anything else.
/// </para>
/// </remarks>
internal sealed partial class CanvasView
{
    private sealed record Floating(Guid Source, Guid Layer, CanvasDocument Before, IReadOnlyCollection<Guid> ChosenBefore,
                                   LayerTransform Original, PixelBuffer Lifted, DocumentSelection Selection);

    private Floating? _floating;

    /// <summary>Buffers made while the pixels float, to free whichever the merge does not keep.</summary>
    private readonly List<PixelBuffer> _floatingBuffers = [];

    /// <summary>Whether selected pixels are floating, waiting for Enter or Escape.</summary>
    public bool IsFloating => _floating is not null;

    /// <summary>Ctrl+T can lift the selection: there is one, over a layer's own pixels.</summary>
    public bool CanTransformSelection =>
        CanEdit && !EditingMask && HasSelection && ActiveLayer is { Image: not null, IsGroup: false, Adjustment: null };

    /// <summary>Ctrl+T: the selection's pixels when there is one, else the layer, on the Move tool's handles.</summary>
    public void Transform()
    {
        if (CanTransformSelection)
        {
            LiftSelection();
            return;
        }

        if (CanEdit && ActiveLayer is not null) SetTool(CanvasTool.Move);
    }

    private void LiftSelection()
    {
        if (_document is not CanvasDocument document || _selection is not DocumentSelection selection
            || ActiveLayer is not ImageLayer source) return;
        if (PixelCommands.Copy(source, selection) is not (PixelBuffer lifted, LayerTransform placement)) return;

        _history.Begin(TextKey.CommandTransformSelection, document, source.Id);

        CanvasDocument next = document;
        if (PixelCommands.Clear(source, selection) is ImageLayer cleared)
        {
            next = next.Replacing(cleared);
            _floatingBuffers.Add(cleared.Image!);
        }

        var floating = new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = Localizer.Text(TextKey.LayerFloatingSelection),
            Image = lifted,
            Transform = placement,
            ParentId = source.ParentId,
            Opacity = source.Opacity,
            BlendMode = source.BlendMode,
        };
        _floatingBuffers.Add(lifted);

        var layers = next.Layers.ToList();
        layers.Insert(next.IndexOf(source.Id) + 1, floating);
        _document = next with { Layers = layers.ToEquatableList() };

        _floating = new Floating(source.Id, floating.Id, document, [.. _chosen], placement, lifted, selection);
        // The outline would say the pixels are still where they were; it comes back, moved, on Enter.
        _selection = null;
        _chosen.Clear();
        _chosen.Add(floating.Id);
        _tool = CanvasTool.Move;
        NeedsRedraw = true;
    }

    /// <summary>A buffer a floating edit made — a distortion's — to free if the merge does not keep it.</summary>
    private void FloatingMade(PixelBuffer? buffer)
    {
        if (_floating is not null && buffer is not null) _floatingBuffers.Add(buffer);
    }

    /// <summary>Enter: the pixels into their layer, the selection moved with them, as the one step.</summary>
    public void CommitFloating()
    {
        if (_floating is not Floating floating || _document is null) return;
        _floating = null;

        ImageLayer? layer = _document.Layer(floating.Layer), source = _document.Layer(floating.Source);
        if (layer is null || source is null || FloatingMerge.Merge(source, layer) is not ImageLayer merged)
        {
            CancelFloating(floating);
            return;
        }

        // An upright move, scale or turn carries the outline exactly; a distortion's pixels are an
        // upright box that no longer says where the outline went, so the moved pixels themselves do.
        DocumentSelection? moved = ReferenceEquals(layer.Image, floating.Lifted)
            ? floating.Selection.Transformed(point =>
                LayerGeometry.ToDocument(layer.Transform, LayerGeometry.ToPixels(floating.Original, point, 1, 1), 1, 1))
            : SelectionCommands.FromLayer(_document, layer);

        CanvasDocument next = _document.Replacing(merged);
        _document = next with { Layers = next.Layers.Where(each => each.Id != floating.Layer).ToEquatableList() };
        _selection = moved;
        _chosen.Clear();
        _chosen.Add(floating.Source);
        _history.End(_document, floating.Source);
        ReleaseFloating();
        NeedsRedraw = true;
    }

    /// <summary>Escape: the layer, the selection and the choice as they were before Ctrl+T.</summary>
    public void CancelFloating()
    {
        if (_floating is not Floating floating) return;
        _floating = null;
        CancelFloating(floating);
    }

    private void CancelFloating(Floating floating)
    {
        _document = floating.Before;
        _selection = floating.Selection;
        _chosen.Clear();
        foreach (Guid id in floating.ChosenBefore) _chosen.Add(id);
        _history.End(_document, floating.Source);
        ReleaseFloating();
        NeedsRedraw = true;
    }

    /// <summary>Frees the buffers the float made that the document no longer holds.</summary>
    private void ReleaseFloating()
    {
        var kept = new HashSet<PixelBuffer>(ReferenceEqualityComparer.Instance);
        if (_document is not null)
        {
            foreach (ImageLayer layer in _document.Layers)
            {
                if (layer.Image is PixelBuffer image) kept.Add(image);
                if (layer.Mask?.Coverage is PixelBuffer mask) kept.Add(mask);
            }
        }

        foreach (PixelBuffer buffer in _floatingBuffers)
            if (!kept.Contains(buffer)) buffer.Release();
        _floatingBuffers.Clear();
    }

    /// <summary>Anything else the user turns to while pixels float lays them down first.</summary>
    public void SettleFloating()
    {
        if (IsFloating) CommitFloating();
    }
}
