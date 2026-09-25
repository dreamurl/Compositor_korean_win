using System.Text;
using System.Text.Json;
using static Compositor_korean_win.Core.ToolSchema;

namespace Compositor_korean_win.Core;

/// <summary>
/// Several tool calls as one: one undo step, and all or nothing.
/// </summary>
/// <remarks>
/// <para>
/// A model building a poster makes dozens of calls, and many only make sense together — a group
/// and the layers moved into it, a selection and the fill that keeps to it. Sent one at a time, a
/// failure halfway leaves the document half made, and every call is a round trip.
/// </para>
/// <para>
/// The history already joins nested edits into one step (<see cref="DocumentHistory.Begin"/>), so
/// the batch opens an edit on every document before the first call and closes it after the last.
/// When a call fails, the documents are put back as they were before the batch — the step then
/// records nothing — and documents the batch opened are closed.
/// </para>
/// <para>
/// Pixels a call made and a later call replaced belong to no step: the history only ever sees the
/// documents before and after the whole batch. They are released here, as the history releases
/// the pixels of a step it drops.
/// </para>
/// </remarks>
public sealed partial class McpTools
{
    private bool _batching;

    private void DefineBatchTool()
    {
        Define("batch",
            "Run several tool calls as one: 'calls' is [{\"tool\": name, \"arguments\": {…}}, …], run in order. They make one " +
            "undo step, and if one fails none of them takes effect — the documents go back as they were — and the answer " +
            "says which call failed and why. Renders inside a batch come back as images. undo, redo, close_document and " +
            "batch cannot be inside one.",
            Build(new Property("calls", "array", "The calls, in order: [{\"tool\": …, \"arguments\": {…}}, …].", Required: true, Items: "object"),
                  Bool("keep_going", "Carry on past a failed call and keep what worked, instead of undoing everything. Default false.")),
            Batch);
    }

    private ToolResult Batch(ToolArguments arguments)
    {
        if (_batching) throw new ToolException("A batch cannot contain another batch.");
        if (arguments.Raw("calls") is not { ValueKind: JsonValueKind.Array } calls || calls.GetArrayLength() == 0)
            throw new ToolException("'calls' is a list of {\"tool\": …, \"arguments\": {…}}.");
        bool keepGoing = arguments.Bool("keep_going") ?? false;

        var before = _session.Documents.Select(open => new Before(open, open.Document, open.Active, open.Selection, open.Feather)).ToList();
        EditorSession.Open? current = _session.Current;
        foreach (Before entry in before) entry.Open.History.Begin("Batch", entry.Document, entry.Active);

        var passing = new HashSet<PixelBuffer>();
        var report = new StringBuilder();
        var images = new List<ToolContent>();
        int count = 0, failures = 0;
        string? failure = null;

        _batching = true;
        try
        {
            foreach (JsonElement call in calls.EnumerateArray())
            {
                count++;
                (string tool, ToolResult result) = Run(call);
                foreach (Before entry in before) Collect(passing, entry.Open.Document);

                string text = string.Join("\n", result.Content.Where(part => part.Type == "text").Select(part => part.Text));
                if (text.Length > 1500) text = text[..1500] + "…";
                report.Append(count).Append(". ").Append(tool).Append(result.IsError ? ": FAILED — " : ": ").AppendLine(text);
                images.AddRange(result.Content.Where(part => part.Type != "text"));

                if (!result.IsError) continue;
                failures++;
                failure ??= $"call {count} ({tool}) failed: {text}";
                if (!keepGoing) break;
            }
        }
        finally
        {
            _batching = false;
        }

        bool undone = failures > 0 && !keepGoing;
        if (undone)
        {
            foreach (EditorSession.Open made in _session.Documents.Where(open => !before.Any(entry => ReferenceEquals(entry.Open, open))).ToList())
                _session.Close(made);
            foreach (Before entry in before)
            {
                entry.Open.Document = entry.Document;
                entry.Open.Active = entry.Active;
                entry.Open.Selection = entry.Selection;
                entry.Open.Feather = entry.Feather;
            }
            if (current is not null && _session.Documents.Contains(current)) _session.Select(current);
        }

        foreach (Before entry in before) entry.Open.History.End(entry.Open.Document, entry.Open.Active);

        var kept = new HashSet<PixelBuffer>();
        foreach (Before entry in before)
        {
            Collect(kept, entry.Document);
            Collect(kept, entry.Open.Document);
        }
        foreach (PixelBuffer buffer in passing)
            if (!kept.Contains(buffer)) buffer.Release();

        string headline = undone
            ? $"The batch was undone: {failure}. Nothing it did was kept."
            : failures > 0
                ? $"Ran {count} calls as one step; {failures} failed and the rest were kept."
                : $"Ran {count} call{(count == 1 ? "" : "s")} as one step.";
        var answer = new ToolResult { IsError = undone };
        answer.Content.Add(ToolContent.Of(headline + "\n" + report.ToString().TrimEnd()));
        answer.Content.AddRange(images);
        return answer;
    }

    private sealed record Before(EditorSession.Open Open, CanvasDocument Document, Guid? Active, DocumentSelection? Selection, double Feather);

    /// <summary>One call of a batch, run as a call on its own would be, its failure as its answer.</summary>
    private (string Tool, ToolResult Result) Run(JsonElement call)
    {
        if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("tool", out JsonElement named)
            || named.ValueKind != JsonValueKind.String || named.GetString() is not { Length: > 0 } tool)
            return ("?", ToolResult.Error("Each call is {\"tool\": name, \"arguments\": {…}}."));
        if (tool is "batch" or "undo" or "redo" or "close_document")
            return (tool, ToolResult.Error($"{tool} cannot be inside a batch."));

        var arguments = new ToolArguments(call.TryGetProperty("arguments", out JsonElement given) ? given : default);
        try
        {
            return (tool, Call(tool, arguments));
        }
        catch (ToolException exception)
        {
            return (tool, ToolResult.Error(exception.Message));
        }
        catch (ProjectException exception)
        {
            return (tool, ToolResult.Error(exception.Message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                                       or ArgumentException or FormatException or JsonException)
        {
            return (tool, ToolResult.Error($"{exception.GetType().Name}: {exception.Message}"));
        }
    }

    private static void Collect(HashSet<PixelBuffer> into, CanvasDocument document)
    {
        foreach (ImageLayer layer in document.Layers)
        {
            if (layer.Image is PixelBuffer image) into.Add(image);
            if (layer.Mask?.Coverage is PixelBuffer coverage) into.Add(coverage);
        }
    }
}
