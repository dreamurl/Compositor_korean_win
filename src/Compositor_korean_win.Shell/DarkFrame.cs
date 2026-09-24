using Vortice.Mathematics;
using static Compositor_korean_win.Shell.Win32;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The title bar and the menu bar in the panels' dark grey, so the window does not wear a white
/// band above the options bar.
/// </summary>
/// <remarks>
/// <para>
/// The title bar is Windows' own: DWM draws it dark when asked (Windows 10 20H1 and later), and in
/// the panels' exact colour on Windows 11. An older Windows ignores the attributes and keeps its
/// light frame, which is only a look.
/// </para>
/// <para>
/// The menu bar has no such switch — even a window in dark mode keeps a white one. What Notepad++,
/// the Windows Terminal team's samples and most dark Win32 programs do is answer the two messages
/// uxtheme sends before drawing it, WM_UAHDRAWMENU (the bar's background) and WM_UAHDRAWMENUITEM
/// (each title), and paint over the one-pixel light line the frame leaves under it. The messages are
/// undocumented but have been stable since Windows Vista; if a future Windows stopped sending them
/// the bar would simply be drawn light again. The bar stays a native menu for the reasons MenuBar
/// gives — keyboard, accessibility, Korean — and only its colours change. The menus it opens are
/// the system's, as before.
/// </para>
/// </remarks>
internal static unsafe class DarkFrame
{
    private static readonly uint Bar = Colour(Ui.Panel);
    private static readonly uint Hot = Colour(Ui.Hover);
    private static readonly uint Text = Colour(Ui.Ink);
    private static readonly uint Dimmed = Colour(Ui.Dim);

    private static nint s_barBrush, s_hotBrush;
    private static nint s_theme;

    /// <summary>Asks DWM for a dark title bar in the panels' colour.</summary>
    public static void Apply(nint hwnd)
    {
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, &on, sizeof(int)) < 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, &on, sizeof(int));

        uint caption = Bar, text = Text;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, &caption, sizeof(uint));
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, &text, sizeof(uint));
    }

    /// <summary>
    /// Draws what the message asks for, when it is one of the menu bar's. True with the window
    /// procedure's answer in <paramref name="result"/>; false to let the message through.
    /// </summary>
    public static bool Handle(nint hwnd, uint message, nuint wParam, nint lParam, out nint result)
    {
        result = 0;
        switch (message)
        {
            case WM_UAHDRAWMENU:
            {
                var menu = (UAHMENU*)lParam;
                if (BarRect(hwnd) is not RECT bar) return false;
                FillRect(menu->hdc, bar, BarBrush());
                return true;
            }

            case WM_UAHDRAWMENUITEM:
                DrawItem(hwnd, (UAHDRAWMENUITEM*)lParam);
                return true;

            case WM_NCPAINT or WM_NCACTIVATE:
                // The frame's own drawing first, then the light line it leaves under the bar.
                result = DefWindowProcW(hwnd, message, wParam, lParam);
                CoverLine(hwnd);
                return true;

            case WM_THEMECHANGED or WM_DPICHANGED:
                // The menu theme carries the font for a DPI; open it again for the new one.
                if (s_theme != 0) CloseThemeData(s_theme);
                s_theme = 0;
                return false;
        }
        return false;
    }

    private static void DrawItem(nint hwnd, UAHDRAWMENUITEM* item)
    {
        uint state = item->dis.itemState;
        bool hot = (state & (ODS_HOTLIGHT | ODS_SELECTED)) != 0;
        bool greyed = (state & (ODS_GRAYED | ODS_DISABLED | ODS_INACTIVE)) != 0;

        RECT area = item->dis.rcItem;
        FillRect(item->um.hdc, area, hot ? HotBrush() : BarBrush());

        char* title = stackalloc char[256];
        int length = GetMenuStringW(item->um.hmenu, (uint)item->iPosition, title, 256, MF_BYPOSITION);
        if (length <= 0) return;

        if (s_theme == 0) s_theme = OpenThemeData(hwnd, "Menu");
        if (s_theme == 0) return;

        uint flags = DT_CENTER | DT_SINGLELINE | DT_VCENTER;
        // The underlined access keys show only once Alt has been pressed, as on a light bar.
        if ((state & ODS_NOACCEL) != 0) flags |= DT_HIDEPREFIX;

        var options = new DTTOPTS
        {
            dwSize = (uint)sizeof(DTTOPTS),
            dwFlags = DTT_TEXTCOLOR,
            crText = greyed ? Dimmed : Text,
        };
        DrawThemeTextEx(s_theme, item->um.hdc, MENU_BARITEM, MBI_NORMAL, title, length, flags, ref area, in options);
    }

    /// <summary>The bar in window coordinates, which is what the frame's device context uses.</summary>
    private static RECT? BarRect(nint hwnd)
    {
        var info = new MENUBARINFO { cbSize = (uint)sizeof(MENUBARINFO) };
        if (!GetMenuBarInfo(hwnd, OBJID_MENU, 0, ref info) || !GetWindowRect(hwnd, out RECT window)) return null;
        RECT bar = info.rcBar;
        return new RECT
        {
            Left = bar.Left - window.Left, Top = bar.Top - window.Top,
            Right = bar.Right - window.Left, Bottom = bar.Bottom - window.Top,
        };
    }

    /// <summary>The single light row the frame draws between the menu bar and the client area.</summary>
    private static void CoverLine(nint hwnd)
    {
        if (!GetClientRect(hwnd, out RECT client) || !GetWindowRect(hwnd, out RECT window)) return;
        var origin = new POINTSTRUCT();
        ClientToScreen(hwnd, ref origin);

        int top = origin.Y - window.Top;
        var line = new RECT
        {
            Left = origin.X - window.Left, Top = top - 1,
            Right = origin.X - window.Left + client.Right, Bottom = top,
        };

        nint dc = GetWindowDC(hwnd);
        if (dc == 0) return;
        FillRect(dc, line, BarBrush());
        ReleaseDC(hwnd, dc);
    }

    private static nint BarBrush() => s_barBrush != 0 ? s_barBrush : s_barBrush = CreateSolidBrush(Bar);

    private static nint HotBrush() => s_hotBrush != 0 ? s_hotBrush : s_hotBrush = CreateSolidBrush(Hot);

    /// <summary>A COLORREF: red in the low byte.</summary>
    private static uint Colour(Color4 colour) =>
        (uint)Math.Round(colour.R * 255) | (uint)Math.Round(colour.G * 255) << 8 | (uint)Math.Round(colour.B * 255) << 16;
}
