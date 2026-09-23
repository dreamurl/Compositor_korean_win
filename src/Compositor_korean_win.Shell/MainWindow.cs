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

    public nint Handle { get; private set; }

    /// <summary>The canvas this window shows, once one has been opened.</summary>
    public CanvasView? Canvas { get; set; }

    /// <summary>The menu bar, and through it every command and its shortcut.</summary>
    public MenuBar? Menu { get; set; }

    /// <summary>Asked before the window closes; false keeps it open (unsaved changes, cancelled).</summary>
    public Func<bool>? CanClose { get; set; }

    /// <summary>Files dropped on the window from Explorer, in the order they came.</summary>
    public Action<IReadOnlyList<string>>? FilesDropped { get; set; }

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

    /// <summary>A button went down on a panel, so the drag that follows is the panel's.</summary>
    private bool _uiHasPointer;

    /// <summary>
    /// Keys meant for a panel's own text box, looked at before they are dispatched. True when taken.
    /// </summary>
    public bool PreTranslate(in MSG message) => Chrome?.RenameKey(message) == true;

    private void AfterInput()
    {
        if (Canvas?.NeedsRedraw == true) InvalidateRect(Handle, 0, false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        MainWindow? window = s_instance;
        CanvasView? canvas = window?.Canvas;

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
                    SetCapture(hwnd);
                    var at = new Point(PositionX(lParam), PositionY(lParam));
                    window.Guarded(() =>
                    {
                        // A click anywhere ends a rename in progress, keeping what was typed.
                        window.Chrome?.FinishRename(commit: true);

                        if (message != WM_MBUTTONDOWN && window.Chrome?.Ui.PointerDown(at, message == WM_LBUTTONDBLCLK) == true)
                        {
                            window._uiHasPointer = true;
                            window.Invalidate();
                            return;
                        }

                        canvas.PointerDown(canvas.ToView(PositionX(lParam), PositionY(lParam)),
                                           IsPanning(message == WM_MBUTTONDOWN));
                    });
                    window.AfterInput();
                }
                break;

            case WM_MOUSEMOVE:
                if (canvas is not null && window is not null)
                {
                    var at = new Point(PositionX(lParam), PositionY(lParam));
                    window.Guarded(() =>
                    {
                        if (window.Chrome?.Ui is Ui ui && ui.PointerMoved(at))
                        {
                            window.Invalidate();
                            if (ui.TooltipPending) SetTimer(hwnd, TooltipTimer, Ui.TooltipDelay + 50, 0);
                        }
                        if (window._uiHasPointer) return;

                        canvas.PointerMoved(canvas.ToView(PositionX(lParam), PositionY(lParam)),
                                            IsKeyDown(VK_SHIFT), IsKeyDown(VK_MENU), IsKeyDown(VK_CONTROL));
                    });
                    window.AfterInput();
                }
                break;

            case WM_LBUTTONUP or WM_MBUTTONUP:
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

            // A right click on a layer's row opens its menu.
            case WM_RBUTTONUP:
                if (window?.Chrome is Chrome menuChrome)
                {
                    var at = new Point(PositionX(lParam), PositionY(lParam));
                    window.Guarded(() =>
                    {
                        if (menuChrome.ContextMenu(at)) window.Invalidate();
                    });
                    window.AfterInput();
                }
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
                        canvas.Wheel(canvas.ToView(where.X, where.Y), notches);
                    window.AfterInput();
                }
                break;

            case WM_KEYDOWN or WM_SYSKEYDOWN:
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
                canvas?.PointerUp();
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
            if (Chrome?.SheetKey(key, control, shift) == true)
            {
                taken = true;
                Invalidate();
            }
            else if (canvas.IsFiltering && canvas.Key(key, control)) taken = true;
            else if (Menu?.TryShortcut(new Shortcut(key, control, shift, alt)) == true) taken = true;
            else if (!alt) taken = canvas.Key(key, control);
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
