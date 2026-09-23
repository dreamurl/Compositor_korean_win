using Compositor_korean_win.Core;
using Vortice.Mathematics;
using Point = Compositor_korean_win.Core.Point;
using Rect = Compositor_korean_win.Core.Rect;

namespace Compositor_korean_win.Shell;

/// <summary>
/// Curves on the filter sheet — upstream's <c>CurvesControls</c>: a channel, the curve on a grid,
/// a click to add a handle and a drag to move one, and the handle's numbers.
/// </summary>
internal sealed partial class FilterSheet
{
    /// <summary>The most handles a curve may have — upstream's limit, which its project format checks.</summary>
    private const int MaximumCurvePoints = 32;

    private int? _curveSelected;

    private void Curves(SheetLayout layout)
    {
        CurvesSettings curves = Adjustment.Curves;
        int channel = (int)curves.Channel;
        EquatableList<CurvePoint> points = curves.Channels[channel];

        void SetPoints(IEnumerable<CurvePoint> changed)
        {
            CurvesSettings now = Adjustment.Curves;
            EquatableList<CurvePoint>[] channels = [.. now.Channels];
            channels[(int)now.Channel] = new EquatableList<CurvePoint>([.. changed]);
            Adjustment = Adjustment with { Curves = now with { Channels = new EquatableList<EquatableList<CurvePoint>>(channels) } };
        }

        layout.Choice(null,
        [
            .. Enum.GetValues<LevelsChannel>().Select(each => (ChannelName(each), each == curves.Channel, (Action)(() =>
            {
                _curveSelected = null;
                Adjustment = Adjustment with { Curves = Adjustment.Curves with { Channel = each } };
            }))),
        ]);

        Ui ui = layout.Ui;
        layout.Custom(300, area => Graph(ui, new Rect(area.X, area.Y, area.Width, area.Height), curves, channel, SetPoints));
        layout.Note(Localizer.Text(TextKey.NoteCurvesHelp));

        if (_curveSelected is int selected && selected < points.Count)
        {
            CurvePoint point = points[selected];
            layout.Numbers(
                (Localizer.Text(TextKey.LabelCurveInput), point.X, 0, value =>
                {
                    List<CurvePoint> list = [.. Adjustment.Curves.Channels[(int)Adjustment.Curves.Channel]];
                    if (selected <= 0 || selected >= list.Count - 1) return; // the ends stay at 0 and 255
                    list[selected] = list[selected] with { X = Math.Clamp(value, list[selected - 1].X + 1, list[selected + 1].X - 1) };
                    SetPoints(list);
                }),
                (Localizer.Text(TextKey.LabelCurveOutput), point.Y, 0, value =>
                {
                    List<CurvePoint> list = [.. Adjustment.Curves.Channels[(int)Adjustment.Curves.Channel]];
                    list[selected] = list[selected] with { Y = Math.Clamp(value, 0, 255) };
                    SetPoints(list);
                }));
        }

        var buttons = new List<(string, bool, Action)>();
        if (_curveSelected is int chosen && chosen > 0 && chosen < points.Count - 1)
        {
            buttons.Add((Localizer.Text(TextKey.CurvesRemovePoint), false, () =>
            {
                List<CurvePoint> list = [.. Adjustment.Curves.Channels[(int)Adjustment.Curves.Channel]];
                if (chosen >= list.Count - 1) return;
                list.RemoveAt(chosen);
                _curveSelected = null;
                SetPoints(list);
            }));
        }
        buttons.Add((Localizer.Text(TextKey.CurvesResetCurve), false, () =>
        {
            _curveSelected = null;
            SetPoints([new CurvePoint(0, 0), new CurvePoint(255, 255)]);
        }));
        layout.Buttons(null, [.. buttons]);
    }

    private void Graph(Ui ui, Rect area, CurvesSettings curves, int channel, Action<IEnumerable<CurvePoint>> set)
    {
        // Square, however wide the sheet, so a 45° line is the identity.
        double side = Math.Min(area.Width, area.Height);
        var graph = new Rect(area.X + (area.Width - side) / 2, area.Y, side, side);
        Point At(double x, double y) => new(graph.X + x / 255 * graph.Width, graph.MaxY - y / 255 * graph.Height);

        ui.Fill(graph, new Color4(0.09f, 0.09f, 0.095f, 1f));
        for (int i = 1; i < 4; i++)
        {
            double f = i / 4.0;
            ui.Rule(new Point(graph.X + f * graph.Width, graph.Y), new Point(graph.X + f * graph.Width, graph.MaxY), Ui.Raised);
            ui.Rule(new Point(graph.X, graph.Y + f * graph.Height), new Point(graph.MaxX, graph.Y + f * graph.Height), Ui.Raised);
        }
        ui.Rule(At(0, 0), At(255, 255), Ui.Raised);

        Color4 ink = channel == 0 ? Ui.Ink : ChannelColours[channel];
        Point previous = At(0, curves.Value(0, channel));
        for (int x = 4; x <= 256; x += 4)
        {
            double input = Math.Min(255, x);
            Point next = At(input, Math.Clamp(curves.Value(input, channel), 0, 255));
            ui.Rule(previous, next, ink, ui.P(2));
            previous = next;
        }

        EquatableList<CurvePoint> points = curves.Channels[channel];
        for (int i = 0; i < points.Count; i++)
            ui.Dot(At(points[i].X, points[i].Y), ui.P(4), _curveSelected == i ? Ui.Accent : Ui.Ink, Ui.Line);
        ui.Frame(graph, Ui.Line);

        // Press near a handle to move it; press elsewhere on the curve's span to add one there.
        int? dragging = null;
        ui.Drag(graph, (point, finished) =>
        {
            (double fx, double fy) = RectFractions.Of(graph, point);
            double x = fx * 255, y = (1 - fy) * 255;
            List<CurvePoint> list = [.. Adjustment.Curves.Channels[channel]];

            if (dragging is null)
            {
                double reach = 14 * 255 / Math.Max(1, graph.Width / ui.Scale);
                int nearest = Enumerable.Range(0, list.Count).MinBy(k => Math.Pow(list[k].X - x, 2) + Math.Pow(list[k].Y - y, 2));
                if (Math.Sqrt(Math.Pow(list[nearest].X - x, 2) + Math.Pow(list[nearest].Y - y, 2)) < reach)
                {
                    dragging = nearest;
                }
                else if (list.Count < MaximumCurvePoints && x is > 1 and < 254 && list.All(p => Math.Abs(p.X - x) > 1))
                {
                    list.Add(new CurvePoint(Math.Round(x), Math.Round(y)));
                    list.Sort((a, b) => a.X.CompareTo(b.X));
                    dragging = list.FindIndex(p => p.X == Math.Round(x));
                }
            }

            if (dragging is int held && held >= 0 && held < list.Count)
            {
                _curveSelected = held;
                double moved = held > 0 && held < list.Count - 1
                    ? Math.Clamp(Math.Round(x), list[held - 1].X + 1, list[held + 1].X - 1)
                    : list[held].X;
                list[held] = new CurvePoint(moved, Math.Round(Math.Clamp(y, 0, 255)));
                set(list);
            }

            if (finished) dragging = null;
        });
    }
}
