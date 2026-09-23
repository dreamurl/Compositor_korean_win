using Compositor_korean_win.Core;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Point = Compositor_korean_win.Core.Point;
using Rect = Compositor_korean_win.Core.Rect;

namespace Compositor_korean_win.Shell;

/// <summary>
/// View › Pixel Grid and View › Show Transform Controls — upstream's two View toggles.
/// </summary>
/// <remarks>
/// Both are the window's, not the document's, and last the session, as upstream's do.
/// </remarks>
internal sealed partial class CanvasView
{
    /// <summary>Upstream's threshold: the grid shows from 800 % in.</summary>
    public const double PixelGridZoom = 8;

    /// <summary>Lines between the document's pixels once they are large enough to tell apart.</summary>
    public bool ShowPixelGrid { get; set; } = true;

    /// <summary>
    /// The Move tool's box and handles. Off, the layer is still dragged about, but nothing is drawn
    /// round it and no handle takes the press — for looking at the edges of what is being placed.
    /// </summary>
    public bool ShowTransformControls { get; set; } = true;

    public void TogglePixelGrid()
    {
        ShowPixelGrid = !ShowPixelGrid;
        NeedsRedraw = true;
    }

    public void ToggleTransformControls()
    {
        ShowTransformControls = !ShowTransformControls;
        NeedsRedraw = true;
    }

    /// <summary>
    /// A line along every pixel edge of the document in view, one device pixel wide, faint so it
    /// reads over light and dark alike.
    /// </summary>
    private void DrawPixelGrid(ID2D1DeviceContext context, CanvasProjection projection)
    {
        if (!ShowPixelGrid || _viewport.Zoom < PixelGridZoom || _document is null) return;

        Rect sheet = projection.Apply(new Rect(0, 0, _document.Width, _document.Height)).Intersect(_area);
        if (sheet.IsEmpty) return;

        Point first = projection.Invert(sheet.Origin), last = projection.Invert(new Point(sheet.MaxX, sheet.MaxY));
        using ID2D1SolidColorBrush line = context.CreateSolidColorBrush(new Color4(0.5f, 0.5f, 0.5f, 0.35f));

        for (int x = (int)Math.Ceiling(first.X); x <= (int)Math.Floor(last.X); x++)
        {
            // On a whole device pixel, so the line is crisp rather than smeared over two.
            float at = (float)Math.Round(projection.Apply(new Point(x, 0)).X) + 0.5f;
            context.DrawLine(new System.Numerics.Vector2(at, (float)sheet.MinY), new System.Numerics.Vector2(at, (float)sheet.MaxY), line, 1);
        }

        for (int y = (int)Math.Ceiling(first.Y); y <= (int)Math.Floor(last.Y); y++)
        {
            float at = (float)Math.Round(projection.Apply(new Point(0, y)).Y) + 0.5f;
            context.DrawLine(new System.Numerics.Vector2((float)sheet.MinX, at), new System.Numerics.Vector2((float)sheet.MaxX, at), line, 1);
        }
    }
}
