using System.Runtime.InteropServices;
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
    /// <summary>WIC's own default quality, which is what Export JPEG used before it had a slider.</summary>
    public const double DefaultQuality = 0.9;

    /// <summary>Writes premultiplied pixels as a JPEG, laid over white first.</summary>
    public static void WriteJpeg(PixelBuffer pixels, string path) =>
        File.WriteAllBytes(path, EncodeJpeg(pixels, DefaultQuality, (1, 1, 1)));

    /// <summary>
    /// Premultiplied pixels as a JPEG file's bytes, at a quality from 0 to 1, laid over
    /// <paramref name="matte"/> first — JPEG has no transparency, so what shows through has to be
    /// some colour.
    /// </summary>
    public static byte[] EncodeJpeg(PixelBuffer pixels, double quality, (double Red, double Green, double Blue) matte)
    {
        int width = pixels.Width, height = pixels.Height;
        int stride = (width * 3 + 3) & ~3;
        var bgr = new byte[stride * height];
        double mr = matte.Red * 255, mg = matte.Green * 255, mb = matte.Blue * 255;

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = pixels.Row(y);
            Span<byte> target = bgr.AsSpan(y * stride, width * 3);
            for (int x = 0; x < width; x++)
            {
                // Over the matte: premultiplied colour plus the matte showing through what is not covered.
                double uncovered = (255 - row[x * 4 + 3]) / 255.0;
                target[x * 3] = Byte(row[x * 4 + 2] + mb * uncovered);
                target[x * 3 + 1] = Byte(row[x * 4 + 1] + mg * uncovered);
                target[x * 3 + 2] = Byte(row[x * 4] + mr * uncovered);
            }
        }

        using var factory = new IWICImagingFactory();
        using var memory = new MemoryStream();
        using (IWICBitmapEncoder encoder = factory.CreateEncoder(ContainerFormat.Jpeg, memory))
        {
            using IWICBitmapFrameEncode frame = encoder.CreateNewFrame(out var options);
            SetQuality(options.NativePointer, (float)Math.Clamp(quality, 0, 1));
            frame.Initialize(options);
            frame.SetSize((uint)width, (uint)height);
            Guid format = PixelFormat.Format24bppBGR;
            frame.SetPixelFormat(ref format);
            frame.WritePixels((uint)height, (uint)stride, bgr);
            frame.Commit();
            encoder.Commit();
            options.Dispose();
        }
        return memory.ToArray();

        static byte Byte(double value) => (byte)Math.Min(255, Math.Round(value));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyBag2
    {
        public uint Type;
        public ushort VariantType;
        public ushort ClipboardFormat;
        public uint Hint;
        public nint Name;
        public Guid Class;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public float Single;
    }

    /// <summary>
    /// Sets the encoder's "ImageQuality" option, a VT_R4 from 0 to 1, through IPropertyBag2::Write
    /// — the fifth entry in its table. The wrapper has no typed way to write an option; this is
    /// what the call it would make comes down to.
    /// </summary>
    private static unsafe void SetQuality(nint bag, float quality)
    {
        if (bag == 0) return;
        nint name = Marshal.StringToCoTaskMemUni("ImageQuality");
        try
        {
            var property = new PropertyBag2 { Name = name };
            var value = new Variant { Type = 4 /* VT_R4 */, Single = quality };
            var write = (delegate* unmanaged[Stdcall]<nint, uint, PropertyBag2*, Variant*, int>)(*(nint**)bag)[4];
            int result = write(bag, 1, &property, &value);
            if (result < 0) Console.Error.WriteLine($"JPEG quality not set: 0x{result:X8}");
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }
}
