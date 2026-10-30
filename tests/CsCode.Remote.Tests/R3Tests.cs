using System.Diagnostics;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.Core.Terminal;
using CsCode.Remote.Bridge;
using CsCode.Remote.Client;
using CsCode.Terminal.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>Helpers for remote terminals in tests: the screen text, waiting for output.</summary>
internal static class TerminalProbe
{
    /// <summary>bash without any rc file (never the user's), with a plain prompt.</summary>
    public static TerminalCreateOptions Bash(string? name = null) => new()
    {
        Name = name,
        ShellPath = "/bin/bash",
        ShellArgs = ["--noprofile", "--norc"],
        Env = new Dictionary<string, string?> { ["PS1"] = "$ ", ["PROMPT_COMMAND"] = null, ["HISTFILE"] = "/dev/null" },
        Show = false,
        InitialSize = (100, 30),
    };

    public static Task<string> ScreenAsync(TerminalSession t) => t.InvokeAsync(e => e.ScreenText());

    public static async Task<string> WaitForTextAsync(TerminalSession t, string text, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(30));
        string screen;
        while (!(screen = await ScreenAsync(t)).Contains(text, StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"'{text}' did not appear on the terminal:\n{screen}");
            await Task.Delay(30, TestEnv.Ct);
        }
        return screen;
    }
}

/// <summary>
/// R3 through the real stack: a loopback cscode-server (a real child process behind an <c>ssh://</c> authority) runs the terminals, debug adapters
/// and tasks of the remote workspace; the client side is the real Terminal module's service, the real Dap debug service and task runner.
/// </summary>
public class R3Tests
{
    private static CancellationToken Ct => TestEnv.Ct;

    [Fact]
    public async Task A_remote_workspace_terminal_runs_on_the_server_with_input_output_resize_and_exit_code()
    {
        await using var host = new RemoteTestHost(r3: true);
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();
        var terminals = host.Services.GetRequiredService<TerminalService>();

        var t = (TerminalSession)await terminals.CreateAsync(TerminalProbe.Bash(), Ct);
        Assert.False(t.HasExited, await TerminalProbe.ScreenAsync(t));
        // the shell is the server's child (a PTY there), started in the workspace folder on the server
        var serverPid = host.Manager.Session!.Server.ProcessId;
        Assert.Contains(t.ProcessId!.Value, TestEnv.Descendants(serverPid));
        Assert.Equal(host.Workspace.Path, t.CurrentDirectory);
        Assert.Equal("bash", t.Title + (t.Title == "bash" ? "" : $" (program title '{t.ProgramTitle}')"));

        await t.SendTextAsync("echo marker-$((40+2)); pwd", true, Ct);
        var screen = await TerminalProbe.WaitForTextAsync(t, "marker-42");
        Assert.Contains(host.Workspace.Path, screen);

        t.Resize(120, 40);
        await t.SendTextAsync("stty size", true, Ct);
        await TerminalProbe.WaitForTextAsync(t, "40 120");

        // the foreground process is reported by the server (polled lazily)
        await t.SendTextAsync("sleep 30", true, Ct);
        await TestEnv.WaitUntilAsync(() => t.ForegroundProcessName() == "sleep", TimeSpan.FromSeconds(15), "the foreground process to be sleep");
        Assert.True(t.Input([3], urgent: true)); // Ctrl+C reaches the remote program
        await TestEnv.WaitUntilAsync(() => t.ForegroundProcessName() == "bash", TimeSpan.FromSeconds(15), "bash back in the foreground");

        await t.SendTextAsync("exit 7", true, Ct);
        await TestEnv.WaitUntilAsync(() => t.HasExited, TimeSpan.FromSeconds(15), "the shell to exit");
        Assert.Equal(7, t.ExitCode);
        await terminals.KillAsync(t, Ct);
        var status = await host.Manager.Session!.Peer.InvokeAsync<Protocol.Empty, Protocol.ServerStatus>(Protocol.RemoteMethods.Status, Protocol.Empty.Value, Ct);
        await TestEnv.WaitUntilAsync(() => host.Manager.Session!.Peer.InvokeAsync<Protocol.Empty, Protocol.ServerStatus>(Protocol.RemoteMethods.Status, Protocol.Empty.Value, Ct).Result!.Channels == 0,
            TimeSpan.FromSeconds(10), "the server to forget the terminal");
    }

    [Fact]
    public async Task Killing_a_remote_terminal_ends_its_process_group_on_the_server_and_a_local_workspace_stays_local()
    {
        await using var host = new RemoteTestHost(r3: true);
        await host.ConnectAsync();
        var terminals = host.Services.GetRequiredService<TerminalService>();

        // no remote folder open: terminals are local (this process's children)
        var local = (TerminalSession)await terminals.CreateAsync(TerminalProbe.Bash(), Ct);
        Assert.DoesNotContain(local.ProcessId!.Value, TestEnv.Descendants(host.Manager.Session!.Server.ProcessId));
        Assert.Contains(local.ProcessId!.Value, TestEnv.Descendants(Environment.ProcessId));
        await terminals.KillAsync(local, Ct);

        await host.OpenWorkspaceAsync();
        var t = (TerminalSession)await terminals.CreateAsync(TerminalProbe.Bash(), Ct);
        await t.SendTextAsync("sleep 300 & echo bg-$!", true, Ct);
        var bg = 0;
        await TestEnv.WaitUntilAsync(() =>
        {
            var m = System.Text.RegularExpressions.Regex.Match(TerminalProbe.ScreenAsync(t).Result, @"^bg-(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline);
            return m.Success && int.TryParse(m.Groups[1].Value, out bg);
        }, TimeSpan.FromSeconds(30), "the background job's pid");
        Assert.True(TestEnv.IsAlive(bg));
        var shell = t.ProcessId!.Value;
        await terminals.KillAsync(t, Ct);
        await TestEnv.WaitUntilAsync(() => !TestEnv.IsAlive(shell) && !TestEnv.IsAlive(bg), TimeSpan.FromSeconds(15), "the remote shell and its background job to end");
    }

    [Fact]
    public async Task A_terminal_of_a_program_that_does_not_exist_on_the_server_shows_why()
    {
        await using var host = new RemoteTestHost(r3: true);
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();
        var terminals = host.Services.GetRequiredService<TerminalService>();
        var t = (TerminalSession)await terminals.CreateAsync(TerminalProbe.Bash() with { ShellPath = "/no/such/shell" }, Ct);
        await TestEnv.WaitUntilAsync(() => t.HasExited, TimeSpan.FromSeconds(10), "the failed terminal to count as exited");
        Assert.Equal(-1, t.ExitCode);
        await TerminalProbe.WaitForTextAsync(t, "/no/such/shell");
        await terminals.KillAsync(t, Ct);
    }

    [Fact]
    public async Task Tasks_of_a_remote_folder_run_on_the_server_with_exit_codes_output_and_cancellation()
    {
        await using var host = new RemoteTestHost(r3: true);
        Directory.CreateDirectory(Path.Combine(host.Workspace.Path, ".vscode"));
        File.WriteAllText(Path.Combine(host.Workspace.Path, ".vscode", "tasks.json"), """
            {
              "version": "2.0.0",
              "tasks": [
                { "label": "build", "type": "shell", "command": "echo built-by-$PPID > built.txt; echo task-output-line; exit 3", "group": { "kind": "build", "isDefault": true } },
                { "label": "slow", "type": "shell", "command": "sleep 300" }
              ]
            }
            """);
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();
        var tasks = host.Services.GetRequiredService<ITaskService>();
        Assert.IsType<RemoteTaskService>(tasks);

        var list = await tasks.GetTasksAsync(null, Ct);
        Assert.Equal(["build", "slow"], list.Select(t => t.Label).Order());
        Assert.True(list.Single(t => t.Label == "build").IsDefaultBuild);

        var result = await tasks.RunAsync("build", null, Ct);
        Assert.Equal(3, result.ExitCode);
        Assert.Null(result.Error);
        var built = File.ReadAllText(Path.Combine(host.Workspace.Path, "built.txt")).Trim();
        // the task's shell was started by the server (its parent is a descendant of the server process)
        var parent = int.Parse(built["built-by-".Length..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(parent, TestEnv.Descendants(host.Manager.Session!.Server.ProcessId).Append(host.Manager.Session!.Server.ProcessId));
        await TestEnv.WaitUntilAsync(() => host.Output.Text("Remote: Tasks").Contains("task-output-line", StringComparison.Ordinal), TimeSpan.FromSeconds(10),
            "the task's output in the forwarded Tasks channel");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var sw = Stopwatch.StartNew();
        var cancelled = await tasks.RunAsync("slow", null, cts.Token);
        Assert.True(cancelled.Cancelled);
        Assert.InRange(sw.Elapsed.TotalSeconds, 1, 30);
        await TestEnv.WaitUntilAsync(() => !TestEnv.Descendants(host.Manager.Session!.Server.ProcessId).Any(p => Cmdline(p).Contains("sleep 300", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15), "the cancelled task's process tree to end on the server");
    }

    [Fact]
    public async Task A_remote_debug_session_runs_gdb_on_the_server_and_maps_breakpoints_and_frames_to_the_workspace_uris()
    {
        Assert.SkipUnless(File.Exists("/usr/bin/gdb") && File.Exists("/usr/bin/gcc"), "needs gdb (14+) and gcc");
        await using var host = new RemoteTestHost(r3: true, machineSettings: """{ "cscode.debug.gdbArgs": ["-nx"] }"""); // machine scope: the server host's file
        var source = Path.Combine(host.Workspace.Path, "my prog.c");
        File.WriteAllText(source, """
            #include <stdio.h>
            int twice(int v) {
                return v * 2;
            }
            int main(void) {
                int x = 21;
                int y = twice(x);
                printf("remote-out %d\n", y);
                return y - 40;
            }
            """);
        Compile(source, Path.Combine(host.Workspace.Path, "prog"));
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();
        var debug = host.Services.GetRequiredService<IDebugService>();
        var sourceUri = host.Remote("my prog.c");
        Assert.Equal("ssh", sourceUri.Scheme);
        debug.Breakpoints.Add(sourceUri, 2); // "return v * 2;" (0-based line 2)

        var stops = new List<DebugStoppedEventArgs>();
        var output = new System.Text.StringBuilder();
        var terminated = new TaskCompletionSource<DebugTerminatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.SessionStarted.Subscribe(s =>
        {
            s.Stopped.Subscribe(e => { lock (stops) stops.Add(e); });
            s.Terminated.Subscribe(e => terminated.TrySetResult(e));
        });
        debug.Output.Subscribe(e => { lock (output) output.Append(e.Output); });

        var config = DebugConfiguration.FromJson(JsonDocument.Parse("""
            { "type": "gdb", "request": "launch", "name": "remote C", "program": "${workspaceFolder}/prog", "cwd": "${workspaceFolder}" }
            """).RootElement, host.Remote());
        var session = await debug.StartAsync(config, Ct) ?? throw new Xunit.Sdk.XunitException("no session: " + string.Join(" | ", host.Recording.Messages.Select(m => m.Message)));

        await TestEnv.WaitUntilAsync(() => debug.StopLocation is not null, TimeSpan.FromSeconds(60), "the breakpoint to be hit");
        var stop = debug.StopLocation!;
        Assert.Equal(sourceUri, stop.Uri); // the adapter's remote path came back as the workspace's ssh: URI
        Assert.Equal(2, stop.Line);
        var frames = await session.GetStackTraceAsync(stop.ThreadId, 0, 10, Ct);
        Assert.Equal(["twice", "main"], frames.Take(2).Select(f => f.Name.Split('(')[0].Trim()));
        Assert.Equal(host.Workspace.Path + "/my prog.c", frames[0].Source!.Path); // what GDB speaks: the server's path
        var scopes = await session.GetScopesAsync(frames[0].Id, Ct);
        var locals = await session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct);
        var argument = (await session.EvaluateAsync("v", frames[0].Id, "watch", Ct)).Result;
        Assert.Equal("21", argument);

        // the adapter (gdb) and the debuggee run on the server
        var serverTree = TestEnv.Descendants(host.Manager.Session!.Server.ProcessId);
        Assert.Contains(serverTree, p => Cmdline(p).Contains("-i=dap", StringComparison.Ordinal));
        Assert.Contains(serverTree, p => Cmdline(p).Contains("/prog", StringComparison.Ordinal) && !Cmdline(p).Contains("gdb", StringComparison.Ordinal));

        await session.ContinueAsync(null, Ct);
        var end = await terminated.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await TestEnv.WaitUntilAsync(() => { lock (output) return output.ToString().Contains("remote-out 42", StringComparison.Ordinal); }, TimeSpan.FromSeconds(10), "the debuggee's output");
        _ = end;
        await TestEnv.WaitUntilAsync(() => !TestEnv.Descendants(host.Manager.Session!.Server.ProcessId).Any(p => Cmdline(p).Contains("-i=dap", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(20), "gdb to end on the server");
        Assert.NotEmpty(locals);
    }

    [Fact]
    public async Task A_remote_nodejs_session_runs_js_debug_on_the_server_and_its_child_session_reaches_the_same_adapter_server()
    {
        // js-debug is an adapter SERVER: the launched program becomes a child session (startDebugging) that must connect to that same server.
        var jsDebug = Environment.GetEnvironmentVariable("CSCODE_JSDEBUG");
        Assert.SkipUnless(jsDebug is { Length: > 0 } && Directory.Exists(jsDebug) && File.Exists("/usr/bin/node"), "needs CSCODE_JSDEBUG (tools/fetch-js-debug.sh) and node");
        await using var host = new RemoteTestHost(r3: true, machineSettings: JsonSerializer.Serialize(new Dictionary<string, object> { ["cscode.debug.jsDebugPath"] = jsDebug! }));
        File.WriteAllText(Path.Combine(host.Workspace.Path, "app.js"), """
            function twice(v) {
              const r = v * 2;
              return r;
            }
            const out = twice(21);
            console.log("remote-node " + out);
            """);
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();
        var debug = host.Services.GetRequiredService<IDebugService>();
        var uri = host.Remote("app.js");
        debug.Breakpoints.Add(uri, 2); // return r;
        var started = new List<IDebugSession>();
        var output = new System.Text.StringBuilder();
        debug.SessionStarted.Subscribe(s => { lock (started) started.Add(s); });
        debug.Output.Subscribe(e => { lock (output) output.Append(e.Output); });
        var config = DebugConfiguration.FromJson(JsonDocument.Parse("""{ "type": "pwa-node", "request": "launch", "name": "remote node", "program": "${workspaceFolder}/app.js" }""").RootElement, host.Remote());
        var parent = await debug.StartAsync(config, Ct) ?? throw new Xunit.Sdk.XunitException("no session: " + string.Join(" | ", host.Recording.Messages.Select(m => m.Message)));

        await TestEnv.WaitUntilAsync(() => debug.StopLocation is not null, TimeSpan.FromSeconds(90), "the breakpoint in the child session");
        var stop = debug.StopLocation!;
        Assert.Equal(uri, stop.Uri);
        Assert.Equal(2, stop.Line);
        Assert.NotSame(parent, stop.Session); // the program runs in the child session, on the parent's adapter server
        Assert.Same(parent, stop.Session.Parent);
        var frame = (await stop.Session.GetStackTraceAsync(stop.ThreadId, 0, 1, Ct))[0];
        Assert.Equal("42", (await stop.Session.EvaluateAsync("r", frame.Id, "watch", Ct)).Result);
        var serverTree = TestEnv.Descendants(host.Manager.Session!.Server.ProcessId);
        Assert.Contains(serverTree, p => Cmdline(p).Contains("app.js", StringComparison.Ordinal)); // the debuggee is on the server

        await stop.Session.ContinueAsync(null, Ct);
        await TestEnv.WaitUntilAsync(() => { lock (output) return output.ToString().Contains("remote-node 42", StringComparison.Ordinal); }, TimeSpan.FromSeconds(30), "the program's output");
        await debug.StopAllAsync(Ct);
        await TestEnv.WaitUntilAsync(() => debug.Sessions.Count == 0, TimeSpan.FromSeconds(30), "the sessions to end");
        await TestEnv.WaitUntilAsync(() => !TestEnv.Descendants(host.Manager.Session!.Server.ProcessId).Any(p => Cmdline(p).Contains("app.js", StringComparison.Ordinal) || Cmdline(p).Contains("dapDebugServer", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(30), "js-debug and node to end on the server");
    }

    [Fact]
    public void The_debug_path_mapper_maps_only_the_connected_hosts_uris()
    {
        var authority = Protocol.RemoteAuthority.Parse("me@box:2222");
        var mapper = new Protocol.SshUriMapper(authority);
        var remote = mapper.FromRemotePath("/home/me/a b/#x.c");
        Assert.Equal("/home/me/a b/#x.c", mapper.ToRemotePath(remote));
        Assert.Equal(remote, mapper.FromRemotePath(mapper.ToRemotePath(remote)));
    }

    internal static void Compile(string source, string output)
    {
        using var p = Process.Start(new ProcessStartInfo("/usr/bin/gcc", ["-g", "-O0", "-o", output, source]) { UseShellExecute = false, RedirectStandardError = true })!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "gcc failed: " + err);
    }

    internal static string Cmdline(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' '); }
        catch (IOException) { return ""; }
    }
}
