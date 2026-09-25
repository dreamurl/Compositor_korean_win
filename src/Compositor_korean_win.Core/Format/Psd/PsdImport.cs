namespace Compositor_korean_win.Core;

/// <summary>A Photoshop document opened as this editor's, and what could not come across exactly.</summary>
public sealed class PsdImportResult
{
    public required CanvasDocument Document { get; init; }

    /// <summary>How many times each kind of loss happened; empty when the file came across whole.</summary>
    public required IReadOnlyDictionary<PsdNote, int> Notes { get; init; }

    /// <summary>Families the type asks for that are not installed, so the person knows what to install.</summary>
    public IReadOnlyList<string> MissingFonts { get; init; } = [];
}

/// <summary>
/// Opens Photoshop documents (<c>.psd</c> and <c>.psb</c>) as layered documents.
/// </summary>
/// <remarks>
/// <para>
/// A PSD keeps each layer as pixels on the document's own grid, which is exactly an image layer
/// here with its origin at the layer's rectangle and no rotation. Groups, masks, clipping,
/// opacity, visibility and the thirteen shared blend modes carry straight across; Levels, Curves,
/// Hue/Saturation, Exposure and two-colour Gradient Maps become this editor's adjustment layers;
/// drop shadow, outer glow and stroke become its layer effects.
/// </para>
/// <para>
/// Point type arrives as this editor's text over the pixels Photoshop drew for it, so it looks the
/// same until the words are changed and is set again from then on (<see cref="PsdType"/>). Box
/// type, turned type, smart objects and shape layers arrive as the pixels Photoshop last drew for
/// them, which every PSD stores for exactly this purpose. What cannot be carried — a blend mode or an
/// adjustment this editor lacks, an effect it does not draw — is counted in
/// <see cref="PsdImportResult.Notes"/> so the person is told, and the rest of the document still
/// opens: refusing a whole file over one Soft Light layer would help no one.
/// </para>
/// <para>
/// A group with an opacity or blend mode of its own composites its contents in isolation, which a
/// folder here — always pass-through, as upstream's — cannot. Such a group is merged into one
/// layer that keeps its opacity, mode and mask, so it looks as it did.
/// </para>
/// </remarks>
public static class PsdImport
{
    public static PsdImportResult Read(byte[] data) => Read(data, null);

    /// <param name="installedFamilies">
    /// The font families installed, by English name, for finding the ones the type names; null to
    /// guess every family from its PostScript name.
    /// </param>
    public static PsdImportResult Read(byte[] data, IReadOnlyCollection<string>? installedFamilies) =>
        new Importer(PsdFile.Read(data), installedFamilies).Run();

    public static PsdImportResult Read(string path, IReadOnlyCollection<string>? installedFamilies = null) =>
        Read(File.ReadAllBytes(path), installedFamilies);

    /// <summary>Whether <paramref name="start"/> begins a PSD or PSB, whatever the file is called.</summary>
    public static bool IsPsd(ReadOnlySpan<byte> start) => PsdFile.LooksLikePsd(start);

    /// <summary>
    /// The flattened image Photoshop saved beside the layers, premultiplied — what the document
    /// looked like in Photoshop, which is what an import is measured against.
    /// </summary>
    public static PixelBuffer Composite(byte[] data)
    {
        PsdFile file = PsdFile.Read(data);
        return new Importer(file, null).Flattened();
    }

    private sealed class Node
    {
        public required PsdLayerRecord Record { get; init; }
        public List<Node>? Children { get; init; }
    }

    private sealed class Importer(PsdFile file, IReadOnlyCollection<string>? families)
    {
        private readonly Dictionary<PsdNote, int> _notes = [];
        private readonly SortedSet<string> _missingFonts = new(StringComparer.OrdinalIgnoreCase);
        private List<ImageLayer> _layers = [];
        private readonly LayerTransform _canvas = new(Point.Zero, new Size(file.Width, file.Height));

        private void Note(PsdNote note) => _notes[note] = _notes.GetValueOrDefault(note) + 1;

        public PsdImportResult Run()
        {
            if (file.Mode == PsdColorMode.Multichannel) throw PsdFormat.Invalid("multichannel documents");

            try
            {
                if (file.Layers.Count > 0) Emit(BuildTree(), parent: null, depth: 0);

                if (_layers.Count == 0)
                {
                    if (file.Layers.Count == 0) Note(PsdNote.FlattenedOnly);
                    _layers.Add(new ImageLayer
                    {
                        Id = Guid.NewGuid(),
                        Name = Localizer.Text(TextKey.LayerBackground),
                        Transform = _canvas,
                        Image = Flattened(),
                    });
                }

                var document = new CanvasDocument
                {
                    Id = Guid.NewGuid(),
                    Width = file.Width,
                    Height = file.Height,
                    Resolution = file.Resolution,
                    Layers = new EquatableList<ImageLayer>(_layers),
                };

                // The same checks a .comp must pass before it replaces the open document, so a
                // PSD can never put the editor in a state its own format would refuse.
                ManifestValidator.Validate(ProjectMapping.ToSnapshot(document, activeLayerId: null).Manifest);

                return new PsdImportResult { Document = document, Notes = _notes, MissingFonts = [.. _missingFonts] };
            }
            catch
            {
                ReleaseAll(_layers);
                throw;
            }
        }

        private static void ReleaseAll(IEnumerable<ImageLayer> layers)
        {
            foreach (ImageLayer layer in layers)
            {
                layer.Image?.Release();
                layer.Mask?.Coverage.Release();
            }
        }

        /// <summary>Records in file order, bottom first, folded into groups by their section dividers.</summary>
        private List<Node> BuildTree()
        {
            var open = new Stack<List<Node>>();
            open.Push([]);

            foreach (PsdLayerRecord record in file.Layers)
            {
                switch (Section(record, out _))
                {
                    case 3:
                        // The divider that closes a group from below: its members follow.
                        open.Push([]);
                        break;
                    case 1:
                    case 2:
                    {
                        List<Node> members = open.Count > 1 ? open.Pop() : [];
                        open.Peek().Add(new Node { Record = record, Children = members });
                        break;
                    }
                    default:
                        open.Peek().Add(new Node { Record = record });
                        break;
                }
            }

            // A divider never closed leaves its members at the level it opened on.
            while (open.Count > 1)
            {
                List<Node> orphans = open.Pop();
                open.Peek().AddRange(orphans);
            }
            return open.Pop();
        }

        /// <summary>0 for a layer, 1 or 2 for a group, 3 for the divider below one; and the group's own blend key.</summary>
        private int Section(PsdLayerRecord record, out string? blendKey)
        {
            blendKey = null;
            PsdReader? block = file.Block(record, "lsct") ?? file.Block(record, "lsdk");
            if (block is null || block.Remaining < 4) return 0;
            int kind = (int)block.U32();
            if (block.Remaining >= 8 && block.Key() == "8BIM") blendKey = block.Key();
            return kind;
        }

        private void Emit(List<Node> siblings, Guid? parent, int depth)
        {
            Guid? clipBase = null;
            bool baseHidden = false;

            foreach (Node node in siblings)
            {
                bool clipped = node.Record.Clipping != 0;
                ImageLayer? made = Make(node, parent, depth);

                if (!clipped)
                {
                    // Only a plain layer can be a base here; a folder or an adjustment cannot.
                    clipBase = made is { IsGroup: false, Adjustment: null } ? made.Id : null;
                    baseHidden = made is { IsVisible: false };
                    continue;
                }

                if (made is not ImageLayer clippedLayer) continue;
                if (clipBase is not Guid source || clippedLayer.IsGroup)
                {
                    Note(PsdNote.ClippingDropped);
                    continue;
                }

                // Photoshop hides a whole clipping group with its base; here a hidden base still
                // clips, so its clipped layers are hidden with it to look the same.
                Guid id = clippedLayer.Id;
                int index = _layers.FindIndex(layer => layer.Id == id);
                _layers[index] = clippedLayer with { MaskSourceId = source, IsVisible = clippedLayer.IsVisible && !baseHidden };
            }
        }

        private ImageLayer? Make(Node node, Guid? parent, int depth)
        {
            PsdLayerRecord record = node.Record;
            string name = Name(record);
            bool visible = !record.IsHidden;

            if (node.Children is List<Node> members)
            {
                Section(record, out string? key);
                string blend = key ?? record.BlendKey;
                if (blend == "pass" && record.Opacity == 255 && depth < ProjectLimits.MaximumNesting - 1)
                {
                    var folder = new ImageLayer
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        Transform = _canvas,
                        IsGroup = true,
                        ParentId = parent,
                        IsVisible = visible,
                        Mask = Mask(record, grid: null),
                    };
                    _layers.Add(folder);
                    Emit(members, folder.Id, depth + 1);
                    return folder;
                }
                return Merged(record, members, parent, name, visible, blend == "pass" ? "norm" : blend);
            }

            if (PsdAdjustments.Supported.Any(record.Blocks.ContainsKey))
            {
                LayerAdjustment? adjustment = PsdAdjustments.Read(file, record, Note);
                if (adjustment is null) return null;
                var layer = new ImageLayer
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    Transform = _canvas,
                    ParentId = parent,
                    IsVisible = visible,
                    Opacity = record.Opacity / 255.0 * Fill(record) / 255.0,
                    BlendMode = Blend(record.BlendKey),
                    Adjustment = adjustment,
                    Mask = Mask(record, grid: null),
                };
                _layers.Add(layer);
                return layer;
            }

            if (PsdAdjustments.Unsupported.Any(record.Blocks.ContainsKey))
            {
                Note(PsdNote.AdjustmentDropped);
                return null;
            }

            bool typed = record.Blocks.ContainsKey("TySh") || record.Blocks.ContainsKey("tySh");
            if (record.Blocks.ContainsKey("SoLd") || record.Blocks.ContainsKey("PlLd") || record.Blocks.ContainsKey("SoLE"))
                Note(PsdNote.SmartObjectRasterized);

            // A fill or shape layer's pixels are already cut to its vector mask; a pixel layer's are not.
            bool vector = record.Blocks.ContainsKey("vmsk") || record.Blocks.ContainsKey("vsms");
            bool filled = record.Blocks.ContainsKey("SoCo") || record.Blocks.ContainsKey("GdFl") || record.Blocks.ContainsKey("PtFl")
                          || record.Blocks.ContainsKey("vscg");
            if (vector && !filled) Note(PsdNote.VectorMaskDropped);

            (PixelBuffer Pixels, PixelRect Box)? pixels = Pixels(record);
            LayerTransform placement = _canvas;
            if (pixels is { } found)
                placement = new LayerTransform(new Point(found.Box.X, found.Box.Y), new Size(found.Box.Width, found.Box.Height));

            LayerText? text = typed ? TypeLayer(record, pixels) : null;
            if (typed && text is null) Note(PsdNote.TypeRasterized);

            var made = new ImageLayer
            {
                Id = Guid.NewGuid(),
                Name = name,
                Transform = placement,
                Image = pixels?.Pixels,
                ParentId = parent,
                IsVisible = visible,
                Opacity = record.Opacity / 255.0,
                BlendMode = Blend(record.BlendKey),
                Mask = Mask(record, pixels?.Box),
                Effects = PsdEffects.Read(file, record, Note),
                Text = text,
            };
            _layers.Add(made);
            return made;
        }

        /// <summary>
        /// A type layer as live text over Photoshop's own pixels — it looks exactly as it did until
        /// the words change — or null to keep it as pixels.
        /// </summary>
        private LayerText? TypeLayer(PsdLayerRecord record, (PixelBuffer Pixels, PixelRect Box)? pixels)
        {
            // Fill opacity is multiplied into the pixels (see Pixels); set again, the words would lose it.
            if (pixels is not (PixelBuffer image, PixelRect box) || Fill(record) != 255) return null;
            if (file.Block(record, "TySh") is not PsdReader block) return null;

            PsdTypeLayer? type;
            try
            {
                type = PsdType.Read(block);
            }
            catch (ProjectException)
            {
                return null;
            }
            if (type is null || string.IsNullOrWhiteSpace(type.PostScriptName)) return null;

            TextFace face = PsdFonts.Resolve(type.PostScriptName, families, out bool installed);
            if (!installed)
            {
                Note(PsdNote.FontMissing);
                _missingFonts.Add(face.Family);
            }
            if (type.Simplified) Note(PsdNote.TypeSimplified);

            LayerText text = type.Text with
            {
                Font = face.Family,
                Weight = Math.Max(type.Text.Weight, face.Weight),
                Italic = type.Text.Italic || face.Italic,
                // The anchor is kept in the raster's own pixels; Photoshop's origin is on the document.
                AnchorX = type.AnchorX - box.X,
                AnchorY = type.AnchorY - box.Y,
                Rendered = image,
                Runs = null,
            };

            // A run's face is a PostScript name too; each is found installed the same way, and the
            // runs are rebuilt against the layer's resolved face so a run naming it adds nothing.
            if (type.Text.Runs is not null)
            {
                LayerText[] letters = TextRuns.PerCharacter(type.Text);
                var resolved = new Dictionary<LayerText, LayerText>(ReferenceEqualityComparer.Instance);
                for (int i = 0; i < letters.Length; i++)
                {
                    LayerText letter = letters[i];
                    if (!resolved.TryGetValue(letter, out LayerText? found))
                    {
                        TextFace runFace = PsdFonts.Resolve(letter.Font, families, out bool here);
                        if (!here)
                        {
                            Note(PsdNote.FontMissing);
                            _missingFonts.Add(runFace.Family);
                        }
                        found = text with
                        {
                            Font = runFace.Family,
                            Weight = Math.Max(letter.Weight, runFace.Weight),
                            Italic = letter.Italic || runFace.Italic,
                            Size = letter.Size,
                            Red = letter.Red,
                            Green = letter.Green,
                            Blue = letter.Blue,
                        };
                        resolved[letter] = found;
                    }
                    letters[i] = found;
                }
                text = TextRuns.FromCharacters(text, letters) with { Rendered = image };
            }
            return text.IsValid ? text : null;
        }

        private string Name(PsdLayerRecord record)
        {
            string name = file.Block(record, "luni") is PsdReader block && block.Remaining >= 4 ? block.Unicode() : record.Name;
            name = name.Trim('\0');
            return string.IsNullOrWhiteSpace(name) ? Localizer.Format(TextKey.LayerNameNumbered, _layers.Count + 1) : name;
        }

        /// <summary>Fill opacity, 0–255: Photoshop's second opacity, which leaves effects alone.</summary>
        private int Fill(PsdLayerRecord record) =>
            file.Block(record, "iOpa") is PsdReader block && block.Remaining >= 1 ? block.U8() : 255;

        private LayerBlendMode Blend(string key)
        {
            if (key == "pass") return LayerBlendMode.Normal;
            if (PsdBlend.FromKey(key) is LayerBlendMode mode) return mode;
            Note(PsdNote.BlendModeReplaced);
            return LayerBlendMode.Normal;
        }

        /// <summary>
        /// A layer's pixels, premultiplied, and where they sit — or null when it has none. Fill
        /// opacity is multiplied into the pixels, which is exactly what it does: it fades the layer's
        /// content and not its effects, and this editor's opacity fades both.
        /// </summary>
        private (PixelBuffer, PixelRect)? Pixels(PsdLayerRecord record)
        {
            int width = record.Width, height = record.Height;
            if (width == 0 || height == 0) return null;

            // A layer can reach far past the canvas; past this editor's size limits, only the part
            // over the canvas is kept.
            var box = new PixelRect(record.Left, record.Top, width, height);
            if (width > ProjectLimits.MaximumSide || height > ProjectLimits.MaximumSide || (long)width * height > ProjectLimits.MaximumPixels)
            {
                int left = Math.Max(record.Left, 0), top = Math.Max(record.Top, 0);
                int right = Math.Min(record.Right, file.Width), bottom = Math.Min(record.Bottom, file.Height);
                if (right <= left || bottom <= top) return null;
                box = new PixelRect(left, top, right - left, bottom - top);
            }

            byte[]? alpha = file.Plane(record.Channel(-1), width, height, linear: false);
            byte[]?[] colour = [.. Enumerable.Range(0, file.ColourChannels)
                .Select(c => file.Plane(record.Channel((short)c), width, height, linear: true))];
            int fill = Fill(record);

            PixelBuffer buffer = PixelBuffer.Allocate(box.Width, box.Height);
            int offsetX = box.X - record.Left, offsetY = box.Y - record.Top;
            for (int y = 0; y < box.Height; y++)
            {
                Span<byte> row = buffer.Row(y);
                long source = (long)(y + offsetY) * width + offsetX;
                for (int x = 0; x < box.Width; x++, source++)
                {
                    int a = alpha is null ? 255 : alpha[source];
                    if (fill < 255) a = (a * fill + 127) / 255;
                    (byte r, byte g, byte b) = Colour(colour, source);
                    row[x * 4] = (byte)((r * a + 127) / 255);
                    row[x * 4 + 1] = (byte)((g * a + 127) / 255);
                    row[x * 4 + 2] = (byte)((b * a + 127) / 255);
                    row[x * 4 + 3] = (byte)a;
                }
            }
            return (buffer, box);
        }

        /// <summary>One pixel's colour, from however many channels the document's mode uses, as sRGB.</summary>
        private (byte R, byte G, byte B) Colour(byte[]?[] channels, long i)
        {
            byte At(int c) => c < channels.Length && channels[c] is byte[] plane ? plane[i] : (byte)0;

            switch (file.Mode)
            {
                case PsdColorMode.Rgb:
                    return (At(0), At(1), At(2));

                case PsdColorMode.Cmyk:
                {
                    // Stored inverted: 255 is no ink. A plain conversion without a colour profile.
                    int k = At(3);
                    return ((byte)(At(0) * k / 255), (byte)(At(1) * k / 255), (byte)(At(2) * k / 255));
                }

                case PsdColorMode.Lab:
                    return FromLab(At(0) * 100.0 / 255, At(1) - 128.0, At(2) - 128.0);

                case PsdColorMode.Indexed:
                {
                    int index = At(0);
                    byte[] palette = file.ColorModeData;
                    return palette.Length >= 768 ? (palette[index], palette[256 + index], palette[512 + index]) : (At(0), At(0), At(0));
                }

                default:
                    return (At(0), At(0), At(0));
            }
        }

        /// <summary>CIELAB (D50, as Photoshop keeps it) to sRGB.</summary>
        private static (byte, byte, byte) FromLab(double l, double a, double b)
        {
            double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
            static double Inverse(double t) => t > 6.0 / 29 ? t * t * t : 3 * (6.0 / 29) * (6.0 / 29) * (t - 4.0 / 29);
            double x = 0.9642 * Inverse(fx), y = Inverse(fy), z = 0.8249 * Inverse(fz);

            // XYZ (D50) to linear sRGB, Bradford-adapted to D65.
            double r = 3.1338561 * x - 1.6168667 * y - 0.4906146 * z;
            double g = -0.9787684 * x + 1.9161415 * y + 0.0334540 * z;
            double bl = 0.0719453 * x - 0.2289914 * y + 1.4052427 * z;
            return (Encode(r), Encode(g), Encode(bl));

            static byte Encode(double v)
            {
                v = Math.Clamp(v, 0, 1);
                v = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
                return (byte)Math.Round(v * 255);
            }
        }

        /// <summary>
        /// A layer's pixel mask. Over a layer's own pixels (<paramref name="grid"/>) it covers that
        /// grid, as every mask here does; on a folder or an adjustment it is placed on the document
        /// over just the rectangle Photoshop stored, framed by one pixel of Photoshop's default level
        /// — which is the level this editor shows beyond a placed mask — so a small mask on a large
        /// canvas stays small.
        /// </summary>
        private LayerMask? Mask(PsdLayerRecord record, PixelRect? grid)
        {
            if (record.Mask is not PsdMaskRecord mask) return null;

            bool real = record.Channel(-3) is not null && mask.HasReal;
            PsdChannel? channel = real ? record.Channel(-3) : record.Channel(-2);
            if (channel is null) return null;
            if (mask.HasParameters) Note(PsdNote.MaskParametersDropped);

            int top = real ? mask.RealTop : mask.Top, left = real ? mask.RealLeft : mask.Left;
            int bottom = real ? mask.RealBottom : mask.Bottom, right = real ? mask.RealRight : mask.Right;
            byte level = real ? mask.RealDefault : mask.Default;
            bool enabled = !mask.Disabled(real);
            int width = Math.Max(0, right - left), height = Math.Max(0, bottom - top);

            byte[]? plane = file.Plane(channel, width, height, linear: false);
            if (plane is null) return LayerMask.Solid(level >= 128) with { IsEnabled = enabled };

            if (grid is PixelRect box)
            {
                PixelBuffer coverage = Grey(box.Width, box.Height, level);
                Copy(plane, width, height, left - box.X, top - box.Y, coverage);
                return new LayerMask { Coverage = coverage, IsEnabled = enabled };
            }

            PixelBuffer placed = Grey(width + 2, height + 2, level);
            Copy(plane, width, height, 1, 1, placed);
            return new LayerMask
            {
                Coverage = placed,
                IsEnabled = enabled,
                Placement = new LayerTransform(new Point(left - 1, top - 1), new Size(width + 2, height + 2)),
            };
        }

        private static PixelBuffer Grey(int width, int height, byte level)
        {
            PixelBuffer buffer = PixelBuffer.Allocate(width, height);
            for (int y = 0; y < height; y++)
            {
                Span<byte> row = buffer.Row(y);
                for (int x = 0; x < width; x++)
                {
                    row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = level;
                    row[x * 4 + 3] = 255;
                }
            }
            return buffer;
        }

        /// <summary>Copies a mask plane into a grey buffer, placed at an offset and cut to it.</summary>
        private static void Copy(byte[] plane, int width, int height, int offsetX, int offsetY, PixelBuffer target)
        {
            for (int y = Math.Max(0, -offsetY); y < height && y + offsetY < target.Height; y++)
            {
                Span<byte> row = target.Row(y + offsetY);
                for (int x = Math.Max(0, -offsetX); x < width && x + offsetX < target.Width; x++)
                {
                    byte value = plane[(long)y * width + x];
                    int at = (x + offsetX) * 4;
                    row[at] = row[at + 1] = row[at + 2] = value;
                }
            }
        }

        /// <summary>A group that composites apart from what is under it, merged into one layer that looks the same.</summary>
        private ImageLayer? Merged(PsdLayerRecord record, List<Node> members, Guid? parent, string name, bool visible, string blendKey)
        {
            Note(PsdNote.GroupMerged);

            List<ImageLayer> outer = _layers;
            _layers = [];
            try
            {
                Emit(members, parent: null, depth: 0);
                var inside = new CanvasDocument
                {
                    Id = Guid.NewGuid(),
                    Width = file.Width,
                    Height = file.Height,
                    Layers = new EquatableList<ImageLayer>(_layers),
                };

                PixelBuffer? pixels;
                PixelRect box;
                using (var backend = new SoftwareRenderBackend())
                using (PixelBuffer whole = LayerCompositor.Render(inside, backend))
                {
                    (pixels, box) = Trimmed(whole);
                }

                var made = new ImageLayer
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    Transform = pixels is null ? _canvas : new LayerTransform(new Point(box.X, box.Y), new Size(box.Width, box.Height)),
                    Image = pixels,
                    ParentId = parent,
                    IsVisible = visible,
                    Opacity = record.Opacity / 255.0,
                    Mask = Mask(record, pixels is null ? null : box),
                    Effects = PsdEffects.Read(file, record, Note),
                };
                ReleaseAll(_layers);
                _layers = outer;
                made = made with { BlendMode = Blend(blendKey) };
                _layers.Add(made);
                return made;
            }
            catch
            {
                ReleaseAll(_layers);
                _layers = outer;
                throw;
            }
        }

        /// <summary>A copy of just the part of <paramref name="whole"/> that shows anything, or null if nothing does.</summary>
        private static (PixelBuffer?, PixelRect) Trimmed(PixelBuffer whole)
        {
            int minX = whole.Width, minY = whole.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < whole.Height; y++)
            {
                ReadOnlySpan<byte> row = whole.Row(y);
                for (int x = 0; x < whole.Width; x++)
                {
                    if (row[x * 4 + 3] == 0) continue;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
            if (maxX < 0) return (null, default);

            var box = new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
            PixelBuffer trimmed = PixelBuffer.Allocate(box.Width, box.Height);
            for (int y = 0; y < box.Height; y++)
                whole.Row(box.Y + y).Slice(box.X * 4, box.Width * 4).CopyTo(trimmed.Row(y));
            return (trimmed, box);
        }

        /// <summary>The composite image section as a premultiplied buffer the size of the canvas.</summary>
        public PixelBuffer Flattened()
        {
            byte[][] planes = file.Composite();
            int colours = file.Mode is PsdColorMode.Indexed or PsdColorMode.Bitmap ? 1 : file.ColourChannels;
            byte[]? alpha = file.MergedAlpha && planes.Length > colours ? planes[colours] : null;
            byte[]?[] colour = [.. planes.Take(colours)];

            PixelBuffer buffer = PixelBuffer.Allocate(file.Width, file.Height);
            for (int y = 0; y < file.Height; y++)
            {
                Span<byte> row = buffer.Row(y);
                long source = (long)y * file.Width;
                for (int x = 0; x < file.Width; x++, source++)
                {
                    int a = alpha is null ? 255 : alpha[source];
                    (byte r, byte g, byte b) = Colour(colour, source);

                    // Photoshop saves the composite matted against white where it is not opaque;
                    // taking the white back out leaves the colour the layers had.
                    if (alpha is not null && a is > 0 and < 255)
                    {
                        r = Unmatte(r, a);
                        g = Unmatte(g, a);
                        b = Unmatte(b, a);
                    }

                    row[x * 4] = (byte)((r * a + 127) / 255);
                    row[x * 4 + 1] = (byte)((g * a + 127) / 255);
                    row[x * 4 + 2] = (byte)((b * a + 127) / 255);
                    row[x * 4 + 3] = (byte)a;
                }
            }
            return buffer;

            static byte Unmatte(byte value, int alpha) =>
                (byte)Math.Clamp((int)Math.Round((value - 255.0 * (1 - alpha / 255.0)) * 255.0 / alpha), 0, 255);
        }
    }
}
