namespace CsCode.Core.Tasks;

/// <summary>tasks.json <c>presentation.reveal</c>: when the terminal of a task comes forward.</summary>
public enum TaskRevealKind { Always, Silent, Never }

/// <summary>tasks.json <c>presentation.panel</c>: which terminal a task runs in.</summary>
public enum TaskPanelKind { Shared, Dedicated, New }

/// <summary>tasks.json <c>presentation.revealProblems</c>: when the Error List comes forward after the task.</summary>
public enum TaskRevealProblemsKind { Never, OnProblem, Always }

/// <summary>
/// VS Code's <c>presentation</c> options of a task (defaults as VS Code's): <see cref="Reveal"/> always (silent: only when the task fails or
/// its problem matchers find errors), <see cref="Panel"/> shared (an idle task terminal is reused; dedicated: the one this task ran in last;
/// new: a fresh one each time), <see cref="Echo"/> the command line, <see cref="ShowReuseMessage"/>, <see cref="Clear"/> the terminal first,
/// <see cref="Close"/> it when the task ends, tasks of the same <see cref="Group"/> split into one terminal group.
/// </summary>
public sealed record TaskPresentation
{
    public TaskRevealKind Reveal { get; init; } = TaskRevealKind.Always;
    public TaskRevealProblemsKind RevealProblems { get; init; } = TaskRevealProblemsKind.Never;
    /// <summary>The terminal takes the keyboard when it is revealed.</summary>
    public bool Focus { get; init; }
    public bool Echo { get; init; } = true;
    public bool ShowReuseMessage { get; init; } = true;
    public TaskPanelKind Panel { get; init; } = TaskPanelKind.Shared;
    public bool Clear { get; init; }
    public bool Close { get; init; }
    public string? Group { get; init; }
}

/// <summary>One run of a task's process in a terminal: what to start (no shell is added: a shell task's shell is <see cref="FileName"/>) and how to present it.</summary>
/// <param name="Label">The task's label: the terminal is named after it.</param>
/// <param name="Environment">Variables on top of the terminal's own environment (a null value removes one).</param>
public sealed record TaskTerminalRequest(string Label, string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment, TaskPresentation Presentation)
{
    /// <summary>The command line shown before the output ("&gt; Executing task: ... &lt;") when <see cref="TaskPresentation.Echo"/>.</summary>
    public string? CommandLine { get; init; }

    /// <summary>
    /// Receives the program's output as it arrives (decoded UTF-8, escape sequences included), in order, on a background thread; it must
    /// return at once (hand the text to a lane). Problem matchers read the task's output through it.
    /// </summary>
    public Action<string>? Output { get; init; }
}

/// <param name="ExitCode">The process's exit code (signals as 128 + n); -1 when it could not be started.</param>
/// <param name="Terminated">The run was ended by <see cref="ITaskTerminalRun.Terminate"/> or by closing its terminal.</param>
public sealed record TaskTerminalExit(int ExitCode, bool Terminated);

/// <summary>A task's process running in a terminal.</summary>
public interface ITaskTerminalRun
{
    /// <summary>Completes when the process has exited (or its terminal was closed); never faults.</summary>
    Task<TaskTerminalExit> Completion { get; }

    /// <summary>Ends the process (its process group: SIGHUP, then SIGKILL); the terminal stays with its output. Returns at once.</summary>
    void Terminate();

    /// <summary>Brings the task's terminal forward without taking the keyboard (<c>reveal: silent</c> after a failure).</summary>
    ValueTask RevealAsync(CancellationToken ct = default);
}

/// <summary>
/// The presentation seam of the task runner (additive, wave 3): when the integrated terminal is composed, <see cref="ITaskService"/> runs a
/// task's process in a terminal named after the task, following the task's <see cref="TaskPresentation"/>, instead of piping its output
/// into the "Tasks" output channel. Implemented by <c>CsCode.Terminal</c>. Every member may be called from any thread.
/// </summary>
public interface ITaskTerminalHost
{
    /// <summary>Picks (or creates) the task's terminal and starts the process in it. Completes once the process was spawned.</summary>
    ValueTask<ITaskTerminalRun> StartAsync(TaskTerminalRequest request, CancellationToken ct = default);
}
