using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using CsCode.Core.Contributions;
using CsCode.Core.Platform;
using CsCode.Ui.Icons;
using CsCode.Ui.Theming;

namespace CsCode.Ui.Menus;

/// <summary>
/// One resolved item of a context menu: what the user sees (Visual Studio wording with a <c>_</c> access key, the shortcut from the active keymap,
/// an icon) and what it does. Built fresh every time a menu opens, so enablement and shortcut text are always current.
/// </summary>
public sealed class MenuEntry
{
    public MenuEntry(string header, Func<Task>? run = null)
    {
        Header = header;
        Run = run;
    }

    /// <summary>Text with an optional <c>_</c> access key ("Cu_t", "_Copy").</summary>
    public string Header { get; }
    public Func<Task>? Run { get; }
    /// <summary>Shortcut text from the keybinding service ("Ctrl+K, Ctrl+C"), or null.</summary>
    public string? Gesture { get; init; }
    public string? Icon { get; init; }
    /// <summary>The command this item runs, when it is a command (tests and the Edit menu look items up by it).</summary>
    public string? CommandId { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsChecked { get; init; }
    public bool IsSeparator { get; private init; }
    public IReadOnlyList<MenuEntry> Children { get; init; } = [];
    /// <summary>Asynchronous enablement: the item opens disabled and is enabled when this returns true (cancelled when the menu closes).</summary>
    public Func<CancellationToken, ValueTask<bool>>? EnablementCheck { get; init; }

    public static MenuEntry Separator() => new("-") { IsSeparator = true };

    /// <summary>The header without access-key markers, as a screen reader should say it.</summary>
    public string AccessibleName => StripAccessKey(Header);

    internal static string StripAccessKey(string header) => header.Replace("__", "\u0001", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).Replace('\u0001', '_');

    public override string ToString() => IsSeparator ? "-" : AccessibleName + (Gesture is null ? "" : "\t" + Gesture);
}

/// <summary>The services a menu needs to resolve commands: availability, `when` clauses, shortcut labels and contributed items.</summary>
public sealed record MenuServices(ICommandService Commands, IContextKeyService Keys, IKeybindingService? Bindings = null, IContributionRegistry? Contributions = null)
{
    /// <summary>
    /// An item that runs a command: hidden (null) when no module registered the command, disabled when its <see cref="CommandMetadata.Enablement"/>
    /// or <paramref name="enabled"/> says so, shortcut text from the active keymap.
    /// </summary>
    public MenuEntry? Command(string header, string commandId, string? icon = null, bool enabled = true, IReadOnlyList<object?>? args = null, bool isChecked = false)
    {
        if (!Commands.Has(commandId)) return null;
        var metadata = Commands.All.FirstOrDefault(c => c.Id == commandId);
        var allowed = metadata?.Enablement is not { } en || Keys.Evaluate(en);
        return new MenuEntry(header, () => Execute(commandId, args))
        {
            CommandId = commandId,
            Gesture = Bindings?.GetLabel(commandId),
            Icon = icon,
            IsEnabled = enabled && allowed,
            IsChecked = isChecked,
        };
    }

    internal async Task Execute(string commandId, IReadOnlyList<object?>? args)
    {
        if (args is { Count: > 0 }) await Commands.ExecuteAsync(commandId, args.ToArray()).ConfigureAwait(true);
        else await Commands.ExecuteAsync(commandId).ConfigureAwait(true);
    }
}

/// <summary>
/// Turns the <see cref="MenuContribution"/>s of one menu id into entries, so modules add their items to a host's menu (the editor's, a tab's, the
/// Explorer's) instead of the host hard-coding them. Rules (VS Code's): `when` false or an unregistered command hides an item, the command's
/// enablement disables it; groups (<c>"name@order"</c>) become sections, <c>navigation</c> first and the rest by name; a submenu with no visible
/// item is hidden.
/// </summary>
public static class MenuContributions
{
    private const int MaxDepth = 4;

    public static IReadOnlyList<MenuEntry> Resolve(MenuServices services, string menuId, IReadOnlyList<object?>? context = null) =>
        services.Contributions is null ? [] : Resolve(services, menuId, context ?? [], 0);

    private static IReadOnlyList<MenuEntry> Resolve(MenuServices s, string menuId, IReadOnlyList<object?> context, int depth)
    {
        if (depth > MaxDepth) return [];
        var registry = s.Contributions!;
        var items = registry.Of<MenuContribution>().Items;
        if (items.Count == 0) return [];
        Dictionary<string, SubmenuContribution>? submenus = null;
        Dictionary<string, CommandMetadata>? metadata = null;

        var visible = new List<(string Group, double Order, int Seq, MenuEntry Entry)>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i].Value;
            if (item.MenuId != menuId || !s.Keys.Evaluate(item.When)) continue;
            var (group, order) = ParseGroup(item.Group);
            MenuEntry? entry;
            if (item.Submenu is { } subId)
            {
                submenus ??= registry.Of<SubmenuContribution>().Items.GroupBy(x => x.Value.Id).ToDictionary(g => g.Key, g => g.Last().Value);
                var children = Resolve(s, subId, context, depth + 1);
                if (children.Count == 0) continue;
                var declared = submenus.GetValueOrDefault(subId);
                entry = new MenuEntry(item.Title ?? declared?.Title ?? subId) { Icon = item.Icon ?? declared?.Icon, Children = children, CommandId = subId };
            }
            else
            {
                if (!s.Commands.Has(item.Command)) continue;
                metadata ??= s.Commands.All.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.Last());
                var meta = metadata.GetValueOrDefault(item.Command);
                var allowed = meta?.Enablement is not { } en || s.Keys.Evaluate(en);
                var args = item.Args ?? context;
                var check = item.EnablementCheck;
                entry = new MenuEntry(item.Title ?? meta?.Title ?? item.Command, () => s.Execute(item.Command, args))
                {
                    CommandId = item.Command,
                    Gesture = s.Bindings?.GetLabel(item.Command),
                    Icon = item.Icon,
                    IsEnabled = allowed,
                    IsChecked = item.Toggled is { } t && s.Keys.Evaluate(t),
                    EnablementCheck = check is null || !allowed ? null : ct => check(args, ct),
                };
            }
            visible.Add((group, order, i, entry));
        }
        if (visible.Count == 0) return [];
        var sections = visible
            .GroupBy(v => v.Group)
            .OrderBy(g => g.Key == "navigation" ? 0 : 1).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(v => v.Order).ThenBy(v => v.Seq).Select(v => v.Entry));
        return MenuSections.Join(sections.ToArray());
    }

    internal static (string Group, double Order) ParseGroup(string? group)
    {
        if (string.IsNullOrEmpty(group)) return ("", 0);
        var at = group.LastIndexOf('@');
        if (at < 0) return (group, 0);
        return (group[..at], double.TryParse(group[(at + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var o) ? o : 0);
    }
}

/// <summary>Composes menus from sections: empty sections vanish, one separator between non-empty ones, never two in a row or one at an end.</summary>
public static class MenuSections
{
    public static IReadOnlyList<MenuEntry> Join(params IEnumerable<MenuEntry?>?[] sections)
    {
        var result = new List<MenuEntry>();
        foreach (var section in sections)
        {
            if (section is null) continue;
            var items = section.Where(e => e is not null).Select(e => e!).ToList();
            // Leading/trailing/double separators inside a section collapse too.
            Trim(items);
            if (items.Count == 0) continue;
            if (result.Count > 0) result.Add(MenuEntry.Separator());
            foreach (var e in items)
            {
                if (e.IsSeparator && result.Count > 0 && result[^1].IsSeparator) continue;
                result.Add(e);
            }
        }
        Trim(result);
        return result;
    }

    private static void Trim(List<MenuEntry> items)
    {
        while (items.Count > 0 && items[0].IsSeparator) items.RemoveAt(0);
        while (items.Count > 0 && items[^1].IsSeparator) items.RemoveAt(items.Count - 1);
    }
}

/// <summary>
/// Builds and opens Avalonia context menus from <see cref="MenuEntry"/> lists, the same way everywhere: the theme's menu colours (Fluent retargeted
/// at the workbench palette), Codicon icons, a right-aligned shortcut column, access keys, screen-reader names and accelerator keys, keyboard
/// navigation (Avalonia's own). A menu is built when it opens and dropped when it closes, so nothing is retained between openings.
/// </summary>
public static class ContextMenus
{
    /// <summary>Receives exceptions thrown by an item's action (the shell shows them as notifications). Default: ignored.</summary>
    public static Action<Exception>? ErrorSink { get; set; }

    [ThreadStatic] private static WeakReference<ContextMenu>? _lastOpened;

    /// <summary>The menu most recently opened on this thread, while it is alive (tests).</summary>
    public static ContextMenu? LastOpened => _lastOpened is not null && _lastOpened.TryGetTarget(out var m) ? m : null;

    /// <summary>Creates a context menu for <paramref name="entries"/> (not opened).</summary>
    public static ContextMenu Build(IReadOnlyList<MenuEntry> entries, string? accessibleName = null)
    {
        var menu = new ContextMenu();
        if (accessibleName is not null) AutomationProperties.SetName(menu, accessibleName);
        Populate(menu.Items, entries);
        CancellationTokenSource? checks = null;
        menu.Opened += (_, _) =>
        {
            checks?.Cancel();
            checks = new CancellationTokenSource();
            StartChecks(menu.Items, checks.Token);
        };
        menu.Closed += (_, _) =>
        {
            checks?.Cancel();
            checks?.Dispose();
            checks = null;
        };
        return menu;
    }

    /// <summary>
    /// Opens a menu over <paramref name="target"/>: at the pointer, or (opened from the keyboard: Shift+F10, the Menu key) below
    /// <paramref name="keyboardAnchor"/>, a rectangle in <paramref name="target"/>'s coordinates such as the caret.
    /// </summary>
    public static ContextMenu Open(IReadOnlyList<MenuEntry> entries, Control target, Rect? keyboardAnchor = null, string? accessibleName = null)
    {
        var menu = Build(entries, accessibleName);
        menu.PlacementTarget = target;
        if (keyboardAnchor is { } r)
        {
            menu.Placement = PlacementMode.AnchorAndGravity;
            menu.PlacementRect = r;
            menu.PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.BottomLeft;
            menu.PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight;
        }
        else menu.Placement = PlacementMode.Pointer;
        _lastOpened = new WeakReference<ContextMenu>(menu);
        if (entries.Count > 0) menu.Open(target);
        return menu;
    }

    /// <summary>The entry behind a menu item built here (tests, automation).</summary>
    public static MenuEntry? EntryOf(MenuItem item) => item.Tag as MenuEntry;

    /// <summary>Flattened items of a built menu (top level only), skipping separators.</summary>
    public static IEnumerable<MenuItem> ItemsOf(ItemsControl menu) => menu.Items.OfType<MenuItem>();

    private static void Populate(ItemCollection target, IReadOnlyList<MenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator) { target.Add(new Separator()); continue; }
            target.Add(CreateItem(entry));
        }
    }

    private static MenuItem CreateItem(MenuEntry entry)
    {
        var item = new MenuItem { Header = HeaderFor(entry), IsEnabled = entry.IsEnabled, Tag = entry };
        AutomationProperties.SetName(item, entry.AccessibleName);
        if (entry.Gesture is { Length: > 0 } g) AutomationProperties.SetAcceleratorKey(item, g);
        if (entry.Icon is { } icon && IconCatalog.Contains(icon)) item.Icon = new Icon(icon, 14);
        if (entry.IsChecked)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = true;
        }
        if (entry.Children.Count > 0) Populate(item.Items, entry.Children);
        else if (entry.Run is { } run) item.Click += (_, e) => Invoke(run, e);
        return item;
    }

    private static async void Invoke(Func<Task> run, RoutedEventArgs e)
    {
        e.Handled = true;
        try { await run().ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorSink?.Invoke(ex); }
    }

    private static Control HeaderFor(MenuEntry entry)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinWidth = 200 };
        grid.Children.Add(new AccessText { Text = entry.Header, VerticalAlignment = VerticalAlignment.Center });
        if (entry.Gesture is { Length: > 0 } g)
        {
            var gesture = new TextBlock { Text = g, Margin = new Thickness(32, 0, 0, 0), Foreground = ThemeBrushes.Get("descriptionForeground"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(gesture, 1);
            grid.Children.Add(gesture);
        }
        return grid;
    }

    private static void StartChecks(ItemCollection items, CancellationToken ct)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            if (item.Tag is not MenuEntry entry) continue;
            if (entry.EnablementCheck is { } check)
            {
                item.IsEnabled = false;
                _ = RunCheckAsync(item, check, ct);
            }
            if (item.Items.Count > 0) StartChecks(item.Items, ct);
        }
    }

    // Runs on the UI thread up to the first await; the check itself must not block (it awaits a language server, the clipboard owner...).
    private static async Task RunCheckAsync(MenuItem item, Func<CancellationToken, ValueTask<bool>> check, CancellationToken ct)
    {
        try
        {
            var ok = await check(ct).ConfigureAwait(true);
            if (!ct.IsCancellationRequested) item.IsEnabled = ok;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorSink?.Invoke(ex); }
    }
}
