using Compositor_korean_win.Core;
using Point = Compositor_korean_win.Core.Point;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The Eyedropper, Hand and Zoom tools — upstream's three tools that change no pixel.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Eyedropper (I): press and drag to take the colour the document shows into the foreground;
/// Alt takes it into the background. Alt with the Brush, Spot Healing or Gradient does the same
/// without changing tool, as upstream's does.</item>
/// <item>Hand (H): a drag moves the view — what Space or the wheel button already did.</item>
/// <item>Zoom (Z): a click doubles the zoom at the point, Alt halves it; a drag sideways zooms
/// continuously, doubling every hundred points, anchored where it began.</item>
/// </list>
/// </remarks>
internal sealed partial class CanvasView
{
    private bool _sampling;
    private bool _samplingBackground;
    private Point? _zoomFrom;
    private double _zoomAtStart;
    private bool _zoomMoved;
    private bool _zoomOut;

    /// <summary>Whether a press with this tool and Alt takes a colour instead of doing the tool's own thing.</summary>
    private bool PicksWithAlt => _tool is CanvasTool.Brush or CanvasTool.Heal or CanvasTool.Gradient;

    /// <summary>Starts a press with one of the three; true when it was theirs.</summary>
    private bool BeginNavigation(Point view, Point pixel, bool alt)
    {
        switch (_tool)
        {
            case CanvasTool.Hand:
                _panning = true;
                _panFrom = view;
                return true;

            case CanvasTool.Zoom:
                _zoomFrom = view;
                _zoomAtStart = _viewport.Zoom;
                _zoomMoved = false;
                _zoomOut = alt;
                return true;

            case CanvasTool.Eyedropper:
            case var _ when alt && PicksWithAlt:
                _sampling = true;
                _samplingBackground = alt && _tool == CanvasTool.Eyedropper;
                Sample(pixel);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Carries a press with one of the three on; true when it was theirs.</summary>
    private bool DragNavigation(Point view, Point pixel)
    {
        if (_sampling)
        {
            Sample(pixel);
            return true;
        }

        if (_zoomFrom is Point from && _document is not null)
        {
            double dx = view.X - from.X;
            if (Math.Abs(dx) >= 3) _zoomMoved = true;
            if (_zoomMoved) _viewport = _viewport.ZoomedTo(_zoomAtStart * Math.Pow(2, dx / 100), from, _document.Size);
            NeedsRedraw = true;
            return true;
        }

        return false;
    }

    /// <summary>Ends a press with one of the three; true when it was theirs.</summary>
    private bool EndNavigation()
    {
        if (_sampling)
        {
            _sampling = false;
            return true;
        }

        if (_zoomFrom is Point from)
        {
            _zoomFrom = null;
            if (!_zoomMoved && _document is not null)
                _viewport = _viewport.ZoomedTo(_viewport.Zoom * (_zoomOut ? 0.5 : 2), from, _document.Size);
            NeedsRedraw = true;
            return true;
        }

        return false;
    }

    /// <summary>The colour the document shows at a point, into the foreground or background.</summary>
    private void Sample(Point pixel)
    {
        // A mask's colours are its black and white, not something to pick.
        if (EditingMask || CompositeColour(pixel) is not (double red, double green, double blue)) return;

        var colour = new Rgba((byte)Math.Round(red * 255), (byte)Math.Round(green * 255), (byte)Math.Round(blue * 255));
        if (_samplingBackground) BackgroundColor = colour;
        else ForegroundColor = colour;
        NeedsRedraw = true;
    }
}
