using System.Diagnostics;
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

/// <summary>A plain Win32 window that presents one Direct2D frame.</summary>
/// <remarks>
/// M0 is deliberately the whole shell: a window class, a message loop and a swap chain, and no
/// widget toolkit under it. docs/windows-port.md §5.1 leaves the shell open between this, WinUI 3
/// and Avalonia, and the figures this window produces are how that gets decided — the other two
/// only make sense if they buy something worth 20–40 MB.
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

        Handle = CreateWindowExW(0, ClassName, "Compositor 한국어판",
                                 WS_OVERLAPPEDWINDOW, int.MinValue, int.MinValue, width, height,
                                 0, 0, instance, 0);

        if (Handle == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW failed");

        ShowWindow(Handle, visible ? SW_SHOW : SW_HIDE);
        if (visible) UpdateWindow(Handle);

        Resize();
    }

    /// <summary>The image the window shows. The window takes its own share of the buffer.</summary>
    public void SetImage(PixelBuffer image)
    {
        _bitmap?.Dispose();
        _image?.Release();

        _image = image.Retain();
        _bitmap = ImageLoader.Upload(_device.D2DContext, _image, _format);
    }

    private void Resize()
    {
        GetClientRect(Handle, out RECT client);
        _device.BindWindow(Handle, client.Width, client.Height, _format);
        _sized = true;
    }

    /// <summary>Draws one frame: the checkerboard behind the image, then the image, centred.</summary>
    public void Render()
    {
        if (!_sized) return;

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

            // Placing the bitmap by transform rather than by destination rectangle keeps this to
            // the one DrawBitmap overload that takes no rectangle types, and it is the same path
            // the canvas will use in M3, where pan and zoom already live in a transform.
            context.Transform = Matrix3x2.CreateScale((float)scale) * Matrix3x2.CreateTranslation(left, top);
            context.DrawBitmap(_bitmap, 1.0f,
                               scale < 1.0 ? InterpolationMode.HighQualityCubic
                                           : InterpolationMode.NearestNeighbor);
            context.Transform = Matrix3x2.Identity;
        }

        context.EndDraw().CheckError();
        _device.Present();

        TimeToFirstFrame ??= Stopwatch.GetElapsedTime(Program.Started);
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

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        MainWindow? window = s_instance;

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

            case WM_KEYDOWN:
                if ((int)wParam == VK_ESCAPE) PostQuitMessage(0);
                break;

            case WM_DESTROY:
                PostQuitMessage(0);
                break;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
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
