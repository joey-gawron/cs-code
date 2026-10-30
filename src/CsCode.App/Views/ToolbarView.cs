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

/// <summary>A group of the toolbar that View &gt; Toolbars shows or hides; <paramref name="SettingKey"/> (a boolean, default true) persists it.</summary>
public sealed record ToolbarGroup(string Id, string Title, string SettingKey);

/// <summary>
/// The standard toolbar (VS2022 layout), in groups that View &gt; Toolbars hides: <b>Standard</b> (back/forward, new/open/save/save-all,
/// undo/redo, find in files) and <b>Debug</b> (launch-configuration dropdown with the green Start button, step buttons that appear only while
/// debugging). Every button runs a command id; enablement follows <c>when</c> clauses and command availability and is refreshed (coalesced)
/// when context keys or registrations change. The launch-configuration combo is bound to <see cref="IDebugConfigurationService"/> only when a
/// debug service is resolvable. There is no Color Theme dropdown here (the user's choice, 2026-10-29): it lives on the Settings page only
/// (<i>Environment › Appearance</i>), and the palette's Color Theme command stays.
/// </summary>
public sealed class ToolbarView : Border, IDisposable
{
    public const string StandardGroup = "standard";
    public const string DebugGroup = "debug";

    /// <summary>The hideable groups, in toolbar order. Their settings are declared by the shell (<see cref="ShellController"/>).</summary>
    public static IReadOnlyList<ToolbarGroup> Groups { get; } =
    [
        new(StandardGroup, "Standard", "cscode.toolbars.standard"),
        new(DebugGroup, "Debug", "cscode.toolbars.debug"),
    ];

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
    private readonly IConfigurationService? _config;
    private readonly Dictionary<string, (Panel Panel, Control LeadingSeparator)> _groups = new(StringComparer.Ordinal);
    private Button? _start;

    public ToolbarView(ICommandService commands, IContextKeyService keys, IUiDispatcher ui, IKeybindingService? bindings = null, IDebugService? debug = null,
        IConfigurationService? config = null)
    {
        _commands = commands;
        _keys = keys;
        _bindings = bindings;
        _ui = ui;
        _debug = debug;
        _config = config;
        Height = 30;
        Background = ThemeBrushes.Get("toolbar.background");
        BorderBrush = ThemeBrushes.Get("panel.border");
        BorderThickness = new Thickness(0, 0, 0, 1);
        TextElement.SetForeground(this, ThemeBrushes.Get("toolbar.foreground"));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0), Spacing = 1 };
        StackPanel Group(string id)
        {
            // A group carries its own leading separator, so hiding a group leaves no double separator behind.
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Name = "toolbar-" + id };
            var separator = ViewHelpers.Separator();
            panel.Children.Add(separator);
            _groups[id] = (panel, separator);
            row.Children.Add(panel);
            return panel;
        }

        var standard = Group(StandardGroup);
        var i = 0;
        foreach (var spec in Standard)
        {
            standard.Children.Add(MakeButton(spec));
            if (++i is 2 or 6) standard.Children.Add(ViewHelpers.Separator());
        }
        standard.Children.Add(ViewHelpers.Separator());
        standard.Children.Add(MakeButton(new Spec("workbench.action.findInFiles", IconKey.Search, "Find in Files", "workspaceFolderCount")));

        var debugGroup = Group(DebugGroup);
        if (debug is not null)
        {
            _configs = new ComboBox { MinWidth = 150, Height = 22, MinHeight = 0, Padding = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12, PlaceholderText = "No configurations" };
            _configs.SelectionChanged += (_, _) =>
            {
                if (_configs.SelectedItem is DebugConfiguration c && !ReferenceEquals(debug.Configurations.Selected, c)) debug.Configurations.Selected = c;
            };
            debugGroup.Children.Add(_configs);
            _subs.Add(debug.Configurations.ConfigurationsChanged.Subscribe(_ => _ui.Post(() => _ = ReloadConfigurationsAsync())));
            _subs.Add(debug.Configurations.SelectedChanged.Subscribe(_ => _ui.Post(SyncSelection)));
            _ = ReloadConfigurationsAsync();
        }

        var startContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { new Icon(IconKey.Play, 14) { Foreground = ThemeBrushes.Get("debugIcon.startForeground"), VerticalAlignment = VerticalAlignment.Center }, _startLabel } };
        _start = new Button { Content = startContent, Padding = new Thickness(8, 0), Height = 22, MinHeight = 0, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _start.Classes.Add("flat");
        _start.Click += async (_, _) => await Run(KnownCommands.DebugStart).ConfigureAwait(true);
        ToolTip.SetTip(_start, Tip("Start Debugging", KnownCommands.DebugStart));
        debugGroup.Children.Add(_start);
        var startNoDebug = new Button { Content = ViewHelpers.Glyph("play", 14, "foreground"), Padding = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
        startNoDebug.Classes.Add("flat");
        startNoDebug.Click += async (_, _) => await Run(KnownCommands.DebugRun).ConfigureAwait(true);
        ToolTip.SetTip(startNoDebug, Tip("Start Without Debugging", KnownCommands.DebugRun));
        debugGroup.Children.Add(startNoDebug);
        _buttons.Add((startNoDebug, new Spec(KnownCommands.DebugRun, "play", "", "debugState == 'inactive'")));

        var stepSep = ViewHelpers.Separator();
        debugGroup.Children.Add(stepSep);
        _debugOnly.Add(stepSep);
        foreach (var spec in Steps)
        {
            var b = MakeButton(spec);
            _debugOnly.Add(b);
            debugGroup.Children.Add(b);
        }

        Child = row;
        Refresh();
        ApplyGroupVisibility();
        _subs.Add(keys.Changed.Subscribe(_ => ScheduleRefresh()));
        _subs.Add(commands.Registered.Subscribe(_ => ScheduleRefresh()));
        if (config is not null)
            _subs.Add(config.Changed.Subscribe(e =>
            {
                if (Groups.Any(g => e.Affects(g.SettingKey))) _ui.PostCoalesced("toolbar.groups", ApplyGroupVisibility);
            }));
    }

    /// <summary>Whether a group is shown (View &gt; Toolbars).</summary>
    public bool IsGroupVisible(string groupId) => _groups.TryGetValue(groupId, out var g) && g.Panel.IsVisible;

    private void ApplyGroupVisibility()
    {
        // Only a group with a visible group before it shows its leading separator.
        var anyBefore = false;
        foreach (var g in Groups)
        {
            if (!_groups.TryGetValue(g.Id, out var group)) continue;
            group.Panel.IsVisible = _config?.Get(g.SettingKey, true) ?? true;
            group.LeadingSeparator.IsVisible = anyBefore;
            anyBefore |= group.Panel.IsVisible;
        }
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
