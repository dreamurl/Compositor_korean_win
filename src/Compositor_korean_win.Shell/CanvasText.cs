using Compositor_korean_win.Core;
using Point = Compositor_korean_win.Core.Point;
using Size = Compositor_korean_win.Core.Size;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The Type tool: setting words on a layer of their own, and setting them again.
/// </summary>
/// <remarks>
/// <para>
/// A click on the canvas starts a text layer there, or picks up the live text layer under the
/// pointer; the window then opens a native edit box for the words (<see cref="TextEditRequested"/>)
/// — native because Korean goes through the IME, which a self-drawn box would have to reimplement.
/// Every change to the words or their style sets the layer again from its recipe, keeping the point
/// the text was set from where it was on the document.
/// </para>
/// <para>
/// A whole edit, from the click to the box closing, is one history step: typing a word is one
/// thing to undo, not one per letter. Escape puts the document back as it was, and a new layer
/// left empty goes away, as Photoshop's does.
/// </para>
/// </remarks>
internal sealed partial class CanvasView
{
    /// <summary>What the Type tool sets next, and what a style change applies to: the words are ignored.</summary>
    public LayerText TextStyle { get; set; } = new() { Font = "Malgun Gothic", Size = 72 };

    /// <summary>Where glyphs come from. The self-test may swap in its own.</summary>
    public static IGlyphSource Glyphs { get; set; } = DirectWriteGlyphs.Shared;

    /// <summary>Raised when a text layer wants its words typed — a new one, or one clicked on.</summary>
    public Action<Guid>? TextEditRequested { get; set; }

    /// <summary>The text layer whose words are being typed, if any.</summary>
    public Guid? EditingText { get; private set; }

    /// <summary>
    /// The letters selected in the typing box, as indices into the words, while it is open. A style
    /// change then reaches only those letters, as it does in Photoshop with letters selected.
    /// </summary>
    public Func<(int Start, int End)?>? TextSelection { get; set; }

    private CanvasDocument? _textBefore;
    private bool _textIsNew;

    /// <summary>
    /// Pixels set during the open edit that no history step holds. Each is released as soon as the
    /// next setting replaces it; history only ever sees the first and the last.
    /// </summary>
    private readonly HashSet<PixelBuffer> _textInterim = [];

    /// <summary>Puts a newly set edited layer in the document, freeing the setting it replaces.</summary>
    private void ReplaceEdited(ImageLayer layer)
    {
        if (_document?.Layer(layer.Id)?.Image is PixelBuffer previous && _textInterim.Remove(previous))
            previous.Release();
        if (layer.Image is PixelBuffer made) _textInterim.Add(made);
        _document = _document!.Replacing(layer);
    }

    private void ReleaseInterim()
    {
        foreach (PixelBuffer buffer in _textInterim) buffer.Release();
        _textInterim.Clear();
    }

    /// <summary>A click with the Type tool.</summary>
    private void TextClick(Point document)
    {
        if (_document is null || EditingText is not null) return;

        if (OpenTextAt(document)) return;

        if (AddTextLayer(document, "") is Guid added) TextEditRequested?.Invoke(added);
    }

    /// <summary>Opens the topmost live text under a canvas point and asks the window for its editor.</summary>
    private bool OpenTextAt(Point document)
    {
        if (EditingText is not null || TextLayerAt(document) is not Guid existing) return false;
        if (!BeginTextEdit(existing)) return false;
        TextEditRequested?.Invoke(existing);
        return true;
    }

    /// <summary>The topmost visible live text layer whose box covers a document point.</summary>
    public Guid? TextLayerAt(Point document)
    {
        if (_document is null) return null;
        for (int i = _document.Layers.Count - 1; i >= 0; i--)
        {
            ImageLayer layer = _document.Layers[i];
            if (!layer.IsVisible || !layer.IsLiveText || layer.Image is not PixelBuffer image) continue;
            Point pixel = LayerGeometry.ToPixels(layer.Transform, document, image.Width, image.Height);
            if (pixel.X >= 0 && pixel.Y >= 0 && pixel.X < image.Width && pixel.Y < image.Height) return layer.Id;
        }
        return null;
    }

    /// <summary>
    /// A new text layer above the active one, set from <paramref name="document"/> in the current
    /// style, opened for typing. Returns its id.
    /// </summary>
    public Guid? AddTextLayer(Point document, string text)
    {
        if (_document is null || !CanEdit || EditingText is not null) return null;

        (CanvasDocument next, Guid id) = LayerCommands.AddBlankLayer(_document, Primary);
        _history.Begin(TextKey.HistoryAddText, _document, Primary);
        _textBefore = _document;
        _textIsNew = true;
        EditingText = id;

        ImageLayer blank = next.Layer(id)!;
        LayerText recipe = TextStyle with { Text = text, Rendered = null };
        _document = next;
        ReplaceEdited(Set(blank, recipe, document));
        _chosen.Clear();
        _chosen.Add(id);
        NeedsRedraw = true;
        return id;
    }

    /// <summary>Opens an existing live text layer for typing.</summary>
    public bool BeginTextEdit(Guid id)
    {
        if (_document is null || !CanEdit || EditingText is not null) return false;
        if (_document.Layer(id) is not { IsLiveText: true } layer) return false;

        _history.Begin(TextKey.HistoryEditText, _document, id);
        _textBefore = _document;
        _textIsNew = false;
        EditingText = id;
        _chosen.Clear();
        _chosen.Add(id);
        // The tool takes on the style of what is being edited, as Photoshop's options bar does.
        TextStyle = layer.Text! with { Text = "", Rendered = null, Runs = null };
        NeedsRedraw = true;
        return true;
    }

    /// <summary>The words of the layer being typed, as they stand.</summary>
    public string EditedText =>
        EditingText is Guid id && _document?.Layer(id)?.Text is LayerText text ? text.Text : "";

    /// <summary>Sets the edited layer's words again.</summary>
    public void UpdateEditedText(string words)
    {
        if (_document is null || EditingText is not Guid id || _document.Layer(id) is not { Text: LayerText recipe } layer) return;
        if (recipe.Text == words) return;

        // Runs stay on the letters they were on; new letters take the style of the one before.
        ImageLayer set = Set(layer, TextRuns.Retype(recipe, words), anchor: null);
        ReplaceEdited(set with { Name = NameFor(words, layer.Name, _textIsNew) });
        NeedsRedraw = true;
    }

    /// <summary>Closes the edit: kept as one history step, or put back as it was.</summary>
    public void EndTextEdit(bool commit)
    {
        if (EditingText is not Guid id) return;

        bool discard = !commit
            // Nothing typed into a new layer: the layer goes, and the edit comes to nothing.
            || (_textIsNew && _document?.Layer(id) is { Text: LayerText { Text: var words } } && string.IsNullOrWhiteSpace(words));
        if (discard && _textBefore is not null)
        {
            _document = _textBefore;
            ReleaseInterim();
        }
        // Kept: the last setting now belongs to the document, and history takes it from here.
        _textInterim.Clear();

        EditingText = null;
        _textBefore = null;
        _chosen.RemoveWhere(each => _document?.Layer(each) is null);
        if (_chosen.Count == 0 && _document?.Layers.Count > 0) _chosen.Add(_document.Layers[^1].Id);
        _history.End(_document, Primary);
        NeedsRedraw = true;
    }

    /// <summary>
    /// Changes the Type tool's style and, with it, the live text layers chosen — or the one being
    /// typed, inside the edit already open.
    /// </summary>
    public void ChangeTextStyle(Func<LayerText, LayerText> change)
    {
        TextStyle = change(TextStyle) with { Text = "", Rendered = null, Runs = null };
        if (_document is null) return;

        IEnumerable<Guid> targets = EditingText is Guid editing ? [editing] : _chosen.ToList();

        // Letters selected in the typing box take the change alone; otherwise the whole layer does,
        // its runs included.
        (int Start, int End) range = EditingText is not null && TextSelection?.Invoke() is (int start, int end) ? (start, end) : (0, 0);
        LayerText Restyled(LayerText recipe) => TextRuns.Restyle(recipe, range.Start, range.End, change);

        if (EditingText is not null || InSession)
        {
            foreach (Guid id in targets)
                if (_document.Layer(id) is { IsLiveText: true, Text: LayerText recipe } layer)
                    ReplaceEdited(Set(layer, Restyled(recipe), anchor: null));
            NeedsRedraw = true;
            return;
        }

        Edit(TextKey.HistoryTextStyle, document =>
        {
            CanvasDocument next = document;
            bool changed = false;
            foreach (Guid id in targets)
            {
                if (next.Layer(id) is not { IsLiveText: true, Text: LayerText recipe } layer) continue;
                next = next.Replacing(Set(layer, Restyled(recipe), anchor: null));
                changed = true;
            }
            return changed ? (next, null) : null;
        });
    }

    /// <summary>
    /// The style of the letters selected in the text being typed — of the letter before the caret
    /// when none are, as Photoshop shows — or of the first chosen text layer, or the tool's.
    /// </summary>
    public LayerText ShownTextStyle
    {
        get
        {
            if (EditingText is Guid editing && _document?.Layer(editing)?.Text is LayerText typed)
            {
                (int start, int end) = TextSelection?.Invoke() ?? (typed.Text.Length, typed.Text.Length);
                return TextRuns.StyleAt(typed, start < end ? start : Math.Max(0, start - 1));
            }
            return _chosen.Select(id => _document?.Layer(id)).FirstOrDefault(layer => layer?.IsLiveText == true)?.Text
                   ?? TextStyle;
        }
    }

    /// <summary>Whether a style change would reach a layer, rather than only the tool.</summary>
    public bool ChosenLiveText => _chosen.Any(id => _document?.Layer(id)?.IsLiveText == true);

    /// <summary>The layer set from its recipe, keeping its anchor or placing it at <paramref name="anchor"/>.</summary>
    private static ImageLayer Set(ImageLayer layer, LayerText recipe, Point? anchor) =>
        TextPlacement.Set(layer, recipe, Glyphs, anchor);

    /// <summary>A text layer is named after its first line, as Photoshop names it, until renamed.</summary>
    private static string NameFor(string words, string current, bool isNew)
    {
        string first = words.Replace("\r", "").Split('\n')[0].Trim();
        if (first.Length > 40) first = first[..40];
        if (first.Length == 0) return current;
        return isNew ? first : current;
    }

    /// <summary>Makes a text layer's words its pixels for good, as Photoshop's Rasterize Type does.</summary>
    public void RasterizeText()
    {
        Edit(TextKey.CommandRasterizeType, document =>
        {
            CanvasDocument next = document;
            bool changed = false;
            foreach (Guid id in _chosen)
            {
                if (next.Layer(id) is not { Text: not null } layer) continue;
                next = next.Replacing(layer with { Text = null });
                changed = true;
            }
            return changed ? (next, null) : null;
        });
    }

    public bool CanRasterizeText => CanEdit && _chosen.Any(id => _document?.Layer(id)?.Text is not null);

    /// <summary>
    /// Sets a new text layer in one step, without an edit box — for callers that have the words
    /// already, such as the self-test and automation.
    /// </summary>
    public Guid? PlaceText(Point document, string words, LayerText? style = null)
    {
        if (style is not null) TextStyle = style with { Text = "", Rendered = null, Runs = null };
        if (AddTextLayer(document, "") is not Guid id) return null;
        UpdateEditedText(words);
        EndTextEdit(commit: true);
        return _document?.Layer(id) is null ? null : id;
    }
}
