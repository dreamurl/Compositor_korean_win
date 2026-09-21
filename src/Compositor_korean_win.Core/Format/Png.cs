using System.Buffers.Binary;
using System.IO.Compression;

namespace Compositor_korean_win.Core;

/// <summary>
/// The slice of PNG a <c>.comp</c> project contains: 8 bits per channel, no interlacing.
/// </summary>
/// <remarks>
/// Decoding images in general is the backend's job — the shell hands that to WIC, which also brings
/// JPEG, TIFF and HEIC. This exists because a project's own assets are the one case the Core layer
/// has to handle by itself: <c>.comp</c> round-tripping is what M1 is judged on, and
/// docs/windows-port.md §8 has those tests running with no backend and no display. A reader for
/// what <c>CGImageDestination</c> writes is a few hundred lines; a dependency on the backend would
/// cost the Core layer its independence.
///
/// PNG stores straight alpha and a <see cref="PixelBuffer"/> holds premultiplied, so reading
/// multiplies through and writing divides back out.
/// </remarks>
public static class Png
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private enum ColorType { Grey = 0, Rgb = 2, Palette = 3, GreyAlpha = 4, Rgba = 6 }

    /// <summary>What a PNG's header says, without decoding its pixels.</summary>
    public readonly record struct Header(int Width, int Height, int BitDepth, int Channels)
    {
        public bool IsGreyscaleWithoutAlpha => Channels == 1;
    }

    /// <summary>Reads the size and depth from the front of <paramref name="data"/>.</summary>
    public static Header ReadHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 + 25 || !data[..8].SequenceEqual(Signature))
            throw ProjectException.MissingImage("not a PNG");

        int length = ReadInt(data[8..]);
        if (length != 13 || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw ProjectException.MissingImage("no IHDR");

        int width = ReadInt(data[16..]);
        int height = ReadInt(data[20..]);
        int depth = data[24];
        var color = (ColorType)data[25];
        int interlace = data[28];

        if (width <= 0 || height <= 0) throw ProjectException.MissingImage("empty image");
        if (depth != 8) throw ProjectException.MissingImage($"{depth} bits per channel, expected 8");
        if (interlace != 0) throw ProjectException.MissingImage("interlaced");

        int channels = color switch
        {
            ColorType.Grey => 1,
            ColorType.Rgb => 3,
            ColorType.GreyAlpha => 2,
            ColorType.Rgba => 4,
            _ => throw ProjectException.MissingImage($"colour type {(int)color}"),
        };

        return new Header(width, height, depth, channels);
    }

    /// <summary>
    /// Decodes into a premultiplied RGBA buffer. Greyscale becomes opaque grey.
    /// </summary>
    public static PixelBuffer Decode(ReadOnlySpan<byte> data)
    {
        Header header = ReadHeader(data);
        byte[] raw = Inflate(data);
        byte[] rows = Unfilter(raw, header);

        PixelBuffer buffer = PixelBuffer.Allocate(header.Width, header.Height);
        int stride = header.Width * header.Channels;

        for (int y = 0; y < header.Height; y++)
        {
            ReadOnlySpan<byte> source = rows.AsSpan(y * stride, stride);
            Span<byte> target = buffer.Row(y);

            for (int x = 0; x < header.Width; x++)
            {
                byte red, green, blue, alpha;
                switch (header.Channels)
                {
                    case 1:
                        red = green = blue = source[x];
                        alpha = 255;
                        break;
                    case 2:
                        red = green = blue = source[x * 2];
                        alpha = source[x * 2 + 1];
                        break;
                    case 3:
                        red = source[x * 3];
                        green = source[x * 3 + 1];
                        blue = source[x * 3 + 2];
                        alpha = 255;
                        break;
                    default:
                        red = source[x * 4];
                        green = source[x * 4 + 1];
                        blue = source[x * 4 + 2];
                        alpha = source[x * 4 + 3];
                        break;
                }

                target[x * 4 + 0] = Premultiply(red, alpha);
                target[x * 4 + 1] = Premultiply(green, alpha);
                target[x * 4 + 2] = Premultiply(blue, alpha);
                target[x * 4 + 3] = alpha;
            }
        }

        return buffer;
    }

    /// <summary>
    /// Encodes a premultiplied RGBA buffer as an 8-bit RGBA PNG, dividing the alpha back out.
    /// </summary>
    public static byte[] Encode(PixelBuffer buffer)
    {
        int stride = buffer.Width * 4;
        byte[] rows = new byte[(stride + 1) * buffer.Height];

        for (int y = 0; y < buffer.Height; y++)
        {
            Span<byte> source = buffer.Row(y);
            Span<byte> target = rows.AsSpan(y * (stride + 1));
            target[0] = 0; // No filter: these are already compressed well by deflate.

            for (int x = 0; x < buffer.Width; x++)
            {
                byte alpha = source[x * 4 + 3];
                target[1 + x * 4 + 0] = Unpremultiply(source[x * 4 + 0], alpha);
                target[1 + x * 4 + 1] = Unpremultiply(source[x * 4 + 1], alpha);
                target[1 + x * 4 + 2] = Unpremultiply(source[x * 4 + 2], alpha);
                target[1 + x * 4 + 3] = alpha;
            }
        }

        return Assemble(buffer.Width, buffer.Height, ColorType.Rgba, rows);
    }

    /// <summary>
    /// Encodes the buffer's alpha as an 8-bit greyscale PNG with no alpha — a mask's format.
    /// </summary>
    /// <remarks>
    /// Masks are coverage, so they are stored in the one channel that means anything for them.
    /// A <see cref="PixelBuffer"/> is always RGBA, and the convention here is that a mask keeps its
    /// coverage in every channel, so reading back what was written round-trips.
    /// </remarks>
    public static byte[] EncodeMask(PixelBuffer buffer)
    {
        int stride = buffer.Width;
        byte[] rows = new byte[(stride + 1) * buffer.Height];

        for (int y = 0; y < buffer.Height; y++)
        {
            Span<byte> source = buffer.Row(y);
            Span<byte> target = rows.AsSpan(y * (stride + 1));
            target[0] = 0;
            for (int x = 0; x < buffer.Width; x++) target[1 + x] = source[x * 4];
        }

        return Assemble(buffer.Width, buffer.Height, ColorType.Grey, rows);
    }

    private static byte[] Assemble(int width, int height, ColorType color, byte[] rows)
    {
        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = (byte)color;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WriteChunk(output, "IHDR"u8, header);

        using (var compressed = new MemoryStream())
        {
            using (var deflate = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                deflate.Write(rows);
            WriteChunk(output, "IDAT"u8, compressed.ToArray());
        }

        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(tag);
        output.Write(data);

        uint crc = Crc32(tag, data);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        output.Write(checksum);
    }

    /// <summary>Concatenates every IDAT chunk and inflates them as one stream.</summary>
    private static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        using var compressed = new MemoryStream();
        int offset = 8;
        bool sawEnd = false;

        while (offset + 12 <= data.Length)
        {
            int length = ReadInt(data[offset..]);
            if (length < 0 || offset + 12 + length > data.Length)
                throw ProjectException.MissingImage("truncated chunk");

            ReadOnlySpan<byte> tag = data.Slice(offset + 4, 4);
            ReadOnlySpan<byte> body = data.Slice(offset + 8, length);

            if (tag.SequenceEqual("IDAT"u8)) compressed.Write(body);
            else if (tag.SequenceEqual("IEND"u8)) { sawEnd = true; break; }

            offset += 12 + length;
        }

        if (!sawEnd) throw ProjectException.MissingImage("no IEND");
        if (compressed.Length == 0) throw ProjectException.MissingImage("no pixel data");

        compressed.Position = 0;
        using var inflated = new MemoryStream();
        using (var stream = new ZLibStream(compressed, CompressionMode.Decompress))
            stream.CopyTo(inflated);
        return inflated.ToArray();
    }

    /// <summary>Reverses the per-row filters, leaving tightly packed rows.</summary>
    private static byte[] Unfilter(byte[] raw, Header header)
    {
        int stride = header.Width * header.Channels;
        int step = header.Channels;

        if (raw.Length < (long)(stride + 1) * header.Height)
            throw ProjectException.MissingImage("short pixel data");

        byte[] rows = new byte[stride * header.Height];

        for (int y = 0; y < header.Height; y++)
        {
            int filter = raw[y * (stride + 1)];
            ReadOnlySpan<byte> source = raw.AsSpan(y * (stride + 1) + 1, stride);
            Span<byte> target = rows.AsSpan(y * stride, stride);
            ReadOnlySpan<byte> above = y > 0 ? rows.AsSpan((y - 1) * stride, stride) : default;

            for (int i = 0; i < stride; i++)
            {
                byte left = i >= step ? target[i - step] : (byte)0;
                byte up = y > 0 ? above[i] : (byte)0;
                byte upLeft = y > 0 && i >= step ? above[i - step] : (byte)0;

                target[i] = filter switch
                {
                    0 => source[i],
                    1 => (byte)(source[i] + left),
                    2 => (byte)(source[i] + up),
                    3 => (byte)(source[i] + (left + up) / 2),
                    4 => (byte)(source[i] + Paeth(left, up, upLeft)),
                    _ => throw ProjectException.MissingImage($"filter {filter}"),
                };
            }
        }

        return rows;
    }

    private static byte Paeth(byte left, byte up, byte upLeft)
    {
        int estimate = left + up - upLeft;
        int fromLeft = Math.Abs(estimate - left);
        int fromUp = Math.Abs(estimate - up);
        int fromUpLeft = Math.Abs(estimate - upLeft);
        if (fromLeft <= fromUp && fromLeft <= fromUpLeft) return left;
        return fromUp <= fromUpLeft ? up : upLeft;
    }

    private static byte Premultiply(byte value, byte alpha) =>
        alpha == 255 ? value : (byte)((value * alpha + 127) / 255);

    private static byte Unpremultiply(byte value, byte alpha)
    {
        if (alpha == 255) return value;
        if (alpha == 0) return 0;
        return (byte)Math.Min(255, (value * 255 + alpha / 2) / alpha);
    }

    private static int ReadInt(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadInt32BigEndian(data);

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> tag, ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in tag) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
