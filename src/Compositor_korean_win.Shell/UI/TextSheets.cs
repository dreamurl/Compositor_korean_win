using Compositor_korean_win.Core;
using Vortice.Mathematics;
using Rect = Compositor_korean_win.Core.Rect;

namespace Compositor_korean_win.Shell;

/// <summary>Photoshop's Warp Text dialog: a style, how far it bends, and two distortions.</summary>
/// <remarks>
/// Every change sets the text again at once, and the whole visit is one history step
/// (<see cref="CanvasView.BeginSession"/>); Cancel puts the warp back as it was.
/// </remarks>
internal sealed class TextWarpSheet : Sheet
{
    private static readonly TextWarpStyle[] Styles = Enum.GetValues<TextWarpStyle>();

    private readonly CanvasView _canvas;
    private readonly Chrome _host;
    private readonly TextWarp? _initial;
    private TextWarp _warp;

    public TextWarpSheet(CanvasView canvas, Chrome host)
    {
        _canvas = canvas;
        _host = host;
        _initial = canvas.ShownTextStyle.Warp;
        _warp = _initial ?? new TextWarp { Style = TextWarpStyle.None };
        canvas.BeginSession(TextKey.HistoryTextStyle);
    }

    public override string Title => Localizer.Text(TextKey.TitleWarpText);

    public override double Width => 420;

    public static TextKey Name(TextWarpStyle style) => style switch
    {
        TextWarpStyle.Arc => TextKey.WarpArc,
        TextWarpStyle.ArcLower => TextKey.WarpArcLower,
        TextWarpStyle.ArcUpper => TextKey.WarpArcUpper,
        TextWarpStyle.Arch => TextKey.WarpArch,
        TextWarpStyle.Bulge => TextKey.WarpBulge,
        TextWarpStyle.ShellLower => TextKey.WarpShellLower,
        TextWarpStyle.ShellUpper => TextKey.WarpShellUpper,
        TextWarpStyle.Flag => TextKey.WarpFlag,
        TextWarpStyle.Wave => TextKey.WarpWave,
        TextWarpStyle.Fish => TextKey.WarpFish,
        TextWarpStyle.Rise => TextKey.WarpRise,
        TextWarpStyle.Fisheye => TextKey.WarpFisheye,
        TextWarpStyle.Inflate => TextKey.WarpInflate,
        TextWarpStyle.Squeeze => TextKey.WarpSqueeze,
        TextWarpStyle.Twist => TextKey.WarpTwist,
        _ => TextKey.WarpNone,
    };

    private void Set(TextWarp warp)
    {
        _warp = warp;
        TextWarp? stored = warp.Style == TextWarpStyle.None ? null : warp;
        _canvas.ChangeTextStyle(text => text with { Warp = stored });
    }

    public override void Content(SheetLayout layout)
    {
        layout.Dropdown(Localizer.Text(TextKey.LabelStyle), Localizer.Text(Name(_warp.Style)), () =>
        {
            int picked = _host.Popup([.. Styles.Select(style => (Localizer.Text(Name(style)), style == _warp.Style))],
                                     separatorsAfter: [0, 3, 7, 10]);
            if (picked >= 0) Set(_warp with { Style = Styles[picked] });
        });

        if (_warp.Style == TextWarpStyle.None) return;
        string percent = Localizer.Text(TextKey.UnitPercent);
        layout.Slider(Localizer.Text(TextKey.LabelBend), _warp.Bend, -100, 100, 0, value => Set(_warp with { Bend = value }), percent);
        layout.Slider(Localizer.Text(TextKey.LabelHorizontalDistortion), _warp.Horizontal, -100, 100, 0,
                      value => Set(_warp with { Horizontal = value }), percent);
        layout.Slider(Localizer.Text(TextKey.LabelVerticalDistortion), _warp.Vertical, -100, 100, 0,
                      value => Set(_warp with { Vertical = value }), percent);
    }

    public override void Accept() => _canvas.EndSession(keep: true);

    public override void Cancel()
    {
        _canvas.ChangeTextStyle(text => text with { Warp = _initial });
        _canvas.EndSession(keep: false);
    }
}

/// <summary>
/// Layer Style: a drop shadow, an outer glow and a stroke, each switched on with its box and set
/// underneath it — Photoshop's dialog cut to the three effects posters use most.
/// </summary>
internal sealed class LayerStyleSheet : Sheet
{
    private readonly CanvasView _canvas;
    private readonly Chrome _host;
    private LayerEffects _effects;

    public LayerStyleSheet(CanvasView canvas, Chrome host)
    {
        _canvas = canvas;
        _host = host;
        _effects = canvas.ActiveEffects ?? new LayerEffects();
        canvas.BeginSession(TextKey.HistoryLayerStyle);
    }

    public override string Title => Localizer.Text(TextKey.HistoryLayerStyle);

    public override double Width => 460;

    private void Set(LayerEffects effects)
    {
        _effects = effects;
        _canvas.SetEffects(effects);
    }

    public override void Content(SheetLayout layout)
    {
        string px = "px", percent = Localizer.Text(TextKey.UnitPercent);

        // Drop Shadow.
        ShadowEffect? shadow = _effects.Shadow;
        layout.Check(Localizer.Text(TextKey.EffectShadow), shadow is { Enabled: true },
                     () => Set(_effects with { Shadow = shadow is { Enabled: true } ? shadow with { Enabled = false } : (shadow ?? new ShadowEffect()) with { Enabled = true } }));
        if (shadow is { Enabled: true } s)
        {
            Colour(layout, (s.Red, s.Green, s.Blue), c => Set(_effects with { Shadow = _effects.Shadow! with { Red = c.R / 255.0, Green = c.G / 255.0, Blue = c.B / 255.0 } }));
            layout.Slider(Localizer.Text(TextKey.LabelOpacity), s.Opacity * 100, 0, 100, 0,
                          v => Set(_effects with { Shadow = _effects.Shadow! with { Opacity = v / 100 } }), percent);
            layout.Slider(Localizer.Text(TextKey.LabelAngle), s.Angle, -180, 180, 0,
                          v => Set(_effects with { Shadow = _effects.Shadow! with { Angle = v } }), "°");
            layout.Slider(Localizer.Text(TextKey.LabelDistance), s.Distance, 0, 300, 0,
                          v => Set(_effects with { Shadow = _effects.Shadow! with { Distance = v } }), px);
            layout.Slider(Localizer.Text(TextKey.LabelSpread), s.Spread * 100, 0, 100, 0,
                          v => Set(_effects with { Shadow = _effects.Shadow! with { Spread = v / 100 } }), percent);
            layout.Slider(Localizer.Text(TextKey.LabelSize), s.Size, 0, 250, 0,
                          v => Set(_effects with { Shadow = _effects.Shadow! with { Size = v } }), px);
        }

        // Outer Glow.
        GlowEffect? glow = _effects.Glow;
        layout.Check(Localizer.Text(TextKey.EffectGlow), glow is { Enabled: true },
                     () => Set(_effects with { Glow = glow is { Enabled: true } ? glow with { Enabled = false } : (glow ?? new GlowEffect()) with { Enabled = true } }));
        if (glow is { Enabled: true } g)
        {
            Colour(layout, (g.Red, g.Green, g.Blue), c => Set(_effects with { Glow = _effects.Glow! with { Red = c.R / 255.0, Green = c.G / 255.0, Blue = c.B / 255.0 } }));
            layout.Slider(Localizer.Text(TextKey.LabelOpacity), g.Opacity * 100, 0, 100, 0,
                          v => Set(_effects with { Glow = _effects.Glow! with { Opacity = v / 100 } }), percent);
            layout.Slider(Localizer.Text(TextKey.LabelSpread), g.Spread * 100, 0, 100, 0,
                          v => Set(_effects with { Glow = _effects.Glow! with { Spread = v / 100 } }), percent);
            layout.Slider(Localizer.Text(TextKey.LabelSize), g.Size, 0, 250, 0,
                          v => Set(_effects with { Glow = _effects.Glow! with { Size = v } }), px);
        }

        // Stroke.
        StrokeEffect? stroke = _effects.Stroke;
        layout.Check(Localizer.Text(TextKey.EffectStroke), stroke is { Enabled: true },
                     () => Set(_effects with { Stroke = stroke is { Enabled: true } ? stroke with { Enabled = false } : (stroke ?? new StrokeEffect()) with { Enabled = true } }));
        if (stroke is { Enabled: true } k)
        {
            Colour(layout, (k.Red, k.Green, k.Blue), c => Set(_effects with { Stroke = _effects.Stroke! with { Red = c.R / 255.0, Green = c.G / 255.0, Blue = c.B / 255.0 } }));
            layout.Slider(Localizer.Text(TextKey.LabelSize), k.Size, 1, 250, 0,
                          v => Set(_effects with { Stroke = _effects.Stroke! with { Size = v } }), px);
            layout.Choice(Localizer.Text(TextKey.LabelPosition),
            [
                (Localizer.Text(TextKey.StrokeOutside), k.Position == StrokePosition.Outside,
                 () => Set(_effects with { Stroke = _effects.Stroke! with { Position = StrokePosition.Outside } })),
                (Localizer.Text(TextKey.StrokeInside), k.Position == StrokePosition.Inside,
                 () => Set(_effects with { Stroke = _effects.Stroke! with { Position = StrokePosition.Inside } })),
                (Localizer.Text(TextKey.StrokeCenter), k.Position == StrokePosition.Center,
                 () => Set(_effects with { Stroke = _effects.Stroke! with { Position = StrokePosition.Center } })),
            ]);
            layout.Slider(Localizer.Text(TextKey.LabelOpacity), k.Opacity * 100, 0, 100, 0,
                          v => Set(_effects with { Stroke = _effects.Stroke! with { Opacity = v / 100 } }), percent);
        }
    }

    /// <summary>A colour swatch that opens the colour picker.</summary>
    private void Colour(SheetLayout layout, (double Red, double Green, double Blue) colour, Action<Rgba> set)
    {
        var current = new Rgba((byte)Math.Round(colour.Red * 255), (byte)Math.Round(colour.Green * 255), (byte)Math.Round(colour.Blue * 255));
        layout.Custom(26, area =>
        {
            Ui ui = layout.Ui;
            string label = Localizer.Text(TextKey.LabelColour);
            ui.Text(label, new Rect(area.X + ui.P(22), area.Y, layout.LabelWidth, area.Height), Ui.Dim);
            var swatch = new Rect(area.X + ui.P(22) + layout.LabelWidth, area.Y + ui.P(3), ui.P(48), area.Height - ui.P(6));
            ui.Fill(swatch, new Color4((float)colour.Red, (float)colour.Green, (float)colour.Blue, 1));
            ui.Frame(swatch, Ui.Line);
            ui.Button(swatch, () => _host.PickColour(TextKey.LabelColour, current, set), null);
        });
    }

    public override void Accept() => _canvas.EndSession(keep: true);

    public override void Cancel() => _canvas.EndSession(keep: false);
}
