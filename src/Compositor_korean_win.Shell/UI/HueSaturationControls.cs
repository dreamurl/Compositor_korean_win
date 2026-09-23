using Compositor_korean_win.Core;
using Vortice.Mathematics;
using Point = Compositor_korean_win.Core.Point;
using Rect = Compositor_korean_win.Core.Rect;

namespace Compositor_korean_win.Shell;

/// <summary>
/// Hue/Saturation on the filter sheet — upstream's <c>HueSaturationSheet</c> and
/// <c>SpectrumEditor</c>: the colour range, its three sliders, Photoshop's two spectrum bars with
/// the range's band between them, the eyedroppers that move the band, and the targeted adjustment
/// that drags a colour's saturation on the image itself.
/// </summary>
internal sealed partial class FilterSheet
{
    private HueSampleMode? _hueSampling;
    private bool _hueTargeting;

    /// <summary>A targeted drag in progress: the range it changes, and its hue and saturation when it began.</summary>
    private (ColorRange Range, double Hue, double Saturation)? _hueTarget;

    private HueSaturationSettings Hsv
    {
        get => Adjustment.ResolvedHsv;
        set => Adjustment = Adjustment with { HsvSettings = value };
    }

    private void HueSaturation(SheetLayout layout)
    {
        HueSaturationSettings hsv = Hsv;
        RangeAdjustment current = hsv.Current;
        bool spectrum = hsv.Range != ColorRange.Master && !hsv.Colorize;

        void Set(RangeAdjustment changed) => Hsv = hsv with { Adjustments = hsv.Adjustments.With(hsv.Range, changed) };

        if (!hsv.Colorize)
        {
            layout.Dropdown(null, RangeName(hsv.Range), () =>
            {
                int chosen = host.Popup([.. ColorRanges.All.Select(range => (RangeName(range), range == hsv.Range))]);
                if (chosen < 0) return;
                _hueSampling = null;
                Hsv = Hsv with { Range = ColorRanges.All[chosen] };
            });
        }

        layout.Slider(Localizer.Text(TextKey.LabelHue), current.Hue, hsv.Colorize ? 0 : -180, hsv.Colorize ? 360 : 180, 0,
                      value => Set(current with { Hue = value }), "°");
        layout.Slider(Localizer.Text(TextKey.LabelSaturation), current.Saturation, hsv.Colorize ? 0 : -100, 100, 0,
                      value => Set(current with { Saturation = value }));
        layout.Slider(Localizer.Text(TextKey.LabelLightness), current.Lightness, -100, 100, 0,
                      value => Set(current with { Lightness = value }));

        if (spectrum)
        {
            Ui ui = layout.Ui;
            layout.Custom(14, area => Spectrum(ui, area, hsv, after: false));
            layout.Custom(12, area => BandHandles(ui, area, hsv));
            layout.Custom(14, area => Spectrum(ui, area, hsv, after: true));
            layout.Custom(14, area => ui.Text(string.Join("   ", hsv.Band.Handles.Select(h => $"{Math.Round(h)}°")),
                                              area, Ui.Dim, Ui.TextSize.Small, centred: true, user: true));
            layout.Check(Localizer.Text(TextKey.LabelInvertRange), hsv.InvertRange,
                         () => Hsv = Hsv with { InvertRange = !Hsv.InvertRange });

            layout.Buttons(null,
                [.. Enum.GetValues<HueSampleMode>().Select(mode => (HueSampleName(mode), _hueSampling == mode, (Action)(() =>
                {
                    _hueTargeting = false;
                    _hueSampling = _hueSampling == mode ? null : mode;
                })))]);
            if (_hueSampling is HueSampleMode sampling) layout.Note(Localizer.Text(HueSampleHint(sampling)));
        }

        if (!hsv.Colorize)
        {
            layout.Buttons(null, (Localizer.Text(TextKey.HueTargeted), _hueTargeting, () =>
            {
                _hueSampling = null;
                _hueTargeting = !_hueTargeting;
            }));
            if (_hueTargeting) layout.Note(Localizer.Text(TextKey.NoteHueTargeted));
        }

        // Photoshop starts colorizing at hue 0, saturation 25.
        layout.Check(Localizer.Text(TextKey.LabelColorize), hsv.Colorize, () =>
        {
            _hueSampling = null;
            _hueTargeting = false;
            Hsv = hsv.Colorize ? new HueSaturationSettings() : HueSaturationSettings.ColorizeStart;
        });
    }

    /// <summary>The hues round the circle, as they are or as the settings leave them.</summary>
    private static void Spectrum(Ui ui, Rect area, HueSaturationSettings hsv, bool after)
    {
        const int Slices = 72;
        double width = area.Width / Slices;
        for (int slice = 0; slice < Slices; slice++)
        {
            double hue = slice * 360.0 / Slices;
            (double r, double g, double b) = new Hsb(after ? HueSampling.ShiftedHue(hue, hsv) : hue, 1, 1).ToRgb();
            ui.Fill(new Rect(area.X + slice * width, area.Y, width + 0.6, area.Height), new Color4((float)r, (float)g, (float)b, 1));
        }
        ui.Frame(area, Ui.Line);
    }

    /// <summary>The band's four handles: the outer ones are its shoulders, the inner bars its full-strength range.</summary>
    private void BandHandles(Ui ui, Rect area, HueSaturationSettings hsv)
    {
        double[] handles = hsv.Band.Handles;
        for (int i = 0; i < handles.Length; i++)
        {
            double x = area.X + handles[i] / 360 * area.Width;
            if (i is 1 or 2) ui.Fill(new Rect(x - ui.P(1), area.Y, ui.P(2), area.Height), Ui.Ink);
            else ui.Fill(new Rect(x - ui.P(3.5), area.Y + area.Height / 2 - ui.P(2.5), ui.P(7), ui.P(5)), Ui.Ink);
        }

        int? held = null;
        ui.Drag(area, (point, finished) =>
        {
            double degrees = Math.Clamp((point.X - area.X) / area.Width, 0, 1) * 360;
            held ??= Enumerable.Range(0, handles.Length).MinBy(i =>
            {
                double gap = Math.Abs(handles[i] - degrees) % 360;
                return Math.Min(gap, 360 - gap);
            });
            HueSaturationSettings now = Hsv;
            Hsv = now with { Bands = now.Bands.With(now.Range, now.Band.WithHandle(held.Value, degrees)) };
            if (finished) held = null;
        });
    }

    private void SampleHue(Point document)
    {
        if (_hueSampling is not HueSampleMode mode || Canvas.FilterSourceColour(document) is not var (red, green, blue)) return;
        if (HueSampling.HueOf(red, green, blue) is double hue) Hsv = Hsv.Sampled(hue, mode);
    }

    /// <summary>A press on the image: the colour's range becomes the one being changed.</summary>
    private void BeginTargeting(Point document)
    {
        if (Canvas.FilterSourceColour(document) is not var (red, green, blue) || HueSampling.HueOf(red, green, blue) is not double hue) return;

        HueSaturationSettings hsv = Hsv;
        ColorRange range = HueSampling.RangeFor(hsv, hue);
        RangeAdjustment adjustment = hsv.Adjustments.ValueOr(range, new RangeAdjustment());
        _hueTarget = (range, adjustment.Hue, adjustment.Saturation);
        Hsv = hsv with { Range = range };
    }

    /// <summary>Right raises, left lowers, half a unit a point: the saturation, or the hue with Ctrl held.</summary>
    private void DragTargeting(double across, bool control)
    {
        if (_hueTarget is not var (range, hue, saturation)) return;

        HueSaturationSettings hsv = Hsv;
        RangeAdjustment adjustment = hsv.Adjustments.ValueOr(range, new RangeAdjustment());
        adjustment = control
            ? adjustment with { Hue = Math.Clamp(hue + across / 2, -180, 180) }
            : adjustment with { Saturation = Math.Clamp(saturation + across / 2, -100, 100) };
        Hsv = hsv with { Adjustments = hsv.Adjustments.With(range, adjustment) };
    }

    private static string RangeName(ColorRange range) => Localizer.Text(range switch
    {
        ColorRange.Reds => TextKey.RangeReds,
        ColorRange.Yellows => TextKey.RangeYellows,
        ColorRange.Greens => TextKey.RangeGreens,
        ColorRange.Cyans => TextKey.RangeCyans,
        ColorRange.Blues => TextKey.RangeBlues,
        ColorRange.Magentas => TextKey.RangeMagentas,
        _ => TextKey.RangeMaster,
    });

    private static string HueSampleName(HueSampleMode mode) => Localizer.Text(mode switch
    {
        HueSampleMode.Replace => TextKey.HueSample,
        HueSampleMode.Add => TextKey.HueAddToSample,
        _ => TextKey.HueSubtractFromSample,
    });

    private static TextKey HueSampleHint(HueSampleMode mode) => mode switch
    {
        HueSampleMode.Replace => TextKey.NoteHueSample,
        HueSampleMode.Add => TextKey.NoteHueAddToSample,
        _ => TextKey.NoteHueSubtractFromSample,
    };
}
