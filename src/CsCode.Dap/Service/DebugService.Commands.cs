using System.Diagnostics;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Dap.Configuration;
using CsCode.Dap.Session;

namespace CsCode.Dap.Service;

// Command table entries (VS Code ids from KnownCommands) so keybindings, menus, the palette, the toolbar and macros all drive the
// debugger through ICommandService. Handlers return immediately: they only enqueue onto the Debug lane.
public sealed partial class DebugService
{
    public const string PickProcessCommand = "pickProcess";

    private void RegisterCommands()
    {
        if (_p.Commands is not { } commands) return;

        void Reg(string id, string title, Func<CommandInvocation, ValueTask<object?>> handler) =>
            _subs.Add(commands.Register(id, handler, new CommandMetadata(id, title, "Debug")));

        Reg(KnownCommands.DebugStart, "Start Debugging", async _ => { await StartSelectedAsync(noDebug: false); return null; });
        Reg(KnownCommands.DebugRun, "Start Without Debugging", async _ => { await StartSelectedAsync(noDebug: true); return null; });
        Reg(KnownCommands.DebugStop, "Stop Debugging", async _ => { await StopAllAsync(); return null; });
        Reg(KnownCommands.DebugRestart, "Restart", async _ => { if (ActiveSession is { } s) await RootOf(s).RestartAsync(); return null; });
        Reg(KnownCommands.DebugContinue, "Continue", async _ => { if (ActiveSession is { State: DebugSessionState.Stopped } s) await s.ContinueAsync(); return null; });
        Reg(KnownCommands.DebugPause, "Pause", async _ => { if (ActiveSession is { State: DebugSessionState.Running } s) await s.PauseAsync(); return null; });
        Reg(KnownCommands.DebugStepOver, "Step Over", async _ => { if (ActiveSession is { State: DebugSessionState.Stopped } s) await s.StepOverAsync(); return null; });
        Reg(KnownCommands.DebugStepInto, "Step Into", async _ => { if (ActiveSession is { State: DebugSessionState.Stopped } s) await s.StepIntoAsync(); return null; });
        Reg(KnownCommands.DebugStepOut, "Step Out", async _ => { if (ActiveSession is { State: DebugSessionState.Stopped } s) await s.StepOutAsync(); return null; });
        Reg(KnownCommands.DebugToggleBreakpoint, "Toggle Breakpoint", _ =>
        {
            if (_p.Navigation is { ActiveDocument: { } doc, ActiveCaret: { } caret }) _breakpoints.Toggle(doc.Uri, caret.Line);
            return ValueTask.FromResult<object?>(null);
        });
        Reg(PickProcessCommand, "Pick Process to Attach To", async inv => await PickProcessAsync(inv.Cancellation));
        Reg(ConfigureCommand, "Open launch.json", async inv => { await _lane.RunAsync(() => OpenLaunchJsonCoreAsync(inv.Cancellation), inv.Cancellation); return null; });
        Reg(AddConfigurationCommand, "Add Configuration...", async inv => { await _lane.RunAsync(() => AddConfigurationCoreAsync(inv.Cancellation), inv.Cancellation); return null; });
    }

    /// <summary>VS Code's "Open launch.json" (Debug menu, Run view gear).</summary>
    public const string ConfigureCommand = "workbench.action.debug.configure";
    /// <summary>VS Code's "Add Configuration..." (Debug menu).</summary>
    public const string AddConfigurationCommand = "debug.addConfiguration";

    /// <summary>The folder the debug commands work on: the selected configuration's, else the first workspace folder.</summary>
    private Uri? CommandFolder => _configurations.Selected?.WorkspaceFolder ?? _p.PrimaryFolder;

    /// <summary>
    /// Lane. Opens the folder's launch.json; without one it is created first, for the folder's debugger (or the one the user picks), as VS Code
    /// does. Returns the file (null: no folder, or the user cancelled).
    /// </summary>
    private async Task<Uri?> OpenLaunchJsonCoreAsync(CancellationToken ct)
    {
        if (CommandFolder is not { } folder)
        {
            await ReportAsync("Open a folder to configure debugging: launch.json belongs to a workspace folder.");
            return null;
        }
        var uri = await _configurations.FindLaunchJsonAsync(folder, ct);
        if (uri is null)
        {
            if (_p.Window is not { } window || await PickDebugTypeAsync(window, folder, ct) is not { } type) return null;
            uri = await CreateLaunchJsonAsync(type, folder, ct);
            if (uri is null) return null;
        }
        if (_p.Navigation is { } navigation) await navigation.OpenAsync(uri, ct: ct);
        return uri;
    }

    private async Task<Uri?> CreateLaunchJsonAsync(string type, Uri folder, CancellationToken ct)
    {
        try { return await _configurations.CreateLaunchJsonAsync(type, folder, ct); }
        catch (InvalidOperationException ex)
        {
            await ReportAsync(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Lane. VS Code's "Add Configuration...": the user picks a debugger, then one of its configurations (what a new launch.json for it holds),
    /// which is appended to the <c>configurations</c> of the folder's launch.json — in the editor, as an edit the user can undo and must save,
    /// with the rest of the file as written — and revealed. The task its preLaunchTask names comes with it when the folder lacks it
    /// (<see cref="AddCompanionTasksAsync"/>). Without a launch.json this is "Open launch.json" (which creates one).
    /// </summary>
    private async Task AddConfigurationCoreAsync(CancellationToken ct)
    {
        if (CommandFolder is not { } folder || _p.Window is not { } window || await _configurations.FindLaunchJsonAsync(folder, ct) is not { } uri)
        {
            await OpenLaunchJsonCoreAsync(ct);
            return;
        }
        var types = DebuggerTypes;
        if (types.Count == 0) { await ReportAsync("No debugger is available. Configure one in the 'cscode.debuggers' setting."); return; }
        var debugger = await window.ShowQuickPickAsync([.. types.Select(t => new QuickPickItem(t.Label, t.Type, Tag: t.Type))],
            new QuickPickOptions(Title: "Add Configuration", Placeholder: "Select debugger"), ct);
        if (debugger?.Tag is not string type) return;
        var offered = await _configurations.GetConfigurationTemplatesAsync(type, folder, ct);
        var templates = offered.Configurations;
        if (templates.Count == 0) { await ReportAsync($"'{debugger.Label}' offers no configurations to add."); return; }
        var chosen = templates[0];
        if (templates.Count > 1)
        {
            var picked = await window.ShowQuickPickAsync([.. templates.Select(c => new QuickPickItem(c.Name, c.Request, Tag: c))],
                new QuickPickOptions(Title: "Add Configuration", Placeholder: "Select the configuration to add"), ct);
            if (picked?.Tag is not DebugConfiguration c) return;
            chosen = c;
        }

        var navigation = _p.Navigation;
        var document = navigation is null ? null : await navigation.OpenAsync(uri, ct: ct);
        string text;
        if (document is not null) text = document.GetText(); // what the editor holds, unsaved edits included
        else if (_p.Workspace is { } ws) text = System.Text.Encoding.UTF8.GetString(await ws.FileSystem.ReadFileAsync(uri, ct));
        else return;
        if (DebugConfigurationService.AddConfiguration(text, chosen) is not { } edit)
        {
            await ReportAsync($"{uri.LocalPath}: the configuration cannot be added: launch.json has no 'configurations' list that can be edited.");
            return;
        }
        var updated = edit.ApplyTo(text);
        var at = PositionOf(updated, edit.ElementOffset);
        if (document is not null && navigation is not null)
        {
            var range = new TextRange(document.PositionAt(edit.Position), document.PositionAt(edit.Position + edit.Length));
            var changes = new Dictionary<Uri, IReadOnlyList<TextEdit>> { [uri] = [new TextEdit(range, edit.Text)] };
            if (!await navigation.ApplyWorkspaceEditAsync(new WorkspaceEdit(changes), "Add Configuration", ct)) return;
        }
        else
        {
            await _p.Workspace!.FileSystem.WriteFileAsync(uri, System.Text.Encoding.UTF8.GetBytes(updated), createParents: false, ct);
            _configurations.Invalidate(folder);
        }
        if (navigation is not null) await navigation.OpenAsync(uri, new TextRange(at, at), ct: ct);
        await AddCompanionTasksAsync(folder, uri, chosen, offered.TasksJson, navigation, ct);
    }

    /// <summary>
    /// Lane. The tasks the added configuration runs (its preLaunchTask, postDebugTask) that its debugger's template defines — what Create
    /// launch.json writes as the companion tasks.json — and the folder's tasks.json lacks: CsCode detects no tasks (in VS Code 'tsc: build' or a
    /// cargo build task exist without a tasks.json), so without them the new configuration would name a task that exists nowhere and fail at F5.
    /// They are appended to the tasks.json in use, keeping its text as written — in the editor when it is open there (unsaved, like the
    /// configuration), else on disk — with no second default build task; without a tasks.json the template's is written next to the launch.json.
    /// </summary>
    private async Task AddCompanionTasksAsync(Uri folder, Uri launchJson, DebugConfiguration added, string? tasksJson, INavigationService? navigation, CancellationToken ct)
    {
        var needed = DebugConfigurationService.TasksNamedBy(added, tasksJson);
        if (needed.Count == 0 || _p.Workspace is not { } ws) return;
        if (await _configurations.FindTasksJsonAsync(folder, ct) is not { } tasksUri)
        {
            await ws.FileSystem.WriteFileAsync(new Uri(launchJson, "tasks.json"), System.Text.Encoding.UTF8.GetBytes(tasksJson!), createParents: true, ct);
            return;
        }
        var open = navigation?.OpenDocuments.FirstOrDefault(d => d.Uri == tasksUri);
        var text = open is not null ? open.GetText() : System.Text.Encoding.UTF8.GetString(await ws.FileSystem.ReadFileAsync(tasksUri, ct));
        if (DebugConfigurationService.AddTasks(text, needed, out var problem) is not { } edit)
        {
            if (problem is not null)
                await ReportAsync($"{tasksUri.LocalPath}: the task '{needed[0]["label"]}' that '{added.Name}' runs could not be added: {problem}.");
            return;
        }
        if (open is not null && navigation is not null)
        {
            var range = new TextRange(open.PositionAt(edit.Position), open.PositionAt(edit.Position + edit.Length));
            var changes = new Dictionary<Uri, IReadOnlyList<TextEdit>> { [tasksUri] = [new TextEdit(range, edit.Text)] };
            await navigation.ApplyWorkspaceEditAsync(new WorkspaceEdit(changes), "Add Configuration", ct);
        }
        else await ws.FileSystem.WriteFileAsync(tasksUri, System.Text.Encoding.UTF8.GetBytes(edit.ApplyTo(text)), createParents: false, ct);
    }

    /// <summary>Line and character of <paramref name="offset"/> in <paramref name="text"/> ('\n', '\r\n' and '\r' end lines).</summary>
    private static TextPosition PositionOf(string text, int offset)
    {
        int line = 0, lineStart = 0;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))) { line++; lineStart = i + 1; }
        }
        return new TextPosition(line, offset - lineStart);
    }

    /// <summary>
    /// The session a user-level Restart applies to: the top of the active session's tree. A child session (js-debug's program, started by the
    /// adapter through <c>startDebugging</c>) cannot be relaunched on its own — js-debug v1.140.0 closes a child's connection on <c>restart</c>
    /// — while restarting the parent relaunches the program, which comes back as a new child.
    /// </summary>
    internal static IDebugSession RootOf(IDebugSession session)
    {
        var root = session;
        for (var depth = 0; root.Parent is { State: not DebugSessionState.Terminated } parent && depth < 16; depth++) root = parent;
        return root;
    }

    /// <summary>
    /// Starts the toolbar's selected launch configuration (first one if none selected). Without any: a launch.json that exists (but holds no
    /// configuration, or does not parse — its error was shown) is opened; otherwise creating one is offered, and the new file is opened, as
    /// VS Code does.
    /// </summary>
    public async ValueTask<IDebugSession?> StartSelectedAsync(bool noDebug, CancellationToken ct = default)
    {
        var configs = await _configurations.GetLaunchConfigurationsAsync(null, ct);
        var cfg = _configurations.Selected ?? configs.FirstOrDefault();
        if (cfg is null)
        {
            if (_p.Window is { } window && _p.PrimaryFolder is { } folder)
            {
                if (await _configurations.FindLaunchJsonAsync(folder, ct) is { } existing)
                {
                    if (_p.Navigation is { } navigation) await navigation.OpenAsync(existing, ct: ct);
                    return null;
                }
                var choice = await window.ShowMessageAsync(MessageSeverity.Info, "There is no launch configuration. Create a launch.json?",
                    [new MessageAction("Create launch.json"), new MessageAction("Cancel", IsCloseAffordance: true)], ct);
                if (choice == "Create launch.json" && await PickDebugTypeAsync(window, folder, ct) is { } type
                    && await CreateLaunchJsonAsync(type, folder, ct) is { } created && _p.Navigation is { } nav)
                    await nav.OpenAsync(created, ct: ct);
            }
            return null;
        }
        return await StartAsync(noDebug ? cfg with { NoDebug = true } : cfg, ct);
    }

    /// <summary>
    /// Setting: the debugger a Rust folder's launch.json is created for — <c>"gdb"</c> or <c>"lldb"</c> (CodeLLDB); the default is
    /// <see cref="DefaultRustDebugger"/>. See docs/DEBUGGING-RUST-TS.md, "Which Rust debugger is the default".
    /// </summary>
    public const string RustDebuggerSetting = "cscode.debug.rustDebugger";

    /// <summary>
    /// <see cref="RustDebuggerSetting"/>'s default: <c>"lldb"</c> (CodeLLDB) on macOS, where GDB cannot debug native arm64 programs, and
    /// <c>"gdb"</c> elsewhere (evaluated, the system's own debugger). Decided 2026-10-30 (docs/handoff/decisions.md).
    /// </summary>
    internal static string DefaultRustDebugger(bool macOS) => macOS ? "lldb" : "gdb";

    /// <summary>The folder's default debugger (<see cref="DefaultDebugTypeAsync"/>), else the user picks one ("Select debugger", as VS Code asks).</summary>
    private async Task<string?> PickDebugTypeAsync(IWindowService window, Uri? folder, CancellationToken ct)
    {
        if (await DefaultDebugTypeAsync(folder, ct) is { } known) return known;
        var types = DebuggerTypes;
        if (types.Count == 0) return null;
        var picked = await window.ShowQuickPickAsync([.. types.Select(t => new QuickPickItem(t.Label, t.Type, Tag: t.Type))],
            new QuickPickOptions(Title: "Select debugger", Placeholder: "The debugger the new launch.json is for"), ct);
        return picked?.Tag as string;
    }

    /// <summary>
    /// The debug type "Create launch.json" uses for <paramref name="folder"/>, from the files at its root: <c>Cargo.toml</c> → the Rust debugger
    /// (<see cref="RustDebuggerSetting"/>), a .NET project or solution → <c>coreclr</c>, <c>tsconfig.json</c>/<c>package.json</c> → <c>pwa-node</c>;
    /// otherwise the only debugger there is, else null. (The first debugger in label order — what was used before — made a .NET launch.json
    /// for a cargo package.)
    /// </summary>
    internal async Task<string?> DefaultDebugTypeAsync(Uri? folder, CancellationToken ct)
    {
        var types = DebuggerTypes;
        string? Known(string type) => types.FirstOrDefault(t => string.Equals(t.Type, type, StringComparison.OrdinalIgnoreCase))?.Type;
        if (folder is { IsFile: true } && _p.Workspace is { } ws)
        {
            HashSet<string> files;
            try
            {
                files = (await ws.FileSystem.ReadDirectoryAsync(folder, ct)).Where(e => e.Type == FileType.File).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { files = []; }
            if (files.Contains("Cargo.toml")) return Known(RustDebugger()) ?? Known(Adapters.GdbAdapterFactory.PrimaryType);
            if (files.Any(n => n.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
                return Known("coreclr");
            if (files.Contains("tsconfig.json") || files.Contains("package.json")) return Known(Adapters.JsDebugAdapterFactory.PrimaryType);
        }
        return types.Count == 1 ? types[0].Type : null;
    }

    /// <summary>The Rust debugger's debug type from <see cref="RustDebuggerSetting"/>, else this OS's default.</summary>
    private string RustDebugger() => RustDebugType(_p.Configuration?.Get<string>(RustDebuggerSetting), OperatingSystem.IsMacOS());

    /// <summary>The debug type <paramref name="setting"/> names (<c>codelldb</c> names CodeLLDB's <c>lldb</c> too, <c>lldb-dap</c> LLVM's adapter); unset or empty: the OS's
    /// default (<see cref="DefaultRustDebugger"/>).</summary>
    internal static string RustDebugType(string? setting, bool macOS)
    {
        var chosen = setting?.Trim() is { Length: > 0 } s ? s : DefaultRustDebugger(macOS);
        if (string.Equals(chosen, Adapters.LldbDapAdapterFactory.PrimaryType, StringComparison.OrdinalIgnoreCase)) return Adapters.LldbDapAdapterFactory.PrimaryType;
        return string.Equals(chosen, "lldb", StringComparison.OrdinalIgnoreCase) || string.Equals(chosen, "codelldb", StringComparison.OrdinalIgnoreCase)
            ? Adapters.CodeLldbAdapterFactory.PrimaryType
            : Adapters.GdbAdapterFactory.PrimaryType;
    }

    /// <summary><c>${command:pickProcess}</c>: lists processes in a quick pick; returns the chosen PID as text (null = cancelled).</summary>
    private async ValueTask<object?> PickProcessAsync(CancellationToken ct)
    {
        var window = _p.Window ?? throw new InvalidOperationException("Picking a process needs a window.");
        var items = await Task.Run(ListProcesses, ct); // enumerating /proc or the process table is blocking I/O: never on the caller's thread
        var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Title: "Select the process to attach to", Placeholder: "Type a process name or id", MatchOnDescription: true, MatchOnDetail: true), ct);
        return picked?.Tag is int pid ? pid.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    internal static IReadOnlyList<QuickPickItem> ListProcesses()
    {
        var self = Environment.ProcessId;
        var list = new List<(string Name, int Id)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { if (p.Id != self) list.Add((p.ProcessName, p.Id)); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return [.. list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).Take(3_000)
            .Select(p => new QuickPickItem(p.Name, p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), CommandLine(p.Id), Tag: p.Id))];
    }

    private static string? CommandLine(int pid)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try { return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim() is { Length: > 0 } s ? (s.Length > 200 ? s[..200] : s) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
