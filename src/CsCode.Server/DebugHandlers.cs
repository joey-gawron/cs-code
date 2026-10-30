using System.Collections.Concurrent;
using CsCode.Core.Debugging;
using CsCode.Core.Tasks;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using CsCode.Dap.Service;
using CsCode.Dap.Transport;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CsCode.Server;

/// <summary>
/// R3 remote debugging: the debug adapter runs HERE, next to the program and its files. The client's debug service keeps its DAP client and UI; only
/// the adapter's byte stream crosses the connection, as a channel (resumable across reconnects). The built-in adapter factories of the Dap module run
/// on this machine (netcoredbg / js-debug / GDB found here; an installer's download prompt is forwarded to the client like any message), and
/// <c>dap/resolve</c> runs their configuration providers here, where <c>${workspaceFolder}</c> paths exist. Adapter servers (js-debug) can be
/// reached again by child sessions (<c>dap/connect</c>).
/// </summary>
internal sealed class DebugHandlers(IServiceProvider sp, ChannelHub hub, ILogger log)
{
    private readonly ConcurrentDictionary<long, (DapSource Source, DebugAdapterDescriptor? Endpoint)> _adapters = new();

    public int Count => _adapters.Count;

    public void Register(IRpcEndpoint link)
    {
        link.OnRequest<DapResolveParams, DapResolveResult>(RemoteMethods.DapResolve, ResolveAsync);
        link.OnRequest<DapStartParams, DapStartResult>(RemoteMethods.DapStart, StartAsync);
        link.OnRequest<DapConnectParams, DapStartResult>(RemoteMethods.DapConnect, ConnectAsync);
    }

    private BuiltinDebugAdapter? Builtin(string type) =>
        sp.GetServices<BuiltinDebugAdapter>().FirstOrDefault(b => string.Equals(b.Type, type, StringComparison.OrdinalIgnoreCase));

    private async ValueTask<DapResolveResult> ResolveAsync(DapResolveParams p, CancellationToken ct)
    {
        var config = p.Configuration;
        if (Builtin(config.Type)?.Provider is { } provider)
        {
            var resolved = await provider.ResolveAsync(p.Folder ?? config.WorkspaceFolder, config, ct).ConfigureAwait(false);
            return new DapResolveResult(resolved);
        }
        return new DapResolveResult(config);
    }

    private async ValueTask<DapStartResult> StartAsync(DapStartParams p, CancellationToken ct)
    {
        var config = p.Configuration;
        var descriptor = Builtin(config.Type) is { } builtin ? await builtin.Factory.CreateDescriptorAsync(config, ct).ConfigureAwait(false) : null;
        descriptor ??= await new ContributedAdapterFactory(sp.GetRequiredService<DapPlatform>()).CreateDescriptorAsync(config, ct).ConfigureAwait(false);
        if (descriptor is null)
            throw new NotSupportedException($"No debug adapter for type '{config.Type}' is available on the remote host.");
        return await OpenAsync(descriptor, config.Type, ct).ConfigureAwait(false);
    }

    private async ValueTask<DapStartResult> ConnectAsync(DapConnectParams p, CancellationToken ct)
    {
        if (!_adapters.TryGetValue(p.Parent, out var parent) || parent.Endpoint is not { } endpoint)
            throw new InvalidOperationException("The parent debug adapter is gone (or is not an adapter server).");
        return await OpenAsync(endpoint, "child", ct).ConfigureAwait(false);
    }

    private async ValueTask<DapStartResult> OpenAsync(DebugAdapterDescriptor descriptor, string what, CancellationToken ct)
    {
        var transport = await DapTransportFactory.OpenAsync(descriptor, log, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        var channel = hub.Create("debug");
        var source = new DapSource(channel, transport, log, () => _adapters.TryRemove(channel.Id, out _));
        var endpoint = DapTransportFactory.EndpointOf(transport);
        _adapters[channel.Id] = (source, endpoint);
        channel.Source = source;
        source.Start();
        log.LogInformation("Debug adapter {Id} ({What}): {Description}", channel.Id, what, transport.Description);
        return new DapStartResult(channel.Id, transport.ProcessId, transport.Description, endpoint is not null);
    }
}

/// <summary>
/// A debug adapter's DAP stream as a channel source: a reader thread copies the adapter's output into the channel (blocking there when the client is
/// a window behind: the adapter waits on its pipe), a writer thread feeds client bytes to the adapter from a bounded queue (the connection's reader
/// never waits on an adapter that stopped reading). When the adapter ends, the channel reports it with the tail of its stderr.
/// </summary>
internal sealed class DapSource(ServerChannel channel, IDapTransport transport, ILogger log, Action removed) : IChannelSource
{
    private const int MaxQueued = 4096;
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _toAdapter = new(MaxQueued);
    private int _disposed;

    public void Start()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = "CsCode.Server-dap-read-" + channel.Id }.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = "CsCode.Server-dap-write-" + channel.Id }.Start();
    }

    private void ReadLoop()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int n;
            while ((n = transport.Input.Read(buffer)) > 0) channel.Write(buffer.AsSpan(0, n));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        // The stream ended: the adapter exited (or closed its side). Give its exit a moment so the stderr tail is complete.
        try { transport.Completion.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
        var tail = transport.StandardErrorTail;
        channel.Complete(null, string.IsNullOrWhiteSpace(tail) ? null : tail.Length > 4000 ? tail[^4000..] : tail);
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var chunk in _toAdapter.GetConsumingEnumerable())
            {
                transport.Output.Write(chunk);
                transport.Output.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            log.LogDebug("Debug adapter {Id} stopped taking input: {Error}", channel.Id, ex.Message);
        }
    }

    public bool Accept(ReadOnlySpan<byte> data)
    {
        if (_toAdapter.IsAddingCompleted) return false;
        try { return _toAdapter.TryAdd(data.ToArray()); }
        catch (InvalidOperationException) { return false; }
    }

    public void Close(TimeSpan grace)
    {
        _toAdapter.CompleteAdding();
        _ = transport.CloseAsync(grace).AsTask();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        removed();
        _toAdapter.CompleteAdding();
        // Process trees are killed by the transport; bounded so a stuck adapter cannot hold the caller.
        try { transport.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException ex) { log.LogDebug(ex, "Disposing debug adapter {Id}", channel.Id); }
    }
}

/// <summary>
/// R3 remote tasks: tasks.json tasks of a remote folder run HERE through the Dap module's task runner (shell/process tasks, dependsOn, problem
/// matchers → this server's diagnostics, mirrored to the client; output → the "Tasks" channel, forwarded). A run outlives the connection that
/// started it: the client waits for it with <c>task/wait</c> and waits again after a reconnect to this (persistent) server.
/// </summary>
internal sealed class TaskHandlers(ITaskService tasks, ILogger log) : IDisposable
{
    private const int MaxRuns = 64;
    private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.Ordinal);

    private sealed class Run(Task<TaskResult> task, CancellationTokenSource cts)
    {
        public Task<TaskResult> Task { get; } = task;
        public CancellationTokenSource Cts { get; } = cts;
        public long FinishedAt;
    }

    public int Running => _runs.Values.Count(r => !r.Task.IsCompleted);

    public void Register(IRpcEndpoint link)
    {
        link.OnRequest<TaskListParams, IReadOnlyList<TaskDefinition>>(RemoteMethods.TaskList, async (p, ct) => await tasks.GetTasksAsync(p.Folder, ct).ConfigureAwait(false));
        link.OnRequest<TaskStartParams, TaskRunParams>(RemoteMethods.TaskStart, (p, _) => ValueTask.FromResult(Start(p)));
        link.OnRequest<TaskRunParams, TaskResultWire>(RemoteMethods.TaskWait, async (p, ct) =>
        {
            if (!_runs.TryGetValue(p.RunId, out var run)) throw new InvalidOperationException("The task run is unknown on the remote (the server restarted?).");
            var result = await run.Task.WaitAsync(ct).ConfigureAwait(false);
            Interlocked.Exchange(ref run.FinishedAt, Environment.TickCount64);
            return new TaskResultWire(result);
        });
        link.OnRequest<TaskRunParams, Empty>(RemoteMethods.TaskCancel, (p, _) =>
        {
            if (_runs.TryGetValue(p.RunId, out var run)) { try { run.Cts.Cancel(); } catch (ObjectDisposedException) { } }
            return ValueTask.FromResult(Empty.Value);
        });
    }

    private TaskRunParams Start(TaskStartParams p)
    {
        Prune();
        if (_runs.Count >= MaxRuns) throw new RemoteException(RpcErrorCodes.Busy, $"More than {MaxRuns} remote task runs");
        var id = Guid.NewGuid().ToString("N");
        var cts = new CancellationTokenSource();
        // The runner starts processes and reads tasks.json: off the connection's handler thread.
        var task = System.Threading.Tasks.Task.Run(async () => await tasks.RunAsync(p.Label, p.Folder, cts.Token).ConfigureAwait(false));
        _runs[id] = new Run(task, cts);
        log.LogInformation("Task '{Label}' started (run {Id})", p.Label, id);
        return new TaskRunParams(id);
    }

    /// <summary>Forgets runs whose result was collected a while ago (bounded bookkeeping).</summary>
    private void Prune()
    {
        var now = Environment.TickCount64;
        foreach (var (id, run) in _runs)
        {
            var finished = Interlocked.Read(ref run.FinishedAt);
            if (run.Task.IsCompleted && finished > 0 && now - finished > 60_000 && _runs.TryRemove(id, out var gone)) gone.Cts.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var run in _runs.Values) { try { run.Cts.Cancel(); } catch (ObjectDisposedException) { } }
        _runs.Clear();
    }
}
