using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Compositor_korean_win.Core;

/// <summary>Big-endian reads over a PSD held in memory, bounded to one section of it.</summary>
/// <remarks>
/// The whole file is read into one array rather than streamed. Channel data is decoded on demand,
/// long after the records that locate it were parsed, and a PSD cannot exceed 2 GiB anyway — the
/// format's own lengths are 32-bit — so an array holds any file this port can open (PSB, the
/// 64-bit variant, is read too, but only up to the same size).
/// </remarks>
internal sealed class PsdReader
{
    private readonly byte[] _data;
    private readonly int _end;
    private int _position;

    public PsdReader(byte[] data) : this(data, 0, data.Length) { }

    private PsdReader(byte[] data, int start, int end)
    {
        _data = data;
        _position = start;
        _end = end;
    }

    /// <summary>A reader over <paramref name="length"/> bytes starting at <paramref name="offset"/>.</summary>
    public static PsdReader At(byte[] data, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset + length > data.Length) throw PsdFormat.Truncated();
        return new PsdReader(data, (int)offset, (int)(offset + length));
    }

    public byte[] Data => _data;
    public int Position => _position;
    public int Remaining => _end - _position;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining) throw PsdFormat.Truncated();
        var span = new ReadOnlySpan<byte>(_data, _position, count);
        _position += count;
        return span;
    }

    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    public short I16() => BinaryPrimitives.ReadInt16BigEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
    public long I64() => BinaryPrimitives.ReadInt64BigEndian(Take(8));
    public float F32() => BinaryPrimitives.ReadSingleBigEndian(Take(4));
    public double F64() => BinaryPrimitives.ReadDoubleBigEndian(Take(8));
    public ReadOnlySpan<byte> Bytes(int count) => Take(count);

    /// <summary>A four-character code, such as a signature or a block key.</summary>
    public string Key() => Encoding.Latin1.GetString(Take(4));

    /// <summary>The next four bytes as a key, without consuming them; null near the end.</summary>
    public string? PeekKey() => Remaining >= 4 ? Encoding.Latin1.GetString(_data, _position, 4) : null;

    public void Skip(long count)
    {
        if (count < 0 || count > Remaining) throw PsdFormat.Truncated();
        _position += (int)count;
    }

    /// <summary>A section length: 32-bit in a PSD, 64-bit in a PSB where the format widens it.</summary>
    public int Length(bool wide)
    {
        long value = wide ? I64() : U32();
        if (value < 0 || value > Remaining) throw PsdFormat.Truncated();
        return (int)value;
    }

    /// <summary>The next <paramref name="length"/> bytes as a reader of their own; this one moves past them.</summary>
    public PsdReader Section(int length)
    {
        if (length < 0 || length > Remaining) throw PsdFormat.Truncated();
        var section = new PsdReader(_data, _position, _position + length);
        _position += length;
        return section;
    }

    /// <summary>A length-prefixed UTF-16 string, without the terminating null Photoshop often counts.</summary>
    public string Unicode()
    {
        uint count = U32();
        if (count > (uint)(Remaining / 2)) throw PsdFormat.Truncated();
        return Encoding.BigEndianUnicode.GetString(Take((int)count * 2)).TrimEnd('\0');
    }

    /// <summary>A Pascal string padded so that it and its length byte fill a multiple of <paramref name="alignment"/>.</summary>
    public string Pascal(int alignment)
    {
        int length = U8();
        string text = Encoding.Latin1.GetString(Take(length));
        int used = 1 + length;
        Skip((alignment - used % alignment) % alignment);
        return text;
    }
}

/// <summary>Big-endian writes to a seekable stream, with lengths patched in once known.</summary>
internal sealed class PsdWriter
{
    private readonly Stream _stream;
    private readonly byte[] _scratch = new byte[8];

    public PsdWriter(Stream stream)
    {
        if (!stream.CanSeek) throw new ArgumentException("a PSD is written with lengths patched in afterwards, which needs a seekable stream", nameof(stream));
        _stream = stream;
    }

    public long Position => _stream.Position;

    public void U8(byte value) => _stream.WriteByte(value);

    public void U16(ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_scratch, value);
        _stream.Write(_scratch, 0, 2);
    }

    public void I16(short value)
    {
        BinaryPrimitives.WriteInt16BigEndian(_scratch, value);
        _stream.Write(_scratch, 0, 2);
    }

    public void U32(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_scratch, value);
        _stream.Write(_scratch, 0, 4);
    }

    public void I32(int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(_scratch, value);
        _stream.Write(_scratch, 0, 4);
    }

    public void F32(float value)
    {
        BinaryPrimitives.WriteSingleBigEndian(_scratch, value);
        _stream.Write(_scratch, 0, 4);
    }

    public void F64(double value)
    {
        BinaryPrimitives.WriteDoubleBigEndian(_scratch, value);
        _stream.Write(_scratch, 0, 8);
    }

    public void Bytes(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

    /// <summary>A four-character code.</summary>
    public void Key(string key)
    {
        if (key.Length != 4) throw new ArgumentException($"'{key}' is not a four-character code", nameof(key));
        foreach (char c in key) _stream.WriteByte((byte)c);
    }

    public void Zeros(int count)
    {
        for (int i = 0; i < count; i++) _stream.WriteByte(0);
    }

    /// <summary>Reserves a 32-bit length to be filled in by <see cref="EndLength"/>.</summary>
    public long BeginLength()
    {
        long at = Position;
        U32(0);
        return at;
    }

    /// <summary>Pads what follows the length to a multiple of <paramref name="alignment"/> and writes the length.</summary>
    public void EndLength(long at, int alignment = 1)
    {
        long length = Position - at - 4;
        int padding = (int)((alignment - length % alignment) % alignment);
        Zeros(padding);
        length += padding;

        long end = Position;
        _stream.Position = at;
        U32(checked((uint)length));
        _stream.Position = end;
    }

    /// <summary>A length-prefixed UTF-16 string.</summary>
    /// <param name="terminated">
    /// Counts and writes a trailing null, as Photoshop does inside descriptors; a layer's Unicode
    /// name is written without one, as Photoshop writes it.
    /// </param>
    public void Unicode(string text, bool terminated)
    {
        U32((uint)(text.Length + (terminated ? 1 : 0)));
        foreach (char c in text) U16(c);
        if (terminated) U16(0);
    }

    /// <summary>A Pascal string of at most 255 Latin-1 characters, padded to <paramref name="alignment"/>.</summary>
    public void Pascal(string text, int alignment)
    {
        var bytes = new List<byte>(Math.Min(text.Length, 255));
        foreach (char c in text)
        {
            if (bytes.Count == 255) break;
            bytes.Add(c is >= ' ' and <= '~' ? (byte)c : (byte)'?');
        }
        U8((byte)bytes.Count);
        Bytes(bytes.ToArray());
        int used = 1 + bytes.Count;
        Zeros((alignment - used % alignment) % alignment);
    }
}

/// <summary>The compression schemes channel data is stored in.</summary>
internal static class PsdCompression
{
    public const ushort Raw = 0;
    public const ushort Rle = 1;
    public const ushort Zip = 2;
    public const ushort ZipPredicted = 3;

    /// <summary>Unpacks one PackBits row into <paramref name="row"/>; returns false if it ran short.</summary>
    /// <remarks>A short or overlong row fills what it can and leaves the rest as it was, rather than failing the file.</remarks>
    public static bool UnpackRow(ReadOnlySpan<byte> packed, Span<byte> row)
    {
        int input = 0, output = 0;
        while (input < packed.Length && output < row.Length)
        {
            int header = (sbyte)packed[input++];
            if (header >= 0)
            {
                int count = Math.Min(header + 1, Math.Min(row.Length - output, packed.Length - input));
                packed.Slice(input, count).CopyTo(row[output..]);
                input += header + 1;
                output += count;
            }
            else if (header != -128)
            {
                if (input >= packed.Length) break;
                byte value = packed[input++];
                int count = Math.Min(1 - header, row.Length - output);
                row.Slice(output, count).Fill(value);
                output += count;
            }
        }
        return output == row.Length;
    }

    /// <summary>Packs one row, appending it to <paramref name="output"/>; returns the packed length.</summary>
    public static int PackRow(ReadOnlySpan<byte> row, List<byte> output)
    {
        int start = output.Count;
        int i = 0;
        while (i < row.Length)
        {
            // A run of three or more repeats is worth a repeat header.
            int run = 1;
            while (i + run < row.Length && run < 128 && row[i + run] == row[i]) run++;
            if (run >= 3)
            {
                output.Add((byte)(sbyte)(1 - run));
                output.Add(row[i]);
                i += run;
                continue;
            }

            // Otherwise copy literally up to the next run of three.
            int literal = 0;
            while (i + literal < row.Length && literal < 128)
            {
                int ahead = i + literal;
                if (ahead + 2 < row.Length && row[ahead] == row[ahead + 1] && row[ahead] == row[ahead + 2]) break;
                literal++;
            }
            output.Add((byte)(literal - 1));
            for (int k = 0; k < literal; k++) output.Add(row[i + k]);
            i += literal;
        }
        return output.Count - start;
    }

    /// <summary>
    /// Channel data decompressed to raw rows: <paramref name="rowBytes"/> × <paramref name="height"/>
    /// bytes, big-endian at depths above 8.
    /// </summary>
    public static byte[] Decode(ushort compression, ReadOnlySpan<byte> data, int rowBytes, int height, int depth, bool wide)
    {
        long size = (long)rowBytes * height;
        if (size > int.MaxValue) throw PsdFormat.TooLarge();
        var raw = new byte[size];

        switch (compression)
        {
            case Raw:
                data[..(int)Math.Min(data.Length, size)].CopyTo(raw);
                break;

            case Rle:
            {
                int countBytes = wide ? 4 : 2;
                if ((long)height * countBytes > data.Length) throw PsdFormat.Truncated();
                int offset = height * countBytes;
                for (int y = 0; y < height; y++)
                {
                    int length = wide
                        ? (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[(y * 4)..]), int.MaxValue)
                        : BinaryPrimitives.ReadUInt16BigEndian(data[(y * 2)..]);
                    if (offset + length > data.Length) length = Math.Max(0, data.Length - offset);
                    UnpackRow(data.Slice(offset, length), raw.AsSpan(y * rowBytes, rowBytes));
                    offset += length;
                }
                break;
            }

            case Zip:
            case ZipPredicted:
                Inflate(data, raw);
                if (compression == ZipPredicted) Unpredict(raw, rowBytes, height, depth);
                break;

            default:
                throw PsdFormat.Invalid($"compression {compression}");
        }

        return raw;
    }

    private static void Inflate(ReadOnlySpan<byte> data, byte[] raw)
    {
        using var input = new MemoryStream(data.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        int filled = 0;
        while (filled < raw.Length)
        {
            int read = zlib.Read(raw, filled, raw.Length - filled);
            if (read == 0) break;
            filled += read;
        }
    }

    /// <summary>Undoes Photoshop's horizontal prediction.</summary>
    /// <remarks>
    /// Each row is a running difference: of bytes at depth 8, of big-endian words at 16, and at 32
    /// of bytes after each row's four-byte floats were split into four planes — so the planes are
    /// summed first and then woven back into floats.
    /// </remarks>
    private static void Unpredict(byte[] raw, int rowBytes, int height, int depth)
    {
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = raw.AsSpan(y * rowBytes, rowBytes);
            switch (depth)
            {
                case 16:
                    for (int x = 2; x + 1 < row.Length; x += 2)
                    {
                        int value = (BinaryPrimitives.ReadUInt16BigEndian(row[x..]) + BinaryPrimitives.ReadUInt16BigEndian(row[(x - 2)..])) & 0xFFFF;
                        BinaryPrimitives.WriteUInt16BigEndian(row[x..], (ushort)value);
                    }
                    break;

                case 32:
                {
                    for (int x = 1; x < row.Length; x++) row[x] = (byte)(row[x] + row[x - 1]);
                    int width = rowBytes / 4;
                    byte[] planes = row.ToArray();
                    for (int x = 0; x < width; x++)
                        for (int b = 0; b < 4; b++)
                            row[x * 4 + b] = planes[b * width + x];
                    break;
                }

                default:
                    for (int x = 1; x < row.Length; x++) row[x] = (byte)(row[x] + row[x - 1]);
                    break;
            }
        }
    }
}

/// <summary>The errors a PSD raises, as the project format's own exception so the shell reports them alike.</summary>
internal static class PsdFormat
{
    public static ProjectException Truncated() => ProjectException.Invalid("the PSD ends before its data does");

    public static ProjectException Invalid(string detail) => ProjectException.Invalid("PSD: " + detail);

    public static ProjectException TooLarge() =>
        ProjectException.TooLarge($"the PSD is larger than {ProjectLimits.MaximumSide} pixels a side or {ProjectLimits.MaximumPixels} pixels in all");
}
