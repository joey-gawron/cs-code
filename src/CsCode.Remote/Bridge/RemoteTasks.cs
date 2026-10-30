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

    /// <summary>A local folder's task: the local runner knows (a remote folder's tasks are the server's: not tracked here).</summary>
    public bool IsActive(string taskLabel, Uri? folder = null) => RemoteFolder(folder) is null && local.IsActive(taskLabel, folder);

    public ValueTask TerminateAsync(string taskLabel, Uri? folder = null, CancellationToken ct = default) =>
        RemoteFolder(folder) is null ? local.TerminateAsync(taskLabel, folder, ct) : ValueTask.CompletedTask;

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
        Task<TaskRunParams?>? starting = null;
        try
        {
            session = await manager.WaitForSessionAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (!session.Server.Capabilities.Contains(RemoteProtocol.Capabilities.Tasks))
                return new TaskResult(taskLabel, -1, Error: $"The server on {manager.HostName} cannot run tasks (reinstall it).");
            // Not cancelled with ct: once sent, the server starts the run whatever happens to the request, so its answer is still wanted.
            starting = session.Peer.InvokeAsync<TaskStartParams, TaskRunParams>(RemoteMethods.TaskStart, new TaskStartParams(taskLabel, remote), CancellationToken.None);
            run = await starting.WaitAsync(ct).ConfigureAwait(false) ?? throw new IOException("The server did not start the task.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled before the run's id came back: the server may have started it, and nobody would ever cancel it (its terminal would run
            // on unseen). It is cancelled as soon as the id arrives; if the connection drops first, task/keep cancels it after the reconnect.
            _ = starting?.ContinueWith(t =>
            {
                if (t.Result is { } started) manager.Session?.Peer.Fire(RemoteMethods.TaskCancel, started);
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new TaskResult(taskLabel, -1, Cancelled: true);
        }
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
    private readonly ConcurrentDictionary<string, Task<ITaskTerminalRun>> _starts = new(StringComparer.Ordinal);
    private readonly ILogger _log = log ?? NullLogger.Instance;

    /// <summary>Task terminals started or starting whose program still runs (bookkeeping, tests).</summary>
    public int Count => _starts.Count;

    void IRemoteParticipant.Attach(RpcPeer peer)
    {
        peer.OnRequest<TaskTerminalParams, Empty>(RemoteMethods.TaskTerminal, async (p, ct) =>
        {
            // The server asks again when the connection went away with its request or the answer: the start already made (or still being made)
            // answers it, unless its terminal could not start (then this is the retry).
            while (true)
            {
                var created = new Lazy<Task<ITaskTerminalRun>>(() => StartAsync(p));
                var start = _starts.Count < MaxTracked ? _starts.GetOrAdd(p.Tag, _ => created.Value) : created.Value;
                ITaskTerminalRun run;
                try { run = await start.WaitAsync(ct).ConfigureAwait(false); }
                catch (Exception) when (!ct.IsCancellationRequested && !created.IsValueCreated && start.IsCompleted)
                {
                    _starts.TryRemove(new KeyValuePair<string, Task<ITaskTerminalRun>>(p.Tag, start)); // an earlier attempt failed: start again
                    continue;
                }
                if (!created.IsValueCreated && run.Completion is { IsCompleted: true, Result.ExitCode: -1 })
                {
                    _starts.TryRemove(new KeyValuePair<string, Task<ITaskTerminalRun>>(p.Tag, start)); // its terminal did not start: start again
                    continue;
                }
                return Empty.Value;
            }
        });
        peer.OnNotification<TaskTagParams>(RemoteMethods.TaskReveal, p =>
        {
            if (_starts.TryGetValue(p.Tag, out var start) && start.IsCompletedSuccessfully) _ = RevealAsync(start.Result);
        });
    }

    /// <summary>
    /// Opens the task's terminal (not bound to the asking connection: a start under way when the connection drops goes on, and the server's next
    /// request for the same tag waits for it). The terminal's program runs on the server: the tag goes into its <c>term/create</c>.
    /// </summary>
    private Task<ITaskTerminalRun> StartAsync(TaskTerminalParams p)
    {
        var tag = p.Tag;
        var start = Task.Run(async () =>
        {
            var terminals = host() ?? throw new NotSupportedException("This client cannot show tasks in terminals.");
            var env = new Dictionary<string, string?>(p.Environment, StringComparer.Ordinal) { [TaskTerminalTag.Variable] = tag };
            var run = await terminals.StartAsync(new TaskTerminalRequest(p.Label, p.FileName, p.Arguments, p.WorkingDirectory, env, p.Presentation)
                { CommandLine = p.CommandLine }).ConfigureAwait(false);
            // Forgotten when its program ends (a later request for the tag is a stale one).
            _ = run.Completion.ContinueWith(_ => Forget(tag), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return run;
        });
        _ = start.ContinueWith(_ => Forget(tag), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return start;
    }

    private void Forget(string tag)
    {
        // Kept a little while after the end, so a repeated request that crossed the end does not start the task a second time.
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => _starts.TryRemove(tag, out var _), TaskScheduler.Default);
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
