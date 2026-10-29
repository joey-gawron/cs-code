using System.Text.Json;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui.Threading;

namespace CsCode.App.Input;

public enum KeyDispatch
{
    /// <summary>No binding: the focused control handles the key (typing into the editor, text boxes, ...).</summary>
    PassThrough,
    /// <summary>The key was consumed (a command ran, or it is a chord prefix, or Esc cancelled a pending chord).</summary>
    Handled,
}

/// <summary>
/// The shell's key path: toolkit-independent <see cref="KeyStroke"/> in, decision out. A matching binding executes its command (with args);
/// a chord prefix is swallowed and announced in the status bar ("(Ctrl+K) was pressed. Waiting for second key of chord..."); anything
/// else passes through so typing is never slowed or swallowed. Esc or a timeout abandons a pending chord. Resolution is allocation-light
/// (the keybinding service owns the chord state) and the command runs asynchronously: the key handler returns immediately.
/// </summary>
public sealed class KeyboardPipeline : IDisposable
{
    public static readonly TimeSpan DefaultChordTimeout = TimeSpan.FromSeconds(5);
    private const string PendingStatusId = "keys.pending";

    private readonly IKeybindingService _keys;
    private readonly ICommandService _commands;
    private readonly IStatusBarService? _status;
    private readonly IWindowService? _window;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private IDisposable? _pendingStatus;
    private ITimer? _timer;

    public KeyboardPipeline(IKeybindingService keys, ICommandService commands, IUiDispatcher ui, IStatusBarService? status = null,
        IWindowService? window = null, TimeProvider? time = null, TimeSpan? chordTimeout = null)
    {
        _keys = keys;
        _commands = commands;
        _ui = ui;
        _status = status;
        _window = window;
        _time = time ?? TimeProvider.System;
        _timeout = chordTimeout ?? DefaultChordTimeout;
    }

    public bool IsChordPending => _keys.PendingChord.Count > 0;

    /// <summary>Number of commands started by key bindings (diagnostics / tests).</summary>
    public int ExecutedCount { get; private set; }

    public KeyDispatch Handle(KeyStroke stroke)
    {
        var wasPending = IsChordPending;
        if (wasPending) _lastChord = _keys.PendingChord.ToArray(); // the service clears its state on a dead chord; keep the prefix for the message
        if (wasPending && stroke.Key == "escape" && stroke is { Ctrl: false, Shift: false, Alt: false })
        {
            Reset();
            return KeyDispatch.Handled;
        }

        var result = _keys.Resolve(stroke);
        switch (result.Kind)
        {
            case KeyResolution.Matched when result.Binding is { } binding:
                ClearPending();
                Execute(binding);
                return KeyDispatch.Handled;

            case KeyResolution.Pending:
                ShowPending();
                return KeyDispatch.Handled;

            default:
                ClearPending();
                if (wasPending)
                {
                    // A dead chord: VS Code swallows the second key and says so.
                    ShowDead(stroke);
                    return KeyDispatch.Handled;
                }
                return KeyDispatch.PassThrough;
        }
    }

    /// <summary>Abandons a pending chord (Esc, timeout, focus change).</summary>
    public void Reset()
    {
        _keys.ResetChord();
        ClearPending();
    }

    private void ShowPending()
    {
        var chord = string.Join(", ", _keys.PendingChord.Select(Format));
        _pendingStatus?.Dispose();
        _pendingStatus = _status?.Set(new StatusBarItemModel(PendingStatusId, $"({chord}) was pressed. Waiting for second key of chord…", Priority: int.MaxValue));
        _timer?.Dispose();
        _timer = _time.CreateTimer(_ => _ui.Post(Reset), null, _timeout, Timeout.InfiniteTimeSpan);
    }

    private void ClearPending()
    {
        _timer?.Dispose();
        _timer = null;
        _pendingStatus?.Dispose();
        _pendingStatus = null;
    }

    private IDisposable? _deadStatus;
    private ITimer? _deadTimer;

    private void ShowDead(KeyStroke last)
    {
        ClearDead();
        var text = string.Join(", ", _lastChord.Append(last).Select(Format));
        _deadStatus = _status?.Set(new StatusBarItemModel("keys.dead", $"The key combination ({text}) is not a command.", Priority: int.MaxValue));
        _deadTimer = _time.CreateTimer(_ => _ui.Post(ClearDead), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }

    private void ClearDead()
    {
        _deadTimer?.Dispose();
        _deadTimer = null;
        _deadStatus?.Dispose();
        _deadStatus = null;
    }

    private KeyStroke[] _lastChord = [];

    private void Execute(KeyBinding binding)
    {
        ExecutedCount++;
        _ = RunAsync(binding);
    }

    private async Task RunAsync(KeyBinding binding)
    {
        try
        {
            if (binding.Args is { } args) await _commands.ExecuteAsync(binding.Command, args.ValueKind == JsonValueKind.Null ? null : args.Clone());
            else await _commands.ExecuteAsync(binding.Command);
        }
        catch (CommandNotFoundException)
        {
            if (_window is not null) await _window.ShowMessageAsync(MessageSeverity.Warning, $"Command '{binding.Command}' is not available.");
        }
        catch (OperationCanceledException)
        {
            // cancelled by the command itself
        }
        catch (Exception ex)
        {
            if (_window is not null) await _window.ShowMessageAsync(MessageSeverity.Error, $"Command '{binding.Command}' failed: {ex.Message}");
        }
    }

    internal static string Format(KeyStroke s)
    {
        var parts = new List<string>(5);
        if (s.Ctrl) parts.Add("Ctrl");
        if (s.Shift) parts.Add("Shift");
        if (s.Alt) parts.Add("Alt");
        if (s.Meta) parts.Add("Win");
        parts.Add(s.Key.Length == 1 ? s.Key.ToUpperInvariant() : char.ToUpperInvariant(s.Key[0]) + s.Key[1..]);
        return string.Join('+', parts);
    }

    public void Dispose()
    {
        ClearPending();
        ClearDead();
    }
}
