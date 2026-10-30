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
/// diagnostics (mirrored into the Error List), output goes to the forwarded "Remote: Tasks" channel. A run survives a short loss of the connection:
/// the client waits for it again on the same (persistent) server. Cancelling kills the task's process tree on the server. Local folders use the
/// local runner unchanged.
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
