using Compositor_korean_win.Core;

namespace Compositor_korean_win.Shell;

/// <summary>
/// Layer Style, and the sessions that let a sheet's live changes land as one history step.
/// </summary>
internal sealed partial class CanvasView
{
    /// <summary>The document as a sheet found it, while the sheet is open.</summary>
    private CanvasDocument? _sessionBefore;

    /// <summary>Whether a sheet's changes are being gathered into one step.</summary>
    public bool InSession => _sessionBefore is not null;

    /// <summary>
    /// Opens a step that a sheet fills as its controls move — a colour dragged across the picker, a
    /// warp's bend slid — so the whole visit is one thing to undo, not one per mouse move.
    /// </summary>
    public void BeginSession(HistoryName name)
    {
        if (_document is null || InSession) return;
        // Inside a text edit the edit is already the step; this only marks that a sheet is open.
        if (EditingText is null) _history.Begin(name, _document, Primary);
        _sessionBefore = _document;
    }

    /// <summary>Closes the step: kept, or everything the sheet did put back.</summary>
    public void EndSession(bool keep)
    {
        if (_sessionBefore is not CanvasDocument before) return;
        if (!keep)
        {
            _document = before;
            // Settings made during the session that no step holds; inside a text edit they are the
            // edit's own, which it releases when it closes.
            if (EditingText is null) ReleaseInterim();
        }
        if (EditingText is null)
        {
            _textInterim.Clear();
            _history.End(_document, Primary);
        }
        _sessionBefore = null;
        NeedsRedraw = true;
    }

    /// <summary>The chosen layers that can carry a style: pixels, not folders or adjustments.</summary>
    private IEnumerable<ImageLayer> Styleable() =>
        _chosen.Select(id => _document?.Layer(id)).OfType<ImageLayer>()
               .Where(layer => !layer.IsGroup && layer.Adjustment is null);

    public bool CanStyleLayers => (CanEdit || InSession) && Styleable().Any();

    /// <summary>The active layer's style, for a sheet to start from.</summary>
    public LayerEffects? ActiveEffects => ActiveLayer?.Effects;

    /// <summary>Gives every chosen layer this style — nothing, when <paramref name="effects"/> is empty.</summary>
    public void SetEffects(LayerEffects? effects)
    {
        if (_document is null) return;
        if (effects is { IsEmpty: true }) effects = null;

        if (InSession)
        {
            foreach (ImageLayer layer in Styleable().ToList())
                _document = _document.Replacing(layer with { Effects = effects });
            NeedsRedraw = true;
            return;
        }

        Edit(TextKey.HistoryLayerStyle, document =>
        {
            CanvasDocument next = document;
            foreach (ImageLayer layer in Styleable().ToList())
                next = next.Replacing(layer with { Effects = effects });
            return next == document ? null : (next, null);
        });
    }
}
