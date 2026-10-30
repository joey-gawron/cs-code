using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui.Threading;

namespace CsCode.App.QuickInput;

/// <summary>
/// Opens the command palette / Quick Launch on the shared quick-input overlay and runs the chosen entry once the overlay has closed
/// (so a command that itself opens a picker is never fighting the palette for the overlay). Owns the shared workspace file index.
/// </summary>
public sealed class PaletteController : IDisposable
{
    private readonly QuickInputViewModel _quick;
    private readonly PaletteContext _ctx;
    private readonly IUiDispatcher _ui;
    private readonly IWindowService? _window;
    private readonly FileIndex _index;
    private readonly IDisposable? _memoryReg;
    private readonly Func<IInputElement?>? _focused;
    private readonly IDisposable _keyPresses, _pointerPresses;
    private long _presses; // keys and mouse buttons pressed in any window (UI thread)

    /// <param name="focused">The element that has the keyboard (in any window), for giving the keyboard to an editor an accepted entry opens.</param>
    public PaletteController(QuickInputViewModel quick, PaletteContext context, IUiDispatcher ui, IWindowService? window = null, Func<IInputElement?>? focused = null)
    {
        _quick = quick;
        _ctx = context;
        _ui = ui;
        _window = window;
        _focused = focused;
        _index = new FileIndex(context.Workspace);
        _memoryReg = context.Memory?.Register(_index);
        // The user's own presses, in every window (a tunnel class handler on each top level): one increment per press.
        _keyPresses = InputElement.KeyDownEvent.AddClassHandler<TopLevel>((_, _) => _presses++, RoutingStrategies.Tunnel, handledEventsToo: true);
        _pointerPresses = InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((_, _) => _presses++, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public FileIndex Index => _index;

    /// <summary>Opens the palette pre-filled with a mode prefix ("" = files, "&gt;" commands, ":" line, "@" symbols, "#" workspace symbols).</summary>
    public async Task ShowAsync(string prefix)
    {
        var activeBefore = _ctx.Well.Active;
        var focusBefore = _focused?.Invoke() is { } f ? new WeakReference<IInputElement>(f) : null;
        var picked = await await _ui.InvokeAsync(() => _quick.PickAsync(new PaletteSource(_ctx, _index), null, null, prefix)).ConfigureAwait(true);
        if (picked?.Accept is not { } accept) return;
        var presses = Interlocked.Read(ref _presses);
        try { await accept().ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_window is not null) await _window.ShowMessageAsync(MessageSeverity.Error, ex.Message).ConfigureAwait(true);
            return;
        }
        // The entry went to another editor (a file, a symbol, Open Next Editor...): it gets the keyboard, as in VS Code, also when the palette was
        // opened from the terminal or a tool window (the palette gave the keyboard back there when it closed, before the file was open, and the
        // open does not take it from a tool window). Only from where the palette left it, though, and only while the user has pressed no key or
        // mouse button since accepting: an entry can run for long (Start Debugging waits for its build), and a user who switched tabs or clicked
        // into the terminal meanwhile keeps the keyboard where they put it.
        if (ReferenceEquals(_ctx.Well.Active, activeBefore) || Interlocked.Read(ref _presses) != presses) return;
        var leftAt = focusBefore?.TryGetTarget(out var before) == true ? before : null;
        _ctx.Well.FocusActiveEditor(mayTakeFrom: focused => Interlocked.Read(ref _presses) == presses
            && (ReferenceEquals(focused, leftAt) || focused is Visual { IsEffectivelyVisible: false })); // (the closed palette's own box)
    }

    /// <summary>A fixed-list pick whose highlight changes are reported (live theme preview).</summary>
    public async Task<QuickPickItem?> PickWithPreviewAsync(IReadOnlyList<QuickPickItem> items, string title, string placeholder, Action<QuickPickEntry?> onActive)
    {
        using var sub = _quick.ActiveEntryChanged.Subscribe(onActive);
        var entry = await await _ui.InvokeAsync(() => _quick.PickAsync(new StaticQuickPickSource(items, new QuickPickOptions(Title: title, Placeholder: placeholder)), title, placeholder)).ConfigureAwait(true);
        return entry?.Tag as QuickPickItem;
    }

    public void Dispose()
    {
        _keyPresses.Dispose();
        _pointerPresses.Dispose();
        _memoryReg?.Dispose();
        _index.Dispose();
    }
}
