using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// The PNG a project stores its assets as.
/// </summary>
/// <remarks>
/// Only what <c>.comp</c> contains is covered — 8 bits per channel, no interlacing. General image
/// import belongs to the backend and its WIC decoder.
/// </remarks>
public class PngTests
{
    [Fact]
    public void AnImageSurvivesEncodingAndDecoding()
    {
        PixelBuffer original = ProjectFixture.Image(9, 7);
        PixelBuffer restored = Png.Decode(Png.Encode(original));

        Assert.Equal(original.Width, restored.Width);
        Assert.Equal(original.Height, restored.Height);

        for (int y = 0; y < original.Height; y++)
        {
            Span<byte> before = original.Row(y), after = restored.Row(y);
            for (int x = 0; x < original.Width; x++)
            {
                Assert.Equal(before[x * 4 + 3], after[x * 4 + 3]);
                for (int channel = 0; channel < 3; channel++)
                    Assert.True(Math.Abs(before[x * 4 + channel] - after[x * 4 + channel]) <= 1);
            }
        }

        original.Release();
        restored.Release();
    }

    [Fact]
    public void AFullyTransparentPixelStaysTransparent()
    {
        PixelBuffer buffer = PixelBuffer.Allocate(2, 1);
        Span<byte> row = buffer.Row(0);
        row[4] = row[5] = row[6] = row[7] = 255; // The second pixel is opaque white.

        PixelBuffer restored = Png.Decode(Png.Encode(buffer));
        Span<byte> after = restored.Row(0);

        Assert.Equal(0, after[3]);
        Assert.Equal(0, after[0]);
        Assert.Equal(255, after[7]);
        Assert.Equal(255, after[4]);

        buffer.Release();
        restored.Release();
    }

    [Fact]
    public void AMaskIsStoredAsGreyscaleWithoutAlpha()
    {
        PixelBuffer mask = ProjectFixture.Mask(5, 3);
        byte[] encoded = Png.EncodeMask(mask);

        Png.Header header = Png.ReadHeader(encoded);
        Assert.True(header.IsGreyscaleWithoutAlpha);
        Assert.Equal(5, header.Width);
        Assert.Equal(3, header.Height);

        PixelBuffer restored = Png.Decode(encoded);
        for (int y = 0; y < mask.Height; y++)
        {
            Span<byte> before = mask.Row(y), after = restored.Row(y);
            for (int x = 0; x < mask.Width; x++)
            {
                // Greyscale decodes to opaque grey in every channel, which is the convention a
                // mask buffer keeps.
                Assert.Equal(before[x * 4], after[x * 4]);
                Assert.Equal(before[x * 4], after[x * 4 + 1]);
                Assert.Equal(255, after[x * 4 + 3]);
            }
        }

        mask.Release();
        restored.Release();
    }

    [Fact]
    public void AUniformOnePixelMaskIsValid()
    {
        // Upstream leaves an unpainted mask as a single pixel rather than allocating one the size
        // of the layer; docs/windows-port.md §2.4 keeps that.
        LayerMask mask = LayerMask.Solid(revealing: true);

        Assert.Equal(1, mask.Coverage.Width);
        Assert.Equal(1, mask.Coverage.Height);

        PixelBuffer restored = Png.Decode(Png.EncodeMask(mask.Coverage));
        Assert.Equal(255, restored.Row(0)[0]);

        mask.Coverage.Release();
        restored.Release();
    }

    [Theory]
    [InlineData("not a png at all, not even close to the signature")]
    public void SomethingThatIsNotAPngIsRejected(string text) =>
        Assert.Throws<ProjectException>(() => Png.ReadHeader(System.Text.Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void ATruncatedPngIsRejected()
    {
        PixelBuffer buffer = ProjectFixture.Image(4, 4);
        byte[] encoded = Png.Encode(buffer);

        Assert.Throws<ProjectException>(() => Png.Decode(encoded[..(encoded.Length / 2)]));
        buffer.Release();
    }

    [Fact]
    public void EveryRowFilterDecodes()
    {
        // The encoder writes unfiltered rows, but CGImageDestination picks per row, so the decoder
        // has to handle all five. This builds the same 4×4 image once per filter and checks they
        // all come back the same.
        PixelBuffer expected = Png.Decode(BuildFiltered(0));
        for (byte filter = 1; filter <= 4; filter++)
        {
            PixelBuffer actual = Png.Decode(BuildFiltered(filter));
            for (int y = 0; y < expected.Height; y++)
                Assert.True(expected.Row(y).SequenceEqual(actual.Row(y)), $"filter {filter}, row {y}");
            actual.Release();
        }
        expected.Release();
    }

    /// <summary>The same 4×4 opaque image, written with one filter applied to every row.</summary>
    private static byte[] BuildFiltered(byte filter)
    {
        const int Width = 4, Height = 4, Channels = 4;
        byte[] pixels = new byte[Width * Height * Channels];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Width + x) * Channels;
                pixels[i] = (byte)(x * 40 + 5);
                pixels[i + 1] = (byte)(y * 30 + 7);
                pixels[i + 2] = (byte)(x * y * 11);
                pixels[i + 3] = 255;
            }

        byte[] rows = new byte[(Width * Channels + 1) * Height];
        for (int y = 0; y < Height; y++)
        {
            int target = y * (Width * Channels + 1);
            rows[target] = filter;
            for (int i = 0; i < Width * Channels; i++)
            {
                byte value = pixels[y * Width * Channels + i];
                byte left = i >= Channels ? pixels[y * Width * Channels + i - Channels] : (byte)0;
                byte up = y > 0 ? pixels[(y - 1) * Width * Channels + i] : (byte)0;
                byte upLeft = y > 0 && i >= Channels ? pixels[(y - 1) * Width * Channels + i - Channels] : (byte)0;

                rows[target + 1 + i] = filter switch
                {
                    0 => value,
                    1 => (byte)(value - left),
                    2 => (byte)(value - up),
                    3 => (byte)(value - (left + up) / 2),
                    _ => (byte)(value - Paeth(left, up, upLeft)),
                };
            }
        }

        return PngWriter.Assemble(Width, Height, rows);

        static byte Paeth(byte left, byte up, byte upLeft)
        {
            int estimate = left + up - upLeft;
            int a = Math.Abs(estimate - left), b = Math.Abs(estimate - up), c = Math.Abs(estimate - upLeft);
            if (a <= b && a <= c) return left;
            return b <= c ? up : upLeft;
        }
    }
}

/// <summary>Writes a raw RGBA PNG with rows already filtered, which the encoder never does.</summary>
internal static class PngWriter
{
    public static byte[] Assemble(int width, int height, byte[] filteredRows)
    {
        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        byte[] header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6; // RGBA
        Chunk(output, "IHDR"u8, header);

        using var compressed = new MemoryStream();
        using (var deflate = new System.IO.Compression.ZLibStream(
                   compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(filteredRows);
        Chunk(output, "IDAT"u8, compressed.ToArray());

        Chunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> data)
    {
        byte[] length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(tag);
        output.Write(data);

        uint crc = 0xFFFFFFFFu;
        foreach (byte b in tag) crc = Step(crc, b);
        foreach (byte b in data) crc = Step(crc, b);
        byte[] checksum = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(checksum, crc ^ 0xFFFFFFFFu);
        output.Write(checksum);

        static uint Step(uint c, byte b)
        {
            c ^= b;
            for (int i = 0; i < 8; i++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }
    }
}
