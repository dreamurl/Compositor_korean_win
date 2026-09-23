using Compositor_korean_win.Core;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.WIC;

namespace Compositor_korean_win.Shell;

/// <summary>
/// Reads images through the Windows Imaging Component, which stands in for upstream's ImageIO.
/// </summary>
/// <remarks>
/// WIC ships with Windows and already decodes everything upstream imports — JPEG, PNG, TIFF and
/// HEIC — so nothing has to be bundled for this.
/// </remarks>
internal sealed class ImageLoader : IDisposable
{
    private readonly IWICImagingFactory _factory = new();

    /// <summary>
    /// Premultiplied RGBA, the channel order the C kernels assume.
    /// </summary>
    public static readonly Guid KernelFormat = PixelFormat.Format32bppPRGBA;

    /// <summary>
    /// Premultiplied BGRA, the channel order Direct2D is conventionally fed.
    /// </summary>
    public static readonly Guid Direct2DFormat = PixelFormat.Format32bppPBGRA;

    /// <summary>Decodes the first frame of <paramref name="path"/> into a new buffer.</summary>
    /// <param name="pixelFormat">
    /// <see cref="KernelFormat"/> or <see cref="Direct2DFormat"/>. Which of the two the whole
    /// pipeline should settle on is the open question docs/windows-port.md §9 puts to M0; keeping
    /// both reachable is what lets the self-test answer it by measurement.
    /// </param>
    public unsafe PixelBuffer Load(string path, Guid pixelFormat)
    {
        using IWICBitmapDecoder decoder =
            _factory.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnDemand);
        using IWICBitmapFrameDecode frame = decoder.GetFrame(0);

        using IWICFormatConverter converter = _factory.CreateFormatConverter();
        converter.Initialize(frame, pixelFormat).CheckError();

        var size = converter.Size;
        PixelBuffer buffer = PixelBuffer.Allocate(size.Width, size.Height);
        converter.CopyPixels((uint)buffer.Stride, (uint)buffer.ByteCount, buffer.Scan0);
        return buffer;
    }

    /// <summary>Decodes an image held in memory — a PNG from the clipboard — into a new buffer.</summary>
    public PixelBuffer Load(byte[] data, Guid pixelFormat)
    {
        using var stream = new MemoryStream(data, writable: false);
        using IWICBitmapDecoder decoder = _factory.CreateDecoderFromStream(stream, DecodeOptions.CacheOnDemand);
        using IWICBitmapFrameDecode frame = decoder.GetFrame(0);

        using IWICFormatConverter converter = _factory.CreateFormatConverter();
        converter.Initialize(frame, pixelFormat).CheckError();

        var size = converter.Size;
        PixelBuffer buffer = PixelBuffer.Allocate(size.Width, size.Height);
        converter.CopyPixels((uint)buffer.Stride, (uint)buffer.ByteCount, buffer.Scan0);
        return buffer;
    }

    /// <summary>Uploads <paramref name="buffer"/> to the GPU as a Direct2D bitmap.</summary>
    public static ID2D1Bitmap1 Upload(ID2D1DeviceContext context, PixelBuffer buffer,
                                      Vortice.DXGI.Format format)
    {
        BitmapProperties1 properties = new()
        {
            PixelFormat = new Vortice.DCommon.PixelFormat(format, Vortice.DCommon.AlphaMode.Premultiplied),
        };

        return context.CreateBitmap(new Vortice.Mathematics.SizeI(buffer.Width, buffer.Height),
                                    buffer.Scan0, (uint)buffer.Stride, properties);
    }

    public void Dispose() => _factory.Dispose();
}
