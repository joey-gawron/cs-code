using System.Diagnostics.CodeAnalysis;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;

namespace CsCode.Panels.Debug.Console;

/// <summary>One REPL line of the transcript (an entered expression, a result or an error), placed after the program output it followed.</summary>
/// <param name="After">The output event that was the newest one when the line was added; null or no longer in the history = before any
/// output still in the history.</param>
internal sealed record ReplLine(ConsoleLineKind Kind, string Text, OutputAnchor? After);

/// <summary>
/// The output event a REPL line followed, recognised in a later console's history. When a session ends the debug service replaces each of
/// its history entries with a copy whose <c>Session</c> no longer references it (<c>DebugService.ArchiveHistory</c>, a record <c>with</c>),
/// so the event is matched by instance first and then by its <see cref="DebugOutputEventArgs.Output"/> string instance, which the copy shares
/// (each DAP output batch carries a string of its own). Both are held weakly: an event references its session and its text can be large, and
/// once the history drops it there is nothing to place the line after anyway.
/// </summary>
internal sealed class OutputAnchor(DebugOutputEventArgs after)
{
    private readonly WeakReference<DebugOutputEventArgs> _event = new(after);
    private readonly WeakReference<string> _output = new(after.Output);

    public bool TryGetEvent([NotNullWhen(true)] out DebugOutputEventArgs? e) => _event.TryGetTarget(out e);
    public bool TryGetOutput([NotNullWhen(true)] out string? output) => _output.TryGetTarget(out output);
}

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

    internal void Record(ConsoleLineKind kind, string text, DebugOutputEventArgs? after)
    {
        var line = new ReplLine(kind, text, after is null ? null : new OutputAnchor(after));
        lock (_gate)
        {
            _transcript.Enqueue(line);
            while (_transcript.Count > MaxTranscriptLines) _transcript.Dequeue();
        }
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
