using CsCode.Core.Debugging;
using CsCode.Core.Platform;

namespace CsCode.Panels.Debug.Console;

/// <summary>One REPL line of the transcript (an entered expression, a result or an error), placed after the program output it followed.</summary>
/// <param name="After">The <see cref="DebugOutputEventArgs.Sequence"/> of the output event that was the newest one when the line was added
/// (0: none). A console shows the line after the last output event it shows whose sequence is not greater: the event itself, or its copy in
/// the history (an ended session's relabelled events and an oversize batch whose head was cut keep the number), or, once the event left the
/// history, the output that came before it.</param>
internal sealed record ReplLine(ConsoleLineKind Kind, string Text, long After);

/// <summary>
/// What the Debug Console's REPL keeps across console view-models. The shell disposes the view-model whenever the window is hidden (also
/// the automatic hide at the end of every debug session); program output comes back from <see cref="IDebugOutputHistory"/>, and this holds
/// the rest: the input history (persisted in <see cref="IStateStorage"/>, application scope, like a shell history) and the transcript of
/// entered expressions with their results, which a new console interleaves with the output history. App lifetime, bounded.
/// </summary>
public sealed class ReplState
{
    public const string HistoryKey = "debug.replHistory";
    /// <summary>REPL lines kept for the next console (the console itself shows at most 5,000 lines of everything).</summary>
    public const int MaxTranscriptLines = 1_000;

    private readonly IStateStorage? _state;
    private readonly object _gate = new();
    private readonly Queue<ReplLine> _transcript = new();

    public ReplState(IStateStorage? state)
    {
        _state = state;
        List<string>? saved = null;
        try { saved = state?.Get<List<string>>(HistoryKey); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or NotSupportedException) { /* start empty */ }
        if (saved is not null)
            foreach (var entry in saved.Where(e => !string.IsNullOrWhiteSpace(e)).TakeLast(ReplHistory.DefaultCapacity)) History.Add(entry);
    }

    /// <summary>↑/↓ history, shared by every console view-model (UI thread only; each new console resets the cursor).</summary>
    public ReplHistory History { get; } = new();

    /// <summary>Adds an entered expression to the history and saves it (best effort).</summary>
    public void AddHistory(string entry)
    {
        History.Add(entry);
        if (_state is null) return;
        try { _state.Set(HistoryKey, History.Entries.ToList()); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { /* persistence is best effort */ }
    }

    /// <summary>Keeps a REPL line for the next console, placed after <paramref name="after"/> (the newest output when it was added).</summary>
    internal ReplLine Record(ConsoleLineKind kind, string text, DebugOutputEventArgs? after)
    {
        var line = new ReplLine(kind, text, after?.Sequence ?? 0);
        lock (_gate)
        {
            _transcript.Enqueue(line);
            while (_transcript.Count > MaxTranscriptLines) _transcript.Dequeue();
        }
        return line;
    }

    internal IReadOnlyList<ReplLine> Transcript()
    {
        lock (_gate) return [.. _transcript];
    }

    /// <summary>The console was cleared (here or through <see cref="IDebugOutputHistory.ClearOutputHistory"/>).</summary>
    public void ClearTranscript()
    {
        lock (_gate) _transcript.Clear();
    }
}
