using System.Diagnostics;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
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
        Reg(KnownCommands.DebugRestart, "Restart", async _ => { if (ActiveSession is { } s) await s.RestartAsync(); return null; });
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
    }

    /// <summary>Starts the toolbar's selected launch configuration (first one if none selected); offers to create launch.json when there is none.</summary>
    public async ValueTask<IDebugSession?> StartSelectedAsync(bool noDebug, CancellationToken ct = default)
    {
        var configs = await _configurations.GetLaunchConfigurationsAsync(null, ct);
        var cfg = _configurations.Selected ?? configs.FirstOrDefault();
        if (cfg is null)
        {
            if (_p.Window is { } window && _p.PrimaryFolder is not null)
            {
                var choice = await window.ShowMessageAsync(MessageSeverity.Info, "There is no launch configuration. Create a launch.json?",
                    [new MessageAction("Create launch.json"), new MessageAction("Cancel", IsCloseAffordance: true)], ct);
                if (choice == "Create launch.json" && DebuggerTypes.FirstOrDefault() is { } type)
                    await _configurations.CreateLaunchJsonAsync(type.Type, null, ct);
            }
            return null;
        }
        return await StartAsync(noDebug ? cfg with { NoDebug = true } : cfg, ct);
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
