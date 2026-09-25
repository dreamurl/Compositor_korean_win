using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Compositor_korean_win.Core;
using static Compositor_korean_win.Shell.Win32;

namespace Compositor_korean_win.Shell;

/// <summary>
/// Pixels to and from the Windows clipboard, in the two forms other programs use.
/// </summary>
/// <remarks>
/// <para>
/// Copying puts down a PNG — the registered "PNG" format, which browsers, Office and Photoshop read
/// with its transparency — and a 32-bit DIB for everything older. Pasting takes a PNG first, since it
/// keeps alpha, and falls back to a DIB, which is what a screenshot arrives as.
/// </para>
/// <para>
/// Upstream pastes back where the pixels were copied from. The clipboard has no room for a position,
/// so this remembers the last copy's placement with the clipboard's sequence number: if nothing has
/// been copied since, a paste lands in place, and anything from elsewhere is centred.
/// </para>
/// </remarks>
internal sealed unsafe class Clipboard(nint owner)
{
    private const uint CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint CF_UNICODETEXT = 13;

    private static readonly uint PngFormat = RegisterClipboardFormatW("PNG");

    private uint _ours;
    private LayerTransform? _placement;

    /// <summary>Puts pixels on the clipboard, remembering where they came from.</summary>
    public void Put(PixelBuffer pixels, LayerTransform placement)
    {
        if (!OpenClipboard(owner)) return;
        try
        {
            EmptyClipboard();
            Give(PngFormat, Png.Encode(pixels));
            Give(CF_DIB, Dib(pixels));
        }
        finally
        {
            CloseClipboard();
        }

        _ours = GetClipboardSequenceNumber();
        _placement = placement;
    }

    /// <summary>
    /// The clipboard's image, and where to put it when it is our own copy; null when it holds none.
    /// </summary>
    public (PixelBuffer Pixels, LayerTransform? Placement)? Take()
    {
        byte[]? png = null, dib = null;

        if (!OpenClipboard(owner)) return null;
        try
        {
            if (IsClipboardFormatAvailable(PngFormat)) png = Read(PngFormat);
            if (png is null && IsClipboardFormatAvailable(CF_DIB)) dib = Read(CF_DIB);
        }
        finally
        {
            CloseClipboard();
        }

        LayerTransform? placement = GetClipboardSequenceNumber() == _ours ? _placement : null;

        if (png is not null)
        {
            try
            {
                using var loader = new ImageLoader();
                return (loader.Load(png, ImageLoader.KernelFormat), placement);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("clipboard PNG unreadable: " + exception.Message);
            }
        }

        return dib is not null && FromDib(dib) is PixelBuffer pixels ? (pixels, placement) : null;
    }

    /// <summary>Puts text on the clipboard. False when the clipboard could not be opened.</summary>
    public static bool PutText(nint owner, string text)
    {
        if (!OpenClipboard(owner)) return false;
        try
        {
            EmptyClipboard();
            // CF_UNICODETEXT: UTF-16 with a terminating null, and Windows line breaks.
            Give(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\0"));
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Whether the clipboard holds anything this can paste.</summary>
    public static bool HasImage => IsClipboardFormatAvailable(PngFormat) || IsClipboardFormatAvailable(CF_DIB);

    private static void Give(uint format, byte[] data)
    {
        nint memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)data.Length);
        if (memory == 0) return;

        nint target = GlobalLock(memory);
        Marshal.Copy(data, 0, target, data.Length);
        GlobalUnlock(memory);

        // The clipboard owns the memory once it takes it, and only then.
        if (SetClipboardData(format, memory) == 0) GlobalFree(memory);
    }

    private static byte[]? Read(uint format)
    {
        nint memory = GetClipboardData(format);
        if (memory == 0) return null;

        nint source = GlobalLock(memory);
        if (source == 0) return null;
        try
        {
            var data = new byte[(int)GlobalSize(memory)];
            Marshal.Copy(source, data, 0, data.Length);
            return data;
        }
        finally
        {
            GlobalUnlock(memory);
        }
    }

    /// <summary>A bottom-up 32-bit DIB with straight alpha, which is how programs read one.</summary>
    private static byte[] Dib(PixelBuffer pixels)
    {
        int width = pixels.Width, height = pixels.Height;
        var data = new byte[40 + width * height * 4];
        Span<byte> header = data.AsSpan(0, 40);

        BinaryPrimitives.WriteInt32LittleEndian(header, 40);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], width * height * 4);

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = pixels.Row(y);
            Span<byte> target = data.AsSpan(40 + (height - 1 - y) * width * 4, width * 4);
            for (int x = 0; x < width; x++)
            {
                int alpha = row[x * 4 + 3];
                int Straight(int value) => alpha == 0 ? 0 : Math.Min(255, (value * 255 + alpha / 2) / alpha);

                target[x * 4] = (byte)Straight(row[x * 4 + 2]);
                target[x * 4 + 1] = (byte)Straight(row[x * 4 + 1]);
                target[x * 4 + 2] = (byte)Straight(row[x * 4]);
                target[x * 4 + 3] = (byte)alpha;
            }
        }

        return data;
    }

    /// <summary>
    /// A 24- or 32-bit uncompressed DIB, as premultiplied RGBA. A 32-bit one whose alpha is all
    /// zero is opaque: most programs leave the fourth byte unused.
    /// </summary>
    internal static PixelBuffer? FromDib(byte[] data)
    {
        if (data.Length < 40) return null;

        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(data);
        int width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        int bits = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(14));
        int compression = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16));
        int colours = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(32));

        bool topDown = rawHeight < 0;
        int height = Math.Abs(rawHeight);
        if (width <= 0 || height <= 0 || width > 30_000 || height > 30_000 || (bits != 24 && bits != 32)) return null;
        if (compression != 0 && compression != 3) return null;

        int offset = headerSize + (compression == 3 && headerSize == 40 ? 12 : 0) + colours * 4;
        int stride = (width * bits / 8 + 3) & ~3;
        if (offset + (long)stride * height > data.Length) return null;

        int step = bits / 8;
        bool anyAlpha = false;
        if (bits == 32)
        {
            for (int y = 0; y < height && !anyAlpha; y++)
                for (int x = 0; x < width; x++)
                    if (data[offset + y * stride + x * 4 + 3] != 0) { anyAlpha = true; break; }
        }

        PixelBuffer pixels = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            int sourceRow = topDown ? y : height - 1 - y;
            ReadOnlySpan<byte> source = data.AsSpan(offset + sourceRow * stride, width * step);
            Span<byte> row = pixels.Row(y);

            for (int x = 0; x < width; x++)
            {
                int alpha = anyAlpha ? source[x * step + 3] : 255;
                row[x * 4] = (byte)((source[x * step + 2] * alpha + 127) / 255);
                row[x * 4 + 1] = (byte)((source[x * step + 1] * alpha + 127) / 255);
                row[x * 4 + 2] = (byte)((source[x * step] * alpha + 127) / 255);
                row[x * 4 + 3] = (byte)alpha;
            }
        }

        return pixels;
    }
}
