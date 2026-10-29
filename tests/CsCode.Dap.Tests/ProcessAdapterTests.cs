using System.Diagnostics;
using CsCode.Core.Debugging;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.Dap.Transport;
using CsCode.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The REAL <see cref="DebugSession"/> over REAL stdio of a child process (a ~60-line Python DAP adapter written to a temp dir):
/// the process-lifecycle half of the brief — graceful stop, hard kill after timeout, crash, startup failure, and "never leaves zombies".
/// Skips (with a reason) when python3 is missing.
/// </summary>
[Collection(SerialCollection.Name)]
public class ProcessAdapterTests : IDisposable
{
    /// <summary>The scripted adapter (modes from the launch arguments: crash, ignore, chatty, linger — slow to exit once told to go).</summary>
    internal const string Script = """
        import sys, json, os, time

        def read():
            hdr = b''
            while not hdr.endswith(b'\r\n\r\n'):
                c = sys.stdin.buffer.read(1)
                if not c:
                    return None
                hdr += c
            n = int([l for l in hdr.split(b'\r\n') if l.lower().startswith(b'content-length')][0].split(b':')[1])
            return json.loads(sys.stdin.buffer.read(n))

        seq = 0
        def send(m):
            global seq
            seq += 1
            m['seq'] = seq
            b = json.dumps(m).encode()
            sys.stdout.buffer.write(b'Content-Length: %d\r\n\r\n' % len(b) + b)
            sys.stdout.buffer.flush()

        def resp(req, body=None, ok=True, msg=None):
            m = {'type': 'response', 'request_seq': req['seq'], 'success': ok, 'command': req['command']}
            if body is not None: m['body'] = body
            if msg: m['message'] = msg
            send(m)

        mode = None
        if os.environ.get('ADAPTER_MODE') == 'startup-failure':
            sys.stderr.write('FATAL: bad adapter configuration\n'); sys.stderr.flush()
            sys.exit(4)

        while True:
            r = read()
            if r is None:
                if mode == 'linger':
                    time.sleep(3)
                break
            c = r['command']
            if c == 'initialize':
                resp(r, {'supportsConfigurationDoneRequest': True, 'supportsTerminateRequest': True})
                send({'type': 'event', 'event': 'initialized'})
            elif c == 'launch':
                mode = (r.get('arguments') or {}).get('mode')
                resp(r)
            elif c == 'configurationDone':
                resp(r)
                if mode == 'crash':
                    os._exit(3)
            elif c == 'threads':
                if mode == 'chatty':
                    send({'type': 'event', 'event': 'output', 'body': {'category': 'stdout', 'output': 'hello from adapter\n'}})
                resp(r, {'threads': [{'id': 1, 'name': 'main'}]})
            elif c == 'setBreakpoints':
                resp(r, {'breakpoints': [{'verified': True, 'line': b.get('line')} for b in r['arguments'].get('breakpoints', [])]})
            elif c == 'terminate':
                if mode == 'ignore':
                    continue
                resp(r)
                send({'type': 'event', 'event': 'exited', 'body': {'exitCode': 0}})
                send({'type': 'event', 'event': 'terminated', 'body': {}})
            elif c == 'disconnect':
                if mode == 'ignore':
                    continue
                resp(r)
                if mode == 'linger':
                    time.sleep(3)
                break
            else:
                resp(r)
        """;

    private readonly TempDirectory _dir = new("dapproc-");
    private readonly List<DebugSession> _sessions = new();
    private DebugLane? _lane;
    private readonly CsCode.Dap.Breakpoints.BreakpointManager _breakpoints = new();

    internal static string? Python() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "python3")).FirstOrDefault(File.Exists);

    private async Task<DebugSession> StartAsync(string? mode = null, DebugSessionSettings? settings = null, IDictionary<string, string>? env = null)
    {
        var python = Python();
        Assert.SkipUnless(python is not null, "python3 is not available (used as a scripted DAP adapter)");
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "posix process semantics");
        var script = _dir.WriteFile("adapter.py", Script);
        _lane ??= new DebugLane("proc-adapter");
        var descriptor = new DebugAdapterExecutable(python!, ["-I", "-u", script], env is null ? null : new Dictionary<string, string>(env), _dir.Path);
        var cfg = Cfg.Make(extra: mode is null ? null : new Dictionary<string, object?> { ["mode"] = mode });
        var session = new DebugSession(new DebugSessionOptions("p" + _sessions.Count, cfg,
            ct => DapTransportFactory.OpenAsync(descriptor, NullLogger.Instance, TimeSpan.FromSeconds(10), ct), _breakpoints, _lane)
        {
            Settings = settings ?? new DebugSessionSettings { RequestTimeout = TimeSpan.FromSeconds(5), StopTimeout = TimeSpan.FromSeconds(3) },
        });
        _sessions.Add(session);
        await session.StartAsync(Ct);
        return session;
    }

    [Fact]
    public async Task A_full_handshake_over_real_stdio_works()
    {
        var s = await StartAsync();
        Assert.Equal(DebugSessionState.Running, s.State);
        Assert.NotNull(s.AdapterProcessId);
        Assert.Equal("main", Assert.Single(await s.GetThreadsAsync(Ct)).Name);
    }

    [Fact]
    public async Task Graceful_stop_uses_terminate_and_the_adapter_process_exits()
    {
        var s = await StartAsync();
        var pid = s.AdapterProcessId!.Value;
        var terminated = new List<DebugTerminatedEventArgs>();
        s.Terminated.Subscribe(e => { lock (terminated) terminated.Add(e); });
        var sw = Stopwatch.StartNew();
        await s.StopAsync(Ct);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"graceful stop took {sw.Elapsed}");
        Assert.Equal(DebugSessionState.Terminated, s.State);
        lock (terminated) Assert.Equal(0, Assert.Single(terminated).ExitCode);
        await SessionHarness.Eventually(() => !SessionReverseRequestTests.ProcessAlive(pid), 5000, "adapter must exit");
    }

    [Fact]
    public async Task An_adapter_that_ignores_terminate_and_disconnect_is_hard_killed()
    {
        var settings = new DebugSessionSettings { RequestTimeout = TimeSpan.FromMilliseconds(400), StopTimeout = TimeSpan.FromMilliseconds(400) };
        var s = await StartAsync("ignore", settings);
        var pid = s.AdapterProcessId!.Value;
        var sw = Stopwatch.StartNew();
        await s.StopAsync(Ct);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"hard stop took {sw.Elapsed}");
        Assert.Equal(DebugSessionState.Terminated, s.State);
        await SessionHarness.Eventually(() => !SessionReverseRequestTests.ProcessAlive(pid), 5000, "hung adapter must be killed, not orphaned");
    }

    [Fact]
    public async Task An_adapter_crash_after_launch_ends_the_session_cleanly_and_leaves_no_process()
    {
        var python = Python();
        Assert.SkipUnless(python is not null, "python3 is not available (used as a scripted DAP adapter)");
        var s = await StartAsync("crash"); // exits with code 3 right after configurationDone
        var pid = s.AdapterProcessId;
        await SessionHarness.Eventually(() => s.State == DebugSessionState.Terminated, 10_000, "crash detected");
        if (pid is { } p) await SessionHarness.Eventually(() => !SessionReverseRequestTests.ProcessAlive(p), 5000);
    }

    [Fact]
    public async Task A_startup_failure_reports_the_adapters_stderr()
    {
        var python = Python();
        Assert.SkipUnless(python is not null && !OperatingSystem.IsWindows(), "python3 is not available (used as a scripted DAP adapter)");
        var ex = await Assert.ThrowsAsync<DebugSessionStartException>(async () => await StartAsync(env: new Dictionary<string, string> { ["ADAPTER_MODE"] = "startup-failure" }));
        Assert.Contains("FATAL: bad adapter configuration", ex.Message);
    }

    [Fact]
    public async Task Adapter_output_flows_through_real_stdio()
    {
        var s = await StartAsync("chatty");
        var seen = new TaskCompletionSource();
        s.Output.Subscribe(e => { if (e.Output.Contains("hello from adapter")) seen.TrySetResult(); });
        await s.GetThreadsAsync(Ct); // the adapter emits the output event while answering this
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task Breakpoints_are_mirrored_over_real_stdio_and_verified()
    {
        _breakpoints.Toggle(new Uri("file:///work/a.cs"), 4);
        var s = await StartAsync();
        var bp = _breakpoints.SourceBreakpoints.Single();
        await SessionHarness.Eventually(() => _breakpoints.GetStatus(bp.Id)?.Verified == true, 5000, "verdict from the process adapter");
        Assert.Equal(4, _breakpoints.GetStatus(bp.Id)!.ActualLine);
        await s.StopAsync(Ct);
        await SessionHarness.Eventually(() => _breakpoints.StatusCount == 0);
    }

    [Fact]
    public async Task Twenty_start_stop_cycles_over_real_processes_leave_none_behind()
    {
        var pids = new List<int>();
        for (var i = 0; i < 20; i++)
        {
            var s = await StartAsync();
            pids.Add(s.AdapterProcessId!.Value);
            await s.StopAsync(Ct);
        }
        foreach (var pid in pids) await SessionHarness.Eventually(() => !SessionReverseRequestTests.ProcessAlive(pid), 5000, $"adapter {pid}");
    }

    public void Dispose()
    {
        foreach (var s in _sessions) { try { s.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)); } catch (Exception) { } }
        _breakpoints.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _lane?.Dispose();
        _dir.Dispose();
    }
}
