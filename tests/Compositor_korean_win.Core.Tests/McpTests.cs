using System.Text.Json;
using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// The MCP server driven the way a model drives it: JSON-RPC messages in, JSON-RPC messages out.
/// </summary>
public sealed class McpTests : IDisposable
{
    private sealed class TestServices(IImageGenerator? generator = null) : BasicEditorServices(new BoxGlyphs())
    {
        public override IReadOnlyList<string> FontFamilies() => ["Box Sans", "Box Serif"];
        public override IImageGenerator? ImageGenerator => generator;
    }

    /// <summary>Stands in for Codex: a picture of the size asked for, one colour.</summary>
    private sealed class FakeGenerator : IImageGenerator
    {
        public string Name => "fake";
        public string? LastPrompt { get; private set; }

        public byte[] Generate(string prompt, int width, int height)
        {
            LastPrompt = prompt;
            using PixelBuffer pixels = RenderFixture.Solid(width, height, 180, 120, 90);
            return Png.Encode(pixels);
        }
    }

    private readonly int _liveBefore;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "compositor-mcp-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGenerator _generator = new();
    private readonly EditorSession _session;
    private readonly McpServer _server;
    private int _id;

    public McpTests()
    {
        EffectRendering.ClearCache();
        _liveBefore = PixelBuffer.LiveCount;
        Directory.CreateDirectory(_folder);
        _session = new EditorSession(new TestServices(_generator));
        _server = new McpServer(new McpTools(_session), "test");
    }

    public void Dispose()
    {
        _session.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    // ---- Protocol ----------------------------------------------------------------------------

    [Fact]
    public void TheHandshakeNamesTheServerAndItsTools()
    {
        using JsonDocument initialize = Request("initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}""");
        JsonElement result = initialize.RootElement.GetProperty("result");
        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("compositor", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.Contains("render", result.GetProperty("instructions").GetString());

        Assert.Null(_server.Handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));

        using JsonDocument list = Request("tools/list", "{}");
        string[] names = [.. list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                                 .Select(tool => tool.GetProperty("name").GetString()!)];
        foreach (string expected in new[]
                 {
                     "new_document", "open_document", "save_document", "export_image", "get_document", "render", "undo", "redo",
                     "add_image", "add_text", "edit_text", "add_shape", "add_gradient", "update_layer", "arrange_layer",
                     "delete_layers", "group_layers", "set_clipping", "set_mask", "set_effects", "add_adjustment",
                     "apply_filter", "remove_background", "generate_image", "list_fonts",
                 })
            Assert.Contains(expected, names);

        foreach (JsonElement tool in list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()));
        }
    }

    [Fact]
    public void MalformedAndUnknownRequestsAreProtocolErrors()
    {
        using JsonDocument unknown = Request("no/such/method", "{}");
        Assert.Equal(-32601, unknown.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        using JsonDocument broken = JsonDocument.Parse(_server.Handle("{not json")!);
        Assert.Equal(-32700, broken.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void AFailedToolSaysWhyAndTheSessionGoesOn()
    {
        JsonElement noDocument = Call("add_text", """{"text":"hi","x":1,"y":1}""", expectError: true);
        Assert.Contains("new_document", Text(noDocument));

        Call("new_document", """{"width":100,"height":80}""");
        JsonElement noLayer = Call("update_layer", """{"layer":"nothing here","visible":false}""", expectError: true);
        Assert.Contains("get_document", Text(noLayer));

        JsonElement missing = Call("add_shape", """{"kind":"star"}""", expectError: true);
        Assert.Contains("'x' is required", Text(missing));

        Call("add_shape", """{"kind":"rectangle","x":10,"y":10,"width":20,"height":20,"fill":"#FF0000"}""");
    }

    // ---- Making a poster ----------------------------------------------------------------------

    [Fact]
    public void APosterIsBuiltFromLayersAndLooksRight()
    {
        Call("new_document", """{"width":300,"height":400,"background":"#000000","name":"poster"}""");

        string panel = Id(Call("add_shape", """{"kind":"rectangle","x":40,"y":60,"width":220,"height":160,"fill":"#FFFFFF","name":"panel"}"""));
        string gradient = Id(Call("add_gradient", """{"start":{"x":40,"y":60},"end":{"x":260,"y":220},"from":"#F5E400","to":"#2BB673","name":"panel colour"}"""));
        Call("set_clipping", $$"""{"layer":"{{gradient}}"}""");

        string title = Id(Call("add_text", """
            {"text":"BRUTALISM","x":150,"y":300,"size":40,"align":"center","color":"#FFFFFF","tracking":50,
             "warp":{"style":"arc","bend":30},"name":"title"}
            """));
        Call("set_mask", $$$"""{"layer":"{{{title}}}","shape":"linear_gradient","start":{"x":0,"y":260},"end":{"x":0,"y":320}}""");
        Call("set_effects", $$$"""{"layer":"{{{title}}}","drop_shadow":{"distance":4,"size":3,"opacity":0.6},"stroke":{"size":2,"color":"#FF00FF"}}""");

        Call("add_text", """{"text":"FREE ENTRY","x":20,"y":380,"size":14,"color":"#FFFFFF","rotation":-90}""");
        Call("add_shape", """{"kind":"star","x":230,"y":20,"width":50,"height":50,"sides":4,"inset":0.15,"curved":true,"fill":"#FFFFFF"}""");
        Call("add_shape", """{"kind":"star","x":20,"y":230,"width":30,"height":30,"sides":4,"inset":0.2,"fill":"none","stroke":"#FFFFFF","stroke_width":2}""");
        Call("add_shape", """{"kind":"line","x1":20,"y1":240,"x2":280,"y2":240,"color":"#FFFFFF","line_width":1.5}""");

        using (PixelBuffer photo = RenderFixture.Solid(60, 80, 200, 80, 60))
        {
            string data = Convert.ToBase64String(Png.Encode(photo));
            string person = Id(Call("add_image", $$"""{"data":"{{data}}","x":120,"y":100,"width":60,"name":"person"}"""));
            Call("add_adjustment", $$"""{"kind":"hue_saturation","saturation":-100,"above":"{{person}}","clip":true}""");
        }

        // The picture: the panel is coloured by the clipped gradient, and the black background is left alone.
        JsonElement look = Call("render", """{"background":"white"}""");
        using PixelBuffer shown = Png.Decode(Image(look));
        Assert.Equal(300, shown.Width);
        Assert.Equal(400, shown.Height);
        (byte r, byte g, byte b) = Pixel(shown, 45, 65);
        Assert.True(r > 200 && g > 180 && b < 80, $"the panel's top-left should be yellow, was {r},{g},{b}");
        (r, g, b) = Pixel(shown, 255, 215);
        Assert.True(g > r && g > b, $"the panel's bottom-right should be green, was {r},{g},{b}");
        (r, g, b) = Pixel(shown, 5, 5);
        Assert.True(r < 10 && g < 10 && b < 10, "the background should stay black");
        (r, g, b) = Pixel(shown, 150, 150);
        Assert.True(Math.Abs(r - g) < 6 && Math.Abs(g - b) < 6, $"the clipped Hue/Saturation should turn the photo grey, was {r},{g},{b}");

        // The layers are what a person would have made, and say so.
        string described = Text(Call("get_document", "{}"));
        Assert.Contains("\"kind\": \"text\"", described);
        Assert.Contains("\"style\": \"arc\"", described);
        Assert.Contains("\"clipped_to\": \"panel", described);
        Assert.Contains("\"adjustment\": \"hue_saturation\"", described);
        Assert.Contains("drop_shadow", described);

        // A render of one layer, scaled down.
        JsonElement titleOnly = Call("render", $$"""{"layer":"{{title}}","max_size":100}""");
        using (PixelBuffer small = Png.Decode(Image(titleOnly))) Assert.Equal(100, Math.Max(small.Width, small.Height));

        // Saved both ways and opened again.
        string psd = Path.Combine(_folder, "poster.psd"), comp = Path.Combine(_folder, "poster.comp"), png = Path.Combine(_folder, "poster.png");
        Call("save_document", $$"""{"path":{{JsonSerializer.Serialize(psd)}}}""");
        Call("save_document", $$"""{"path":{{JsonSerializer.Serialize(comp)}}}""");
        Call("export_image", $$"""{"path":{{JsonSerializer.Serialize(png)}}}""");
        Assert.True(File.Exists(psd) && File.Exists(comp) && File.Exists(png));

        int layers = _session.Current!.Document.Layers.Count;
        Call("open_document", $$"""{"path":{{JsonSerializer.Serialize(comp)}}}""");
        Assert.Equal(layers, _session.Current!.Document.Layers.Count);
        Assert.Contains(_session.Current.Document.Layers, layer => layer.IsLiveText && layer.Name == "title");
        Call("open_document", $$"""{"path":{{JsonSerializer.Serialize(psd)}}}""");
        Assert.Equal(layers, _session.Current!.Document.Layers.Count);
        Assert.Equal(3, _session.Documents.Count);
    }

    [Fact]
    public void TextCanBeReworded()
    {
        Call("new_document", """{"width":200,"height":100}""");
        string id = Id(Call("add_text", """{"text":"one","x":10,"y":50,"size":20}"""));
        ImageLayer before = _session.Current!.Document.Layer(Guid.Parse(id))!;

        Call("edit_text", $$"""{"layer":"{{id}}","text":"three words here","color":"#FF0000","bold":true}""");
        ImageLayer after = _session.Current!.Document.Layer(Guid.Parse(id))!;
        Assert.True(after.IsLiveText);
        Assert.Equal("three words here", after.Text!.Text);
        Assert.Equal(700, after.Text.Weight);
        Assert.True(after.Image!.Width > before.Image!.Width);

        // The anchor stays where it was set.
        Point anchorBefore = LayerGeometry.ToDocument(before.Transform, new Point(before.Text!.AnchorX, before.Text.AnchorY), before.Image.Width, before.Image.Height);
        Point anchorAfter = LayerGeometry.ToDocument(after.Transform, new Point(after.Text.AnchorX, after.Text.AnchorY), after.Image.Width, after.Image.Height);
        Assert.Equal(anchorBefore.X, anchorAfter.X, 3);
        Assert.Equal(anchorBefore.Y, anchorAfter.Y, 3);
    }

    [Fact]
    public void UndoAndRedoWalkTheHistory()
    {
        Call("new_document", """{"width":50,"height":50}""");
        Call("add_layer", "{}");
        Call("add_shape", """{"kind":"ellipse","x":5,"y":5,"width":20,"height":20}""");
        Assert.Equal(3, _session.Current!.Document.Layers.Count);

        Call("undo", """{"steps":2}""");
        Assert.Single(_session.Current!.Document.Layers);
        Call("redo", "{}");
        Assert.Equal(2, _session.Current!.Document.Layers.Count);
        Assert.Contains("Nothing to redo", Text(Call("redo", """{"steps":5}""")) + Text(Call("redo", "{}")));
    }

    [Fact]
    public void LayersAreFoundByNameAndArranged()
    {
        Call("new_document", """{"width":50,"height":50,"background":"transparent"}""");
        Call("add_shape", """{"kind":"rectangle","x":0,"y":0,"width":10,"height":10,"name":"red","fill":"#FF0000"}""");
        Call("add_shape", """{"kind":"rectangle","x":0,"y":0,"width":10,"height":10,"name":"blue","fill":"#0000FF"}""");

        Call("arrange_layer", """{"layer":"red","to":"top"}""");
        Assert.Equal("red", _session.Current!.Document.Layers[^1].Name);

        Call("group_layers", """{"layers":["red","blue"],"name":"pair"}""");
        ImageLayer pair = EditorSession.Layer(_session.Current!.Document, "pair");
        Assert.True(pair.IsGroup);
        Assert.Equal(pair.Id, EditorSession.Layer(_session.Current!.Document, "red").ParentId);

        Call("update_layer", """{"layer":"pair","x":20,"y":30}""");
        Assert.Equal(20, EditorSession.Layer(_session.Current!.Document, "red").Transform.Origin.X, 3);
        Call("ungroup", """{"layer":"pair"}""");
        Assert.Null(EditorSession.Layer(_session.Current!.Document, "red").ParentId);
    }

    [Fact]
    public void AGeneratedPictureBecomesALayer()
    {
        Call("new_document", """{"width":400,"height":300}""");
        JsonElement result = Call("generate_image", """{"prompt":"a portrait, black and white","width":512,"height":512,"fit":"contain"}""");
        Assert.Contains("fake", Text(result));
        Assert.Equal("a portrait, black and white", _generator.LastPrompt);
        ImageLayer layer = _session.Current!.Document.Layers[^1];
        Assert.Equal(300, layer.Transform.Size.Height, 3);
    }

    [Fact]
    public void GeneratorsAreFilledInAndSizedAsTheyExpect()
    {
        string line = CommandImageGenerator.Fill("make \"{prompt}\" -o {output} {width}x{height}", "a \"red\"\ndress", "C:\\x.png", 640, 480);
        Assert.Equal("make \"a 'red' dress\" -o C:\\x.png 640x480", line);
        Assert.Contains("$imagegen", ImageGenerators.CodexTemplate);
        Assert.Equal("1024x1536", OpenAiImageGenerator.SizeFor(600, 900));
        Assert.Equal("1536x1024", OpenAiImageGenerator.SizeFor(900, 600));
        Assert.Equal("1024x1024", OpenAiImageGenerator.SizeFor(500, 500));
    }

    [Fact]
    public void ClosingEverythingFreesEveryPixel()
    {
        Call("new_document", """{"width":120,"height":90}""");
        string title = Id(Call("add_text", """{"text":"leak","x":10,"y":50,"size":30}"""));
        Call("edit_text", $$"""{"layer":"{{title}}","text":"no leaks"}""");
        Call("set_effects", $$$"""{"layer":"{{{title}}}","outer_glow":{"size":6}}""");
        Call("render", "{}");
        Call("apply_filter", $$"""{"layer":"{{title}}","filter":"gaussian_blur","radius":2}""");
        Call("undo", "{}");
        Call("set_mask", $$"""{"layer":"{{title}}","shape":"ellipse","x":0,"y":0,"width":60,"height":60}""");
        Call("close_document", "{}");

        EffectRendering.ClearCache();
        Assert.Equal(_liveBefore, PixelBuffer.LiveCount);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private JsonDocument Request(string method, string parameters)
    {
        string reply = _server.Handle($$"""{"jsonrpc":"2.0","id":{{++_id}},"method":"{{method}}","params":{{parameters}}}""")!;
        return JsonDocument.Parse(reply);
    }

    /// <summary>Calls a tool and hands back its result, asserting it did or did not fail.</summary>
    private JsonElement Call(string tool, string arguments, bool expectError = false)
    {
        using JsonDocument reply = Request("tools/call", $$"""{"name":"{{tool}}","arguments":{{arguments}}}""");
        Assert.False(reply.RootElement.TryGetProperty("error", out JsonElement error), $"{tool}: protocol error {error}");
        JsonElement result = reply.RootElement.GetProperty("result").Clone();
        bool failed = result.GetProperty("isError").GetBoolean();
        Assert.True(failed == expectError, $"{tool} {(failed ? "failed" : "succeeded")}: {Text(result)}");
        return result;
    }

    private static string Text(JsonElement result) =>
        string.Join("\n", result.GetProperty("content").EnumerateArray()
            .Where(part => part.GetProperty("type").GetString() == "text")
            .Select(part => part.GetProperty("text").GetString()));

    private static byte[] Image(JsonElement result) =>
        result.GetProperty("content").EnumerateArray()
            .First(part => part.GetProperty("type").GetString() == "image")
            .GetProperty("data").GetBytesFromBase64();

    /// <summary>The first layer id a tool's answer mentions.</summary>
    private static string Id(JsonElement result)
    {
        string text = Text(result);
        foreach (string word in text.Split([' ', '\'', '.', ',', '\n'], StringSplitOptions.RemoveEmptyEntries))
            if (Guid.TryParse(word, out _)) return word;
        throw new InvalidOperationException("no id in: " + text);
    }

    private static (byte, byte, byte) Pixel(PixelBuffer buffer, int x, int y)
    {
        ReadOnlySpan<byte> row = buffer.Row(y);
        return (row[x * 4], row[x * 4 + 1], row[x * 4 + 2]);
    }
}
