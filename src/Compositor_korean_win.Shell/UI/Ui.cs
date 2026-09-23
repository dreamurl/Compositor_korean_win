using System.Numerics;
using Compositor_korean_win.Core;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Point = Compositor_korean_win.Core.Point;
using Rect = Compositor_korean_win.Core.Rect;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The panels' drawing kit: text, buttons, sliders and check boxes drawn with Direct2D, and the
/// clicks that land on them.
/// </summary>
/// <remarks>
/// <para>
/// Immediate mode: the panels are drawn from the document and the settings every frame, and each
/// control records where it is and what a click on it does as it is drawn. There is no tree of
/// widget objects to keep in step with the document, which is the thing that goes wrong in a
/// retained toolkit — a panel showing a layer that has been deleted. docs/windows-port.md §5.1 chose
/// self-drawn widgets for the weight of the binary; this is the least code that gets there.
/// </para>
/// <para>
/// Every piece of text drawn is also written down, unless it is the user's own (a layer's name, a
/// file's name). The self-test reads that list back in each language: in Korean, a label with
/// letters in it and no Korean is a label that was not translated.
/// </para>
/// <para>
/// All geometry is in the window's device pixels; <see cref="Scale"/> turns the design's points
/// into them.
/// </para>
/// </remarks>
internal sealed class Ui : IDisposable
{
    /// <summary>Somewhere a click does something.</summary>
    private sealed record Hit(Rect Area, Action? Click, Action<Point, bool>? Drag, string? Tooltip, Action? DoubleClick);

    private readonly IDWriteFactory _writer = CreateWriter();

    /// <summary>
    /// DirectWrite's factory, made by calling dwrite.dll directly. The wrapper's generic
    /// <c>DWriteCreateFactory&lt;T&gt;</c> finds the interface's ID and constructor by reflection,
    /// which the trimmed NativeAOT build does not keep: it handed back an object with no native
    /// pointer, and the first call on it threw.
    /// </summary>
    private static IDWriteFactory CreateWriter()
    {
        var iid = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
        int result = Win32.DWriteCreateFactory(0, iid, out nint factory);
        if (result < 0 || factory == 0) throw new InvalidOperationException($"DWriteCreateFactory failed: 0x{result:X8}");
        return new IDWriteFactory(factory);
    }
    private readonly List<Hit> _hits = [];
    private List<Hit> _live = [];
    private Hit? _dragging;
    private Hit? _hover;
    private long _hoverSince;
    private Point _pointer;

    private ID2D1DeviceContext? _context;
    private ID2D1SolidColorBrush? _brush;
    private IDWriteTextFormat? _body;
    private IDWriteTextFormat? _bodyCentered;
    private IDWriteTextFormat? _small;
    private IDWriteTextFormat? _title;
    private Language _formatsFor = (Language)(-1);
    private double _formatsScale;

    public static readonly Color4 Window = new(0.14f, 0.14f, 0.145f, 1f);
    public static readonly Color4 Panel = new(0.17f, 0.17f, 0.175f, 1f);
    public static readonly Color4 Raised = new(0.22f, 0.22f, 0.23f, 1f);
    public static readonly Color4 Hover = new(0.27f, 0.27f, 0.28f, 1f);
    public static readonly Color4 Line = new(0.08f, 0.08f, 0.085f, 1f);
    public static readonly Color4 Ink = new(0.90f, 0.90f, 0.91f, 1f);
    public static readonly Color4 Dim = new(0.60f, 0.60f, 0.62f, 1f);
    public static readonly Color4 Accent = new(0.20f, 0.48f, 0.96f, 1f);
    public static readonly Color4 Selected = new(0.16f, 0.30f, 0.55f, 1f);

    /// <summary>Device pixels per point.</summary>
    public double Scale { get; private set; } = 1;

    public ID2D1DeviceContext Context => _context!;

    /// <summary>The text drawn this frame that is the interface's own, not the user's.</summary>
    public List<string> Drawn { get; } = [];

    /// <summary>The tooltips this frame's controls would show, for the same check.</summary>
    public List<string> Tooltips { get; } = [];

    /// <summary>Points to device pixels.</summary>
    public float P(double points) => (float)(points * Scale);

    public void Begin(ID2D1DeviceContext context, double scale)
    {
        _context = context;
        Scale = scale;
        _hits.Clear();
        Drawn.Clear();
        Tooltips.Clear();
        _brush ??= context.CreateSolidColorBrush(Ink);

        if (_formatsFor != Localizer.Current || _formatsScale != scale) MakeFormats();
    }

    /// <summary>Hands this frame's controls to input, and draws the tooltip over everything.</summary>
    public void End()
    {
        _live = [.. _hits];

        if (_hover?.Tooltip is string tip && _dragging is null && Environment.TickCount64 - _hoverSince >= TooltipDelay)
            Tooltip(tip, _pointer);
    }

    /// <summary>How long the pointer rests on a control before its tooltip shows, in milliseconds.</summary>
    public const int TooltipDelay = 500;

    /// <summary>
    /// The interface's font in the chosen language: Malgun Gothic reads Korean the way Windows'
    /// own Korean interface does; Segoe UI is Windows' own for English.
    /// </summary>
    private void MakeFormats()
    {
        _body?.Dispose();
        _bodyCentered?.Dispose();
        _small?.Dispose();
        _title?.Dispose();

        bool korean = Localizer.Current == Language.Korean;
        string family = korean ? "Malgun Gothic" : "Segoe UI";

        IDWriteTextFormat Make(double points, FontWeight weight, TextAlignment alignment)
        {
            IDWriteTextFormat format = _writer.CreateTextFormat(family, weight, Vortice.DirectWrite.FontStyle.Normal,
                                                                FontStretch.Normal, P(points));
            format.TextAlignment = alignment;
            format.ParagraphAlignment = ParagraphAlignment.Center;
            format.WordWrapping = WordWrapping.NoWrap;
            return format;
        }

        _body = Make(12, FontWeight.Normal, TextAlignment.Leading);
        _bodyCentered = Make(12, FontWeight.Normal, TextAlignment.Center);
        _small = Make(10.5, FontWeight.Normal, TextAlignment.Leading);
        _title = Make(12, FontWeight.SemiBold, TextAlignment.Leading);

        _formatsFor = Localizer.Current;
        _formatsScale = Scale;
    }

    // MARK: Drawing

    public void Fill(Rect area, Color4 colour)
    {
        _brush!.Color = colour;
        Context.FillRectangle(Raw(area), _brush);
    }

    public void Frame(Rect area, Color4 colour, float width = 1)
    {
        _brush!.Color = colour;
        Context.DrawRectangle(Raw(Inset(area, width / 2)), _brush, width);
    }

    public void Rule(Point from, Point to, Color4 colour, float width = 1)
    {
        _brush!.Color = colour;
        Context.DrawLine(Vector(from), Vector(to), _brush, width);
    }

    public ID2D1SolidColorBrush Brush(Color4 colour)
    {
        _brush!.Color = colour;
        return _brush;
    }

    public enum TextSize { Body, Small, Title }

    /// <summary>Text in a box, clipped to it, vertically centred.</summary>
    /// <param name="user">The user's own words — a layer or file name — which are not the interface's to translate.</param>
    public void Text(string text, Rect area, Color4 colour, TextSize size = TextSize.Body, bool centred = false, bool user = false)
    {
        if (text.Length == 0) return;
        if (!user) Drawn.Add(text);

        IDWriteTextFormat format = size switch
        {
            TextSize.Small => _small!,
            TextSize.Title => _title!,
            _ => centred ? _bodyCentered! : _body!,
        };

        _brush!.Color = colour;
        Context.DrawText(text, format, Raw(area), _brush, DrawTextOptions.Clip);
    }

    /// <summary>How wide a piece of text is in the body font, in device pixels.</summary>
    public float Measure(string text, TextSize size = TextSize.Body)
    {
        IDWriteTextFormat format = size == TextSize.Small ? _small! : size == TextSize.Title ? _title! : _body!;
        using IDWriteTextLayout layout = _writer.CreateTextLayout(text, format, 10_000, 1_000);
        return layout.Metrics.Width;
    }

    // MARK: Controls

    /// <summary>Marks an area as taken, so a click there does not fall through to the canvas.</summary>
    public void Block(Rect area) => _hits.Add(new Hit(area, null, null, null, null));

    /// <summary>A plain area that does something when clicked, drawn by the caller.</summary>
    public bool Area(Rect area, Action? click, string? tooltip = null, Action? doubleClick = null)
    {
        _hits.Add(new Hit(area, click, null, tooltip, doubleClick));
        if (tooltip is not null) Tooltips.Add(tooltip);
        return IsHovered(area);
    }

    /// <summary>A flat button: its background lights on hover and when active; the caller draws the face.</summary>
    public void Button(Rect area, Action click, string? tooltip, bool active = false, bool enabled = true,
                       Action<Rect>? face = null, string? label = null)
    {
        bool hovered = enabled && Area(area, enabled ? click : null, tooltip);
        if (active) Fill(area, Selected);
        else if (hovered) Fill(area, Hover);

        face?.Invoke(area);
        if (label is not null) Text(label, area, enabled ? Ink : Dim, centred: true);
    }

    /// <summary>
    /// A labelled slider: label, then track, then the value as text. The drag reports a fraction of
    /// the track, 0 to 1; <paramref name="begin"/> and <paramref name="end"/> bracket one drag.
    /// </summary>
    public void Slider(Rect area, string label, double fraction, string value, Action<double> set,
                       Action? begin = null, Action? end = null, float labelWidth = 0)
    {
        labelWidth = labelWidth > 0 ? labelWidth : Measure(label) + P(8);
        float valueWidth = P(52);
        Text(label, new Rect(area.X, area.Y, labelWidth, area.Height), Dim);

        var track = new Rect(area.X + labelWidth, area.Y, Math.Max(P(20), area.Width - labelWidth - valueWidth), area.Height);
        double middle = track.Y + track.Height / 2;
        fraction = Math.Clamp(fraction, 0, 1);

        Fill(new Rect(track.X, middle - P(1.5), track.Width, P(3)), Raised);
        Fill(new Rect(track.X, middle - P(1.5), track.Width * fraction, P(3)), Accent);
        _brush!.Color = Ink;
        Context.FillEllipse(new Ellipse(new Vector2((float)(track.X + track.Width * fraction), (float)middle), P(5), P(5)), _brush);

        Text(value, new Rect(track.MaxX + P(6), area.Y, valueWidth - P(6), area.Height), Ink, user: IsNumeric(value));

        bool started = false;
        _hits.Add(new Hit(track, null, (point, finished) =>
        {
            if (!started)
            {
                started = true;
                begin?.Invoke();
            }
            set(Math.Clamp((point.X - track.X) / track.Width, 0, 1));
            if (finished)
            {
                started = false;
                end?.Invoke();
            }
        }, null, null));
    }

    /// <summary>A box to tick, with its label after it.</summary>
    public void Check(Rect area, string label, bool value, Action toggle)
    {
        Area(area, toggle);
        var box = new Rect(area.X, area.Y + (area.Height - P(14)) / 2, P(14), P(14));
        Fill(box, value ? Accent : Raised);
        Frame(box, Line);
        if (value)
        {
            _brush!.Color = Ink;
            Context.DrawLine(new Vector2((float)box.X + P(3), (float)box.Y + P(7)),
                             new Vector2((float)box.X + P(6), (float)box.Y + P(10.5)), _brush, P(1.8));
            Context.DrawLine(new Vector2((float)box.X + P(6), (float)box.Y + P(10.5)),
                             new Vector2((float)box.X + P(11), (float)box.Y + P(3.5)), _brush, P(1.8));
        }
        Text(label, new Rect(box.MaxX + P(6), area.Y, area.Width - box.Width - P(6), area.Height), Ink);
    }

    private void Tooltip(string text, Point at)
    {
        float width = Measure(text, TextSize.Small) + P(12), height = P(22);
        double x = Math.Max(0, at.X + P(14)), y = at.Y + P(18);
        var area = new Rect(x, y, width, height);
        Fill(area, new Color4(0.08f, 0.08f, 0.09f, 0.96f));
        Frame(area, Raised);
        Text(text, new Rect(x + P(6), y, width - P(6), height), Ink, TextSize.Small);
    }

    // MARK: Input

    /// <summary>A button went down at <paramref name="at"/>. True when a control took it.</summary>
    public bool PointerDown(Point at, bool doubleClick)
    {
        _pointer = at;
        Hit? hit = HitAt(at);
        if (hit is null) return false;

        if (hit.Drag is not null)
        {
            _dragging = hit;
            hit.Drag(at, false);
        }
        else if (doubleClick && hit.DoubleClick is not null)
        {
            hit.DoubleClick();
        }
        else
        {
            hit.Click?.Invoke();
        }
        return true;
    }

    /// <summary>The pointer moved. True when a control is being dragged, or the hover changed.</summary>
    public bool PointerMoved(Point at)
    {
        _pointer = at;
        if (_dragging is not null)
        {
            _dragging.Drag!(at, false);
            return true;
        }

        Hit? hover = HitAt(at);
        if (ReferenceEquals(hover, _hover) || (hover is not null && _hover is not null && hover.Area == _hover.Area)) return false;
        _hover = hover;
        _hoverSince = Environment.TickCount64;
        return true;
    }

    public bool PointerUp(Point at)
    {
        if (_dragging is not Hit dragging) return false;
        _dragging = null;
        dragging.Drag!(at, true);
        return true;
    }

    /// <summary>Whether the pointer is over any control or panel.</summary>
    public bool Covers(Point at) => HitAt(at) is not null;

    /// <summary>Whether a tooltip is waiting for its delay to pass.</summary>
    public bool TooltipPending => _hover?.Tooltip is not null;

    private bool IsHovered(Rect area) => _dragging is null && Contains(area, _pointer);

    private Hit? HitAt(Point at)
    {
        for (int i = _live.Count - 1; i >= 0; i--)
            if (Contains(_live[i].Area, at)) return _live[i];
        return null;
    }

    private static bool Contains(Rect area, Point at) =>
        at.X >= area.X && at.Y >= area.Y && at.X < area.MaxX && at.Y < area.MaxY;

    /// <summary>Numbers and the symbols between them read the same in every language.</summary>
    private static bool IsNumeric(string text) => text.All(c => !char.IsLetter(c));

    private static Rect Inset(Rect area, double by) =>
        new(area.X + by, area.Y + by, Math.Max(0, area.Width - by * 2), Math.Max(0, area.Height - by * 2));

    public static Vortice.RawRectF Raw(Rect rect) =>
        new((float)rect.X, (float)rect.Y, (float)rect.MaxX, (float)rect.MaxY);

    public static Vector2 Vector(Point point) => new((float)point.X, (float)point.Y);

    public void Dispose()
    {
        _body?.Dispose();
        _bodyCentered?.Dispose();
        _small?.Dispose();
        _title?.Dispose();
        _brush?.Dispose();
        _writer.Dispose();
    }
}
