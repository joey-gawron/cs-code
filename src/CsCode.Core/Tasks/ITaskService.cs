namespace CsCode.Core.Tasks;

/// <summary>One entry of a tasks.json (shell/process task) as the UI and the debugger need to see it.</summary>
/// <param name="Group">"build", "test" or null (tasks.json <c>group</c>, string or <c>{kind}</c> form).</param>
public sealed record TaskDefinition(string Label, string Type, string? Group = null, bool IsDefault = false, string? Detail = null)
{
    public bool IsDefaultBuild => IsDefault && string.Equals(Group, "build", StringComparison.OrdinalIgnoreCase);
}

/// <param name="ExitCode">Exit code of the task's own process (or of the first failing dependency); -1 when it never started.</param>
/// <param name="Error">Non-null when the task could not run at all (unknown label, dependency cycle, process start failure...).</param>
public sealed record TaskResult(string Label, int ExitCode, bool Cancelled = false, string? Error = null)
{
    public bool Succeeded => !Cancelled && Error is null && ExitCode == 0;
}

/// <summary>
/// Minimal tasks.json runner (shell and process tasks, dependsOn, variable substitution). Used by the debugger for
/// <c>preLaunchTask</c>/<c>postDebugTask</c>; the shell may bind "Run Task…" to it later. Implemented by CsCode.Dap.
/// </summary>
public interface ITaskService
{
    /// <summary>Tasks of <c>.cscode/tasks.json</c> (falling back to <c>.vscode/tasks.json</c>) in the folder (default: primary workspace folder).</summary>
    ValueTask<IReadOnlyList<TaskDefinition>> GetTasksAsync(Uri? folder = null, CancellationToken ct = default);

    /// <summary>
    /// Runs the task (and its dependsOn closure). Output goes to the "Tasks" output channel. Never throws for task failures
    /// (see <see cref="TaskResult"/>); cancelling <paramref name="ct"/> kills the whole process tree and yields <c>Cancelled = true</c>.
    /// </summary>
    ValueTask<TaskResult> RunAsync(string taskLabel, Uri? folder = null, CancellationToken ct = default);

    /// <summary>
    /// True when the task is running as many times as it may (tasks.json <c>runOptions.instanceLimit</c>, default 1): <see cref="RunAsync"/> then
    /// starts no second instance but waits for the running one (a background task: until it is idle), and a user's Run Task shows VS Code's
    /// "The task is already active" (Terminate Task / Restart Task). Returns at once (any thread). Additive (end pass): false by default.
    /// </summary>
    bool IsActive(string taskLabel, Uri? folder = null) => false;

    /// <summary>
    /// Ends every running instance of the task (its process tree; its terminal stays) and completes once they have ended, so a following
    /// <see cref="RunAsync"/> starts it afresh (Restart Task). Additive (end pass): nothing by default.
    /// </summary>
    ValueTask TerminateAsync(string taskLabel, Uri? folder = null, CancellationToken ct = default) => ValueTask.CompletedTask;
}
