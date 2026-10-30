using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Dap.Adapters;
using CsCode.Dap.Session;
using CsCode.Panels.Debug.CallStack;
using CsCode.Panels.Debug.Exceptions;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Rust debugging in the REAL application (<see cref="RealAppHost"/>: every real module, <c>CSCODE_HOME</c> in a temp directory, headless
/// window): a cargo package with a launch.json and a tasks.json as a user would have them, a breakpoint set with F9 in main.rs, F5 — the
/// <c>cargo: build</c> preLaunchTask builds with the real cargo, GDB's native DAP launches the program — the Call Stack shows the Rust frame
/// while the editor marks the stop line, a stop in another file opens it, a panic stops as an exception, and Shift+F5 leaves nothing running.
/// GDB runs with <c>-nx</c> (<c>cscode.debug.gdbArgs</c>): never the user's gdbinit. Skips with the reason when gdb (14+), cargo or rustc is missing.
/// </summary>
public class RealAppRustDebugTests
{
    private const string LaunchJson = """
        {
          "version": "0.2.0",
          "configurations": [
            {
              "name": "Rust: Debug sample (cargo build)",
              "type": "gdb",
              "request": "launch",
              "program": "${workspaceFolder}/target/debug/sample",
              "args": [],
              "cwd": "${workspaceFolder}",
              "preLaunchTask": "cargo: build"
            }
          ]
        }
        """;

    // As the template writes it, plus --offline: a test never touches the network.
    private const string TasksJson = """
        {
          "version": "2.0.0",
          "tasks": [
            { "label": "cargo: build", "type": "process", "command": "cargo", "args": ["build", "--offline"], "options": { "cwd": "${workspaceFolder}" }, "group": { "kind": "build", "isDefault": true } }
          ]
        }
        """;

    /// <summary>The editor's stop line of a view (internal to the editor module, which has no public getter; read for the assertion only).</summary>
    private static int? StopLineOf(CsCode.Editor.IEditorViewController? controller) =>
        (int?)controller?.GetType().GetProperty("StopLine", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(controller);

    /// <summary>A cargo package with the Rust sample, launch.json and tasks.json, and the host's settings keeping GDB away from the user's gdbinit.</summary>
    private static RealAppHost CreateHost() => RealAppHost.Create(h =>
    {
        h.Files.WriteFile("Cargo.toml", RustSample.CargoToml);
        h.Files.WriteFile("src/main.rs", RustSample.MainSource);
        h.Files.WriteFile("src/util.rs", RustSample.UtilSource);
        h.Files.WriteFile(".vscode/launch.json", LaunchJson);
        h.Files.WriteFile(".vscode/tasks.json", TasksJson);
        var settingsPath = h.Home.Combine("config", "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings[GdbAdapterFactory.ArgsSettingPath] = new JsonArray("-nx");
        File.WriteAllText(settingsPath, settings.ToJsonString());
    });

    private static void TestNote(string text)
    {
        if (Environment.GetEnvironmentVariable("CSCODE_DAP_TEST_METRICS") is { Length: > 0 } path) File.AppendAllText(path, "[app/rust] " + text + Environment.NewLine);
    }

    [AvaloniaFact(Timeout = 240_000)]
    public async Task F5_builds_with_cargo_and_stops_at_a_rust_breakpoint_then_on_a_panic_with_nothing_left_running_after_shift_f5()
    {
        Assert.SkipWhen(GdbToolchain.SkipReason() is not null, GdbToolchain.SkipReason() ?? "");
        await using var host = CreateHost();
        await host.StartAsync([host.Files.Path]);
        var debug = host.Get<IDebugService>();
        var commands = host.Get<ICommandService>();
        var main = new Uri(host.Files.Combine("src", "main.rs"));
        var line = RustSample.MainLine("loop");

        // The user opens main.rs at the loop and presses F9.
        await host.Get<INavigationService>().OpenAsync(main, TextRange.At(line, 8));
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.Any(t => t.Title == "main.rs" && t.Controller is not null)), "main.rs opened");
        await host.RunAsync(KnownCommands.DebugToggleBreakpoint);
        Assert.Equal(line, Assert.Single(debug.Breakpoints.GetForUri(main)).Line);
        Assert.True(await RealAppHost.WaitFor(() => debug.Configurations.Selected?.Name == "Rust: Debug sample (cargo build)", 10_000), "launch.json read");

        // F5: cargo build (preLaunchTask), GDB, the breakpoint in main.rs.
        var start = commands.ExecuteAsync(KnownCommands.DebugStart).AsTask();
        Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation is not null, 120_000),
            $"stopped at the breakpoint (state {debug.State}, sessions: {string.Join(", ", debug.Sessions.Select(s => $"{s.Name}/{s.State}"))})");
        await start;
        Assert.True(File.Exists(host.Files.Combine("target", "debug", "sample")), "the preLaunchTask built the program");
        var location = debug.StopLocation!;
        Assert.Equal(main, location.Uri);
        Assert.Equal(line, location.Line);
        var session = (DebugSession)debug.Sessions.Single();
        var gdbPid = session.AdapterProcessId!.Value;
        var marker = session.AdapterProcessMarker!;
        TestNote($"F5: stopped at main.rs:{line + 1} under gdb pid {gdbPid} ({ProcessTable.CommandLine(gdbPid)})");
        Assert.Contains(ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker), p => p != gdbPid); // the program runs under GDB

        // The Call Stack window (auto-shown while debugging) shows the Rust frame with the arrow; the editor marks the stop line.
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Tools.Get("debug.callstack")?.LiveView?.DataContext is CallStackViewModel vm
            && vm.Rows.Any(r => r.IsCurrent && r.Text == "sample::main" && r.Location == $"main.rs : {line + 1}"), 30_000), "the Call Stack shows sample::main at the line");
        var tab = host.Shell.Well.EditorTabs.Single(t => t.Title == "main.rs");
        Assert.True(await RealAppHost.WaitFor(() => StopLineOf(tab.Controller) == line, 10_000), "the editor's stop line");

        // A stop in a file that is not open (util.rs) opens it at the stop line.
        var util = new Uri(host.Files.Combine("src", "util.rs"));
        Assert.DoesNotContain(host.Shell.Well.EditorTabs, t => t.Title == "util.rs");
        debug.Breakpoints.Add(util, RustSample.UtilLine("add"));
        debug.Breakpoints.Remove(debug.Breakpoints.GetForUri(main).Single().Id);
        await Task.Delay(300); // the breakpoint changes reach the adapter (debounced)
        await commands.ExecuteAsync(KnownCommands.DebugContinue);
        Assert.True(await RealAppHost.WaitFor(() => debug.StopLocation?.Uri == util, 30_000), "stopped in util.rs");
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.Any(t => t.Title == "util.rs" && StopLineOf(t.Controller) == RustSample.UtilLine("add")), 10_000),
            "util.rs opened with the stop line; tabs: " + string.Join(", ", host.Shell.Well.EditorTabs.Select(t => t.Title)));
        Assert.Null(StopLineOf(tab.Controller));

        // The sample then panics (and catches it): "Rust panics" is on by default, so it stops at the panic, in the user's frame.
        debug.Breakpoints.RemoveAll();
        await Task.Delay(300);
        await commands.ExecuteAsync(KnownCommands.DebugContinue);
        Assert.True(await RealAppHost.WaitFor(() => debug.StopLocation is { Reason: "exception" } l && l.Uri == util && l.Line == RustSample.UtilLine("panic"), 30_000),
            $"stopped at the panic (stop: {debug.StopLocation?.Reason} {debug.StopLocation?.Uri} {debug.StopLocation?.Line})");
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Tools.Get("debug.exception")?.LiveView?.DataContext is ExceptionHelperViewModel { HasException: true }, 20_000),
            "the Exception window shows the panic");
        var exception = (ExceptionHelperViewModel)host.Shell.Tools.Get("debug.exception")!.LiveView!.DataContext!;
        TestNote($"panic in the real app: {exception.Title}: {exception.Details!.TypeName}: {exception.Details.Message} ({exception.Details.StackTrace})");
        Assert.Equal(("panic", "boom at the bottom"), (exception.Details.TypeName, exception.Details.Message));

        // Shift+F5: everything ends, nothing is left running.
        await host.RunAsync(KnownCommands.DebugStop);
        Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Inactive && debug.Sessions.Count == 0, 20_000), "debugging stopped");
        Assert.True(await RealAppHost.WaitFor(() => !ProcessTable.IsAlive(gdbPid) && ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker).Count == 0, 10_000), "no gdb or program left");
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.All(t => StopLineOf(t.Controller) is null), 10_000), "the stop line cleared");
    }

    /// <summary>A host disposed synchronously while the program is stopped (no Shift+F5, no AppShutdown) leaves no GDB and no program running.</summary>
    [AvaloniaFact(Timeout = 180_000)]
    public async Task Disposing_the_host_while_stopped_at_a_breakpoint_leaves_no_gdb_or_program_running()
    {
        Assert.SkipWhen(GdbToolchain.SkipReason() is not null, GdbToolchain.SkipReason() ?? "");
        var host = CreateHost();
        var gdbPid = 0;
        string? marker = null;
        try
        {
            await host.StartAsync([host.Files.Path]);
            var debug = host.Get<IDebugService>();
            debug.Breakpoints.Add(new Uri(host.Files.Combine("src", "main.rs")), RustSample.MainLine("loop"));
            Assert.True(await RealAppHost.WaitFor(() => debug.Configurations.Selected?.Name == "Rust: Debug sample (cargo build)", 10_000), "launch.json read");
            var start = host.Get<ICommandService>().ExecuteAsync(KnownCommands.DebugStart).AsTask();
            Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation is not null, 120_000), $"stopped (state {debug.State})");
            await start;
            var session = (DebugSession)debug.Sessions.Single();
            gdbPid = session.AdapterProcessId!.Value;
            marker = session.AdapterProcessMarker!;
            Assert.Contains(ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker), p => p != gdbPid);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await host.DisposeAsync(); // still stopped at the breakpoint
            var gone = await RealAppHost.WaitFor(() => !ProcessTable.IsAlive(gdbPid) && ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker).Count == 0, 3_000);
            TestNote($"host disposed while stopped: gdb and program gone={gone} after {sw.ElapsedMilliseconds} ms");
            Assert.True(gone, "a disposed host leaves no gdb or program running");
        }
        finally
        {
            await host.DisposeAsync();
            if (gdbPid > 0)
                try { using var p = System.Diagnostics.Process.GetProcessById(gdbPid); p.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (marker is not null)
                foreach (var pid in ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker))
                    try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.Kill(); }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
}
