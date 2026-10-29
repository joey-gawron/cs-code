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

    [AvaloniaFact(Timeout = 180_000)]
    public async Task F5_builds_with_tsc_and_stops_at_a_ts_breakpoint_with_the_call_stack_and_the_stop_line_shown()
    {
        Assert.SkipWhen(JsDebugToolchain.SkipReason() is not null, JsDebugToolchain.SkipReason() ?? "");
        var jsDebug = JsDebugToolchain.FindJsDebug()!;
        await using var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("src/main.ts", JsDebugSample.MainSource);
            h.Files.WriteFile("src/util.ts", JsDebugSample.UtilSource);
            h.Files.WriteFile("tsconfig.json", JsDebugSample.TsConfig);
            h.Files.WriteFile(".vscode/launch.json", LaunchJson);
            h.Files.WriteFile(".vscode/tasks.json", TasksJson);
            // The host's settings (language servers off) plus where js-debug is: never the download offer in a test.
            var settingsPath = h.Home.Combine("config", "settings.json");
            var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
            settings[JsDebugAdapterFactory.SettingPath] = jsDebug;
            File.WriteAllText(settingsPath, settings.ToJsonString());
        });
        await host.StartAsync([host.Files.Path]);
        var debug = host.Get<IDebugService>();
        try
        {
            await DebugAsync(host, debug);
        }
        finally
        {
            // The host disposes the container synchronously (no AppShutdown), which only STARTS the debug service's asynchronous shutdown:
            // a failing assertion must not leave js-debug's server (it never exits by itself) running after the test process ends.
            await debug.StopAllAsync();
        }
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
