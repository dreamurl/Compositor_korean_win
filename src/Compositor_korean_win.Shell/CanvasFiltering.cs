using Compositor_korean_win.Core;
using Point = Compositor_korean_win.Core.Point;
using Size = Compositor_korean_win.Core.Size;

namespace Compositor_korean_win.Shell;

/// <summary>The Image and Filter menus' entries, as the keys reach them for now.</summary>
internal enum FilterCommand
{
    Levels,
    Curves,
    HueSaturation,
    Exposure,
    GradientMap,
    Grain,
    GaussianBlur,
    MotionBlur,
    AddNoise,
    LensCorrection,
}

/// <summary>
/// Trying an adjustment or a filter on the canvas: open it, drag to set it, Enter or Escape.
/// </summary>
/// <remarks>
/// <para>
/// This is the smallest front end that exercises the live preview end to end, and nothing more:
/// the dialogs with their sliders, curves and colour pickers are M6's. Until then one horizontal
/// drag stands for each command's main setting, which is enough to see the preview follow the
/// pointer.
/// </para>
/// <para>
/// Two paths, as upstream has. On its own, a command previews on the chosen layer's pixels and
/// commits into them as one history step. With shift held, an adjustment command adds an adjustment
/// layer above the chosen one instead, which the ordinary compositor already draws — so its preview
/// is simply the document being drawn with the new settings, and Escape takes the layer away again.
/// </para>
/// <list type="table">
/// <item><term>Ctrl+L / Ctrl+M / Ctrl+U</term><description>Levels, Curves, Hue/Saturation (Photoshop's keys)</description></item>
/// <item><term>F2 / F3 / F4</term><description>Exposure, Gradient Map, Grain</description></item>
/// <item><term>F5 / F6 / F7 / F8</term><description>Gaussian Blur, Motion Blur, Add Noise, Lens Correction</description></item>
/// </list>
/// </remarks>
internal sealed partial class CanvasView
{
    /// <summary>View points of drag for the whole of a setting's range either side.</summary>
    private const double DragRange = 300;

    /// <summary>Where a command starts, so something shows before the pointer moves.</summary>
    private const double StartingAmount = 0.25;

    private FilterCommand _command;
    private FilterPreview? _preview;
    private Guid? _adjusting;
    private CanvasDocument? _beforeAdjusting;
    private double _amount;
    private double? _amountAtDrag;
    private Point _dragStart;
    private uint _seed;

    /// <summary>Whether a command is open, which takes the keys and the pointer until it closes.</summary>
    public bool IsFiltering => _preview is not null || _adjusting is not null;

    private static FilterCommand? CommandFor(int key, bool control) => (key, control) switch
    {
        (Win32.VK_L, true) => FilterCommand.Levels,
        (Win32.VK_M, true) => FilterCommand.Curves,
        (Win32.VK_U, true) => FilterCommand.HueSaturation,
        (Win32.VK_F2, false) => FilterCommand.Exposure,
        (Win32.VK_F3, false) => FilterCommand.GradientMap,
        (Win32.VK_F4, false) => FilterCommand.Grain,
        (Win32.VK_F5, false) => FilterCommand.GaussianBlur,
        (Win32.VK_F6, false) => FilterCommand.MotionBlur,
        (Win32.VK_F7, false) => FilterCommand.AddNoise,
        (Win32.VK_F8, false) => FilterCommand.LensCorrection,
        _ => null,
    };

    private static bool IsAdjustment(FilterCommand command) => command <= FilterCommand.Grain;

    /// <summary>What menus and the history call it.</summary>
    internal static TextKey Title(FilterCommand command) => command switch
    {
        FilterCommand.Levels => TextKey.AdjustLevels,
        FilterCommand.Curves => TextKey.AdjustCurves,
        FilterCommand.HueSaturation => TextKey.AdjustHueSaturation,
        FilterCommand.Exposure => TextKey.AdjustExposure,
        FilterCommand.GradientMap => TextKey.AdjustGradientMap,
        FilterCommand.Grain => TextKey.AdjustGrain,
        FilterCommand.GaussianBlur => TextKey.FilterGaussianBlur,
        FilterCommand.MotionBlur => TextKey.FilterMotionBlur,
        FilterCommand.AddNoise => TextKey.FilterAddNoise,
        _ => TextKey.FilterLensCorrection,
    };

    /// <summary>Handles a key for the filters. Returns true when it was theirs.</summary>
    private bool FilterKey(int key, bool control)
    {
        if (IsFiltering)
        {
            switch (key)
            {
                case Win32.VK_RETURN:
                    Finish(keep: true);
                    return true;

                case Win32.VK_ESCAPE:
                    Finish(keep: false);
                    return true;

                // Zooming to look closer is fine; anything that edits the document is not.
                case Win32.VK_0 or Win32.VK_1 when control:
                    return false;

                default:
                    return true;
            }
        }

        if (key == Win32.VK_ESCAPE) return false;
        if (CommandFor(key, control) is not FilterCommand command) return false;

        Start(command, asLayer: Win32.IsKeyDown(Win32.VK_SHIFT) && IsAdjustment(command));
        return true;
    }

    private void Start(FilterCommand command, bool asLayer)
    {
        if (_document is null) return;

        _command = command;
        _amount = StartingAmount;
        _seed = (uint)Random.Shared.Next();

        ImageLayer? chosen = Primary is Guid id ? _document.Layer(id) : null;

        if (asLayer)
        {
            // Above the chosen layer and in its folder, or at the top when nothing is chosen.
            int index = chosen is null ? _document.Layers.Count : _document.IndexOf(chosen.Id) + 1;
            var layer = new ImageLayer
            {
                Id = Guid.NewGuid(),
                // A layer's name is the user's from here on, so it is fixed in today's language.
                Name = Localizer.Text(Title(command)),
                Transform = new LayerTransform(Point.Zero, new Size(_document.Width, _document.Height)),
                ParentId = chosen?.ParentId,
                Adjustment = AdjustmentFor(command, _amount),
            };

            _beforeAdjusting = _document;
            _history.Begin(HistoryName.Of(TextKey.HistoryAdjustmentLayer, Title(command)), _document, layer.Id);

            var layers = _document.Layers.ToList();
            layers.Insert(Math.Clamp(index, 0, layers.Count), layer);
            _document = _document with { Layers = layers.ToEquatableList() };
            _adjusting = layer.Id;
            return;
        }

        // Pixels to run over: a layer with an image, not a folder and not an adjustment.
        if (chosen is not { Image: not null, IsGroup: false, Adjustment: null }) return;

        _preview = new FilterPreview(chosen, KindFor(command), SettingsFor(command, _amount), _selection);
    }

    /// <summary>Closes the open command, keeping what it did or putting everything back.</summary>
    private void Finish(bool keep)
    {
        if (_preview is FilterPreview preview)
        {
            _preview = null;

            if (keep && _document is not null)
            {
                _history.Begin(Title(_command), _document, preview.Layer.Id);
                _document = _document.Replacing(preview.Commit());
                _history.End(_document, preview.Layer.Id);
            }

            preview.Dispose();
        }

        if (_adjusting is Guid added)
        {
            _adjusting = null;

            if (keep)
            {
                _chosen.Clear();
                _chosen.Add(added);
            }
            else
            {
                _document = _beforeAdjusting;
            }

            _beforeAdjusting = null;
            _history.End(_document, keep ? added : Primary);
        }

        _amountAtDrag = null;
    }

    private bool BeginAmountDrag(Point view)
    {
        if (!IsFiltering) return false;
        _amountAtDrag = _amount;
        _dragStart = view;
        return true;
    }

    private bool DragAmount(Point view)
    {
        if (_amountAtDrag is not double from) return false;

        _amount = Math.Clamp(from + (view.X - _dragStart.X) / DragRange, -1, 1);

        if (_preview is not null)
        {
            _preview.Settings = SettingsFor(_command, _amount);
        }
        else if (_adjusting is Guid id && _document?.Layer(id) is ImageLayer layer)
        {
            _document = _document.Replacing(layer with { Adjustment = AdjustmentFor(_command, _amount) });
        }

        NeedsRedraw = true;
        return true;
    }

    private bool EndAmountDrag()
    {
        if (_amountAtDrag is null) return false;
        _amountAtDrag = null;
        return true;
    }

    /// <summary>The frame's stand-in for a layer being filtered, when one is.</summary>
    private LiveEdit? Previewing(int width, int height) =>
        _preview is FilterPreview preview && _document is not null
            ? preview.Frame(_viewport.DeviceProjection(_document.Size), width, height)
            : null;

    private static FilterKind KindFor(FilterCommand command) => command switch
    {
        FilterCommand.GaussianBlur => FilterKind.GaussianBlur,
        FilterCommand.MotionBlur => FilterKind.MotionBlur,
        FilterCommand.AddNoise => FilterKind.AddNoise,
        FilterCommand.LensCorrection => FilterKind.LensCorrection,
        _ => FilterKind.Adjustment,
    };

    private FilterSettings SettingsFor(FilterCommand command, double t)
    {
        double strength = Math.Abs(t);
        return command switch
        {
            FilterCommand.GaussianBlur => new FilterSettings { Radius = Math.Max(0.1, 50 * strength) },
            FilterCommand.MotionBlur => new FilterSettings { Distance = Math.Max(1, 200 * strength), Angle = 0 },
            FilterCommand.AddNoise => new FilterSettings { Amount = Math.Max(0.1, 100 * strength), Seed = _seed },
            FilterCommand.LensCorrection => new FilterSettings { Distortion = 100 * t },
            _ => new FilterSettings { Adjustment = AdjustmentFor(command, t), Seed = _seed },
        };
    }

    /// <summary>One adjustment with its main setting at <paramref name="t"/>, −1 to 1.</summary>
    private LayerAdjustment AdjustmentFor(FilterCommand command, double t)
    {
        var diagonal = new EquatableList<CurvePoint>([new CurvePoint(0, 0), new CurvePoint(255, 255)]);

        return command switch
        {
            FilterCommand.Levels => new LayerAdjustment(AdjustmentKind.Levels)
            {
                Levels = new LevelsSettings
                {
                    Ranges = new EquatableList<LevelRange>(
                        [new LevelRange { Gamma = Math.Pow(2, 1.5 * t) }, new(), new(), new()]),
                },
            },

            FilterCommand.Curves => new LayerAdjustment(AdjustmentKind.Curves)
            {
                Curves = new CurvesSettings
                {
                    Channels = new EquatableList<EquatableList<CurvePoint>>(
                    [
                        new([new CurvePoint(0, 0), new CurvePoint(128, Math.Clamp(128 + 100 * t, 0, 255)),
                             new CurvePoint(255, 255)]),
                        diagonal, diagonal, diagonal,
                    ]),
                },
            },

            FilterCommand.HueSaturation => new LayerAdjustment(AdjustmentKind.Hsv)
            {
                HsvSettings = HueSaturationSettings.From(180 * t, 0, 0, colorize: false),
            },

            FilterCommand.Exposure => new LayerAdjustment(AdjustmentKind.Exposure)
            {
                ExposureSettings = new ExposureSettings { Exposure = 3 * t },
            },

            FilterCommand.GradientMap => new LayerAdjustment(AdjustmentKind.GradientMap)
            {
                GradientMapSettings = new GradientMapSettings
                {
                    Shadows = new AdjustmentColor(0.05, 0.05, 0.3),
                    Highlights = new AdjustmentColor(1, 0.85, 0.5),
                    Reversed = t < 0,
                },
            },

            _ => new LayerAdjustment(AdjustmentKind.Grain)
            {
                GrainSettings = new GrainSettings { Amount = 100 * Math.Abs(t), Seed = _seed },
            },
        };
    }
}
