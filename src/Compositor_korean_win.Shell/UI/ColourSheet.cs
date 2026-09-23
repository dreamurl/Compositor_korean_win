using Compositor_korean_win.Core;
using Vortice.Mathematics;
using Point = Compositor_korean_win.Core.Point;
using Rect = Compositor_korean_win.Core.Rect;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The program's colour picker — upstream's <c>ColorPickerSheet</c>: a saturation and brightness
/// field, a hue strip, the new colour beside the old, RGB and hex boxes, and an eyedropper on the
/// canvas.
/// </summary>
/// <remarks>
/// It replaces Windows' colour dialog, which speaks the language Windows is in rather than the one
/// chosen here, and cannot sample from the canvas. The colour is handed on as it changes, so what
/// it colours — a tool, a Gradient Map end — shows it at once; Cancel hands back the colour it
/// opened with.
/// </remarks>
internal sealed class ColourSheet(
    TextKey title,
    (double Red, double Green, double Blue) initial,
    Action<(double Red, double Green, double Blue)> change,
    Func<Point, (double Red, double Green, double Blue)?>? sample) : Sheet
{
    private Hsb _hsb = Hsb.FromRgb(initial.Red, initial.Green, initial.Blue);

    public override string Title => Localizer.Text(title);

    public override double Width => 470;

    public override bool UsesCanvas => sample is not null;

    private (double Red, double Green, double Blue) Colour => _hsb.ToRgb();

    private void Set(Hsb hsb)
    {
        _hsb = hsb;
        change(Colour);
    }

    private void SetRgb(double red, double green, double blue)
    {
        Hsb next = Hsb.FromRgb(Math.Clamp(red, 0, 1), Math.Clamp(green, 0, 1), Math.Clamp(blue, 0, 1));
        // A grey has no hue of its own; keep the one the strip was on, so the field does not jump to red.
        Set(next.Saturation == 0 ? next with { Hue = _hsb.Hue } : next);
    }

    public override void Accept() { }

    public override void Cancel() => change(initial);

    public override void CanvasPress(Point document, bool control) => Sample(document);

    public override void CanvasDrag(Point document, double across, bool control) => Sample(document);

    private void Sample(Point document)
    {
        if (sample?.Invoke(document) is var (red, green, blue)) SetRgb(red, green, blue);
    }

    public override void Content(SheetLayout layout)
    {
        Ui ui = layout.Ui;
        layout.Custom(220, area =>
        {
            var field = new Rect(area.X, area.Y, area.Height, area.Height);
            var strip = new Rect(field.MaxX + ui.P(12), area.Y, ui.P(20), area.Height);
            Field(ui, field);
            HueStrip(ui, strip);
            Details(ui, new Rect(strip.MaxX + ui.P(18), area.Y, area.MaxX - strip.MaxX - ui.P(18), area.Height));
        });

        if (sample is not null) layout.Note(Localizer.Text(TextKey.NoteColourPickerSample));
    }

    private void Field(Ui ui, Rect field)
    {
        (double r, double g, double b) = new Hsb(_hsb.Hue, 1, 1).ToRgb();
        ui.Gradient(field, vertical: false, new Color4(1, 1, 1, 1), new Color4((float)r, (float)g, (float)b, 1));
        ui.Gradient(field, vertical: true, new Color4(0, 0, 0, 0), new Color4(0, 0, 0, 1));
        ui.Frame(field, Ui.Line);

        var marker = new Point(field.X + _hsb.Saturation * field.Width, field.Y + (1 - _hsb.Brightness) * field.Height);
        ui.Dot(marker, ui.P(5.5), new Color4(0, 0, 0, 0), new Color4(1, 1, 1, 1));

        ui.Drag(field, (point, _) =>
        {
            (double x, double y) = RectFractions.Of(field, point);
            Set(_hsb with { Saturation = x, Brightness = 1 - y });
        });
    }

    private void HueStrip(Ui ui, Rect strip)
    {
        // Red at both ends, round the circle downwards, as Photoshop's picker has it.
        Color4[] hues = [.. Enumerable.Range(0, 7).Select(i =>
        {
            (double r, double g, double b) = new Hsb(360 - i * 60, 1, 1).ToRgb();
            return new Color4((float)r, (float)g, (float)b, 1);
        })];
        ui.Gradient(strip, vertical: true, hues);
        ui.Frame(strip, Ui.Line);

        double y = strip.Y + (1 - _hsb.Hue / 360) * strip.Height;
        ui.Fill(new Rect(strip.X - ui.P(4), y - ui.P(1.5), strip.Width + ui.P(8), ui.P(3)), Ui.Ink);

        ui.Drag(new Rect(strip.X - ui.P(6), strip.Y, strip.Width + ui.P(12), strip.Height), (point, _) =>
        {
            double fraction = RectFractions.Of(strip, point).Y;
            Set(_hsb with { Hue = (1 - fraction) * 360 });
        });
    }

    private void Details(Ui ui, Rect column)
    {
        (double red, double green, double blue) = Colour;
        static Color4 Paint((double Red, double Green, double Blue) c) => new((float)c.Red, (float)c.Green, (float)c.Blue, 1);

        // The new colour over the one the picker opened with; a click on the old one goes back to it.
        float swatch = ui.P(56), half = ui.P(26);
        var now = new Rect(column.X, column.Y, swatch, half);
        var before = new Rect(column.X, now.MaxY, swatch, half);
        ui.Fill(now, Paint(Colour));
        ui.Fill(before, Paint(initial));
        ui.Frame(new Rect(now.X, now.Y, swatch, half * 2), Ui.Line);
        ui.Area(before, () => SetRgb(initial.Red, initial.Green, initial.Blue), Localizer.Text(TextKey.LabelCurrentColour));
        ui.Text(Localizer.Text(TextKey.LabelNewColour), new Rect(now.MaxX + ui.P(8), now.Y, column.MaxX - now.MaxX - ui.P(8), half), Ui.Dim);
        ui.Text(Localizer.Text(TextKey.LabelCurrentColour), new Rect(now.MaxX + ui.P(8), before.Y, column.MaxX - now.MaxX - ui.P(8), half), Ui.Dim);

        double y = before.MaxY + ui.P(18);
        float row = ui.P(28), fieldWidth = ui.P(60), labelWidth = ui.P(22);

        void Channel(TextKey label, double value, Action<double> set)
        {
            ui.Text(Localizer.Text(label), new Rect(column.X, y, labelWidth, row), Ui.Dim);
            ui.Field(new Rect(column.X + labelWidth, y + ui.P(2), fieldWidth, row - ui.P(4)), label.ToString(),
                     Math.Round(value * 255), 0, typed => set(Math.Clamp(typed, 0, 255) / 255));
            y += row + ui.P(4);
        }

        Channel(TextKey.LabelR, red, value => SetRgb(value, green, blue));
        Channel(TextKey.LabelG, green, value => SetRgb(red, value, blue));
        Channel(TextKey.LabelB, blue, value => SetRgb(red, green, value));

        ui.Text("#", new Rect(column.X, y, labelWidth, row), Ui.Dim);
        ui.TextField(new Rect(column.X + labelWidth, y + ui.P(2), ui.P(84), row - ui.P(4)), "hex", Hex(Colour), typed =>
        {
            string digits = typed.TrimStart('#');
            if (digits.Length == 6 && int.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out int value))
                SetRgb((value >> 16 & 0xFF) / 255.0, (value >> 8 & 0xFF) / 255.0, (value & 0xFF) / 255.0);
        }, character => char.IsAsciiHexDigit(character) || character == '#');
    }

    private static string Hex((double Red, double Green, double Blue) colour) =>
        $"{(int)Math.Round(colour.Red * 255):X2}{(int)Math.Round(colour.Green * 255):X2}{(int)Math.Round(colour.Blue * 255):X2}";
}
