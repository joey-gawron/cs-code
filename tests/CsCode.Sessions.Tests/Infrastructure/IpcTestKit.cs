using System.Collections.Concurrent;
using CsCode.Sessions.Ipc;
using CsCode.Sessions.Launcher;

namespace CsCode.Sessions.Tests;

/// <summary>A short private runtime directory under /tmp (Unix socket paths are limited to ~108 bytes). Deleted on dispose.</summary>
public sealed class TestRuntime : IDisposable
{
    public TestRuntime()
    {
        Root = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp", "cst-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        Runtime = new RuntimeDirectory(Path.Combine(Root, "run"), "cscode-test-" + Guid.NewGuid().ToString("N")[..6] + "-");
        Assert(Runtime.Ensure() is null);
        Store = new SessionStore(Path.Combine(Root, "data"));
    }

    private static void Assert(bool ok) { if (!ok) throw new InvalidOperationException("the test runtime directory could not be created"); }

    public string Root { get; }
    public RuntimeDirectory Runtime { get; }
    public SessionStore Store { get; }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// An in-process stand-in for a CsCode process: an <see cref="IpcServer"/> at the endpoint of a made-up pid, answering hello/claim/show/hide/quit
/// like the real host does (session ownership, states, last-shown time). Used to test the launcher without starting processes.
/// </summary>
public sealed class FakeHost : IIpcHandler, IDisposable
{
    private static int s_nextPid = 4_100_000;
    private readonly IpcServer _server;
    private readonly object _gate = new();

    public FakeHost(RuntimeDirectory runtime, string? session, string state, int? pid = null)
    {
        Pid = pid ?? Interlocked.Increment(ref s_nextPid);
        Session = session;
        State = state;
        _server = new IpcServer(runtime.EndpointOf(Pid), this);
        _server.Start();
    }

    public int Pid { get; }
    public string? Session { get; private set; }
    public string State { get; private set; }
    public DateTimeOffset? LastShown { get; set; }
    public bool Debugging { get; set; }
    public bool Wedged { get; set; }
    public bool RefuseClaim { get; set; }
    public TimeSpan ShowDelay { get; set; }
    /// <summary>A show finds it quitting (answers <see cref="IpcMessage.QuittingError"/> and reports the quitting state from then on).</summary>
    public bool QuitsBeforeShow { get; set; }
    public ConcurrentQueue<IpcMessage> Received { get; } = new();
    public bool QuitRequested { get; private set; }

    public async Task<IpcMessage> HandleAsync(IpcMessage request, CancellationToken ct)
    {
        Received.Enqueue(request);
        if (Wedged) await Task.Delay(Timeout.Infinite, ct);
        lock (_gate)
        {
            switch (request.Type)
            {
                case IpcVerbs.Hello:
                    return new IpcMessage
                    {
                        Type = IpcVerbs.Hello, Ok = true, Pid = Pid, Session = Session, State = State, Debugging = Debugging,
                        LastShownUnixMs = LastShown?.ToUnixTimeMilliseconds(), ProtocolVersion = IpcMessage.ProtocolVersionCurrent,
                    };
                case IpcVerbs.Claim:
                    if (RefuseClaim || State != ProcessStates.Spare) return IpcMessage.Failure("not a spare");
                    Session = request.Session;
                    State = request.Hidden == true ? ProcessStates.Hidden : ProcessStates.Shown;
                    return IpcMessage.Reply();
                case IpcVerbs.Hide:
                    State = ProcessStates.Hidden;
                    return IpcMessage.Reply();
                case IpcVerbs.Quit:
                    QuitRequested = true;
                    State = ProcessStates.Quitting;
                    return IpcMessage.Reply();
            }
        }
        if (request.Type == IpcVerbs.Show)
        {
            if (QuitsBeforeShow)
            {
                // Its quit committed between the launcher's discovery and the show (the real host's answer).
                lock (_gate) State = ProcessStates.Quitting;
                return IpcMessage.Failure(IpcMessage.QuittingError);
            }
            if (ShowDelay > TimeSpan.Zero) await Task.Delay(ShowDelay, ct);
            lock (_gate)
            {
                State = ProcessStates.Shown;
                LastShown = DateTimeOffset.UtcNow;
            }
            return IpcMessage.Reply();
        }
        return IpcMessage.Failure("unknown verb " + request.Type);
    }

    public void Dispose() => _server.Dispose();
}

/// <summary>Spawner for launcher tests: "starts" a <see cref="FakeHost"/> owning the requested session (or a spare).</summary>
public sealed class FakeSpawner(RuntimeDirectory runtime) : IHostSpawner, IDisposable
{
    public ConcurrentQueue<IReadOnlyList<string>> Spawned { get; } = new();
    public ConcurrentBag<FakeHost> Hosts { get; } = new();

    /// <summary>When set, a spawned session host exits at once instead of starting (start-up failure).</summary>
    public bool FailToStart { get; set; }

    public ISpawnedHost Spawn(IReadOnlyList<string> args)
    {
        Spawned.Enqueue(args);
        if (FailToStart) return new Exited(4_190_000 + Spawned.Count);
        var spare = args.Contains("--spare");
        var i = args.ToList().IndexOf("--session");
        var host = new FakeHost(runtime, spare ? null : args[i + 1], spare ? ProcessStates.Spare : args.Contains("--hidden") ? ProcessStates.Hidden : ProcessStates.Shown);
        Hosts.Add(host);
        return new Running(host.Pid);
    }

    public int SessionStarts => Spawned.Count(a => !a.Contains("--spare"));
    public int SpareStarts => Spawned.Count(a => a.Contains("--spare"));

    public void Dispose()
    {
        foreach (var h in Hosts) h.Dispose();
    }

    private sealed record Running(int Pid) : ISpawnedHost
    {
        public bool HasExited => false;
        public int? ExitCode => null;
    }

    private sealed record Exited(int Pid) : ISpawnedHost
    {
        public bool HasExited => true;
        public int? ExitCode => 1;
    }
}

/// <summary>
/// A Unix socket file left behind by a process that died: bound by a short-lived python3 process that exits without unlinking it (.NET unlinks the
/// sockets it bound when they are disposed, so it cannot produce one).
/// </summary>
public static class StaleSocket
{
    public static void Create(string path)
    {
        if (!File.Exists("/usr/bin/python3")) Xunit.Assert.Skip("needs python3 to leave a stale socket behind");
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/python3",
            ["-c", "import socket,sys; s=socket.socket(socket.AF_UNIX); s.bind(sys.argv[1])", path]) { UseShellExecute = false })!;
        if (!p.WaitForExit(10_000)) { p.Kill(); throw new TimeoutException("python3 did not exit"); }
        if (!File.Exists(path)) throw new InvalidOperationException("no stale socket was left at " + path);
    }
}
