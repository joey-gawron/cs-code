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
internal sealed class VimShellActions(VimIntegration owner, DocumentWell well, IWindowService? windows, IWorkspaceService? workspace)
{
    public bool Perform(VimView view, VimEditorAction action, in VimActionArgs args)
    {
        if (action is VimEditorAction.ResizeEqual or VimEditorAction.ResizeMaximize or VimEditorAction.ResizeIncreaseHeight or VimEditorAction.ResizeDecreaseHeight
            or VimEditorAction.ResizeIncreaseWidth or VimEditorAction.ResizeDecreaseWidth or VimEditorAction.MoveWindowLeft or VimEditorAction.MoveWindowRight
            or VimEditorAction.MoveWindowUp or VimEditorAction.MoveWindowDown or VimEditorAction.ExchangeWindow or VimEditorAction.ShowRegisters or VimEditorAction.ShowMarks)
            return false; // groups are sized and arranged by dragging in this shell
        // After the key: closing or switching tabs may destroy the very view whose engine is asking.
        var a = args;
        owner.Post(() => PerformNow(view, action, a));
        return true;
    }

    private void PerformNow(VimView view, VimEditorAction action, VimActionArgs args)
    {
        var tab = TabOf(view);
        var arg = args.Argument?.Trim();
        var bang = args.Bang;
        switch (action)
        {
            case VimEditorAction.Write:
                if (tab is null) return;
                Run(() => WriteAsync(tab, arg));
                return;
            case VimEditorAction.WriteQuit:
                if (tab is null) return;
                Run(async () =>
                {
                    if (await WriteAsync(tab, arg).ConfigureAwait(true)) await well.CloseAsync(tab, prompt: false).ConfigureAwait(true);
                });
                return;
            case VimEditorAction.Quit or VimEditorAction.Close or VimEditorAction.DeleteBuffer or VimEditorAction.CloseTab:
                if (tab is null) return;
                Run(() => well.CloseAsync(tab, prompt: !bang).AsTask());
                return;
            case VimEditorAction.QuitAll:
                Run(async () =>
                {
                    foreach (var t in well.EditorTabs.ToArray())
                        if (!await well.CloseAsync(t, prompt: !bang).ConfigureAwait(true)) return; // Cancel stops, like Vim's :qa on a modified buffer
                });
                return;
            case VimEditorAction.WriteAll:
                Run(SaveAllAsync);
                return;
            case VimEditorAction.WriteQuitAll:
                Run(async () =>
                {
                    await SaveAllAsync().ConfigureAwait(true);
                    foreach (var t in well.EditorTabs.ToArray()) await well.CloseAsync(t, prompt: true).ConfigureAwait(true);
                });
                return;
            case VimEditorAction.Edit or VimEditorAction.NewTab:
                if (string.IsNullOrEmpty(arg))
                {
                    if (action == VimEditorAction.NewTab) well.OpenUntitled();
                    else if (tab is not null && bang) Run(() => tab.Doc.ReloadAsync()); // :e! re-reads the file
                    return;                                                      // :e re-edits the current file: it is already current
                }
                if (Resolve(arg, tab) is not { } uri) return;
                Run(() => well.OpenAsync(uri).AsTask());
                return;
            case VimEditorAction.Reload:
                if (tab is null) return;
                Run(() => tab.Doc.ReloadAsync());
                return;
            case VimEditorAction.NewBuffer:
                well.OpenUntitled();
                return;
            case VimEditorAction.SaveAs:
                if (tab is null) return;
                if (string.IsNullOrEmpty(arg)) Run(() => well.SaveAsync(tab, saveAs: true).AsTask());
                else if (Resolve(arg, tab) is { } target) Run(() => tab.Doc.SaveAsAsync(target));
                return;
            case VimEditorAction.NextBuffer or VimEditorAction.NextTab:
                Cycle(tab, +Math.Max(1, args.Count ?? 1));
                return;
            case VimEditorAction.PreviousBuffer or VimEditorAction.PreviousTab:
                Cycle(tab, -Math.Max(1, args.Count ?? 1));
                return;
            case VimEditorAction.GoToBuffer:
                GoToBuffer(view, arg, args.Count);
                return;
            case VimEditorAction.AlternateBuffer:
                if (well.MruOrder.OfType<EditorTab>().FirstOrDefault(t => !ReferenceEquals(t, tab)) is { } alt) well.Activate(alt);
                else owner.ShowError(view, "E23: No alternate file");
                return;
            case VimEditorAction.ListBuffers:
                Run(ListBuffersAsync);
                return;
            case VimEditorAction.Split or VimEditorAction.VSplit or VimEditorAction.NewSplit or VimEditorAction.NewVSplit:
                Split(tab, arg, vertical: action is VimEditorAction.VSplit or VimEditorAction.NewVSplit, untitled: action is VimEditorAction.NewSplit or VimEditorAction.NewVSplit);
                return;
            case VimEditorAction.Only:
                Run(() => well.CloseOtherGroupsAsync(tab).AsTask());
                return;
            case VimEditorAction.FocusLeft or VimEditorAction.FocusRight or VimEditorAction.FocusUp or VimEditorAction.FocusDown:
                well.FocusGroup(tab, action switch
                {
                    VimEditorAction.FocusLeft => GroupDirection.Left,
                    VimEditorAction.FocusRight => GroupDirection.Right,
                    VimEditorAction.FocusUp => GroupDirection.Up,
                    _ => GroupDirection.Down,
                });
                return;
            case VimEditorAction.FocusNext or VimEditorAction.FocusPrevious or VimEditorAction.FocusTop or VimEditorAction.FocusBottom or VimEditorAction.FocusLast:
                well.FocusGroup(tab, action switch
                {
                    VimEditorAction.FocusTop => GroupCycle.First,
                    VimEditorAction.FocusBottom => GroupCycle.Last,
                    VimEditorAction.FocusPrevious or VimEditorAction.FocusLast => GroupCycle.Previous,
                    _ => GroupCycle.Next,
                });
                return;
            case VimEditorAction.Help:
                if (windows is not null) Run(() => windows.OpenExternalAsync(new Uri("https://vimhelp.org/" + (string.IsNullOrEmpty(arg) ? "" : "search.html#q=" + Uri.EscapeDataString(arg)))).AsTask());
                return;
            default:
                return;
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
                if (untitled)
                {
                    well.OpenUntitledSplit(right: vertical);
                    return;
                }
                var opened = Resolve(fileName!, tab) is { } uri ? await well.OpenAsync(uri).ConfigureAwait(true) : null;
                if (opened is null || ReferenceEquals(opened, tab)) return;
                if (vertical) well.SplitRight(opened); else well.SplitDown(opened);
            });
            return;
        }
        if (tab is null) return;
        if (vertical) well.SplitRight(tab); else well.SplitDown(tab);
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
