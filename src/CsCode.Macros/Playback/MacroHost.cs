using CsCode.Core.Platform;

namespace CsCode.Macros;

/// <summary>
/// The editor side of macro playback, implemented by the shell over the active editor (src/CsCode.App/Macros). Every member is called on the UI
/// thread, once per step: an implementation resolves the active editor on each call and keeps no reference to it in between.
/// </summary>
public interface IMacroHost
{
    /// <summary>Why key and text steps cannot run now (no editor, Vim emulation on...), or null when they can.</summary>
    string? TypingUnavailableReason { get; }

    /// <summary>Why recording cannot start now (Vim emulation is on and has its own macros), or null when it can.</summary>
    MacroBlock? RecordingBlocked { get; }

    /// <summary>Presses <paramref name="key"/> in the active editor as the keyboard would. Null when done, otherwise why it could not.</summary>
    string? SendKey(KeyStroke key);

    /// <summary>Types <paramref name="text"/> (one character, or one IME piece) in the active editor. Null when done, otherwise why it could not.</summary>
    string? TypeText(string text);

    /// <summary>The active editor's caret, or null when no editor is active.</summary>
    MacroCaret? Caret { get; }

    /// <summary>The lines (0-based, inclusive) of the active editor's selection; a selection that ends at the start of a line does not include it.</summary>
    (int First, int Last)? SelectedLines { get; }

    /// <summary>Puts the caret at the start of <paramref name="line"/> of the active editor (no selection). False when it cannot.</summary>
    bool MoveCaretToLineStart(int line);

    /// <summary>
    /// Starts making the playback's edits ONE undo step per editor it acts on (called when a playback starts; the player calls
    /// <see cref="IMacroUndoScope.Enter"/> before every step and disposes the scope when the playback ends, however it ends). Null when the
    /// host cannot (then every step undoes as it would when typed).
    /// </summary>
    IMacroUndoScope? BeginUndoScope() => null;
}

/// <summary>The undo grouping of one playback (<see cref="IMacroHost.BeginUndoScope"/>). UI thread.</summary>
public interface IMacroUndoScope : IDisposable
{
    /// <summary>Before a step: the active editor's edits from now on join the playback's undo step for that editor (its group opens on first use).</summary>
    void Enter();
}

/// <summary>Where the caret of the active editor is (0-based), how long its document is and which version of it.</summary>
public readonly record struct MacroCaret(Uri Document, int Line, int Column, int LineCount, int Version)
{
    /// <summary>Lines below the caret's line.</summary>
    public int LinesBelow => LineCount - 1 - Line;
}

/// <summary>The UI thread, as the player needs it (the shell supplies one over its dispatcher).</summary>
public interface IMacroScheduler
{
    /// <summary>Whether the caller is on the UI thread.</summary>
    bool IsUiThread { get; }

    /// <summary>Runs <paramref name="action"/> on the UI thread after pending input and rendering: the next slice of a playback.</summary>
    void Post(Action action);
}

/// <summary>How often a macro plays.</summary>
public enum MacroRepeat
{
    /// <summary><see cref="MacroPlayOptions.Times"/> times.</summary>
    Times,
    /// <summary>Again and again while each run moves the caret towards the end of the file (every line from the caret's to the last).</summary>
    UntilEndOfFile,
    /// <summary>Again and again until a recorded search finds nothing more (the macro needs a search step).</summary>
    UntilSearchFails,
    /// <summary>Once per line of the selection, starting at the beginning of each line.</summary>
    OnSelectedLines,
}

public sealed record MacroPlayOptions(MacroRepeat Repeat = MacroRepeat.Times, int Times = 1)
{
    public static readonly MacroPlayOptions Once = new();

    public string Describe() => Repeat switch
    {
        MacroRepeat.Times when Times == 1 => "once",
        MacroRepeat.Times => $"{Times:N0} times",
        MacroRepeat.UntilEndOfFile => "until the end of the file",
        MacroRepeat.UntilSearchFails => "until the search fails",
        _ => "on each selected line",
    };
}

public enum MacroPlayStatus
{
    /// <summary>Every run finished (for the "until" modes: the condition ended it).</summary>
    Completed,
    /// <summary>Esc, <c>macro.stop</c>, or the caller's token.</summary>
    Cancelled,
    /// <summary>A step failed (a command threw or does not exist, a search found nothing, no editor...): playback stopped there.</summary>
    Failed,
    /// <summary>The runaway guard (step or time budget, nesting depth, a macro that runs itself) stopped it.</summary>
    Stopped,
    /// <summary>It did not start (already playing or recording, Vim on for a macro that types, no search step for "until search fails"...).</summary>
    Refused,
}

/// <param name="Runs">Runs started (the last one may be partial when playback stopped inside it).</param>
/// <param name="Steps">Steps executed in total (nested macros included).</param>
/// <param name="Message">Why it stopped (for the user), or null when it simply completed.</param>
/// <param name="Step">The 1-based step of the macro that stopped it, when a step did.</param>
public sealed record MacroPlayResult(MacroPlayStatus Status, int Runs, long Steps, string? Message, int? Step = null)
{
    public bool Succeeded => Status == MacroPlayStatus.Completed;
}

/// <summary>Runaway guards and the UI budget of a playback.</summary>
public sealed record MacroLimits
{
    public long MaxSteps { get; init; } = MacroSettings.DefaultMaxPlaybackSteps;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(MacroSettings.DefaultMaxPlaybackSeconds);
    /// <summary>How deep macros may run macros (<c>macro.run.x</c> steps).</summary>
    public int MaxDepth { get; init; } = 8;
    /// <summary>
    /// Target time on the UI thread per dispatcher callback. The player stops a slice before a step whose cost (the most expensive of the recent
    /// steps) would take the slice past it. Well below the 4 ms callback budget (docs/ARCHITECTURE.md §6): a real editor's keystroke cost varies
    /// (a collection, rebuilding the visual lines after an edit) and the margin keeps such a step within the budget. Measured in the real editor
    /// playing a typing macro 10,000 times (headless, load average 2-3): slices p99 3.6-3.8 ms with this target, 4.1-4.4 ms with 1.5 ms,
    /// 3.8-5.1 ms with 2 ms, 5.6-5.9 ms with 3 ms. A slice still runs at least one step, so one step that alone takes longer is not split.
    /// </summary>
    public TimeSpan SliceBudget { get; init; } = TimeSpan.FromMilliseconds(1);

    public static readonly MacroLimits Default = new();
}
