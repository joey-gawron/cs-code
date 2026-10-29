using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Panels.Debug.Variables;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Debug.ToolWindows;

/// <summary>
/// App-lifetime state shared by the debug tool windows (the windows themselves are singletons but their views come and go): the lazily
/// resolved <see cref="DebugPanelServices"/>, and the <c>debug.addToWatch</c> command, which must work while the Watch window is hidden.
/// </summary>
public sealed class DebugPanelsRuntime : IDisposable
{
    public const string AddToWatchCommand = "debug.addToWatch";

    private readonly IServiceProvider _provider;
    private readonly DisposableStore _subs = new();
    private readonly object _gate = new();
    private DebugPanelServices? _services;
    private bool _resolved;
    private WeakReference<WatchViewModel>? _activeWatch;

    public DebugPanelsRuntime(IServiceProvider provider)
    {
        _provider = provider;
        if (provider.GetService<ICommandService>() is { } commands)
            _subs.Add(commands.Register(AddToWatchCommand, AddToWatchAsync, new CommandMetadata(AddToWatchCommand, "Add Watch", "Debug")));
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
            }
            return _services;
        }
    }

    /// <summary>The open Watch window, if any (the command focuses its "add" row instead of prompting).</summary>
    internal void SetActiveWatch(WatchViewModel? watch)
    {
        lock (_gate) _activeWatch = watch is null ? null : new WeakReference<WatchViewModel>(watch);
    }

    private async ValueTask<object?> AddToWatchAsync(CommandInvocation invocation)
    {
        if (GetServices() is not { } services) return null;
        var expression = invocation.Arg<string>(0);
        if (string.IsNullOrWhiteSpace(expression))
        {
            WatchViewModel? open = null;
            lock (_gate) _activeWatch?.TryGetTarget(out open);
            if (open is not null)
            {
                await services.Ui.InvokeAsync(open.BeginAdd);
                return null;
            }
            if (services.Window is { } window)
                expression = await window.ShowInputBoxAsync(new InputBoxOptions(Title: "Add Watch", Prompt: "Expression to watch"));
        }
        if (string.IsNullOrWhiteSpace(expression)) return null;
        services.Watches.EnsureLoaded();
        services.Watches.Add(expression);
        return null;
    }

    public void Dispose() => _subs.Dispose();
}
