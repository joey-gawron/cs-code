using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using CsCode.Panels.Debug.Breakpoints;
using CsCode.Panels.Debug.CallStack;
using CsCode.Panels.Debug.Console;
using CsCode.Panels.Debug.Exceptions;
using CsCode.Panels.Debug.Modules;
using CsCode.Panels.Debug.Threads;
using CsCode.Panels.Debug.Variables;
using CsCode.Panels.Debug.Views;
using CsCode.Ui;

namespace CsCode.Panels.Debug.ToolWindows;

/// <summary>
/// Base of the debug tool windows. The shell discovers them from DI, creates a view only when the window is shown and disposes it
/// (view and view-model, hence every service subscription) when it is hidden, so creating a view must be cheap and the view-models
/// must be fully disposable. All of them dock at the bottom and auto-show while a debug session is active (<c>inDebugMode</c>).
/// </summary>
public abstract class DebugToolWindow(DebugPanelsRuntime runtime) : IToolWindow
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string? IconKey { get; }
    public abstract string? ToggleCommandId { get; }
    public ToolWindowLocation DefaultLocation => ToolWindowLocation.Bottom;
    public virtual string? AutoShowWhen => "inDebugMode";

    public Control CreateView(IServiceProvider services) =>
        runtime.GetServices() is { } s ? Create(s, runtime) : Unavailable();

    protected abstract Control Create(DebugPanelServices services, DebugPanelsRuntime runtime);

    private static Control Unavailable()
    {
        var text = new TextBlock { Text = "Debugging is not available: no debug service is registered.", Margin = new Thickness(12, 10), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("vsc:descriptionForeground"));
        return text;
    }
}

public sealed class CallStackToolWindow(DebugPanelsRuntime runtime) : DebugToolWindow(runtime)
{
    public override string Id => "debug.callstack";
    public override string Title => "Call Stack";
    public override string? IconKey => "debug-stackframe";
    public override string? ToggleCommandId => "workbench.debug.action.toggleCallStack";
    protected override Control Create(DebugPanelServices services, DebugPanelsRuntime runtime) => new CallStackView { DataContext = new CallStackViewModel(services) };
}

public sealed class LocalsToolWindow(DebugPanelsRuntime runtime) : DebugToolWindow(runtime)
{
    public override string Id => "debug.locals";
    public override string Title => "Locals";
    public override string? IconKey => "symbol-variable";
    public override string? ToggleCommandId => "workbench.debug.action.toggleLocals";
    protected override Control Create(DebugPanelServices services, DebugPanelsRuntime runtime)
    {
        var vm = new LocalsViewModel(services);
        return new VariablesPanelView(vm, vm.Tree);
    }
}

public sealed class AutosToolWindow(DebugPanelsRuntime runtime) : DebugToolWindow(runtime)
{
    public override string Id => "debug.autos";
    public override string Title => "Autos";
    public override string? IconKey => "symbol-field";
    public override string? ToggleCommandId => "workbench.debug.action.toggleAutos";
    protected override Control Create(DebugPanelServices services, DebugPanelsRuntime runtime)
    {
        var vm = new AutosViewModel(services);
        return new VariablesPanelView(vm, vm.Tree);
    }
}

public sealed class WatchToolWindow(DebugPanelsRuntime runtime) : DebugToolWindow(runtime)
{
    public override string Id => "debug.watch";
    public override string Title => "Watch 1";
    public override string? IconKey => "watch";
    public override string? ToggleCommandId => "workbench.debug.action.toggleWatch";
    protected override Control Create(DebugPanelServices services, DebugPanelsRuntime runtime)
    {
        var vm = new WatchViewModel(services);
        runtime.SetActiveWatch(vm);
        return new VariablesPanelView(vm, vm.Tree, onDispose: () => runtime.SetActiveWatch(null));
    }
}

public sealed class BreakpointsToolWindow(DebugPanelsRuntime runtime) : DebugToolWindow(runtime)
{
    public override string Id => "debug.breakpoints";
    public override string Title => "Breakpoints";
    public override string? IconKey => "record";
    public override string? ToggleCommandId => "workbench.debug.action.toggleBreakpoints";
    protected override Control Create(DebugPanelServices services, DebugPanelsRuntime runtime) => new BreakpointsView { DataContext = new BreakpointsViewModel(services) };
}

public sealed class ThreadsToolWindow(DebugPanelsRuntime runtime) : DebugToolWindow(runtime)
{
    public override string Id => "debug.threads";
    public override string Title => "Threads";
    public override string? IconKey => "layers";
    public override string? ToggleCommandId => "workbench.debug.action.toggleThreads";
    protected override Control Create(DebugPanelServices services, DebugPanelsRuntime runtime) => new ThreadsView { DataContext = new ThreadsViewModel(services) };
}

public sealed class DebugConsoleToolWindow(DebugPanelsRuntime runtime) : DebugToolWindow(runtime)
{
    public override string Id => "debug.console";
    public override string Title => "Debug Console";
    public override string? IconKey => "debug-console";
    public override string? ToggleCommandId => "workbench.debug.action.toggleDebugConsole";
    protected override Control Create(DebugPanelServices services, DebugPanelsRuntime runtime) => new ConsoleView { DataContext = new DebugConsoleViewModel(services) };
}

/// <summary>
/// The Exception Helper as a tool window: shows type, message, stack and inner exceptions while the debuggee is stopped on an exception
/// (auto-shown through the context key <see cref="DebugPanelsRuntime.ExceptionStoppedKey"/>, which the runtime keeps up to date).
/// </summary>
public sealed class ExceptionToolWindow(DebugPanelsRuntime runtime) : DebugToolWindow(runtime)
{
    public override string Id => "debug.exception";
    public override string Title => "Exception";
    public override string? IconKey => "error";
    public override string? ToggleCommandId => "workbench.debug.action.toggleException";
    public override string? AutoShowWhen => DebugPanelsRuntime.ExceptionStoppedKey;
    protected override Control Create(DebugPanelServices services, DebugPanelsRuntime runtime) => new ExceptionHelperView { DataContext = new ExceptionHelperViewModel(services) };
}

/// <summary>Modules window (manual: opened from the View menu, not auto-shown).</summary>
public sealed class ModulesToolWindow(DebugPanelsRuntime runtime) : DebugToolWindow(runtime)
{
    public override string Id => "debug.modules";
    public override string Title => "Modules";
    public override string? IconKey => "package";
    public override string? ToggleCommandId => "workbench.debug.action.toggleModules";
    public override string? AutoShowWhen => null;
    protected override Control Create(DebugPanelServices services, DebugPanelsRuntime runtime) => new ModulesView { DataContext = new ModulesViewModel(services) };
}
