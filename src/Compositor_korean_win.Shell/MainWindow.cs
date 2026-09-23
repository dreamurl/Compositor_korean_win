using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Compositor_korean_win.Core;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using SharpGen.Runtime;
using static Compositor_korean_win.Shell.Win32;

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
                style = CS_HREDRAW | CS_VREDRAW,
                lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WndProc,
                hInstance = instance,
                hCursor = LoadCursorW(0, IDC_ARROW),
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
        GetClientRect(Handle, out RECT client);
        canvas.Resize(Math.Max(1, client.Width), Math.Max(1, client.Height), BackingScale());
    }

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
        Canvas?.Resize(Math.Max(1, client.Width), Math.Max(1, client.Height), BackingScale());
        _sized = true;
    }

    /// <summary>Draws one frame: the canvas if there is one, otherwise the image, centred.</summary>
    public void Render()
    {
        if (!_sized) return;

        if (Canvas is CanvasView canvas)
        {
            canvas.Render();
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
                window?.Render();
                break;

            case WM_SIZE:
                if (window is not null && window.Handle == hwnd)
                {
                    window.Resize();
                    window.Render();
                }
                break;

            case WM_LBUTTONDOWN or WM_MBUTTONDOWN:
                if (canvas is not null && window is not null)
                {
                    SetCapture(hwnd);
                    canvas.PointerDown(canvas.ToView(PositionX(lParam), PositionY(lParam)),
                                       IsPanning(message == WM_MBUTTONDOWN));
                    window.AfterInput();
                }
                break;

            case WM_MOUSEMOVE:
                if (canvas is not null && window is not null)
                {
                    canvas.PointerMoved(canvas.ToView(PositionX(lParam), PositionY(lParam)),
                                        IsKeyDown(VK_SHIFT), IsKeyDown(VK_MENU), IsKeyDown(VK_CONTROL));
                    window.AfterInput();
                }
                break;

            case WM_LBUTTONUP or WM_MBUTTONUP:
                if (canvas is not null && window is not null)
                {
                    ReleaseCapture();
                    canvas.PointerUp();
                    window.AfterInput();
                }
                break;

            case WM_MOUSEWHEEL:
                if (canvas is not null && window is not null)
                {
                    // The wheel reports the pointer on the desktop, not in the window.
                    var where = new POINT { X = PositionX(lParam), Y = PositionY(lParam) };
                    ScreenToClient(hwnd, ref where);
                    canvas.Wheel(canvas.ToView(where.X, where.Y),
                                 WheelDelta(wParam) / (double)WHEEL_DELTA);
                    window.AfterInput();
                }
                break;

            case WM_KEYDOWN:
                // Escape cancels whatever the canvas has open first, and only quits when nothing is.
                if ((int)wParam == VK_ESCAPE && canvas?.Key(VK_ESCAPE, control: false) != true)
                {
                    PostQuitMessage(0);
                    break;
                }

                if ((int)wParam == VK_ESCAPE)
                {
                    window?.AfterInput();
                    break;
                }

                if (canvas is not null && window is not null)
                {
                    canvas.Key((int)wParam, IsKeyDown(VK_CONTROL));
                    window.AfterInput();
                }
                break;

            case WM_CAPTURECHANGED:
                canvas?.PointerUp();
                break;

            case WM_DESTROY:
                PostQuitMessage(0);
                break;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
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
