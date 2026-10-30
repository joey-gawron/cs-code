using Avalonia.Controls;
using CsCode.Core.Terminal;
using CsCode.Panels.Terminal.Views;
using CsCode.Terminal;
using CsCode.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CsCode.Panels.Terminal;

/// <summary>The Terminal tool window (bottom well, id <c>terminal</c>, Visual Studio's "Terminal").</summary>
public sealed class TerminalToolWindow(TerminalPanelRuntime runtime) : IToolWindow
{
    public string Id => TerminalPanelRuntime.ViewId;
    public string Title => "Terminal";
    public string? IconKey => "terminal";
    public ToolWindowLocation DefaultLocation => ToolWindowLocation.Bottom;
    /// <summary>Registered by the runtime (VS Code's toggle: hide when shown, else show, focus and create a terminal if there is none).</summary>
    public string? ToggleCommandId => KnownCommandsTerminal.Toggle;
    /// <summary>View &gt; Tool Windows &gt; Terminal: shown, a terminal created when there is none, and the keyboard in it (never a hide, unlike the toggle).</summary>
    public string? ShowCommandId => KnownCommandsTerminal.Focus;
    public string? AutoShowWhen => null;

    public Control CreateView(IServiceProvider services) => new TerminalView(runtime);
}

internal static class KnownCommandsTerminal
{
    public const string Toggle = TerminalCommands.Toggle;
    public const string Focus = TerminalCommands.Focus;
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The integrated terminal: <see cref="ITerminalService"/> (CsCode.Terminal) plus the Terminal tool window, its runtime (commands and
    /// keybindings with VS Code's ids, keybinding precedence while a terminal has focus) and views. One line in the composition root.
    /// </summary>
    public static IServiceCollection AddCsCodeTerminal(this IServiceCollection services)
    {
        services.AddCsCodeTerminalService();
        services.TryAddSingleton<TerminalPanelRuntime>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolWindow, TerminalToolWindow>());
        return services;
    }
}
