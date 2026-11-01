using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Testing.Engine;
using CsCode.Testing.Providers.DotNet;
using CsCode.Testing.Providers.JavaScript;
using CsCode.Testing.Providers.Rust;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Providers;

/// <summary>A debug session that replays output (in arbitrary chunks) and then terminates.</summary>
internal sealed class ScriptedSession(DebugConfiguration configuration, IReadOnlyList<string> chunks, int exitCode = 0) : IDebugSession
{
    private readonly Emitter<DebugOutputEventArgs> _output = new();
    private readonly Emitter<DebugTerminatedEventArgs> _terminated = new();
    private readonly Emitter<DebugSessionState> _state = new();
    public bool Stopped { get; private set; }

    public void Play() => _ = Task.Run(async () =>
    {
        await Task.Delay(20);
        foreach (var c in chunks) _output.Fire(new DebugOutputEventArgs(this, "stdout", c));
        _output.Fire(new DebugOutputEventArgs(this, "telemetry", "ignored\n"));
        State = DebugSessionState.Terminated;
        _terminated.Fire(new DebugTerminatedEventArgs(this, exitCode, null));
    });

    public string Id => "s";
    public string Name => configuration.Name;
    public DebugConfiguration Configuration => configuration;
    public DebugSessionState State { get; private set; } = DebugSessionState.Running;
    public IDebugSession? Parent => null;
    public DebugCapabilities Capabilities { get; } = new();
    public int? FocusedThreadId => null;
    public IEventSource<DebugStoppedEventArgs> Stopped_ { get; } = new Emitter<DebugStoppedEventArgs>();
    IEventSource<DebugStoppedEventArgs> IDebugSession.Stopped => Stopped_;
    public IEventSource<DebugContinuedEventArgs> Continued { get; } = new Emitter<DebugContinuedEventArgs>();
    public IEventSource<DebugOutputEventArgs> Output => _output;
    public IEventSource<DebugThreadEventArgs> ThreadChanged { get; } = new Emitter<DebugThreadEventArgs>();
    public IEventSource<DebugSessionState> StateChanged => _state;
    public IEventSource<DebugTerminatedEventArgs> Terminated => _terminated;
    public IEventSource<DebugBreakpointStatus> BreakpointStatusChanged { get; } = new Emitter<DebugBreakpointStatus>();
    public IEventSource<IDebugSession> Invalidated { get; } = new Emitter<IDebugSession>();
    public ValueTask<IReadOnlyList<DebugThread>> GetThreadsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(int threadId, int startFrame = 0, int levels = 64, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<DebugScope>> GetScopesAsync(int frameId, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, int? start = null, int? count = null, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask<DebugEvaluateResult> EvaluateAsync(string expression, int? frameId, string context = "repl", CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask<DebugVariable> SetVariableAsync(int variablesReference, string name, string value, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<DebugCompletionItem>> CompletionsAsync(int? frameId, string text, int column, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask ContinueAsync(int? threadId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask StepOverAsync(int? threadId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask StepIntoAsync(int? threadId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask StepOutAsync(int? threadId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask PauseAsync(int? threadId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask RestartAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken ct = default) { Stopped = true; return ValueTask.CompletedTask; }
    public ValueTask<JsonElement?> CustomRequestAsync(string command, JsonElement? arguments = null, CancellationToken ct = default) => ValueTask.FromResult<JsonElement?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Starts <see cref="ScriptedSession"/>s that replay <see cref="Chunks"/>, recording the configurations.</summary>
internal sealed class ScriptedLauncher : ITestDebugLauncher
{
    public List<DebugConfiguration> Started { get; } = [];
    public IReadOnlyList<string> Chunks { get; set; } = [];
    public int ExitCode { get; set; }

    public ValueTask<IDebugSession?> StartAsync(DebugConfiguration configuration, CancellationToken ct)
    {
        Started.Add(configuration);
        var session = new ScriptedSession(configuration, Chunks, ExitCode);
        session.Play();
        return ValueTask.FromResult<IDebugSession?>(session);
    }
}

/// <summary>Debugging tests: the launch configurations the providers build and the results they read back from the session.</summary>
public sealed class DebugTests
{
    private static string[] Chunk(string text, int size) => [.. Enumerable.Range(0, (text.Length + size - 1) / size).Select(i => text.Substring(i * size, Math.Min(size, text.Length - i * size)))];

    [Fact]
    public async Task Dotnet_xunit_v3_debugs_the_test_assembly_under_netcoredbg_and_reads_results_from_its_output()
    {
        var processes = new ScriptedProcesses
        {
            Answer = spec => spec.Arguments.Contains("msbuild")
                ? (0, ["{\"Properties\":{\"IsTestProject\":\"true\",\"TargetPath\":\"/p/bin/Debug/net10.0/p.dll\",\"TargetFramework\":\"net10.0\",\"TargetFrameworks\":\"\"},\"Items\":{\"PackageReference\":[{\"Identity\":\"xunit.v3\"}]}}"], [])
                : (0, [], []),
        };
        var provider = new DotNetTestProvider(new TestProviderContext(processes));
        var project = new TestProject("dotnet", "xs", "/work/xs/xs.csproj", "/work/xs", DotNetTestProvider.FrameworkXunitV3);
        // the reporter's lines as the debuggee prints them, cut in odd places by the adapter's output events
        var launcher = new ScriptedLauncher { Chunks = Chunk(Fixture.Read("xunit-v3-reporter.jsonl"), 97) };
        var sink = new RecordingSink();
        var outcome = await provider.DebugAsync(new TestRunRequest(project, ["Sample.Ns.CalcTests.Fails"]), sink, launcher, TestContext.Current.CancellationToken);
        Assert.Null(outcome.Error);

        Assert.Contains(processes.Started, s => s.Arguments.Count > 0 && s.Arguments[0] == "build"); // built first
        var config = Assert.Single(launcher.Started);
        Assert.Equal(("coreclr", "launch"), (config.Type, config.Request));
        Assert.Equal("/p/bin/Debug/net10.0/p.dll", config.GetString("program"));
        Assert.Equal(["-reporter", "json", "-noColor", "-noLogo", "-method", "Sample.Ns.CalcTests.Fails"], config.Properties["args"].EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("/p/bin/Debug/net10.0", config.GetString("cwd"));
        Assert.Equal(TestOutcome.Failed, sink.Result("Sample.Ns.CalcTests.Fails").Outcome);
        Assert.Equal(TestOutcome.Passed, sink.Result("Sample.Ns.CalcTests.Adds").Outcome);
    }

    [Fact]
    public async Task Dotnet_vstest_projects_attach_to_the_test_host_dotnet_test_announces()
    {
        using var dir = new TempDirectory();
        var processes = new ScriptedProcesses
        {
            Answer = spec =>
            {
                if (spec.Arguments.Contains("msbuild"))
                    return (0, ["{\"Properties\":{\"IsTestProject\":\"true\",\"TargetPath\":\"/p/p.dll\",\"TargetFramework\":\"net10.0\",\"TargetFrameworks\":\"\"},\"Items\":{\"PackageReference\":[{\"Identity\":\"xunit\"}]}}"], []);
                // dotnet test: announce the host, then write the TRX where the logger was told
                var logger = spec.Arguments[spec.Arguments.ToList().IndexOf("--logger") + 1];
                File.WriteAllText(logger["trx;LogFileName=".Length..], Fixture.Read("dotnet-test.trx"));
                return (1, ["Host debugging is enabled. Please attach debugger to testhost process to continue.", "Process Id: 4242, Name: testhost"], []);
            },
        };
        var provider = new DotNetTestProvider(new TestProviderContext(processes));
        var project = new TestProject("dotnet", "xs", dir.Combine("xs.csproj"), dir.Path, DotNetTestProvider.FrameworkVsTest);
        var launcher = new ScriptedLauncher();
        var sink = new RecordingSink();
        await provider.DebugAsync(new TestRunRequest(project, ["Sample.Ns.CalcTests.Fails"]), sink, launcher, TestContext.Current.CancellationToken);
        var test = processes.Started.Single(s => s.Arguments.Count > 0 && s.Arguments[0] == "test");
        Assert.Equal("1", test.Environment!["VSTEST_HOST_DEBUG"]);
        Assert.Contains(@"FullyQualifiedName=Sample.Ns.CalcTests.Fails|FullyQualifiedName~Sample.Ns.CalcTests.Fails\(", test.Arguments);
        var attach = Assert.Single(launcher.Started);
        Assert.Equal(("coreclr", "attach", 4242), (attach.Type, attach.Request, attach.Properties["processId"].GetInt32()));
        Assert.Equal(TestOutcome.Failed, sink.Result("Sample.Ns.CalcTests.Fails").Outcome); // from the TRX
    }

    [Fact]
    public async Task Rust_debugs_the_test_binary_under_the_platform_debugger_and_reads_libtest_output()
    {
        var processes = new ScriptedProcesses
        {
            Answer = spec => spec.FileName.EndsWith("cargo", StringComparison.Ordinal) || spec.Arguments.Contains("--no-run")
                ? (0, ["""{"reason":"compiler-artifact","target":{"kind":["lib"],"name":"rs_sample","src_path":"/w/src/lib.rs"},"profile":{"test":true},"executable":"/w/target/debug/deps/rs_sample-2"}"""], [])
                : (0, [], []),
        };
        string? setting = "gdb";
        var provider = new RustTestProvider(new TestProviderContext(processes, key => key == RustTestProvider.RustDebuggerSetting ? setting : null));
        var project = new TestProject("rust", "rs_sample", "/w/Cargo.toml", "/w", "cargo");
        var launcher = new ScriptedLauncher { Chunks = Chunk(Fixture.Read("libtest-lib.txt"), 33) };
        var sink = new RecordingSink();
        await provider.DebugAsync(new TestRunRequest(project, ["lib/tests::fails"]), sink, launcher, TestContext.Current.CancellationToken);
        var config = Assert.Single(launcher.Started);
        Assert.Equal(("gdb", "/w/target/debug/deps/rs_sample-2", "/w"), (config.Type, config.GetString("program"), config.GetString("cwd")));
        Assert.Equal(["tests::fails", "--exact", "--include-ignored", "--nocapture", "--test-threads=1"], config.Properties["args"].EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("/w", config.Properties["env"].GetProperty("CARGO_MANIFEST_DIR").GetString());
        Assert.Equal(TestOutcome.Failed, sink.Result("lib/tests::fails").Outcome);

        setting = null; // the platform default
        launcher.Started.Clear();
        await provider.DebugAsync(new TestRunRequest(project, ["lib/tests::adds"]), new RecordingSink(), launcher, TestContext.Current.CancellationToken);
        Assert.Equal(OperatingSystem.IsMacOS() ? "lldb" : "gdb", launcher.Started.Single().Type);
    }

    [Fact]
    public async Task Javascript_debugs_the_runner_under_js_debug_in_band()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("node_modules/jest/bin/jest.js", "// stand-in");
        var project = new TestProject("javascript", "js", dir.Combine("package.json"), dir.Path, "jest");
        var provider = new JavaScriptTestProvider(new TestProviderContext(new ScriptedProcesses()));
        var launcher = new ScriptedLauncher();
        var outcome = await provider.DebugAsync(new TestRunRequest(project, ["src/a.test.js::math > adds"]), new RecordingSink(), launcher, TestContext.Current.CancellationToken);
        var config = Assert.Single(launcher.Started);
        Assert.Equal(("pwa-node", dir.Combine("node_modules", "jest", "bin", "jest.js")), (config.Type, config.GetString("program")));
        var args = config.Properties["args"].EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains("--runInBand", args);
        Assert.Contains("--testNamePattern=^(math adds)$", args);
        Assert.Contains("Jest wrote no report", outcome.Error); // the stand-in session wrote none
    }

    [Fact]
    public async Task Cancelling_a_debug_run_stops_the_session()
    {
        var session = new ScriptedSession(new DebugConfiguration("x", "launch", "x", new Dictionary<string, JsonElement>()), []);
        using var cts = new CancellationTokenSource();
        var wait = DebugSessions.PumpOutputAsync(session, _ => { }, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.True(session.Stopped);
    }
}
