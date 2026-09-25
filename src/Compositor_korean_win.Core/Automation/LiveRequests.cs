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

    /// <summary>
    /// How many rendered images the folder keeps. A model looks at the one it just asked for; a
    /// long session renders hundreds, so older ones go as new ones arrive rather than piling up.
    /// </summary>
    public const int ImagesKept = 20;

    /// <summary>Numbers the files, so two renders in the same millisecond do not share a name.</summary>
    private static int s_rendered;

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

    /// <summary>Whether a line is JSON-RPC — from an MCP client, which reads the rules on connecting — rather than the short form.</summary>
    public static bool IsJsonRpc(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("method", out _);
        }
        catch (JsonException)
        {
            return false;
        }
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
        bool korean = Localizer.Current == Language.Korean;
        guide.AppendLine(korean ? "# Compositor — 열려 있는 편집기에서 작업하기" : "# Compositor — working in the open editor window");
        guide.AppendLine();
        guide.AppendLine(korean
            ? "명령으로 한 편집은 창에 즉시 나타나며 각각 실행 취소 한 단계가 됩니다. 사용자는 같은 문서에서 계속 작업할 수 있습니다."
            : "Edits appear in the window as you make them, one undo step each, and the person can keep working on them.");
        guide.AppendLine();
        if (korean)
        {
            guide.AppendLine("편집기와 함께 설치되어 PATH에 등록된 `compositor` 명령 뒤에 도구 이름과 key=value 인수를 적습니다.");
            guide.AppendLine("숫자와 true/false는 해당 형식으로, [..]와 {..}는 JSON으로, 나머지는 문자열로 처리됩니다.");
            guide.AppendLine("공백이 든 값은 따옴표로 감싸세요. `compositor`만 실행하면 이 안내서를 다시 표시합니다.");
        }
        else
        {
            guide.AppendLine("Call a tool with the `compositor` command (installed with the editor, on the PATH): the tool's name, then");
            guide.AppendLine("its arguments as key=value. Numbers and true/false are typed, [..] or {..} is JSON, anything else is text;");
            guide.AppendLine("quote a value with spaces. `compositor` alone prints this guide.");
        }
        guide.AppendLine("```");
        guide.AppendLine("compositor get_document");
        guide.AppendLine("compositor new_document width=1080 height=1350 background=#101010");
        guide.AppendLine("compositor add_text text=\"Hello world\" x=100 y=200 size=72 color=#E02020");
        guide.AppendLine("compositor edit_text layer=Title start=0 end=5 size=120");
        guide.AppendLine("compositor render");
        guide.AppendLine("```");
        if (korean)
        {
            guide.AppendLine("도구의 응답이 출력되며, render는 확인할 PNG 파일 경로도 출력합니다.");
            guide.AppendLine("document=...를 생략하면 창에 표시된 문서를 대상으로 합니다. new_document와 open_document는 새 탭을 엽니다.");
            guide.AppendLine("종료 코드는 도구 실패 1, 인수 오류 2, 편집기 시작 실패 3, 권한 거부 4, 연결 실패 5, 응답 시간 초과 6입니다.");
            guide.AppendLine("버전은 앱 연결 없이 `compositor --version`으로 확인할 수 있습니다.");
        }
        else
        {
            guide.AppendLine("It prints the tool's answer, and for render the path of a PNG — open that file to look at the picture.");
            guide.AppendLine("Tools act on the document shown in the window unless given document=...; new_document and open_document");
            guide.AppendLine("open a new tab. Exit codes are 1 tool failure, 2 arguments, 3 editor start, 4 permission, 5 connection and 6 response timeout.");
            guide.AppendLine("Read the installed version without connecting to the app with `compositor --version`.");
        }
        guide.AppendLine();
        guide.AppendLine(korean
            ? "명령을 사용할 수 없다면 같은 요청을 named pipe \\\\.\\pipe\\" + PipeName +
              "에 JSON 한 줄로 보내고 응답 한 줄({\"ok\", \"text\", \"images\"})을 읽습니다. 예:"
            : "Without the command, send the same requests as one JSON line to the named pipe \\\\.\\pipe\\" + PipeName +
              " and read one line back ({\"ok\", \"text\", \"images\"}), for example with this PowerShell function:");
        guide.AppendLine("```powershell");
        guide.AppendLine(PowerShellFunction);
        guide.AppendLine("Compositor '{\"tool\":\"add_text\",\"arguments\":{\"text\":\"Hello\",\"x\":100,\"y\":200,\"size\":72}}'");
        guide.AppendLine("```");
        guide.AppendLine();
        guide.AppendLine(korean
            ? "Compositor는 레이어 기반 이미지 편집기입니다. 좌표는 문서 왼쪽 위에서 시작하는 픽셀이며, 색상은 #RRGGBB 또는 #RRGGBBAA, 불투명도는 0–1입니다. 작업 전 get_document로 구조를 읽고, 편집 중 render로 자주 확인하며, 요청받은 경우에만 save_document나 export_image로 저장하세요."
            : McpServer.Instructions);
        guide.AppendLine();
        if (server.Rules is AiRules rules)
        {
            guide.AppendLine(korean ? "## 사용자가 정한 작업 규칙 — 반드시 따르세요." : "## Rules — set by the person who uses this editor. Follow them.");
            guide.AppendLine();
            guide.AppendLine(rules.Text);
            guide.AppendLine();
        }
        if (korean)
        {
            guide.AppendLine("## 연결 문제 해결");
            guide.AppendLine();
            guide.AppendLine("- 권한 거부가 나오면 Compositor와 명령을 같은 Windows 사용자·권한 수준에서 실행하세요.");
            guide.AppendLine("- Codex 같은 샌드박스에서는 로컬 named pipe 접근 승인이 필요할 수 있습니다.");
            guide.AppendLine("- 앱이 실행 중인데 연결되지 않으면 새 창을 반복 실행하지 말고 오류 메시지와 종료 코드를 확인하세요.");
            guide.AppendLine();
        }
        guide.AppendLine(korean ? "## 도구 (* = 필수)" : "## Tools (* = required)");

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
                    $"render-{DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}-{Interlocked.Increment(ref s_rendered):D6}{extension}");
                File.WriteAllBytes(path, data.GetBytesFromBase64());
                images.Add(path);
            }
        }
        if (images.Count > 0) Prune(imageFolder, ImagesKept);
        bool isError = result.TryGetProperty("isError", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
        return Simple(!isError, text.ToString(), images);
    }

    /// <summary>Deletes all but the <paramref name="keep"/> newest images in the folder; with 0, all of them.</summary>
    public static void Prune(string imageFolder, int keep)
    {
        if (!Directory.Exists(imageFolder)) return;
        IEnumerable<FileInfo> stale = new DirectoryInfo(imageFolder).EnumerateFiles("render-*")
            .OrderByDescending(file => file.CreationTimeUtc).ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(keep);
        foreach (FileInfo file in stale)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // Open in a viewer: it goes on a later pass.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
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
