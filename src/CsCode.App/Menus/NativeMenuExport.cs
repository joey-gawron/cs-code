using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui.Threading;

namespace CsCode.App.Menus;

/// <summary>
/// The macOS menu bar, exported from the <see cref="MenuModel"/> that the in-window menu bar shows on the other platforms: File ... Help for a
/// window (<see cref="BuildWindowMenu"/>; Window gains Minimize and Zoom), and the application menu (<see cref="BuildApplicationMenu"/>: About
/// CsCode and Settings..., above the Services, Hide, Hide Others, Show All and Quit CsCode items Avalonia adds). File &gt; Exit and Help &gt; About
/// are left out: their place on a Mac is the application menu. Like the in-window bar, a menu is rebuilt from the model each time it opens, so
/// enablement, check marks, recent folders and the tool-window list are current. UI-thread object; dispose to drop its subscriptions.
/// <para>Shortcuts: macOS gives a key to the menu bar's key equivalents before the window sees it. A shortcut that both had would run twice; one
/// that only the menu had would bypass the keyboard pipeline (chords, when-clauses, Vim, the terminal's own keys, keybindings.json). So the
/// exported items carry no key equivalent (every bound shortcut stays the pipeline's, and the item's tooltip names it), and the items this menu
/// adds itself (Settings... ⌘, and Minimize ⌘M) carry theirs only while no keybinding starts with that stroke.</para>
/// </summary>
public sealed class NativeMenuExport : IDisposable
{
    /// <summary>The application menu's Settings... item, and the stroke and gesture it would take (⌘,).</summary>
    internal static readonly (KeyStroke Stroke, KeyGesture Gesture) SettingsKey = (KeyStrokeParser.ParseStroke("cmd+,"), new KeyGesture(Key.OemComma, KeyModifiers.Meta));
    internal static readonly (KeyStroke Stroke, KeyGesture Gesture) MinimizeKey = (KeyStrokeParser.ParseStroke("cmd+m"), new KeyGesture(Key.M, KeyModifiers.Meta));
    /// <summary>The application menu's New Window item (⇧⌘N, VS Code's on a Mac).</summary>
    internal static readonly (KeyStroke Stroke, KeyGesture Gesture) NewWindowKey = (KeyStrokeParser.ParseStroke("cmd+shift+n"), new KeyGesture(Key.N, KeyModifiers.Meta | KeyModifiers.Shift));

    private readonly DisposableStore _subs = new();
    private readonly List<(NativeMenuItem Item, (KeyStroke Stroke, KeyGesture Gesture) Key)> _keyed = new();
    private IKeybindingService? _bindings;
    /// <summary>The application menu: the run whose commands its items run (<see cref="Connect"/>); null while there is none.</summary>
    private IServiceProvider? _services;
    private bool _disposed;

    private NativeMenuExport(IKeybindingService? bindings, IUiDispatcher? ui)
    {
        _bindings = bindings;
        if (bindings is not null && ui is not null) _subs.Add(FollowKeybindings(bindings, ui));
    }

    /// <summary>The menu bar (a window's), or the application menu's items.</summary>
    public NativeMenu Menu { get; } = new();

    /// <summary>File ... Help from <paramref name="model"/>, for <paramref name="window"/> (whose Window menu minimises and zooms it).</summary>
    public static NativeMenuExport BuildWindowMenu(MenuModel model, Window window, IKeybindingService? bindings, IUiDispatcher ui)
    {
        var export = new NativeMenuExport(bindings, ui);
        foreach (var root in model.Roots)
        {
            var top = new NativeMenuItem(Header(root.Header)) { Menu = new NativeMenu() };
            var native = top.Menu;
            var captured = root;
            NativeMenuItemBase[] own = Header(root.Header) == "Window" ? export.WindowItems(window) : [];
            void Populate()
            {
                native.Items.Clear();
                foreach (var item in own) native.Items.Add(item);
                export.Populate(native, captured);
            }
            Populate();
            native.NeedsUpdate += (_, _) =>
            {
                if (export._disposed) return;
                model.Refresh();
                Populate();
            };
            export.Menu.Items.Add(top);
        }
        return export;
    }

    /// <summary>
    /// The application menu's own items: About CsCode, Settings..., New Window (Avalonia adds the standard ones below them). It belongs to the
    /// application, not to a run: set before the first window (macOS reads it as the app starts), About and Settings do nothing until a run is
    /// <see cref="Connect"/>ed. New Window runs <see cref="NewWindow"/> (the app's, which works with no window at all: the menu bar is all a Mac
    /// app shows after its last window closed), and carries ⇧⌘N while no keybinding takes the stroke (always while no window is open).
    /// </summary>
    public static NativeMenuExport BuildApplicationMenu()
    {
        var export = new NativeMenuExport(null, null);
        export.Menu.Items.Add(Item("About CsCode", () => export.Run(ShellMenus.About)));
        export.Menu.Items.Add(new NativeMenuItemSeparator());
        export.Menu.Items.Add(export.Keyed(Item("Settings…", () => export.Run(KnownCommands.OpenSettings)), SettingsKey));
        export.Menu.Items.Add(new NativeMenuItemSeparator());
        export.Menu.Items.Add(export.Keyed(Item("New Window", () => export.NewWindow?.Invoke()), NewWindowKey));
        export.Menu.Items.Add(new NativeMenuItemSeparator());
        return export;
    }

    /// <summary>The Dock icon's menu (macOS, above the system's Options, Show in Finder, Hide and Quit): New Window, running <paramref name="newWindow"/>.</summary>
    public static NativeMenu BuildDockMenu(Action newWindow) => new() { Items = { Item("New Window", newWindow) } };

    /// <summary>What the application menu's New Window does (the app's <see cref="Startup.AppSession.NewWindow"/>); null: nothing.</summary>
    public Action? NewWindow { get; set; }

    /// <summary>The application menu runs <paramref name="services"/>' commands and follows its keybindings until the result is disposed.</summary>
    public IDisposable Connect(IServiceProvider services)
    {
        _services = services;
        _bindings = services.GetService(typeof(IKeybindingService)) as IKeybindingService;
        UpdateKeys();
        var follow = _bindings is not null && services.GetService(typeof(IUiDispatcher)) is IUiDispatcher ui ? FollowKeybindings(_bindings, ui) : null;
        return Disposable.Create(() =>
        {
            follow?.Dispose();
            if (!ReferenceEquals(_services, services)) return; // another run connected since
            _services = null;
            _bindings = null;
            UpdateKeys();
        });
    }

    // Keybindings reload off the UI thread (keybindings.json edited): the key equivalents follow on it.
    private IDisposable FollowKeybindings(IKeybindingService bindings, IUiDispatcher ui) =>
        bindings.Changed.Subscribe(_ => ui.PostCoalesced("nativemenu.keys." + RuntimeHelpers.GetHashCode(this), UpdateKeys));

    /// <summary>
    /// The key equivalent an item this menu adds may carry: <paramref name="key"/>'s gesture while no keybinding starts with its stroke, else none
    /// (the keyboard pipeline handles the stroke: one handler per key).
    /// </summary>
    internal static KeyGesture? NativeGesture(IKeybindingService? bindings, (KeyStroke Stroke, KeyGesture Gesture) key) =>
        bindings is not null && bindings.All.Any(b => !b.IsNegative && b.Chord.Count > 0 && b.Chord[0] == key.Stroke) ? null : key.Gesture;

    /// <summary>A menu header without its access-key marks ("Save A_ll" → "Save All"; "__" is a literal underscore) and with a Mac ellipsis.</summary>
    internal static string Header(string header)
    {
        var text = new StringBuilder(header.Length);
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] != '_') text.Append(header[i]);
            else if (i + 1 < header.Length && header[i + 1] == '_') text.Append(header[++i]);
        }
        if (text.Length >= 3 && text.ToString(text.Length - 3, 3) == "...") text.Remove(text.Length - 3, 3).Append('…');
        return text.ToString();
    }

    /// <summary>The Mac Window menu's own items, above the model's: Minimize (⌘M) and Zoom.</summary>
    private NativeMenuItemBase[] WindowItems(Window window) =>
    [
        Keyed(Item("Minimize", () => window.WindowState = WindowState.Minimized), MinimizeKey),
        Item("Zoom", () => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized),
        new NativeMenuItemSeparator(),
    ];

    private void Populate(NativeMenu target, MenuItemVm vm)
    {
        var added = new List<NativeMenuItemBase>();
        foreach (var child in vm.Children)
        {
            if (child.IsSeparator) { added.Add(new NativeMenuItemSeparator()); continue; }
            if (child.CommandId is KnownCommands.Quit or ShellMenus.About) continue; // the application menu's
            var item = new NativeMenuItem(Header(child.Header)) { IsEnabled = child.IsEnabled, ToolTip = child.Gesture };
            if (child.IsChecked)
            {
                item.ToggleType = MenuItemToggleType.CheckBox;
                item.IsChecked = true;
            }
            if (child.HasChildren)
            {
                item.Menu = new NativeMenu();
                Populate(item.Menu, child);
            }
            else if (child.Command is { } command) item.Click += (_, _) => { if (command.CanExecute(null)) command.Execute(null); };
            added.Add(item);
        }
        // No separator first, last or twice in a row (left where an item went to the application menu).
        for (var i = 0; i < added.Count; i++)
        {
            var separator = added[i] is NativeMenuItemSeparator;
            if (separator && (target.Items.Count == 0 || target.Items[^1] is NativeMenuItemSeparator || i == added.Count - 1)) continue;
            target.Items.Add(added[i]);
        }
        if (target.Items.Count > 0 && target.Items[^1] is NativeMenuItemSeparator) target.Items.RemoveAt(target.Items.Count - 1);
    }

    private NativeMenuItem Keyed(NativeMenuItem item, (KeyStroke Stroke, KeyGesture Gesture) key)
    {
        _keyed.Add((item, key));
        item.Gesture = NativeGesture(_bindings, key);
        return item;
    }

    private void UpdateKeys()
    {
        if (_disposed) return;
        foreach (var (item, key) in _keyed) item.Gesture = NativeGesture(_bindings, key);
    }

    private static NativeMenuItem Item(string header, Action click)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => click();
        return item;
    }

    private async void Run(string id)
    {
        if (_services?.GetService(typeof(ICommandService)) is not ICommandService commands) return;
        try { await commands.ExecuteAsync(id).ConfigureAwait(true); }
        catch (CommandNotFoundException) { /* not registered in this composition: the item does nothing */ }
    }

    public void Dispose()
    {
        _disposed = true;
        _subs.Dispose();
        _keyed.Clear();
    }
}
