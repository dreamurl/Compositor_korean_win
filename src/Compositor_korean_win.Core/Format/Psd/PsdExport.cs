namespace Compositor_korean_win.Core;

/// <summary>
/// Writes a document as a layered Photoshop document (<c>.psd</c>).
/// </summary>
/// <remarks>
/// <para>
/// Layers here are placed — scaled, rotated, flipped — without their pixels being redrawn, and a
/// PSD layer is pixels on the document's grid. So each layer is drawn once onto that grid, over
/// just the rectangle it reaches, and written as those pixels. Everything Photoshop can hold as
/// itself is written as itself rather than baked: groups with their masks, layer masks, clipping,
/// opacity, visibility, blend modes, the five adjustment layers both editors share, and drop
/// shadow, outer glow and stroke as editable layer effects.
/// </para>
/// <para>
/// The flattened image is written too, as Photoshop does with Maximize Compatibility on, so any
/// program that reads only that — a file browser's preview, most viewers — shows the right
/// picture.
/// </para>
/// </remarks>
public static class PsdExport
{
    /// <summary>Writes <paramref name="document"/> to <paramref name="stream"/>; returns what could not be carried exactly.</summary>
    public static IReadOnlyDictionary<PsdNote, int> Write(CanvasDocument document, Stream stream)
    {
        var exporter = new Exporter(document);
        exporter.Build();
        var buffered = new BufferedStream(stream, 1 << 16);
        exporter.WriteTo(new PsdWriter(buffered));
        buffered.Flush();
        return exporter.Notes;
    }

    private sealed class MaskOut
    {
        public int Top, Left, Bottom, Right;
        public byte Default;
        public bool Disabled;
        public byte[]? Plane;
    }

    private sealed class Record
    {
        public string Name = "";
        public int Top, Left, Bottom, Right;
        public readonly List<(short Id, byte[] Data)> Channels = [];
        public string Blend = "norm";
        public byte Opacity = 255;
        public byte Clipping;
        public byte Flags = 0x08;
        public MaskOut? Mask;
        public readonly List<PsdBlockWriter> Blocks = [];
    }

    private sealed class Exporter(CanvasDocument document)
    {
        private readonly List<Record> _records = [];
        private readonly Dictionary<PsdNote, int> _notes = [];
        private readonly Dictionary<Guid, ImageLayer> _byId = document.Layers.ToDictionary(layer => layer.Id);

        public IReadOnlyDictionary<PsdNote, int> Notes => _notes;

        private void Note(PsdNote note) => _notes[note] = _notes.GetValueOrDefault(note) + 1;

        public void Build()
        {
            var children = new Dictionary<Guid, List<ImageLayer>>();
            var roots = new List<ImageLayer>();
            foreach (ImageLayer layer in document.Layers)
            {
                if (layer.ParentId is Guid parent && _byId.ContainsKey(parent))
                {
                    if (!children.TryGetValue(parent, out List<ImageLayer>? siblings)) children[parent] = siblings = [];
                    siblings.Add(layer);
                }
                else
                {
                    roots.Add(layer);
                }
            }

            Visit(roots, children);
        }

        private void Visit(List<ImageLayer> siblings, Dictionary<Guid, List<ImageLayer>> children)
        {
            for (int i = 0; i < siblings.Count; i++)
            {
                ImageLayer layer = siblings[i];
                bool clipped = layer.MaskSourceId is Guid source && ClipsDirectly(siblings, i, source);

                if (layer.IsGroup)
                {
                    _records.Add(Divider());
                    Visit(children.GetValueOrDefault(layer.Id) ?? [], children);
                    _records.Add(Group(layer));
                }
                else if (layer.Adjustment is LayerAdjustment adjustment)
                {
                    if (Adjustment(layer, adjustment, clipped) is Record record) _records.Add(record);
                }
                else
                {
                    _records.Add(Pixels(layer, clipped));
                }
            }
        }

        /// <summary>
        /// Whether a layer clips to the sibling just below it — with only other layers clipped to
        /// the same base in between — which is the one arrangement Photoshop's clipping flag says.
        /// </summary>
        private static bool ClipsDirectly(List<ImageLayer> siblings, int index, Guid source)
        {
            int below = index - 1;
            while (below >= 0 && siblings[below].MaskSourceId == source) below--;
            return below >= 0 && siblings[below].Id == source && !siblings[below].IsGroup;
        }

        private static Record Divider()
        {
            var record = new Record { Name = "</Layer group>", Flags = 0x18 };
            Empty(record);
            record.Blocks.Add(new PsdBlockWriter("lsct", writer => writer.U32(3)));
            return record;
        }

        private Record Group(ImageLayer layer)
        {
            var record = new Record { Name = layer.Name, Flags = Flags(layer, 0x18) };
            Empty(record);
            AttachMask(record, layer);
            record.Blocks.Add(Unicode(layer.Name));
            record.Blocks.Add(new PsdBlockWriter("lsct", writer =>
            {
                writer.U32(1);
                writer.Key("8BIM");
                writer.Key("pass");
            }));
            return record;
        }

        private Record? Adjustment(ImageLayer layer, LayerAdjustment adjustment, bool clipped)
        {
            if (PsdAdjustments.Writer(adjustment, Note) is not PsdBlockWriter block) return null;
            if (layer.MaskSourceId is not null && !clipped) Note(PsdNote.ClippingDropped);

            var record = new Record
            {
                Name = layer.Name,
                Blend = PsdBlend.ToKey(layer.BlendMode),
                Opacity = Byte(layer.Opacity),
                Clipping = clipped ? (byte)1 : (byte)0,
                Flags = Flags(layer, 0x08),
            };
            Empty(record);
            AttachMask(record, layer);
            record.Blocks.Add(Unicode(layer.Name));
            record.Blocks.Add(block);
            return record;
        }

        private Record Pixels(ImageLayer layer, bool clipped)
        {
            var record = new Record
            {
                Name = layer.Name,
                Blend = PsdBlend.ToKey(layer.BlendMode),
                Opacity = Byte(layer.Opacity),
                Clipping = clipped ? (byte)1 : (byte)0,
                Flags = Flags(layer, 0x08),
            };

            // A clip Photoshop cannot say is applied to the pixels instead: the base goes into the
            // drawing hidden, where it still clips, as it does on screen.
            List<ImageLayer> sources = [];
            if (layer.MaskSourceId is Guid source && !clipped)
            {
                Note(PsdNote.ClippingBaked);
                var seen = new HashSet<Guid>();
                Guid? next = source;
                while (next is Guid id && seen.Add(id) && _byId.TryGetValue(id, out ImageLayer? link) && sources.Count < ProjectLimits.MaximumMaskChain)
                {
                    sources.Add(link);
                    next = link.MaskSourceId;
                }
            }

            if (layer.Image is not null
                && Drawn(layer with { Mask = null, Effects = null, MaskSourceId = sources.Count > 0 ? layer.MaskSourceId : null },
                         sources, layer.Transform) is { } drawnLayer)
            {
                (PixelRect box, PixelBuffer pixels) = drawnLayer;
                using (pixels)
                {
                    record.Left = box.X;
                    record.Top = box.Y;
                    record.Right = box.X + box.Width;
                    record.Bottom = box.Y + box.Height;
                    (byte[] r, byte[] g, byte[] b, byte[] a) = Straight(pixels, matte: false);
                    record.Channels.Add((-1, Encode(a, box.Width, box.Height)));
                    record.Channels.Add((0, Encode(r, box.Width, box.Height)));
                    record.Channels.Add((1, Encode(g, box.Width, box.Height)));
                    record.Channels.Add((2, Encode(b, box.Width, box.Height)));
                }
            }
            else
            {
                Empty(record);
            }

            AttachMask(record, layer);
            record.Blocks.Add(Unicode(layer.Name));
            if (PsdType.Writer(layer) is PsdBlockWriter type) record.Blocks.Add(type);
            if (PsdEffects.Writer(layer.Effects) is Action<PsdWriter> effects) record.Blocks.Add(new PsdBlockWriter("lfx2", effects));
            return record;
        }

        private static byte Flags(ImageLayer layer, byte flags) => layer.IsVisible ? flags : (byte)(flags | 0x02);

        private static byte Byte(double opacity) => (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);

        /// <summary>No pixels: the four channels a layer record always lists, each empty.</summary>
        private static void Empty(Record record)
        {
            foreach (short id in new short[] { -1, 0, 1, 2 }) record.Channels.Add((id, new byte[2]));
        }

        private static PsdBlockWriter Unicode(string name) => new("luni", writer => writer.Unicode(name, terminated: false));

        private void AttachMask(Record record, ImageLayer layer)
        {
            if (layer.Mask is not LayerMask mask) return;
            MaskOut output = Mask(mask, layer.Transform);
            record.Mask = output;
            int width = output.Right - output.Left, height = output.Bottom - output.Top;
            record.Channels.Add((-2, output.Plane is byte[] plane ? Encode(plane, width, height) : new byte[2]));
        }

        /// <summary>
        /// A mask on the document's grid, with what it shows beyond its own pixels as Photoshop's
        /// default level: the edge level for a mask placed apart, as this editor draws it.
        /// </summary>
        private MaskOut Mask(LayerMask mask, LayerTransform layer)
        {
            byte level = mask.Beyond();
            var output = new MaskOut { Default = level, Disabled = !mask.IsEnabled };

            // A uniform mask needs no pixels: an empty rectangle and its level say it all.
            if (mask.Coverage.Width == 1 && mask.Coverage.Height == 1)
            {
                output.Default = mask.Coverage.Row(0)[0];
                return output;
            }

            var carrier = new ImageLayer
            {
                Id = Guid.NewGuid(),
                Name = "mask",
                Transform = mask.Placement ?? layer,
                Image = mask.Coverage,
            };
            if (Drawn(carrier, [], carrier.Transform) is not { } drawnMask) return output;

            (PixelRect box, PixelBuffer drawn) = drawnMask;
            using (drawn)
            {
                var plane = new byte[(long)box.Width * box.Height];
                for (int y = 0; y < box.Height; y++)
                {
                    ReadOnlySpan<byte> row = drawn.Row(y);
                    for (int x = 0; x < box.Width; x++)
                    {
                        // Premultiplied grey over nothing; where the mask's own pixels thin out at
                        // its edge, its level beyond fills the rest.
                        int grey = row[x * 4], alpha = row[x * 4 + 3];
                        plane[(long)y * box.Width + x] = (byte)Math.Min(255, grey + (255 - alpha) * level / 255);
                    }
                }
                output.Left = box.X;
                output.Top = box.Y;
                output.Right = box.X + box.Width;
                output.Bottom = box.Y + box.Height;
                output.Plane = plane;
            }
            return output;
        }

        /// <summary>
        /// One layer drawn alone onto the document's grid over the rectangle its placement reaches,
        /// with any clipping sources drawn hidden beside it; null if that rectangle is empty.
        /// </summary>
        private (PixelRect, PixelBuffer)? Drawn(ImageLayer layer, List<ImageLayer> sources, LayerTransform placement)
        {
            Point[] corners =
            [
                placement.PointAt(new Point(0, 0)), placement.PointAt(new Point(1, 0)),
                placement.PointAt(new Point(0, 1)), placement.PointAt(new Point(1, 1)),
            ];
            int left = (int)Math.Floor(corners.Min(point => point.X)), right = (int)Math.Ceiling(corners.Max(point => point.X));
            int top = (int)Math.Floor(corners.Min(point => point.Y)), bottom = (int)Math.Ceiling(corners.Max(point => point.Y));

            // Past the size a document may have, keep only what is over the canvas.
            if (right - left > ProjectLimits.MaximumSide || bottom - top > ProjectLimits.MaximumSide
                || (long)(right - left) * (bottom - top) > ProjectLimits.MaximumPixels)
            {
                left = Math.Max(left, 0);
                top = Math.Max(top, 0);
                right = Math.Min(right, document.Width);
                bottom = Math.Min(bottom, document.Height);
            }
            if (right <= left || bottom <= top) return null;

            var shift = new Point(-left, -top);
            var layers = new List<ImageLayer>();
            foreach (ImageLayer source in sources)
            {
                layers.Add(Shifted(source, shift) with
                {
                    ParentId = null,
                    IsVisible = false,
                    IsGroup = false,
                    Effects = null,
                });
            }
            layers.Add(Shifted(layer, shift) with
            {
                ParentId = null,
                IsVisible = true,
                IsGroup = false,
                Opacity = 1,
                BlendMode = LayerBlendMode.Normal,
            });

            var alone = new CanvasDocument
            {
                Id = Guid.NewGuid(),
                Width = right - left,
                Height = bottom - top,
                Layers = new EquatableList<ImageLayer>(layers),
            };

            using var backend = new SoftwareRenderBackend();
            return (new PixelRect(left, top, right - left, bottom - top), LayerCompositor.Render(alone, backend));
        }

        private static ImageLayer Shifted(ImageLayer layer, Point shift)
        {
            LayerTransform Move(LayerTransform transform) =>
                transform with { Origin = new Point(transform.Origin.X + shift.X, transform.Origin.Y + shift.Y) };

            return layer with
            {
                Transform = Move(layer.Transform),
                Mask = layer.Mask is LayerMask mask && mask.Placement is LayerTransform placement
                    ? mask with { Placement = Move(placement) }
                    : layer.Mask,
            };
        }

        /// <summary>Premultiplied pixels as straight colour planes and alpha; matted over white for the composite, as Photoshop saves it.</summary>
        private static (byte[] R, byte[] G, byte[] B, byte[] A) Straight(PixelBuffer pixels, bool matte)
        {
            long count = (long)pixels.Width * pixels.Height;
            byte[] r = new byte[count], g = new byte[count], b = new byte[count], a = new byte[count];
            for (int y = 0; y < pixels.Height; y++)
            {
                ReadOnlySpan<byte> row = pixels.Row(y);
                long i = (long)y * pixels.Width;
                for (int x = 0; x < pixels.Width; x++, i++)
                {
                    int alpha = row[x * 4 + 3];
                    a[i] = (byte)alpha;
                    if (matte)
                    {
                        r[i] = (byte)Math.Min(255, row[x * 4] + 255 - alpha);
                        g[i] = (byte)Math.Min(255, row[x * 4 + 1] + 255 - alpha);
                        b[i] = (byte)Math.Min(255, row[x * 4 + 2] + 255 - alpha);
                    }
                    else if (alpha > 0)
                    {
                        r[i] = (byte)Math.Min(255, (row[x * 4] * 255 + alpha / 2) / alpha);
                        g[i] = (byte)Math.Min(255, (row[x * 4 + 1] * 255 + alpha / 2) / alpha);
                        b[i] = (byte)Math.Min(255, (row[x * 4 + 2] * 255 + alpha / 2) / alpha);
                    }
                }
            }
            return (r, g, b, a);
        }

        /// <summary>One channel packed with PackBits: the compression code, each row's length, then the rows.</summary>
        private static byte[] Encode(byte[] plane, int width, int height)
        {
            if (width <= 0 || height <= 0) return [0, 0];

            var packed = new List<byte>(plane.Length / 2 + height * 2);
            var lengths = new int[height];
            for (int y = 0; y < height; y++)
                lengths[y] = PsdCompression.PackRow(plane.AsSpan(y * width, width), packed);

            var data = new byte[2 + height * 2 + packed.Count];
            data[1] = (byte)PsdCompression.Rle;
            for (int y = 0; y < height; y++)
            {
                data[2 + y * 2] = (byte)(lengths[y] >> 8);
                data[3 + y * 2] = (byte)lengths[y];
            }
            packed.CopyTo(data, 2 + height * 2);
            return data;
        }

        public void WriteTo(PsdWriter writer)
        {
            bool layered = _records.Count > 0;

            writer.Key("8BPS");
            writer.U16(1);
            writer.Zeros(6);
            writer.U16(layered ? (ushort)4 : (ushort)3);
            writer.U32((uint)document.Height);
            writer.U32((uint)document.Width);
            writer.U16(8);
            writer.U16((ushort)PsdColorMode.Rgb);

            writer.U32(0); // colour mode data

            long resources = writer.BeginLength();
            writer.Key("8BIM");
            writer.U16(0x03ED);
            writer.U16(0); // empty name, padded to even
            writer.U32(16);
            int fixedResolution = (int)Math.Round(document.Resolution * 65536);
            writer.I32(fixedResolution);
            writer.U16(1);
            writer.U16(1);
            writer.I32(fixedResolution);
            writer.U16(1);
            writer.U16(1);
            writer.EndLength(resources);

            long section = writer.BeginLength();
            if (layered)
            {
                long info = writer.BeginLength();
                // Negative: the composite's extra channel is its transparency, not an alpha channel.
                writer.I16((short)-_records.Count);
                foreach (Record record in _records) WriteRecord(writer, record);
                foreach (Record record in _records)
                    foreach (var (_, data) in record.Channels) writer.Bytes(data);
                writer.EndLength(info, alignment: 4);
            }
            else
            {
                writer.U32(0);
            }
            writer.U32(0); // global layer mask
            writer.EndLength(section);

            WriteComposite(writer, layered);
        }

        private static void WriteRecord(PsdWriter writer, Record record)
        {
            writer.I32(record.Top);
            writer.I32(record.Left);
            writer.I32(record.Bottom);
            writer.I32(record.Right);
            writer.U16((ushort)record.Channels.Count);
            foreach ((short id, byte[] data) in record.Channels)
            {
                writer.I16(id);
                writer.U32((uint)data.Length);
            }
            writer.Key("8BIM");
            writer.Key(record.Blend);
            writer.U8(record.Opacity);
            writer.U8(record.Clipping);
            writer.U8(record.Flags);
            writer.U8(0);

            long extra = writer.BeginLength();

            if (record.Mask is MaskOut mask)
            {
                writer.U32(20);
                writer.I32(mask.Top);
                writer.I32(mask.Left);
                writer.I32(mask.Bottom);
                writer.I32(mask.Right);
                writer.U8(mask.Default);
                writer.U8(mask.Disabled ? (byte)0x02 : (byte)0);
                writer.Zeros(2);
            }
            else
            {
                writer.U32(0);
            }

            // Blending ranges: the composite and each channel passing everything, as Photoshop writes them.
            writer.U32(40);
            for (int i = 0; i < 10; i++)
            {
                writer.U16(0);
                writer.U16(0xFFFF);
            }

            writer.Pascal(record.Name, 4);

            foreach (PsdBlockWriter block in record.Blocks)
            {
                writer.Key("8BIM");
                writer.Key(block.Key);
                long length = writer.BeginLength();
                block.Write(writer);
                writer.EndLength(length, alignment: 2);
            }

            writer.EndLength(extra);
        }

        private void WriteComposite(PsdWriter writer, bool layered)
        {
            byte[][] planes;
            using (var backend = new SoftwareRenderBackend())
            using (PixelBuffer flattened = LayerCompositor.Render(document, backend))
            {
                (byte[] r, byte[] g, byte[] b, byte[] a) = Straight(flattened, matte: true);
                planes = layered ? new[] { r, g, b, a } : new[] { r, g, b };
            }

            int width = document.Width, height = document.Height;
            var packed = new List<byte>[planes.Length];
            var lengths = new int[planes.Length][];
            for (int c = 0; c < planes.Length; c++)
            {
                packed[c] = new List<byte>(planes[c].Length / 2);
                lengths[c] = new int[height];
                for (int y = 0; y < height; y++)
                    lengths[c][y] = PsdCompression.PackRow(planes[c].AsSpan(y * width, width), packed[c]);
            }

            writer.U16(PsdCompression.Rle);
            foreach (int[] rows in lengths)
                foreach (int length in rows) writer.U16((ushort)length);
            foreach (List<byte> channel in packed) writer.Bytes(channel.ToArray());
        }
    }
}
