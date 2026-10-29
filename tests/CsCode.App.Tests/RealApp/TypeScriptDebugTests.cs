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
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// TypeScript debugging in the REAL application (<see cref="RealAppHost"/>: every real module, <c>CSCODE_HOME</c> in a temp directory, headless
/// window): a folder with a launch.json and a tasks.json as a user would have them, a breakpoint set with F9 in the .ts file, F5 — the
/// preLaunchTask compiles with the real tsc, the real js-debug launches the program — and the Call Stack shows the TypeScript frame while the
/// editor marks the stop line. Skips with the reason when node, tsc or js-debug is missing (tools/fetch-js-debug.sh, CSCODE_JSDEBUG).
/// </summary>
public class RealAppTypeScriptDebugTests
{
    private const string LaunchJson = """
        {
          "version": "0.2.0",
          "configurations": [
            {
              "name": "TypeScript: Launch (tsc build)",
              "type": "pwa-node",
              "request": "launch",
              "program": "${workspaceFolder}/src/main.ts",
              "preLaunchTask": "tsc: build",
              "outFiles": ["${workspaceFolder}/out/**/*.js"],
              "sourceMaps": true,
              "skipFiles": ["<node_internals>/**"],
              "console": "internalConsole"
            }
          ]
        }
        """;

    private const string TasksJson = """
        {
          "version": "2.0.0",
          "tasks": [
            { "label": "tsc: build", "type": "shell", "command": "tsc", "args": ["-p", "${workspaceFolder}/tsconfig.json"], "group": { "kind": "build", "isDefault": true } }
          ]
        }
        """;

    /// <summary>The editor's stop line of a view (internal to the editor module, which has no public getter; read for the assertion only).</summary>
    private static int? StopLineOf(CsCode.Editor.IEditorViewController? controller) =>
        (int?)controller?.GetType().GetProperty("StopLine", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(controller);

    /// <summary>A folder with the TypeScript sample, launch.json and tasks.json, and the host's settings pointing at js-debug.</summary>
    private static RealAppHost CreateHost() => RealAppHost.Create(h =>
    {
        h.Files.WriteFile("src/main.ts", JsDebugSample.MainSource);
        h.Files.WriteFile("src/util.ts", JsDebugSample.UtilSource);
        h.Files.WriteFile("tsconfig.json", JsDebugSample.TsConfig);
        h.Files.WriteFile(".vscode/launch.json", LaunchJson);
        h.Files.WriteFile(".vscode/tasks.json", TasksJson);
        // The host's settings (language servers off) plus where js-debug is: never the download offer in a test.
        var settingsPath = h.Home.Combine("config", "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings[JsDebugAdapterFactory.SettingPath] = JsDebugToolchain.FindJsDebug();
        File.WriteAllText(settingsPath, settings.ToJsonString());
    });

    // No try/finally StopAllAsync any more: a failing assertion disposes the host, and a disposed host leaves nothing running
    // (Disposing_the_host_while_stopped_at_a_breakpoint_leaves_no_js_debug_server_or_program_running).
    [AvaloniaFact(Timeout = 180_000)]
    public async Task F5_builds_with_tsc_and_stops_at_a_ts_breakpoint_with_the_call_stack_and_the_stop_line_shown()
    {
        Assert.SkipWhen(JsDebugToolchain.SkipReason() is not null, JsDebugToolchain.SkipReason() ?? "");
        await using var host = CreateHost();
        await host.StartAsync([host.Files.Path]);
        await DebugAsync(host, host.Get<IDebugService>());
    }

    /// <summary>
    /// Integrator finding: two js-debug servers outlived failed runs of the test above. The host disposes the container synchronously (no
    /// AppShutdown), and the debug service's Dispose only STARTED its asynchronous shutdown, which runs on the debug lane — disposed right after
    /// it in the same container dispose, or never reached when the process exits first. js-debug's server never exits by itself. A disposed host
    /// (any synchronous container dispose) must leave no server and no program running, at once.
    /// </summary>
    [AvaloniaFact(Timeout = 180_000)]
    public async Task Disposing_the_host_while_stopped_at_a_breakpoint_leaves_no_js_debug_server_or_program_running()
    {
        Assert.SkipWhen(JsDebugToolchain.SkipReason() is not null, JsDebugToolchain.SkipReason() ?? "");
        var host = CreateHost();
        var serverPid = 0;
        string? marker = null;
        try
        {
            await host.StartAsync([host.Files.Path]);
            var debug = host.Get<IDebugService>();
            var main = new Uri(host.Files.Combine("src", "main.ts"));
            debug.Breakpoints.Add(main, JsDebugSample.MainLine("loop"));
            Assert.True(await RealAppHost.WaitFor(() => debug.Configurations.Selected?.Name == "TypeScript: Launch (tsc build)", 10_000), "launch.json read");
            var start = host.Get<ICommandService>().ExecuteAsync(KnownCommands.DebugStart).AsTask();
            Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation is not null, 90_000), $"stopped (state {debug.State})");
            await start;
            var parent = (DebugSession)debug.Sessions.Single(s => s.Parent is null);
            serverPid = parent.AdapterProcessId!.Value;
            marker = parent.AdapterProcessMarker!;
            Assert.True(ProcessTable.IsAlive(serverPid));
            Assert.Contains(ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker), p => p != serverPid); // the program runs

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await host.DisposeAsync(); // still stopped at the breakpoint: no Shift+F5, no AppShutdown
            var gone = await RealAppHost.WaitFor(() => !ProcessTable.IsAlive(serverPid) && ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker).Count == 0, 3_000);
            TestNote($"host disposed while stopped: js-debug server and program gone={gone} after {sw.ElapsedMilliseconds} ms; left: " +
                string.Join("; ", ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker).Select(p => $"{p} {ProcessTable.CommandLine(p)}")));
            Assert.True(gone, "a disposed host leaves no js-debug server or program running");
        }
        finally
        {
            await host.DisposeAsync();
            KillLeftovers(serverPid, marker); // only when the assertion above failed: this test's own server and what it started
        }
    }

    private static void TestNote(string text)
    {
        if (Environment.GetEnvironmentVariable("CSCODE_DAP_TEST_METRICS") is { Length: > 0 } path) File.AppendAllText(path, "[app] " + text + Environment.NewLine);
    }

    private static void KillLeftovers(int serverPid, string? marker)
    {
        if (serverPid > 0)
            try { using var p = System.Diagnostics.Process.GetProcessById(serverPid); p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        if (marker is null) return;
        foreach (var pid in ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker)) // the marker of this test's own server
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.Kill(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static async Task DebugAsync(RealAppHost host, IDebugService debug)
    {
        var commands = host.Get<ICommandService>();
        var main = new Uri(host.Files.Combine("src", "main.ts"));
        var line = JsDebugSample.MainLine("loop");

        // The user opens main.ts at the loop and presses F9.
        await host.Get<INavigationService>().OpenAsync(main, TextRange.At(line, 4));
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.Any(t => t.Title == "main.ts" && t.Controller is not null)), "main.ts opened");
        await host.RunAsync(KnownCommands.DebugToggleBreakpoint);
        Assert.Equal(line, Assert.Single(debug.Breakpoints.GetForUri(main)).Line);
        Assert.True(await RealAppHost.WaitFor(() => debug.Configurations.Selected?.Name == "TypeScript: Launch (tsc build)", 10_000), "launch.json read");

        // F5: tsc (preLaunchTask), js-debug, the program as a child session, the breakpoint in the .ts source.
        var start = commands.ExecuteAsync(KnownCommands.DebugStart).AsTask();
        Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation is not null, 90_000),
            $"stopped at the breakpoint (state {debug.State}, sessions: {string.Join(", ", debug.Sessions.Select(s => $"{s.Name}/{s.State}"))})");
        await start;
        Assert.True(File.Exists(host.Files.Combine("out", "main.js.map")), "the preLaunchTask compiled the program");
        var location = debug.StopLocation!;
        Assert.Equal(main, location.Uri);
        Assert.Equal(line, location.Line);
        var parent = (DebugSession)debug.Sessions.Single(s => s.Parent is null);
        var serverPid = parent.AdapterProcessId!.Value;
        var marker = parent.AdapterProcessMarker!;
        Assert.Same(parent, location.Session.Parent); // stopped in js-debug's child session

        // The Call Stack window (auto-shown while debugging) shows the TypeScript frame with the arrow.
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Tools.Get("debug.callstack")?.LiveView?.DataContext is CallStackViewModel vm
            && vm.Rows.Any(r => r.IsCurrent && r.Text == "main" && r.Location == $"src/main.ts : {line + 1}"), 30_000), "the Call Stack shows main() at the .ts line");
        var callStack = (CallStackViewModel)host.Shell.Tools.Get("debug.callstack")!.LiveView!.DataContext!;
        Assert.Contains(callStack.Rows, r => r.Text == "<anonymous>" && r.Location.StartsWith("src/main.ts", StringComparison.Ordinal));

        // The editor marks the stop line in main.ts.
        var tab = host.Shell.Well.EditorTabs.Single(t => t.Title == "main.ts");
        Assert.True(await RealAppHost.WaitFor(() => StopLineOf(tab.Controller) == line, 10_000), "the editor's stop line");

        // A stop in a file that is not open (util.ts) opens it at the stop line, as Visual Studio and VS Code do.
        var util = new Uri(host.Files.Combine("src", "util.ts"));
        Assert.DoesNotContain(host.Shell.Well.EditorTabs, t => t.Title == "util.ts");
        debug.Breakpoints.Add(util, JsDebugSample.UtilLine("add"));
        debug.Breakpoints.Remove(debug.Breakpoints.GetForUri(main).Single().Id);
        await Task.Delay(300); // the breakpoint changes reach the adapter (debounced)
        await commands.ExecuteAsync(KnownCommands.DebugContinue);
        Assert.True(await RealAppHost.WaitFor(() => debug.StopLocation?.Uri == util, 30_000), "stopped in util.ts");
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.Any(t => t.Title == "util.ts" && StopLineOf(t.Controller) == JsDebugSample.UtilLine("add")), 10_000),
            "util.ts opened with the stop line; tabs: " + string.Join(", ", host.Shell.Well.EditorTabs.Select(t => t.Title)));
        Assert.Null(StopLineOf(tab.Controller)); // main.ts no longer has the stop line

        // Shift+F5: everything ends, nothing is left running.
        await host.RunAsync(KnownCommands.DebugStop);
        Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Inactive && debug.Sessions.Count == 0, 20_000), "debugging stopped");
        Assert.True(await RealAppHost.WaitFor(() => !ProcessTable.IsAlive(serverPid) && ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker).Count == 0, 10_000), "no js-debug server or debuggee left");
        Assert.True(await RealAppHost.WaitFor(() => StopLineOf(tab.Controller) is null, 10_000), "the stop line cleared");
    }
}
