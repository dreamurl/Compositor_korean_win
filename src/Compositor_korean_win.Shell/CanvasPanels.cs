using Compositor_korean_win.Core;

namespace Compositor_korean_win.Shell;

/// <summary>What the panels ask of the canvas: choosing tools and layers, and editing a layer's settings.</summary>
internal sealed partial class CanvasView
{
    /// <summary>Takes up a tool, as the rail and the tool keys do.</summary>
    public void SetTool(CanvasTool tool)
    {
        if (IsFiltering || _stroke is not null) return;
        _tool = tool;
        NeedsRedraw = true;
    }

    /// <summary>
    /// A click on a layer's row: with control it joins or leaves the chosen set, with shift the
    /// run from the active layer to it is chosen, and otherwise it is chosen alone.
    /// </summary>
    public void ClickLayer(Guid id, bool control, bool shift, IReadOnlyList<Guid> rowsTopFirst)
    {
        if (!CanEdit || _document?.Layer(id) is null) return;

        if (control)
        {
            if (!_chosen.Remove(id)) _chosen.Add(id);
            if (_chosen.Count == 0) _chosen.Add(id);
        }
        else if (shift && Primary is Guid anchor)
        {
            int from = rowsTopFirst.ToList().IndexOf(anchor), to = rowsTopFirst.ToList().IndexOf(id);
            if (from >= 0 && to >= 0)
            {
                _chosen.Clear();
                _chosen.Add(anchor);
                for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) _chosen.Add(rowsTopFirst[i]);
            }
        }
        else
        {
            Choose(id);
        }

        NeedsRedraw = true;
    }

    /// <summary>Shows or hides one layer, as its eye does.</summary>
    public void ToggleVisibility(Guid id)
    {
        if (_document?.Layer(id) is not ImageLayer layer) return;
        Edit(layer.IsVisible ? TextKey.CommandHideLayer : TextKey.CommandShowLayer,
             document => (LayerCommands.ToggleVisibility(document, id), null));
    }

    private bool _settingOpacity;

    /// <summary>Starts dragging the active layer's opacity: one history step for the whole drag.</summary>
    public void BeginOpacity()
    {
        if (!CanEdit || ActiveLayer is null) return;
        _history.Begin(TextKey.HistoryOpacity, _document, Primary);
        _settingOpacity = true;
    }

    public void SetOpacity(double opacity)
    {
        if (_document is null || !_settingOpacity) return;
        opacity = Math.Clamp(Math.Round(opacity, 2), 0, 1);
        foreach (Guid id in _chosen)
            if (_document.Layer(id) is ImageLayer layer) _document = _document.Replacing(layer with { Opacity = opacity });
        NeedsRedraw = true;
    }

    public void EndOpacity()
    {
        if (!_settingOpacity) return;
        _settingOpacity = false;
        _history.End(_document, Primary);
    }

    public void SetBlendMode(LayerBlendMode mode) =>
        Edit(TextKey.HistoryBlendMode, document =>
        {
            CanvasDocument next = document;
            foreach (Guid id in _chosen)
                if (next.Layer(id) is ImageLayer layer) next = next.Replacing(layer with { BlendMode = mode });
            return (next, null);
        });

    public void Rename(Guid id, string name) =>
        Edit(TextKey.HistoryRenameLayer,
             document => LayerCommands.Rename(document, id, name) is CanvasDocument next ? (next, null) : null);

    /// <summary>The colour the brush and fills use.</summary>
    public Rgba ForegroundColor
    {
        get => Brush.Color;
        set
        {
            Brush = Brush with { Color = value };
            Shape = Shape with { Color = value };
            Gradient = Gradient with { From = value };
            NeedsRedraw = true;
        }
    }

    public void SwapColors()
    {
        Rgba foreground = ForegroundColor;
        ForegroundColor = BackgroundColor;
        BackgroundColor = foreground;
        Gradient = Gradient with { To = BackgroundColor };
    }

    public void DefaultColors()
    {
        ForegroundColor = new Rgba(0, 0, 0);
        BackgroundColor = new Rgba(255, 255, 255);
        Gradient = Gradient with { To = BackgroundColor };
    }
}
