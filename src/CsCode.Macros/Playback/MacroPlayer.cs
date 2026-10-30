using System.Diagnostics;
using System.Text;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Macros;

/// <summary>
/// Plays macros on the UI thread through the same paths the user's input takes: key and text steps go to the active editor
/// (<see cref="IMacroHost"/>), command steps through <see cref="ICommandService"/>. Playback runs in <b>slices</b>: a dispatcher callback runs steps
/// until the next one would exceed <see cref="MacroLimits.SliceBudget"/>, then the player posts the rest (<see cref="IMacroScheduler.Post"/>), so
/// input, rendering and Esc are handled between slices and a long macro, or one played 10,000 times, never freezes the window. Typed text is
/// replayed character by character (as typed) and may be split across slices.
/// <list type="bullet">
/// <item><b>Abort on error</b>: a command that throws or does not exist, a search that finds nothing, a key or text the editor cannot take (no
/// editor, a read-only file, Vim switched on) stops playback at that step and the result says why.</item>
/// <item><b>Runaway guard</b>: at most <see cref="MacroLimits.MaxSteps"/> steps and <see cref="MacroLimits.MaxDuration"/> per playback (nested
/// macros included), at most <see cref="MacroLimits.MaxDepth"/> nested macros, and a macro that (indirectly) runs itself is stopped.</item>
/// <item><b>Nesting</b>: a command step that plays another macro (<c>macro.run.x</c>) joins the running playback (same guards, Esc stops all);
/// any other playback request while one runs is refused.</item>
/// </list>
/// The player keeps no editor reference: the host resolves the active editor on every step.
/// </summary>
public sealed class MacroPlayer
{
    /// <summary>The playback whose command step is executing in this async flow: a macro played from that step joins it.</summary>
    private static readonly AsyncLocal<Run?> s_current = new();

    private const int SliceHistory = 4096;

    private readonly ICommandService _commands;
    private readonly IMacroScheduler _scheduler;
    private readonly Func<IMacroHost?> _host;
    private readonly TimeProvider _time;
    private readonly Emitter<bool> _playingChanged = new();
    private readonly double[] _slices = new double[SliceHistory];
    private int _sliceCount;
    private Root? _active;

    public MacroPlayer(ICommandService commands, IMacroScheduler scheduler, Func<IMacroHost?> host, TimeProvider? time = null)
    {
        _commands = commands;
        _scheduler = scheduler;
        _host = host;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Read when a playback starts.</summary>
    public Func<MacroLimits> Limits { get; set; } = () => MacroLimits.Default;

    public bool IsPlaying => _active is not null;

    /// <summary>Fires on the UI thread when a playback starts (true) and ends (false).</summary>
    public IEventSource<bool> PlayingChanged => _playingChanged;

    /// <summary>
    /// UI-thread time of the recent playback slices (each dispatcher callback that ran steps), in milliseconds, oldest first; bounded
    /// (diagnostics and the responsiveness tests).
    /// </summary>
    public IReadOnlyList<double> SliceDurations
    {
        get
        {
            var n = Math.Min(_sliceCount, SliceHistory);
            var list = new double[n];
            for (var i = 0; i < n; i++) list[i] = _slices[(_sliceCount - n + i) % SliceHistory];
            return list;
        }
    }

    public void ResetSliceDurations() => _sliceCount = 0;

    /// <summary>Stops the playback (the current step finishes; a command that is still running is no longer waited for).</summary>
    public void Cancel() => _active?.Cancel();

    /// <summary>
    /// Plays <paramref name="macro"/>. Completes when playback ended, with how it ended (never throws for a failing step; the result says it).
    /// Called from a command step of a running playback, the macro plays as part of it.
    /// </summary>
    public Task<MacroPlayResult> PlayAsync(Macro macro, MacroPlayOptions? options = null, CancellationToken ct = default)
    {
        options ??= MacroPlayOptions.Once;
        if (s_current.Value is { Root.IsOver: false } parent) return PlayNestedAsync(parent, macro, options);
        if (_active is not null) return Task.FromResult(new MacroPlayResult(MacroPlayStatus.Refused, 0, 0, "A macro is already playing (Esc stops it)."));
        return PlayRootAsync(macro, options, ct);
    }

    private async Task<MacroPlayResult> PlayRootAsync(Macro macro, MacroPlayOptions options, CancellationToken ct)
    {
        var limits = Limits();
        var root = new Root(this, limits, ct);
        _active = root;
        try
        {
            if (!_scheduler.IsUiThread) await root.HopAsync();
            root.BeginSlice();
            _playingChanged.Fire(true);
            var result = await PlayRunAsync(new Run(root, macro, null, 0), options).ConfigureAwait(true);
            root.EndSlice();
            return result;
        }
        finally
        {
            root.Finish();
            if (ReferenceEquals(_active, root)) _active = null;
            _playingChanged.Fire(false);
        }
    }

    private async Task<MacroPlayResult> PlayNestedAsync(Run parent, Macro macro, MacroPlayOptions options)
    {
        var root = parent.Root;
        for (var r = parent; r is not null; r = r.Parent)
            if (ReferenceEquals(r.Macro, macro) || (macro.IsNamed && r.Macro.Name == macro.Name))
                return root.Stop(MacroPlayStatus.Stopped, $"{macro.Subject} runs itself (through {parent.Macro.DisplayName}); it was stopped.");
        if (parent.Depth + 1 > root.Limits.MaxDepth)
            return root.Stop(MacroPlayStatus.Stopped, $"Macros are nested more than {root.Limits.MaxDepth} deep; playback was stopped.");
        // the step that started this is running in the root's flow: continue on the UI thread inside its slice
        if (!_scheduler.IsUiThread) await root.HopAsync();
        return await PlayRunAsync(new Run(root, macro, parent, parent.Depth + 1), options).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------------------------------------------------ repetition

    private async Task<MacroPlayResult> PlayRunAsync(Run run, MacroPlayOptions options)
    {
        var macro = run.Macro;
        var root = run.Root;
        if (macro.TypesIntoEditor && _host()?.TypingUnavailableReason is { } why)
            return root.Stop(MacroPlayStatus.Refused, $"{macro.Subject} types into the editor and cannot play: {why}");
        switch (options.Repeat)
        {
            case MacroRepeat.Times:
            {
                var times = Math.Max(1, options.Times);
                for (var i = 0; i < times; i++)
                {
                    if (await PlayOnceAsync(run).ConfigureAwait(true) is { } end) return end;
                }
                return root.Result(MacroPlayStatus.Completed, null);
            }
            case MacroRepeat.UntilEndOfFile:
            {
                if (_host()?.Caret is not { } caret) return root.Stop(MacroPlayStatus.Refused, "Play until end of file needs an editor.");
                while (true)
                {
                    if (await PlayOnceAsync(run).ConfigureAwait(true) is { } end) return end;
                    // every run must bring the caret closer to the end of the file (a run that deletes lines counts); the first that does not,
                    // typically the run on the last line, is the last one
                    if (_host()?.Caret is not { } after || after.Document != caret.Document || after.LinesBelow >= caret.LinesBelow)
                        return root.Result(MacroPlayStatus.Completed, null);
                    caret = after;
                }
            }
            case MacroRepeat.UntilSearchFails:
            {
                if (!HasSearch(macro)) return root.Stop(MacroPlayStatus.Refused, $"{macro.Subject} has no search step: record one (Find, then Enter or F3) to play it until the search fails.");
                var before = _host()?.Caret;
                while (true)
                {
                    var end = await PlayOnceAsync(run).ConfigureAwait(true);
                    if (end is not null)
                    {
                        if (end.Status != MacroPlayStatus.Failed || !root.SearchFailed) return end;
                        // the normal end: the run in which the search failed did not complete
                        return root.Override(new MacroPlayResult(MacroPlayStatus.Completed, root.Runs - 1, root.Steps, "The search found nothing more."));
                    }
                    var after = _host()?.Caret;
                    if (after == before) return root.Result(MacroPlayStatus.Completed, "The macro made no change in its last run; playback stopped.");
                    before = after;
                }
            }
            default:
            {
                if (_host() is not { SelectedLines: { } lines, Caret: { } start }) return root.Stop(MacroPlayStatus.Refused, "Play on selected lines needs an editor.");
                var line = lines.First;
                var count = lines.Last - lines.First + 1;
                for (var i = 0; i < count; i++)
                {
                    if (_host() is not { } host || host.Caret is not { } c || c.Document != start.Document || !host.MoveCaretToLineStart(line))
                        return root.Stop(MacroPlayStatus.Failed, $"Line {line + 1} of the selection is no longer there; playback stopped.");
                    var lineCount = c.LineCount;
                    if (await PlayOnceAsync(run).ConfigureAwait(true) is { } end) return end;
                    // lines the run added or removed move the next selected line
                    line += 1 + ((_host()?.Caret?.LineCount ?? lineCount) - lineCount);
                }
                return root.Result(MacroPlayStatus.Completed, null);
            }
        }
    }

    private static bool HasSearch(Macro macro) =>
        macro.Steps.Any(static s => s is CommandStep c && (c.Command == MacroCommands.Find || c.Command.StartsWith(MacroCommands.RunPrefix, StringComparison.Ordinal)));

    // ------------------------------------------------------------------------------------------------------------ one run

    /// <summary>Plays the steps once; null when every step ran, otherwise the result that ends playback.</summary>
    private async Task<MacroPlayResult?> PlayOnceAsync(Run run)
    {
        var root = run.Root;
        if (run.Depth == 0) root.Runs++;
        var steps = run.Macro.Steps;
        for (var i = 0; i < steps.Count; i++)
        {
            if (root.Check() is { } stop) return stop;
            await root.MaybeYieldAsync().ConfigureAwait(true);
            if (root.Check() is { } stopped) return stopped;
            var step = steps[i];
            string? error;
            try
            {
                switch (step)
                {
                    case KeyStep k:
                    {
                        var started = _time.GetTimestamp();
                        error = _host() is { } host ? host.TypingUnavailableReason ?? host.SendKey(k.Key) : "No editor is open.";
                        root.Measure(started);
                        break;
                    }
                    case TextStep t:
                        error = await TypeAsync(root, t.Text).ConfigureAwait(true);
                        if (root.Check() is { } cancelled) return cancelled;
                        break;
                    case CommandStep c:
                        error = await ExecuteAsync(run, c).ConfigureAwait(true);
                        if (root.Check() is { } halted) return halted;
                        break;
                    case PauseStep p:
                        error = null;
                        root.EndSlice();
                        try { await Task.Delay(TimeSpan.FromMilliseconds(p.Milliseconds), _time, root.Token).ConfigureAwait(true); }
                        catch (OperationCanceledException) { /* Esc during the pause: Check below ends playback */ }
                        await root.HopAsync();
                        root.BeginSlice();
                        if (root.Check() is { } paused) return paused;
                        break;
                    default:
                        error = $"Unknown step {step}.";
                        break;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // the editor threw for a key or a character: a bug elsewhere, but it must end this playback cleanly, not leave it running
                error = $"The editor failed on {step}: {ex.Message}";
            }
            root.Steps++;
            if (error is not null) return root.Stop(MacroPlayStatus.Failed, error, i + 1, run);
        }
        return null;
    }

    /// <summary>Types one character (a UTF-16 surrogate pair stays together) at a time, yielding between characters when the slice is used up.</summary>
    private async ValueTask<string?> TypeAsync(Root root, string text)
    {
        for (var i = 0; i < text.Length;)
        {
            var n = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            if (i > 0)
            {
                await root.MaybeYieldAsync().ConfigureAwait(true);
                if (root.IsCancelled) return null;
            }
            if (_host() is not { } host) return "No editor is open.";
            if (host.TypingUnavailableReason is { } why) return why;
            var started = _time.GetTimestamp();
            if (host.TypeText(n == 1 ? Single(text[i]) : text.Substring(i, 2)) is { } error) return error;
            root.Measure(started);
            i += n;
        }
        return null;
    }

    private static readonly string[] s_ascii = Enumerable.Range(0, 128).Select(c => ((char)c).ToString()).ToArray();

    private static string Single(char c) => c < 128 ? s_ascii[c] : c.ToString();

    private async ValueTask<string?> ExecuteAsync(Run run, CommandStep step)
    {
        var root = run.Root;
        ValueTask<object?> pending;
        var started = _time.GetTimestamp();
        var previous = s_current.Value;
        s_current.Value = run; // a macro the command plays joins this playback (the command's flow captures it)
        try { pending = _commands.ExecuteAsync(step.Command, step.ArgumentArray()); }
        catch (Exception ex) { return Describe(step, ex, root); }
        finally { s_current.Value = previous; }
        if (pending.IsCompletedSuccessfully)
        {
            root.Measure(started);
            return null;
        }
        try
        {
            if (pending.IsCompleted) await pending.ConfigureAwait(true); // completed with an exception: observe it
            else
            {
                // the command runs on: this slice ends, the next starts on the UI thread once it completes (or Esc stops waiting for it)
                root.EndSlice();
                try { await pending.AsTask().WaitAsync(root.Token).ConfigureAwait(true); }
                finally
                {
                    await root.HopAsync();
                    root.BeginSlice();
                }
            }
            return null;
        }
        catch (OperationCanceledException) when (root.IsCancelled) { return null; }
        catch (Exception ex) { return Describe(step, ex, root); }
    }

    private static string Describe(CommandStep step, Exception ex, Root root)
    {
        switch (ex)
        {
            case MacroSearchFailedException:
                root.SearchFailed = true;
                return ex.Message;
            case CommandNotFoundException:
                return $"Command '{step.Command}' is not available.";
            case OperationCanceledException:
                return $"Command '{step.Command}' was cancelled.";
            default:
                return $"Command '{step.Command}' failed: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------------------------------------------------ state

    private void RecordSlice(double ms)
    {
        _slices[_sliceCount % SliceHistory] = ms;
        _sliceCount++;
        if (_sliceCount >= SliceHistory * 2) _sliceCount -= SliceHistory; // keeps the ring index bounded without losing order
    }

    private sealed record Run(Root Root, Macro Macro, Run? Parent, int Depth);

    /// <summary>
    /// <c>await</c> resumes inside a callback posted through the scheduler (the UI thread, below input priority). A task completed from a posted
    /// callback would not do: with a SynchronizationContext on the thread, the runtime moves such continuations to the thread pool.
    /// </summary>
    private readonly struct HopAwaitable(IMacroScheduler scheduler) : System.Runtime.CompilerServices.ICriticalNotifyCompletion
    {
        public HopAwaitable GetAwaiter() => this;
        public bool IsCompleted => false;
        public void GetResult() { }
        public void OnCompleted(Action continuation) => scheduler.Post(continuation);
        public void UnsafeOnCompleted(Action continuation) => scheduler.Post(continuation);
    }

    /// <summary>One playback, shared by the macros it nests: cancellation, guards, slice timing and the result bookkeeping.</summary>
    private sealed class Root
    {
        private readonly MacroPlayer _owner;
        private readonly CancellationTokenSource _cts;
        private readonly long _started;
        private readonly double[] _recentCosts = new double[8];
        private int _costIndex;
        private long _sliceStart;
        private bool _inSlice;
        private MacroPlayResult? _stopped;

        public Root(MacroPlayer owner, MacroLimits limits, CancellationToken external)
        {
            _owner = owner;
            Limits = limits;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
            _started = owner._time.GetTimestamp();
        }

        public MacroLimits Limits { get; }
        public long Steps { get; set; }
        public int Runs { get; set; }
        public bool SearchFailed { get; set; }
        public bool IsOver { get; private set; }
        public CancellationToken Token => _cts.Token;
        public bool IsCancelled => _cts.IsCancellationRequested;

        public void Cancel()
        {
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>A reason to end now: an earlier stop (a nested macro's), cancellation, or a guard.</summary>
        public MacroPlayResult? Check()
        {
            if (_stopped is not null) return _stopped;
            if (IsCancelled) return Stop(MacroPlayStatus.Cancelled, "Macro playback was stopped.");
            if (Steps >= Limits.MaxSteps)
                return Stop(MacroPlayStatus.Stopped, $"Macro playback was stopped after {Steps:N0} steps (runaway guard, setting {MacroSettings.MaxPlaybackSteps}).");
            if (_owner._time.GetElapsedTime(_started) > Limits.MaxDuration)
                return Stop(MacroPlayStatus.Stopped, $"Macro playback was stopped after {Limits.MaxDuration.TotalSeconds:N0} s (runaway guard, setting {MacroSettings.MaxPlaybackSeconds}).");
            return null;
        }

        /// <summary>Ends the playback with <paramref name="status"/> (the first stop wins: nested macros and their callers see the same result).</summary>
        public MacroPlayResult Stop(MacroPlayStatus status, string message, int? step = null, Run? run = null)
        {
            if (_stopped is not null) return _stopped;
            if (run is { Depth: > 0 }) message = $"{message} (in {run.Macro.DisplayName}, step {step})";
            _stopped = new MacroPlayResult(status, Runs, Steps, message, run is { Depth: > 0 } ? null : step);
            return _stopped;
        }

        public MacroPlayResult Result(MacroPlayStatus status, string? message) => _stopped ?? new MacroPlayResult(status, Runs, Steps, message);

        /// <summary>Replaces the stop with <paramref name="result"/> (a failed search that is the normal end of "until search fails").</summary>
        public MacroPlayResult Override(MacroPlayResult result) => _stopped = result;

        public void BeginSlice()
        {
            _sliceStart = _owner._time.GetTimestamp();
            _inSlice = true;
        }

        public void EndSlice()
        {
            if (!_inSlice) return;
            _inSlice = false;
            _owner.RecordSlice(_owner._time.GetElapsedTime(_sliceStart).TotalMilliseconds);
        }

        public void Measure(long started)
        {
            _recentCosts[_costIndex++ & 7] = _owner._time.GetElapsedTime(started).TotalMilliseconds;
        }

        /// <summary>Ends the slice when the next step (estimated by the most expensive of the last eight) would take it past the budget.</summary>
        public ValueTask MaybeYieldAsync()
        {
            if (!_inSlice) return ValueTask.CompletedTask;
            var estimate = 0.0;
            foreach (var c in _recentCosts) estimate = Math.Max(estimate, c);
            var used = _owner._time.GetElapsedTime(_sliceStart).TotalMilliseconds;
            if (used == 0 || used + estimate <= Limits.SliceBudget.TotalMilliseconds) return ValueTask.CompletedTask;
            return YieldAsync();
        }

        private async ValueTask YieldAsync()
        {
            EndSlice();
            await HopAsync();
            BeginSlice();
        }

        /// <summary>Continues on the UI thread in a later dispatcher callback (after pending input and rendering).</summary>
        public HopAwaitable HopAsync() => new(_owner._scheduler);

        public void Finish()
        {
            EndSlice();
            IsOver = true;
            _cts.Dispose();
        }
    }
}

/// <summary>Formats playback results for the user.</summary>
public static class MacroPlayResultText
{
    public static string Describe(Macro macro, MacroPlayOptions options, MacroPlayResult result)
    {
        var sb = new StringBuilder();
        switch (result.Status)
        {
            case MacroPlayStatus.Completed when result.Runs == 0:
                sb.Append($"{macro.Subject} did not complete a run.");
                if (result.Message is { } why) sb.Append(' ').Append(why);
                break;
            case MacroPlayStatus.Completed:
                sb.Append($"{macro.Subject} played ");
                sb.Append(options.Repeat == MacroRepeat.Times ? options.Describe() : result.Runs == 1 ? "once" : $"{result.Runs:N0} times");
                sb.Append('.');
                if (result.Message is { } note) sb.Append(' ').Append(note);
                break;
            case MacroPlayStatus.Cancelled:
                sb.Append($"{macro.Subject} was stopped (run {result.Runs:N0}).");
                break;
            case MacroPlayStatus.Refused:
                sb.Append(result.Message ?? $"{macro.Subject} did not play.");
                break;
            default:
                sb.Append($"{macro.Subject} stopped");
                if (result.Step is { } step) sb.Append($" at step {step} of {macro.Steps.Count}");
                if (result.Runs > 1 || options.Repeat != MacroRepeat.Times || options.Times > 1) sb.Append($" (run {result.Runs:N0})");
                sb.Append(": ").Append(result.Message);
                break;
        }
        return sb.ToString();
    }
}
