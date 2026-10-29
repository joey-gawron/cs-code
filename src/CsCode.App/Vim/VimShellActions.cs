using Avalonia;
using Avalonia.Controls;
using CsCode.App.Docking;
using CsCode.Core.Platform;
using CsCode.Vim;

namespace CsCode.App.Vim;

/// <summary>
/// Vim's window, buffer and file commands (<c>:w :q :e :bn :sp :vsp Ctrl-W…</c>) mapped onto the shell. A Vim window is an editor group, a buffer
/// is an editor tab. Everything that can prompt or touch the disk runs asynchronously: <see cref="Perform"/> starts it and returns at once
/// (true = the command exists here), so a key never waits for a dialog or a file.
/// <list type="bullet">
/// <item><c>:q</c> closes the editor tab (with the shell's Save / Don't Save / Cancel prompt when it is dirty), never the application, also on
/// the last editor: CsCode is a workbench, not one Vim window. <c>:q!</c> closes without asking; <c>:qa</c> closes every editor.</item>
/// <item><c>:sp</c>/<c>:vsp</c> move the editor into a new group below / to the right (the shell keeps one tab per file); with a file name the
/// file opens in the new group and the current editor stays.</item>
/// </list>
/// </summary>
internal sealed class VimShellActions(VimIntegration owner, DocumentWell well, ShellDockFactory dock, IWindowService? windows, IWorkspaceService? workspace)
{
    public bool Perform(VimView view, VimEditorAction action, in VimActionArgs args)
    {
        var tab = TabOf(view);
        var arg = args.Argument?.Trim();
        var bang = args.Bang;
        switch (action)
        {
            case VimEditorAction.Write:
                if (tab is null) return false;
                Run(() => WriteAsync(tab, arg));
                return true;
            case VimEditorAction.WriteQuit:
                if (tab is null) return false;
                Run(async () =>
                {
                    if (await WriteAsync(tab, arg).ConfigureAwait(true)) await well.CloseAsync(tab, prompt: false).ConfigureAwait(true);
                });
                return true;
            case VimEditorAction.Quit or VimEditorAction.Close or VimEditorAction.DeleteBuffer or VimEditorAction.CloseTab:
                if (tab is null) return false;
                Run(() => well.CloseAsync(tab, prompt: !bang).AsTask());
                return true;
            case VimEditorAction.QuitAll:
                Run(async () =>
                {
                    foreach (var t in well.EditorTabs.ToArray())
                        if (!await well.CloseAsync(t, prompt: !bang).ConfigureAwait(true)) return; // Cancel stops, like Vim's :qa on a modified buffer
                });
                return true;
            case VimEditorAction.WriteAll:
                Run(SaveAllAsync);
                return true;
            case VimEditorAction.WriteQuitAll:
                Run(async () =>
                {
                    await SaveAllAsync().ConfigureAwait(true);
                    foreach (var t in well.EditorTabs.ToArray()) await well.CloseAsync(t, prompt: true).ConfigureAwait(true);
                });
                return true;
            case VimEditorAction.Edit or VimEditorAction.NewTab:
                if (string.IsNullOrEmpty(arg))
                {
                    if (action == VimEditorAction.NewTab) well.OpenUntitled();
                    else if (tab is not null && bang) Run(() => tab.Doc.ReloadAsync()); // :e! re-reads the file
                    return true;                                                      // :e re-edits the current file: it is already current
                }
                if (Resolve(arg, tab) is not { } uri) { owner.ShowError(view, $"E499: Empty file name for '%' or '#', works only with \":p:h\": {arg}"); return true; }
                Run(() => well.OpenAsync(uri).AsTask());
                return true;
            case VimEditorAction.Reload:
                if (tab is null) return false;
                Run(() => tab.Doc.ReloadAsync());
                return true;
            case VimEditorAction.NewBuffer:
                well.OpenUntitled();
                return true;
            case VimEditorAction.SaveAs:
                if (tab is null) return false;
                if (string.IsNullOrEmpty(arg)) Run(() => well.SaveAsync(tab, saveAs: true).AsTask());
                else if (Resolve(arg, tab) is { } target) Run(() => tab.Doc.SaveAsAsync(target));
                return true;
            case VimEditorAction.NextBuffer or VimEditorAction.NextTab:
                Cycle(tab, +Math.Max(1, args.Count ?? 1));
                return true;
            case VimEditorAction.PreviousBuffer or VimEditorAction.PreviousTab:
                Cycle(tab, -Math.Max(1, args.Count ?? 1));
                return true;
            case VimEditorAction.GoToBuffer:
                GoToBuffer(view, arg, args.Count);
                return true;
            case VimEditorAction.AlternateBuffer:
                if (well.MruOrder.OfType<EditorTab>().FirstOrDefault(t => !ReferenceEquals(t, tab)) is { } alt) well.Activate(alt);
                else owner.ShowError(view, "E23: No alternate file");
                return true;
            case VimEditorAction.ListBuffers:
                Run(ListBuffersAsync);
                return true;
            case VimEditorAction.Split or VimEditorAction.VSplit or VimEditorAction.NewSplit or VimEditorAction.NewVSplit:
                Split(tab, arg, vertical: action is VimEditorAction.VSplit or VimEditorAction.NewVSplit, untitled: action is VimEditorAction.NewSplit or VimEditorAction.NewVSplit);
                return true;
            case VimEditorAction.Only:
                Run(async () =>
                {
                    var group = tab is null ? null : dock.GroupOf(tab);
                    foreach (var t in well.Tabs.Where(t => dock.GroupOf(t) is { } g && !ReferenceEquals(g, group)).ToArray())
                        if (!await well.CloseAsync(t).ConfigureAwait(true)) return;
                });
                return true;
            case VimEditorAction.FocusLeft or VimEditorAction.FocusRight or VimEditorAction.FocusUp or VimEditorAction.FocusDown:
                FocusDirection(tab, action);
                return true;
            case VimEditorAction.FocusNext or VimEditorAction.FocusPrevious or VimEditorAction.FocusTop or VimEditorAction.FocusBottom or VimEditorAction.FocusLast:
                FocusCycle(tab, action);
                return true;
            case VimEditorAction.Help:
                if (windows is not null) Run(() => windows.OpenExternalAsync(new Uri("https://vimhelp.org/" + (string.IsNullOrEmpty(arg) ? "" : "search.html#q=" + Uri.EscapeDataString(arg)))).AsTask());
                return true;
            default:
                return false; // resizing and moving windows: groups are sized and arranged by dragging in this shell
        }
    }

    private EditorTab? TabOf(VimView view) => well.EditorTabs.FirstOrDefault(t => ReferenceEquals(t.LiveView, view.View));

    private async Task<bool> WriteAsync(EditorTab tab, string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return await well.SaveAsync(tab).ConfigureAwait(true);
        // ":w {file}" writes a copy and leaves the buffer as it is (Vim); writing to the buffer's own file is a plain save
        if (Resolve(fileName, tab) is not { } target) return false;
        if (target == tab.Uri) return await well.SaveAsync(tab).ConfigureAwait(true);
        if (workspace is null) return false;
        await workspace.FileSystem.WriteFileAsync(target, System.Text.Encoding.UTF8.GetBytes(tab.Doc.GetText())).ConfigureAwait(true);
        return true;
    }

    private async Task SaveAllAsync()
    {
        foreach (var t in well.EditorTabs.Where(t => t.IsDirty).ToArray())
            if (!await well.SaveAsync(t).ConfigureAwait(true)) return;
    }

    /// <summary>Absolute paths and <c>~/</c> as given; relative ones against the current file's folder, else the workspace folder.</summary>
    internal Uri? Resolve(string path, EditorTab? tab)
    {
        if (path.Length == 0) return null;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute) && absolute.Scheme is not "file") return absolute;
        if (Path.IsPathRooted(path)) return new Uri(Path.GetFullPath(path));
        var baseDir = tab is { Uri.IsFile: true } ? Path.GetDirectoryName(tab.Uri.LocalPath) : workspace?.PrimaryFolder is { IsFile: true } folder ? folder.LocalPath : null;
        baseDir ??= Environment.CurrentDirectory;
        return new Uri(Path.GetFullPath(Path.Combine(baseDir, path)));
    }

    private void Cycle(EditorTab? current, int delta)
    {
        var tabs = well.EditorTabs;
        if (tabs.Count == 0) return;
        var i = current is null ? 0 : Math.Max(0, IndexOf(tabs, current));
        var next = tabs[((i + delta) % tabs.Count + tabs.Count) % tabs.Count];
        well.Activate(next);
    }

    private static int IndexOf(IReadOnlyList<EditorTab> tabs, EditorTab tab)
    {
        for (var i = 0; i < tabs.Count; i++) if (ReferenceEquals(tabs[i], tab)) return i;
        return -1;
    }

    /// <summary><c>:b N</c> (1-based, the order <c>:ls</c> shows), <c>:b name</c> (unique part of the file name), <c>:bfirst</c>/<c>:blast</c>.</summary>
    private void GoToBuffer(VimView view, string? arg, int? count)
    {
        var tabs = well.EditorTabs;
        if (tabs.Count == 0) return;
        EditorTab? target = null;
        if (string.IsNullOrEmpty(arg)) target = count is { } n ? (n >= 1 && n <= tabs.Count ? tabs[n - 1] : null) : tabs[0];
        else if (int.TryParse(arg, out var number)) target = number >= 1 && number <= tabs.Count ? tabs[number - 1] : null;
        else
        {
            var matches = tabs.Where(t => t.Title.Contains(arg, StringComparison.OrdinalIgnoreCase) || t.Uri.ToString().Contains(arg, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1)
            {
                owner.ShowError(view, $"E93: More than one match for {arg}");
                return;
            }
            target = matches.FirstOrDefault();
        }
        if (target is null) owner.ShowError(view, $"E86: Buffer {arg ?? count?.ToString(System.Globalization.CultureInfo.InvariantCulture)} does not exist");
        else well.Activate(target);
    }

    private async Task ListBuffersAsync()
    {
        if (windows is null) return;
        var tabs = well.EditorTabs;
        var active = well.ActiveEditor;
        var items = tabs.Select((t, i) => new QuickPickItem(
            $"{i + 1,3} {(ReferenceEquals(t, active) ? "%a" : "  ")} {(t.IsDirty ? "+" : " ")} {t.Title}",
            t.Uri.IsFile ? t.Uri.LocalPath : t.Uri.ToString(), Tag: t)).ToArray();
        var picked = await windows.ShowQuickPickAsync(items, new QuickPickOptions(Title: ":ls", Placeholder: "Buffers (pick one to switch to it)")).ConfigureAwait(true);
        if (picked?.Tag is EditorTab tab && well.Tabs.Contains(tab)) well.Activate(tab);
    }

    private void Split(EditorTab? tab, string? fileName, bool vertical, bool untitled)
    {
        if (untitled || !string.IsNullOrEmpty(fileName))
        {
            Run(async () =>
            {
                EditorTab? opened = untitled ? well.OpenUntitled() : Resolve(fileName!, tab) is { } uri ? await well.OpenAsync(uri).ConfigureAwait(true) : null;
                if (opened is null || ReferenceEquals(opened, tab)) return;
                if (vertical) well.SplitRight(opened); else well.SplitDown(opened);
            });
            return;
        }
        if (tab is null) return;
        if (vertical) well.SplitRight(tab); else well.SplitDown(tab);
    }

    /// <summary>The active editors of the document groups with their on-screen centres (for Ctrl-W h/j/k/l).</summary>
    private List<(EditorTab Tab, Point Centre)> GroupEditors()
    {
        var list = new List<(EditorTab, Point)>();
        var root = owner.Window;
        foreach (var t in well.EditorTabs)
        {
            if (dock.GroupOf(t) is not { } g || !ReferenceEquals(g.ActiveDockable, t) || t.LiveView is not { } v || root is null) continue;
            if (v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), root) is { } centre) list.Add((t, centre));
        }
        return list;
    }

    private void FocusDirection(EditorTab? tab, VimEditorAction action)
    {
        var editors = GroupEditors();
        var from = editors.FirstOrDefault(e => ReferenceEquals(e.Tab, tab));
        if (from.Tab is null) return;
        var best = editors.Where(e => !ReferenceEquals(e.Tab, tab)).Select(e =>
        {
            var dx = e.Centre.X - from.Centre.X;
            var dy = e.Centre.Y - from.Centre.Y;
            var ok = action switch
            {
                VimEditorAction.FocusLeft => dx < -1 && Math.Abs(dy) <= Math.Abs(dx) * 2,
                VimEditorAction.FocusRight => dx > 1 && Math.Abs(dy) <= Math.Abs(dx) * 2,
                VimEditorAction.FocusUp => dy < -1 && Math.Abs(dx) <= Math.Abs(dy) * 2,
                _ => dy > 1 && Math.Abs(dx) <= Math.Abs(dy) * 2,
            };
            return (e.Tab, Distance: ok ? dx * dx + dy * dy : double.MaxValue);
        }).Where(e => e.Distance < double.MaxValue).OrderBy(e => e.Distance).FirstOrDefault();
        if (best.Tab is not null) Focus(best.Tab);
    }

    private void FocusCycle(EditorTab? tab, VimEditorAction action)
    {
        var editors = GroupEditors().OrderBy(e => e.Centre.Y).ThenBy(e => e.Centre.X).Select(e => e.Tab).ToList();
        if (editors.Count == 0) return;
        var i = tab is null ? -1 : editors.FindIndex(t => ReferenceEquals(t, tab));
        var target = action switch
        {
            VimEditorAction.FocusTop => editors[0],
            VimEditorAction.FocusBottom => editors[^1],
            VimEditorAction.FocusPrevious or VimEditorAction.FocusLast => editors[((i - 1) % editors.Count + editors.Count) % editors.Count],
            _ => editors[(i + 1) % editors.Count],
        };
        Focus(target);
    }

    private void Focus(EditorTab tab)
    {
        well.Activate(tab);
        tab.Controller?.Focus();
    }

    private void Run(Func<Task> work)
    {
        async Task Guarded()
        {
            try { await work().ConfigureAwait(true); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { owner.ShowError(null, ex.Message); }
        }
        _ = Guarded();
    }
}
