using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CsCode.App.Docking;
using CsCode.App.ToolWindows.Explorer;
using CsCode.Core.Contributions;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui.Menus;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace CsCode.App.Menus;

/// <summary>
/// The right-click menus the shell owns at window level, all built with <see cref="ContextMenus"/> from <see cref="MenuEntry"/> lists:
/// <list type="bullet">
/// <item>the editor's text area: entirely from <see cref="MenuIds.EditorContext"/> contributions (the editor module adds its items, the shell
/// hard-codes none), opened at the pointer or, from the keyboard (Shift+F10, the Menu key), below the caret;</item>
/// <item>document tabs (Visual Studio's tab menu, plus <see cref="MenuIds.EditorTitleContext"/> contributions), replacing Dock's own;</item>
/// <item>tool-window title bars and tool tabs (Dock, Auto Hide, Close, plus <see cref="MenuIds.ToolWindowTitleContext"/>), replacing Dock's own.</item>
/// </list>
/// A tunnelling handler on the window sees every ContextRequested first: requests it does not own (a text box, a panel row) pass on untouched.
/// Also registers the file commands those menus and the Explorer's share: Copy Full Path, Copy Relative Path, Open Containing Folder, Reveal in
/// Explorer (VS Code's ids, taking the file's URI).
/// </summary>
public sealed class ShellContextMenus : IDisposable
{
    public const string CopyPath = "copyFilePath";
    public const string CopyRelativePath = "copyRelativeFilePath";
    public const string RevealInOs = "revealFileInOS";
    public const string RevealInExplorer = "revealInExplorer";

    private readonly DocumentWell _well;
    private readonly ToolWindowManager _tools;
    private readonly ShellDockFactory _factory;
    private readonly MenuServices _menus;
    private readonly IWindowService _window;
    private readonly IWorkspaceService? _workspace;
    private readonly DisposableStore _subs = new();

    public ShellContextMenus(DocumentWell well, ToolWindowManager tools, ShellDockFactory factory, MenuServices menus, IWindowService window, IWorkspaceService? workspace)
    {
        _well = well;
        _tools = tools;
        _factory = factory;
        _menus = menus;
        _window = window;
        _workspace = workspace;
        var commands = menus.Commands;
        void Reg(string id, string title, Func<Uri, Task> run) => _subs.Add(commands.Register(id, async inv =>
        {
            if (UriArg(inv) is { } uri) await run(uri).ConfigureAwait(true);
            return null;
        }, new CommandMetadata(id, title, "File")));
        Reg(CopyPath, "Copy Path of Active File", uri => _window.CopyToClipboardAsync(PathOf(uri)).AsTask());
        Reg(CopyRelativePath, "Copy Relative Path of Active File", uri => _window.CopyToClipboardAsync(RelativePathOf(uri)).AsTask());
        Reg(RevealInOs, "Reveal Active File in File Manager", async uri =>
        {
            var folder = uri.IsFile && Directory.Exists(uri.LocalPath) ? uri.LocalPath : Path.GetDirectoryName(uri.LocalPath);
            if (folder is not null) await _window.OpenExternalAsync(new Uri(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar)).ConfigureAwait(true);
        });
        Reg(RevealInExplorer, "Reveal Active File in Explorer", RevealInExplorerAsync);
    }

    /// <summary>The command argument as a URI (a Uri, its string form, or a file path); without one, the active editor's file.</summary>
    private Uri? UriArg(CommandInvocation inv) => inv.Args is [{ } a, ..] ? a switch
    {
        Uri u => u,
        string s when Uri.TryCreate(s, UriKind.Absolute, out var u) => u,
        string s when Path.IsPathRooted(s) => new Uri(s),
        _ => null,
    } : _well.ActiveEditor?.Uri;

    internal static string PathOf(Uri uri) => uri.IsFile ? uri.LocalPath : uri.ToString();

    internal string RelativePathOf(Uri uri)
    {
        if (!uri.IsFile) return uri.ToString();
        var path = uri.LocalPath;
        var folder = _workspace?.Folders.Select(f => f.LocalPath).Where(f => path.StartsWith(Path.TrimEndingDirectorySeparator(f) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderByDescending(f => f.Length).FirstOrDefault();
        return folder is null ? path : Path.GetRelativePath(folder, path);
    }

    private async Task RevealInExplorerAsync(Uri uri)
    {
        _tools.Show("explorer");
        if (_tools.Get("explorer")?.LiveView is ExplorerView explorer) await explorer.Model.RevealAsync(uri).ConfigureAwait(true);
    }

    /// <summary>Installs the window-level handler; dispose to remove it.</summary>
    public IDisposable Attach(Control root)
    {
        root.AddHandler(InputElement.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
        return Disposable.Create(() => root.RemoveHandler(InputElement.ContextRequestedEvent, OnContextRequested));
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || e.Source is not Visual source) return;
        if (FindEditor(source) is { } editor)
        {
            if (HasOwnMenu(source, editor.View)) return; // the find box inside the editor keeps its text menu
            e.Handled = true;
            OpenEditorMenu(editor.Tab, editor.View, e);
            return;
        }
        if (source.FindAncestorOfType<DocumentTabStripItem>(includeSelf: true) is { DataContext: ShellDocument doc } docItem)
        {
            e.Handled = true;
            OpenTabMenu(doc, docItem);
            return;
        }
        if (FindTool(source) is { } tool)
        {
            e.Handled = true;
            OpenToolMenu(tool.Tab, tool.Anchor);
        }
    }

    // ------------------------------------------------------------------ editor

    private (EditorTab Tab, Control View)? FindEditor(Visual source)
    {
        foreach (var tab in _well.EditorTabs)
            if (tab.LiveView is { } view && (ReferenceEquals(view, source) || view.IsVisualAncestorOf(source))) return (tab, view);
        return null;
    }

    /// <summary>A control between the source and the container with a menu of its own (a text box's Cut/Copy/Paste) keeps it.</summary>
    private static bool HasOwnMenu(Visual source, Visual container)
    {
        for (var v = source; v is not null && !ReferenceEquals(v, container); v = v.GetVisualParent())
            if (v is TextBox || (v is Control c && (c.ContextMenu is not null || c.ContextFlyout is not null))) return true;
        return false;
    }

    /// <summary>The editor's menu: the editor's contributed items (and any other module's), for the clicked document.</summary>
    internal ContextMenu? OpenEditorMenu(EditorTab tab, Control view, ContextRequestedEventArgs? e = null)
    {
        var controller = tab.Controller;
        controller?.PrepareContextMenu();
        var entries = MenuContributions.Resolve(_menus, MenuIds.EditorContext, [tab.Uri]);
        if (entries.Count == 0) return null;
        Rect? anchor = e is not null && e.TryGetPosition(view, out _) ? null : controller?.GetCaretBounds();
        return ContextMenus.Open(entries, view, anchor, "Editor context menu");
    }

    // ------------------------------------------------------------------ document tabs

    /// <summary>Visual Studio's document-tab menu (plus contributed <see cref="MenuIds.EditorTitleContext"/> items) for <paramref name="doc"/>.</summary>
    internal IReadOnlyList<MenuEntry> TabMenu(ShellDocument doc)
    {
        var editor = doc as EditorTab;
        var file = editor?.Uri is { IsFile: true } u ? u : null;
        var group = _factory.GroupOf(doc);
        var siblings = group?.VisibleDockables?.OfType<ShellDocument>().ToList() ?? [];
        var index = siblings.IndexOf(doc);
        string? Gesture(string id) => _menus.Bindings?.GetLabel(id);
        return MenuSections.Join(
            [
                editor is { IsDirty: true } dirty ? new MenuEntry("_Save " + dirty.Title, async () => await _well.SaveAsync(dirty).ConfigureAwait(true)) { Gesture = Gesture(KnownCommands.Save), Icon = "save" } : null,
            ],
            [
                new MenuEntry("_Close", async () => await _well.CloseAsync(doc).ConfigureAwait(true)) { Gesture = Gesture(KnownCommands.CloseActiveEditor), Icon = "close" },
                new MenuEntry("Close All _But This", async () => await _well.CloseOthersAsync(doc).ConfigureAwait(true)) { Gesture = Gesture("workbench.action.closeOtherEditors"), IsEnabled = _well.Tabs.Count > 1 },
                new MenuEntry("Close _All", async () => await _well.CloseAllAsync().ConfigureAwait(true)) { Gesture = Gesture("workbench.action.closeAllEditors"), Icon = "close-all" },
                new MenuEntry("Close To The _Right", async () => await _well.CloseToTheRightAsync(doc).ConfigureAwait(true)) { IsEnabled = index >= 0 && index < siblings.Count - 1 },
            ],
            [
                editor is null ? null : editor.IsPinned
                    ? new MenuEntry("Unpin _Tab", () => { _well.Unpin(editor); return Task.CompletedTask; }) { Icon = "pinned" }
                    : new MenuEntry("Pin _Tab", () => { _well.Pin(editor); return Task.CompletedTask; }) { Gesture = Gesture("workbench.action.pinEditor"), Icon = "pin" },
            ],
            file is null ? null : (MenuEntry?[])
            [
                _menus.Command("Copy Full _Path", CopyPath, "copy", args: [file]),
                _menus.Command("_Open Containing Folder", RevealInOs, "folder-opened", args: [file]),
                _menus.Command("Reveal in _Explorer", RevealInExplorer, "files", args: [file], enabled: InWorkspace(file)),
            ],
            [
                new MenuEntry("New _Horizontal Document Group", () => { _well.SplitDown(doc); return Task.CompletedTask; }) { Gesture = Gesture("workbench.action.splitEditorDown"), Icon = "split-vertical", IsEnabled = siblings.Count > 1 },
                new MenuEntry("New _Vertical Document Group", () => { _well.SplitRight(doc); return Task.CompletedTask; }) { Gesture = Gesture("workbench.action.splitEditorRight"), Icon = "split-horizontal", IsEnabled = siblings.Count > 1 },
            ],
            MenuContributions.Resolve(_menus, MenuIds.EditorTitleContext, file is null ? [] : [file]));
    }

    private bool InWorkspace(Uri file) =>
        _workspace?.Folders.Any(f => file.LocalPath.StartsWith(Path.TrimEndingDirectorySeparator(f.LocalPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) == true;

    internal ContextMenu OpenTabMenu(ShellDocument doc, Control anchor) => ContextMenus.Open(TabMenu(doc), anchor, accessibleName: doc.Title + " tab menu");

    // ------------------------------------------------------------------ tool windows

    /// <summary>
    /// A tool tab (bottom strip), an auto-hide strip tab, or the title bar of a tool window. A click inside the tool's content is not a title-bar
    /// click: the content (a shell-marked host) handles its own menu.
    /// </summary>
    private (ToolTab Tab, Control Anchor)? FindTool(Visual source)
    {
        for (var v = source; v is not null; v = v.GetVisualParent())
        {
            if (v is Control c && c.GetValue(Input.ShellFocus.AreaProperty) != Input.FocusArea.None) return null; // inside a tool's content
            switch (v)
            {
                case ToolTabStripItem { DataContext: ToolTab tab } item: return (tab, item);
                case ToolPinItemControl { DataContext: ToolTab pinned } pin: return (pinned, pin);
                case ToolChromeControl { DataContext: IDock { ActiveDockable: ToolTab active } } chrome: return (active, chrome);
            }
        }
        return null;
    }

    /// <summary>Visual Studio's tool-window title menu: Dock (when auto-hidden), Auto Hide, Close; plus contributed items. Float is left out:
    /// the shell has no floating host windows yet (see docs/SHELL.md).</summary>
    internal IReadOnlyList<MenuEntry> ToolMenu(ToolTab tool)
    {
        var id = tool.Spec.Id;
        var pinned = _factory.Root is IRootDock root && _factory.IsDockablePinned(tool, root);
        return MenuSections.Join(
            [
                new MenuEntry("_Dock", () => { _tools.Unpin(tool); return Task.CompletedTask; }) { IsEnabled = pinned },
                new MenuEntry("_Auto Hide", () => { _factory.PinDockable(tool); return Task.CompletedTask; }) { IsEnabled = !pinned, Icon = "pin" },
                new MenuEntry("_Close", () => { _tools.Hide(id); return Task.CompletedTask; }) { Icon = "close" },
            ],
            MenuContributions.Resolve(_menus, MenuIds.ToolWindowTitleContext, [id]));
    }

    internal ContextMenu OpenToolMenu(ToolTab tool, Control anchor) => ContextMenus.Open(ToolMenu(tool), anchor, accessibleName: tool.Title + " window menu");

    public void Dispose() => _subs.Dispose();
}
