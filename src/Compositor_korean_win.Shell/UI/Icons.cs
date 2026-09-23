using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Rect = Compositor_korean_win.Core.Rect;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The tool rail's and the panels' icons, drawn from lines and shapes.
/// </summary>
/// <remarks>
/// Drawn rather than taken from a font. Windows' icon fonts have no clone stamp or magic wand, and
/// a picture drawn here is the same on every Windows version and costs nothing in the binary. Each
/// icon is laid out on a 20-point grid centred in the area it is given.
/// </remarks>
internal static class Icons
{
    public static void Tool(Ui ui, CanvasTool tool, Rect area, Color4 colour)
    {
        switch (tool)
        {
            case CanvasTool.Move: Move(ui, area, colour); break;
            case CanvasTool.RectangleMarquee: DashedRect(ui, area, colour); break;
            case CanvasTool.EllipseMarquee: DashedEllipse(ui, area, colour); break;
            case CanvasTool.Lasso: Lasso(ui, area, colour); break;
            case CanvasTool.PolygonLasso: Polygon(ui, area, colour); break;
            case CanvasTool.Brush: Brush(ui, area, colour); break;
            case CanvasTool.Eraser: Eraser(ui, area, colour); break;
            case CanvasTool.CloneStamp: Stamp(ui, area, colour); break;
            case CanvasTool.Blur: Drop(ui, area, colour); break;
            case CanvasTool.Heal: Bandage(ui, area, colour); break;
            case CanvasTool.MagicWand: Wand(ui, area, colour); break;
            case CanvasTool.Gradient: Gradient(ui, area, colour); break;
            case CanvasTool.Shape: Shape(ui, area, colour); break;
            case CanvasTool.Crop:
                // Photoshop's two crossing corners.
                Stroke(ui, area, colour, (5, 1), (5, 15), (19, 15));
                Stroke(ui, area, colour, (1, 5), (15, 5), (15, 19));
                break;
        }
    }

    /// <summary>A point on the icon grid: (0, 0) top left, (20, 20) bottom right.</summary>
    private static Vector2 At(Ui ui, Rect area, double x, double y)
    {
        double size = ui.P(20);
        double left = area.X + (area.Width - size) / 2, top = area.Y + (area.Height - size) / 2;
        return new Vector2((float)(left + ui.P(x)), (float)(top + ui.P(y)));
    }

    private static void Stroke(Ui ui, Rect area, Color4 colour, params (double X, double Y)[] points)
    {
        ID2D1SolidColorBrush brush = ui.Brush(colour);
        for (int i = 0; i + 1 < points.Length; i++)
            ui.Context.DrawLine(At(ui, area, points[i].X, points[i].Y), At(ui, area, points[i + 1].X, points[i + 1].Y),
                                brush, ui.P(1.5));
    }

    private static void Move(Ui ui, Rect area, Color4 colour)
    {
        // An arrow pointer.
        Stroke(ui, area, colour, (5, 2), (5, 16), (8.5, 12.5), (11, 18), (13, 17), (10.5, 11.5), (15, 11.5), (5, 2));
    }

    private static void DashedRect(Ui ui, Rect area, Color4 colour)
    {
        for (int i = 0; i < 4; i++)
        {
            double from = 3 + i * 4;
            Stroke(ui, area, colour, (from, 4), (from + 2, 4));
            Stroke(ui, area, colour, (from, 16), (from + 2, 16));
            Stroke(ui, area, colour, (3, from + 1), (3, from + 3));
            Stroke(ui, area, colour, (17, from + 1), (17, from + 3));
        }
    }

    private static void DashedEllipse(Ui ui, Rect area, Color4 colour)
    {
        for (int i = 0; i < 12; i += 2)
        {
            double a = i * Math.PI / 6, b = (i + 1) * Math.PI / 6;
            Stroke(ui, area, colour, (10 + 7 * Math.Cos(a), 10 + 6 * Math.Sin(a)), (10 + 7 * Math.Cos(b), 10 + 6 * Math.Sin(b)));
        }
    }

    private static void Lasso(Ui ui, Rect area, Color4 colour)
    {
        var loop = new List<(double, double)>();
        for (int i = 0; i <= 16; i++)
        {
            double a = i * Math.PI / 8;
            loop.Add((10 + 7 * Math.Cos(a), 8 + 4.5 * Math.Sin(a)));
        }
        Stroke(ui, area, colour, [.. loop]);
        Stroke(ui, area, colour, (5, 11), (6, 15), (4, 18));
    }

    private static void Polygon(Ui ui, Rect area, Color4 colour) =>
        Stroke(ui, area, colour, (4, 6), (12, 3), (17, 9), (13, 16), (5, 14), (4, 6));

    private static void Brush(Ui ui, Rect area, Color4 colour)
    {
        Stroke(ui, area, colour, (17, 3), (9, 11));
        Stroke(ui, area, colour, (9, 11), (6, 12), (4, 16), (3, 18), (6, 17), (9, 15), (10, 12), (9, 11));
    }

    private static void Eraser(Ui ui, Rect area, Color4 colour)
    {
        Stroke(ui, area, colour, (3, 13), (11, 5), (17, 11), (11, 17), (7, 17), (3, 13));
        Stroke(ui, area, colour, (7, 9), (13, 15));
        Stroke(ui, area, colour, (8, 18), (17, 18));
    }

    private static void Stamp(Ui ui, Rect area, Color4 colour)
    {
        Stroke(ui, area, colour, (8, 3), (12, 3), (12, 9), (8, 9), (8, 3));
        Stroke(ui, area, colour, (4, 12), (16, 12), (16, 14), (4, 14), (4, 12));
        Stroke(ui, area, colour, (9, 9), (9, 12));
        Stroke(ui, area, colour, (11, 9), (11, 12));
        Stroke(ui, area, colour, (4, 17), (16, 17));
    }

    private static void Drop(Ui ui, Rect area, Color4 colour)
    {
        var drop = new List<(double, double)> { (10, 3) };
        for (int i = 0; i <= 12; i++)
        {
            double a = Math.PI * (-0.15 + 1.3 * i / 12);
            drop.Add((10 + 5 * Math.Cos(a), 12.5 + 5 * Math.Sin(a)));
        }
        drop.Add((10, 3));
        Stroke(ui, area, colour, [.. drop]);
    }

    private static void Bandage(Ui ui, Rect area, Color4 colour)
    {
        Stroke(ui, area, colour, (2.5, 13), (13, 2.5), (17.5, 7), (7, 17.5), (2.5, 13));
        Stroke(ui, area, colour, (8, 8), (12, 12));
        Stroke(ui, area, colour, (12, 8), (8, 12));
    }

    private static void Wand(Ui ui, Rect area, Color4 colour)
    {
        Stroke(ui, area, colour, (3, 17), (13, 7));
        Stroke(ui, area, colour, (15, 2), (15, 6));
        Stroke(ui, area, colour, (13, 4), (17, 4));
        Stroke(ui, area, colour, (17.5, 9), (17.5, 11));
        Stroke(ui, area, colour, (9, 2.5), (9, 4.5));
    }

    private static void Gradient(Ui ui, Rect area, Color4 colour)
    {
        Stroke(ui, area, colour, (3, 4), (17, 4), (17, 16), (3, 16), (3, 4));
        for (int i = 0; i < 5; i++)
        {
            double x = 5 + i * 2.6;
            var faded = new Color4(colour.R, colour.G, colour.B, colour.A * (1 - i / 5f));
            Stroke(ui, area, faded, (x, 6), (x, 14));
        }
    }

    private static void Shape(Ui ui, Rect area, Color4 colour)
    {
        ui.Context.FillRectangle(new Vortice.RawRectF(At(ui, area, 3, 6).X, At(ui, area, 3, 6).Y,
                                                      At(ui, area, 12, 15).X, At(ui, area, 12, 15).Y), ui.Brush(colour));
        ui.Context.DrawEllipse(new Ellipse(At(ui, area, 13, 8), ui.P(4.5), ui.P(4.5)), ui.Brush(colour), ui.P(1.5));
    }

    // MARK: Panel buttons

    public static void Eye(Ui ui, Rect area, Color4 colour, bool open)
    {
        ID2D1SolidColorBrush brush = ui.Brush(colour);
        ui.Context.DrawEllipse(new Ellipse(At(ui, area, 10, 10), ui.P(7), ui.P(4)), brush, ui.P(1.3));
        if (open) ui.Context.FillEllipse(new Ellipse(At(ui, area, 10, 10), ui.P(2.2), ui.P(2.2)), brush);
        else Stroke(ui, area, colour, (3, 16), (17, 4));
    }

    public static void Plus(Ui ui, Rect area, Color4 colour)
    {
        Stroke(ui, area, colour, (4, 4), (13, 4), (13, 16), (4, 16), (4, 4));
        Stroke(ui, area, colour, (15, 5), (15, 11));
        Stroke(ui, area, colour, (12, 8), (18, 8));
    }

    public static void Folder(Ui ui, Rect area, Color4 colour) =>
        Stroke(ui, area, colour, (3, 6), (8, 6), (9.5, 8), (17, 8), (17, 16), (3, 16), (3, 6));

    public static void Mask(Ui ui, Rect area, Color4 colour)
    {
        Stroke(ui, area, colour, (3, 4), (17, 4), (17, 16), (3, 16), (3, 4));
        ui.Context.FillEllipse(new Ellipse(At(ui, area, 10, 10), ui.P(3.8), ui.P(3.8)), ui.Brush(colour));
    }

    public static void Adjustment(Ui ui, Rect area, Color4 colour)
    {
        ui.Context.DrawEllipse(new Ellipse(At(ui, area, 10, 10), ui.P(6.5), ui.P(6.5)), ui.Brush(colour), ui.P(1.5));
        ui.Context.FillRectangle(new Vortice.RawRectF(At(ui, area, 10, 3.5).X, At(ui, area, 10, 3.5).Y,
                                                      At(ui, area, 16.5, 16.5).X, At(ui, area, 16.5, 16.5).Y), ui.Brush(colour));
    }

    public static void Bin(Ui ui, Rect area, Color4 colour)
    {
        Stroke(ui, area, colour, (4, 5), (16, 5));
        Stroke(ui, area, colour, (8, 5), (8, 3), (12, 3), (12, 5));
        Stroke(ui, area, colour, (5.5, 5), (6.5, 17), (13.5, 17), (14.5, 5));
    }

    public static void Chain(Ui ui, Rect area, Color4 colour) =>
        Stroke(ui, area, colour, (7, 4), (7, 16), (13, 16));

    public static void Disclosure(Ui ui, Rect area, Color4 colour, bool open)
    {
        if (open) Stroke(ui, area, colour, (6, 8), (10, 12), (14, 8));
        else Stroke(ui, area, colour, (8, 6), (12, 10), (8, 14));
    }

    public static void Swap(Ui ui, Rect area, Color4 colour)
    {
        Stroke(ui, area, colour, (5, 15), (5, 5), (15, 5));
        Stroke(ui, area, colour, (3, 7), (5, 5), (7, 7));
        Stroke(ui, area, colour, (13, 3), (15, 5), (13, 7));
    }
}
