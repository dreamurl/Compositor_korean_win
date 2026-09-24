using Compositor_korean_win.Core;
using Point = Compositor_korean_win.Core.Point;
using Rect = Compositor_korean_win.Core.Rect;

namespace Compositor_korean_win.Shell;

/// <summary>
/// A panel that floats over the window while something is being set — a filter, a canvas size, a
/// colour — with Cancel and OK along its foot.
/// </summary>
/// <remarks>
/// <para>
/// Upstream's sheets are SwiftUI views in a floating panel beside the canvas; these are drawn with
/// the same kit as the rest of the window (<see cref="Ui"/>), which is what lets the self-test read
/// every word on them in each language, and keeps them in the window's own greys rather than
/// Windows' dialog grey.
/// </para>
/// <para>
/// A sheet is modal to the rest of the window: the panels round the canvas stop taking clicks while
/// one is open. The canvas does too, unless the sheet samples from it (<see cref="UsesCanvas"/>) —
/// Levels' eyedroppers, Hue/Saturation's colour ranges.
/// </para>
/// </remarks>
internal abstract class Sheet
{
    /// <summary>The title across the top, in the current language.</summary>
    public abstract string Title { get; }

    /// <summary>How wide the sheet is, in points.</summary>
    public virtual double Width => 380;

    /// <summary>The controls, laid out top to bottom. Called twice a frame: once to measure, once to draw.</summary>
    public abstract void Content(SheetLayout layout);

    /// <summary>OK, or Enter.</summary>
    public abstract void Accept();

    /// <summary>Cancel, or Escape.</summary>
    public abstract void Cancel();

    public virtual bool CanAccept => true;

    /// <summary>Whether the sheet takes clicks on the canvas while it is open — an eyedropper's.</summary>
    public virtual bool UsesCanvas => false;

    /// <summary>A button went down on the canvas, at this document point.</summary>
    public virtual void CanvasPress(Point document, bool control) { }

    /// <summary>
    /// The pointer moved with the button down: the document point, and how far it has moved across
    /// since the press, in points — a targeted adjustment turns that into a value.
    /// </summary>
    public virtual void CanvasDrag(Point document, double across, bool control) { }

    public virtual void CanvasRelease() { }

    /// <summary>The Preview box's state, or null for a sheet without one.</summary>
    public virtual bool? Preview
    {
        get => null;
        set { }
    }

    /// <summary>What Reset does, or null for a sheet without it.</summary>
    public virtual Action? Reset => null;

    /// <summary>The label on the accepting button: OK unless the sheet says otherwise.</summary>
    public virtual TextKey AcceptLabel => TextKey.DialogOk;

    /// <summary>Whether the foot includes Cancel as well as the accepting button.</summary>
    public virtual bool ShowsCancel => true;

    /// <summary>Set by the host when the sheet has gone, so a sheet can close itself.</summary>
    public bool IsClosed { get; set; }
}

/// <summary>A message that stays inside the application window until it is acknowledged.</summary>
internal sealed class NoticeSheet(string title, string message, bool warning = false) : Sheet
{
    public override string Title => title;
    public override double Width => 500;
    public override bool ShowsCancel => false;

    public override void Content(SheetLayout layout) => layout.Note(message, warning);

    public override void Accept() { }
    public override void Cancel() { }
}

/// <summary>
/// Stacks a sheet's controls down its width: a row at a time, each label in a column as wide as
/// the widest.
/// </summary>
/// <remarks>
/// The sheet is drawn twice a frame, first with <see cref="Measuring"/> set, which only moves down
/// the page. That is how the sheet's ground, drawn first, is already the height of what goes on it,
/// and how the label column is known before the first label is drawn.
/// </remarks>
internal sealed class SheetLayout
{
    private const double RowPoints = 28, GapPoints = 10;

    public SheetLayout(Ui ui, Rect area, bool measuring, float labelWidth)
    {
        Ui = ui;
        X = area.X;
        Y = area.Y;
        Width = area.Width;
        Top = area.Y;
        Measuring = measuring;
        LabelWidth = labelWidth;
    }

    public Ui Ui { get; }
    public bool Measuring { get; }
    public double X { get; }
    public double Width { get; }
    public double Y { get; private set; }
    private double Top { get; }

    /// <summary>How far down the page the controls reach, in device pixels.</summary>
    public double Height => Math.Max(0, Y - Top - Ui.P(GapPoints));

    /// <summary>The label column: while measuring, the widest label so far; while drawing, that width.</summary>
    public float LabelWidth { get; private set; }

    /// <summary>A strip of the page this many points tall, and the gap under it.</summary>
    public Rect Take(double points) => TakePixels(Ui.P(points));

    public Rect TakePixels(double pixels)
    {
        var area = new Rect(X, Y, Width, pixels);
        Y += pixels + Ui.P(GapPoints);
        return area;
    }

    /// <summary>Extra space, in points.</summary>
    public void Space(double points) => Y += Ui.P(points);

    private void Label(string label)
    {
        if (Measuring) LabelWidth = Math.Max(LabelWidth, Ui.Measure(label) + Ui.P(12));
    }

    /// <summary>
    /// A label, a slider and a box to type the exact number into, then the unit. A logarithmic
    /// slider gives the small values, the ones used most, most of its travel — as upstream's do.
    /// </summary>
    public void Slider(string label, double value, double minimum, double maximum, int decimals, Action<double> set,
                       string unit = "", bool logarithmic = false)
    {
        Label(label);
        Rect row = Take(RowPoints);
        if (Measuring) return;

        // The unit column is kept even for a row without one, so every track in a sheet is the same length.
        float fieldWidth = Ui.P(62), unitWidth = Ui.P(34);
        Ui.Text(label, new Rect(row.X, row.Y, LabelWidth, row.Height), Ui.Dim);

        var track = new Rect(row.X + LabelWidth + Ui.P(6), row.Y,
                             Math.Max(Ui.P(20), row.Width - LabelWidth - fieldWidth - unitWidth - Ui.P(24)), row.Height);

        double Fraction(double v) => logarithmic
            ? (Math.Log(Math.Max(minimum, v)) - Math.Log(minimum)) / (Math.Log(maximum) - Math.Log(minimum))
            : (v - minimum) / (maximum - minimum);
        double Value(double fraction) => logarithmic
            ? Math.Exp(Math.Log(minimum) + fraction * (Math.Log(maximum) - Math.Log(minimum)))
            : minimum + fraction * (maximum - minimum);
        double Tidy(double v) => Math.Clamp(Math.Round(v, decimals), minimum, maximum);

        Ui.Track(track, Fraction(value), fraction => set(Tidy(Value(fraction))));

        var field = new Rect(track.MaxX + Ui.P(14), row.Y + Ui.P(2), fieldWidth, row.Height - Ui.P(4));
        Ui.Field(field, label, value, decimals, typed => set(Tidy(typed)));

        if (unit.Length > 0)
            Ui.Text(unit, new Rect(field.MaxX + Ui.P(6), row.Y, unitWidth, row.Height), Ui.Dim, user: !unit.Any(char.IsLetter));
    }

    /// <summary>A labelled box holding a number, without a slider — for sizes, which have no useful range to slide over.</summary>
    public void Number(string label, double value, int decimals, Action<double> set, string unit = "", double fieldPoints = 90)
    {
        Label(label);
        Rect row = Take(RowPoints);
        if (Measuring) return;

        Ui.Text(label, new Rect(row.X, row.Y, LabelWidth, row.Height), Ui.Dim);
        var field = new Rect(row.X + LabelWidth + Ui.P(6), row.Y + Ui.P(2), Ui.P(fieldPoints), row.Height - Ui.P(4));
        Ui.Field(field, label, value, decimals, set);
        if (unit.Length > 0)
            Ui.Text(unit, new Rect(field.MaxX + Ui.P(6), row.Y, Ui.P(60), row.Height), Ui.Dim, user: !unit.Any(char.IsLetter));
    }

    /// <summary>Several labelled number boxes side by side, sharing the row equally.</summary>
    public void Numbers(params (string Label, double Value, int Decimals, Action<double> Set)[] items)
    {
        Rect row = Take(RowPoints);
        if (Measuring) return;

        float fieldWidth = Ui.P(54);
        double part = row.Width / items.Length;
        for (int i = 0; i < items.Length; i++)
        {
            (string label, double value, int decimals, Action<double> set) = items[i];
            double x = row.X + part * i;
            var field = new Rect(x + part - fieldWidth - Ui.P(i == items.Length - 1 ? 0 : 12), row.Y + Ui.P(2),
                                 fieldWidth, row.Height - Ui.P(4));
            Ui.Text(label, new Rect(x, row.Y, field.X - x - Ui.P(4), row.Height), Ui.Dim, Ui.TextSize.Small);
            Ui.Field(field, label, value, decimals, set);
        }
    }

    /// <summary>A label and then buttons sized to their words — one of them lit when it is a mode that is on.</summary>
    public void Buttons(string? label, params (string Text, bool Active, Action Press)[] buttons)
    {
        if (label is not null) Label(label);
        Rect row = Take(26);
        if (Measuring) return;

        double x = row.X;
        if (label is not null)
        {
            Ui.Text(label, new Rect(row.X, row.Y, LabelWidth, row.Height), Ui.Dim);
            x += LabelWidth + Ui.P(6);
        }
        foreach ((string text, bool active, Action press) in buttons)
        {
            var button = new Rect(x, row.Y, Math.Min(row.MaxX - x, Ui.Measure(text) + Ui.P(18)), row.Height);
            Ui.Fill(button, active ? Ui.Selected : Ui.Raised);
            Ui.Button(button, press, null, active: active, label: text);
            x = button.MaxX + Ui.P(4);
        }
    }

    /// <summary>A button showing the current choice, which opens the list to choose from.</summary>
    public void Dropdown(string? label, string value, Action open, double points = 180)
    {
        if (label is not null) Label(label);
        Rect row = Take(26);
        if (Measuring) return;

        double x = row.X;
        if (label is not null)
        {
            Ui.Text(label, new Rect(row.X, row.Y, LabelWidth, row.Height), Ui.Dim);
            x += LabelWidth + Ui.P(6);
        }
        var button = new Rect(x, row.Y, Math.Min(Ui.P(points), row.MaxX - x), row.Height);
        Ui.Fill(button, Ui.Raised);
        Ui.Button(button, open, null);
        Ui.Text(value, new Rect(button.X + Ui.P(10), button.Y, button.Width - Ui.P(28), button.Height), Ui.Ink);
        Ui.Text("▾", new Rect(button.MaxX - Ui.P(18), button.Y, Ui.P(12), button.Height), Ui.Dim);
    }

    public void Check(string label, bool value, Action toggle)
    {
        Rect row = Take(22);
        if (!Measuring) Ui.Check(row, label, value, toggle);
    }

    /// <summary>Mutually exclusive choices, with a label before them when there is one.</summary>
    public void Choice(string? label, (string Text, bool On, Action Pick)[] choices)
    {
        if (label is not null) Label(label);
        Rect row = Take(26);
        if (Measuring) return;

        double x = row.X;
        if (label is not null)
        {
            Ui.Text(label, new Rect(row.X, row.Y, LabelWidth, row.Height), Ui.Dim);
            x += LabelWidth + Ui.P(6);
        }
        Ui.Choice(new Rect(x, row.Y, row.MaxX - x, row.Height), choices);
    }

    /// <summary>A sentence or two of explanation, wrapped to the sheet's width.</summary>
    public void Note(string text, bool warning = false)
    {
        Rect area = TakePixels(Ui.ParagraphHeight(text, Width));
        if (!Measuring) Ui.Paragraph(text, area, warning ? new Vortice.Mathematics.Color4(1f, 0.62f, 0.2f, 1f) : Ui.Dim);
    }

    /// <summary>A strip the sheet draws itself — a histogram, a curve — and nothing while measuring.</summary>
    public Rect Custom(double points, Action<Rect> draw)
    {
        Rect area = Take(points);
        if (!Measuring) draw(area);
        return area;
    }
}

/// <summary>A point in a rectangle as fractions of its width and height, each clamped to 0–1.</summary>
internal static class RectFractions
{
    public static (double X, double Y) Of(Rect area, Point point) =>
        (Math.Clamp((point.X - area.X) / Math.Max(1, area.Width), 0, 1),
         Math.Clamp((point.Y - area.Y) / Math.Max(1, area.Height), 0, 1));
}
