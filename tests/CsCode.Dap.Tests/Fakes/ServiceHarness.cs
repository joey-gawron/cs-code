using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CsCode.Core.Contributions;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Dap.Adapters;
using CsCode.Dap.Breakpoints;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Service;
using CsCode.Dap.Tasks;
using CsCode.TestDoubles;

namespace CsCode.Dap.Tests.Fakes;

/// <summary>
/// Serves a fresh <see cref="FakeDebugAdapter"/> per TCP connection on an ephemeral loopback port, so DebugService tests run the REAL
/// TcpTransport + client + session code against scripted adapters.
/// </summary>
public sealed class TcpFakeAdapterHost : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<FakeDebugAdapter> _adapters = new();
    private readonly List<Socket> _sockets = new();
    private readonly Task _acceptLoop;

    public int Port { get; }
    /// <summary>Applied to every adapter at connection time (before it sees the first request).</summary>
    public Action<FakeDebugAdapter, int>? Configure { get; set; }
    public IReadOnlyList<FakeDebugAdapter> Adapters { get { lock (_adapters) return [.. _adapters]; } }

    public TcpFakeAdapterHost()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var socket = await _listener.AcceptSocketAsync(_cts.Token);
                socket.NoDelay = true;
                var adapter = new FakeDebugAdapter();
                int index;
                lock (_adapters) { _adapters.Add(adapter); _sockets.Add(socket); index = _adapters.Count - 1; }
                Configure?.Invoke(adapter, index);
                _ = Bridge(socket, adapter);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task Bridge(Socket socket, FakeDebugAdapter adapter)
    {
        var net = new NetworkStream(socket, ownsSocket: false);
        var inbound = Task.Run(async () =>
        {
            var buf = new byte[16384];
            try
            {
                int n;
                while ((n = await net.ReadAsync(buf, _cts.Token)) > 0) await adapter.Transport.Output.WriteAsync(buf.AsMemory(0, n), _cts.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            await adapter.Transport.CloseAsync(TimeSpan.Zero);
        });
        var outbound = Task.Run(async () =>
        {
            var buf = new byte[16384];
            try
            {
                int n;
                while ((n = await adapter.Transport.Input.ReadAsync(buf, _cts.Token)) > 0) await net.WriteAsync(buf.AsMemory(0, n), _cts.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            try { socket.Shutdown(SocketShutdown.Send); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        });
        await Task.WhenAll(inbound, outbound);
        // Both directions are done: release the server-side socket now (a real adapter process exiting does exactly this).
        lock (_adapters) _sockets.Remove(socket);
        try { net.Dispose(); socket.Dispose(); } catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        Socket[] sockets;
        lock (_adapters) sockets = [.. _sockets];
        foreach (var s in sockets) { try { s.Close(); } catch (SocketException) { } catch (ObjectDisposedException) { } }
        try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        foreach (var a in Adapters) await a.DisposeAsync();
    }
}

public sealed class FixedPortAdapterFactory(int port) : IDebugAdapterFactory
{
    public int Calls;
    public ValueTask<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration configuration, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return ValueTask.FromResult<DebugAdapterDescriptor?>(new DebugAdapterServer("127.0.0.1", port));
    }
}

/// <summary>Everything wired like the app does it, minus the UI: real services, fake platform pieces, fake adapter over real TCP.</summary>
public sealed class ServiceHarness : IAsyncDisposable
{
    public TempDirectory Dir { get; } = new("dapsvc-");
    public DebugLane Lane { get; }
    public TcpFakeAdapterHost Host { get; } = new();
    public FakeWorkspace Workspace { get; }
    public FakeStateStorage State { get; } = new();
    public TestConfigurationService Settings { get; } = new();
    public RecordingWindowService Window { get; } = new();
    public TestCommandService Commands { get; } = new();
    public TestContextKeyService ContextKeys { get; } = new();
    public InMemoryOutputService Output { get; } = new();
    public ContributionRegistry Contributions { get; } = new();
    public TestAppPaths Paths { get; } = new();
    public DapPlatform Platform { get; }
    public BreakpointManager Breakpoints { get; }
    public TaskService Tasks { get; }
    public DebugConfigurationService Configurations { get; }
    public DebugService Service { get; }
    public FixedPortAdapterFactory FakeFactory { get; }

    public ServiceHarness(DebugLane? lane = null, INavigationService? navigation = null, bool withPlatform = true, Action<TcpFakeAdapterHost>? configureHost = null,
        Func<DapPlatform, IEnumerable<BuiltinDebugAdapter>>? moreBuiltins = null)
    {
        Lane = lane ?? new DebugLane("svc-lane");
        configureHost?.Invoke(Host);
        Workspace = new FakeWorkspace(Dir.Uri);
        Platform = new DapPlatform
        {
            Workspace = Workspace, State = withPlatform ? State : null, Configuration = withPlatform ? Settings : null,
            Window = withPlatform ? Window : null, Commands = withPlatform ? Commands : null, ContextKeys = withPlatform ? ContextKeys : null,
            Output = withPlatform ? Output : null, Contributions = withPlatform ? Contributions : null, Paths = withPlatform ? Paths : null,
            Navigation = navigation,
        };
        Breakpoints = new BreakpointManager(Platform.State, Workspace);
        Tasks = new TaskService(Lane, Platform);
        Configurations = new DebugConfigurationService(Lane, Platform, () => Tasks);
        FakeFactory = new FixedPortAdapterFactory(Host.Port);
        var netcore = new NetCoreDbgAdapterFactory(Platform);
        Service = new DebugService(Lane, Platform, Breakpoints, Configurations, Tasks,
            [new BuiltinDebugAdapter("coreclr", ".NET (netcoredbg)", ["csharp", "fsharp", "vb"], netcore, netcore), .. moreBuiltins?.Invoke(Platform) ?? []]);
        Service.RegisterAdapterFactory("fake", FakeFactory);
    }

    public static DebugConfiguration Config(string name = "Test", string type = "fake", string request = "launch", IDictionary<string, object?>? extra = null, Uri? folder = null)
    {
        var d = new Dictionary<string, object?> { ["type"] = type, ["request"] = request, ["name"] = name };
        if (extra is not null) foreach (var (k, v) in extra) d[k] = v;
        return DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(d), folder);
    }

    public async Task<IDebugSession> StartAsync(DebugConfiguration? config = null)
    {
        var s = await Service.StartAsync(config ?? Config(folder: Dir.Uri), CancellationToken.None);
        return s ?? throw new InvalidOperationException("session did not start; messages: " + string.Join(" | ", Window.Messages.Select(m => m.Message)));
    }

    public FakeDebugAdapter AdapterFor(int index) => Host.Adapters[index];

    public async Task WaitForAdapters(int count) => await SessionHarness.Eventually(() => Host.Adapters.Count >= count, 5000, $"{count} adapter connection(s)");

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await Service.DisposeAsync();
        await Configurations.DisposeAsync();
        await Breakpoints.DisposeAsync();
        await Host.DisposeAsync();
        Lane.Dispose();
        Paths.Dispose();
        Dir.Dispose();
    }
}
