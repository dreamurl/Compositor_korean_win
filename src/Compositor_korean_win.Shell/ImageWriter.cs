using Compositor_korean_win.Core;
using SharpGen.Runtime;
using Vortice.WIC;

namespace Compositor_korean_win.Shell;

/// <summary>
/// Writes images through the Windows Imaging Component, for the export formats the Core layer does
/// not write itself.
/// </summary>
/// <remarks>
/// PNG export goes through Core's own encoder, which is what projects are saved with; JPEG needs a
/// DCT encoder, and WIC has one in every copy of Windows.
/// </remarks>
internal static class ImageWriter
{
    /// <summary>Writes premultiplied pixels as a JPEG, laid over white first.</summary>
    public static void WriteJpeg(PixelBuffer pixels, string path)
    {
        int width = pixels.Width, height = pixels.Height;
        int stride = (width * 3 + 3) & ~3;
        var bgr = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = pixels.Row(y);
            Span<byte> target = bgr.AsSpan(y * stride, width * 3);
            for (int x = 0; x < width; x++)
            {
                // Over white: premultiplied colour plus the white showing through what is not covered.
                int uncovered = 255 - row[x * 4 + 3];
                target[x * 3] = (byte)(row[x * 4 + 2] + uncovered);
                target[x * 3 + 1] = (byte)(row[x * 4 + 1] + uncovered);
                target[x * 3 + 2] = (byte)(row[x * 4] + uncovered);
            }
        }

        using var factory = new IWICImagingFactory();
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using IWICBitmapEncoder encoder = factory.CreateEncoder(ContainerFormat.Jpeg, file);
        using IWICBitmapFrameEncode frame = encoder.CreateNewFrame(out IPropertyBag2 options);

        frame.Initialize(options);
        frame.SetSize((uint)width, (uint)height);
        Guid format = PixelFormat.Format24bppBGR;
        frame.SetPixelFormat(ref format);
        frame.WritePixels((uint)height, (uint)stride, bgr);
        frame.Commit();
        encoder.Commit();
        options.Dispose();
    }
}
