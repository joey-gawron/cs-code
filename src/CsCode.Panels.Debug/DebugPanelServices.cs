using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Debug;

/// <summary>
/// The Core services the debug panels consume, resolved once from DI. Only <see cref="Debug"/> is mandatory; everything else degrades
/// gracefully (no navigation service = clicking a frame only focuses it, no window service = no input boxes / clipboard, …) so a host
/// that has not wired every service still gets working panels.
/// </summary>
public sealed class DebugPanelServices(
    IDebugService debug,
    IUiDispatcher ui,
    IDebugFrameFocus? frameFocus = null,
    IDebugOutputHistory? outputHistory = null,
    INavigationService? navigation = null,
    IWindowService? window = null,
    ICommandService? commands = null,
    IContextKeyService? contextKeys = null,
    IStateStorage? state = null,
    IDocumentManager? documents = null)
{
    public IDebugService Debug { get; } = debug;
    public IUiDispatcher Ui { get; } = ui;
    public IDebugFrameFocus? FrameFocus { get; } = frameFocus ?? debug as IDebugFrameFocus;
    public IDebugOutputHistory? OutputHistory { get; } = outputHistory ?? debug as IDebugOutputHistory;
    public INavigationService? Navigation { get; } = navigation;
    public IWindowService? Window { get; } = window;
    public ICommandService? Commands { get; } = commands;
    public IContextKeyService? ContextKeys { get; } = contextKeys;
    public IStateStorage? State { get; } = state;
    public IDocumentManager? Documents { get; } = documents;

    public IBreakpointManager Breakpoints => Debug.Breakpoints;

    /// <summary>The watch list shared by every Watch window (persisted per workspace).</summary>
    public Variables.WatchStore Watches { get; } = new(debug, state);

    /// <summary>Resolves the services from DI; null when there is no <see cref="IDebugService"/> (the panels then show a placeholder).</summary>
    public static DebugPanelServices? TryCreate(IServiceProvider services)
    {
        var debug = services.GetService<IDebugService>();
        if (debug is null) return null;
        return new DebugPanelServices(
            debug,
            services.GetService<IUiDispatcher>() ?? AvaloniaUiDispatcher.Instance,
            services.GetService<IDebugFrameFocus>(),
            services.GetService<IDebugOutputHistory>(),
            services.GetService<INavigationService>(),
            services.GetService<IWindowService>(),
            services.GetService<ICommandService>(),
            services.GetService<IContextKeyService>(),
            services.GetService<IStateStorage>(),
            services.GetService<IDocumentManager>());
    }
}
