using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsCode.Core.Contributions;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui;
using CsCode.Ui.Threading;

namespace CsCode.App.Menus;

/// <summary>A menu entry as bound by the menu bar / context menus. Enablement, check state and shortcut text refresh with <see cref="MenuModel.Refresh"/>.</summary>
public sealed class MenuItemVm : ObservableObject
{
    private bool _isEnabled = true, _isChecked;
    private string? _gesture;

    internal MenuItemVm(MenuNode node, string header, IRelayCommand? command)
    {
        Node = node;
        Header = header;
        Command = command;
        IsSeparator = node.IsSeparator;
        IconKey = node.IconKey;
    }

    public MenuNode Node { get; }
    public string Header { get; }
    public string? IconKey { get; }
    public bool IsSeparator { get; }
    public IRelayCommand? Command { get; }
    public IReadOnlyList<MenuItemVm> Children { get; internal set; } = [];
    public bool HasChildren => Children.Count > 0;
    public string? CommandId => Node.CommandId;
    public bool IsEnabled { get => _isEnabled; internal set => SetProperty(ref _isEnabled, value); }
    public bool IsChecked { get => _isChecked; internal set => SetProperty(ref _isChecked, value); }
    /// <summary>Platform-formatted shortcut text from the keybinding service ("Ctrl+K, Ctrl+C").</summary>
    public string? Gesture { get => _gesture; internal set => SetProperty(ref _gesture, value); }
}

/// <summary>
/// Builds menu view-models from definitions and keeps them current: enablement from `when` + command availability, shortcut labels from
/// <see cref="IKeybindingService.GetLabel"/>, check state from context keys / tool-window visibility, dynamic bodies (recent folders,
/// tool windows) regenerated on refresh. Refreshes are coalesced to one per UI turn when context keys or keybindings change.
/// UI-thread object.
/// </summary>
public sealed class MenuModel : IDisposable
{
    private readonly ICommandService _commands;
    private readonly IContextKeyService _keys;
    private readonly IKeybindingService? _bindings;
    private readonly IWorkspaceService? _workspace;
    private readonly IUiDispatcher _ui;
    private readonly Func<IEnumerable<IToolWindow>> _toolWindows;
    private readonly Func<string, bool> _isToolVisible;
    private readonly DisposableStore _subs = new();
    private readonly List<MenuItemVm> _all = new();

    public MenuModel(IReadOnlyList<MenuNode> definition, ICommandService commands, IContextKeyService keys, IUiDispatcher ui,
        IKeybindingService? bindings = null, IWorkspaceService? workspace = null,
        Func<IEnumerable<IToolWindow>>? toolWindows = null, Func<string, bool>? isToolVisible = null)
    {
        _commands = commands;
        _keys = keys;
        _bindings = bindings;
        _workspace = workspace;
        _ui = ui;
        _toolWindows = toolWindows ?? (() => []);
        _isToolVisible = isToolVisible ?? (_ => false);
        Roots = definition.Select(Create).ToArray();
        Refresh();
        _subs.Add(keys.Changed.Subscribe(_ => ScheduleRefresh()));
        _subs.Add(commands.Registered.Subscribe(_ => ScheduleRefresh()));
        if (bindings is not null) _subs.Add(bindings.Changed.Subscribe(_ => ScheduleRefresh()));
        if (workspace is not null) _subs.Add(workspace.FoldersChanged.Subscribe(_ => ScheduleRefresh()));
    }

    /// <summary>Top-level menus (File ... Help).</summary>
    public IReadOnlyList<MenuItemVm> Roots { get; }

    /// <summary>Number of refresh passes performed (tests assert coalescing).</summary>
    public int RefreshCount { get; private set; }

    public void ScheduleRefresh() => _ui.PostCoalesced("menus.refresh", Refresh);

    private MenuItemVm Create(MenuNode node)
    {
        IRelayCommand? command = null;
        if (node.CommandId is { } id)
            command = new AsyncRelayCommand(async () => await Run(id, node.Argument));
        var vm = new MenuItemVm(node, node.Header, command);
        if (node.Children is { } kids && node.Dynamic is null) vm.Children = kids.Select(Create).ToArray();
        _all.Add(vm);
        return vm;
    }

    private async Task Run(string id, object? argument)
    {
        try
        {
            if (argument is null) await _commands.ExecuteAsync(id);
            else await _commands.ExecuteAsync(id, argument);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastError = ex;
        }
    }

    /// <summary>Last exception thrown by a menu-invoked command (the shell reports it as a notification).</summary>
    public Exception? LastError { get; private set; }

    /// <summary>Recomputes enablement, check state, shortcut labels and dynamic submenus.</summary>
    public void Refresh()
    {
        RefreshCount++;
        foreach (var root in Roots) RefreshItem(root);
    }

    private void RefreshItem(MenuItemVm item)
    {
        var node = item.Node;
        if (node.Dynamic is not null) item.Children = BuildDynamic(node);
        foreach (var child in item.Children) RefreshItem(child);
        if (node.IsSeparator) return;

        if (node.CommandId is { } id)
        {
            item.IsEnabled = _commands.Has(id) && _keys.Evaluate(node.When);
            item.Gesture = _bindings?.GetLabel(id);
        }
        else if (node.Dynamic is not null) item.IsEnabled = item.Children.Count > 0;
        else item.IsEnabled = item.Children.Any(c => c.IsEnabled && !c.IsSeparator);

        if (node.CheckWhen is not null) item.IsChecked = _keys.Evaluate(node.CheckWhen);
    }

    private IReadOnlyList<MenuItemVm> BuildDynamic(MenuNode node)
    {
        switch (node.Dynamic)
        {
            case "recent":
            {
                var recents = _workspace?.RecentFolders.Take(10).ToArray() ?? [];
                if (recents.Length == 0) return [Placeholder("(no recent folders)")];
                return recents.Select((u, i) => CreateDynamic(new MenuNode($"_{i + 1} {Describe(u)}", ShellMenus.OpenRecent, Argument: u.ToString()))).ToArray();
            }
            case "toolWindows":
                return _toolWindows()
                    .Where(t => t.ToggleCommandId is not null)
                    .OrderBy(t => t.DefaultLocation).ThenBy(t => t.Title, StringComparer.Ordinal)
                    .Select(t => CreateDynamic(new MenuNode(t.Title, t.ToggleCommandId, IconKey: t.IconKey, Argument: null, CheckWhen: null))
                        .WithChecked(_isToolVisible(t.Id)))
                    .ToArray();
            default:
                return [];
        }
    }

    private MenuItemVm Placeholder(string text)
    {
        var vm = new MenuItemVm(new MenuNode(text), text, null) { IsEnabled = false };
        return vm;
    }

    private MenuItemVm CreateDynamic(MenuNode node)
    {
        var command = new AsyncRelayCommand(async () => await Run(node.CommandId!, node.Argument));
        return new MenuItemVm(node, node.Header, command) { IsEnabled = _commands.Has(node.CommandId!) };
    }

    private static string Describe(Uri uri) => uri.IsFile ? uri.LocalPath : uri.ToString();

    /// <summary>Looks up an item by command id (tests and the native-menu mirror use this).</summary>
    public MenuItemVm? Find(string commandId) => _all.Concat(Roots.SelectMany(Flatten)).FirstOrDefault(i => i.CommandId == commandId);

    private static IEnumerable<MenuItemVm> Flatten(MenuItemVm item) => new[] { item }.Concat(item.Children.SelectMany(Flatten));

    public void Dispose()
    {
        _subs.Dispose();
        _all.Clear();
    }
}

internal static class MenuItemVmExtensions
{
    public static MenuItemVm WithChecked(this MenuItemVm item, bool value)
    {
        item.IsChecked = value;
        return item;
    }
}

/// <summary>Contributed menu items (setting-free, file-based): `commandPalette`, `editor/context`, `explorer/context`, `editor/title`.</summary>
public static class ContributedMenus
{
    public const string CommandPalette = "commandPalette";
    public const string EditorContext = "editor/context";
    public const string ExplorerContext = "explorer/context";
    public const string EditorTitle = "editor/title";

    /// <summary>Items of one menu whose `when` currently holds, grouped/ordered by their group string, with titles from command metadata.</summary>
    public static IReadOnlyList<(MenuContribution Item, string Title)> Get(IContributionRegistry registry, ICommandService commands, IContextKeyService keys, string menuId)
    {
        var titles = commands.All.Where(c => c.Title is not null).ToDictionary(c => c.Id, c => c.Title!);
        return registry.Of<MenuContribution>().Items
            .Select(i => i.Value)
            .Where(m => m.MenuId == menuId && keys.Evaluate(m.When))
            .OrderBy(m => m.Group ?? "", StringComparer.Ordinal)
            .Select(m => (m, titles.GetValueOrDefault(m.Command, m.Command)))
            .ToArray();
    }
}
