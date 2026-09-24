using Compositor_korean_win.Core;
using Vortice.Mathematics;
using Point = Compositor_korean_win.Core.Point;
using Rect = Compositor_korean_win.Core.Rect;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The open adjustment's or filter's settings — upstream's <c>FilterSheet</c>, with the
/// adjustments' own sheets folded into it.
/// </summary>
/// <remarks>
/// Holds nothing of its own: every control reads the canvas's open edit and writes a changed copy
/// back (<see cref="CanvasView.FilterSettings"/>), which moves the preview.
/// </remarks>
internal sealed partial class FilterSheet(CanvasView canvas, Chrome host) : Sheet
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

    public override double Width => Command switch
    {
        FilterCommand.HueSaturation => 460,
        FilterCommand.Levels => 440,
        FilterCommand.Curves => 400,
        _ => 380,
    };

    /// <summary>An eyedropper or the targeted adjustment is on, so a click on the image is the sheet's.</summary>
    public override bool UsesCanvas => _levelsSampling is not null || _hueSampling is not null || _hueTargeting;

    public override void CanvasPress(Point document, bool control)
    {
        if (_levelsSampling is not null) SampleLevels(document);
        else if (_hueSampling is not null) SampleHue(document);
        else if (_hueTargeting) BeginTargeting(document);
    }

    public override void CanvasDrag(Point document, double across, bool control)
    {
        if (_levelsSampling is not null) SampleLevels(document);
        else if (_hueTargeting) DragTargeting(across, control);
    }

    public override void CanvasRelease() => _hueTarget = null;

    public override bool? Preview
    {
        get => canvas.FilterPreviewOn;
        set => canvas.FilterPreviewOn = value ?? true;
    }

    public override Action Reset => () =>
    {
        FilterCommand command = Command;
        _curveSelected = null;
        Settings = CanvasView.IsAdjustment(command)
            ? Settings with { Adjustment = CanvasView.StartingAdjustment(command, Settings.Seed) }
            : new FilterSettings { Seed = Settings.Seed };
    };

    /// <summary>Remove Background has nothing to keep until the model has answered.</summary>
    public override bool CanAccept => Command != FilterCommand.RemoveBackground || canvas.BackgroundReady;

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

            case FilterCommand.Pinch or FilterCommand.Spherize:
                layout.Slider(Localizer.Text(TextKey.LabelAmount), Settings.Strength, -100, 100, 0,
                              value => Settings = Settings with { Strength = value }, "%");
                break;

            case FilterCommand.Twirl:
                layout.Slider(Localizer.Text(TextKey.LabelAngle), Settings.TwirlAngle, -999, 999, 0,
                              value => Settings = Settings with { TwirlAngle = value }, "°");
                break;

            case FilterCommand.Wave:
                layout.Slider(Localizer.Text(TextKey.LabelWavelength), Settings.Wavelength, 2, 999, 0,
                              value => Settings = Settings with { Wavelength = value }, Px, logarithmic: true);
                layout.Slider(Localizer.Text(TextKey.LabelAmplitude), Settings.Amplitude, 0, 999, 0,
                              value => Settings = Settings with { Amplitude = value }, Px);
                break;

            case FilterCommand.PolarCoordinates:
                layout.Choice(null,
                [
                    (Localizer.Text(TextKey.PolarFromRectangular), Settings.ToPolar, () => Settings = Settings with { ToPolar = true }),
                    (Localizer.Text(TextKey.PolarToRectangular), !Settings.ToPolar, () => Settings = Settings with { ToPolar = false }),
                ]);
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

            case FilterCommand.Curves:
                Curves(layout);
                break;

            case FilterCommand.GradientMap:
                GradientMap(layout);
                break;

            case FilterCommand.RemoveBackground:
                RemoveBackground(layout);
                break;
        }

        if (canvas.FilterLimitedToSelection) layout.Note(Localizer.Text(TextKey.NoteLimitedToSelection));
    }

    /// <summary>
    /// Upstream's Remove Background sheet: what it does, Basic or Advanced, and Advanced's three
    /// ways of working the model's mask — none of which runs the model again.
    /// </summary>
    private void RemoveBackground(SheetLayout layout)
    {
        layout.Note(Localizer.Text(TextKey.NoteRemoveBackground));

        if (canvas.BackgroundWorking) layout.Note(Localizer.Text(TextKey.NoteFindingSubject));
        else if (canvas.BackgroundFailure is string failure) layout.Note(failure, warning: true);

        BackgroundSettings background = Settings.Background;
        void Set(BackgroundSettings changed) => Settings = Settings with { Background = changed };

        layout.Choice(Localizer.Text(TextKey.LabelQuality),
        [
            (Localizer.Text(TextKey.QualityBasic), background.Quality == BackgroundQuality.Basic,
             () => Set(background with { Quality = BackgroundQuality.Basic })),
            (Localizer.Text(TextKey.QualityAdvanced), background.Quality == BackgroundQuality.Advanced,
             () => Set(background with { Quality = BackgroundQuality.Advanced })),
        ]);

        if (background.Quality != BackgroundQuality.Advanced) return;

        layout.Slider(Localizer.Text(TextKey.LabelRefine), background.Refine, 0, 40, 0,
                      value => Set(Settings.Background with { Refine = value }), Px);
        layout.Slider(Localizer.Text(TextKey.LabelMatteContrast), background.Contrast, 0, 100, 0,
                      value => Set(Settings.Background with { Contrast = value }), "%");
        layout.Slider(Localizer.Text(TextKey.LabelShiftEdge), background.ShiftEdge, -10, 10, 0,
                      value => Set(Settings.Background with { ShiftEdge = value }), Px);
    }

    private void GradientMap(SheetLayout layout)
    {
        GradientMapSettings map = Adjustment.GradientMap;
        void Set(GradientMapSettings changed) => Adjustment = Adjustment with { GradientMapSettings = changed };
        Ui ui = layout.Ui;

        (AdjustmentColor dark, AdjustmentColor light) = map.Ends;
        layout.Custom(22, area =>
        {
            ui.Gradient(area, vertical: false, Paint(dark), Paint(light));
            ui.Frame(area, Ui.Line);
        });

        layout.Custom(30, area =>
        {
            Swatch(ui, new Rect(area.X, area.Y, area.Width / 2, area.Height), TextKey.LabelShadows, map.Shadows,
                   colour => Set(Adjustment.GradientMap with { Shadows = colour }));
            Swatch(ui, new Rect(area.X + area.Width / 2, area.Y, area.Width / 2, area.Height), TextKey.LabelHighlights,
                   map.Highlights, colour => Set(Adjustment.GradientMap with { Highlights = colour }));
        });

        layout.Check(Localizer.Text(TextKey.LabelReverse), map.Reversed, () => Set(map with { Reversed = !map.Reversed }));
    }

    /// <summary>One end's colour, which opens the colour picker; the map follows the picker as it moves.</summary>
    private void Swatch(Ui ui, Rect area, TextKey label, AdjustmentColor colour, Action<AdjustmentColor> set)
    {
        var box = new Rect(area.X, area.Y + (area.Height - ui.P(24)) / 2, ui.P(24), ui.P(24));
        ui.Fill(box, Paint(colour));
        ui.Frame(box, Ui.Ink);
        ui.Text(Localizer.Text(label), new Rect(box.MaxX + ui.P(8), area.Y, area.Width - box.Width - ui.P(8), area.Height), Ui.Ink);
        ui.Area(new Rect(area.X, area.Y, area.Width - ui.P(8), area.Height), () =>
            host.Open(new ColourSheet(label, (colour.Red, colour.Green, colour.Blue),
                                      picked => set(new AdjustmentColor(picked.Red, picked.Green, picked.Blue)),
                                      canvas.CompositeColour)));
    }

    private static Color4 Paint(AdjustmentColor colour) =>
        new((float)colour.Red, (float)colour.Green, (float)colour.Blue, 1);

    private static string Px => Localizer.Text(TextKey.UnitPx);
}
