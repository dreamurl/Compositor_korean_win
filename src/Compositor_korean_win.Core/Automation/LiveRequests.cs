using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Compositor_korean_win.Core;

/// <summary>
/// Requests to the running editor over its local pipe: the same tools as the MCP server, for a
/// client that has nothing registered — a model that can run a shell command and has read a guide.
/// </summary>
/// <remarks>
/// <para>
/// The point is that nobody has to configure anything. MCP clients only see servers someone added
/// to their settings, and the setting names the exe's path, which breaks when the program moves.
/// The pipe has a fixed name instead, so any client on the machine that can run PowerShell finds
/// the open window by name, wherever it was installed.
/// </para>
/// <para>
/// One line in, one line out. A line is either a JSON-RPC message, passed to the MCP server as it
/// is — that is what <c>--mcp</c> forwards when it finds the window open — or the short form
/// <c>{"tool": "...", "arguments": {...}}</c>, answered as <c>{"ok", "text", "images"}</c>. Images a
/// tool returns are written to files and answered with their paths: a model reading a shell's
/// output can open a file, not a page of base64.
/// </para>
/// </remarks>
public static class LiveRequests
{
    /// <summary>The pipe the running editor listens on: <c>\\.\pipe\compositor-korean-win</c>.</summary>
    public const string PipeName = "compositor-korean-win";

    /// <summary>The tool that answers with this guide rather than going to the MCP server.</summary>
    public const string GuideTool = "guide";

    /// <summary>
    /// The answer to one line, or an empty string for a JSON-RPC notification, which needs none —
    /// the pipe still sends a line, so a client always knows the request was taken.
    /// </summary>
    public static string Handle(McpServer server, string line, string imageFolder)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            return Simple(false, "Not JSON: " + exception.Message, []);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("method", out _))
                return server.Handle(line) ?? "";

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tool", out JsonElement toolElement)
                || toolElement.ValueKind != JsonValueKind.String || toolElement.GetString() is not { Length: > 0 } tool)
                return Simple(false, "Send {\"tool\": \"name\", \"arguments\": {...}}, or {\"tool\": \"guide\"} to read how.", []);

            if (tool == GuideTool) return Simple(true, Guide(server), []);

            string call = Envelope(writer =>
            {
                writer.WriteString("method", "tools/call");
                writer.WriteStartObject("params");
                writer.WriteString("name", tool);
                writer.WritePropertyName("arguments");
                if (root.TryGetProperty("arguments", out JsonElement arguments) && arguments.ValueKind == JsonValueKind.Object)
                    arguments.WriteTo(writer);
                else
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            });
            return Simplify(server.Handle(call), imageFolder);
        }
    }

    /// <summary>
    /// The tool a line calls and its arguments, in either form — for the window, which answers a
    /// few tools itself (undo belongs to its own history).
    /// </summary>
    public static (string Tool, int? Steps)? Called(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            JsonElement call = root;
            if (root.TryGetProperty("method", out JsonElement method))
            {
                if (method.GetString() != "tools/call" || !root.TryGetProperty("params", out call)) return null;
                if (!call.TryGetProperty("name", out JsonElement named) || named.GetString() is not string name) return null;
                return (name, Steps(call));
            }
            return root.TryGetProperty("tool", out JsonElement tool) && tool.GetString() is string simple ? (simple, Steps(root)) : null;
        }
        catch (JsonException)
        {
            return null;
        }

        static int? Steps(JsonElement call) =>
            call.TryGetProperty("arguments", out JsonElement arguments) && arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("steps", out JsonElement steps) && steps.ValueKind == JsonValueKind.Number
                ? (int)Math.Round(steps.GetDouble())
                : null;
    }

    /// <summary>A text answer shaped for whichever form <paramref name="line"/> was in.</summary>
    public static string Answer(string line, string text, bool isError)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("method", out _))
            {
                JsonElement? id = root.TryGetProperty("id", out JsonElement given) ? given.Clone() : null;
                return Envelope(numbered: false, body: writer =>
                {
                    writer.WritePropertyName("id");
                    if (id is JsonElement value) value.WriteTo(writer);
                    else writer.WriteNullValue();
                    writer.WriteStartObject("result");
                    writer.WriteStartArray("content");
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", text);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    writer.WriteBoolean("isError", isError);
                    writer.WriteEndObject();
                });
            }
        }
        catch (JsonException)
        {
            // Not JSON at all: answered in the short form, like any other line that is not JSON-RPC.
        }
        return Simple(!isError, text, []);
    }

    /// <summary>
    /// How to use the editor from a shell: the call, the conventions, and every tool with its
    /// arguments — built from the live tool list so it cannot drift from what the server does.
    /// </summary>
    public static string Guide(McpServer server)
    {
        var guide = new StringBuilder();
        guide.AppendLine("# Compositor — working in the open editor window");
        guide.AppendLine();
        guide.AppendLine("Send one JSON line to the named pipe \\\\.\\pipe\\" + PipeName + " and read one line back. " +
                         "Edits appear in the window as you make them, one undo step each, and the person can keep working on them.");
        guide.AppendLine();
        guide.AppendLine("PowerShell (define once per shell, then call):");
        guide.AppendLine("```powershell");
        guide.AppendLine(PowerShellFunction);
        guide.AppendLine("Compositor '{\"tool\":\"get_document\"}'");
        guide.AppendLine("Compositor '{\"tool\":\"add_text\",\"arguments\":{\"text\":\"안녕\",\"x\":100,\"y\":200,\"size\":72}}'");
        guide.AppendLine("```");
        guide.AppendLine();
        guide.AppendLine("Answers are {\"ok\": true|false, \"text\": \"...\", \"images\": [paths]}. render writes a PNG and answers with " +
                         "its path — open that file to look at the picture. Tools act on the document shown in the window " +
                         "unless given \"document\"; new_document and open_document open a new tab.");
        guide.AppendLine();
        guide.AppendLine(McpServer.Instructions);
        guide.AppendLine();
        guide.AppendLine("## Tools (* = required)");

        string listed = server.Handle(Envelope(writer => writer.WriteString("method", "tools/list"))) ?? "";
        using JsonDocument tools = JsonDocument.Parse(listed);
        foreach (JsonElement tool in tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            string name = tool.GetProperty("name").GetString() ?? "";
            var required = new HashSet<string>(StringComparer.Ordinal);
            var parameters = new List<string>();
            if (tool.TryGetProperty("inputSchema", out JsonElement schema))
            {
                if (schema.TryGetProperty("required", out JsonElement needed))
                    foreach (JsonElement each in needed.EnumerateArray())
                        if (each.GetString() is string key) required.Add(key);
                if (schema.TryGetProperty("properties", out JsonElement properties))
                    foreach (JsonProperty property in properties.EnumerateObject())
                    {
                        string type = property.Value.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
                            ? t.GetString()! : "value";
                        string about = property.Value.TryGetProperty("description", out JsonElement d) ? d.GetString() ?? "" : "";
                        parameters.Add($"    - {property.Name}{(required.Contains(property.Name) ? "*" : "")} ({type}) {about}".TrimEnd());
                    }
            }
            guide.AppendLine($"- {name}: {tool.GetProperty("description").GetString()}");
            foreach (string parameter in parameters) guide.AppendLine(parameter);
        }
        return guide.ToString();
    }

    /// <summary>A PowerShell function that sends one line to the editor and returns its answer.</summary>
    public const string PowerShellFunction =
        "function Compositor([string]$json) { $p = [IO.Pipes.NamedPipeClientStream]::new('.', '" + PipeName + "', 'InOut'); " +
        "try { $p.Connect(5000); $e = [Text.UTF8Encoding]::new($false); $w = [IO.StreamWriter]::new($p, $e); " +
        "$r = [IO.StreamReader]::new($p, $e); $w.WriteLine($json); $w.Flush(); $r.ReadLine() } finally { $p.Dispose() } }";

    /// <summary>A tools/call reply as the short form, with images saved to files.</summary>
    private static string Simplify(string? reply, string imageFolder)
    {
        if (reply is null) return Simple(false, "No answer.", []);
        using var document = JsonDocument.Parse(reply);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("error", out JsonElement error))
            return Simple(false, error.TryGetProperty("message", out JsonElement message) ? message.GetString() ?? "" : "Failed.", []);

        JsonElement result = root.GetProperty("result");
        var text = new StringBuilder();
        var images = new List<string>();
        foreach (JsonElement content in result.GetProperty("content").EnumerateArray())
        {
            string type = content.GetProperty("type").GetString() ?? "";
            if (type == "text")
            {
                if (text.Length > 0) text.AppendLine();
                text.Append(content.GetProperty("text").GetString());
            }
            else if (content.TryGetProperty("data", out JsonElement data))
            {
                string mime = content.TryGetProperty("mimeType", out JsonElement m) ? m.GetString() ?? "" : "";
                string extension = mime switch
                {
                    "image/jpeg" => ".jpg",
                    "image/png" => ".png",
                    _ => ".bin",
                };
                Directory.CreateDirectory(imageFolder);
                string path = Path.Combine(imageFolder,
                    $"render-{DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}-{images.Count + 1}{extension}");
                File.WriteAllBytes(path, data.GetBytesFromBase64());
                images.Add(path);
            }
        }
        bool isError = result.TryGetProperty("isError", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
        return Simple(!isError, text.ToString(), images);
    }

    private static string Simple(bool ok, string text, IReadOnlyList<string> images)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", ok);
            writer.WriteString("text", text);
            writer.WriteStartArray("images");
            foreach (string image in images) writer.WriteStringValue(image);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Envelope(Action<Utf8JsonWriter> body, bool numbered = true)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            if (numbered) writer.WriteNumber("id", 1);
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
