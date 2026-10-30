using System.Text.Json;
using CsCode.Core.Contributions;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Terminal;
using CsCode.Terminal.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Terminal;

/// <summary>The terminal's commands (VS Code's ids, titles and default keys) and their implementations over the runtime and the active view.</summary>
public static class TerminalCommandSet
{
    public const string New = TerminalCommands.New;
    public const string NewHere = TerminalCommands.NewHere;
    public const string Toggle = TerminalCommands.Toggle;
    public const string Focus = TerminalCommands.Focus;
    public const string Kill = TerminalCommands.Kill;
    public const string Clear = "workbench.action.terminal.clear";
    public const string FocusNext = "workbench.action.terminal.focusNext";
    public const string FocusPrevious = "workbench.action.terminal.focusPrevious";
    public const string Rename = "workbench.action.terminal.rename";
    public const string SelectAll = "workbench.action.terminal.selectAll";
    public const string CopySelection = "workbench.action.terminal.copySelection";
    public const string Paste = "workbench.action.terminal.paste";
    public const string ScrollUp = "workbench.action.terminal.scrollUp";
    public const string ScrollDown = "workbench.action.terminal.scrollDown";
    public const string ScrollUpPage = "workbench.action.terminal.scrollUpPage";
    public const string ScrollDownPage = "workbench.action.terminal.scrollDownPage";
    public const string ScrollToTop = "workbench.action.terminal.scrollToTop";
    public const string ScrollToBottom = "workbench.action.terminal.scrollToBottom";
    public const string RunSelectedText = TerminalCommands.RunSelectedText;
    public const string RunActiveFile = TerminalCommands.RunActiveFile;
    public const string SendSequence = TerminalCommands.SendSequence;
    public const string ToggleMaximized = "workbench.action.toggleMaximizedPanel";
    public const string ScrollToPreviousCommand = "workbench.action.terminal.scrollToPreviousCommand";
    public const string ScrollToNextCommand = "workbench.action.terminal.scrollToNextCommand";
    public const string FocusFind = "workbench.action.terminal.focusFind";
    public const string FindNext = "workbench.action.terminal.findNext";
    public const string FindPrevious = "workbench.action.terminal.findPrevious";
    public const string HideFind = "workbench.action.terminal.hideFind";
    public const string Split = "workbench.action.terminal.split";
    public const string Unsplit = "workbench.action.terminal.unsplit";
    public const string Join = "workbench.action.terminal.join";
    public const string FocusNextPane = "workbench.action.terminal.focusNextPane";
    public const string FocusPreviousPane = "workbench.action.terminal.focusPreviousPane";
    public const string ResizePaneLeft = "workbench.action.terminal.resizePaneLeft";
    public const string ResizePaneRight = "workbench.action.terminal.resizePaneRight";
    public const string KillAll = "workbench.action.terminal.killAll";

    public static readonly string[] Ids =
    [
        New, NewHere, Toggle, Focus, Kill, Clear, FocusNext, FocusPrevious, Rename, SelectAll, CopySelection, Paste, ScrollUp, ScrollDown, ScrollUpPage,
        ScrollDownPage, ScrollToTop, ScrollToBottom, RunSelectedText, RunActiveFile, SendSequence, ScrollToPreviousCommand, ScrollToNextCommand,
        FocusFind, FindNext, FindPrevious, HideFind, Split, Unsplit, Join, FocusNextPane, FocusPreviousPane, ResizePaneLeft, ResizePaneRight, KillAll,
    ];

    private const string Focused = "terminalFocus";
    private const string FocusedPrimary = "terminalFocus && !terminalAltBufferActive";
    // VS Code binds the pane keys whenever a terminal has focus; CsCode only while the group is split, so Alt+arrows reach a lone shell
    private const string FocusedSplit = "terminalFocus && terminalSplitPaneActive";

    /// <summary>Default keys (VS Code's on Linux/Windows). Ctrl+` (toggle) comes with both keymap presets.</summary>
    public static readonly KeybindingContribution[] Keybindings =
    [
        new(New, "ctrl+shift+`"),
        new(CopySelection, "ctrl+shift+c", When: Focused),
        new(CopySelection, "ctrl+insert", When: Focused),
        new(Paste, "ctrl+shift+v", When: Focused),
        new(Paste, "shift+insert", When: Focused),
        new(SelectAll, "ctrl+shift+a", When: Focused),
        new(ScrollUp, "ctrl+shift+up", When: FocusedPrimary),
        new(ScrollDown, "ctrl+shift+down", When: FocusedPrimary),
        new(ScrollUpPage, "shift+pageup", When: FocusedPrimary),
        new(ScrollDownPage, "shift+pagedown", When: FocusedPrimary),
        new(ScrollToTop, "ctrl+home", When: FocusedPrimary),
        new(ScrollToBottom, "ctrl+end", When: FocusedPrimary),
        new(ScrollToPreviousCommand, "ctrl+up", When: FocusedPrimary),
        new(ScrollToNextCommand, "ctrl+down", When: FocusedPrimary),
        new(FocusFind, "ctrl+f", When: Focused),
        new(FocusNext, "ctrl+pagedown", When: Focused),
        new(FocusPrevious, "ctrl+pageup", When: Focused),
        new(Split, "ctrl+shift+5", When: ProcessSupported),
        new(FocusNextPane, "alt+right", When: FocusedSplit),
        new(FocusNextPane, "alt+down", When: FocusedSplit),
        new(FocusPreviousPane, "alt+left", When: FocusedSplit),
        new(FocusPreviousPane, "alt+up", When: FocusedSplit),
        new(ResizePaneLeft, "ctrl+shift+left", When: FocusedSplit),
        new(ResizePaneRight, "ctrl+shift+right", When: FocusedSplit),
    ];

    private const string ProcessSupported = "terminalProcessSupported";

    public static IDisposable Register(ICommandService commands, TerminalPanelRuntime runtime)
    {
        var store = new DisposableStore();
        void Add(string id, string title, Func<CommandInvocation, Task> run, string? icon = null) =>
            store.Add(commands.Register(id, async inv => { await run(inv).ConfigureAwait(true); return null; }, new CommandMetadata(id, title, "Terminal", icon)));

        var service = runtime.Service;
        Add(New, "Create New Terminal", async _ => await NewTerminalAsync(runtime, null).ConfigureAwait(true), "add");
        Add(NewHere, "Open in Integrated Terminal", async inv => await NewTerminalAsync(runtime, FolderOf(inv)).ConfigureAwait(true));
        Add(Toggle, "Toggle Terminal", async _ =>
        {
            if (runtime.View is { IsShown: true } view) view.Hide();
            else await runtime.ShowAsync(focus: true).ConfigureAwait(true);
        }, "terminal");
        Add(Focus, "Focus Terminal", async _ => await runtime.ShowAsync(focus: true).ConfigureAwait(true));
        Add(Kill, "Kill the Active Terminal Instance", async _ =>
        {
            if (service.ActiveSession is { } active && await ConfirmKillAsync(runtime, active).ConfigureAwait(true)) await service.KillAsync(active).ConfigureAwait(true);
        }, "trash");
        Add(Clear, "Clear", _ =>
        {
            if (service.ActiveSession is { } s) s.Post(() => s.Emulator.ClearKeepingCursorLine());
            return Task.CompletedTask;
        }, "clear-all");
        Add(FocusNext, "Focus Next Terminal Group", async _ => await CycleAsync(runtime, +1).ConfigureAwait(true));
        Add(FocusPrevious, "Focus Previous Terminal Group", async _ => await CycleAsync(runtime, -1).ConfigureAwait(true));
        Add(Split, "Split Terminal", async _ => await SplitAsync(runtime).ConfigureAwait(true), "split-horizontal");
        Add(Unsplit, "Unsplit Terminal", _ =>
        {
            if (service.ActiveSession is { } s) service.Unsplit(s);
            return Task.CompletedTask;
        });
        Add(Join, "Join Terminals...", async _ => await JoinAsync(runtime).ConfigureAwait(true));
        Add(FocusNextPane, "Focus Next Terminal in Terminal Group", _ => { runtime.View?.FocusPane(+1); return Task.CompletedTask; });
        Add(FocusPreviousPane, "Focus Previous Terminal in Terminal Group", _ => { runtime.View?.FocusPane(-1); return Task.CompletedTask; });
        Add(ResizePaneLeft, "Resize Terminal Left", _ => { runtime.View?.ResizeActivePane(-1); return Task.CompletedTask; });
        Add(ResizePaneRight, "Resize Terminal Right", _ => { runtime.View?.ResizeActivePane(+1); return Task.CompletedTask; });
        Add(KillAll, "Kill All Terminals", async _ =>
        {
            foreach (var s in service.Sessions.Reverse()) await service.KillAsync(s).ConfigureAwait(true);
        });
        Add(Rename, "Rename...", async inv =>
        {
            if (service.ActiveSession is not { } s) return;
            var name = inv.Arg<string>(0);
            if (name is null && runtime.Window is { } window)
                name = await window.ShowInputBoxAsync(new InputBoxOptions(Title: "Rename Terminal", Prompt: "Enter a name for the terminal (empty: automatic)", Value: s.Title)).ConfigureAwait(true);
            if (name is not null) s.UserTitle = name;
        });
        Add(SelectAll, "Select All", _ => { runtime.View?.Control.SelectAll(); return Task.CompletedTask; });
        Add(CopySelection, "Copy Selection", async _ => { if (runtime.View?.Control is { } c) await c.CopyAsync().ConfigureAwait(true); }, "copy");
        Add(Paste, "Paste into Active Terminal", async _ => { if (runtime.View?.Control is { } c) await c.PasteAsync().ConfigureAwait(true); }, "clippy");
        Add(ScrollUp, "Scroll Up (Line)", _ => { runtime.View?.Control.ScrollLines(-1); return Task.CompletedTask; });
        Add(ScrollDown, "Scroll Down (Line)", _ => { runtime.View?.Control.ScrollLines(1); return Task.CompletedTask; });
        Add(ScrollUpPage, "Scroll Up (Page)", _ => { runtime.View?.Control.ScrollPages(-1); return Task.CompletedTask; });
        Add(ScrollDownPage, "Scroll Down (Page)", _ => { runtime.View?.Control.ScrollPages(1); return Task.CompletedTask; });
        Add(ScrollToTop, "Scroll to Top", _ => { runtime.View?.Control.ScrollToTop(); return Task.CompletedTask; });
        Add(ScrollToBottom, "Scroll to Bottom", _ => { runtime.View?.Control.ScrollToBottom(); return Task.CompletedTask; });
        Add(ScrollToPreviousCommand, "Scroll To Previous Command", _ => { runtime.View?.Control.ScrollToCommand(-1); return Task.CompletedTask; });
        Add(ScrollToNextCommand, "Scroll To Next Command", _ => { runtime.View?.Control.ScrollToCommand(+1); return Task.CompletedTask; });
        Add(FocusFind, "Focus Find", async _ =>
        {
            if (runtime.View is not { IsShown: true }) await runtime.ShowAsync(focus: true).ConfigureAwait(true);
            runtime.View?.ShowFind();
        }, "search");
        Add(FindNext, "Find Next", async _ => { if (runtime.View is { IsFindVisible: true } v) await v.FindAsync(backward: false).ConfigureAwait(true); });
        Add(FindPrevious, "Find Previous", async _ => { if (runtime.View is { IsFindVisible: true } v) await v.FindAsync(backward: true).ConfigureAwait(true); });
        Add(HideFind, "Hide Find", _ => { runtime.View?.HideFind(); return Task.CompletedTask; });
        Add(RunSelectedText, "Run Selected Text In Active Terminal", async inv =>
        {
            var text = inv.Arg<string>(0) ?? SelectedOrCurrentLine(runtime);
            if (string.IsNullOrEmpty(text)) return;
            await service.SendTextAsync(text, addNewLine: true).ConfigureAwait(true);
            await service.ShowAsync(null, preserveFocus: true).ConfigureAwait(true);
        });
        Add(RunActiveFile, "Run Active File In Active Terminal", async _ =>
        {
            if (runtime.Navigation?.ActiveDocument is not { } doc || !doc.Uri.IsFile)
            {
                runtime.Window?.SetStatusBarMessage("The active editor is not a file on disk.", TimeSpan.FromSeconds(4));
                return;
            }
            if (doc.IsDirty) await doc.SaveAsync().ConfigureAwait(true);
            await service.SendTextAsync(ShellQuote(doc.Uri.LocalPath), addNewLine: true).ConfigureAwait(true);
            await service.ShowAsync(null, preserveFocus: true).ConfigureAwait(true);
        });
        Add(SendSequence, "Send Custom Sequence to Terminal", async inv =>
        {
            var text = SequenceText(inv);
            if (string.IsNullOrEmpty(text)) return;
            if (service.ActiveSession is { HasExited: false } s) s.Input(System.Text.Encoding.UTF8.GetBytes(text));
            else await service.SendTextAsync(text, addNewLine: false).ConfigureAwait(true);
        });
        return store;
    }

    private static async Task NewTerminalAsync(TerminalPanelRuntime runtime, string? cwd)
    {
        var t = await runtime.Service.CreateAsync(new TerminalCreateOptions { Cwd = cwd, Show = false }).ConfigureAwait(true);
        await runtime.Service.ShowAsync(t, preserveFocus: false).ConfigureAwait(true);
    }

    /// <summary>VS Code's Focus Next/Previous Terminal Group: the neighbouring entry of the terminal list (wrapping), its focused pane active.</summary>
    private static async Task CycleAsync(TerminalPanelRuntime runtime, int delta)
    {
        var groups = runtime.Service.Groups;
        if (groups.Count == 0) return;
        var index = runtime.Service.ActiveGroup is { } a ? groups.ToList().IndexOf(a) : 0;
        var group = groups[((index + delta) % groups.Count + groups.Count) % groups.Count];
        if ((group.ActiveTerminal ?? group.Terminals.FirstOrDefault()) is { } next) await runtime.Service.ShowAsync(next, preserveFocus: false).ConfigureAwait(true);
    }

    /// <summary>Split Terminal: a new pane right of the active terminal (no terminal yet: a new one), shown and focused.</summary>
    private static async Task SplitAsync(TerminalPanelRuntime runtime)
    {
        if (runtime.Service.ActiveSession is not { IsDisposed: false } parent)
        {
            await NewTerminalAsync(runtime, null).ConfigureAwait(true);
            return;
        }
        var split = await runtime.Service.SplitAsync(parent).ConfigureAwait(true);
        await runtime.Service.ShowAsync(split, preserveFocus: false).ConfigureAwait(true);
    }

    /// <summary>Join Terminals: pick terminals of other groups; they become panes of the active terminal's group.</summary>
    private static async Task JoinAsync(TerminalPanelRuntime runtime)
    {
        var service = runtime.Service;
        if (service.ActiveSession is not { } target || runtime.Window is not { } window) return;
        var group = service.GroupOf(target);
        var others = service.Sessions.Where(s => group is null || !group.Contains(s)).ToList();
        if (others.Count == 0)
        {
            window.SetStatusBarMessage("There are no other terminals to join.", TimeSpan.FromSeconds(4));
            return;
        }
        var items = others.Select(s => new QuickPickItem($"{s.Id}: {s.Title}", Tag: s)).ToList();
        var picked = await window.ShowQuickPickManyAsync(items, new QuickPickOptions(Title: "Join Terminals", Placeholder: "Select the terminals to join with the active one")).ConfigureAwait(true);
        if (picked is null || picked.Count == 0) return;
        service.Join(target, picked.Select(p => p.Tag).OfType<TerminalSession>());
    }

    /// <summary>terminal.integrated.confirmOnKill: "editor" (default) never asks for panel terminals; "panel"/"always" ask when a program runs in it.</summary>
    private static async Task<bool> ConfirmKillAsync(TerminalPanelRuntime runtime, TerminalSession session)
    {
        var mode = runtime.Config?.Get<string>(TerminalSettingKeys.ConfirmOnKill) ?? "editor";
        if (mode is not ("panel" or "always") || runtime.Window is not { } window) return true;
        var busy = await Task.Run(session.HasForegroundChild).ConfigureAwait(true);
        if (!busy && mode != "always") return true;
        var answer = await window.ShowMessageAsync(MessageSeverity.Warning, $"Do you want to terminate the active terminal session '{session.Title}'?",
            [new MessageAction("Terminate"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(true);
        return answer == "Terminate";
    }

    /// <summary>The Explorer passes the folder's (or a file's) URI; a file means its folder.</summary>
    private static string? FolderOf(CommandInvocation inv)
    {
        var arg = inv.Args.Count > 0 ? inv.Args[0] : null;
        Uri? uri = arg switch
        {
            Uri u => u,
            string s when Uri.TryCreate(s, UriKind.Absolute, out var p) => p,
            JsonElement { ValueKind: JsonValueKind.String } je when Uri.TryCreate(je.GetString(), UriKind.Absolute, out var p) => p,
            _ => null,
        };
        return uri is { IsFile: true } ? uri.LocalPath : null; // a file's folder is taken by the launch resolver (off the UI thread)
    }

    private static string? SelectedOrCurrentLine(TerminalPanelRuntime runtime)
    {
        var selection = runtime.ServiceProvider.GetService<IActiveSelectionProvider>()?.SelectedText;
        if (!string.IsNullOrEmpty(selection)) return selection;
        // VS Code: no selection sends the line the caret is on
        if (runtime.Navigation is { ActiveDocument: { } doc, ActiveCaret: { } caret }) return doc.GetLineText(caret.Line);
        return null;
    }

    /// <summary>VS Code's sendSequence argument: <c>{ "text": "..." }</c> (or the text itself).</summary>
    private static string? SequenceText(CommandInvocation inv)
    {
        if (inv.Args.Count == 0) return null;
        return inv.Args[0] switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.Object } o when o.TryGetProperty("text", out var t) => t.GetString(),
            JsonElement { ValueKind: JsonValueKind.String } s => s.GetString(),
            IReadOnlyDictionary<string, object?> d when d.TryGetValue("text", out var t) => t as string,
            _ => null,
        };
    }

    private static string ShellQuote(string path) =>
        path.All(c => char.IsLetterOrDigit(c) || "/._-+@:,".Contains(c)) ? path : "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
