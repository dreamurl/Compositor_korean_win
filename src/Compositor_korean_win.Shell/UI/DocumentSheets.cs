using Compositor_korean_win.Core;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Point = Compositor_korean_win.Core.Point;
using Rect = Compositor_korean_win.Core.Rect;

namespace Compositor_korean_win.Shell;

/// <summary>Sizes as the size sheets let them be typed: in pixels, or as a percentage of what they were.</summary>
internal enum SizeUnit
{
    Pixels,
    Percent,
}

/// <summary>
/// A width and a height being typed, in pixels or percent, absolute or relative to the size they
/// start from, with the aspect ratio locked or not — the part upstream's Canvas Size and Image Size
/// sheets share (<c>CanvasSizeDraft</c>).
/// </summary>
internal sealed class SizeDraft(int width, int height)
{
    public int OriginalWidth { get; } = width;
    public int OriginalHeight { get; } = height;
    public double Width { get; private set; } = width;
    public double Height { get; private set; } = height;
    public SizeUnit Unit { get; set; } = SizeUnit.Pixels;
    public bool Relative { get; set; }
    public bool Locked { get; set; }

    public int PixelWidth => (int)Math.Round(Width);
    public int PixelHeight => (int)Math.Round(Height);
    public bool Valid => DocumentCommands.IsValidSize(PixelWidth, PixelHeight);

    /// <summary>What the box for one side shows in the current unit.</summary>
    public double Shown(bool widthAxis)
    {
        double target = widthAxis ? Width : Height, original = widthAxis ? OriginalWidth : OriginalHeight;
        return (Unit, Relative) switch
        {
            (SizeUnit.Pixels, false) => target,
            (SizeUnit.Pixels, true) => target - original,
            (SizeUnit.Percent, false) => target / original * 100,
            _ => target / original * 100 - 100,
        };
    }

    /// <summary>Takes a typed number for one side; with the ratio locked, the other side follows.</summary>
    public void Set(double value, bool widthAxis)
    {
        double original = widthAxis ? OriginalWidth : OriginalHeight;
        double target = (Unit, Relative) switch
        {
            (SizeUnit.Pixels, false) => value,
            (SizeUnit.Pixels, true) => original + value,
            (SizeUnit.Percent, false) => original * value / 100,
            _ => original * (100 + value) / 100,
        };
        if (!double.IsFinite(target)) return;

        if (widthAxis)
        {
            Width = target;
            if (Locked) Height = target * OriginalHeight / OriginalWidth;
        }
        else
        {
            Height = target;
            if (Locked) Width = target * OriginalWidth / OriginalHeight;
        }
    }

    /// <summary>The unit choice, the two boxes, and the result or what is wrong with it.</summary>
    public void Rows(SheetLayout layout, bool relativeOption)
    {
        layout.Choice(null,
        [
            (Localizer.Text(TextKey.UnitPixelsName), Unit == SizeUnit.Pixels, () => Unit = SizeUnit.Pixels),
            (Localizer.Text(TextKey.UnitPercentName), Unit == SizeUnit.Percent, () => Unit = SizeUnit.Percent),
        ]);

        string unit = Unit == SizeUnit.Pixels ? Localizer.Text(TextKey.UnitPx) : "%";
        layout.Number(Localizer.Text(TextKey.SheetWidth), Shown(widthAxis: true), Unit == SizeUnit.Pixels ? 0 : 2,
                      value => Set(value, widthAxis: true), unit);
        layout.Number(Localizer.Text(TextKey.SheetHeight), Shown(widthAxis: false), Unit == SizeUnit.Pixels ? 0 : 2,
                      value => Set(value, widthAxis: false), unit);

        if (relativeOption) layout.Check(Localizer.Text(TextKey.LabelRelative), Relative, () => Relative = !Relative);
        layout.Check(Localizer.Text(TextKey.LabelLockAspect), Locked, () =>
        {
            Locked = !Locked;
            if (Locked) Set(Shown(widthAxis: true), widthAxis: true);
        });

        layout.Note(Valid
            ? Localizer.Format(TextKey.NoteNewSize, PixelWidth, PixelHeight)
            : Localizer.Text(TextKey.NoteSizeLimits), warning: !Valid);
    }
}

/// <summary>File › New: a blank, transparent canvas of a typed size — upstream's <c>NewCanvasSheet</c>.</summary>
internal sealed class NewCanvasSheet(Action<int, int> create) : Sheet
{
    private double _width = 1920, _height = 1080;

    private bool Valid => DocumentCommands.IsValidSize((int)Math.Round(_width), (int)Math.Round(_height));

    public override string Title => Localizer.Text(TextKey.SheetNewCanvas);

    public override double Width => 360;

    public override TextKey AcceptLabel => TextKey.DialogCreate;

    public override bool CanAccept => Valid;

    public override void Accept() => create((int)Math.Round(_width), (int)Math.Round(_height));

    public override void Cancel() { }

    public override void Content(SheetLayout layout)
    {
        string px = Localizer.Text(TextKey.UnitPx);
        layout.Number(Localizer.Text(TextKey.SheetWidth), _width, 0, value => _width = value, px);
        layout.Number(Localizer.Text(TextKey.SheetHeight), _height, 0, value => _height = value, px);
        layout.Note(Localizer.Text(Valid ? TextKey.NoteNewCanvas : TextKey.NoteSizeLimits), warning: !Valid);
    }
}

/// <summary>What fills the space a larger canvas adds.</summary>
internal enum CanvasExtension
{
    Transparent,
    Foreground,
    Background,
    White,
    Black,
}

/// <summary>Image › Canvas Size — upstream's <c>CanvasSizeSheet</c>: the new size, where the old content sits, and what fills the rest.</summary>
internal sealed class CanvasSizeSheet(CanvasView canvas, Chrome host) : Sheet
{
    private readonly SizeDraft _draft = new(canvas.Document?.Width ?? 1, canvas.Document?.Height ?? 1);
    private int _anchor = 4;
    private CanvasExtension _extension = CanvasExtension.Transparent;

    public override string Title => Localizer.Text(TextKey.SheetCanvasSize);

    public override double Width => 380;

    public override bool CanAccept => _draft.Valid;

    public override void Accept()
    {
        Rgba? fill = _extension switch
        {
            CanvasExtension.Foreground => canvas.ForegroundColor,
            CanvasExtension.Background => canvas.BackgroundColor,
            CanvasExtension.White => new Rgba(255, 255, 255),
            CanvasExtension.Black => new Rgba(0, 0, 0),
            _ => null,
        };
        canvas.ResizeCanvas(_draft.PixelWidth, _draft.PixelHeight, _anchor, fill);
    }

    public override void Cancel() { }

    public override void Content(SheetLayout layout)
    {
        layout.Note(Localizer.Format(TextKey.NoteCurrentSize, _draft.OriginalWidth, _draft.OriginalHeight));
        _draft.Rows(layout, relativeOption: true);

        Ui ui = layout.Ui;
        string anchorLabel = Localizer.Text(TextKey.LabelAnchor);
        layout.Custom(3 * 24 + 8, area =>
        {
            ui.Text(anchorLabel, new Rect(area.X, area.Y, ui.P(90), ui.P(24)), Ui.Dim);
            double cell = ui.P(24), gap = ui.P(3);
            double left = area.X + layout.LabelWidth + ui.P(6);
            for (int index = 0; index < 9; index++)
            {
                var box = new Rect(left + index % 3 * (cell + gap), area.Y + index / 3 * (cell + gap), cell, cell);
                int chosen = index;
                ui.Fill(box, index == _anchor ? Ui.Accent : Ui.Raised);
                ui.Button(box, () => _anchor = chosen, null, active: index == _anchor);
            }
        });

        layout.Dropdown(Localizer.Text(TextKey.LabelCanvasExtension), ExtensionName(_extension), () =>
        {
            CanvasExtension[] all = Enum.GetValues<CanvasExtension>();
            int chosen = host.Popup([.. all.Select(each => (ExtensionName(each), each == _extension))]);
            if (chosen >= 0) _extension = all[chosen];
        }, points: 150);
    }

    private static string ExtensionName(CanvasExtension extension) => Localizer.Text(extension switch
    {
        CanvasExtension.Foreground => TextKey.ExtensionForeground,
        CanvasExtension.Background => TextKey.ExtensionBackground,
        CanvasExtension.White => TextKey.ExtensionWhite,
        CanvasExtension.Black => TextKey.ExtensionBlack,
        _ => TextKey.ExtensionTransparent,
    });
}

/// <summary>
/// Image › Image Size — upstream's <c>ImageSizeSheet</c>: the new size in pixels or percent, and the
/// resolution; with Resample off only the resolution changes.
/// </summary>
internal sealed class ImageSizeSheet(CanvasView canvas) : Sheet
{
    private readonly SizeDraft _draft = new(canvas.Document?.Width ?? 1, canvas.Document?.Height ?? 1) { Locked = true };
    private double _resolution = canvas.Document?.Resolution ?? 72;
    private bool _resample = true;

    public override string Title => Localizer.Text(TextKey.SheetImageSize);

    public override double Width => 380;

    public override bool CanAccept => !_resample || _draft.Valid;

    public override void Accept()
    {
        if (_resample) canvas.ResizeImage(_draft.PixelWidth, _draft.PixelHeight, _resolution);
        else canvas.SetResolution(_resolution);
    }

    public override void Cancel() { }

    public override void Content(SheetLayout layout)
    {
        layout.Note(Localizer.Format(TextKey.NoteCurrentSize, _draft.OriginalWidth, _draft.OriginalHeight));
        if (_resample) _draft.Rows(layout, relativeOption: false);

        layout.Number(Localizer.Text(TextKey.LabelResolution), _resolution, 2,
                      value => _resolution = Math.Clamp(value, 1, 9600), Localizer.Text(TextKey.UnitPixelsPerInch));
        layout.Check(Localizer.Text(TextKey.LabelResample), _resample, () => _resample = !_resample);
        layout.Note(Localizer.Text(_resample ? TextKey.NoteResample : TextKey.NoteNoResample));
    }
}

/// <summary>
/// File › Export as JPEG — upstream's <c>JPEGExportSheet</c>: the picture as the encoder will leave
/// it, its quality, the colour transparency becomes, and the size of the file.
/// </summary>
/// <remarks>
/// The preview is the encoded file decoded again, so what it shows is what the quality costs. It is
/// encoded once the slider is let go, not on every step of a drag: a large composite takes a
/// noticeable moment to encode.
/// </remarks>
internal sealed class JpegSheet(PixelBuffer composite, Func<byte[], bool> save, Chrome host) : Sheet, IDisposable
{
    /// <summary>The last export's quality, which the next one starts from, as upstream remembers it.</summary>
    private static double s_quality = ImageWriter.DefaultQuality;

    private double _quality = s_quality;
    private (double Red, double Green, double Blue) _matte = (1, 1, 1);
    private byte[]? _encoded;
    private (double Quality, (double, double, double) Matte)? _encodedFor;
    private PixelBuffer? _decoded;
    private ID2D1Bitmap1? _bitmap;

    public override string Title => Localizer.Text(TextKey.SheetExportJpeg);

    public override double Width => 520;

    public override TextKey AcceptLabel => TextKey.DialogExport;

    public override void Accept()
    {
        Encode();
        s_quality = _quality;
        if (_encoded is not null) save(_encoded);
        Dispose();
    }

    public override void Cancel() => Dispose();

    public override void Content(SheetLayout layout)
    {
        Ui ui = layout.Ui;
        if (!layout.Measuring && !ui.Dragging) Encode();

        layout.Custom(260, area =>
        {
            ui.Fill(area, new Color4(0.09f, 0.09f, 0.095f, 1f));
            if (_decoded is not null)
            {
                _bitmap ??= ImageLoader.Upload(ui.Context, _decoded, Vortice.DXGI.Format.R8G8B8A8_UNorm);
                double scale = Math.Min(area.Width / _decoded.Width, area.Height / _decoded.Height);
                double w = _decoded.Width * scale, h = _decoded.Height * scale;
                var fitted = new Rect(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
                ui.Context.DrawBitmap(_bitmap, Ui.Raw(fitted), 1f, BitmapInterpolationMode.Linear, null);
            }
            ui.Frame(area, Ui.Line);
        });

        layout.Slider(Localizer.Text(TextKey.LabelQuality), Math.Round(_quality * 100), 0, 100, 0,
                      value => _quality = value / 100, "%");

        layout.Custom(28, area =>
        {
            var box = new Rect(area.X, area.Y + (area.Height - ui.P(22)) / 2, ui.P(22), ui.P(22));
            ui.Fill(box, new Color4((float)_matte.Red, (float)_matte.Green, (float)_matte.Blue, 1));
            ui.Frame(box, Ui.Ink);
            ui.Text(Localizer.Text(TextKey.LabelMatte), new Rect(box.MaxX + ui.P(8), area.Y, area.Width - box.Width, area.Height), Ui.Ink);
            ui.Area(new Rect(area.X, area.Y, area.Width / 2, area.Height), () =>
                host.Open(new ColourSheet(TextKey.LabelMatte, _matte, colour => _matte = colour, null)));
        });

        string size = _encoded is null ? "" : Localizer.Format(TextKey.NoteJpegSize, composite.Width, composite.Height, Bytes(_encoded.Length));
        layout.Note(size);
    }

    private void Encode()
    {
        if (_encodedFor is var (quality, matte) && quality == _quality && matte == _matte) return;

        _encoded = ImageWriter.EncodeJpeg(composite, _quality, _matte);
        _encodedFor = (_quality, _matte);

        _bitmap?.Dispose();
        _bitmap = null;
        _decoded?.Release();
        using var loader = new ImageLoader();
        _decoded = loader.Load(_encoded, FormatProbe.WicFormatFor(Vortice.DXGI.Format.R8G8B8A8_UNorm));
    }

    /// <summary>A file size the way Explorer writes it.</summary>
    private static string Bytes(long count) => count switch
    {
        < 1024 => $"{count} B",
        < 1024 * 1024 => $"{count / 1024.0:0.#} KB",
        _ => $"{count / 1024.0 / 1024.0:0.##} MB",
    };

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bitmap?.Dispose();
        _bitmap = null;
        _decoded?.Release();
        _decoded = null;
        composite.Release();
    }
}
