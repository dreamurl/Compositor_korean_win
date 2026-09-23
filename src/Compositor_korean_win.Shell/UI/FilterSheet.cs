using Compositor_korean_win.Core;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The open adjustment's or filter's settings — upstream's <c>FilterSheet</c>, with the
/// adjustments' own sheets folded into it.
/// </summary>
/// <remarks>
/// Holds nothing of its own: every control reads the canvas's open edit and writes a changed copy
/// back (<see cref="CanvasView.FilterSettings"/>), which moves the preview.
/// </remarks>
internal sealed partial class FilterSheet(CanvasView canvas) : Sheet
{
    private CanvasView Canvas => canvas;

    private FilterCommand Command => canvas.OpenFilter ?? FilterCommand.GaussianBlur;

    private FilterSettings Settings
    {
        get => canvas.FilterSettings;
        set => canvas.FilterSettings = value;
    }

    private LayerAdjustment Adjustment
    {
        get => canvas.FilterAdjustment;
        set => canvas.FilterAdjustment = value;
    }

    public override string Title => Localizer.Text(CanvasView.FilterTitle(Command));

    public override double Width => Command is FilterCommand.HueSaturation or FilterCommand.Levels ? 440 : 380;

    public override bool UsesCanvas => canvas.FilterSampler is not null;

    public override bool? Preview
    {
        get => canvas.FilterPreviewOn;
        set => canvas.FilterPreviewOn = value ?? true;
    }

    public override Action Reset => () =>
    {
        FilterCommand command = Command;
        Settings = CanvasView.IsAdjustment(command)
            ? Settings with { Adjustment = CanvasView.StartingAdjustment(command, Settings.Seed) }
            : new FilterSettings { Seed = Settings.Seed };
    };

    public override void Accept() => canvas.FinishFilter(keep: true);

    public override void Cancel() => canvas.FinishFilter(keep: false);

    public override void Content(SheetLayout layout)
    {
        switch (Command)
        {
            case FilterCommand.GaussianBlur:
                layout.Slider(Localizer.Text(TextKey.LabelRadius), Settings.Radius, 0.1, 250, 1,
                              value => Settings = Settings with { Radius = value }, Px, logarithmic: true);
                break;

            case FilterCommand.MotionBlur:
                layout.Slider(Localizer.Text(TextKey.LabelAngle), Settings.Angle, -90, 90, 0,
                              value => Settings = Settings with { Angle = value }, "°");
                layout.Slider(Localizer.Text(TextKey.LabelDistance), Settings.Distance, 1, 2000, 0,
                              value => Settings = Settings with { Distance = value }, Px, logarithmic: true);
                break;

            case FilterCommand.AddNoise:
                layout.Slider(Localizer.Text(TextKey.LabelAmount), Settings.Amount, 0.1, 400, 1,
                              value => Settings = Settings with { Amount = value }, "%", logarithmic: true);
                layout.Choice(null,
                [
                    (Localizer.Text(TextKey.NoiseUniform), !Settings.Gaussian, () => Settings = Settings with { Gaussian = false }),
                    (Localizer.Text(TextKey.NoiseGaussian), Settings.Gaussian, () => Settings = Settings with { Gaussian = true }),
                ]);
                layout.Check(Localizer.Text(TextKey.LabelMonochromatic), Settings.Monochromatic,
                             () => Settings = Settings with { Monochromatic = !Settings.Monochromatic });
                break;

            case FilterCommand.LensCorrection:
                layout.Slider(Localizer.Text(TextKey.LabelRemoveDistortion), Settings.Distortion, -100, 100, 0,
                              value => Settings = Settings with { Distortion = value });
                layout.Note(Localizer.Text(TextKey.NoteLensDistortion));
                break;

            case FilterCommand.Exposure:
            {
                ExposureSettings exposure = Adjustment.Exposure;
                void Set(ExposureSettings changed) => Adjustment = Adjustment with { ExposureSettings = changed };
                layout.Slider(Localizer.Text(TextKey.LabelExposure), exposure.Exposure, -20, 20, 2,
                              value => Set(exposure with { Exposure = value }));
                layout.Slider(Localizer.Text(TextKey.LabelOffset), exposure.Offset, -0.5, 0.5, 4,
                              value => Set(exposure with { Offset = value }));
                layout.Slider(Localizer.Text(TextKey.LabelGamma), exposure.Gamma, 0.01, 9.99, 2,
                              value => Set(exposure with { Gamma = value }), logarithmic: true);
                break;
            }

            case FilterCommand.Grain:
            {
                GrainSettings grain = Adjustment.Grain;
                void Set(GrainSettings changed) => Adjustment = Adjustment with { GrainSettings = changed };
                layout.Slider(Localizer.Text(TextKey.LabelAmount), grain.Amount, 0, 100, 0,
                              value => Set(grain with { Amount = value }));
                layout.Slider(Localizer.Text(TextKey.LabelSize), grain.Size, 0.5, 20, 1,
                              value => Set(grain with { Size = value }), Px, logarithmic: true);
                layout.Slider(Localizer.Text(TextKey.LabelRoughness), grain.Roughness, 0, 100, 0,
                              value => Set(grain with { Roughness = value }));
                break;
            }

            case FilterCommand.HueSaturation:
                HueSaturation(layout);
                break;

            case FilterCommand.Levels:
                Levels(layout);
                break;
        }

        if (canvas.FilterLimitedToSelection) layout.Note(Localizer.Text(TextKey.NoteLimitedToSelection));
    }

    private void HueSaturation(SheetLayout layout)
    {
        HueSaturationSettings hsv = Adjustment.ResolvedHsv;
        RangeAdjustment current = hsv.Current;

        void Set(RangeAdjustment changed) =>
            Adjustment = Adjustment with { HsvSettings = hsv with { Adjustments = hsv.Adjustments.With(hsv.Range, changed) } };

        layout.Slider(Localizer.Text(TextKey.LabelHue), current.Hue, hsv.Colorize ? 0 : -180, hsv.Colorize ? 360 : 180, 0,
                      value => Set(current with { Hue = value }), "°");
        layout.Slider(Localizer.Text(TextKey.LabelSaturation), current.Saturation, hsv.Colorize ? 0 : -100, 100, 0,
                      value => Set(current with { Saturation = value }));
        layout.Slider(Localizer.Text(TextKey.LabelLightness), current.Lightness, -100, 100, 0,
                      value => Set(current with { Lightness = value }));

        // Photoshop starts colorizing at hue 0, saturation 25.
        layout.Check(Localizer.Text(TextKey.LabelColorize), hsv.Colorize, () =>
            Adjustment = Adjustment with
            {
                HsvSettings = hsv.Colorize ? new HueSaturationSettings() : HueSaturationSettings.ColorizeStart,
            });
    }

    private static string Px => Localizer.Text(TextKey.UnitPx);
}
