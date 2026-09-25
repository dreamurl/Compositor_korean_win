using System.Text;
using System.Text.Json;
using static Compositor_korean_win.Core.ToolSchema;

namespace Compositor_korean_win.Core;

/// <summary>One tool: its name, what it is for, its arguments' schema, and what it does.</summary>
public sealed record ToolDefinition(string Name, string Description, string InputSchema, Func<ToolArguments, ToolResult> Run);

/// <summary>
/// The editor's tools, as a model calls them over MCP.
/// </summary>
/// <remarks>
/// <para>
/// Each tool is a thin layer over a command the editor itself uses — the same shape drawing, text
/// setting, masks, effects, adjustments and filters — so what a model makes is what a person would
/// make with the same settings, and opens in the editor as editable layers.
/// </para>
/// <para>
/// Coordinates are document pixels from the top-left. Colours are hex. Opacities are 0–1. Every
/// tool that makes a layer answers with its id; <c>render</c> answers with a picture, which is how
/// a model sees what it has done and corrects it.
/// </para>
/// </remarks>
public sealed partial class McpTools
{
    private readonly EditorSession _session;
    private readonly List<ToolDefinition> _tools = [];

    public McpTools(EditorSession session)
    {
        _session = session;
        DefineDocumentTools();
        DefineLayerTools();
        DefineEffectTools();
        DefineSelectionTools();
        DefinePixelTools();
        DefineBatchTool();
    }

    public IReadOnlyList<ToolDefinition> Definitions => _tools;

    public ToolResult Call(string name, ToolArguments arguments)
    {
        ToolDefinition tool = _tools.FirstOrDefault(each => each.Name == name)
                              ?? throw new ToolException($"No tool '{name}'.");
        return tool.Run(arguments);
    }

    private void Define(string name, string description, string schema, Func<ToolArguments, ToolResult> run) =>
        _tools.Add(new ToolDefinition(name, description, schema, run));

    private static readonly Property DocumentArgument =
        Str("document", "Which open document (doc1, doc2… or its title). Defaults to the current one.");

    private EditorSession.Open Doc(ToolArguments arguments) => _session.Get(arguments.String("document"));

    // ---- Documents ---------------------------------------------------------------------------

    private void DefineDocumentTools()
    {
        Define("new_document",
            "Create a new document and make it current. Starts with one background layer filled with 'background' " +
            "(white by default; \"transparent\" for none).",
            Build(Int("width", "Canvas width in pixels.", true), Int("height", "Canvas height in pixels.", true),
                  Colour("background", "Background fill."), Num("resolution", "Pixels per inch, for print. Default 72."),
                  Str("name", "A title for the document.")),
            arguments =>
            {
                int width = arguments.Int("width") ?? throw new ToolException("'width' is required.");
                int height = arguments.Int("height") ?? throw new ToolException("'height' is required.");
                if (!DocumentCommands.IsValidSize(width, height))
                    throw new ToolException($"{width}×{height} is not a canvas size this editor allows (up to {ProjectLimits.MaximumSide} a side).");
                Rgba background = arguments.Colour("background") ?? Rgba.White;
                CanvasDocument document = DocumentCommands.New(width, height, arguments.Number("resolution", 72),
                                                               background.A == 0 ? null : background);
                EditorSession.Open open = _session.Add(document, arguments.String("name") ?? "Untitled", path: null);
                return Described(open, $"Created {open.Id}, {width}×{height}.");
            });

        Define("open_document",
            "Open a .comp project, a .psd/.psb, or an image (PNG, JPEG…) as a new current document.",
            Build(Str("path", "Full path of the file.", true)),
            arguments =>
            {
                string path = arguments.Required("path");
                if (!File.Exists(path)) throw new ToolException($"No file at {path}.");
                string extension = Path.GetExtension(path).ToLowerInvariant();
                string note = "";
                CanvasDocument document;
                if (extension == ".comp")
                {
                    document = ProjectMapping.ToDocument(ProjectStore.Load(path));
                }
                else if (extension is ".psd" or ".psb")
                {
                    PsdImportResult result = PsdImport.Read(path);
                    document = result.Document;
                    if (result.Notes.Count > 0)
                        note = " Not carried exactly: " + string.Join(", ", result.Notes.Select(pair => $"{pair.Key} ×{pair.Value}")) + ".";
                }
                else
                {
                    PixelBuffer pixels = _session.Services.DecodeImage(File.ReadAllBytes(path));
                    document = FromImage(pixels, Path.GetFileNameWithoutExtension(path));
                }

                EditorSession.Open open = _session.Add(document, Path.GetFileNameWithoutExtension(path),
                                                       extension is ".comp" or ".psd" ? path : null);
                return Described(open, $"Opened {path} as {open.Id}.{note}");
            });

        Define("save_document",
            "Save the document as a layered file: .psd (opens in other editors) or .comp (this editor's own). " +
            "Without 'path' it saves where it was last opened or saved.",
            Build(Str("path", "Where to save; the extension picks the format."), DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                string path = arguments.String("path") ?? open.Path ?? throw new ToolException("'path' is required the first time a document is saved.");
                string extension = Path.GetExtension(path).ToLowerInvariant();
                string note = "";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                if (extension == ".psd")
                {
                    IReadOnlyDictionary<PsdNote, int> notes = WritePsd(open.Document, path);
                    if (notes.Count > 0)
                        note = " Not carried exactly: " + string.Join(", ", notes.Select(pair => $"{pair.Key} ×{pair.Value}")) + ".";
                }
                else if (extension == ".comp")
                {
                    ProjectStore.Save(ProjectMapping.ToSnapshot(open.Document, open.Active), path);
                }
                else
                {
                    throw new ToolException("save_document writes .psd or .comp; use export_image for PNG or JPEG.");
                }
                open.Path = path;
                open.History.MarkSaved();
                return ToolResult.Text($"Saved {open.Id} to {path}.{note}");
            });

        Define("export_image",
            "Write the flattened picture as PNG (keeps transparency) or JPEG (over white).",
            Build(Str("path", "Where to write; .png, .jpg or .jpeg.", true),
                  Num("quality", "JPEG quality 0–100. Default 90."), DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                string path = arguments.Required("path");
                string extension = Path.GetExtension(path).ToLowerInvariant();
                using PixelBuffer pixels = Flatten(open.Document);
                byte[] bytes = extension switch
                {
                    ".png" => Png.Encode(pixels),
                    ".jpg" or ".jpeg" => _session.Services.EncodeJpeg(pixels, Math.Clamp(arguments.Number("quality", 90), 0, 100) / 100)
                                         ?? throw new ToolException("JPEG is not available here; export a .png."),
                    _ => throw new ToolException("export_image writes .png, .jpg or .jpeg."),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllBytes(path, bytes);
                return ToolResult.Text($"Exported {open.Document.Width}×{open.Document.Height} to {path} ({bytes.Length / 1024} KB).");
            });

        Define("list_documents", "List the open documents and which one is current.", Build(),
            _ =>
            {
                if (_session.Documents.Count == 0) return ToolResult.Text("No documents are open.");
                var text = new StringBuilder();
                foreach (EditorSession.Open open in _session.Documents)
                {
                    string current = ReferenceEquals(open, _session.Current) ? " (current)" : "";
                    text.AppendLine($"{open.Id}: {open.Title}, {open.Document.Width}×{open.Document.Height}, {open.Document.Layers.Count} layers{current}{(open.Path is null ? "" : ", " + open.Path)}");
                }
                return ToolResult.Text(text.ToString().TrimEnd());
            });

        Define("select_document", "Make an open document the current one.", Build(Str("document", "Its id or title.", true)),
            arguments =>
            {
                EditorSession.Open open = _session.Get(arguments.Required("document"));
                _session.Select(open);
                return Described(open, $"{open.Id} is current.");
            });

        Define("close_document", "Close a document, discarding unsaved changes.", Build(DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                _session.Close(open);
                return ToolResult.Text($"Closed {open.Id}.");
            });

        Define("get_document",
            "Describe the document: size, and every layer from top to bottom with its id, kind, placement, " +
            "opacity, blend mode, clipping, mask, effects, text and adjustment. Use the ids in other tools.",
            Build(DocumentArgument),
            arguments => Described(Doc(arguments), null));

        Define("render",
            "Look at the document as a PNG — or only some layers, with some hidden, or a region. Call this after changes to " +
            "check the result. 'zoom' enlarges a region pixel for pixel (2 = each canvas pixel twice as wide) to inspect detail.",
            Build(Str("layer", "Show only this layer (id or name); a group shows with everything in it."),
                  Strings("layers", "Show only these layers, together."), Strings("hide", "Hide these layers for this look."),
                  Object("region", "Only this part of the canvas.",
                         Num("x", "Left.", true), Num("y", "Top.", true), Num("width", "Width.", true), Num("height", "Height.", true)),
                  Int("max_size", "Longest side of the returned image. Default 1024."),
                  Num("zoom", "Canvas pixels to image pixels, 0.05–16, instead of max_size: 4 shows each pixel 4×4."),
                  Choice("background", "What shows through transparency. Default checker.", ["checker", "white", "black", "transparent"]),
                  DocumentArgument),
            arguments => Render(Doc(arguments), arguments));

        Define("undo", "Undo the last change (or several).", Build(Int("steps", "How many. Default 1."), DocumentArgument),
            arguments => Step(Doc(arguments), arguments.Int("steps") ?? 1, undo: true));

        Define("redo", "Redo what was undone.", Build(Int("steps", "How many. Default 1."), DocumentArgument),
            arguments => Step(Doc(arguments), arguments.Int("steps") ?? 1, undo: false));

        Define("resize_canvas",
            "Change the canvas size without scaling the layers; 'anchor' says which part stays put.",
            Build(Int("width", "New width.", true), Int("height", "New height.", true),
                  Choice("anchor", "Default center.", ["top_left", "top", "top_right", "left", "center", "right", "bottom_left", "bottom", "bottom_right"]),
                  DocumentArgument),
            arguments =>
            {
                EditorSession.Open open = Doc(arguments);
                int width = arguments.Int("width") ?? throw new ToolException("'width' is required.");
                int height = arguments.Int("height") ?? throw new ToolException("'height' is required.");
                string[] anchors = ["top_left", "top", "top_right", "left", "center", "right", "bottom_left", "bottom", "bottom_right"];
                int anchor = Array.IndexOf(anchors, arguments.String("anchor") ?? "center");
                if (anchor < 0) throw new ToolException("'anchor' is not one of " + string.Join(", ", anchors) + ".");
                _session.Edit(open, "Canvas Size", document =>
                    DocumentCommands.ResizeCanvas(document, width, height, anchor)
                    ?? throw new ToolException($"{width}×{height} is not a canvas size this editor allows."));
                return Described(open, $"Canvas is now {width}×{height}.");
            });

        Define("list_fonts", "List installed font families, optionally only those containing 'query'.",
            Build(Str("query", "Part of a family name, e.g. \"Gothic\" or \"Serif\".")),
            arguments =>
            {
                string? query = arguments.String("query");
                IEnumerable<string> fonts = _session.Services.FontFamilies();
                if (!string.IsNullOrWhiteSpace(query)) fonts = fonts.Where(font => font.Contains(query, StringComparison.OrdinalIgnoreCase));
                List<string> list = [.. fonts.Take(300)];
                return ToolResult.Text(list.Count == 0 ? "No fonts found." : string.Join("\n", list));
            });
    }

    private ToolResult Step(EditorSession.Open open, int steps, bool undo)
    {
        int done = 0;
        for (int i = 0; i < Math.Max(1, steps); i++)
        {
            HistorySnapshot? snapshot = undo ? open.History.Undo() : open.History.Redo();
            if (snapshot is null) break;
            open.Document = snapshot.Document ?? open.Document;
            open.Active = snapshot.ActiveLayerId;
            done++;
        }
        string verb = undo ? "Undid" : "Redid";
        return done == 0
            ? ToolResult.Text(undo ? "Nothing to undo." : "Nothing to redo.")
            : Described(open, $"{verb} {done} step{(done == 1 ? "" : "s")}.");
    }

    /// <summary>A PSD written beside the target and moved over it once whole, as the editor saves.</summary>
    private static IReadOnlyDictionary<PsdNote, int> WritePsd(CanvasDocument document, string path)
    {
        string partial = path + ".partial";
        try
        {
            IReadOnlyDictionary<PsdNote, int> notes;
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                notes = PsdExport.Write(document, stream);
            File.Move(partial, path, overwrite: true);
            return notes;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private static CanvasDocument FromImage(PixelBuffer pixels, string name) => new()
    {
        Id = Guid.NewGuid(),
        Width = pixels.Width,
        Height = pixels.Height,
        Layers = new EquatableList<ImageLayer>(
        [
            new ImageLayer
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(name) ? "Image" : name,
                Image = pixels,
                Transform = new LayerTransform(Point.Zero, new Size(pixels.Width, pixels.Height)),
            },
        ]),
    };

    private static PixelBuffer Flatten(CanvasDocument document)
    {
        using var backend = new SoftwareRenderBackend();
        return LayerCompositor.Render(document, backend);
    }

    // ---- Seeing ------------------------------------------------------------------------------

    private ToolResult Render(EditorSession.Open open, ToolArguments arguments)
    {
        CanvasDocument document = open.Document;
        CanvasDocument shown = document;

        List<string> only = [];
        if (arguments.String("layer") is string reference) only.Add(reference);
        if (arguments.Strings("layers") is List<string> several) only.AddRange(several);
        if (only.Count > 0)
        {
            var tops = new HashSet<Guid>();
            var ids = new HashSet<Guid>();
            foreach (string each in only)
            {
                ImageLayer layer = EditorSession.Layer(document, each);
                tops.Add(layer.Id);
                ids.Add(layer.Id);
                ids.UnionWith(LayerCommands.Descendants(document, layer.Id));
            }

            // A chosen layer shows even in a hidden folder, and one clipped to a layer left out
            // shows unclipped rather than not at all.
            shown = document with
            {
                Layers = document.Layers.Where(each => ids.Contains(each.Id))
                    .Select(each => tops.Contains(each.Id)
                        ? each with
                        {
                            ParentId = each.ParentId is Guid parent && ids.Contains(parent) ? parent : null,
                            MaskSourceId = each.MaskSourceId is Guid clip && ids.Contains(clip) ? clip : null,
                            IsVisible = true,
                        }
                        : each)
                    .ToEquatableList(),
            };
        }
        if (arguments.Strings("hide") is List<string> hidden)
        {
            HashSet<Guid> off = [.. hidden.Select(each => EditorSession.Layer(document, each).Id)];
            shown = shown with { Layers = shown.Layers.Select(each => off.Contains(each.Id) ? each with { IsVisible = false } : each).ToEquatableList() };
        }

        var area = new PixelRect(0, 0, document.Width, document.Height);
        if (arguments.Object("region") is ToolArguments region)
        {
            int x = (int)Math.Floor(region.RequiredNumber("x")), y = (int)Math.Floor(region.RequiredNumber("y"));
            int right = (int)Math.Ceiling(x + region.RequiredNumber("width")), bottom = (int)Math.Ceiling(y + region.RequiredNumber("height"));
            area = new PixelRect(x, y, right - x, bottom - y).Intersect(area);
            if (area.IsEmpty) throw new ToolException("'region' is outside the canvas.");
        }

        const int Largest = 4096;
        double scale;
        if (arguments.Number("zoom") is double zoom)
        {
            if (!(zoom > 0)) throw new ToolException("'zoom' must be above 0.");
            scale = Math.Min(Math.Clamp(zoom, 0.05, 16), (double)Largest / Math.Max(area.Width, area.Height));
        }
        else
        {
            int limit = Math.Clamp(arguments.Int("max_size") ?? 1024, 16, Largest);
            scale = Math.Min(1, (double)limit / Math.Max(area.Width, area.Height));
        }
        int outWidth = Math.Max(1, (int)Math.Round(area.Width * scale)), outHeight = Math.Max(1, (int)Math.Round(area.Height * scale));

        byte[] png;
        using (PixelBuffer whole = Flatten(shown))
        using (PixelBuffer cut = PixelRegion.Copy(whole, area))
        using (PixelBuffer sized = scale > 1 ? Enlarged(cut, outWidth, outHeight) : Downscaled(cut, outWidth, outHeight))
        {
            Backdrop(sized, arguments.String("background") ?? "checker");
            png = Png.Encode(sized);
        }

        string what = only.Count == 0 ? open.Id : only.Count == 1 ? $"Layer '{only[0]}'" : $"Layers {string.Join(", ", only.Select(each => $"'{each}'"))}";
        var result = new ToolResult();
        result.Content.Add(ToolContent.Of(
            $"{what}, canvas region {area.X},{area.Y} {area.Width}×{area.Height}" +
            (scale == 1 ? "." : $", shown at {scale:P0} as {outWidth}×{outHeight}; image pixel (px, py) is canvas ({area.X} + px/{scale:0.###}, {area.Y} + py/{scale:0.###}).")));
        result.Content.Add(ToolContent.Png(png));
        return result;
    }

    /// <summary>Each source pixel repeated into a block, so a zoomed look shows pixels rather than a blur.</summary>
    private static PixelBuffer Enlarged(PixelBuffer source, int width, int height)
    {
        PixelBuffer result = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> line = source.Row(Math.Min(source.Height - 1, y * source.Height / height));
            Span<byte> row = result.Row(y);
            for (int x = 0; x < width; x++)
                line.Slice(Math.Min(source.Width - 1, x * source.Width / width) * 4, 4).CopyTo(row.Slice(x * 4, 4));
        }
        return result;
    }

    /// <summary>An area average down to the given size: every source pixel counts once, so thin type stays visible.</summary>
    private static PixelBuffer Downscaled(PixelBuffer source, int width, int height)
    {
        if (width == source.Width && height == source.Height) return PixelRegion.Copy(source, new PixelRect(0, 0, width, height));

        PixelBuffer result = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            int top = y * source.Height / height, bottom = Math.Max(top + 1, (y + 1) * source.Height / height);
            Span<byte> row = result.Row(y);
            for (int x = 0; x < width; x++)
            {
                int left = x * source.Width / width, right = Math.Max(left + 1, (x + 1) * source.Width / width);
                long r = 0, g = 0, b = 0, a = 0;
                for (int sy = top; sy < bottom; sy++)
                {
                    ReadOnlySpan<byte> line = source.Row(sy);
                    for (int sx = left; sx < right; sx++)
                    {
                        r += line[sx * 4];
                        g += line[sx * 4 + 1];
                        b += line[sx * 4 + 2];
                        a += line[sx * 4 + 3];
                    }
                }
                int count = (bottom - top) * (right - left);
                row[x * 4] = (byte)(r / count);
                row[x * 4 + 1] = (byte)(g / count);
                row[x * 4 + 2] = (byte)(b / count);
                row[x * 4 + 3] = (byte)(a / count);
            }
        }
        return result;
    }

    /// <summary>Puts a backdrop under the premultiplied pixels, in place.</summary>
    private static void Backdrop(PixelBuffer pixels, string kind)
    {
        if (kind == "transparent") return;
        for (int y = 0; y < pixels.Height; y++)
        {
            Span<byte> row = pixels.Row(y);
            for (int x = 0; x < pixels.Width; x++)
            {
                int under = kind switch
                {
                    "white" => 255,
                    "black" => 0,
                    _ => ((x / 8) + (y / 8)) % 2 == 0 ? 255 : 214,
                };
                int alpha = row[x * 4 + 3];
                for (int c = 0; c < 3; c++) row[x * 4 + c] = (byte)Math.Min(255, row[x * 4 + c] + under * (255 - alpha) / 255);
                row[x * 4 + 3] = 255;
            }
        }
    }

    // ---- Describing --------------------------------------------------------------------------

    private static readonly string[] BlendNames =
        ["normal", "multiply", "screen", "overlay", "darken", "lighten", "difference", "color_dodge", "color_burn",
         "hue", "saturation", "color", "luminosity"];

    private static string BlendName(LayerBlendMode mode) => BlendNames[(int)mode];

    private static LayerBlendMode Blend(string name)
    {
        int index = Array.IndexOf(BlendNames, name.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_'));
        return index >= 0 ? (LayerBlendMode)index : throw new ToolException($"'{name}' is not a blend mode: {string.Join(", ", BlendNames)}.");
    }

    private ToolResult Described(EditorSession.Open open, string? headline)
    {
        string json = Describe(open);
        return ToolResult.Text(headline is null ? json : headline + "\n" + json);
    }

    /// <summary>The document as JSON: size, and the layers from the top down.</summary>
    public static string Describe(EditorSession.Open open)
    {
        CanvasDocument document = open.Document;
        var depth = new Dictionary<Guid, int>();
        foreach (ImageLayer layer in document.Layers)
            depth[layer.Id] = layer.ParentId is Guid parent && depth.TryGetValue(parent, out int above) ? above + 1 : 0;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("document", open.Id);
            writer.WriteString("title", open.Title);
            writer.WriteNumber("width", document.Width);
            writer.WriteNumber("height", document.Height);
            writer.WriteNumber("resolution", document.Resolution);
            if (open.Path is string path) writer.WriteString("path", path);
            writer.WriteBoolean("unsaved_changes", open.History.IsModified);
            if (open.Selection is DocumentSelection selection)
            {
                writer.WriteStartObject("selection");
                PixelRect box = selection.IsEmpty ? default
                    : selection.Bounds.Enclosing().Intersect(new PixelRect(0, 0, document.Width, document.Height));
                writer.WriteBoolean("empty", box.IsEmpty);
                if (!box.IsEmpty)
                {
                    writer.WriteNumber("x", box.X);
                    writer.WriteNumber("y", box.Y);
                    writer.WriteNumber("width", box.Width);
                    writer.WriteNumber("height", box.Height);
                }
                if (open.Feather > 0) writer.WriteNumber("feather", Math.Round(open.Feather, 2));
                writer.WriteEndObject();
            }
            writer.WriteString("layer_order", "top first; a folder's members follow it, indented by depth");
            writer.WriteStartArray("layers");
            for (int i = document.Layers.Count - 1; i >= 0; i--) WriteLayer(writer, document, document.Layers[i], depth[document.Layers[i].Id]);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteLayer(Utf8JsonWriter writer, CanvasDocument document, ImageLayer layer, int depth)
    {
        writer.WriteStartObject();
        writer.WriteString("id", layer.Id.ToString());
        writer.WriteString("name", layer.Name);
        writer.WriteString("kind", layer.IsGroup ? "group" : layer.Adjustment is not null ? "adjustment"
            : layer.IsLiveText ? "text" : layer.Image is null ? "empty" : "pixels");
        writer.WriteNumber("depth", depth);
        if (layer.ParentId is Guid parent) writer.WriteString("parent", parent.ToString());
        writer.WriteBoolean("visible", layer.IsVisible);
        writer.WriteNumber("opacity", Math.Round(layer.Opacity, 3));
        writer.WriteString("blend_mode", BlendName(layer.BlendMode));

        if (!layer.IsGroup && layer.Adjustment is null && layer.Image is not null)
        {
            PixelRect bounds = LayerGeometry.Bounds(layer.Transform);
            writer.WriteStartObject("bounds");
            writer.WriteNumber("x", bounds.X);
            writer.WriteNumber("y", bounds.Y);
            writer.WriteNumber("width", bounds.Width);
            writer.WriteNumber("height", bounds.Height);
            writer.WriteEndObject();
            writer.WriteStartObject("placement");
            writer.WriteNumber("x", Math.Round(layer.Transform.Origin.X, 2));
            writer.WriteNumber("y", Math.Round(layer.Transform.Origin.Y, 2));
            writer.WriteNumber("width", Math.Round(layer.Transform.Size.Width, 2));
            writer.WriteNumber("height", Math.Round(layer.Transform.Size.Height, 2));
            if (layer.Transform.Rotation != 0) writer.WriteNumber("rotation", Math.Round(layer.Transform.Rotation, 2));
            if (layer.Transform.FlipX) writer.WriteBoolean("flip_x", true);
            if (layer.Transform.FlipY) writer.WriteBoolean("flip_y", true);
            writer.WriteEndObject();
        }

        if (layer.MaskSourceId is Guid source)
            writer.WriteString("clipped_to", document.Layer(source)?.Name is string name ? $"{name} ({source})" : source.ToString());
        if (layer.Mask is LayerMask mask)
        {
            writer.WriteStartObject("mask");
            writer.WriteBoolean("enabled", mask.IsEnabled);
            writer.WriteEndObject();
        }

        if (layer.Text is LayerText text && layer.IsLiveText)
        {
            writer.WriteStartObject("text");
            writer.WriteString("text", text.Text);
            writer.WriteString("font", text.Font);
            writer.WriteNumber("size", text.Size);
            writer.WriteNumber("weight", text.Weight);
            if (text.Italic) writer.WriteBoolean("italic", true);
            writer.WriteString("color", ToolArguments.Hex(text.Colour));
            writer.WriteString("align", text.Align.ToString().ToLowerInvariant());
            if (text.Tracking != 0) writer.WriteNumber("tracking", text.Tracking);
            writer.WriteNumber("leading", text.Leading);
            if (text.Warp is TextWarp warp && !warp.IsIdentity)
            {
                writer.WriteStartObject("warp");
                writer.WriteString("style", WarpName(warp.Style));
                writer.WriteNumber("bend", warp.Bend);
                writer.WriteNumber("horizontal", warp.Horizontal);
                writer.WriteNumber("vertical", warp.Vertical);
                writer.WriteEndObject();
            }
            Point anchor = LayerGeometry.ToDocument(layer.Transform, new Point(text.AnchorX, text.AnchorY), layer.Image!.Width, layer.Image.Height);
            writer.WriteStartObject("anchor");
            writer.WriteNumber("x", Math.Round(anchor.X, 1));
            writer.WriteNumber("y", Math.Round(anchor.Y, 1));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if (layer.Adjustment is LayerAdjustment adjustment && !layer.IsGroup)
        {
            writer.WriteString("adjustment", AdjustmentName(adjustment.Kind));
            writer.WritePropertyName("settings");
            WriteAdjustment(writer, adjustment);
        }

        if (layer.Effects is LayerEffects effects)
        {
            writer.WriteStartObject("effects");
            if (effects.Shadow is ShadowEffect shadow)
                writer.WriteString("drop_shadow", $"{(shadow.Enabled ? "on" : "off")}, {Hex(shadow.Red, shadow.Green, shadow.Blue)}, opacity {shadow.Opacity:0.##}, angle {shadow.Angle:0}, distance {shadow.Distance:0.#}, size {shadow.Size:0.#}");
            if (effects.Glow is GlowEffect glow)
                writer.WriteString("outer_glow", $"{(glow.Enabled ? "on" : "off")}, {Hex(glow.Red, glow.Green, glow.Blue)}, opacity {glow.Opacity:0.##}, size {glow.Size:0.#}");
            if (effects.Stroke is StrokeEffect stroke)
                writer.WriteString("stroke", $"{(stroke.Enabled ? "on" : "off")}, {Hex(stroke.Red, stroke.Green, stroke.Blue)}, size {stroke.Size:0.#}, {stroke.Position.ToString().ToLowerInvariant()}");
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    /// <summary>An adjustment's settings as JSON, in the names add_adjustment and edit_adjustment take.</summary>
    private static void WriteAdjustment(Utf8JsonWriter writer, LayerAdjustment adjustment)
    {
        writer.WriteStartObject();
        switch (adjustment.Kind)
        {
            case AdjustmentKind.Levels:
            {
                string[] names = ["", "red", "green", "blue"];
                for (int i = 0; i < Math.Min(4, adjustment.Levels.Ranges.Count); i++)
                {
                    LevelRange range = adjustment.Levels.Ranges[i];
                    if (i > 0)
                    {
                        if (range == new LevelRange()) continue;
                        writer.WriteStartObject(names[i]);
                    }
                    writer.WriteNumber("black", Math.Round(range.Black, 2));
                    writer.WriteNumber("white", Math.Round(range.White, 2));
                    writer.WriteNumber("gamma", Math.Round(range.Gamma, 3));
                    writer.WriteNumber("output_black", Math.Round(range.OutputBlack, 2));
                    writer.WriteNumber("output_white", Math.Round(range.OutputWhite, 2));
                    if (i > 0) writer.WriteEndObject();
                }
                break;
            }
            case AdjustmentKind.Curves:
            {
                string[] names = ["points", "red_points", "green_points", "blue_points"];
                for (int i = 0; i < Math.Min(4, adjustment.Curves.Channels.Count); i++)
                {
                    EquatableList<CurvePoint> points = adjustment.Curves.Channels[i];
                    if (i > 0 && points.Count == 2 && points[0] == new CurvePoint(0, 0) && points[1] == new CurvePoint(255, 255)) continue;
                    writer.WriteStartArray(names[i]);
                    foreach (CurvePoint point in points)
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue(Math.Round(point.X, 2));
                        writer.WriteNumberValue(Math.Round(point.Y, 2));
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                }
                break;
            }
            case AdjustmentKind.Hsv:
            {
                HueSaturationSettings hsv = adjustment.ResolvedHsv;
                RangeAdjustment master = hsv.Adjustments.ValueOr(ColorRange.Master, new RangeAdjustment());
                writer.WriteNumber("hue", Math.Round(master.Hue, 2));
                writer.WriteNumber("saturation", Math.Round(master.Saturation, 2));
                writer.WriteNumber("lightness", Math.Round(master.Lightness, 2));
                writer.WriteBoolean("colorize", hsv.Colorize);
                bool started = false;
                foreach (ColorRange range in ColorRanges.Colors)
                {
                    if (hsv.Adjustments.ValueOr(range, new RangeAdjustment()) is not { } one
                        || (one.Hue == 0 && one.Saturation == 0 && one.Lightness == 0)) continue;
                    if (!started) writer.WriteStartObject("ranges");
                    started = true;
                    writer.WriteStartObject(range.ToString().ToLowerInvariant());
                    writer.WriteNumber("hue", Math.Round(one.Hue, 2));
                    writer.WriteNumber("saturation", Math.Round(one.Saturation, 2));
                    writer.WriteNumber("lightness", Math.Round(one.Lightness, 2));
                    writer.WriteEndObject();
                }
                if (started) writer.WriteEndObject();
                break;
            }
            case AdjustmentKind.Exposure:
                writer.WriteNumber("exposure", Math.Round(adjustment.Exposure.Exposure, 3));
                writer.WriteNumber("offset", Math.Round(adjustment.Exposure.Offset, 4));
                writer.WriteNumber("gamma", Math.Round(adjustment.Exposure.Gamma, 3));
                break;
            case AdjustmentKind.GradientMap:
                writer.WriteString("shadows", Hex(adjustment.GradientMap.Shadows.Red, adjustment.GradientMap.Shadows.Green, adjustment.GradientMap.Shadows.Blue));
                writer.WriteString("highlights", Hex(adjustment.GradientMap.Highlights.Red, adjustment.GradientMap.Highlights.Green, adjustment.GradientMap.Highlights.Blue));
                writer.WriteBoolean("reversed", adjustment.GradientMap.Reversed);
                break;
            default:
                writer.WriteNumber("amount", Math.Round(adjustment.Grain.Amount, 2));
                writer.WriteNumber("size", Math.Round(adjustment.Grain.Size, 3));
                writer.WriteNumber("roughness", Math.Round(adjustment.Grain.Roughness, 2));
                writer.WriteNumber("seed", adjustment.Grain.Seed);
                break;
        }
        writer.WriteEndObject();
    }

    /// <summary><see cref="WriteAdjustment"/> on one line, for an answer.</summary>
    private static string AdjustmentSummary(LayerAdjustment adjustment)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteAdjustment(writer, adjustment);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Hex(double red, double green, double blue) =>
        ToolArguments.Hex(new Rgba(Channel(red), Channel(green), Channel(blue)));

    private static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);

    private static string AdjustmentName(AdjustmentKind kind) => kind switch
    {
        AdjustmentKind.Hsv => "hue_saturation",
        AdjustmentKind.Levels => "levels",
        AdjustmentKind.Curves => "curves",
        AdjustmentKind.Exposure => "exposure",
        AdjustmentKind.GradientMap => "gradient_map",
        _ => "grain",
    };

    private static string WarpName(TextWarpStyle style)
    {
        var name = new StringBuilder();
        foreach (char c in style.ToString())
        {
            if (char.IsUpper(c) && name.Length > 0) name.Append('_');
            name.Append(char.ToLowerInvariant(c));
        }
        return name.ToString();
    }
}
