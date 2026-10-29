using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui.Icons;
using CsCode.Ui.Theming;
using CsCode.Ui.Threading;

namespace CsCode.App.Views;

/// <summary>
/// The standard toolbar (VS2022 layout): back/forward, new/open/save/save-all, undo/redo, launch-configuration dropdown with the green Start
/// button, step buttons that appear only while debugging, and find-in-files. Every button runs a command id; enablement follows
/// <c>when</c> clauses and command availability and is refreshed (coalesced) when context keys or registrations change.
/// The launch-configuration combo is bound to <see cref="IDebugConfigurationService"/> only when a debug service is resolvable.
/// </summary>
public sealed class ToolbarView : Border, IDisposable
{
    private sealed record Spec(string CommandId, string Icon, string Tooltip, string? When = null, string ColorId = "foreground", bool DebugOnly = false);

    private static readonly Spec[] Standard =
    [
        new("workbench.action.navigateBack", IconKey.ArrowLeft, "Navigate Backward"),
        new("workbench.action.navigateForward", IconKey.ArrowRight, "Navigate Forward"),
        new(KnownCommands.NewUntitledFile, IconKey.NewFile, "New File"),
        new(KnownCommands.OpenFolder, IconKey.FolderOpened, "Open Folder"),
        new(KnownCommands.Save, IconKey.Save, "Save", "hasOpenEditors"),
        new(KnownCommands.SaveAll, IconKey.SaveAll, "Save All", "hasOpenEditors"),
        new("undo", IconKey.Undo, "Undo", "hasOpenEditors"),
        new("redo", IconKey.Redo, "Redo", "hasOpenEditors"),
    ];

    private static readonly Spec[] Steps =
    [
        new(KnownCommands.DebugContinue, IconKey.Continue, "Continue", "debugState == 'stopped'", "debugIcon.continueForeground", true),
        new(KnownCommands.DebugPause, IconKey.Pause, "Pause", "debugState == 'running'", "debugIcon.pauseForeground", true),
        new(KnownCommands.DebugStepOver, IconKey.StepOver, "Step Over", "debugState == 'stopped'", "debugIcon.stepOverForeground", true),
        new(KnownCommands.DebugStepInto, IconKey.StepInto, "Step Into", "debugState == 'stopped'", "debugIcon.stepIntoForeground", true),
        new(KnownCommands.DebugStepOut, IconKey.StepOut, "Step Out", "debugState == 'stopped'", "debugIcon.stepOutForeground", true),
        new(KnownCommands.DebugRestart, IconKey.Restart, "Restart", "inDebugMode", "debugIcon.restartForeground", true),
        new(KnownCommands.DebugStop, IconKey.Stop, "Stop Debugging", "inDebugMode", "debugIcon.stopForeground", true),
    ];

    private readonly ICommandService _commands;
    private readonly IContextKeyService _keys;
    private readonly IKeybindingService? _bindings;
    private readonly IUiDispatcher _ui;
    private readonly IDebugService? _debug;
    private readonly DisposableStore _subs = new();
    private readonly List<(Button Button, Spec Spec)> _buttons = new();
    private readonly List<Control> _debugOnly = new();
    private readonly ComboBox? _configs;
    private readonly TextBlock _startLabel = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Text = "Start" };
    private Button? _start;

    public ToolbarView(ICommandService commands, IContextKeyService keys, IUiDispatcher ui, IKeybindingService? bindings = null, IDebugService? debug = null)
    {
        _commands = commands;
        _keys = keys;
        _bindings = bindings;
        _ui = ui;
        _debug = debug;
        Height = 30;
        Background = ThemeBrushes.Get("toolbar.background");
        BorderBrush = ThemeBrushes.Get("panel.border");
        BorderThickness = new Thickness(0, 0, 0, 1);
        TextElement.SetForeground(this, ThemeBrushes.Get("toolbar.foreground"));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0), Spacing = 1 };
        var i = 0;
        foreach (var spec in Standard)
        {
            row.Children.Add(MakeButton(spec));
            if (++i is 2 or 6) row.Children.Add(ViewHelpers.Separator());
            if (i == 4) { /* group: new / open / save */ }
        }
        row.Children.Add(ViewHelpers.Separator());

        if (debug is not null)
        {
            _configs = new ComboBox { MinWidth = 150, Height = 22, MinHeight = 0, Padding = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12, PlaceholderText = "No configurations" };
            _configs.SelectionChanged += (_, _) =>
            {
                if (_configs.SelectedItem is DebugConfiguration c && !ReferenceEquals(debug.Configurations.Selected, c)) debug.Configurations.Selected = c;
            };
            row.Children.Add(_configs);
            _subs.Add(debug.Configurations.ConfigurationsChanged.Subscribe(_ => _ui.Post(() => _ = ReloadConfigurationsAsync())));
            _subs.Add(debug.Configurations.SelectedChanged.Subscribe(_ => _ui.Post(SyncSelection)));
            _ = ReloadConfigurationsAsync();
        }

        var startContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { new Icon(IconKey.Play, 14) { Foreground = ThemeBrushes.Get("debugIcon.startForeground"), VerticalAlignment = VerticalAlignment.Center }, _startLabel } };
        _start = new Button { Content = startContent, Padding = new Thickness(8, 0), Height = 22, MinHeight = 0, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _start.Classes.Add("flat");
        _start.Click += async (_, _) => await Run(KnownCommands.DebugStart).ConfigureAwait(true);
        ToolTip.SetTip(_start, Tip("Start Debugging", KnownCommands.DebugStart));
        row.Children.Add(_start);
        var startNoDebug = new Button { Content = ViewHelpers.Glyph("play", 14, "foreground"), Padding = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
        startNoDebug.Classes.Add("flat");
        startNoDebug.Click += async (_, _) => await Run(KnownCommands.DebugRun).ConfigureAwait(true);
        ToolTip.SetTip(startNoDebug, Tip("Start Without Debugging", KnownCommands.DebugRun));
        row.Children.Add(startNoDebug);
        _buttons.Add((startNoDebug, new Spec(KnownCommands.DebugRun, "play", "", "debugState == 'inactive'")));

        var stepSep = ViewHelpers.Separator();
        row.Children.Add(stepSep);
        _debugOnly.Add(stepSep);
        foreach (var spec in Steps)
        {
            var b = MakeButton(spec);
            _debugOnly.Add(b);
            row.Children.Add(b);
        }
        row.Children.Add(ViewHelpers.Separator());
        row.Children.Add(MakeButton(new Spec("workbench.action.findInFiles", IconKey.Search, "Find in Files", "workspaceFolderCount")));

        Child = row;
        Refresh();
        _subs.Add(keys.Changed.Subscribe(_ => ScheduleRefresh()));
        _subs.Add(commands.Registered.Subscribe(_ => ScheduleRefresh()));
    }

    private string Tip(string title, string commandId) => _bindings?.GetLabel(commandId) is { } g ? $"{title} ({g})" : title;

    private Button MakeButton(Spec spec)
    {
        var b = new Button { Content = ViewHelpers.Glyph(spec.Icon, 14, spec.ColorId), Padding = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
        b.Classes.Add("flat");
        ToolTip.SetTip(b, Tip(spec.Tooltip, spec.CommandId));
        b.Click += async (_, _) => await Run(spec.CommandId).ConfigureAwait(true);
        _buttons.Add((b, spec));
        return b;
    }

    private async Task Run(string commandId)
    {
        try { await _commands.ExecuteAsync(commandId).ConfigureAwait(true); }
        catch (CommandNotFoundException) { /* module not installed yet: the button is disabled anyway */ }
    }

    private void ScheduleRefresh() => _ui.PostCoalesced("toolbar.refresh", Refresh);

    /// <summary>Recomputes enablement and the step-button visibility.</summary>
    public void Refresh()
    {
        foreach (var (button, spec) in _buttons)
            button.IsEnabled = _commands.Has(spec.CommandId) && _keys.Evaluate(spec.When);
        if (_start is not null) _start.IsEnabled = _commands.Has(KnownCommands.DebugStart) && _keys.Evaluate("debugState == 'inactive'");
        var debugging = _keys.Get("inDebugMode") is true;
        foreach (var c in _debugOnly) c.IsVisible = debugging;
        _startLabel.Text = _debug?.Configurations.Selected?.Name is { Length: > 0 } n ? n : "Start";
    }

    private async Task ReloadConfigurationsAsync()
    {
        if (_debug is null || _configs is null) return;
        var list = await _debug.Configurations.GetLaunchConfigurationsAsync().ConfigureAwait(true);
        _configs.ItemsSource = list;
        _configs.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<DebugConfiguration>((c, _) => new TextBlock { Text = c?.Name });
        SyncSelection();
    }

    private void SyncSelection()
    {
        if (_configs is null || _debug is null) return;
        var selected = _debug.Configurations.Selected;
        if (!ReferenceEquals(_configs.SelectedItem, selected)) _configs.SelectedItem = selected;
        _startLabel.Text = selected?.Name is { Length: > 0 } n ? n : "Start";
    }

    public void Dispose() => _subs.Dispose();
}
