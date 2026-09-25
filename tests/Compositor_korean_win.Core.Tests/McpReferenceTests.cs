using System.Text.Json;
using System.Text.RegularExpressions;
using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// Working from a reference: exact pixels, free paths, comparing, and the guide that says how.
/// </summary>
public sealed partial class McpReferenceTests : IDisposable
{
    private sealed class TestServices() : BasicEditorServices(new BoxGlyphs())
    {
        public override IReadOnlyList<string> FontFamilies() => ["Box Sans"];
    }

    private readonly EditorSession _session;
    private readonly McpServer _server;
    private readonly McpTools _tools;
    private readonly List<PixelBuffer> _looks = [];
    private int _id;

    public McpReferenceTests()
    {
        _session = new EditorSession(new TestServices());
        _tools = new McpTools(_session);
        _server = new McpServer(_tools, "test");
    }

    public void Dispose()
    {
        _session.Dispose();
        foreach (PixelBuffer look in _looks) look.Release();
    }

    [GeneratedRegex("`([a-z_]+)")]
    private static partial Regex Quoted();

    // ---- put_pixels ----------------------------------------------------------------------------

    [Fact]
    public void PutPixelsCarriesEveryColourExactly()
    {
        Call("new_document", """{"width":60,"height":40,"background":"transparent"}""");
        using PixelBuffer picture = Varied(60, 40);
        string data = Convert.ToBase64String(Png.Encode(picture));

        // A part of the picture as a layer of its own, placed at the canvas's corner.
        string part = Id(Call("put_pixels", $$"""{"data":"{{data}}","region":{"x":10,"y":5,"width":30,"height":20},"x":0,"y":0,"name":"part"}"""));
        PixelBuffer shown = Look($$"""{"layers":["{{part}}"],"background":"transparent"}""");
        Assert.Equal(Colour(picture, 13, 9), Colour(shown, 3, 4));
        Assert.Equal(Colour(picture, 39, 24), Colour(shown, 29, 19));
        Assert.Equal(0, shown.Row(30)[40 * 4 + 3]);

        // Into a blank layer, kept to the selection.
        Call("add_layer", """{"name":"target"}""");
        Call("select", """{"shape":"rectangle","x":0,"y":0,"width":20,"height":40}""");
        Call("put_pixels", $$"""{"data":"{{data}}","layer":"target","within_selection":true}""");
        Call("select", """{"shape":"none"}""");
        shown = Look("""{"layers":["target"],"background":"transparent"}""");
        Assert.Equal(Colour(picture, 5, 5), Colour(shown, 5, 5));
        Assert.Equal(Colour(picture, 19, 30), Colour(shown, 19, 30));
        Assert.Equal(0, shown.Row(5)[30 * 4 + 3]);
    }

    [Fact]
    public void AnElementWrittenAsATransparentPngKeepsItsOutline()
    {
        // The guide's way of carrying an element: a PNG with the element's pixels and nothing around them.
        Call("new_document", """{"width":40,"height":40,"background":"#FFFFFF"}""");
        using PixelBuffer element = PixelBuffer.Allocate(20, 20);
        for (int y = 0; y < 20; y++)
        {
            Span<byte> row = element.Row(y);
            for (int x = 0; x < 10; x++)
            {
                row[x * 4] = 200;
                row[x * 4 + 1] = 30;
                row[x * 4 + 2] = 90;
                row[x * 4 + 3] = 255;
            }
        }
        Call("put_pixels", $$"""{"data":"{{Convert.ToBase64String(Png.Encode(element))}}","x":10,"y":10,"name":"element"}""");
        PixelBuffer shown = Look("{}");
        Assert.Equal((200, 30, 90), Pixel(shown, 12, 15));
        Assert.Equal((255, 255, 255), Pixel(shown, 25, 15));
    }

    // ---- add_path ------------------------------------------------------------------------------

    [Fact]
    public void PathsBecomeLayersMasksAndSelections()
    {
        Call("new_document", """{"width":100,"height":100,"background":"#FFFFFF"}""");
        Call("add_path", """{"d":"M 10 10 L 90 10 L 50 90 Z","fill":"#FF0000","name":"triangle"}""");
        PixelBuffer shown = Look("{}");
        Assert.Equal((255, 0, 0), Pixel(shown, 50, 30));
        Assert.Equal((255, 255, 255), Pixel(shown, 10, 80));

        // Curves and an arc close round the middle; relative commands and compact numbers read.
        Call("delete_layers", """{"layers":["triangle"]}""");
        Call("add_path", """{"d":"M10,50C10,10 90,10 90,50a40,40 0 0 1-80,0z","fill":"#0000FF","name":"round"}""");
        shown = Look("{}");
        Assert.Equal((0, 0, 255), Pixel(shown, 50, 50));
        Assert.Equal((0, 0, 255), Pixel(shown, 50, 85));
        Assert.Equal((255, 255, 255), Pixel(shown, 5, 5));
        Call("delete_layers", """{"layers":["round"]}""");

        // Two squares the same way round: even-odd leaves the middle open, nonzero fills it.
        const string Squares = "M 10 10 H 90 V 90 H 10 Z M 30 30 H 70 V 70 H 30 Z";
        Call("add_path", $$"""{"d":"{{Squares}}","fill":"#000000","fill_rule":"evenodd","name":"frame"}""");
        shown = Look("{}");
        Assert.Equal((255, 255, 255), Pixel(shown, 50, 50));
        Assert.Equal((0, 0, 0), Pixel(shown, 20, 50));
        Call("delete_layers", """{"layers":["frame"]}""");
        Call("add_path", $$"""{"d":"{{Squares}}","fill":"#000000","name":"solid"}""");
        Assert.Equal((0, 0, 0), Pixel(Look("{}"), 50, 50));
        Call("delete_layers", """{"layers":["solid"]}""");

        // A mask cut by paths: shown inside the square, a gap subtracted through it.
        string panel = Id(Call("add_shape", """{"kind":"rectangle","x":0,"y":0,"width":100,"height":100,"fill":"#000000"}"""));
        Call("add_path", $$"""{"as":"mask","layer":"{{panel}}","d":"M 20 20 H 80 V 80 H 20 Z"}""");
        Call("add_path", $$"""{"as":"mask","layer":"{{panel}}","mode":"subtract","points":[[45,0],[55,0],[55,100],[45,100]]}""");
        shown = Look("{}");
        Assert.Equal((0, 0, 0), Pixel(shown, 30, 50));
        Assert.Equal((255, 255, 255), Pixel(shown, 50, 50));
        Assert.Equal((255, 255, 255), Pixel(shown, 10, 50));

        // A selection from a path, filled.
        Call("add_layer", """{"name":"fill"}""");
        Assert.Contains("Selected", Text(Call("add_path", """{"as":"selection","d":"M 0 0 H 10 V 10 H 0 Z"}""")));
        Call("fill_selection", """{"layer":"fill","color":"#00FF00"}""");
        Assert.Equal((0, 255, 0), Pixel(Look("{}"), 5, 5));

        Assert.Contains("not a number", Text(Call("add_path", """{"d":"M 10 x"}""", expectError: true)));
        Assert.Contains("starts with a command", Text(Call("add_path", """{"d":"10 10 L 20 20"}""", expectError: true)));
    }

    // ---- compare_image -------------------------------------------------------------------------

    [Fact]
    public void CompareImageScoresAndLocatesDifferences()
    {
        Call("new_document", """{"width":80,"height":80,"background":"#FFFFFF"}""");
        using PixelBuffer picture = Varied(80, 80);
        string data = Convert.ToBase64String(Png.Encode(picture));
        Call("put_pixels", $$"""{"data":"{{data}}"}""");

        JsonElement same = Call("compare_image", $$"""{"data":"{{data}}"}""");
        Assert.Contains("Similarity 100.0%", Text(same));
        Assert.Contains(same.GetProperty("content").EnumerateArray(), part => part.GetProperty("type").GetString() == "image");

        Call("add_shape", """{"kind":"rectangle","x":60,"y":60,"width":20,"height":20,"fill":"#000000"}""");
        string differs = Text(Call("compare_image", $$"""{"data":"{{data}}","grid":4}"""));
        Assert.DoesNotContain("Similarity 100.0%", differs);
        string worst = differs.Split('\n').First(line => line.StartsWith("- ", StringComparison.Ordinal));
        Assert.StartsWith("- 60, 60", worst);

        // A reference of another size is stretched to the canvas, and says so.
        using PixelBuffer small = Varied(40, 40);
        Assert.Contains("stretched", Text(Call("compare_image", $$"""{"data":"{{Convert.ToBase64String(Png.Encode(small))}}"}""")));
    }

    // ---- The guide ----------------------------------------------------------------------------

    [Fact]
    public void TheGuideGivesStepsForEachJobNamingRealTools()
    {
        Assert.Contains("put_pixels", Text(Call("guide", """{"topic":"reproduce"}""")));
        Assert.Contains("compare_image", Text(Call("guide", "{}")));
        Assert.Contains("reproduce", Text(Call("guide", """{"topic":"sculpt"}""", expectError: true)));

        // Every tool the steps name exists, and both languages name the same ones.
        HashSet<string> tools = [.. _tools.Definitions.Select(tool => tool.Name), "compositor"];
        foreach (TextKey key in new[] { TextKey.AiWorkflow, TextKey.AiTopicReproduce, TextKey.AiTopicDesign, TextKey.AiTopicRetouch })
        {
            HashSet<string> english = Named(Localizer.Text(key, Language.English)), korean = Named(Localizer.Text(key, Language.Korean));
            foreach (string name in english.Concat(korean)) Assert.True(tools.Contains(name), $"{key} names `{name}`, which is not a tool");
            Assert.True(english.SetEquals(korean), $"{key}: English names {string.Join(", ", english.Except(korean))}; Korean names {string.Join(", ", korean.Except(english))}");
        }

        // The shell's guide carries the way of working; a topic asked for over the pipe is the topic.
        string guide = LiveRequests.Guide(_server);
        Assert.Contains(Localizer.Text(TextKey.AiWorkflow).Split('\n')[0], guide);
        string line = LiveRequests.Handle(_server, """{"tool":"guide","arguments":{"topic":"retouch"}}""", Path.GetTempPath());
        using JsonDocument answer = JsonDocument.Parse(line);
        Assert.True(answer.RootElement.GetProperty("ok").GetBoolean());
        Assert.StartsWith(Localizer.Text(TextKey.AiTopicRetouch).Split('\n')[0], answer.RootElement.GetProperty("text").GetString());

        // And a model connecting over MCP reads it in the instructions.
        using JsonDocument initialize = JsonDocument.Parse(_server.Handle(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""")!);
        Assert.Contains("put_pixels", initialize.RootElement.GetProperty("result").GetProperty("instructions").GetString());

        static HashSet<string> Named(string text) => [.. Quoted().Matches(text).Select(match => match.Groups[1].Value)];
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>Every pixel a different opaque colour, so a copy that is off by one shows.</summary>
    private static PixelBuffer Varied(int width, int height)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x * 4] = (byte)(x * 4);
                row[x * 4 + 1] = (byte)(y * 6);
                row[x * 4 + 2] = (byte)((x * 7 + y * 13) % 256);
                row[x * 4 + 3] = 255;
            }
        }
        return buffer;
    }

    private static (int, int, int, int) Colour(PixelBuffer buffer, int x, int y)
    {
        ReadOnlySpan<byte> row = buffer.Row(y);
        return (row[x * 4], row[x * 4 + 1], row[x * 4 + 2], row[x * 4 + 3]);
    }

    private static (int, int, int) Pixel(PixelBuffer buffer, int x, int y)
    {
        ReadOnlySpan<byte> row = buffer.Row(y);
        return (row[x * 4], row[x * 4 + 1], row[x * 4 + 2]);
    }

    private PixelBuffer Look(string arguments)
    {
        PixelBuffer shown = Png.Decode(Image(Call("render", arguments)));
        _looks.Add(shown);
        return shown;
    }

    private JsonElement Call(string tool, string arguments, bool expectError = false)
    {
        string reply = _server.Handle($$$"""{"jsonrpc":"2.0","id":{{{++_id}}},"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}""")!;
        using JsonDocument parsed = JsonDocument.Parse(reply);
        Assert.False(parsed.RootElement.TryGetProperty("error", out JsonElement error), $"{tool}: protocol error {error}");
        JsonElement result = parsed.RootElement.GetProperty("result").Clone();
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

    private static string Id(JsonElement result)
    {
        string text = Text(result);
        foreach (string word in text.Split([' ', '\'', '.', ',', '\n'], StringSplitOptions.RemoveEmptyEntries))
            if (Guid.TryParse(word, out _)) return word;
        throw new InvalidOperationException("no id in: " + text);
    }
}
