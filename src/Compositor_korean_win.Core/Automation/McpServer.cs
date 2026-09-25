using System.Text;
using System.Text.Json;

namespace Compositor_korean_win.Core;

/// <summary>
/// A Model Context Protocol server over standard input and output: one JSON-RPC message a line.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets a model — Claude, Codex, anything that speaks MCP — use the editor: it starts
/// the program with <c>--mcp</c>, lists the tools, and calls them. Everything is written by hand
/// with <see cref="Utf8JsonWriter"/> and read with <see cref="JsonDocument"/>, so it needs no
/// reflection and runs in the trimmed NativeAOT build.
/// </para>
/// <para>
/// Requests are handled one at a time, in order. A tool that fails answers with <c>isError</c> and
/// the reason, as the protocol asks, so the model can read it and try again; only a malformed
/// message is a protocol error.
/// </para>
/// </remarks>
public sealed class McpServer(McpTools tools, string version, AiRules? rules = null)
{
    /// <summary>The person's working rules (<see cref="AiRules"/>), read afresh for each client.</summary>
    public AiRules? Rules => rules;

    private static readonly string[] Versions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>Guidance the client shows the model once, on connecting.</summary>
    public const string Instructions =
        "Compositor is a layered image editor. Coordinates are document pixels from the top-left; colours are hex " +
        "(\"#RRGGBB\", \"#RRGGBBAA\"); opacities are 0–1. Start with new_document or open_document, build the picture " +
        "from layers (add_image, add_text, add_shape, add_gradient, add_adjustment), shape it with set_mask, set_clipping " +
        "and set_effects, and call render often to look at what you have made. Layers are referred to by the ids tools " +
        "answer with (or by name). Save the layered result with save_document (.psd for other editors, .comp for this one) " +
        "and the picture with export_image. Photographs you cannot draw can come from generate_image or add_image.";

    public void Run(TextReader input, TextWriter output, TextWriter? log = null)
    {
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (line.Length == 0 || string.IsNullOrWhiteSpace(line)) continue;
            string? reply = Handle(line, log);
            if (reply is null) continue;
            output.Write(reply);
            output.Write('\n');
            output.Flush();
        }
    }

    /// <summary>The reply to one message, or null for a notification.</summary>
    public string? Handle(string message, TextWriter? log = null)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException exception)
        {
            return Error(null, -32700, "Parse error: " + exception.Message);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Error(null, -32600, "A request is a JSON object.");

            JsonElement? id = root.TryGetProperty("id", out JsonElement given) ? given.Clone() : null;
            string? method = root.TryGetProperty("method", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
            JsonElement parameters = root.TryGetProperty("params", out JsonElement found) ? found.Clone() : default;

            if (method is null) return id is null ? null : Error(id, -32600, "No method.");

            // Notifications — initialized, cancelled — need no answer.
            if (id is null) return null;

            try
            {
                return method switch
                {
                    "initialize" => Reply(id, writer => Initialize(writer, parameters)),
                    "ping" => Reply(id, writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteEndObject();
                    }),
                    "tools/list" => Reply(id, ListTools),
                    "tools/call" => Reply(id, writer => CallTool(writer, parameters, log)),
                    "resources/list" => Reply(id, writer => EmptyList(writer, "resources")),
                    "prompts/list" => Reply(id, writer => EmptyList(writer, "prompts")),
                    _ => Error(id, -32601, $"Method not found: {method}"),
                };
            }
            catch (ToolException exception)
            {
                return Error(id, -32602, exception.Message);
            }
        }
    }

    private void Initialize(Utf8JsonWriter writer, JsonElement parameters)
    {
        string asked = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out JsonElement v)
                       && v.ValueKind == JsonValueKind.String ? v.GetString()! : Versions[0];

        writer.WriteStartObject();
        writer.WriteString("protocolVersion", Versions.Contains(asked) ? asked : Versions[0]);
        writer.WriteStartObject("capabilities");
        writer.WriteStartObject("tools");
        writer.WriteBoolean("listChanged", false);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartObject("serverInfo");
        writer.WriteString("name", "compositor");
        writer.WriteString("title", "Compositor image editor");
        writer.WriteString("version", version);
        writer.WriteEndObject();
        writer.WriteString("instructions", rules is null
            ? Instructions
            : Instructions + "\n\nFollow these rules, set by the person who uses this editor:\n\n" + rules.Text);
        writer.WriteEndObject();
    }

    private void ListTools(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("tools");
        foreach (ToolDefinition tool in tools.Definitions)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("inputSchema");
            writer.WriteRawValue(tool.InputSchema);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void EmptyList(Utf8JsonWriter writer, string name)
    {
        writer.WriteStartObject();
        writer.WriteStartArray(name);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private void CallTool(Utf8JsonWriter writer, JsonElement parameters, TextWriter? log)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("name", out JsonElement nameElement)
            || nameElement.GetString() is not string name)
            throw new ToolException("tools/call needs a tool 'name'.");

        var arguments = new ToolArguments(parameters.TryGetProperty("arguments", out JsonElement given) ? given : default);

        ToolResult result;
        try
        {
            result = tools.Call(name, arguments);
        }
        catch (ToolException exception)
        {
            result = ToolResult.Error(exception.Message);
        }
        catch (ProjectException exception)
        {
            result = ToolResult.Error(exception.Message);
        }
        catch (Exception exception)
        {
            // One failed call must not end the session the model has built up; it reads the reason instead.
            log?.WriteLine(exception);
            result = ToolResult.Error($"{exception.GetType().Name}: {exception.Message}");
        }

        writer.WriteStartObject();
        writer.WriteStartArray("content");
        foreach (ToolContent content in result.Content)
        {
            writer.WriteStartObject();
            writer.WriteString("type", content.Type);
            if (content.Text is string text) writer.WriteString("text", text);
            if (content.Data is byte[] data)
            {
                writer.WriteBase64String("data", data);
                writer.WriteString("mimeType", content.MimeType);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteBoolean("isError", result.IsError);
        writer.WriteEndObject();
    }

    private static string Reply(JsonElement? id, Action<Utf8JsonWriter> result) => Envelope(id, writer =>
    {
        writer.WritePropertyName("result");
        result(writer);
    });

    private static string Error(JsonElement? id, int code, string message) => Envelope(id, writer =>
    {
        writer.WriteStartObject("error");
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
    });

    private static string Envelope(JsonElement? id, Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (id is JsonElement value) value.WriteTo(writer);
            else writer.WriteNullValue();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
