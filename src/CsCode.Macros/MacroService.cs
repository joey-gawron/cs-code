using System.Text.Json;
using CsCode.Core.Contributions;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Macros;

public enum MacroState { Idle, Recording, Playing }

/// <summary>Why recording cannot start (Vim emulation is on: it has its own q/@ macros), with an optional way out for the message.</summary>
public sealed record MacroBlock(string Message, string? ActionTitle = null, string? ActionCommand = null);

/// <summary>
/// The macro module: recording, the last and the saved macros, playback, and the <c>macro.*</c> commands (docs/MACROS.md).
/// <list type="bullet">
/// <item><b>Recording.</b> <see cref="StartRecording"/> collects what the user does: commands from <see cref="ICommandService.Executed"/> (what a key
/// chord, a menu item, a toolbar button or a palette pick ran, recorded as the command itself), and the keys and text the shell's taps report
/// (<see cref="RecordKey"/>, <see cref="RecordText"/>, <see cref="RecordCommand"/>). Only commands the user started are recorded: nothing a command
/// ran itself, nothing that started while the editor was handling a recorded key or text (<see cref="BeginEditorInput"/>: a completion's own
/// command), no <c>macro.*</c> command, not the palette itself. Nothing at all is recorded while the integrated terminal has the keyboard
/// (<see cref="MacroCommands.RecordingPausedWhen"/>): neither what is typed there nor the commands started there (paste, clear, scroll...).</item>
/// <item><b>Playback</b> (<see cref="MacroPlayer"/>): sliced on the UI thread, Esc / <c>macro.stop</c> cancel, abort on error, runaway guards.
/// While recording nothing plays (a macro could otherwise record itself); while playing nothing records.</item>
/// <item><b>Storage</b> (<see cref="MacroStore"/>): saved macros live in macros.json and each becomes the command <c>macro.run.&lt;name&gt;</c>
/// (palette, key bindings); the last recorded macro is kept in <see cref="IStateStorage"/> across restarts.</item>
/// </list>
/// UI-thread object, except that its events may arrive from any thread (they are marshalled through <see cref="IMacroScheduler"/>).
/// </summary>
public sealed class MacroService : IDisposable
{
    public const string LastMacroStateKey = "macros.last";
    /// <summary>The last macro is persisted only up to this size (characters of JSON).</summary>
    public const int MaxPersistedLastChars = 256 * 1024;
    /// <summary>How long a transient status bar message stays.</summary>
    public static readonly TimeSpan MessageDuration = TimeSpan.FromSeconds(4);

    /// <summary>The commands that open the palette or Quick Open: a command they run for the user counts as the user's own.</summary>
    public static readonly IReadOnlySet<string> Launchers = new HashSet<string>(StringComparer.Ordinal) { KnownCommands.CommandPalette, KnownCommands.QuickOpen };

    /// <summary>
    /// Commands that only open an input for the user (the editor's find panel): what the user then does there is recorded instead (Enter or F3
    /// in the find panel becomes a search step), so replaying the command itself would only leave the panel open.
    /// </summary>
    public static readonly IReadOnlySet<string> NotRecorded = new HashSet<string>(StringComparer.Ordinal) { KnownCommands.Find, KnownCommands.Replace };

    private const int MaxInputWindows = 32;

    private readonly ICommandService _commands;
    private readonly IMacroScheduler _scheduler;
    private readonly IConfigurationService? _config;
    private readonly IWindowService? _window;
    private readonly IStatusBarService? _status;
    private readonly IContextKeyService? _keys;
    private readonly IStateStorage? _state;
    private readonly INavigationService? _navigation;
    private readonly IContributionRegistry? _contributions;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly DisposableStore _disposables = new();
    private readonly Dictionary<string, IDisposable> _runCommands = new(StringComparer.Ordinal);
    private readonly Emitter<MacroState> _stateChanged = new();
    private readonly (long Start, long End)[] _inputWindows = new (long, long)[MaxInputWindows];
    private int _inputWindowCount;
    // while the terminal has the keyboard nothing records: since when (Stopwatch timestamp, -1: not now) and the recent such spans, for
    // commands that report after they ran (a command started in the terminal must not count when it completes after focus moved)
    private readonly (long Start, long End)[] _pausedWindows = new (long, long)[MaxInputWindows];
    private int _pausedWindowCount;
    private long _pausedSince = -1;
    private IDisposable? _pauseSubscription;
    private IDisposable? _keyContributions;
    private IDisposable? _executedSubscription;
    private IDisposable? _statusItem;
    private IDisposable? _messageItem;
    private ITimer? _messageTimer;
    private MacroRecorder? _recorder;
    private long _recordingStarted;
    private IMacroHost? _host;
    private IReadOnlyList<Macro> _named = [];
    private bool _disposed, _warnedArgs;

    public MacroService(ICommandService commands, IMacroScheduler scheduler, MacroStore? store = null, IConfigurationService? config = null,
        IWindowService? window = null, IStatusBarService? status = null, IContextKeyService? keys = null, IStateStorage? state = null,
        INavigationService? navigation = null, IContributionRegistry? contributions = null, ILogger<MacroService>? log = null, TimeProvider? time = null)
    {
        _commands = commands;
        _scheduler = scheduler;
        Store = store;
        _config = config;
        _window = window;
        _status = status;
        _keys = keys;
        _state = state;
        _navigation = navigation;
        _contributions = contributions;
        _log = log ?? (ILogger)NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        Player = new MacroPlayer(commands, scheduler, () => _host, _time) { Limits = ReadLimits };
        _disposables.Add(Player.PlayingChanged.Subscribe(OnPlayingChanged));
        if (config is not null) _disposables.Add(config.RegisterProperties(MacroSettings.Properties));
        RegisterCommands();
        if (store is not null)
        {
            store.MaxStepsPerMacro = () => Setting(MacroSettings.MaxRecordedSteps, MacroSettings.DefaultMaxRecordedSteps);
            _disposables.Add(store.Changed.Subscribe(file => Ui(() => ApplyFile(file))));
        }
    }

    public MacroStore? Store { get; }
    public MacroPlayer Player { get; }

    public MacroState State => _recorder is not null ? MacroState.Recording : Player.IsPlaying ? MacroState.Playing : MacroState.Idle;
    public bool IsRecording => _recorder is not null;

    /// <summary>Fires on the UI thread when recording or playback starts or ends.</summary>
    public IEventSource<MacroState> StateChanged => _stateChanged;

    /// <summary>The last recorded macro (unnamed), or null.</summary>
    public Macro? Last { get; private set; }

    /// <summary>The saved macros (macros.json), in file order.</summary>
    public IReadOnlyList<Macro> Named => _named;

    /// <summary>Problems found in macros.json by the last read.</summary>
    public IReadOnlyList<string> FileErrors { get; private set; } = [];

    /// <summary>The steps recorded so far (tests, the status tooltip).</summary>
    public int RecordedStepCount => _recorder?.Count ?? 0;

    /// <summary>
    /// Loads the saved macros and the last macro (off the UI thread) and starts the live reload of macros.json. Completes when both are loaded.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var loadLast = Task.Run(() => _state?.Get<string>(LastMacroStateKey) is { } json ? MacroJson.ParseSteps(json) : null, ct);
        if (Store is { } store)
        {
            store.StartWatching();
            await store.ReloadAsync(ct).ConfigureAwait(false);
        }
        if (await loadLast.ConfigureAwait(false) is { Count: > 0 } steps)
            Ui(() =>
            {
                if (Last is null && !_disposed) SetLast(new Macro("", steps), persist: false);
            });
    }

    /// <summary>The editor side (the shell's adapter). Dispose the result to detach.</summary>
    public IDisposable AttachHost(IMacroHost host)
    {
        _host = host;
        return Disposable.Create(() =>
        {
            if (ReferenceEquals(_host, host)) _host = null;
        });
    }

    // ------------------------------------------------------------------------------------------------------------ recording

    /// <summary>Starts recording (UI thread). False, with a message for the user, when it cannot.</summary>
    public bool StartRecording()
    {
        if (_disposed || _recorder is not null) return false;
        if (Player.IsPlaying)
        {
            ShowStatus("A macro is playing: recording did not start.");
            return false;
        }
        if (_host?.RecordingBlocked is { } block)
        {
            _ = ShowBlockAsync(block);
            return false;
        }
        var pauses = _config?.Get(MacroSettings.RecordPauses, false) == true ? TimeSpan.FromMilliseconds(250) : (TimeSpan?)null;
        _recorder = new MacroRecorder(Setting(MacroSettings.MaxRecordedSteps, MacroSettings.DefaultMaxRecordedSteps), pauses, _time);
        _recordingStarted = System.Diagnostics.Stopwatch.GetTimestamp(); // the clock of CommandExecuted.StartedTimestamp
        _inputOpen = false; // an input a window never reported done must not swallow this recording's commands
        _inputWindowCount = 0;
        _pausedWindowCount = 0;
        _pausedSince = PausedNow() ? _recordingStarted : -1;
        _executedSubscription = _commands.Executed.Subscribe(OnExecuted);
        _pauseSubscription = _keys?.Changed.Subscribe(OnContextKeysChanged);
        ClearMessage();
        ShowRecordingStatus();
        NotifyState();
        return true;
    }

    private void ShowRecordingStatus()
    {
        var paused = _pausedSince >= 0;
        _statusItem?.Dispose(); // first: the old registration removes the item by id
        _statusItem = _status?.Set(new StatusBarItemModel(MacroCommands.RecordingStatusId,
            paused ? "$(record) Recording macro… (paused in the terminal)" : "$(record) Recording macro…",
            paused
                ? "Recording a macro: nothing is recorded while the terminal has the keyboard. Click (or run Stop) to stop."
                : "Recording a macro: what you type and the commands you run are recorded. Click (or run Stop) to stop.",
            MacroCommands.Stop, StatusBarAlignment.Left, Priority: 1000));
    }

    /// <summary>Whether recording is paused now: the terminal has the keyboard.</summary>
    private bool PausedNow() => _keys?.Evaluate(MacroCommands.RecordingPausedWhen) == true;

    /// <summary>Recording pauses while the terminal has the keyboard (any thread: the time of the change is taken here, the rest on the UI thread).</summary>
    private void OnContextKeysChanged(IReadOnlyCollection<string> keys)
    {
        if (!keys.Contains(MacroCommands.RecordingPausedWhen)) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_scheduler.IsUiThread) UpdatePause(now);
        else _scheduler.Post(() => UpdatePause(now));
    }

    private void UpdatePause(long now)
    {
        if (_recorder is not { } recorder) return;
        var paused = PausedNow();
        if (paused == _pausedSince >= 0) return;
        if (paused) _pausedSince = now;
        else
        {
            _pausedWindows[_pausedWindowCount++ % MaxInputWindows] = (_pausedSince, now);
            _pausedSince = -1;
            recorder.RestartPauseClock(); // time spent in the terminal is not a pause of the macro
        }
        ShowRecordingStatus();
    }

    /// <summary>Whether a command that started at <paramref name="started"/> started while recording was paused.</summary>
    private bool StartedWhilePaused(long started)
    {
        if (_pausedSince >= 0 && started >= _pausedSince) return true;
        var n = Math.Min(_pausedWindowCount, MaxInputWindows);
        for (var i = 0; i < n; i++)
        {
            var (start, end) = _pausedWindows[i];
            if (started >= start && started <= end) return true;
        }
        return false;
    }

    /// <summary>
    /// Stops recording (UI thread): the recording becomes the last macro (an empty one leaves the previous last macro). <paramref name="reason"/>
    /// (e.g. Vim was switched on) is shown with the result. Returns the recorded macro, or null when nothing was recorded.
    /// </summary>
    public Macro? StopRecording(string? reason = null)
    {
        if (_recorder is not { } recorder) return null;
        _recorder = null;
        _executedSubscription?.Dispose();
        _executedSubscription = null;
        _pauseSubscription?.Dispose();
        _pauseSubscription = null;
        _pausedSince = -1;
        _statusItem?.Dispose();
        _statusItem = null;
        var steps = recorder.ToSteps();
        Macro? macro = null;
        if (steps.Count == 0) ShowStatus(reason is null ? "Macro recording stopped: nothing was recorded." : $"{reason} Nothing was recorded.");
        else
        {
            macro = new Macro("", steps);
            SetLast(macro, persist: true);
            var text = $"Macro recorded ({steps.Count:N0} step{(steps.Count == 1 ? "" : "s")}): Play Last Macro plays it, Save Last Macro As keeps it.";
            if (reason is not null) _ = _window?.ShowMessageAsync(MessageSeverity.Info, $"{reason} {text}");
            else ShowStatus(text);
        }
        NotifyState();
        return macro;
    }

    /// <summary>A key the editor handled itself (no binding): recorded as a key step (UI thread).</summary>
    public void RecordKey(KeyStroke key)
    {
        if (_recorder is { } r && !Paused && !r.AddKey(key)) RecordingFull();
    }

    /// <summary>Text the user typed into the editor (UI thread).</summary>
    public void RecordText(string text)
    {
        if (_recorder is { } r && !Paused && !r.AddText(text)) RecordingFull();
    }

    /// <summary>A step the shell recognised itself (a search in the editor's find panel: <see cref="MacroCommands.Find"/>).</summary>
    public void RecordCommand(string id, params JsonElement[] args)
    {
        if (_recorder is { } r && !Paused && !r.AddCommand(id, args)) RecordingFull();
    }

    /// <summary>Recording is paused (the terminal has the keyboard): nothing is recorded (also read live, in case a change was not seen yet).</summary>
    public bool IsRecordingPaused => _recorder is not null && Paused;

    private bool Paused => _pausedSince >= 0 || PausedNow();

    /// <summary>
    /// The editor starts handling a recorded key or text input (UI thread; paired with <see cref="EndEditorInput"/>). Commands that start
    /// until the input is done (a completion item's own command, run when Enter accepts it) are part of that input and are not recorded.
    /// </summary>
    public void BeginEditorInput()
    {
        if (_recorder is null) return;
        _inputStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _inputOpen = true;
    }

    /// <summary>The editor finished the input <see cref="BeginEditorInput"/> announced (the end of the key event's route).</summary>
    public void EndEditorInput()
    {
        if (!_inputOpen) return;
        _inputOpen = false;
        if (_recorder is null) return;
        _inputWindows[_inputWindowCount++ % MaxInputWindows] = (_inputStart, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private long _inputStart;
    private bool _inputOpen;

    private void RecordingFull()
    {
        var max = _recorder?.MaxSteps ?? 0;
        StopRecording($"The macro reached the recording limit ({max:N0} steps, setting {MacroSettings.MaxRecordedSteps}): recording stopped.");
    }

    private void OnExecuted(CommandExecuted e)
    {
        if (!_scheduler.IsUiThread)
        {
            _scheduler.Post(() => OnExecuted(e));
            return;
        }
        if (_recorder is not { } recorder || !ShouldRecord(e)) return;
        if (!TryConvertArgs(e.Args, out var args))
        {
            if (!_warnedArgs) ShowStatus($"'{e.Id}' was not recorded: its arguments cannot be stored.");
            _warnedArgs = true;
            return;
        }
        if (!recorder.AddCommand(e.Id, args)) RecordingFull();
    }

    private bool ShouldRecord(CommandExecuted e)
    {
        if (e.StartedTimestamp < _recordingStarted) return false; // started before Record (and finished after)
        if (StartedWhilePaused(e.StartedTimestamp) || PausedNow()) return false; // the terminal had the keyboard (paste, clear, scroll...)
        if (MacroCommands.IsMacroCommand(e.Id) || Launchers.Contains(e.Id) || NotRecorded.Contains(e.Id)) return false;
        foreach (var caller in e.Callers)
            if (!Launchers.Contains(caller)) return false; // a command's own work, not the user's
        if (_inputOpen && e.StartedTimestamp >= _inputStart) return false; // the editor is still handling the key that started it
        var n = Math.Min(_inputWindowCount, MaxInputWindows);
        for (var i = 0; i < n; i++)
        {
            var (start, end) = _inputWindows[i];
            if (e.StartedTimestamp >= start && e.StartedTimestamp <= end) return false; // started by the editor while it handled a recorded key
        }
        return true;
    }

    private static bool TryConvertArgs(IReadOnlyList<object?> args, out JsonElement[] json)
    {
        json = args.Count == 0 ? [] : new JsonElement[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            try
            {
                json[i] = args[i] switch
                {
                    null => JsonSerializer.SerializeToElement<object?>(null),
                    JsonElement e => e.Clone(),
                    Uri u => JsonSerializer.SerializeToElement(u.IsAbsoluteUri ? u.AbsoluteUri : u.OriginalString),
                    var a => JsonSerializer.SerializeToElement(a, a.GetType(), CoreJson.Options),
                };
            }
            catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException or ArgumentException)
            {
                return false;
            }
        }
        return true;
    }

    // ------------------------------------------------------------------------------------------------------------ playback

    /// <summary>
    /// Plays <paramref name="macro"/> and reports the outcome to the user (a toast when it failed or the guard stopped it, the status bar
    /// otherwise). Refused while recording.
    /// </summary>
    public async Task<MacroPlayResult> PlayAsync(Macro macro, MacroPlayOptions? options = null, CancellationToken ct = default)
    {
        options ??= MacroPlayOptions.Once;
        if (RefusedWhileRecording() is { } refused) return refused;
        MacroPlayResult result;
        try { result = await Player.PlayAsync(macro, options, ct).ConfigureAwait(true); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Macro playback failed");
            result = new MacroPlayResult(MacroPlayStatus.Failed, 0, 0, ex.Message);
        }
        Report(macro, options, result);
        return result;
    }

    /// <summary>Nothing plays while recording (the recording would record its own playback): the refusal, reported, or null.</summary>
    private MacroPlayResult? RefusedWhileRecording()
    {
        if (_recorder is null) return null;
        const string text = "Macros do not play while a macro is being recorded: stop recording first.";
        _ = _window?.ShowMessageAsync(MessageSeverity.Info, text);
        return new MacroPlayResult(MacroPlayStatus.Refused, 0, 0, text);
    }

    /// <summary>Stops recording, or the macro that is playing.</summary>
    public void Stop()
    {
        if (_recorder is not null) StopRecording();
        else Player.Cancel();
    }

    private void Report(Macro macro, MacroPlayOptions options, MacroPlayResult result)
    {
        var text = MacroPlayResultText.Describe(macro, options, result);
        switch (result.Status)
        {
            case MacroPlayStatus.Completed when result.Message is null && result.Runs <= 1 && options.Repeat == MacroRepeat.Times:
                break; // a plain run: the edit itself is the feedback
            case MacroPlayStatus.Completed or MacroPlayStatus.Cancelled:
                ShowStatus(text);
                break;
            case MacroPlayStatus.Refused:
                _ = _window?.ShowMessageAsync(MessageSeverity.Info, text);
                break;
            default:
                _ = _window?.ShowMessageAsync(MessageSeverity.Warning, text);
                break;
        }
    }

    private MacroLimits ReadLimits() => new()
    {
        MaxSteps = Math.Max(1, Setting(MacroSettings.MaxPlaybackSteps, MacroSettings.DefaultMaxPlaybackSteps)),
        MaxDuration = TimeSpan.FromSeconds(Math.Max(1, Setting(MacroSettings.MaxPlaybackSeconds, MacroSettings.DefaultMaxPlaybackSeconds))),
    };

    private void OnPlayingChanged(bool playing)
    {
        _statusItem?.Dispose();
        _statusItem = playing
            ? _status?.Set(new StatusBarItemModel(MacroCommands.PlayingStatusId, "$(play) Playing macro… (Esc to stop)", "A macro is playing. Esc or a click stops it.",
                MacroCommands.Stop, StatusBarAlignment.Left, Priority: 1000))
            : null;
        NotifyState();
    }

    // ------------------------------------------------------------------------------------------------------------ last and saved macros

    private void SetLast(Macro macro, bool persist)
    {
        Last = macro;
        _keys?.Set(MacroCommands.HasLastContextKey, true);
        if (!persist || _state is not { } state) return;
        var steps = macro.Steps;
        _ = Task.Run(() =>
        {
            var json = MacroJson.FormatSteps(steps);
            if (json.Length <= MaxPersistedLastChars) state.Set(LastMacroStateKey, json);
            else state.Remove(LastMacroStateKey);
        });
    }

    /// <summary>Saves the last macro as <paramref name="name"/> (macros.json). Errors are reported to the user; returns whether it was saved.</summary>
    public async Task<bool> SaveLastAsync(string name, string? description = null, CancellationToken ct = default)
    {
        if (Last is not { } last)
        {
            ShowStatus("There is no recorded macro to save: record one first (Tools > Macros > Record Macro).");
            return false;
        }
        if (!MacroNames.IsValid(name))
        {
            _ = _window?.ShowMessageAsync(MessageSeverity.Warning, $"'{name}' is not a valid macro name. {MacroNames.Rule}");
            return false;
        }
        if (Store is not { } store) return false;
        var existing = _named.FirstOrDefault(m => m.Name == name);
        var macro = last with { Name = name, Description = description ?? existing?.Description, Key = existing?.Key };
        try { await store.SaveAsync(macro, ct).ConfigureAwait(true); }
        catch (MacroFileException ex)
        {
            _ = _window?.ShowMessageAsync(MessageSeverity.Error, $"Could not save macro '{name}': {ex.Message}");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = _window?.ShowMessageAsync(MessageSeverity.Error, $"Could not save macro '{name}' to {store.FilePath}: {ex.Message}");
            return false;
        }
        ShowStatus($"Macro saved as '{name}' (command {macro.CommandId}; bind it in keybindings.json or with \"key\" in {MacroCommands.FileName}).");
        return true;
    }

    private void ApplyFile(MacroFile file)
    {
        if (_disposed) return;
        var previousErrors = FileErrors;
        FileErrors = file.Errors;
        _named = file.Macros;
        SyncRunCommands(file.Macros);
        if (file.Errors.Count > 0 && !file.Errors.SequenceEqual(previousErrors))
            _ = _window?.ShowMessageAsync(MessageSeverity.Warning, $"{MacroCommands.FileName}: " + string.Join(" ", file.Errors.Take(5)) + (file.Errors.Count > 5 ? $" (and {file.Errors.Count - 5} more)" : ""));
    }

    /// <summary>One <c>macro.run.&lt;name&gt;</c> command per saved macro (it looks its macro up when it runs, so edits apply at once), plus their keys.</summary>
    private void SyncRunCommands(IReadOnlyList<Macro> macros)
    {
        var wanted = macros.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in _runCommands.Keys.Where(n => !wanted.Contains(n)).ToArray())
        {
            _runCommands[name].Dispose();
            _runCommands.Remove(name);
        }
        foreach (var macro in macros)
        {
            if (_runCommands.ContainsKey(macro.Name)) continue;
            var name = macro.Name;
            _runCommands[name] = _commands.Register(macro.CommandId, inv => RunNamedAsync(name, inv),
                new CommandMetadata(macro.CommandId, "Run " + name, MacroCommands.Category));
        }
        _keyContributions?.Dispose();
        _keyContributions = null;
        var keys = macros.Where(m => m.Key is not null).Select(m => new KeybindingContribution(m.CommandId, m.Key!)).ToArray();
        if (keys.Length > 0 && _contributions is not null && Store is { } store)
            _keyContributions = _contributions.Contribute(ContributionSources.User, store.Directory, keys);
    }

    private async ValueTask<object?> RunNamedAsync(string name, CommandInvocation inv)
    {
        if (RefusedWhileRecording() is not null) return null;
        if (_named.FirstOrDefault(m => m.Name == name) is not { } macro)
        {
            ShowStatus($"Macro '{name}' no longer exists.");
            return null;
        }
        var times = inv.Args.Count > 0 ? Math.Max(1, inv.Arg<int>(0)) : 1;
        return await PlayAsync(macro, new MacroPlayOptions(MacroRepeat.Times, times)).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------------------------------------------------ commands

    private void RegisterCommands()
    {
        CommandMetadata Meta(string id, string title) => new(id, title, MacroCommands.Category);
        _disposables.Add(_commands.Register(MacroCommands.Record, _ => Run(ToggleRecording), Meta(MacroCommands.Record, "Record Macro (Start/Stop)")));
        _disposables.Add(_commands.Register(MacroCommands.Stop, _ => Run(Stop), Meta(MacroCommands.Stop, "Stop Recording or Playing Macro")));
        _disposables.Add(_commands.Register(MacroCommands.PlayLast, PlayLastAsync, Meta(MacroCommands.PlayLast, "Play Last Macro")));
        _disposables.Add(_commands.Register(MacroCommands.Play, PlayCommandAsync, Meta(MacroCommands.Play, "Play Macro...")));
        _disposables.Add(_commands.Register(MacroCommands.PlayNamed, PlayNamedAsync, Meta(MacroCommands.PlayNamed, "Play Saved Macro...")));
        _disposables.Add(_commands.Register(MacroCommands.Save, SaveCommandAsync, Meta(MacroCommands.Save, "Save Last Macro As...")));
        _disposables.Add(_commands.Register(MacroCommands.Edit, EditAsync, Meta(MacroCommands.Edit, "Edit Saved Macros (macros.json)")));
    }

    private ValueTask<object?> Run(Action action)
    {
        if (_scheduler.IsUiThread) action();
        else _scheduler.Post(action);
        return ValueTask.FromResult<object?>(null);
    }

    private void ToggleRecording()
    {
        if (_recorder is not null) StopRecording();
        else StartRecording();
    }

    private async ValueTask<object?> PlayLastAsync(CommandInvocation inv)
    {
        if (RefusedWhileRecording() is not null) return null;
        if (Last is not { } last)
        {
            ShowStatus("No macro has been recorded yet: Tools > Macros > Record Macro records one.");
            return null;
        }
        var times = inv.Args.Count > 0 ? Math.Max(1, inv.Arg<int>(0)) : 1;
        return await PlayAsync(last, new MacroPlayOptions(MacroRepeat.Times, times)).ConfigureAwait(true);
    }

    private async ValueTask<object?> PlayNamedAsync(CommandInvocation inv)
    {
        if (RefusedWhileRecording() is not null) return null;
        var name = inv.Args.Count > 0 ? inv.Arg<string>(0) : null;
        var macro = name is not null ? _named.FirstOrDefault(m => m.Name == name) : await PickAsync(includeLast: false).ConfigureAwait(true);
        if (macro is null)
        {
            if (name is not null) ShowStatus($"There is no saved macro '{name}'.");
            return null;
        }
        return await PlayAsync(macro).ConfigureAwait(true);
    }

    /// <summary><c>macro.play</c>: which macro and how are taken from the argument or asked (see <see cref="MacroCommands.Play"/>).</summary>
    private async ValueTask<object?> PlayCommandAsync(CommandInvocation inv)
    {
        if (RefusedWhileRecording() is not null) return null;
        var (name, mode, times) = ParsePlayArgument(inv.Args.Count > 0 ? inv.Args[0] : null);
        Macro? macro;
        if (name is not null) macro = name.Length == 0 ? Last : _named.FirstOrDefault(m => m.Name == name);
        else if (_named.Count == 0 || mode is not null) macro = Last ?? (_named.Count == 1 ? _named[0] : await PickAsync(includeLast: true).ConfigureAwait(true));
        else macro = await PickAsync(includeLast: true).ConfigureAwait(true);
        if (macro is null)
        {
            ShowStatus(name is { Length: > 0 } ? $"There is no saved macro '{name}'." : "No macro to play: record one first (Tools > Macros > Record Macro).");
            return null;
        }
        var options = mode switch
        {
            MacroRepeat.Times when times is { } n => new MacroPlayOptions(MacroRepeat.Times, n),
            MacroRepeat.Times => await AskTimesAsync().ConfigureAwait(true),
            { } repeat => new MacroPlayOptions(repeat),
            null => await AskModeAsync().ConfigureAwait(true),
        };
        if (options is null) return null;
        return await PlayAsync(macro, options).ConfigureAwait(true);
    }

    /// <summary>The argument of <c>macro.play</c>: a mode string, or an object with name / times / until / onSelectedLines.</summary>
    internal static (string? Name, MacroRepeat? Mode, int? Times) ParsePlayArgument(object? argument)
    {
        static MacroRepeat? Mode(string? s) => s?.ToLowerInvariant() switch
        {
            "times" or "ntimes" => MacroRepeat.Times,
            "untilendoffile" or "endoffile" or "eof" => MacroRepeat.UntilEndOfFile,
            "untilsearchfails" or "searchfails" => MacroRepeat.UntilSearchFails,
            "onselectedlines" or "selectedlines" or "lines" => MacroRepeat.OnSelectedLines,
            "once" => MacroRepeat.Times,
            _ => null,
        };
        switch (argument)
        {
            case string s:
                return (null, Mode(s), s.Equals("once", StringComparison.OrdinalIgnoreCase) ? 1 : null);
            case JsonElement { ValueKind: JsonValueKind.String } js:
                return ParsePlayArgument(js.GetString());
            case JsonElement { ValueKind: JsonValueKind.Number } jn when jn.TryGetInt32(out var count):
                return (null, MacroRepeat.Times, Math.Max(1, count));
            case int count:
                return (null, MacroRepeat.Times, Math.Max(1, count));
            case JsonElement { ValueKind: JsonValueKind.Object } o:
            {
                string? name = o.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                int? times = o.TryGetProperty("times", out var t) && t.TryGetInt32(out var tv) ? Math.Max(1, tv) : null;
                MacroRepeat? mode = times is not null ? MacroRepeat.Times : null;
                if (o.TryGetProperty("until", out var u) && u.ValueKind == JsonValueKind.String) mode = Mode("until" + u.GetString()) ?? Mode(u.GetString());
                if (o.TryGetProperty("onSelectedLines", out var l) && l.ValueKind == JsonValueKind.True) mode = MacroRepeat.OnSelectedLines;
                return (name, mode ?? (name is not null ? MacroRepeat.Times : null), times ?? (name is not null && mode is null ? 1 : null));
            }
            default:
                return (null, null, null);
        }
    }

    private async Task<Macro?> PickAsync(bool includeLast)
    {
        var items = new List<QuickPickItem>();
        if (includeLast && Last is { } last) items.Add(new QuickPickItem("Last recorded macro", $"{last.Steps.Count:N0} steps", Tag: last));
        foreach (var m in _named) items.Add(new QuickPickItem(m.Name, m.Description ?? $"{m.Steps.Count:N0} steps", m.Key, Tag: m));
        if (items.Count == 0)
        {
            ShowStatus("There are no macros: record one (Tools > Macros > Record Macro), or save one in macros.json.");
            return null;
        }
        if (_window is null) return items[0].Tag as Macro;
        var picked = await _window.ShowQuickPickAsync(items, new QuickPickOptions("Play Macro", "Pick a macro")).ConfigureAwait(true);
        return picked?.Tag as Macro;
    }

    private async Task<MacroPlayOptions?> AskModeAsync()
    {
        if (_window is null) return MacroPlayOptions.Once;
        QuickPickItem[] items =
        [
            new("Once", Tag: MacroRepeat.Times),
            new("Several times...", "asks how many", Tag: "times"),
            new("Until the end of the file", "again while each run moves the caret down", Tag: MacroRepeat.UntilEndOfFile),
            new("Until the search fails", "again until a recorded search finds nothing more", Tag: MacroRepeat.UntilSearchFails),
            new("On each selected line", "once per line of the selection, from the start of the line", Tag: MacroRepeat.OnSelectedLines),
        ];
        var picked = await _window.ShowQuickPickAsync(items, new QuickPickOptions("Play Macro", "How often?")).ConfigureAwait(true);
        return picked?.Tag switch
        {
            "times" => await AskTimesAsync().ConfigureAwait(true),
            MacroRepeat.Times => MacroPlayOptions.Once,
            MacroRepeat repeat => new MacroPlayOptions(repeat),
            _ => null,
        };
    }

    private async Task<MacroPlayOptions?> AskTimesAsync()
    {
        if (_window is null) return MacroPlayOptions.Once;
        static string? Validate(string s) => int.TryParse(s.Trim(), out var n) && n is >= 1 and <= 1_000_000 ? null : "A number from 1 to 1,000,000";
        var text = await _window.ShowInputBoxAsync(new InputBoxOptions("Play Macro", "How many times?", "e.g. 10", "10", Validate: Validate)).ConfigureAwait(true);
        return text is not null && Validate(text) is null ? new MacroPlayOptions(MacroRepeat.Times, int.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture)) : null;
    }

    private async ValueTask<object?> SaveCommandAsync(CommandInvocation inv)
    {
        if (Last is null)
        {
            ShowStatus("There is no recorded macro to save: record one first (Tools > Macros > Record Macro).");
            return null;
        }
        var name = inv.Args.Count > 0 ? inv.Arg<string>(0) : null;
        if (name is null && _window is not null)
        {
            string? Validate(string s) => MacroNames.IsValid(s.Trim()) ? null : MacroNames.Rule;
            name = (await _window.ShowInputBoxAsync(new InputBoxOptions("Save Last Macro", $"Name (runs as {MacroCommands.RunPrefix}<name>)", "comment-lines",
                Validate: Validate)).ConfigureAwait(true))?.Trim();
            if (name is null) return null;
            if (_named.Any(m => m.Name == name))
            {
                var replace = await _window.ShowMessageAsync(MessageSeverity.Warning, $"A macro named '{name}' exists. Replace it?",
                    [new MessageAction("Replace"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(true);
                if (replace != "Replace") return null;
            }
        }
        if (name is null) return null;
        return await SaveLastAsync(name).ConfigureAwait(true);
    }

    private async ValueTask<object?> EditAsync(CommandInvocation inv)
    {
        if (Store is not { } store) return null;
        try
        {
            var uri = await store.EnsureExistsAsync().ConfigureAwait(true);
            if (_navigation is not null) await _navigation.OpenAsync(uri).ConfigureAwait(true);
            return uri;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = _window?.ShowMessageAsync(MessageSeverity.Error, $"Could not open {store.FilePath}: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------------------------ helpers

    private async Task ShowBlockAsync(MacroBlock block)
    {
        if (_window is null) return;
        MessageAction[] actions = block.ActionTitle is { } title && block.ActionCommand is { } command && _commands.Has(command) ? [new MessageAction(title)] : [];
        var picked = await _window.ShowMessageAsync(MessageSeverity.Info, block.Message, actions).ConfigureAwait(true);
        if (picked is not null && picked == block.ActionTitle && block.ActionCommand is { } id)
        {
            try { await _commands.ExecuteAsync(id).ConfigureAwait(true); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _log.LogWarning(ex, "{Command} failed", id); }
        }
    }

    /// <summary>A transient message on the status bar (replaces the previous one).</summary>
    public void ShowStatus(string text)
    {
        if (_status is null || _disposed) return;
        ClearMessage();
        _lastMessage = text;
        _messageItem = _status.Set(new StatusBarItemModel(MacroCommands.MessageStatusId, text, text, Alignment: StatusBarAlignment.Left, Priority: 999));
        _messageTimer = _time.CreateTimer(_ => _scheduler.Post(ClearMessage), null, MessageDuration, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The transient status message, or null (tests).</summary>
    public string? StatusMessage => _messageItem is null ? null : _lastMessage;

    private string? _lastMessage;

    private void ClearMessage()
    {
        _messageTimer?.Dispose();
        _messageTimer = null;
        _messageItem?.Dispose();
        _messageItem = null;
    }

    private void NotifyState()
    {
        var state = State;
        _keys?.Set(MacroCommands.RecordingContextKey, state == MacroState.Recording);
        _keys?.Set(MacroCommands.PlayingContextKey, state == MacroState.Playing);
        _stateChanged.Fire(state);
    }

    private T Setting<T>(string key, T fallback)
    {
        try { return _config is null ? fallback : _config.Get(key, fallback); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return fallback; }
    }

    private void Ui(Action action)
    {
        if (_scheduler.IsUiThread) action();
        else _scheduler.Post(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Player.Cancel();
        if (_recorder is not null)
        {
            _recorder = null;
            _executedSubscription?.Dispose();
            _pauseSubscription?.Dispose();
        }
        _statusItem?.Dispose();
        ClearMessage();
        _keyContributions?.Dispose();
        if (Store is { } store) store.MaxStepsPerMacro = static () => MacroSettings.DefaultMaxRecordedSteps; // the store may outlive this service
        foreach (var r in _runCommands.Values) r.Dispose();
        _runCommands.Clear();
        _disposables.Dispose();
        _stateChanged.Clear();
        _host = null;
    }
}
