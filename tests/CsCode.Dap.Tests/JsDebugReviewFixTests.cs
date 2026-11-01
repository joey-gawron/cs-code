using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Protocol;
using CsCode.Dap.Service;
using CsCode.Dap.Session;
using CsCode.Dap.Tasks;
using CsCode.Dap.Tests.Fakes;
using CsCode.Dap.Transport;
using CsCode.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// Regression tests for what the adversarial review of the js-debug wave found in the DAP module (docs/DEBUGGING-RUST-TS.md, "Review
/// fixes"). Each one fails without its fix.
/// </summary>
public class JsDebugReviewFixTests
{
    private sealed class RetypingProvider(string to) : IDebugConfigurationProvider
    {
        public ValueTask<DebugConfiguration?> ResolveAsync(Uri? folder, DebugConfiguration configuration, CancellationToken ct) =>
            ValueTask.FromResult<DebugConfiguration?>(configuration with { Type = to });
    }

    // ------------------------------------------------------------------ child sessions

    [Fact]
    public async Task A_child_of_another_debug_type_is_created_by_that_types_factory_not_on_the_parents_server()
    {
        await using var h = new ServiceHarness();
        var other = new FixedPortAdapterFactory(h.Host.Port);
        using var registration = h.Service.RegisterAdapterFactory("other", other);
        var parent = await h.StartAsync();
        await h.AdapterFor(0).RequestFromAdapterAsync("startDebugging", new DapStartDebuggingArguments
        {
            Request = "launch", Configuration = new JsonObject { ["type"] = "other", ["name"] = "child of another type" },
        });
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 2, 5_000, "the child of another type");
        Assert.Same(parent, h.Service.Sessions[1].Parent);
        Assert.Equal(1, other.Calls); // its own type's adapter, not a second connection to the parent's server
        Assert.Equal(1, h.FakeFactory.Calls);

        // a child of the parent's own type (js-debug's children) still connects to the parent's server
        await h.AdapterFor(0).RequestFromAdapterAsync("startDebugging", new DapStartDebuggingArguments
        {
            Request = "launch", Configuration = new JsonObject { ["name"] = "child of the same type" },
        });
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 3, 5_000, "the same-type child");
        Assert.Equal(1, h.FakeFactory.Calls);
        Assert.Equal(1, other.Calls);
    }

    // ------------------------------------------------------------------ breakpoint verdicts of several sessions

    [Fact]
    public void A_session_that_verified_a_breakpoint_wins_over_a_newer_unverified_verdict_of_another_session()
    {
        var m = new CsCode.Dap.Breakpoints.BreakpointManager(null, null);
        var bp = m.Add(new Uri("file:///work/src/main.ts"), 49);
        var parent = new BreakpointManagerTests.FakeSessionRef("parent"); // js-debug's orchestrating session: never binds anything
        var child = new BreakpointManagerTests.FakeSessionRef("child");   // the program's session
        m.ReportStatus(new DebugBreakpointStatus(parent, bp.Id, false, null, "breakpoint.provisionalBreakpoint"));
        m.ReportStatus(new DebugBreakpointStatus(child, bp.Id, false, null, "breakpoint.provisionalBreakpoint"));
        m.ReportStatus(new DebugBreakpointStatus(child, bp.Id, true, 49, null)); // bound through the source map
        Assert.True(m.GetStatus(bp.Id) is { Verified: true, ActualLine: 49 });

        // another breakpoint is added to the file while stopped: both sessions get the file's list again (what real js-debug answers): the
        // parent says "Unbound breakpoint", the child's verdict did not change (and is therefore not re-reported)
        m.ReportStatus(new DebugBreakpointStatus(parent, bp.Id, false, null, "Unbound breakpoint"));
        m.ReportStatus(new DebugBreakpointStatus(child, bp.Id, true, 49, null));
        Assert.True(m.GetStatus(bp.Id) is { Verified: true, ActualLine: 49, Message: null }, $"{m.GetStatus(bp.Id)}"); // was "Unbound breakpoint"

        // when no session verified it, the newest verdict is shown
        m.ClearStatuses("child");
        Assert.Equal("Unbound breakpoint", m.GetStatus(bp.Id)!.Message);
        m.ReportStatus(new DebugBreakpointStatus(child, bp.Id, false, null, "no code"));
        Assert.Equal("no code", m.GetStatus(bp.Id)!.Message);
    }

    // ------------------------------------------------------------------ last-known capabilities

    [Fact]
    public async Task Known_capabilities_answer_for_the_type_the_configuration_asked_for_and_gate_the_breakpoint_editors_after_the_session()
    {
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) =>
        {
            a.Capabilities["supportsHitConditionalBreakpoints"] = false;
            a.Capabilities["supportsLogPoints"] = false;
        });
        // like launch.json "node", which js-debug's provider turns into "pwa-node"
        using var provider = h.Service.RegisterConfigurationProvider("alias", new RetypingProvider("fake"));
        var config = ServiceHarness.Config("aliased", "alias", folder: h.Dir.Uri);
        Assert.Null(h.Service.GetKnownCapabilities("alias"));
        var session = await h.StartAsync(config);
        Assert.Equal("fake", session.Configuration.Type);
        await SessionHarness.Eventually(() => h.Service.GetKnownCapabilities("fake") is not null, 5_000, "capabilities recorded");
        await h.Service.StopAllAsync(Ct);
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 0, 10_000, "the session ended");

        Assert.False(h.Service.GetKnownCapabilities("fake")!.SupportsLogPoints);
        Assert.False(h.Service.GetKnownCapabilities("alias")?.SupportsHitConditionalBreakpoints ?? true, "recorded under the type that was asked for");

        // no session runs: the gate falls back to what the selected configuration's type said last time
        h.Configurations.Selected = config;
        Assert.Equal("The 'alias' debug adapter does not support hit-count breakpoints.", DebugCapabilityGate.HitCountUnsupported(h.Service));
        Assert.Equal("The 'alias' debug adapter does not support logpoints.", DebugCapabilityGate.LogPointUnsupported(h.Service));
        h.Configurations.Selected = ServiceHarness.Config("never ran", "coreclr", folder: h.Dir.Uri);
        Assert.Null(DebugCapabilityGate.HitCountUnsupported(h.Service)); // unknown: nothing is gated
    }

    // ------------------------------------------------------------------ templates

    [Theory]
    [InlineData("../build", "${workspaceFolder}/../build/**/*.js", true)]
    [InlineData(".out", "${workspaceFolder}/.out/**/*.js", false)]
    [InlineData("./dist/", "${workspaceFolder}/dist/**/*.js", false)]
    [InlineData("/abs/dist", "/abs/dist/**/*.js", true)]
    public async Task The_typescript_template_keeps_the_outDir_as_tsconfig_means_it(string outDir, string outFiles, bool outsideTheFolder)
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile("tsconfig.json", "{ \"compilerOptions\": { \"outDir\": \"" + outDir + "\", \"sourceMap\": true } }");
        h.Dir.WriteFile("src/index.ts", "console.log(1);\n");
        var ts = (await new JsDebugAdapterFactory(h.Platform).ProvideInitialAsync(h.Dir.Uri, Ct))[0];
        Assert.Equal("TypeScript: Launch (tsc build)", ts.Name);
        Assert.Equal(outFiles, Assert.Single(ts.Properties["outFiles"].EnumerateArray()).GetString());
        if (outsideTheFolder)
            Assert.Equal(["${workspaceFolder}/**", outFiles.Replace("/**/*.js", "/**", StringComparison.Ordinal), "!**/node_modules/**"],
                ts.Properties["resolveSourceMapLocations"].EnumerateArray().Select(e => e.GetString()));
        else Assert.False(ts.Properties.ContainsKey("resolveSourceMapLocations"));
    }

    [Fact]
    public async Task Without_an_outDir_the_template_looks_for_the_javascript_next_to_the_sources_and_package_main_keeps_its_meaning()
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile("tsconfig.json", """{ "compilerOptions": { "sourceMap": true } }""");
        h.Dir.WriteFile("src/main.ts", "console.log(1);\n");
        h.Dir.WriteFile("package.json", """{ "name": "x", "main": "../shared/app.js" }""");
        var configs = await new JsDebugAdapterFactory(h.Platform).ProvideInitialAsync(h.Dir.Uri, Ct);
        Assert.Equal(["${workspaceFolder}/**/*.js", "!**/node_modules/**"], configs[0].Properties["outFiles"].EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("${workspaceFolder}/../shared/app.js", configs[1].GetString("program")); // was "${workspaceFolder}/shared/app.js"
    }

    [Fact]
    public async Task The_tsc_build_task_is_a_process_task_so_a_folder_whose_path_has_a_dollar_sign_still_compiles()
    {
        Assert.SkipWhen(JsDebugToolchain.FindTsc() is null, "the TypeScript compiler ('tsc') is not on PATH");
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps a shell task (tsc is a .cmd script there)");
        using var parent = new TempDirectory("tscdollar-");
        var dir = Path.Combine(parent.Path, "ws$HOME"); // /bin/sh would expand $HOME in an unquoted shell command line
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "tsconfig.json"), JsDebugSample.TsConfig);
        File.WriteAllText(Path.Combine(dir, "src", "main.ts"), "console.log(1);\n");
        var folder = new Uri(dir + "/");
        using var lane = new DebugLane("tsc-dollar");
        var platform = new DapPlatform { Workspace = new FakeWorkspace(folder) };
        var (name, content) = Assert.Single(await new JsDebugAdapterFactory(platform).ProvideInitialFilesAsync(folder, Ct));
        Assert.Equal("tasks.json", name);
        using (var doc = JsonDocument.Parse(content)) Assert.Equal("process", doc.RootElement.GetProperty("tasks")[0].GetProperty("type").GetString());
        Directory.CreateDirectory(Path.Combine(dir, ".cscode"));
        File.WriteAllText(Path.Combine(dir, ".cscode", "tasks.json"), content);
        var tasks = new TaskService(lane, platform);
        try
        {
            var result = await tasks.RunAsync("tsc: build", folder, Ct);
            Assert.True(result.Succeeded, $"exit {result.ExitCode} {result.Error}");
            Assert.True(File.Exists(Path.Combine(dir, "out", "main.js")), "tsc compiled the folder whose path contains '$'");
        }
        finally { tasks.Dispose(); }
    }

    [Fact]
    public async Task A_terminal_launch_runs_in_the_debug_console_with_js_debug_capturing_the_programs_streams()
    {
        var f = new JsDebugAdapterFactory(new DapPlatform());
        static DebugConfiguration Cfg(object o) => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(o), null);
        var terminal = (await f.ResolveAsync(null, Cfg(new { type = "pwa-node", request = "launch", name = "t", program = "/w/a.js", console = "integratedTerminal" }), Ct))!;
        Assert.Equal(("internalConsole", "std"), (terminal.GetString("console"), terminal.GetString("outputCapture")));
        var external = (await f.ResolveAsync(null, Cfg(new { type = "pwa-node", request = "launch", name = "e", program = "/w/a.js", console = "externalTerminal", outputCapture = "console" }), Ct))!;
        Assert.Equal(("internalConsole", "console"), (external.GetString("console"), external.GetString("outputCapture"))); // the user's capture is kept
        var console = (await f.ResolveAsync(null, Cfg(new { type = "pwa-node", request = "launch", name = "c", program = "/w/a.js", console = "internalConsole" }), Ct))!;
        Assert.Null(console.GetString("outputCapture"));
        var attach = (await f.ResolveAsync(null, Cfg(new { type = "pwa-node", request = "attach", name = "a", port = 9229, console = "integratedTerminal" }), Ct))!;
        Assert.Equal("integratedTerminal", attach.GetString("console"));
    }

    // ------------------------------------------------------------------ archives

    private static MemoryStream TarGz(Action<TarWriter> write)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true)) write(tar);
        ms.Position = 0;
        return ms;
    }

    private static void AddLink(TarWriter tar, string name, string target) => tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, name) { LinkName = target });
    private static void AddFile(TarWriter tar, string name, string text) => tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
    private static void AddDirectory(TarWriter tar, string name) => tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name));

    [Fact]
    public void A_chain_of_symlinks_cannot_make_the_tar_extractor_write_outside_the_destination()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");
        using var root = new TempDirectory("tarchain-");
        var dest = Path.Combine(root.Path, "dest");
        Directory.CreateDirectory(dest);
        using var archive = TarGz(tar =>
        {
            AddLink(tar, "y", ".");      // lexically inside
            AddLink(tar, "x", "y/..");   // lexically inside (dest/y/.. = dest), really the parent of dest
            AddFile(tar, "x/escaped.txt", "written outside");
        });
        Assert.Throws<UnsafeArchiveException>(() => NetCoreDbgInstaller.ExtractTarGz(archive, dest));
        Assert.False(File.Exists(Path.Combine(root.Path, "escaped.txt")), "nothing was written next to the destination");
    }

    [Fact]
    public void The_tar_extractor_never_writes_through_a_symlink_but_still_extracts_plain_relative_links()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");
        using var root = new TempDirectory("tarlinks-");
        var dest = Path.Combine(root.Path, "dest");
        using (var ok = TarGz(tar => { AddDirectory(tar, "lib/"); AddFile(tar, "lib/real.txt", "real"); AddLink(tar, "lib/alias.txt", "real.txt"); }))
            NetCoreDbgInstaller.ExtractTarGz(ok, dest);
        Assert.Equal("real", File.ReadAllText(Path.Combine(dest, "lib", "alias.txt")));

        var dest2 = Path.Combine(root.Path, "dest2");
        using var through = TarGz(tar => { AddDirectory(tar, "sub/"); AddLink(tar, "a", "sub"); AddFile(tar, "a/f.txt", "through a link"); });
        Assert.Throws<UnsafeArchiveException>(() => NetCoreDbgInstaller.ExtractTarGz(through, dest2));
        Assert.False(File.Exists(Path.Combine(dest2, "sub", "f.txt")));
    }

    // ------------------------------------------------------------------ console history

    [Fact]
    public void A_flood_batch_then_thousands_of_one_line_events_cut_the_batch_in_place_instead_of_copying_it_per_event()
    {
        var session = new EndedSession("s1", "session", ServiceHarness.Config());
        var history = new OutputHistory(DebugService.MaxHistoryLines, 4 * 1024 * 1024);
        var big = string.Concat(Enumerable.Range(0, 5_000).Select(i => $"batch line {i:D5}..........\n")); // ~135K chars, 5,000 lines in one event
        var small = Enumerable.Range(0, 5_000).Select(i => new DebugOutputEventArgs(session, "stdout", $"line {i}\n")).ToArray();
        history.Add(new DebugOutputEventArgs(session, "stdout", big));
        for (var i = 0; i < 100; i++) history.Add(small[i]);
        var middle = string.Concat(history.Snapshot().Select(e => e.Output)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(DebugService.MaxHistoryLines, middle.Length);
        Assert.Equal("batch line 00100..........", middle[0]); // the newest 4,900 lines of the batch are kept
        Assert.Equal("line 99", middle[^1]);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var slowest = 0.0;
        for (var i = 100; i < small.Length; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            history.Add(small[i]);
            slowest = Math.Max(slowest, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        TestMetrics.Report($"[history] 4,900 one-line events after a 5,000-line batch: {sw.ElapsedMilliseconds} ms, slowest add {slowest:F3} ms, {allocated / 1024} KB allocated");
        Assert.True(allocated < 8L * 1024 * 1024, $"{allocated / (1024 * 1024)} MB allocated: the batch was copied per event");

        var lines = string.Concat(history.Snapshot().Select(e => e.Output)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(small.Select(e => e.Output.TrimEnd('\n')), lines);
        Assert.Equal(DebugService.MaxHistoryLines, history.Lines);
    }

    [Fact]
    public void The_character_budget_cuts_the_head_of_a_single_huge_line()
    {
        var session = new EndedSession("s1", "session", ServiceHarness.Config());
        var history = new OutputHistory(10, 100);
        history.Add(new DebugOutputEventArgs(session, "stdout", new string('a', 150) + "\n"));
        Assert.Equal(100, history.Chars);
        history.Add(new DebugOutputEventArgs(session, "stdout", "b\n"));
        Assert.True(history.Chars <= 100);
        Assert.Equal("b\n", history.Snapshot()[^1].Output);
        Assert.EndsWith("a\n", history.Snapshot()[0].Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_history_copies_of_an_event_keep_its_sequence_when_the_head_is_cut_and_when_the_session_is_relabelled()
    {
        // Follow-up "REPL line placement after a flood": the Debug Console places a REPL line after the event it followed by its Sequence; the
        // history replaces an oversize batch with `with { Output = tail }` and an ended session's events with `with { Session = ended }`.
        var live = new EndedSession("s1", "session", ServiceHarness.Config());
        var ended = new EndedSession("s1", "session (ended)", ServiceHarness.Config());
        var history = new OutputHistory(10, 1_000);
        var batch = new DebugOutputEventArgs(live, "stdout", string.Concat(Enumerable.Range(0, 30).Select(i => $"line {i}\n")));
        var after = new DebugOutputEventArgs(live, "stdout", "after\n");
        Assert.True(after.Sequence > batch.Sequence);
        history.Add(batch);
        history.Add(after);

        var cut = history.Snapshot();
        Assert.NotSame(batch, cut[0]);
        Assert.StartsWith("line 21\n", cut[0].Output, StringComparison.Ordinal);
        Assert.Equal([batch.Sequence, after.Sequence], cut.Select(e => e.Sequence));

        history.Relabel(live, ended);
        var relabelled = history.Snapshot();
        Assert.All(relabelled, e => Assert.Same(ended, e.Session));
        Assert.Equal([batch.Sequence, after.Sequence], relabelled.Select(e => e.Sequence));
    }
}

/// <summary>
/// The lifetime of an adapter SERVER process (<see cref="ServerProcessTransport"/>), with a shell script standing in for js-debug: it reports a
/// fake adapter's port, spawns one process that detaches into a session of its own (<c>setsid</c>, like Node's <c>detached: true</c>) and one
/// plain orphan, then becomes the server (<c>exec sleep</c>). Linux only (/proc).
/// </summary>
[Collection(SerialCollection.Name)]
public class AdapterServerProcessLifetimeTests
{
    private const string Pattern = @"^listening at (?<host>[^\s]+?):(?<port>\d+)\s*$";

    private static DebugAdapterServerProcess Descriptor(int port, bool withChildren = true) => new("/bin/sh",
        ["-c", (withChildren ? "(setsid sleep 600 >/dev/null 2>&1 &); (sleep 601 >/dev/null 2>&1 &); " : "") + $"echo \"listening at 127.0.0.1:{port}\"; exec sleep 602"],
        Pattern) { DisplayName = "fake adapter server" };

    private sealed class ServerProcessFactory(int port) : IDebugAdapterFactory
    {
        public ValueTask<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration configuration, CancellationToken ct) =>
            ValueTask.FromResult<DebugAdapterDescriptor?>(Descriptor(port, withChildren: false));
    }

    private static void SkipUnlessLinux() =>
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/sh") && (File.Exists("/usr/bin/setsid") || File.Exists("/bin/setsid")), "needs Linux /proc, /bin/sh and setsid");

    /// <summary>The detached and the orphaned child of the fake server, once both run.</summary>
    private static async Task<(int Detached, int Orphan)> ChildrenAsync(ServerProcessTransport transport)
    {
        int detached = 0, orphan = 0;
        await SessionHarness.Eventually(() =>
        {
            foreach (var pid in ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", transport.Marker))
            {
                var cmd = ProcessTable.CommandLine(pid);
                if (cmd == "sleep 600") detached = pid;
                else if (cmd == "sleep 601") orphan = pid;
            }
            return detached > 0 && orphan > 0;
        }, 10_000, "the fake server's children run");
        return (detached, orphan);
    }

    private static void KillAll(string marker)
    {
        foreach (var pid in ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker)) // only processes this test started carry its marker
            try { using var p = Process.GetProcessById(pid); p.Kill(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    [Fact]
    public async Task On_a_normal_close_the_servers_tree_dies_and_processes_that_left_it_keep_running()
    {
        SkipUnlessLinux();
        await using var host = new TcpFakeAdapterHost();
        var transport = await ServerProcessTransport.StartAsync(Descriptor(host.Port), NullLogger.Instance, Ct);
        var marker = transport.Marker;
        try
        {
            var server = transport.ProcessId!.Value;
            var (detached, orphan) = await ChildrenAsync(transport);
            await transport.DisposeAsync(); // the session ended normally (Stop, or the program ended)
            await SessionHarness.Eventually(() => !ProcessTable.IsAlive(server), 5_000, "the server is killed");
            await Task.Delay(500, Ct);
            Assert.True(ProcessTable.IsAlive(detached), "a process detached into its own session (a browser opened by the program) is left running");
            Assert.True(ProcessTable.IsAlive(orphan), "a process that left the server's tree is not the server's to kill on a normal close");
        }
        finally { KillAll(marker); }
    }

    [Fact]
    public async Task When_the_server_died_on_its_own_its_orphans_are_reaped_but_not_a_process_detached_into_its_own_session()
    {
        SkipUnlessLinux();
        await using var host = new TcpFakeAdapterHost();
        var transport = await ServerProcessTransport.StartAsync(Descriptor(host.Port), NullLogger.Instance, Ct);
        var marker = transport.Marker;
        try
        {
            var server = transport.ProcessId!.Value;
            var (detached, orphan) = await ChildrenAsync(transport);
            Assert.Equal(AdapterProcessReaper.SessionOf(server), AdapterProcessReaper.SessionOf(orphan));
            Assert.NotEqual(AdapterProcessReaper.SessionOf(server), AdapterProcessReaper.SessionOf(detached));
            using (var p = Process.GetProcessById(server)) p.Kill(); // the adapter crashes
            await SessionHarness.Eventually(() => !ProcessTable.IsAlive(server), 5_000, "the server is gone");
            await transport.DisposeAsync();
            await SessionHarness.Eventually(() => !ProcessTable.IsAlive(orphan), 5_000, "the orphan of the crashed server is reaped");
            Assert.True(ProcessTable.IsAlive(detached), "a process detached into its own session is left running");
        }
        finally { KillAll(marker); }
    }

    [Fact]
    public async Task Disposing_the_debug_service_synchronously_kills_adapter_servers_even_when_the_debug_lane_never_gets_to_run()
    {
        SkipUnlessLinux();
        await using var h = new ServiceHarness();
        using var registration = h.Service.RegisterAdapterFactory("srv", new ServerProcessFactory(h.Host.Port));
        var session = (DebugSession)await h.StartAsync(ServiceHarness.Config("server", "srv", folder: h.Dir.Uri));
        var server = session.AdapterProcessId!.Value;
        var marker = session.AdapterProcessMarker!;
        Assert.True(ProcessTable.IsAlive(server));
        var release = new int[1];
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Lane.Post(() =>
        {
            blocked.TrySetResult();
            var deadline = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
            while (Volatile.Read(ref release[0]) == 0 && Stopwatch.GetTimestamp() < deadline) Thread.Sleep(5); // the lane is busy (or the process is exiting)
        });
        await blocked.Task;
        try
        {
            h.Service.Dispose(); // a container disposed synchronously: the async shutdown cannot run while the lane is busy
            await SessionHarness.Eventually(() => !ProcessTable.IsAlive(server), 3_000, "the server was killed without waiting for the lane");
        }
        finally
        {
            Volatile.Write(ref release[0], 1);
            KillAll(marker);
        }
    }
}
