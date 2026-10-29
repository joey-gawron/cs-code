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
}
