namespace Compositor_korean_win.Core;

/// <summary>The colour modes a PSD header can declare.</summary>
internal enum PsdColorMode
{
    Bitmap = 0,
    Grayscale = 1,
    Indexed = 2,
    Rgb = 3,
    Cmyk = 4,
    Multichannel = 7,
    Duotone = 8,
    Lab = 9,
}

/// <summary>Where one channel's data sits in the file.</summary>
internal sealed class PsdChannel
{
    /// <summary>0… the colour channels, −1 transparency, −2 the layer mask, −3 the real mask beside a vector one.</summary>
    public required short Id { get; init; }

    /// <summary>Bytes of data including the two-byte compression code.</summary>
    public required long Length { get; init; }

    public long Offset { get; set; }
}

/// <summary>A layer mask record.</summary>
internal sealed class PsdMaskRecord
{
    public int Top, Left, Bottom, Right;
    public byte Default;
    public byte Flags;

    /// <summary>Photoshop's own mask when a vector mask sits beside it; −3's rectangle.</summary>
    public int RealTop, RealLeft, RealBottom, RealRight;
    public byte RealDefault;
    public byte RealFlags;
    public bool HasReal;

    public bool Disabled(bool real) => ((real ? RealFlags : Flags) & 0x02) != 0;

    /// <summary>Density or feather, which this port does not apply.</summary>
    public bool HasParameters => (Flags & 0x10) != 0;
}

/// <summary>One layer record: where it is, how it blends, and what extra blocks it carries.</summary>
internal sealed class PsdLayerRecord
{
    public int Top, Left, Bottom, Right;
    public List<PsdChannel> Channels { get; } = [];
    public string BlendKey = "norm";
    public byte Opacity = 255;
    public byte Clipping;
    public byte Flags;
    public PsdMaskRecord? Mask;
    public string Name = "";

    /// <summary>Additional layer information, by key: offset and length of each block's data in the file.</summary>
    public Dictionary<string, (long Offset, int Length)> Blocks { get; } = [];

    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public bool IsHidden => (Flags & 0x02) != 0;

    public PsdChannel? Channel(short id) => Channels.FirstOrDefault(channel => channel.Id == id);
}

/// <summary>
/// A PSD or PSB as its sections lay it out, with channel data located but not yet decoded.
/// </summary>
/// <remarks>
/// Reading stops at structure: each channel's pixels are decoded only when the importer asks for
/// them, so a layer it does not use costs nothing and no layer's pixels are held twice.
/// </remarks>
internal sealed class PsdFile
{
    public required byte[] Data { get; init; }
    public required int Version { get; init; }
    public required int Channels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Depth { get; init; }
    public required PsdColorMode Mode { get; init; }
    public required byte[] ColorModeData { get; init; }
    public double Resolution { get; private set; } = 72;

    /// <summary>The document's global light angle, which effects that use it take instead of their own.</summary>
    public int GlobalAngle { get; private set; } = 120;

    public List<PsdLayerRecord> Layers { get; private set; } = [];

    /// <summary>Whether the first extra channel of the composite is its transparency.</summary>
    public bool MergedAlpha { get; private set; }

    public long ImageDataOffset { get; private set; }

    public bool Wide => Version == 2;

    // Keys whose lengths a PSB widens to 64 bits (Adobe's specification, "Additional Layer Information").
    private static readonly HashSet<string> WideKeys =
        ["LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD"];

    public static bool LooksLikePsd(ReadOnlySpan<byte> start) =>
        start.Length >= 6 && start[0] == (byte)'8' && start[1] == (byte)'B' && start[2] == (byte)'P' && start[3] == (byte)'S'
        && start[4] == 0 && start[5] is 1 or 2;

    public static PsdFile Read(byte[] data)
    {
        var reader = new PsdReader(data);
        if (reader.Key() != "8BPS") throw PsdFormat.Invalid("not a PSD");
        int version = reader.U16();
        if (version is not (1 or 2)) throw PsdFormat.Invalid($"version {version}");
        reader.Skip(6);
        int channels = reader.U16();
        int height = (int)Math.Min(reader.U32(), int.MaxValue);
        int width = (int)Math.Min(reader.U32(), int.MaxValue);
        int depth = reader.U16();
        var mode = (PsdColorMode)reader.U16();

        if (channels is < 1 or > 56) throw PsdFormat.Invalid($"{channels} channels");
        if (depth is not (1 or 8 or 16 or 32)) throw PsdFormat.Invalid($"{depth} bits a channel");
        if (width < 1 || height < 1) throw PsdFormat.Invalid("an empty canvas");
        if (width > ProjectLimits.MaximumSide || height > ProjectLimits.MaximumSide
            || (long)width * height > ProjectLimits.MaximumPixels)
            throw PsdFormat.TooLarge();

        byte[] colorModeData = reader.Bytes(reader.Length(wide: false)).ToArray();

        var file = new PsdFile
        {
            Data = data,
            Version = version,
            Channels = channels,
            Width = width,
            Height = height,
            Depth = depth,
            Mode = mode,
            ColorModeData = colorModeData,
        };

        file.ReadResources(reader.Section(reader.Length(wide: false)));
        file.ReadLayersAndMasks(reader.Section(reader.Length(file.Wide)));
        file.ImageDataOffset = reader.Position;
        return file;
    }

    private void ReadResources(PsdReader section)
    {
        while (section.Remaining >= 12)
        {
            section.Key(); // "8BIM", or another application's signature; the layout is the same.
            int id = section.U16();
            section.Pascal(2);
            int size = (int)Math.Min(section.U32(), (uint)section.Remaining);
            PsdReader block = section.Section(size);
            section.Skip(Math.Min(size % 2, section.Remaining));

            switch (id)
            {
                case 0x03ED when block.Remaining >= 16:
                {
                    // ResolutionInfo: horizontal resolution as 16.16 fixed point, then its unit.
                    double resolution = block.I32() / 65536.0;
                    int unit = block.U16();
                    if (unit == 2) resolution *= 2.54; // pixels per centimetre
                    if (double.IsFinite(resolution) && resolution is >= 1 and <= 9600) Resolution = Math.Round(resolution, 3);
                    break;
                }
                case 0x040D when block.Remaining >= 4:
                    GlobalAngle = block.I32();
                    break;
            }
        }
    }

    private void ReadLayersAndMasks(PsdReader section)
    {
        if (section.Remaining == 0) return;

        int layerInfoLength = section.Length(Wide);
        if (layerInfoLength > 0) ReadLayerInfo(section.Section(layerInfoLength));

        if (section.Remaining >= 4) section.Skip(Math.Min(section.U32(), (uint)section.Remaining));

        // Depths above 8 keep their layers in a document-level block instead.
        foreach ((string key, PsdReader block) in TaggedBlocks(section))
        {
            if (key is "Lr16" or "Lr32" or "Layr" && Layers.Count == 0) ReadLayerInfo(block);
        }
    }

    private IEnumerable<(string Key, PsdReader Block)> TaggedBlocks(PsdReader section)
    {
        while (FindSignature(section))
        {
            section.Key();
            string key = section.Key();
            int length = section.Length(Wide && WideKeys.Contains(key));
            yield return (key, section.Section(length));
        }
    }

    /// <summary>
    /// Moves to the next block signature, stepping over up to three bytes of padding: some writers
    /// pad blocks to an even length or a multiple of four and some do not, and the length does not
    /// say which.
    /// </summary>
    private static bool FindSignature(PsdReader section)
    {
        for (int skipped = 0; section.Remaining >= 12; skipped++)
        {
            if (section.PeekKey() is "8BIM" or "8B64") return true;
            if (skipped == 3) return false;
            section.Skip(1);
        }
        return false;
    }

    private void ReadLayerInfo(PsdReader info)
    {
        if (info.Remaining < 2) return;
        int count = info.I16();
        MergedAlpha = count < 0;
        count = Math.Abs(count);
        if (count > ProjectLimits.MaximumLayers * 2) throw PsdFormat.Invalid($"{count} layers");

        var layers = new List<PsdLayerRecord>(count);
        for (int i = 0; i < count; i++) layers.Add(ReadRecord(info));

        // Channel data follows every record, in the same order.
        foreach (PsdLayerRecord layer in layers)
        {
            foreach (PsdChannel channel in layer.Channels)
            {
                channel.Offset = info.Position;
                info.Skip(Math.Min(channel.Length, info.Remaining));
            }
        }

        Layers = layers;
    }

    private PsdLayerRecord ReadRecord(PsdReader info)
    {
        var layer = new PsdLayerRecord
        {
            Top = info.I32(),
            Left = info.I32(),
            Bottom = info.I32(),
            Right = info.I32(),
        };

        int channels = info.U16();
        if (channels > 56) throw PsdFormat.Invalid($"a layer with {channels} channels");
        for (int c = 0; c < channels; c++)
        {
            short id = info.I16();
            long length = Wide ? info.I64() : info.U32();
            layer.Channels.Add(new PsdChannel { Id = id, Length = length });
        }

        if (info.Key() != "8BIM") throw PsdFormat.Invalid("a layer record without its blend signature");
        layer.BlendKey = info.Key();
        layer.Opacity = info.U8();
        layer.Clipping = info.U8();
        layer.Flags = info.U8();
        info.U8();

        PsdReader extra = info.Section((int)Math.Min(info.U32(), (uint)info.Remaining));

        int maskLength = (int)Math.Min(extra.U32(), (uint)extra.Remaining);
        if (maskLength > 0) layer.Mask = ReadMask(extra.Section(maskLength), layer.Channel(-3) is not null);

        extra.Skip(Math.Min(extra.U32(), (uint)extra.Remaining)); // blending ranges
        layer.Name = extra.Pascal(4);

        while (FindSignature(extra))
        {
            extra.Key();
            string key = extra.Key();
            int length = extra.Length(Wide && WideKeys.Contains(key));
            PsdReader block = extra.Section(length);
            layer.Blocks.TryAdd(key, (block.Position, length));
        }

        return layer;
    }

    private static PsdMaskRecord ReadMask(PsdReader data, bool hasReal)
    {
        var mask = new PsdMaskRecord
        {
            Top = data.I32(),
            Left = data.I32(),
            Bottom = data.I32(),
            Right = data.I32(),
            Default = data.U8(),
            Flags = data.U8(),
        };

        // Photoshop writes the real mask's fields before any parameters, whatever the specification's
        // order says; psd-tools reads them the same way.
        if (hasReal && data.Remaining >= 18)
        {
            mask.RealFlags = data.U8();
            mask.RealDefault = data.U8();
            mask.RealTop = data.I32();
            mask.RealLeft = data.I32();
            mask.RealBottom = data.I32();
            mask.RealRight = data.I32();
            mask.HasReal = true;
        }

        return mask;
    }

    /// <summary>A block's data as a reader, or null when the layer has no such block.</summary>
    public PsdReader? Block(PsdLayerRecord layer, string key) =>
        layer.Blocks.TryGetValue(key, out var at) ? PsdReader.At(Data, at.Offset, at.Length) : null;

    /// <summary>
    /// One channel as 8-bit values, <paramref name="width"/> × <paramref name="height"/>, or null
    /// when the layer has no such channel.
    /// </summary>
    /// <param name="linear">
    /// At 32 bits, whether the values are linear light to be encoded for display (colour) or plain
    /// coverage (transparency and masks).
    /// </param>
    public byte[]? Plane(PsdChannel? channel, int width, int height, bool linear)
    {
        if (channel is null || width <= 0 || height <= 0) return null;
        if (channel.Length < 2) return new byte[width * height];

        PsdReader reader = PsdReader.At(Data, channel.Offset, Math.Min(channel.Length, Data.Length - channel.Offset));
        ushort compression = reader.U16();
        int rowBytes = RowBytes(width);
        byte[] raw = PsdCompression.Decode(compression, reader.Bytes(reader.Remaining), rowBytes, height, Depth, Wide);
        return ToEightBit(raw, width, height, linear);
    }

    /// <summary>The flattened image Photoshop saved beside the layers, one 8-bit plane per channel.</summary>
    public byte[][] Composite()
    {
        PsdReader reader = PsdReader.At(Data, ImageDataOffset, Data.Length - ImageDataOffset);
        ushort compression = reader.U16();
        int rowBytes = RowBytes(Width);
        var planes = new byte[Channels][];

        switch (compression)
        {
            case PsdCompression.Rle:
            {
                int countBytes = Wide ? 4 : 2;
                PsdReader counts = reader.Section(Channels * Height * countBytes);
                for (int c = 0; c < Channels; c++)
                {
                    var raw = new byte[(long)rowBytes * Height];
                    for (int y = 0; y < Height; y++)
                    {
                        int length = Wide ? (int)Math.Min(counts.U32(), int.MaxValue) : counts.U16();
                        length = Math.Min(length, reader.Remaining);
                        PsdCompression.UnpackRow(reader.Bytes(length), raw.AsSpan(y * rowBytes, rowBytes));
                    }
                    planes[c] = ToEightBit(raw, Width, Height, linear: c < ColourChannels);
                }
                break;
            }

            case PsdCompression.Raw:
                for (int c = 0; c < Channels; c++)
                {
                    int size = (int)Math.Min((long)rowBytes * Height, reader.Remaining);
                    var raw = new byte[(long)rowBytes * Height];
                    reader.Bytes(size).CopyTo(raw);
                    planes[c] = ToEightBit(raw, Width, Height, linear: c < ColourChannels);
                }
                break;

            default:
            {
                byte[] all = PsdCompression.Decode(compression, reader.Bytes(reader.Remaining), rowBytes, Height * Channels, Depth, Wide);
                for (int c = 0; c < Channels; c++)
                {
                    byte[] raw = all.AsSpan(c * rowBytes * Height, rowBytes * Height).ToArray();
                    planes[c] = ToEightBit(raw, Width, Height, linear: c < ColourChannels);
                }
                break;
            }
        }

        return planes;
    }

    /// <summary>How many channels carry colour in this mode; any after them are alpha.</summary>
    public int ColourChannels => Mode switch
    {
        PsdColorMode.Rgb or PsdColorMode.Lab => 3,
        PsdColorMode.Cmyk => 4,
        _ => 1,
    };

    private int RowBytes(int width) => (int)(((long)width * Depth + 7) / 8);

    private byte[] ToEightBit(byte[] raw, int width, int height, bool linear)
    {
        var plane = new byte[(long)width * height];
        switch (Depth)
        {
            case 8:
                Array.Copy(raw, plane, Math.Min(raw.Length, plane.Length));
                break;

            case 16:
                for (int i = 0; i < plane.Length; i++)
                {
                    int value = raw[i * 2] << 8 | raw[i * 2 + 1];
                    plane[i] = (byte)((value * 255 + 32767) / 65535);
                }
                break;

            case 32:
                for (int i = 0; i < plane.Length; i++)
                {
                    float value = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(raw.AsSpan(i * 4, 4));
                    double v = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0;
                    if (linear && Mode != PsdColorMode.Lab) v = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
                    plane[i] = (byte)Math.Round(v * 255);
                }
                break;

            case 1:
            {
                int rowBytes = (width + 7) / 8;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        bool inked = (raw[y * rowBytes + x / 8] & (0x80 >> (x % 8))) != 0;
                        plane[(long)y * width + x] = inked ? (byte)0 : (byte)255;
                    }
                break;
            }
        }
        return plane;
    }
}
