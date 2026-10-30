using System.Collections.Concurrent;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Core.Tasks;
using CsCode.Remote.Client;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Remote.Bridge;

/// <summary>
/// R3 remote tasks: wraps the local <see cref="ITaskService"/> (the Dap module's runner). For a remote folder (the given one, else the workspace's
/// primary folder) the server lists and runs the tasks of its tasks.json — processes start next to the files, problem matchers fill the server's
/// diagnostics (mirrored into the Error List). The process runs in a terminal named after the task, on a server PTY, when this client composes the
/// integrated terminal (<see cref="RemoteTaskTerminals"/>, VS Code's presentation), else its output goes to the forwarded "Remote: Tasks" channel.
/// A run survives a short loss of the connection: the client waits for it again on the same (persistent) server. Cancelling kills the task's
/// process tree on the server (also when it happens while the connection is down: <see cref="RemoteTaskRuns"/>). Local folders use the local
/// runner unchanged.
/// </summary>
public sealed class RemoteTaskService(ITaskService local, RemoteConnectionManager manager, Func<IWorkspaceService?> workspace, ILogger? log = null) : ITaskService
{
    private readonly ILogger _log = log ?? NullLogger.Instance;

    public ITaskService Local => local;

    private Uri? RemoteFolder(Uri? folder)
    {
        var f = folder ?? workspace()?.PrimaryFolder;
        return f is not null && manager.MapperFor(f) is not null ? f : null;
    }

    public async ValueTask<IReadOnlyList<TaskDefinition>> GetTasksAsync(Uri? folder = null, CancellationToken ct = default)
    {
        if (RemoteFolder(folder) is not { } remote) return await local.GetTasksAsync(folder, ct).ConfigureAwait(false);
        var session = await manager.WaitForSessionAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return await session.Peer.InvokeAsync<TaskListParams, IReadOnlyList<TaskDefinition>>(RemoteMethods.TaskList, new TaskListParams(remote), ct).ConfigureAwait(false) ?? [];
    }

    public async ValueTask<TaskResult> RunAsync(string taskLabel, Uri? folder = null, CancellationToken ct = default)
    {
        if (RemoteFolder(folder) is not { } remote) return await local.RunAsync(taskLabel, folder, ct).ConfigureAwait(false);
        RemoteSession session;
        TaskRunParams run;
        try
        {
            session = await manager.WaitForSessionAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (!session.Server.Capabilities.Contains(RemoteProtocol.Capabilities.Tasks))
                return new TaskResult(taskLabel, -1, Error: $"The server on {manager.HostName} cannot run tasks (reinstall it).");
            run = await session.Peer.InvokeAsync<TaskStartParams, TaskRunParams>(RemoteMethods.TaskStart, new TaskStartParams(taskLabel, remote), ct).ConfigureAwait(false)
                  ?? throw new IOException("The server did not start the task.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new TaskResult(taskLabel, -1, Cancelled: true); }
        catch (IOException ex) { return new TaskResult(taskLabel, -1, Error: ex.Message); }

        // A cancel while the connection is down cannot be sent: the run is no longer claimed (task/keep) when the client reconnects, so the server
        // cancels it then. The same covers a cancel sent on a link that had died unnoticed.
        manager.TaskRuns.Add(run.RunId);
        using var claimed = Core.Lifecycle.Disposable.Create(() => manager.TaskRuns.Remove(run.RunId));
        using var cancel = ct.Register(() => manager.Session?.Peer.Fire(RemoteMethods.TaskCancel, run));
        var instance = session.Server.InstanceId;
        while (true)
        {
            try
            {
                var r = await session.Peer.InvokeAsync<TaskRunParams, TaskResultWire>(RemoteMethods.TaskWait, run, ct, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                return r?.Result ?? new TaskResult(taskLabel, -1, Error: "The server sent no result.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new TaskResult(taskLabel, -1, Cancelled: true); }
            catch (RemoteDisconnectedException ex)
            {
                // The connection dropped while the task ran: it keeps running on a persistent server; wait for it there again.
                _log.LogInformation("Connection lost while task '{Label}' ran; waiting for the reconnect: {Error}", taskLabel, ex.Message);
                try { session = await manager.WaitForSessionAsync(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new TaskResult(taskLabel, -1, Cancelled: true); }
                catch (Exception wait) when (wait is IOException or TimeoutException) { return new TaskResult(taskLabel, -1, Error: "The connection to the remote was lost: " + wait.Message); }
                if (session.Server.InstanceId != instance)
                    return new TaskResult(taskLabel, -1, Error: "The connection to the remote was lost and the server did not keep the task.");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { return new TaskResult(taskLabel, -1, Error: ex.Message); }
        }
    }
}

/// <summary>
/// T49, the client side of remote tasks in remote terminals (VS Code's behaviour): the server's task runner asks for a terminal for a task
/// (<c>task/terminal</c>) and this client's own task presentation (<see cref="ITaskTerminalHost"/>: a terminal named after the task, reused,
/// revealed, the command line echoed) opens one. The request is tagged, so that terminal always runs on the server (never on this machine) and the
/// server connects its PTY to the task (output to the problem matchers there, exit to the result). Closing the terminal ends the task there.
/// </summary>
public sealed class RemoteTaskTerminals(Func<ITaskTerminalHost?> host, ILogger? log = null) : IRemoteParticipant
{
    private const int MaxTracked = 256;
    private readonly ConcurrentDictionary<string, ITaskTerminalRun> _runs = new(StringComparer.Ordinal);
    private readonly ILogger _log = log ?? NullLogger.Instance;

    /// <summary>Task terminals whose program still runs (bookkeeping, tests).</summary>
    public int Count => _runs.Count;

    void IRemoteParticipant.Attach(RpcPeer peer)
    {
        peer.OnRequest<TaskTerminalParams, Empty>(RemoteMethods.TaskTerminal, async (p, ct) =>
        {
            var terminals = host() ?? throw new NotSupportedException("This client cannot show tasks in terminals.");
            var env = new Dictionary<string, string?>(p.Environment, StringComparer.Ordinal) { [TaskTerminalTag.Variable] = p.Tag };
            var run = await terminals.StartAsync(new TaskTerminalRequest(p.Label, p.FileName, p.Arguments, p.WorkingDirectory, env, p.Presentation)
                { CommandLine = p.CommandLine }, ct).ConfigureAwait(false);
            if (_runs.Count < MaxTracked)
            {
                _runs[p.Tag] = run;
                _ = run.Completion.ContinueWith(_ => _runs.TryRemove(p.Tag, out var _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            return Empty.Value;
        });
        peer.OnNotification<TaskTagParams>(RemoteMethods.TaskReveal, p =>
        {
            if (_runs.TryGetValue(p.Tag, out var run)) _ = RevealAsync(run);
        });
    }

    private async Task RevealAsync(ITaskTerminalRun run)
    {
        try { await run.RevealAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException) { _log.LogDebug("Revealing a remote task's terminal failed: {Error}", ex.Message); }
    }

    Task IRemoteParticipant.OnConnectedAsync(RemoteSession session, CancellationToken ct) => Task.CompletedTask;

    void IRemoteParticipant.OnDisconnected() { }
}

/// <summary>
/// The remote task runs this client still waits for. After every (re)connect the server is told which ones they are (<c>task/keep</c>) and cancels
/// every other run: one cancelled while the connection was down (its <c>task/cancel</c> could not be sent), and every run of a previous client
/// process of the session (a restarted client reattaching to the server a crashed one left running). Thread-safe.
/// </summary>
public sealed class RemoteTaskRuns(ILogger? log = null) : IRemoteParticipant
{
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);
    private readonly ILogger _log = log ?? NullLogger.Instance;

    public int Count => _active.Count;

    internal void Add(string runId) => _active[runId] = 0;

    internal void Remove(string runId) => _active.TryRemove(runId, out _);

    void IRemoteParticipant.Attach(RpcPeer peer) { }

    async Task IRemoteParticipant.OnConnectedAsync(RemoteSession session, CancellationToken ct)
    {
        if (!session.Server.Capabilities.Contains(RemoteProtocol.Capabilities.Tasks)) return;
        try { await session.Peer.InvokeAsync<TaskKeepParams, Empty>(RemoteMethods.TaskKeep, new TaskKeepParams(_active.Keys.ToArray()), ct).ConfigureAwait(false); }
        catch (RemoteException ex) when (ex.Code == RpcErrorCodes.MethodNotFound) { _log.LogDebug("The server cannot cancel unclaimed task runs: {Error}", ex.Message); }
    }

    void IRemoteParticipant.OnDisconnected() { }
}
