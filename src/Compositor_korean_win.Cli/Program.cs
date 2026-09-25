using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Compositor_korean_win.Cli;

/// <summary>
/// <c>compositor</c>: one tool call to the open editor window, from any shell.
/// </summary>
/// <remarks>
/// <para>
/// What a person tells an assistant is only "Compositor is open, use the compositor command". The
/// assistant runs <c>compositor</c>, which prints the guide — the person's rules and every tool —
/// and then runs <c>compositor add_text text=Hello x=100 y=200</c> and so on. The installer puts the
/// command on the user's PATH, so where the program was installed never has to be said.
/// </para>
/// <para>
/// Arguments are <c>key=value</c> rather than JSON because JSON's quotes survive PowerShell, cmd
/// and bash each differently; a value written as a JSON number or true/false is typed, one that
/// starts with <c>[</c> or <c>{</c> is JSON, and anything else is text. <c>text</c> and
/// <c>prompt</c> are always words; the editor reads <c>\n</c> in them as a line break. A single JSON object — a whole
/// request, or a tool's arguments — is taken as it is, for whoever prefers it.
/// </para>
/// <para>
/// With no window open, the editor next to this command is started and waited for, so "work in
/// Compositor" works even when it was closed.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>The window's pipe; the same name as <c>LiveRequests.PipeName</c> in Core.</summary>
    private const string PipeName = "compositor-korean-win";

    private const string EditorExe = "Compositor_korean_win.exe";
    private const string EditorProcess = "Compositor_korean_win";

    private const int EditorUnavailable = 3;
    private const int AccessDenied = 4;
    private const int ConnectionFailed = 5;
    private const int ResponseTimedOut = 6;

    /// <summary>
    /// How long an answer may take. The window holds a request while the person is mid-drag,
    /// mid-stroke, typing or has a dialog open, and a large render or generated image takes a while
    /// of its own, so this is minutes rather than seconds. <c>COMPOSITOR_TIMEOUT</c> (seconds, 0 for
    /// no limit) changes it.
    /// </summary>
    private static readonly TimeSpan DefaultAnswerTimeout = TimeSpan.FromMinutes(5);

    /// <summary>When a wait is long enough to say why, so a model does not take silence for a hang.</summary>
    private static readonly TimeSpan WaitingNoticeAfter = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Arguments that are always words, sent as typed: <c>text=2024</c> is a year, not a number, and
    /// <c>text=[SALE]</c> is not a JSON list. Their <c>\n</c> and <c>\\</c> are left for the editor,
    /// which reads them the same way for every client (<c>ToolArguments.Words</c>); unescaping here
    /// too would take one level of backslashes away before it saw them. Other values are never
    /// unescaped, since a backslash there is far more often a Windows path (<c>path=C:\new\a.psd</c>).
    /// </summary>
    private static readonly HashSet<string> Worded = new(StringComparer.Ordinal) { "text", "prompt" };

    private enum ConnectionFailure
    {
        None,
        TimedOut,
        AccessDenied,
        IoError,
    }

    private readonly record struct ConnectionAttempt(NamedPipeClientStream? Pipe, ConnectionFailure Failure, string? Detail);

    private readonly record struct SendResult(string? Answer, int ExitCode);

    private static async Task<int> Main(string[] args)
    {
        // Git Bash and other UTF-8 terminals read UTF-8; Windows consoles read their code page,
        // which .NET already writes in. Korean rules and names must come out readable in both.
        if (Environment.GetEnvironmentVariable("MSYSTEM") is not null
            || Environment.GetEnvironmentVariable("COMPOSITOR_UTF8") == "1")
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        if (args.Length == 1 && args[0] is "--version" or "version")
        {
            string version = FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion ?? "unknown";
            Console.WriteLine("compositor " + version.Split('+')[0]);
            return 0;
        }

        string request;
        try
        {
            request = Request(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        SendResult sent = await Send(request);
        if (sent.Answer is null) return sent.ExitCode;
        return Print(sent.Answer);
    }

    /// <summary>The line to send: the guide, a whole request given as JSON, or a tool and its key=value arguments.</summary>
    private static string Request(string[] args)
    {
        // "guide topic=reproduce" goes on as a call; "guide" alone is the whole guide.
        if (args.Length == 0 || (args.Length == 1 && args[0] is "guide" or "help" or "--help" or "-h" or "/?"))
            return """{"tool":"guide"}""";

        string first = args[0].Trim();
        if (first.StartsWith('{')) return Compact(string.Join(" ", args));

        var arguments = new JsonObject();
        for (int i = 1; i < args.Length; i++)
        {
            string each = args[i];
            if (each.TrimStart().StartsWith('{') && args.Length == 2)
            {
                if (JsonNode.Parse(each) is not JsonObject given)
                    throw new ArgumentException("The arguments are not a JSON object: " + each);
                arguments = given;
                break;
            }

            int equals = each.IndexOf('=');
            if (equals <= 0) throw new ArgumentException($"Arguments are key=value, as in text=Hello x=100. Not understood: {each}");
            string key = each[..equals];
            string value = each[(equals + 1)..];
            arguments[key] = Worded.Contains(key) ? JsonValue.Create(value) : Value(value);
        }

        var call = new JsonObject { ["tool"] = first, ["arguments"] = arguments };
        return call.ToJsonString();
    }

    /// <summary>A key=value's value, typed: number, true/false, null, JSON, or else text.</summary>
    /// <remarks>
    /// A number is only what JSON itself would write as one, and it is sent as written. So a layer
    /// named <c>007</c> stays "007" rather than becoming 7, and <c>1.50</c> reaches a tool that reads
    /// it as a name still as "1.50". Anything short of that — <c>.5</c>, <c>+3</c> — goes as text,
    /// which the tools read as a number wherever a number is wanted.
    /// </remarks>
    private static JsonNode? Value(string text)
    {
        string trimmed = text.Trim();
        if (trimmed is "true" or "false") return JsonValue.Create(trimmed == "true");
        if (trimmed == "null") return null;
        if (trimmed.Length > 0 && (trimmed[0] == '-' || char.IsAsciiDigit(trimmed[0])))
        {
            try
            {
                if (JsonNode.Parse(trimmed) is JsonValue number && number.GetValueKind() == JsonValueKind.Number) return number;
            }
            catch (JsonException)
            {
                // 007, 12px, 2024-01-01: text.
            }
        }
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
        {
            try
            {
                return JsonNode.Parse(trimmed);
            }
            catch (JsonException)
            {
                // Not JSON after all: the text as it was written.
            }
        }
        return JsonValue.Create(text);
    }

    private static string Compact(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.ToJsonString() ?? json;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Not JSON: " + exception.Message);
        }
    }

    /// <summary>Sends one line to the window, starting it first when none is open.</summary>
    private static async Task<SendResult> Send(string request)
    {
        ConnectionAttempt connected = Connect(2000);
        if (connected.Pipe is null)
        {
            if (connected.Failure == ConnectionFailure.AccessDenied)
                return Denied(connected.Detail);

            if (EditorIsRunning())
            {
                connected = WaitForExistingEditor();
                if (connected.Pipe is null)
                {
                    if (connected.Failure == ConnectionFailure.AccessDenied)
                        return Denied(connected.Detail);
                    Console.Error.WriteLine("Compositor is running, but its command pipe could not be reached. " +
                                            "The app may still be starting, or this shell may not have permission to use its pipe.");
                    if (connected.Detail is { Length: > 0 }) Console.Error.WriteLine(connected.Detail);
                    return new(null, ConnectionFailed);
                }
            }
            else
            {
                connected = StartEditor();
                if (connected.Pipe is null)
                    return connected.Failure == ConnectionFailure.AccessDenied
                        ? Denied(connected.Detail)
                        : new(null, EditorUnavailable);
            }
        }

        using NamedPipeClientStream pipe = connected.Pipe!;
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var writer = new StreamWriter(pipe, encoding, leaveOpen: true) { NewLine = "\n" };
        using var reader = new StreamReader(pipe, encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        TimeSpan timeout = AnswerTimeout();
        try
        {
            await writer.WriteLineAsync(request);
            await writer.FlushAsync();
            Task<string?> reading = reader.ReadLineAsync();
            if (await Task.WhenAny(reading, Task.Delay(WaitingNoticeAfter)) != reading)
                Console.Error.WriteLine("Still waiting for Compositor. It holds a request while the person is dragging, painting, " +
                                        "typing or has a dialog open, and a large render takes a while of its own.");
            string? answer = await reading.WaitAsync(timeout == Timeout.InfiniteTimeSpan ? timeout : timeout - WaitingNoticeAfter);
            if (answer is null)
            {
                Console.Error.WriteLine("Compositor closed the connection without answering.");
                return new(null, ConnectionFailed);
            }
            return new(answer, 0);
        }
        catch (TimeoutException)
        {
            // Closing the pipe on the way out withdraws the request if the window has not started it,
            // but one already running finishes: which of the two happened cannot be told from here.
            Console.Error.WriteLine($"Compositor did not answer within {Describe(timeout)}. The request is withdrawn if Compositor " +
                                    "had not started it yet, but one it had started may still finish. Check with `compositor get_document` " +
                                    "before sending it again.");
            return new(null, ResponseTimedOut);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine("The connection to Compositor failed while waiting for its answer: " + exception.Message);
            return new(null, ConnectionFailed);
        }
    }

    /// <summary><see cref="DefaultAnswerTimeout"/>, or <c>COMPOSITOR_TIMEOUT</c> seconds when that is set; 0 waits for good.</summary>
    private static TimeSpan AnswerTimeout()
    {
        string? given = Environment.GetEnvironmentVariable("COMPOSITOR_TIMEOUT");
        if (string.IsNullOrWhiteSpace(given)) return DefaultAnswerTimeout;
        if (!int.TryParse(given.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
        {
            Console.Error.WriteLine($"COMPOSITOR_TIMEOUT is seconds, as in 600; '{given}' is not. Waiting {Describe(DefaultAnswerTimeout)}.");
            return DefaultAnswerTimeout;
        }
        if (seconds == 0) return Timeout.InfiniteTimeSpan;
        // Never shorter than the notice, which the wait is measured from.
        return TimeSpan.FromSeconds(Math.Max(seconds, (int)WaitingNoticeAfter.TotalSeconds + 1));
    }

    private static string Describe(TimeSpan span) =>
        span.TotalSeconds % 60 == 0 ? $"{span.TotalMinutes:0} minute(s)" : $"{span.TotalSeconds:0} seconds";

    private static ConnectionAttempt Connect(int milliseconds)
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        try
        {
            pipe.Connect(milliseconds);
            return new(pipe, ConnectionFailure.None, null);
        }
        catch (UnauthorizedAccessException exception)
        {
            pipe.Dispose();
            return new(null, ConnectionFailure.AccessDenied, exception.Message);
        }
        catch (TimeoutException exception)
        {
            pipe.Dispose();
            return new(null, ConnectionFailure.TimedOut, exception.Message);
        }
        catch (IOException exception)
        {
            pipe.Dispose();
            return new(null, ConnectionFailure.IoError, exception.Message);
        }
    }

    private static ConnectionAttempt WaitForExistingEditor()
    {
        ConnectionAttempt attempt = default;
        for (int waited = 0; waited < 10; waited++)
        {
            attempt = Connect(500);
            if (attempt.Pipe is not null || attempt.Failure == ConnectionFailure.AccessDenied) return attempt;
        }
        return attempt;
    }

    private static ConnectionAttempt StartEditor()
    {
        string editor = Path.Combine(AppContext.BaseDirectory, EditorExe);
        if (!File.Exists(editor))
        {
            Console.Error.WriteLine("Compositor is not open, and its program was not found next to this command. Start Compositor first.");
            return new(null, ConnectionFailure.IoError, null);
        }

        Console.Error.WriteLine("Compositor was not open; starting it…");
        // Through the shell, not CreateProcess: a child made directly inherits this command's output
        // pipe, and the editor keeps running, so whoever reads that pipe would wait for it forever.
        Process? started;
        try
        {
            started = Process.Start(new ProcessStartInfo(editor) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("Compositor could not be started: " + exception.Message);
            return new(null, ConnectionFailure.IoError, exception.Message);
        }

        using (started)
        {
            ConnectionAttempt attempt = default;
            for (int waited = 0; waited < 60; waited++)
            {
                if (started is not null && started.HasExited)
                {
                    Console.Error.WriteLine($"Compositor exited before opening its command pipe (exit code {started.ExitCode}).");
                    return new(null, ConnectionFailure.IoError, null);
                }
                attempt = Connect(500);
                if (attempt.Pipe is not null || attempt.Failure == ConnectionFailure.AccessDenied) return attempt;
            }
            Console.Error.WriteLine("Compositor started but did not open its command pipe within 30 seconds.");
            return attempt;
        }
    }

    private static bool EditorIsRunning()
    {
        Process[] editors = Process.GetProcessesByName(EditorProcess);
        foreach (Process editor in editors) editor.Dispose();
        return editors.Length > 0;
    }

    private static SendResult Denied(string? detail)
    {
        Console.Error.WriteLine("Compositor is running, but access to its command pipe was denied. " +
                                "Run the command as the same Windows user and permission level as Compositor, " +
                                "or approve local pipe access in the sandbox.");
        if (detail is { Length: > 0 }) Console.Error.WriteLine(detail);
        return new(null, AccessDenied);
    }

    /// <summary>The answer for a person or a model to read: its text, then any images as paths. 0 when it went well.</summary>
    private static int Print(string answer)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(answer);
        }
        catch (JsonException)
        {
            Console.WriteLine(answer);
            return 0;
        }

        if (parsed is not JsonObject root || root["ok"] is not JsonValue okValue)
        {
            // JSON-RPC in, JSON-RPC out: printed as it came.
            Console.WriteLine(answer);
            return 0;
        }

        bool ok = okValue.GetValue<bool>();
        string text = root["text"]?.GetValue<string>() ?? "";
        if (text.Length > 0) (ok ? Console.Out : Console.Error).WriteLine(text);
        if (root["images"] is JsonArray images)
            foreach (JsonNode? image in images)
                Console.WriteLine("image: " + image?.GetValue<string>());
        return ok ? 0 : 1;
    }
}
