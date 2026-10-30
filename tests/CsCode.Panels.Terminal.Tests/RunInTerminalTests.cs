using System.Text.Json;
using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Panels.Terminal.Tests.Infrastructure;
using CsCode.Terminal.Sessions;
using Xunit;

namespace CsCode.Panels.Terminal.Tests;

/// <summary>
/// DAP <c>runInTerminal</c> through the real composition with a real adapter: js-debug with <c>console: "integratedTerminal"</c> asks CsCode to
/// run node, which runs in a Terminal tab named after the launch configuration, without a shell in between (the debuggee's pid is the
/// terminal's process), reused for the next run, and never outliving its session.
/// </summary>
public sealed class RunInTerminalTests
{
    private static string? JsDebug()
    {
        foreach (var candidate in new[] { Environment.GetEnvironmentVariable("CSCODE_JSDEBUG"), "/home/joeyg/repos/cs-code/.scratch/tools/js-debug/v1.140.0/js-debug" })
            if (!string.IsNullOrEmpty(candidate) && (Directory.Exists(candidate) || File.Exists(candidate))) return candidate;
        return null;
    }

    [AvaloniaFact]
    public async Task Js_debug_integrated_terminal_launch_runs_the_program_in_a_reused_terminal()
    {
        var jsDebug = JsDebug();
        Assert.SkipWhen(jsDebug is null, "js-debug is not installed (tools/fetch-js-debug.sh or CSCODE_JSDEBUG)");
        Assert.SkipWhen(Support.Which("node") is null, "node is not installed");
        await using var app = await TerminalAppHost.StartAsync(settings: new() { ["cscode.debug.jsDebugPath"] = jsDebug });
        var folder = app.Sandbox.Combine("jsapp");
        Directory.CreateDirectory(folder);
        var program = Path.Combine(folder, "app.js");
        File.WriteAllText(program, "console.log('debuggee pid=' + process.pid + ' run=' + process.argv[2]);\nsetTimeout(() => console.log('debuggee done'), 200);\n");
        var debug = app.Get<IDebugService>();
        var ended = 0;
        using var sub = debug.SessionTerminated.Subscribe(_ => Interlocked.Increment(ref ended));

        DebugConfiguration Config(string run) => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new
        {
            type = "pwa-node", request = "launch", name = "Launch App", program, args = new[] { run }, cwd = folder,
            console = "integratedTerminal", skipFiles = new[] { "<node_internals>/**" },
        }), new Uri(folder + "/"));

        Assert.NotNull(await debug.StartAsync(Config("one")));
        Assert.True(await TerminalAppHost.WaitFor(() => app.Terminals.Sessions.Any(s => s.Title == "Launch App"), 30_000), "no 'Launch App' terminal");
        var terminal = app.Terminals.Sessions.Single(s => s.Title == "Launch App");
        Assert.True(terminal.IsTransient);
        var pid = terminal.ProcessId!.Value;
        Assert.True(await TerminalAppHost.WaitFor(() => Screen(terminal).Contains("debuggee done", StringComparison.Ordinal), 30_000), Screen(terminal));
        // shell-less: the program the terminal runs is the debuggee itself
        Assert.Contains($"debuggee pid={pid} run=one", Screen(terminal));
        Assert.True(await TerminalAppHost.WaitFor(() => terminal.HasExited && Volatile.Read(ref ended) > 0, 30_000), "the run did not end");
        Assert.False(Directory.Exists($"/proc/{pid}"), "the debuggee is still around");

        // the next run reuses the idle terminal, as VS Code does
        Assert.NotNull(await debug.StartAsync(Config("two")));
        Assert.True(await TerminalAppHost.WaitFor(() => Screen(terminal).Contains("run=two", StringComparison.Ordinal), 30_000), Screen(terminal));
        Assert.Single(app.Terminals.Sessions, s => s.Title == "Launch App");
        Assert.Contains("run=one", Screen(terminal)); // the previous run's output stays above
        Assert.True(await TerminalAppHost.WaitFor(() => terminal.HasExited, 30_000));
    }

    [AvaloniaFact]
    public async Task Stopping_the_session_ends_a_debuggee_running_in_the_terminal()
    {
        var jsDebug = JsDebug();
        Assert.SkipWhen(jsDebug is null, "js-debug is not installed");
        Assert.SkipWhen(Support.Which("node") is null, "node is not installed");
        await using var app = await TerminalAppHost.StartAsync(settings: new() { ["cscode.debug.jsDebugPath"] = jsDebug });
        var folder = app.Sandbox.Combine("jsloop");
        Directory.CreateDirectory(folder);
        var program = Path.Combine(folder, "loop.js");
        File.WriteAllText(program, "console.log('looping'); setInterval(() => {}, 1000);\n");
        var debug = app.Get<IDebugService>();
        var config = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new
        {
            type = "pwa-node", request = "launch", name = "Loop", program, cwd = folder, console = "integratedTerminal",
        }), new Uri(folder + "/"));
        var session = await debug.StartAsync(config);
        Assert.NotNull(session);
        Assert.True(await TerminalAppHost.WaitFor(() => app.Terminals.Sessions.Any(s => s.Title == "Loop" && Screen(s).Contains("looping", StringComparison.Ordinal)), 30_000));
        var terminal = app.Terminals.Sessions.Single(s => s.Title == "Loop");
        var pid = terminal.ProcessId!.Value;
        await debug.StopAllAsync();
        Assert.True(await TerminalAppHost.WaitFor(() => terminal.HasExited, 15_000), "the debuggee survived the stop");
        Assert.True(await TerminalAppHost.WaitFor(() => !Directory.Exists($"/proc/{pid}"), 5_000), "the debuggee process is still around");
        Assert.Contains(terminal, app.Terminals.Sessions); // the terminal stays with its output
    }

    private static string Screen(TerminalSession s) => s.Emulator is { } e ? SafeScreen(s) : "";

    private static string SafeScreen(TerminalSession s)
    {
        var task = s.InvokeAsync(e =>
        {
            var lines = new List<string>();
            for (var a = e.ScrollbackTop; a < e.ScreenTop + e.Rows; a++) if (e.GetRow(a) is { } row) lines.Add(row.GetText());
            return string.Join('\n', lines);
        });
        return task.Wait(2000) ? task.Result : "";
    }
}
