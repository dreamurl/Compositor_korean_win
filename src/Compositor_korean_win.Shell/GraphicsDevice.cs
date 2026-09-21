using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct2D1.D2D1;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The Direct3D 11 device that Direct2D draws through, and the swap chain a window presents from.
/// </summary>
/// <remarks>
/// This is the D2D 1.1 path — an <see cref="ID2D1DeviceContext"/> rather than the older
/// <c>ID2D1HwndRenderTarget</c> — because the effect graph the adjustments and filters need
/// (D2D1GaussianBlur, D2D1ColorMatrix, D2D1LookupTable3D and the rest of the mapping in
/// docs/windows-port.md §3) only exists on a device context.
///
/// Nothing here is bundled: d3d11.dll, dxgi.dll and d2d1.dll all ship with Windows.
/// </remarks>
internal sealed class GraphicsDevice : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    ];

    private IDXGISwapChain1? _swapChain;
    private ID2D1Bitmap1? _backBuffer;

    private GraphicsDevice(IDXGIFactory2 factory, ID3D11Device device, ID3D11DeviceContext context,
                           ID2D1Factory1 d2dFactory, ID2D1Device d2dDevice, ID2D1DeviceContext d2dContext,
                           FeatureLevel featureLevel, bool isWarp)
    {
        DxgiFactory = factory;
        D3DDevice = device;
        D3DContext = context;
        D2DFactory = d2dFactory;
        D2DDevice = d2dDevice;
        D2DContext = d2dContext;
        FeatureLevel = featureLevel;
        IsWarp = isWarp;
    }

    public IDXGIFactory2 DxgiFactory { get; }
    public ID3D11Device D3DDevice { get; }
    public ID3D11DeviceContext D3DContext { get; }
    public ID2D1Factory1 D2DFactory { get; }
    public ID2D1Device D2DDevice { get; }
    public ID2D1DeviceContext D2DContext { get; }
    public FeatureLevel FeatureLevel { get; }

    /// <summary>True when no GPU was available and Windows' software rasteriser took over.</summary>
    /// <remarks>CI runners have no GPU, so the M0 numbers taken there are WARP numbers.</remarks>
    public bool IsWarp { get; }

    public static GraphicsDevice Create()
    {
        IDXGIFactory2 factory = CreateDXGIFactory1<IDXGIFactory2>();

        // BgraSupport is what lets Direct2D use this device at all.
        const DeviceCreationFlags Flags = DeviceCreationFlags.BgraSupport;

        bool warp = false;
        Result result = D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, Flags, FeatureLevels,
                                          out ID3D11Device device, out FeatureLevel level,
                                          out ID3D11DeviceContext context);
        if (result.Failure)
        {
            // No GPU, or none that will take a Direct2D-capable device. WARP is Windows' own
            // software rasteriser; it is slow but complete, so the app still runs everywhere.
            warp = true;
            D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, Flags, FeatureLevels,
                              out device, out level, out context).CheckError();
        }

        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        ID2D1Factory1 d2dFactory = D2D1CreateFactory<ID2D1Factory1>();
        ID2D1Device d2dDevice = d2dFactory.CreateDevice(dxgiDevice);
        ID2D1DeviceContext d2dContext = d2dDevice.CreateDeviceContext();

        return new GraphicsDevice(factory, device, context, d2dFactory, d2dDevice, d2dContext, level, warp);
    }

    /// <summary>
    /// Points the device context at <paramref name="hwnd"/>, creating or resizing the swap chain.
    /// </summary>
    public void BindWindow(nint hwnd, int width, int height, Format format)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        ReleaseBackBuffer();

        if (_swapChain is null)
        {
            SwapChainDescription1 description = new()
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = format,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = SampleDescription.Default,
                Scaling = Scaling.None,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            };

            _swapChain = DxgiFactory.CreateSwapChainForHwnd(D3DDevice, hwnd, description);
            DxgiFactory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
        }
        else
        {
            _swapChain.ResizeBuffers(2, (uint)width, (uint)height, format, SwapChainFlags.None).CheckError();
        }

        using IDXGISurface surface = _swapChain.GetBuffer<IDXGISurface>(0);
        BitmapProperties1 properties = new()
        {
            BitmapOptions = BitmapOptions.Target | BitmapOptions.CannotDraw,
            PixelFormat = new Vortice.DCommon.PixelFormat(format, Vortice.DCommon.AlphaMode.Ignore),
        };

        _backBuffer = D2DContext.CreateBitmapFromDxgiSurface(surface, properties);
        D2DContext.Target = _backBuffer;
    }

    public void Present()
    {
        _swapChain?.Present(1, PresentFlags.None);
    }

    private void ReleaseBackBuffer()
    {
        if (_backBuffer is null) return;
        D2DContext.Target = null;
        _backBuffer.Dispose();
        _backBuffer = null;
    }

    public void Dispose()
    {
        ReleaseBackBuffer();
        _swapChain?.Dispose();
        D2DContext.Dispose();
        D2DDevice.Dispose();
        D2DFactory.Dispose();
        D3DContext.Dispose();
        D3DDevice.Dispose();
        DxgiFactory.Dispose();
    }
}
