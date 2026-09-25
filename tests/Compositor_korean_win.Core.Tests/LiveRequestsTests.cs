using System.Text.Json;
using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>The open window's pipe requests: the short form a shell sends, and JSON-RPC passed through.</summary>
public sealed class LiveRequestsTests : IDisposable
{
    private sealed class TestServices() : BasicEditorServices(new BoxGlyphs())
    {
        public override IReadOnlyList<string> FontFamilies() => ["Box Sans"];
    }

    private readonly string _images = Path.Combine(Path.GetTempPath(), "compositor-live-" + Guid.NewGuid().ToString("N"));
    private readonly EditorSession _session = new(new TestServices());
    private readonly McpServer _server;

    public LiveRequestsTests() => _server = new McpServer(new McpTools(_session), "test");

    public void Dispose()
    {
        _session.Dispose();
        try { Directory.Delete(_images, recursive: true); } catch (IOException) { }
    }

    private JsonDocument Send(string line) => JsonDocument.Parse(LiveRequests.Handle(_server, line, _images));

    [Fact]
    public void AShortRequestCallsTheToolAndAnswersInPlainFields()
    {
        using (JsonDocument made = Send("""{"tool":"new_document","arguments":{"width":200,"height":100}}"""))
            Assert.True(made.RootElement.GetProperty("ok").GetBoolean());

        using JsonDocument added = Send("""{"tool":"add_text","arguments":{"text":"안녕","x":10,"y":60,"size":40}}""");
        Assert.True(added.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("안녕", added.RootElement.GetProperty("text").GetString());
        Assert.Equal(0, added.RootElement.GetProperty("images").GetArrayLength());

        Assert.Single(_session.Current!.Document.Layers, layer => layer.Text?.Text == "안녕");
    }

    [Fact]
    public void ARenderIsAnsweredWithTheFileItWasWrittenTo()
    {
        Send("""{"tool":"new_document","arguments":{"width":64,"height":32,"background":"#FF0000"}}""").Dispose();

        using JsonDocument rendered = Send("""{"tool":"render"}""");
        string path = rendered.RootElement.GetProperty("images")[0].GetString()!;
        Assert.True(File.Exists(path));
        byte[] png = File.ReadAllBytes(path);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
    }

    [Fact]
    public void AFailingToolSaysSoWithItsReason()
    {
        using JsonDocument failed = Send("""{"tool":"get_document"}""");
        Assert.False(failed.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("No document is open", failed.RootElement.GetProperty("text").GetString());

        using JsonDocument malformed = JsonDocument.Parse(LiveRequests.Handle(_server, "not json", _images));
        Assert.False(malformed.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void TheGuideExplainsTheCallAndListsEveryTool()
    {
        using JsonDocument guide = Send("""{"tool":"guide"}""");
        string text = guide.RootElement.GetProperty("text").GetString()!;

        Assert.Contains(LiveRequests.PipeName, text);
        Assert.Contains("NamedPipeClientStream", text);
        foreach (string tool in new[] { "new_document", "add_text", "edit_text", "render", "save_document" })
            Assert.Contains("- " + tool + ":", text);
        Assert.Contains("start (integer)", text);
    }

    [Fact]
    public void ThePersonsRulesComeWithTheGuideAndTheInstructionsAndFollowTheFile()
    {
        string path = Path.Combine(_images, "ai-rules.md");
        var rules = new AiRules(path, () => "Default rule: group by role.");
        var server = new McpServer(new McpTools(_session), "test", rules);

        using (JsonDocument guide = JsonDocument.Parse(LiveRequests.Handle(server, """{"tool":"guide"}""", _images)))
            Assert.Contains("Default rule: group by role.", guide.RootElement.GetProperty("text").GetString());

        Assert.True(rules.Ensure());
        File.WriteAllText(path, "Name every layer in Korean.");
        using (JsonDocument guide = JsonDocument.Parse(LiveRequests.Handle(server, """{"tool":"guide"}""", _images)))
            Assert.Contains("Name every layer in Korean.", guide.RootElement.GetProperty("text").GetString());

        using JsonDocument initialize = JsonDocument.Parse(server.Handle(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""")!);
        Assert.Contains("Name every layer in Korean.",
                        initialize.RootElement.GetProperty("result").GetProperty("instructions").GetString());

        // An empty file is no rules at all, which falls back to the defaults rather than to nothing.
        File.WriteAllText(path, "   ");
        Assert.Equal("Default rule: group by role.", rules.Text);
    }

    [Fact]
    public void JsonRpcIsPassedThroughAndANotificationGetsAnEmptyLine()
    {
        string reply = LiveRequests.Handle(_server, """{"jsonrpc":"2.0","id":7,"method":"tools/list"}""", _images);
        using JsonDocument listed = JsonDocument.Parse(reply);
        Assert.Equal(7, listed.RootElement.GetProperty("id").GetInt32());
        Assert.True(listed.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() > 20);

        Assert.Equal("", LiveRequests.Handle(_server, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", _images));
    }

    [Fact]
    public void TheToolALineCallsIsFoundInEitherForm()
    {
        Assert.Equal(("undo", (int?)3), LiveRequests.Called("""{"tool":"undo","arguments":{"steps":3}}"""));
        Assert.Equal(("redo", (int?)null),
                     LiveRequests.Called("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"redo"}}"""));
        Assert.Null(LiveRequests.Called("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));

        using JsonDocument rpc = JsonDocument.Parse(LiveRequests.Answer("""{"jsonrpc":"2.0","id":"a","method":"tools/call","params":{"name":"undo"}}""",
                                                                        "Undid 1 step(s).", isError: false));
        Assert.Equal("a", rpc.RootElement.GetProperty("id").GetString());
        Assert.Equal("Undid 1 step(s).", rpc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

        using JsonDocument simple = JsonDocument.Parse(LiveRequests.Answer("""{"tool":"undo"}""", "Nothing to undo.", isError: false));
        Assert.True(simple.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void ASessionThatDoesNotOwnItsPixelsLeavesThemToTheWindow()
    {
        PixelBuffer pixels = RenderFixture.Solid(8, 8, 10, 20, 30);
        CanvasDocument document = ProjectFixture.Document(ProjectFixture.Layer("Photo", pixels, Guid.NewGuid()));
        try
        {
            using (var borrowed = new EditorSession(new TestServices(), ownsPixels: false))
            {
                var server = new McpServer(new McpTools(borrowed), "test");
                EditorSession.Open open = borrowed.Add(document, "window", path: null);
                using JsonDocument deleted = JsonDocument.Parse(LiveRequests.Handle(server,
                    """{"tool":"delete_layers","arguments":{"layers":["Photo"]}}""", _images));
                Assert.True(deleted.RootElement.GetProperty("ok").GetBoolean(), deleted.RootElement.GetProperty("text").GetString());
                Assert.Empty(open.Document.Layers);
            }

            // Closing the session freed nothing of the window's.
            Assert.True(pixels.ReferenceCount > 0);
            Assert.Equal(10, pixels.Row(0)[0]);
        }
        finally
        {
            foreach (ImageLayer layer in document.Layers) layer.Image?.Release();
        }
    }
}
