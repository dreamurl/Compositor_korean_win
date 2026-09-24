using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Compositor_korean_win.Core;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using SharpGen.Runtime;
using static Compositor_korean_win.Shell.Win32;
using Point = Compositor_korean_win.Core.Point;

namespace Compositor_korean_win.Shell;

/// <summary>A plain Win32 window, presenting either a canvas or a single image.</summary>
/// <remarks>
/// M0 made this the whole shell: a window class, a message loop and a swap chain, with no widget
/// toolkit under it, and the figures it produced are what settled docs/windows-port.md §5.1. M3
/// gives it a <see cref="CanvasView"/> and the messages that drive one. The image path is kept
/// because the self-test still measures the plainest thing the window can do.
/// </remarks>
internal sealed unsafe class MainWindow : IDisposable
{
    private const string ClassName = "CompositorKoreanWinMain";

    private static MainWindow? s_instance;

    private readonly GraphicsDevice _device;
    private readonly Format _format;
    private PixelBuffer? _image;
    private ID2D1Bitmap1? _bitmap;
    private bool _sized;
    private OleImageDropTarget? _dropTarget;
    private bool _oleInitialized;
    private bool _oleRegistered;
    private bool _brushAdjusting;

    /// <summary>When the last canvas frame was drawn, for keeping frames coming while the pointer moves.</summary>
    private long _lastFrame;

    /// <summary>
    /// Where a right press on the canvas went down, while it may still be a click — let go there, it
    /// opens the canvas menu. Moving further than <see cref="ClickSlop"/> makes it a drag (a brush
    /// tool's sizing) and forgets it.
    /// </summary>
    private Point? _rightClickFrom;

    /// <summary>How far, in pixels, a press may wander and still be a click — Windows' default drag threshold.</summary>
    private const double ClickSlop = 4;

    /// <summary>
    /// Alt was held for a pointer gesture — a clone source, a colour taken, a copy dragged. Letting
    /// it go then must not open the menu bar, as it does not in Photoshop for Windows; a plain tap
    /// of Alt still does.
    /// </summary>
    private bool _altUsed;

    /// <summary>Whether a WM_MOUSELEAVE has been asked for since the last one arrived.</summary>
    private bool _trackingLeave;

    public nint Handle { get; private set; }

    /// <summary>The canvas this window shows, once one has been opened.</summary>
    public CanvasView? Canvas { get; set; }

    /// <summary>The menu bar, and through it every command and its shortcut.</summary>
    public MenuBar? Menu { get; set; }

    /// <summary>Asked before the window closes; false keeps it open (unsaved changes, cancelled).</summary>
    public Func<bool>? CanClose { get; set; }

    /// <summary>Files dropped on the window from Explorer, in the order they came.</summary>
    public Action<IReadOnlyList<string>>? FilesDropped { get; set; }

    public Action<IReadOnlyList<string>, DropDestination>? OleFilesDropped { get; set; }

    public Action<byte[], bool, string?, DropDestination>? ImageDataDropped { get; set; }

    /// <summary>The paths a WM_DROPFILES carries.</summary>
    private static unsafe List<string> DroppedFiles(nint drop)
    {
        var paths = new List<string>();
        uint count = DragQueryFileW(drop, uint.MaxValue, null, 0);
        char* buffer = stackalloc char[4096];
        for (uint index = 0; index < count; index++)
        {
            uint length = DragQueryFileW(drop, index, buffer, 4096);
            if (length > 0) paths.Add(new string(buffer, 0, (int)length));
        }
        return paths;
    }

    /// <summary>Set once the first frame has been presented.</summary>
    public TimeSpan? TimeToFirstFrame { get; private set; }

    public MainWindow(GraphicsDevice device, Format format, int width, int height, bool visible)
    {
        _device = device;
        _format = format;
        s_instance = this;

        nint instance = GetModuleHandleW(0);

        fixed (char* className = ClassName)
        {
            WNDCLASSEXW windowClass = new()
            {
                cbSize = (uint)Unsafe.SizeOf<WNDCLASSEXW>(),
                style = CS_HREDRAW | CS_VREDRAW | CS_DBLCLKS,
                lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WndProc,
                hInstance = instance,
                hCursor = LoadCursorW(0, IDC_ARROW),
                // The exe's icon on the title bar and the taskbar too, not the generic window.
                hIcon = LoadIconW(instance, ApplicationIconId),
                lpszClassName = (nint)className,
            };

            if (RegisterClassExW(windowClass) == 0)
            {
                int error = Marshal.GetLastWin32Error();
                // 1410 is ERROR_CLASS_ALREADY_EXISTS, which is fine on a second window.
                if (error != 1410) throw new Win32Exception(error, "RegisterClassExW failed");
            }
        }

        Handle = CreateWindowExW(0, ClassName, Localizer.Text(TextKey.AppTitle),
                                 WS_OVERLAPPEDWINDOW, int.MinValue, int.MinValue, width, height,
                                 0, 0, instance, 0);

        if (Handle == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW failed");

        DarkFrame.Apply(Handle);

        // No input method on the window itself. With the Korean IME in Hangul mode every key reaches
        // the window as VK_PROCESSKEY, so B, V, [ and the rest did nothing — and Korean Windows users
        // are in Hangul mode as often as not. Photoshop for Windows reads its single-key shortcuts
        // through the same way. The layer rename box is a window of its own with its own context,
        // so Korean names still type as they should.
        HangulMode = ReadHangul(Handle) ?? false;
        ImmAssociateContextEx(Handle, 0, 0);

        ShowWindow(Handle, visible ? SW_SHOW : SW_HIDE);
        DragAcceptFiles(Handle, true);

        Localizer.Changed += Retitle;
        if (visible) UpdateWindow(Handle);

        Resize();
    }

    /// <summary>
    /// Hands the window a canvas, and the canvas the size the window already has.
    /// </summary>
    /// <remarks>
    /// The window is sized before a canvas exists, so its one WM_SIZE has already been and gone.
    /// Without this the canvas would fit a document to a view of no size and show nothing until
    /// the user dragged the window.
    /// </remarks>
    public void AttachCanvas(CanvasView canvas)
    {
        Canvas = canvas;
        // A model run finishing on its own thread asks for a frame; InvalidateRect may be called
        // from any thread, and the frame it brings picks the result up (CanvasView.Tick).
        nint handle = Handle;
        canvas.Wake = () => InvalidateRect(handle, 0, false);
        LayOut();
    }

    /// <summary>The panels round the canvas, once they exist. The canvas then gets what they leave.</summary>
    public Chrome? Chrome { get; private set; }

    public void AttachChrome(Chrome chrome)
    {
        Chrome = chrome;
        LayOut();
    }

    /// <summary>Accepts both file paths and in-memory images from OLE drag sources.</summary>
    public void EnableOleDrops()
    {
        if (_oleInitialized) return;
        int initialized = OleInitialize(0);
        if (initialized < 0) return;
        _oleInitialized = true;
        _dropTarget = new OleImageDropTarget(
            (paths, point) => Guarded(() => OleFilesDropped?.Invoke(paths, DropDestinationFor(point))),
            (data, png, name, point) => Guarded(() => ImageDataDropped?.Invoke(data, png, name, DropDestinationFor(point))));
        _oleRegistered = RegisterDragDrop(Handle, _dropTarget) >= 0;
    }

    private DropDestination DropDestinationFor(PointL screen)
    {
        POINT client = new() { X = screen.X, Y = screen.Y };
        ScreenToClient(Handle, ref client);
        return Chrome?.DropDestinationAt(new Point(client.X, client.Y)) ?? new DropDestination(null, false);
    }

    /// <summary>Tells the canvas which part of the window is its own.</summary>
    private void LayOut()
    {
        if (Canvas is not CanvasView canvas) return;
        GetClientRect(Handle, out RECT client);
        int width = Math.Max(1, client.Width), height = Math.Max(1, client.Height);
        double scale = BackingScale();

        if (Chrome is Chrome chrome) canvas.Resize(chrome.CanvasArea(width, height, scale), width, height, scale);
        else canvas.Resize(width, height, scale);
    }

    /// <summary>Redraws after a change the canvas did not make — a panel's.</summary>
    public void Invalidate() => InvalidateRect(Handle, 0, false);

    /// <summary>The image the window shows. The window takes its own share of the buffer.</summary>
    public void SetImage(PixelBuffer image)
    {
        _bitmap?.Dispose();
        _image?.Release();

        _image = image.Retain();
        _bitmap = ImageLoader.Upload(_device.D2DContext, _image, _format);
    }

    /// <summary>Device pixels per point for this window, from its own DPI.</summary>
    private double BackingScale()
    {
        uint dpi = GetDpiForWindow(Handle);
        return dpi == 0 ? 1 : dpi / 96.0;
    }

    private void Resize()
    {
        GetClientRect(Handle, out RECT client);
        _device.BindWindow(Handle, client.Width, client.Height, _format);
        LayOut();
        _sized = true;
    }

    /// <summary>Draws one frame: the canvas if there is one, otherwise the image, centred.</summary>
    public void Render() => Render(present: true);

    /// <summary>
    /// Draws one frame, and presents it unless asked not to — the self-test reads a frame back
    /// before presenting, since presenting leaves the back buffer's contents undefined.
    /// </summary>
    public void Render(bool present)
    {
        if (!_sized) return;
        Canvas?.Tick();

        if (Canvas is CanvasView canvas)
        {
            canvas.Render();
            if (Chrome is Chrome chrome)
            {
                GetClientRect(Handle, out RECT client);
                chrome.Draw(_device.D2DContext, Math.Max(1, client.Width), Math.Max(1, client.Height), BackingScale());
            }
            if (present) _device.Present();
            _lastFrame = Environment.TickCount64;
            TimeToFirstFrame ??= ProcessUptime();
            return;
        }

        ID2D1DeviceContext context = _device.D2DContext;

        context.BeginDraw();
        context.Clear(new Color4(0.15f, 0.15f, 0.16f, 1.0f));

        if (_bitmap is not null && _image is not null)
        {
            GetClientRect(Handle, out RECT client);

            // Fit inside the window, but never scale up past 1:1 — upstream keeps a document's
            // pixels honest at 100% and anything above that is the zoom's business, not the fit's.
            double scale = Math.Min(1.0, Math.Min((double)client.Width / _image.Width,
                                                  (double)client.Height / _image.Height));
            float left = (float)((client.Width - _image.Width * scale) / 2);
            float top = (float)((client.Height - _image.Height * scale) / 2);

            context.Transform = Matrix3x2.CreateScale((float)scale) * Matrix3x2.CreateTranslation(left, top);
            context.DrawBitmap(_bitmap, 1.0f,
                               scale < 1.0 ? InterpolationMode.HighQualityCubic
                                           : InterpolationMode.NearestNeighbor);
            context.Transform = Matrix3x2.Identity;
        }

        context.EndDraw().CheckError();
        _device.Present();

        TimeToFirstFrame ??= ProcessUptime();
    }

    /// <summary>Pumps every queued message. Returns false once the window has asked to quit.</summary>
    public static bool PumpMessages()
    {
        while (PeekMessageW(out MSG message, 0, 0, 0, PM_REMOVE))
        {
            if (message.message == WM_QUIT) return false;
            TranslateMessage(message);
            DispatchMessageW(message);
        }
        return true;
    }

    /// <summary>Whether a drag should pan rather than transform: space held, or the wheel pressed.</summary>
    private static bool IsPanning(bool middleButton) => middleButton || IsKeyDown(VK_SPACE);

    private const nuint TooltipTimer = 1;

    /// <summary>
    /// Keeps a drag scrolling while the pointer rests past the canvas's edge. Mouse moves alone
    /// would stop the view the moment the hand stops, which is not how upstream's marquee behaves.
    /// </summary>
    private const nuint AutoScrollTimer = 2;

    /// <summary>A button went down on a panel, so the drag that follows is the panel's.</summary>
    private bool _uiHasPointer;

    /// <summary>
    /// Keys meant for a panel's own text box, looked at before they are dispatched. True when taken.
    /// </summary>
    /// <summary>The canvas's right-click menu, at the pointer, for the selection there is or is not.</summary>
    private void ShowCanvasMenu()
    {
        if (Canvas is not CanvasView canvas || !canvas.HasDocument || Menu is not MenuBar menu) return;
        GetCursorPos(out POINTSTRUCT at);
        if (menu.Popup(AppCommands.CanvasMenu(canvas.HasSelection), at.X, at.Y)) Invalidate();
    }

    public bool PreTranslate(in MSG message) => Chrome?.RenameKey(message) == true || Chrome?.TextBoxKey(message) == true;

    private void AfterInput()
    {
        if (Canvas?.NeedsRedraw == true) InvalidateRect(Handle, 0, false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        MainWindow? window = s_instance;
        CanvasView? canvas = window?.Canvas;

        if (DarkFrame.Handle(hwnd, message, wParam, lParam, out nint framed)) return framed;

        switch (message)
        {
            case WM_ERASEBKGND:
                return 1; // Direct2D covers every pixel; letting Win32 erase first only flickers.

            case WM_PAINT:
                window?.Guarded(window.Render);
                break;

            case WM_SIZE:
                if (window is not null && window.Handle == hwnd)
                {
                    window.Resize();
                    window.Render();
                }
                break;

            case WM_LBUTTONDOWN or WM_MBUTTONDOWN or WM_LBUTTONDBLCLK:
                if (canvas is not null && window is not null)
                {
                    if (IsKeyDown(VK_MENU)) window._altUsed = true;
                    SetCapture(hwnd);
                    var at = new Point(PositionX(lParam), PositionY(lParam));
                    window.Guarded(() =>
                    {
                        // A click anywhere ends a rename in progress, keeping what was typed.
                        window.Chrome?.FinishRename(commit: true);

                        // A click on the canvas while words are being typed only keeps them, as
                        // Photoshop's does; on a panel it goes on to do what it does there too.
                        if (window.Chrome?.FinishTextBox(commit: true) == true && message != WM_MBUTTONDOWN
                            && window.Chrome.OverPanels(at) == false)
                        {
                            window.Invalidate();
                            return;
                        }

                        if (message != WM_MBUTTONDOWN && window.Chrome?.Ui.PointerDown(at, message == WM_LBUTTONDBLCLK) == true)
                        {
                            window._uiHasPointer = true;
                            window.Invalidate();
                            return;
                        }

                        Point view = canvas.ToView(PositionX(lParam), PositionY(lParam));
                        // Photoshop for Windows' temporary Zoom: Ctrl+Space zooms in, Alt+Space out,
                        // where the Mac has Command+Space.
                        if (message != WM_MBUTTONDOWN && IsKeyDown(VK_SPACE)
                            && (IsKeyDown(VK_CONTROL) || IsKeyDown(VK_MENU)))
                        {
                            canvas.BeginZoomClick(view, zoomOut: IsKeyDown(VK_MENU));
                            return;
                        }
                        canvas.PointerDown(view, IsPanning(message == WM_MBUTTONDOWN),
                                           doubleClick: message == WM_LBUTTONDBLCLK);
                        // Liquify's Twirl, Pucker, Bloat and Reconstruct work while held still too,
                        // on the same tick the edge scroll runs on.
                        if (canvas.WantsAutoScroll) SetTimer(hwnd, AutoScrollTimer, 16, 0);
                    });
                    window.AfterInput();
                }
                break;

            case WM_MOUSEMOVE:
                if (canvas is not null && window is not null)
                {
                    var at = new Point(PositionX(lParam), PositionY(lParam));
                    if (window._rightClickFrom is Point pressed
                        && Math.Max(Math.Abs(at.X - pressed.X), Math.Abs(at.Y - pressed.Y)) > ClickSlop)
                    {
                        window._rightClickFrom = null;
                    }
                    window.Guarded(() =>
                    {
                        if (window._brushAdjusting)
                        {
                            canvas.DragBrushAdjust(canvas.ToView(PositionX(lParam), PositionY(lParam)),
                                                   IsKeyDown(VK_SHIFT));
                            return;
                        }
                        if (window.Chrome?.Ui is Ui ui && ui.PointerMoved(at))
                        {
                            window.Invalidate();
                            if (ui.TooltipPending) SetTimer(hwnd, TooltipTimer, Ui.TooltipDelay + 50, 0);
                        }
                        // The brush ring belongs over the picture; off it — on a panel, a bar, or
                        // out of the window (WM_MOUSELEAVE) — it goes, as upstream's cursor does.
                        if (!window._trackingLeave)
                        {
                            var track = new TRACKMOUSEEVENT
                            {
                                cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE, hwndTrack = hwnd,
                            };
                            window._trackingLeave = TrackMouseEvent(ref track);
                        }
                        canvas.PointerOverCanvas(window.Chrome?.OverPanels(at) != true);

                        if (window._uiHasPointer) return;

                        canvas.PointerMoved(canvas.ToView(PositionX(lParam), PositionY(lParam)),
                                            IsKeyDown(VK_SHIFT), IsKeyDown(VK_MENU), IsKeyDown(VK_CONTROL));
                        // About sixty ticks a second, as upstream's; the tick stops itself once the
                        // pointer is back inside or the drag is over.
                        if (canvas.WantsAutoScroll) SetTimer(hwnd, AutoScrollTimer, 16, 0);
                    });
                    window.AfterInput();

                    // Windows hands out WM_PAINT only when no input is waiting, and a moving mouse
                    // always has a move waiting once a frame takes longer than the mouse's report
                    // rate — a document of many layers — so the canvas froze until the pointer
                    // stopped. Past a frame's time the frame is drawn now instead.
                    if (Environment.TickCount64 - window._lastFrame >= 16) UpdateWindow(hwnd);
                }
                break;

            case WM_RBUTTONDOWN:
                if (window is not null && IsKeyDown(VK_MENU)) window._altUsed = true;
                if (window is not null) window._rightClickFrom = null;
                if (canvas is not null && window is not null
                    && window.Chrome?.OverPanels(new Point(PositionX(lParam), PositionY(lParam))) != true)
                {
                    // Not while the left button is down: that is a stroke or a drag under way.
                    if ((wParam & MK_LBUTTON) == 0)
                        window._rightClickFrom = new Point(PositionX(lParam), PositionY(lParam));
                    window._brushAdjusting = canvas.BeginBrushAdjust(canvas.ToView(PositionX(lParam), PositionY(lParam)));
                    if (window._brushAdjusting) SetCapture(hwnd);
                }
                break;

            case WM_LBUTTONUP or WM_MBUTTONUP:
                KillTimer(hwnd, AutoScrollTimer);
                if (canvas is not null && window is not null)
                {
                    ReleaseCapture();
                    var at = new Point(PositionX(lParam), PositionY(lParam));
                    window.Guarded(() =>
                    {
                        if (window._uiHasPointer)
                        {
                            window._uiHasPointer = false;
                            window.Chrome?.Ui.PointerUp(at);
                            window.Invalidate();
                            return;
                        }
                        canvas.PointerUp();
                    });
                    window.AfterInput();
                }
                break;

            // A right click on a layer's row opens its menu; on the canvas, the canvas menu. A brush
            // tool's right-drag sizes the tip, so there only a press let go where it went down counts.
            case WM_RBUTTONUP:
                if (window is not null)
                {
                    bool clicked = window._rightClickFrom is not null;
                    window._rightClickFrom = null;

                    if (window._brushAdjusting && canvas is not null)
                    {
                        // Cancel before letting go: WM_CAPTURECHANGED would otherwise keep the wobble.
                        if (clicked) canvas.CancelBrushAdjust();
                        ReleaseCapture();
                        window._brushAdjusting = false;
                        canvas.EndBrushAdjust();
                        if (clicked) window.Guarded(window.ShowCanvasMenu);
                        window.AfterInput();
                    }
                    else if (window.Chrome is Chrome menuChrome)
                    {
                        var at = new Point(PositionX(lParam), PositionY(lParam));
                        window.Guarded(() =>
                        {
                            if (menuChrome.ContextMenu(at)) window.Invalidate();
                            else if (clicked) window.ShowCanvasMenu();
                        });
                        window.AfterInput();
                    }
                }
                break;

            case WM_TIMER when (nuint)wParam == AutoScrollTimer:
                if (canvas is null || window is null)
                {
                    KillTimer(hwnd, AutoScrollTimer);
                    break;
                }
                window.Guarded(() =>
                {
                    if (!canvas.AutoScrollTick(IsKeyDown(VK_SHIFT), IsKeyDown(VK_MENU), IsKeyDown(VK_CONTROL)))
                        KillTimer(hwnd, AutoScrollTimer);
                });
                window.AfterInput();
                break;

            case WM_TIMER when (nuint)wParam == TooltipTimer:
                KillTimer(hwnd, TooltipTimer);
                window?.Invalidate();
                break;

            case WM_MOUSEWHEEL:
                if (canvas is not null && window is not null)
                {
                    // The wheel reports the pointer on the desktop, not in the window.
                    var where = new POINT { X = PositionX(lParam), Y = PositionY(lParam) };
                    ScreenToClient(hwnd, ref where);
                    double notches = WheelDelta(wParam) / (double)WHEEL_DELTA;

                    if (window.Chrome?.Scroll(new Point(where.X, where.Y), notches) == true) window.Invalidate();
                    else if (window.Chrome?.OverPanels(new Point(where.X, where.Y)) != true)
                        canvas.Wheel(canvas.ToView(where.X, where.Y), notches,
                                     shift: IsKeyDown(VK_SHIFT), control: IsKeyDown(VK_CONTROL));
                    window.AfterInput();
                }
                break;

            // Alt+Space is the window's system menu to Windows, but Photoshop's zoom-out key. While a
            // document is open it is the canvas's.
            case WM_SYSKEYDOWN when (int)wParam == VK_SPACE && canvas?.Document is not null:
                return 0;

            // A fresh Alt press starts clean; the auto-repeat of a held one does not.
            case WM_SYSKEYDOWN when (int)wParam == VK_MENU && (lParam & (1 << 30)) == 0 && window is not null:
                window._altUsed = false;
                break;

            case WM_SYSKEYUP when (int)wParam == VK_MENU && window?._altUsed == true:
                window._altUsed = false;
                return 0;

            // The window has no input context, so its Han/Eng key toggles nothing; it is remembered
            // for the rename box, which should open in the mode the user last chose.
            case WM_KEYDOWN when (int)wParam == VK_HANGUL:
                HangulMode = !HangulMode;
                return 0;

            case WM_MOUSELEAVE:
                if (window is not null) window._trackingLeave = false;
                if (canvas is not null && window is not null)
                {
                    canvas.PointerOverCanvas(false);
                    window.AfterInput();
                }
                break;

            case WM_KEYDOWN or WM_SYSKEYDOWN:
                // Should an input method still be composing (one attached by another route), the key
                // it swallowed is still there to be read.
                if ((int)wParam == VK_PROCESSKEY && ImmGetVirtualKey(hwnd) is var real and not 0 and not (uint)VK_PROCESSKEY)
                    wParam = real;
                if (canvas is not null && window is not null && window.KeyDown((int)wParam, canvas))
                {
                    window.AfterInput();
                    return 0;
                }
                break;

            case WM_DROPFILES:
                if (window is not null)
                {
                    List<string> dropped = DroppedFiles((nint)wParam);
                    DragFinish((nint)wParam);
                    // Nothing lands while a sheet holds the window still.
                    if (window.Chrome?.HasSheet != true && window.FilesDropped is { } take)
                    {
                        window.Guarded(() => take(dropped));
                        window.AfterInput();
                        window.Invalidate();
                    }
                }
                return 0;

            case WM_CHAR:
                if (window?.Chrome?.SheetChar((char)wParam) == true)
                {
                    window.Invalidate();
                    return 0;
                }
                break;

            case WM_COMMAND:
                // The typing box reports each change to its owner.
                if (window?.Chrome is Chrome typing && typing.TextBox != 0 && lParam == typing.TextBox)
                {
                    if ((int)(wParam >> 16) == EN_CHANGE) window.Guarded(typing.TextBoxChanged);
                    return 0;
                }
                // The high word is 0 for a menu; accelerators and controls are not in play.
                if (window is not null && (wParam >> 16) == 0)
                {
                    window.Guarded(() => window.Menu?.Run((int)(wParam & 0xFFFF)));
                    window.AfterInput();
                    return 0;
                }
                break;

            case WM_INITMENUPOPUP:
                window?.Menu?.Refresh((nint)wParam);
                break;

            case WM_CAPTURECHANGED:
                KillTimer(hwnd, AutoScrollTimer);
                if (window?._brushAdjusting == true && canvas is not null)
                {
                    window._brushAdjusting = false;
                    canvas.EndBrushAdjust();
                }
                else canvas?.PointerUp();
                break;

            case WM_CLOSE:
                if (window?.CanClose?.Invoke() == false) return 0;
                break;

            case WM_DESTROY:
                PostQuitMessage(0);
                break;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>
    /// Sends a key where it belongs. True when something took it.
    /// </summary>
    /// <remarks>
    /// An open filter comes first, since it owns Enter and Escape and holds everything else back
    /// until it closes; then the commands' shortcuts; then the canvas's own single keys — tools,
    /// brush size, nudging. A key nobody takes goes on to Windows, which is how Alt and F10 still
    /// reach the menu bar.
    /// </remarks>
    private bool KeyDown(int key, CanvasView canvas)
    {
        bool control = IsKeyDown(VK_CONTROL), shift = IsKeyDown(VK_SHIFT), alt = IsKeyDown(VK_MENU);
        bool taken = false;

        Guarded(() =>
        {
            if (Chrome?.SheetKey(key, control, shift, alt) == true || Chrome?.FieldKey(key, shift) == true)
            {
                taken = true;
                Invalidate();
            }
            else if (canvas.IsFiltering && canvas.Key(key, control, shift, alt)) taken = true;
            // A stroke or a polygonal outline under way takes its keys before the menus do:
            // Backspace takes back a corner rather than clearing the selection, and Ctrl+Z waits.
            else if (canvas.DraftKey(key)) taken = true;
            else if (Menu?.TryShortcut(new Shortcut(key, control, shift, alt)) == true) taken = true;
            else if (!alt) taken = canvas.Key(key, control, shift, alt);
        });

        return taken;
    }

    /// <summary>
    /// Runs something the user asked for, and reports rather than dies if it throws.
    /// </summary>
    /// <remarks>
    /// This is called from the window procedure, which native code calls: an exception escaping
    /// it cannot unwind into Windows and would end the process. The log gets the detail.
    /// </remarks>
    internal void Guarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
        }
    }

    /// <summary>Puts the title in the language just chosen.</summary>
    private void Retitle() => SetWindowTextW(Handle, Localizer.Text(TextKey.AppTitle));

    public void Dispose()
    {
        Localizer.Changed -= Retitle;
        _bitmap?.Dispose();
        _image?.Release();
        if (_oleRegistered)
        {
            RevokeDragDrop(Handle);
            _oleRegistered = false;
        }
        _dropTarget = null;
        if (_oleInitialized)
        {
            OleUninitialize();
            _oleInitialized = false;
        }
        if (Handle != 0)
        {
            DestroyWindow(Handle);
            Handle = 0;
        }
        if (ReferenceEquals(s_instance, this)) s_instance = null;
    }
}

internal sealed class Win32Exception(int error, string message)
    : Exception($"{message} (GetLastError = {error})");
