using Compositor_korean_win.Core;
using Vortice.Mathematics;
using Point = Compositor_korean_win.Core.Point;
using Rect = Compositor_korean_win.Core.Rect;

namespace Compositor_korean_win.Shell;

/// <summary>
/// Levels on the filter sheet: the channel, the histogram with its input handles, the output
/// range, the eyedroppers and Auto — upstream's <c>LevelsSheet</c>.
/// </summary>
internal sealed partial class FilterSheet
{
    /// <summary>The eyedropper that is on, if one is.</summary>
    private LevelsSample? _levelsSampling;

    private static readonly Color4[] ChannelColours =
    [
        new(0.62f, 0.62f, 0.64f, 1f), new(0.86f, 0.30f, 0.28f, 1f), new(0.36f, 0.76f, 0.38f, 1f), new(0.33f, 0.52f, 0.94f, 1f),
    ];

    private void Levels(SheetLayout layout)
    {
        LevelsSettings levels = Adjustment.Levels;
        int channel = (int)levels.Channel;
        LevelRange range = levels.Ranges[channel].Normalized;

        void SetLevels(LevelsSettings changed) => Adjustment = Adjustment with { Levels = changed };
        void SetRange(LevelRange changed)
        {
            LevelRange[] ranges = [.. levels.Ranges];
            ranges[channel] = changed.Normalized;
            SetLevels(levels with { Ranges = new EquatableList<LevelRange>(ranges) });
        }

        layout.Choice(null,
        [
            .. Enum.GetValues<LevelsChannel>().Select(each =>
                (ChannelName(each), each == levels.Channel, (Action)(() => SetLevels(levels with { Channel = each })))),
        ]);

        Ui ui = layout.Ui;

        layout.Custom(130, area => DrawHistogram(ui, area, channel));

        // The gamma handle sits where the midtone lands: halfway through the range raised to the gamma.
        double gammaAt = range.Black + (range.White - range.Black) * Math.Pow(0.5, range.Gamma);
        layout.Custom(14, area => Handles(ui, area, [range.Black, gammaAt, range.White], output: false, (index, level) =>
        {
            level = Math.Round(level);
            SetRange(index switch
            {
                0 => range with { Black = Math.Min(range.White - 1, level) },
                2 => range with { White = Math.Max(range.Black + 1, level) },
                _ => range with
                {
                    Gamma = Math.Log(Math.Clamp((level - range.Black) / (range.White - range.Black), 0.001, 0.999)) / Math.Log(0.5),
                },
            });
        }));

        layout.Numbers(
            (Localizer.Text(TextKey.LabelInputBlack), range.Black, 0, value => SetRange(range with { Black = Math.Min(range.White - 1, value) })),
            (Localizer.Text(TextKey.LabelGamma), range.Gamma, 2, value => SetRange(range with { Gamma = value })),
            (Localizer.Text(TextKey.LabelInputWhite), range.White, 0, value => SetRange(range with { White = Math.Max(range.Black + 1, value) })));

        layout.Space(4);
        layout.Custom(12, area =>
        {
            // The output ramp, black to white, in as many steps as it is pixels wide.
            int steps = Math.Max(1, (int)area.Width);
            for (int i = 0; i < steps; i++)
            {
                float v = (float)i / steps;
                ui.Fill(new Rect(area.X + area.Width * i / steps, area.Y, area.Width / steps + 1, area.Height), new Color4(v, v, v, 1));
            }
            ui.Frame(area, Ui.Line);
        });
        layout.Custom(14, area => Handles(ui, area, [range.OutputBlack, range.OutputWhite], output: true, (index, level) =>
            SetRange(index == 0 ? range with { OutputBlack = Math.Round(level) } : range with { OutputWhite = Math.Round(level) })));

        layout.Numbers(
            (Localizer.Text(TextKey.LabelOutputBlack), range.OutputBlack, 0, value => SetRange(range with { OutputBlack = value })),
            (Localizer.Text(TextKey.LabelOutputWhite), range.OutputWhite, 0, value => SetRange(range with { OutputWhite = value })));

        layout.Space(4);
        layout.Buttons(Localizer.Text(TextKey.LabelSample),
            [.. Enum.GetValues<LevelsSample>().Select(mode =>
                (SampleName(mode), _levelsSampling == mode, (Action)(() => Sample(_levelsSampling == mode ? null : mode))))]);
        if (_levelsSampling is LevelsSample sampling) layout.Note(Localizer.Text(SampleHint(sampling)));

        Histogram? histogram = Canvas.FilterHistogram;
        layout.Buttons(Localizer.Text(TextKey.LabelAuto),
            [.. Enum.GetValues<LevelsAuto>().Select(mode => (AutoName(mode), false, (Action)(() =>
            {
                if (histogram is null) return;
                Sample(null);
                SetLevels(LevelsAutomatic.Auto(mode, histogram) with { Channel = levels.Channel });
            })))]);

        layout.Note(Localizer.Text(Canvas.FilteringLayer ? TextKey.NoteHistogramBelow : TextKey.NoteHistogramOwn));
    }

    /// <summary>Turns an eyedropper on, or all of them off; while one is on, a click on the image sets it.</summary>
    private void Sample(LevelsSample? mode)
    {
        _levelsSampling = mode;
        Canvas.FilterSampler = mode is LevelsSample on
            ? point =>
            {
                if (Canvas.FilterSourceColour(point) is not var (red, green, blue)) return;
                LevelsSettings levels = Adjustment.Levels;
                Adjustment = Adjustment with { Levels = levels.Sampling(red, green, blue, on) };
            }
            : null;
    }

    private void DrawHistogram(Ui ui, Rect area, int channel)
    {
        ui.Fill(area, new Color4(0.09f, 0.09f, 0.095f, 1f));
        if (Canvas.FilterHistogram is Histogram histogram)
        {
            ReadOnlySpan<double> bins = histogram.Channel(channel);
            double peak = LevelsAutomatic.DisplayScale(bins);
            if (peak > 0)
            {
                double width = area.Width / Core.Histogram.BinCount;
                for (int i = 0; i < Core.Histogram.BinCount; i++)
                {
                    double height = area.Height * Math.Min(1, Math.Max(0, bins[i] / peak));
                    if (height <= 0) continue;
                    ui.Fill(new Rect(area.X + i * width, area.MaxY - height, width + 0.6, height), ChannelColours[channel]);
                }
            }
        }
        ui.Frame(area, Ui.Line);
    }

    /// <summary>
    /// Triangles along a strip at 0–255 levels, dragged by whichever is nearest the press. Black, grey
    /// between, white last — the colours say which end each one moves.
    /// </summary>
    private static void Handles(Ui ui, Rect area, double[] levels, bool output, Action<int, double> set)
    {
        double X(double level) => area.X + level / 255 * area.Width;
        for (int i = 0; i < levels.Length; i++)
        {
            Color4 fill = i == 0 ? new Color4(0, 0, 0, 1) : i == levels.Length - 1 ? new Color4(1, 1, 1, 1) : new Color4(0.5f, 0.5f, 0.5f, 1);
            ui.Triangle(new Point(X(levels[i]), area.Y + ui.P(1)), ui.P(11), area.Height - ui.P(3), fill, Ui.Dim);
        }

        int? held = null;
        ui.Drag(new Rect(area.X - ui.P(8), area.Y, area.Width + ui.P(16), area.Height), (point, finished) =>
        {
            double level = Math.Clamp((point.X - area.X) / area.Width * 255, 0, 255);
            held ??= Enumerable.Range(0, levels.Length).OrderBy(i => Math.Abs(levels[i] - level))
                // Handles on top of each other: the one that can move towards the press.
                .ThenBy(i => output ? 0 : level < levels[i] ? i : -i).First();
            set(held.Value, level);
            if (finished) held = null;
        });
    }

    private static string ChannelName(LevelsChannel channel) => Localizer.Text(channel switch
    {
        LevelsChannel.Red => TextKey.ChannelRed,
        LevelsChannel.Green => TextKey.ChannelGreen,
        LevelsChannel.Blue => TextKey.ChannelBlue,
        _ => TextKey.ChannelRgb,
    });

    private static string SampleName(LevelsSample mode) => Localizer.Text(mode switch
    {
        LevelsSample.Black => TextKey.SampleBlack,
        LevelsSample.Gray => TextKey.SampleGray,
        _ => TextKey.SampleWhite,
    });

    private static TextKey SampleHint(LevelsSample mode) => mode switch
    {
        LevelsSample.Black => TextKey.NoteSampleBlack,
        LevelsSample.Gray => TextKey.NoteSampleGray,
        _ => TextKey.NoteSampleWhite,
    };

    private static string AutoName(LevelsAuto mode) => Localizer.Text(mode switch
    {
        LevelsAuto.Contrast => TextKey.AutoContrast,
        LevelsAuto.Color => TextKey.AutoColor,
        _ => TextKey.AutoNeutral,
    });
}
