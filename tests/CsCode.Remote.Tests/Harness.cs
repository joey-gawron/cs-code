using System.Text;
using CsCode.Core;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Dap;
using CsCode.Lsp;
using CsCode.Platform;
using CsCode.Remote;
using CsCode.Remote.Client;
using CsCode.Remote.Protocol;
using CsCode.Remote.Transport;
using CsCode.Terminal;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>A one-way in-memory byte pipe with blocking reads (like a process pipe): disposing the write end is EOF for the reader.</summary>
public sealed class MemoryPipe
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _chunks = new();
    private byte[]? _current;
    private int _offset;
    private bool _closed;

    public Stream Reader => new ReadEnd(this);
    public Stream Writer => new WriteEnd(this);

    /// <summary>Bytes written so far (tests that corrupt or inspect the stream).</summary>
    public long Written;

    private void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_closed) throw new IOException("pipe closed");
            _chunks.Enqueue(data.ToArray());
            Written += data.Length;
            Monitor.PulseAll(_gate);
        }
    }

    private int Read(Span<byte> buffer)
    {
        lock (_gate)
        {
            while (true)
            {
                if (_current is not null && _offset < _current.Length)
                {
                    var n = Math.Min(buffer.Length, _current.Length - _offset);
                    _current.AsSpan(_offset, n).CopyTo(buffer);
                    _offset += n;
                    return n;
                }
                if (_chunks.TryDequeue(out var next)) { _current = next; _offset = 0; continue; }
                if (_closed) return 0;
                Monitor.Wait(_gate);
            }
        }
    }

    public void Close()
    {
        lock (_gate) { _closed = true; Monitor.PulseAll(_gate); }
    }

    private sealed class ReadEnd(MemoryPipe pipe) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => pipe.Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) => pipe.Read(buffer);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) pipe.Close(); base.Dispose(disposing); }
    }

    private sealed class WriteEnd(MemoryPipe pipe) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => pipe.Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => pipe.Write(buffer);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) pipe.Close(); base.Dispose(disposing); }
    }
}

/// <summary>Two peers connected back to back in memory.</summary>
public sealed class PeerPair : IDisposable
{
    public PeerPair(RpcPeerOptions? a = null, RpcPeerOptions? b = null)
    {
        AtoB = new MemoryPipe();
        BtoA = new MemoryPipe();
        A = new RpcPeer(BtoA.Reader, AtoB.Writer, a ?? new RpcPeerOptions { Name = "A" });
        B = new RpcPeer(AtoB.Reader, BtoA.Writer, b ?? new RpcPeerOptions { Name = "B" });
    }

    public MemoryPipe AtoB { get; }
    public MemoryPipe BtoA { get; }
    public RpcPeer A { get; }
    public RpcPeer B { get; }

    public void Start() { A.Start(); B.Start(); }

    public void Dispose()
    {
        A.Close();
        B.Close();
        AtoB.Close();
        BtoA.Close();
    }
}

public static class TestEnv
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The server build next to the tests.</summary>
    public static string ServerAssembly => Path.Combine(AppContext.BaseDirectory, LocalServer.AssemblyFile);

    /// <summary>Repository root (the folder with CsCode.slnx) of the tests' build.</summary>
    public static string RepoRoot
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "CsCode.slnx"))) return d.FullName;
            throw new InvalidOperationException("repository root not found");
        }
    }

    /// <summary>
    /// <c>CSCODE_TEST_SERVER</c>: a published <c>cscode-server</c> executable (an extracted bundle, e.g. the trimmed one) that the loopback tests run
    /// instead of the build's <c>cscode-server.dll</c>; unset: the build's.
    /// </summary>
    public static string? PublishedServer => Environment.GetEnvironmentVariable("CSCODE_TEST_SERVER") is { Length: > 0 } exe && File.Exists(exe) ? exe : null;

    /// <summary>
    /// Launch line of a local server (data dir in <paramref name="dataDir"/>; marker for process accounting). HOME and the XDG directories point into
    /// the data dir, so the server and the language servers it starts never touch the user's caches or configuration.
    /// </summary>
    public static ProcessLaunch ServerLaunch(string dataDir, string marker, (string Session, int GraceSeconds)? persist = null)
    {
        var home = Path.Combine(dataDir, "home");
        Directory.CreateDirectory(home);
        var launch = LocalServer.Launch(ServerAssembly, dataDir, marker, belowNormal: true, persist: persist, environment: ServerEnvironment(home));
        // the same arguments, without `dotnet <dll>`, for a published single-file server
        return PublishedServer is { } exe ? launch with { FileName = exe, Arguments = launch.Arguments.Skip(1).ToArray() } : launch;
    }

    private static Dictionary<string, string?> ServerEnvironment(string home) => new()
        {
            ["SSH_AUTH_SOCK"] = null,
            ["HOME"] = home,
            ["XDG_CONFIG_HOME"] = Path.Combine(home, ".config"),
            ["XDG_CACHE_HOME"] = Path.Combine(home, ".cache"),
            ["XDG_DATA_HOME"] = Path.Combine(home, ".local", "share"),
            ["XDG_STATE_HOME"] = Path.Combine(home, ".local", "state"),
            ["CSCODE_HOME"] = null,
        };

    /// <summary>PIDs of live processes whose command line contains <paramref name="marker"/> (Linux /proc).</summary>
    public static IReadOnlyList<int> ProcessesWithMarker(string marker)
    {
        var pids = new List<int>();
        if (!OperatingSystem.IsLinux()) return pids;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var cmd = File.ReadAllText(Path.Combine(dir, "cmdline"));
                if (cmd.Contains(marker, StringComparison.Ordinal) && !IsZombie(dir)) pids.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return pids;
    }

    /// <summary>Every live descendant of <paramref name="pid"/> (Linux /proc parent links), e.g. the language servers a server started.</summary>
    public static IReadOnlyList<int> Descendants(int pid)
    {
        var parents = new Dictionary<int, int>();
        if (!OperatingSystem.IsLinux()) return [];
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var p)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var close = stat.LastIndexOf(')');
                var fields = stat[(close + 2)..].Split(' ');
                if (fields[0] != "Z") parents[p] = int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { }
        }
        var result = new List<int>();
        var frontier = new Queue<int>([pid]);
        while (frontier.TryDequeue(out var cur))
            foreach (var (child, parent) in parents)
                if (parent == cur) { result.Add(child); frontier.Enqueue(child); }
        return result;
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)] != 'Z';
        }
        catch (IOException) { return false; }
    }

    private static bool IsZombie(string procDir)
    {
        try
        {
            var stat = File.ReadAllText(Path.Combine(procDir, "stat"));
            var close = stat.LastIndexOf(')');
            return close > 0 && close + 2 < stat.Length && stat[close + 2] == 'Z';
        }
        catch (IOException) { return false; }
    }

    /// <summary>Waits until no process carries the marker (servers exit asynchronously after their stdin closes).</summary>
    public static async Task AssertNoProcessesAsync(string marker, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(15));
        IReadOnlyList<int> left;
        while ((left = ProcessesWithMarker(marker)).Count > 0 && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.True(left.Count == 0, $"processes still running with marker {marker}: {string.Join(", ", left)}");
    }

    public static async Task<T> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout, string what) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } v) return v;
            await Task.Delay(25, Ct);
        }
        throw new TimeoutException("timed out waiting for " + what);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting for " + what);
            await Task.Delay(25, Ct);
        }
    }
}

/// <summary>
/// The client side of the real composition (Core + Platform + Lsp + Remote, CSCODE_HOME in a temp dir, recording UI doubles) connected to a REAL
/// cscode-server child process behind an <c>ssh://tester@remote-box/</c> authority (loopback transport instead of ssh). Remote paths are paths of
/// this machine's temp directories.
/// </summary>
public sealed class RemoteTestHost : IAsyncDisposable
{
    public const string Host = "tester@remote-box";
    private TempDirectory _home = new("cscode-remote-client-");
    private readonly TempDirectory _serverData = new("cscode-remote-server-");
    private readonly string? _sharedServerData;

    /// <param name="loopback">false: the real ssh transport (settings from <paramref name="userSettings"/>, e.g. remote.ssh.configFile).</param>
    /// <param name="userSettings">The client's user settings.json (written to the temp CSCODE_HOME before anything reads it).</param>
    /// <param name="r3">Also compose the R3 consumers: the Dap module (debug service, task runner) and the Terminal module's service, with the
    /// remote debug factory/provider registered as the shell would.</param>
    /// <param name="persistent">R4: the loopback server is a persistent one (a relay to a daemon with this grace period).</param>
    /// <param name="machineSettings">The server host's machine settings.json (<see cref="CsCode.Server.ServerHost.MachineSettingsPath"/>): in a remote window,
    /// machine-scoped settings such as <c>cscode.debug.gdbArgs</c> come from there, not from the client's user settings.</param>
    public RemoteTestHost(Action<RemoteOptions>? options = null, Action<IServiceCollection>? services = null, Func<IRemoteTransport, IRemoteTransport>? wrap = null,
        bool loopback = true, string? userSettings = null, IWindowService? window = null, bool r3 = false, TimeSpan? persistent = null, TempDirectory? clientHome = null,
        string? serverDataDir = null, string? marker = null, string? machineSettings = null)
    {
        _sharedServerData = serverDataDir; // another (crashed) client's server: its data dir holds the persistent server's socket
        if (machineSettings is not null)
        {
            var machine = CsCode.Server.ServerHost.MachineSettingsPath(ServerDataDir);
            Directory.CreateDirectory(Path.GetDirectoryName(machine)!);
            File.WriteAllText(machine, machineSettings);
        }
        if (clientHome is not null)
        {
            _home.Dispose();
            _home = clientHome;
            _ownsHome = false;
        }
        if (userSettings is not null)
        {
            Directory.CreateDirectory(Path.Combine(_home.Path, "config"));
            File.WriteAllText(Path.Combine(_home.Path, "config", "settings.json"), userSettings);
        }
        Marker = marker ?? "cscode-test-" + Guid.NewGuid().ToString("N")[..12];
        Workspace = new TempDirectory("cscode-remote-ws-");
        IRemoteTransport Loopback()
        {
            var persist = persistent is { } grace ? (Manager.ServerSessionId ?? throw new InvalidOperationException("no server session id"), (int)Math.Max(1, grace.TotalSeconds)) : ((string, int)?)null;
            return new LoopbackTransport(TestEnv.ServerLaunch(ServerDataDir, Marker, persist));
        }
        var remoteOptions = new RemoteOptions
        {
            ServerMarker = Marker,
            GracePeriod = persistent ?? TimeSpan.Zero,
            TransportFactory = !loopback ? null : (_, _) => wrap is null ? Loopback() : wrap(Loopback()),
            SshEnvironment = new Dictionary<string, string?> { ["SSH_AUTH_SOCK"] = null, ["SSH_AGENT_PID"] = null },
            AskpassDirectory = _home.Path,
            Reconnect = new ReconnectPolicy(3, () => 0.5),
            CallTimeout = TimeSpan.FromSeconds(60),
        };
        options?.Invoke(remoteOptions);
        Recording = new RecordingWindowService();
        Window = window ?? Recording;
        Output = new InMemoryOutputService();
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddCsCodeCore();
        sc.AddSingleton(new AppPaths(name => name == AppPaths.HomeEnvironmentVariable ? _home.Path : Environment.GetEnvironmentVariable(name)));
        sc.AddSingleton<IAppPaths>(sp => sp.GetRequiredService<AppPaths>());
        sc.AddCsCodePlatform(o => { o.WatchDebounce = TimeSpan.FromMilliseconds(30); });
        sc.AddCsCodeLsp();
        sc.AddSingleton<IWindowService>(Window);
        sc.AddSingleton<IOutputService>(Output);
        sc.AddSingleton<IStatusBarService, InMemoryStatusBarService>();
        sc.AddSingleton<ICommandService, TestCommandService>();
        sc.AddSingleton<INavigationService>(sp => new RecordingNavigationService(sp.GetRequiredService<Core.Text.IDocumentManager>()));
        sc.AddSingleton(remoteOptions);
        if (r3)
        {
            sc.AddCsCodeDap();
            sc.AddCsCodeTerminalService();
        }
        sc.AddCsCodeRemote();
        services?.Invoke(sc);
        Services = sc.BuildServiceProvider();
        if (r3 && Services.GetService<Core.Debugging.IDebugService>() is { } debug)
            _r3 = Services.GetRequiredService<CsCode.Remote.Bridge.RemoteDebugging>().RegisterWith(debug);
    }

    private readonly IDisposable? _r3;
    private readonly bool _ownsHome = true;

    public string Marker { get; }
    public TempDirectory Workspace { get; }
    public ServiceProvider Services { get; }
    public IWindowService Window { get; }
    public RecordingWindowService Recording { get; }
    public InMemoryOutputService Output { get; }
    public string ServerDataDir => _sharedServerData ?? _serverData.Path;
    public string ClientHome => _home.Path;

    public RemoteConnectionManager Manager => Services.GetRequiredService<RemoteConnectionManager>();
    public IFileSystem FileSystem => Services.GetRequiredService<IFileSystem>();
    public IWorkspaceService WorkspaceService => Services.GetRequiredService<IWorkspaceService>();

    /// <summary>ssh: URI of a path inside the workspace temp dir.</summary>
    public Uri Remote(string relative = "") => Manager.ToUri(Path.Combine(Workspace.Path, relative).TrimEnd('/') + (relative.Length == 0 || relative.EndsWith('/') ? "/" : ""))!;

    public async Task ConnectAsync(string host = Host)
    {
        await Manager.ConnectAsync(host, TestEnv.Ct);
        Assert.Equal(RemoteConnectionState.Connected, Manager.Status.State);
    }

    public async Task OpenWorkspaceAsync() => await WorkspaceService.OpenFolderAsync(Remote(), TestEnv.Ct);

    public async ValueTask DisposeAsync()
    {
        _r3?.Dispose();
        await Services.DisposeAsync();
        await TestEnv.AssertNoProcessesAsync(Marker);
        Workspace.Dispose();
        _serverData.Dispose();
        if (_ownsHome) _home.Dispose();
    }

    public static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    /// <summary>The client's Remote log and the tail of the server's own log (failure messages).</summary>
    public string Dump()
    {
        var sb = new StringBuilder("--- Remote output ---\n").Append(Output.Text(RemoteConnectionManager.OutputChannel));
        var logs = Path.Combine(ServerDataDir, "logs");
        if (Directory.Exists(logs))
            foreach (var f in Directory.GetFiles(logs))
            {
                var lines = File.ReadAllLines(f);
                sb.Append("--- ").Append(Path.GetFileName(f)).Append(" ---\n").AppendJoin('\n', lines.TakeLast(60));
            }
        return sb.ToString();
    }
}
