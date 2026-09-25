using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using Compositor_korean_win.Core;
using static Compositor_korean_win.Shell.Win32;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The open window's automation pipe: a model on the same machine sends tool calls to
/// <c>\\.\pipe\compositor-korean-win</c> and the edits happen here, in front of the person, with
/// nothing registered anywhere and no mouse or keyboard taken over (<see cref="LiveRequests"/>).
/// </summary>
/// <remarks>
/// <para>
/// Lines are read on pool threads and run on the window's thread, between messages, the way a
/// click is: the pipe thread posts <see cref="Message"/> and waits for the answer. While the person
/// is in the middle of something — a drag, a stroke, typing, an open filter — requests wait, every
/// <see cref="RetryMilliseconds"/>, until the canvas can take an edit. So a model and a person
/// never change the same document at the same moment. A request whose client hangs up before it
/// has started — a command that gave up waiting — is withdrawn rather than run later, when nobody
/// is there to read that it happened and the model may already have sent it again.
/// </para>
/// <para>
/// The tools themselves are the MCP server's, over a session whose documents are the window's
/// tabs. Before each request the session is filled from the tabs; afterwards each document a tool
/// changed goes back to its tab as one undo step named after the tool, and a document a tool made
/// opens as a new tab. The session does not own those pixels: the window's history does.
/// Undo and redo go to the window's own history, since the session's holds only the last call.
/// </para>
/// <para>
/// The pipe admits the current user only. A second window finds the name taken and runs without
/// a pipe; requests go to the first.
/// </para>
/// </remarks>
internal sealed class LiveBridge : IDisposable
{
    /// <summary>Posted to the window when a request is waiting.</summary>
    public const uint Message = WM_APP + 0x41;

    /// <summary>The timer that looks again while the person is mid-gesture.</summary>
    public const nuint RetryTimer = 7;

    private const uint RetryMilliseconds = 100;

    /// <summary>A request waiting for the window's thread, which either runs it or finds it withdrawn — never both.</summary>
    private sealed class Pending(string line)
    {
        private const int Waiting = 0;
        private const int Running = 1;
        private const int Withdrawn = 2;

        private int _state = Waiting;

        public string Line { get; } = line;

        public TaskCompletionSource<string> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Claims the request for running; false once its client has withdrawn it.</summary>
        public bool Start() => Interlocked.CompareExchange(ref _state, Running, Waiting) == Waiting;

        /// <summary>Withdraws the request; false once it is running or has run, which then finishes.</summary>
        public bool Withdraw() => Interlocked.CompareExchange(ref _state, Withdrawn, Waiting) == Waiting;
    }

    private readonly nint _window;
    private readonly CanvasView _canvas;
    private readonly ShellEditorServices _services = new();
    private readonly EditorSession _session;
    private readonly McpServer _server;
    private readonly ConcurrentQueue<Pending> _queue = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly string _images = Path.Combine(Path.GetTempPath(), "compositor-ai");
    private readonly Dictionary<EditorSession.Open, CanvasDocument> _before = new(ReferenceEqualityComparer.Instance);

    /// <summary>When the guide was last read, by the rules' version then; null before it has been.</summary>
    private DateTime? _guideRead;

    private LiveBridge(nint window, CanvasView canvas, NamedPipeServerStream first)
    {
        _window = window;
        _canvas = canvas;
        _session = new EditorSession(_services, ownsPixels: false);
        AiRules rules = Rules;
        rules.Ensure();
        // What an earlier run rendered is of no use to this one.
        LiveRequests.Prune(_images, keep: 0);
        _server = new McpServer(new McpTools(_session), Updates.Version, rules);
        _ = Task.Run(() => Listen(first));
    }

    /// <summary>
    /// Opens the pipe for this window, or null when another window already has it — or when the
    /// pipe cannot be made at all, which leaves the editor working exactly as without it.
    /// </summary>
    public static LiveBridge? Start(nint window, CanvasView canvas)
    {
        try
        {
            return new LiveBridge(window, canvas, Create(first: true));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("automation pipe not opened: " + exception.Message);
            return null;
        }
    }

    /// <summary>The person's rules for assistants: <c>%APPDATA%\Compositor_korean_win\ai-rules.md</c>.</summary>
    public static AiRules Rules { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Compositor_korean_win", "ai-rules.md"),
        () => Localizer.Text(TextKey.AiRulesDefault));

    private static NamedPipeServerStream Create(bool first) =>
        new(LiveRequests.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    private async Task Listen(NamedPipeServerStream waiting)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await waiting.WaitForConnectionAsync(_stop.Token);
            }
            catch (Exception)
            {
                await waiting.DisposeAsync();
                if (_stop.IsCancellationRequested) return;
                waiting = Create(first: false);
                continue;
            }

            NamedPipeServerStream connected = waiting;
            _ = Task.Run(() => Serve(connected));
            try
            {
                waiting = Create(first: false);
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine("automation pipe stopped: " + exception.Message);
                return;
            }
        }
        await waiting.DisposeAsync();
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await using (pipe)
        {
            using var reader = new StreamReader(pipe, encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            // Lines are read ahead of the answers, not after each one, so that a client hanging up is
            // seen while its request still waits for the person to finish — the read ends then.
            Channel<string> lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var hungUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() => Read(reader, lines.Writer, hungUp));
            try
            {
                await using var writer = new StreamWriter(pipe, encoding, leaveOpen: true) { NewLine = "\n", AutoFlush = false };
                await foreach (string line in lines.Reader.ReadAllAsync(_stop.Token))
                {
                    var pending = new Pending(line);
                    _queue.Enqueue(pending);
                    PostMessageW(_window, Message, 0, 0);

                    Task<string> answered = pending.Answer.Task;
                    await Task.WhenAny(answered, hungUp.Task).WaitAsync(_stop.Token);
                    // Gone before it ran: nobody would read the answer, and running it later would
                    // make a change the client was told had not happened.
                    if (!answered.IsCompleted && pending.Withdraw()) return;
                    string answer = await answered.WaitAsync(_stop.Token);

                    await writer.WriteLineAsync(answer);
                    await writer.FlushAsync(_stop.Token);
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away, or the window is closing.
            }
        }
    }

    /// <summary>The client's lines, until it hangs up or the pipe closes.</summary>
    private async Task Read(StreamReader reader, ChannelWriter<string> lines, TaskCompletionSource hungUp)
    {
        try
        {
            while (!_stop.IsCancellationRequested && await reader.ReadLineAsync(_stop.Token) is string line)
            {
                // A client that wrote a BOM before its first line still means the JSON after it.
                line = line.TrimStart((char)0xFEFF);
                if (!string.IsNullOrWhiteSpace(line)) lines.TryWrite(line);
            }
        }
        catch (Exception)
        {
            // IOException, a disposed pipe or the window closing: all mean no more lines. Nothing is
            // waiting on this task, so nothing may escape it.
        }
        finally
        {
            lines.TryComplete();
            hungUp.TrySetResult();
        }
    }

    /// <summary>Runs whatever is waiting, on the window's thread; waits instead while the person is mid-gesture.</summary>
    public void Drain()
    {
        if (_queue.IsEmpty) return;
        if (Busy)
        {
            SetTimer(_window, RetryTimer, RetryMilliseconds, 0);
            return;
        }
        KillTimer(_window, RetryTimer);

        while (_queue.TryDequeue(out Pending? pending))
        {
            // Its client gave up waiting while the person was busy.
            if (!pending.Start()) continue;
            string answer;
            try
            {
                answer = Run(pending.Line);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                answer = LiveRequests.Answer(pending.Line, $"{exception.GetType().Name}: {exception.Message}", isError: true);
            }
            pending.Answer.TrySetResult(answer);
            if (Busy) break;
        }

        if (!_queue.IsEmpty) SetTimer(_window, RetryTimer, RetryMilliseconds, 0);
        InvalidateRect(_window, 0, false);
    }

    /// <summary>Whether the person has something half done that an edit must not land in the middle of.</summary>
    private bool Busy =>
        (_canvas.HasDocument && !_canvas.CanEdit) || !_canvas.CanSwitchTab || GetCapture() == _window;

    private string Run(string line)
    {
        (string Tool, int? Steps)? called = LiveRequests.Called(line);

        // A shell client reads the guide — and with it the person's rules — before its first edit,
        // and again once the rules have changed. An MCP client has them from connecting.
        if (called is (string asked, _) && !LiveRequests.IsJsonRpc(line))
        {
            if (asked == LiveRequests.GuideTool)
            {
                _guideRead = _server.Rules?.Version ?? DateTime.MinValue;
            }
            else if (_guideRead is not DateTime read || read != (_server.Rules?.Version ?? DateTime.MinValue))
            {
                string why = _guideRead is null ? "Read the guide first" : "The rules have changed since you read the guide; read it again";
                return LiveRequests.Answer(line, why + ": run `compositor` with no arguments (or send {\"tool\":\"guide\"}). " +
                                                 "It holds the rules the person set for working in this editor, and every tool. " +
                                                 "Then send this request again.", isError: true);
            }
        }

        if (called is (string tool, var steps) && tool is "undo" or "redo")
            return Step(line, tool == "undo", Math.Clamp(steps ?? 1, 1, 1000));

        Fill();
        try
        {
            string answer = LiveRequests.Handle(_server, line, _images);
            Apply(LiveRequests.Called(line)?.Tool);
            return answer;
        }
        finally
        {
            _before.Clear();
        }
    }

    /// <summary>Undo and redo in the window's history, which is the one the person sees.</summary>
    private string Step(string line, bool undo, int steps)
    {
        int done = 0;
        for (; done < steps && (undo ? _canvas.CanUndo : _canvas.CanRedo); done++)
        {
            if (undo) _canvas.Undo();
            else _canvas.Redo();
        }
        string what = undo ? "Undid" : "Redid";
        return LiveRequests.Answer(line, done == 0 ? $"Nothing to {(undo ? "undo" : "redo")}." : $"{what} {done} step(s).",
                                   isError: false);
    }

    /// <summary>The session's documents made the window's tabs as they are now, the shown one current.</summary>
    private void Fill()
    {
        IReadOnlyList<DocumentTab> tabs = _canvas.Tabs;
        foreach (EditorSession.Open gone in _session.Documents.Where(open => open.Tag is not DocumentTab tab || !tabs.Contains(tab)).ToList())
            _session.Close(gone);

        EditorSession.Open? shown = null;
        for (int index = 0; index < tabs.Count; index++)
        {
            DocumentTab tab = tabs[index];
            if (tab.Document is not CanvasDocument document) continue;

            EditorSession.Open? open = _session.Documents.FirstOrDefault(each => ReferenceEquals(each.Tag, tab));
            if (open is null)
            {
                open = _session.Add(document, CanvasView.TabTitle(tab), tab.FilePath);
                open.Tag = tab;
            }
            open.Document = document;
            open.Path = tab.FilePath;
            open.Title = CanvasView.TabTitle(tab);
            open.Active = tab.Chosen.Count > 0 ? tab.Chosen[^1] : document.Layers.Count > 0 ? document.Layers[^1].Id : null;
            // The person's selection is the model's too. A feather is the model's own and goes with
            // the outline it was set on: once the person selects something else, it no longer applies.
            if (!ReferenceEquals(open.Selection, tab.Selection))
            {
                open.Selection = tab.Selection;
                open.Feather = 0;
            }
            // Not owning its pixels, the session's history only drops what the last call left in it.
            open.History.Clear(document);
            _before[open] = document;
            if (index == _canvas.ActiveTab) shown = open;
        }
        if (shown is not null) _session.Select(shown);
    }

    /// <summary>What the tools did, put into the window: each changed document one undo step, new ones new tabs.</summary>
    private void Apply(string? tool)
    {
        string name = "AI: " + (tool ?? "edit");
        EditorSession.Open? current = _session.Current;

        CloseRemovedDocuments(_canvas, _before.Keys, _session.Documents);

        foreach (EditorSession.Open open in _session.Documents.ToList())
        {
            if (open.Tag is DocumentTab tab)
            {
                if (_before.TryGetValue(open, out CanvasDocument? before) && ReferenceEquals(before, open.Document)) continue;
                int index = IndexOf(tab);
                if (index < 0) continue;
                _canvas.SwitchTo(index);
                _canvas.ApplyAutomation(name, open.Document, open.Active);
            }
            else
            {
                bool photoshop = open.Path is string path && Path.GetExtension(path).ToLowerInvariant() is ".psd" or ".psb";
                _canvas.Open(open.Document, open.Path, open.Title, photoshop);
                if (_canvas.ActiveTab >= 0) open.Tag = _canvas.Tabs[_canvas.ActiveTab];
            }
        }

        // What the model selected shows as the marching ants, where the person can see and change it.
        foreach (EditorSession.Open open in _session.Documents)
            if (open.Tag is DocumentTab tab && !ReferenceEquals(open.Selection, tab.Selection)) _canvas.SetSelection(tab, open.Selection);

        // The document the model last worked on is the one on screen, as select_document asks.
        if (current?.Tag is DocumentTab shown && IndexOf(shown) is int at and >= 0) _canvas.SwitchTo(at);
    }

    /// <summary>
    /// Documents closed by an automation call must leave the window as well as the temporary
    /// session. Otherwise its tab is found by the next <see cref="Fill"/> and added back under a
    /// new <c>docN</c> id, so a document that said it closed appears to reopen.
    /// </summary>
    internal static void CloseRemovedDocuments(CanvasView canvas, IEnumerable<EditorSession.Open> before,
                                               IReadOnlyCollection<EditorSession.Open> after)
    {
        foreach (EditorSession.Open closed in before.Where(open => !after.Contains(open)).ToList())
        {
            if (closed.Tag is not DocumentTab tab) continue;
            int index = canvas.Tabs.IndexOfReference(tab);
            if (index < 0) continue;
            canvas.SwitchTo(index);
            canvas.Close();
        }
    }

    private int IndexOf(DocumentTab tab)
    {
        IReadOnlyList<DocumentTab> tabs = _canvas.Tabs;
        for (int i = 0; i < tabs.Count; i++)
            if (ReferenceEquals(tabs[i], tab)) return i;
        return -1;
    }

    public void Dispose()
    {
        _stop.Cancel();
        while (_queue.TryDequeue(out Pending? pending)) pending.Answer.TrySetCanceled();
        _session.Dispose();
        _services.Dispose();
    }
}
