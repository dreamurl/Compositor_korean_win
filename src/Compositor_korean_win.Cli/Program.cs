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
/// and bash each differently; a value that is a number or true/false is typed, one that starts
/// with <c>[</c> or <c>{</c> is JSON, and anything else is text. A single JSON object — a whole
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
        if (args.Length == 0 || args[0] is "guide" or "help" or "--help" or "-h" or "/?")
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
            arguments[each[..equals]] = Value(each[(equals + 1)..]);
        }

        var call = new JsonObject { ["tool"] = first, ["arguments"] = arguments };
        return call.ToJsonString();
    }

    /// <summary>A key=value's value, typed: number, true/false, null, JSON, or else text.</summary>
    private static JsonNode? Value(string text)
    {
        string trimmed = text.Trim();
        if (trimmed is "true" or "false") return JsonValue.Create(trimmed == "true");
        if (trimmed == "null") return null;
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            && !trimmed.StartsWith('+') && trimmed.Length > 0 && (char.IsDigit(trimmed[^1]) || trimmed[^1] == '.'))
            return JsonValue.Create(number);
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
        // "\n" typed in a shell is two characters; a line break in the words is what is meant.
        return JsonValue.Create(text.Replace("\\n", "\n", StringComparison.Ordinal));
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
        try
        {
            await writer.WriteLineAsync(request);
            await writer.FlushAsync();
            string? answer = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (answer is null)
            {
                Console.Error.WriteLine("Compositor closed the connection without answering.");
                return new(null, ConnectionFailed);
            }
            return new(answer, 0);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("Compositor accepted the connection but did not answer within 30 seconds.");
            return new(null, ResponseTimedOut);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine("The connection to Compositor failed while waiting for its answer: " + exception.Message);
            return new(null, ConnectionFailed);
        }
    }

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
