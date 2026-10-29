using Avalonia.Controls;
using Avalonia.VisualTree;
using CsCode.Core.Contributions;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Panels.Debug.Infrastructure;
using CsCode.Panels.Debug.Variables;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Debug.ToolWindows;

/// <summary>
/// App-lifetime state shared by the debug tool windows (the windows themselves are singletons but their views come and go): the lazily
/// resolved <see cref="DebugPanelServices"/>, and the <c>debug.addToWatch</c> command (bound to Ctrl+Alt+W while debugging), which must work
/// while the Watch window is hidden.
/// </summary>
public sealed class DebugPanelsRuntime : IDisposable
{
    public const string AddToWatchCommand = "debug.addToWatch";

    /// <summary>The Visual Studio "Add Watch" chord, contributed to the keybinding service (only while a debug session exists).</summary>
    public const string AddToWatchKey = "ctrl+alt+w";

    /// <summary>Context key that is true while the debuggee is stopped on an exception (the Exception window auto-shows on it).</summary>
    public const string ExceptionStoppedKey = "cscode.debug.exceptionStopped";

    private readonly IServiceProvider _provider;
    private readonly DisposableStore _subs = new();
    private readonly object _gate = new();
    private DebugPanelServices? _services;
    private bool _resolved;
    private WeakReference<WatchViewModel>? _activeWatch;
    private WeakReference<Control>? _activeWatchView;
    private DebugFocusTracker? _exceptionKey;

    public DebugPanelsRuntime(IServiceProvider provider)
    {
        _provider = provider;
        if (provider.GetService<ICommandService>() is { } commands)
            _subs.Add(commands.Register(AddToWatchCommand, AddToWatchAsync, new CommandMetadata(AddToWatchCommand, "Add Watch", "Debug")));
        // The keybinding service picks up contributions live (built-in ones rank below the user's keybindings.json, which can remove it).
        if (provider.GetService<IContributionRegistry>() is { } contributions)
            _subs.Add(contributions.Contribute(ContributionSources.Builtin, AppContext.BaseDirectory,
                [new KeybindingContribution(AddToWatchCommand, AddToWatchKey, When: "inDebugMode")]));
        GetServices(); // resolves the debug service now (it registers the debug commands) and starts the exception context key
    }

    /// <summary>The panels' services, or null when no <see cref="CsCode.Core.Debugging.IDebugService"/> is registered.</summary>
    public DebugPanelServices? GetServices()
    {
        lock (_gate)
        {
            if (!_resolved)
            {
                _services = DebugPanelServices.TryCreate(_provider);
                _resolved = _services is not null; // keep looking while the debug module has not registered yet
                if (_services is not null)
                {
                    _subs.Add(_services);
                    StartExceptionKey(_services);
                }
            }
            return _services;
        }
    }

    private void StartExceptionKey(DebugPanelServices services)
    {
        if (services.ContextKeys is not { } keys) return;
        var tracker = new DebugFocusTracker(services, focus =>
            keys.Set(ExceptionStoppedKey, focus.IsStopped && (focus.StopReason == "exception" || (services.Debug.StopLocation is { Reason: "exception" } l && ReferenceEquals(l.Session, focus.Session)))));
        _exceptionKey = tracker;
        _subs.Add(tracker);
        tracker.Start();
    }

    /// <summary>The Watch window's view-model and view, while it exists (the command starts its "add" row instead of prompting).</summary>
    internal void SetActiveWatch(WatchViewModel watch, Control view)
    {
        lock (_gate)
        {
            _activeWatch = new WeakReference<WatchViewModel>(watch);
            _activeWatchView = new WeakReference<Control>(view);
        }
    }

    /// <summary>The view of <paramref name="watch"/> was disposed (a newer view may already have registered itself: keep that one).</summary>
    internal void ClearActiveWatch(WatchViewModel watch)
    {
        lock (_gate)
        {
            if (_activeWatch is null || !_activeWatch.TryGetTarget(out var current) || ReferenceEquals(current, watch))
            {
                _activeWatch = null;
                _activeWatchView = null;
            }
        }
    }

    private (WatchViewModel? Watch, Control? View) ActiveWatch()
    {
        WatchViewModel? watch = null;
        Control? view = null;
        lock (_gate)
        {
            _activeWatch?.TryGetTarget(out watch);
            _activeWatchView?.TryGetTarget(out view);
        }
        return (watch, view);
    }

    /// <summary>UI thread: starts the inline "add" row of the Watch window if the user can see it (or, with <paramref name="evenIfHidden"/>, right after it was brought forward).</summary>
    private bool TryBeginAdd(bool evenIfHidden)
    {
        var (watch, view) = ActiveWatch();
        if (watch is null || view is null) return false;
        // The shell creates the views of every auto-shown debug window, but only one tab of a group is on screen; an inactive tab's
        // content is not in the visual tree, and an editor there would neither show nor get the keyboard.
        if (!evenIfHidden && !(view.IsAttachedToVisualTree() && view.IsEffectivelyVisible)) return false;
        watch.BeginAdd();
        return true;
    }

    private async ValueTask<object?> AddToWatchAsync(CommandInvocation invocation)
    {
        if (GetServices() is not { } services) return null;
        var expression = invocation.Arg<string>(0);
        var given = !string.IsNullOrWhiteSpace(expression);
        if (!given)
        {
            // Inline in the Watch window's empty row when it is on screen; otherwise bring the window forward first (the shell's optional
            // IViewActivator, resolved now: resolving it while the tool windows are constructed could be circular), else ask.
            if (await services.Ui.InvokeAsync(() => TryBeginAdd(evenIfHidden: false))) return null;
            if (_provider.GetService<IViewActivator>() is { } views && await ShowWatchWindowAsync(views)
                && await services.Ui.InvokeAsync(() => TryBeginAdd(evenIfHidden: true)))
                return null;
            if (services.Window is { } window)
                expression = await window.ShowInputBoxAsync(new InputBoxOptions(Title: "Add Watch", Prompt: "Expression to watch"));
        }
        if (string.IsNullOrWhiteSpace(expression)) return null;
        services.Watches.Add(expression); // loads the saved list first
        // Visual Studio brings the Watch window up on Add Watch, also when the expression came with the command (editor, Locals). Without one,
        // the window was already brought forward above (or could not be).
        if (given && _provider.GetService<IViewActivator>() is { } shown) await ShowWatchWindowAsync(shown);
        return null;
    }

    private static async Task<bool> ShowWatchWindowAsync(IViewActivator views)
    {
        try { return await views.ShowViewAsync(WatchToolWindow.ViewId); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { return false; }
    }

    public void Dispose() => _subs.Dispose();
}
