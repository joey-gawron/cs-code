using CsCode.Core.Lifecycle;

namespace CsCode.Core.Terminal;

/// <summary>
/// How to start a terminal. Everything is optional: an empty record starts the default profile's shell in the default directory
/// (<c>terminal.integrated.cwd</c>, else the workspace folder, else the home directory), as VS Code's "New Terminal" does.
/// </summary>
public sealed record TerminalCreateOptions
{
    /// <summary>Tab name. Null: the program's window title (OSC 0/2) or, until it sets one, the process name.</summary>
    public string? Name { get; init; }

    /// <summary>A profile of <c>terminal.integrated.profiles.&lt;os&gt;</c> by name. Ignored when <see cref="ShellPath"/> is set.</summary>
    public string? ProfileName { get; init; }

    /// <summary>The program to run (a shell, or any executable: a debuggee is spawned without a shell in between). Null: the profile's.</summary>
    public string? ShellPath { get; init; }

    /// <summary>Arguments for <see cref="ShellPath"/> (the profile's when both are null).</summary>
    public IReadOnlyList<string>? ShellArgs { get; init; }

    /// <summary>Working directory. Null: <c>terminal.integrated.cwd</c>, the workspace folder, else home.</summary>
    public string? Cwd { get; init; }

    /// <summary>Environment overrides on top of CsCode's environment and <c>terminal.integrated.env.&lt;os&gt;</c>; a null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?>? Env { get; init; }

    /// <summary>
    /// A terminal that a feature created for a job (a debuggee, a task): it is never restored, never asks for confirmation when it is closed,
    /// and with <see cref="ReuseWhenIdle"/> it is reused for the next job of the same <see cref="Name"/>.
    /// </summary>
    public bool IsTransient { get; init; }

    /// <summary>
    /// Run in the existing transient terminal of the same <see cref="Name"/> when its process has exited (its buffer stays, as VS Code's
    /// debug terminal is reused for the next run), instead of opening another one.
    /// </summary>
    public bool ReuseWhenIdle { get; init; }

    /// <summary>Bring the Terminal window forward (and make this terminal the active one). Default true.</summary>
    public bool Show { get; init; } = true;

    /// <summary>With <see cref="Show"/>: leave keyboard focus where it is (a debuggee started from the editor).</summary>
    public bool PreserveFocus { get; init; }

    /// <summary>Initial grid size until the view measures itself (columns, rows).</summary>
    public (int Columns, int Rows)? InitialSize { get; init; }
}

/// <summary>A terminal instance: a process on a pseudo-terminal plus its screen. Members are safe to call from any thread.</summary>
public interface ITerminal
{
    /// <summary>1-based instance id, unique for the lifetime of the service (VS Code's terminal numbers).</summary>
    int Id { get; }

    /// <summary>What the tab shows: the user's rename, else the creator's name, else the program's title, else the process name.</summary>
    string Title { get; }

    /// <summary>The process id of the program the terminal runs (the shell, or a debuggee started without a shell); null when it failed to start.</summary>
    int? ProcessId { get; }

    bool IsTransient { get; }

    /// <summary>True once the process has exited (the terminal keeps its buffer until it is killed/closed).</summary>
    bool HasExited { get; }

    /// <summary>The exit code (signals as 128 + n, as shells report them), once <see cref="HasExited"/>.</summary>
    int? ExitCode { get; }

    /// <summary>The working directory last reported by the shell (OSC 7), else the one it started in.</summary>
    string? CurrentDirectory { get; }

    /// <summary>
    /// Types <paramref name="text"/> into the terminal (as a paste would, without bracketed-paste markers), followed by Enter when
    /// <paramref name="addNewLine"/>. Never blocks: the bytes are queued for the terminal's writer thread.
    /// </summary>
    ValueTask SendTextAsync(string text, bool addNewLine = true, CancellationToken ct = default);

    /// <summary>
    /// Ends the terminal's program (SIGHUP to its process group, SIGKILL to its session after a grace period) and keeps the terminal with its
    /// buffer, as a debuggee's terminal stays after the debug session ended it. Returns at once.
    /// </summary>
    void KillProcess();
}

public sealed record TerminalExitedEventArgs(ITerminal Terminal, int? ExitCode);

/// <summary>
/// The integrated terminal (VS Code's <c>window.createTerminal</c> / <c>workbench.action.terminal.*</c> surface, without any UI types).
/// Process-free consumers — the Explorer's "Open in Terminal", the debugger's <c>runInTerminal</c>, tasks and the remote module later — use
/// only this contract. Implemented by <c>CsCode.Terminal</c>; the Terminal tool window (<c>CsCode.Panels.Terminal</c>) presents it.
/// <para>
/// Threading: every member may be called from any thread and returns without waiting for a process (spawning happens off the caller's
/// thread). Events fire on a background thread. Terminals are not restored after a restart.
/// </para>
/// </summary>
public interface ITerminalService
{
    /// <summary>Open terminals in creation order (exited ones included until they are killed).</summary>
    IReadOnlyList<ITerminal> Terminals { get; }

    /// <summary>The terminal the Terminal window shows (and keyboard input goes to), or null.</summary>
    ITerminal? ActiveTerminal { get; }

    /// <summary>
    /// Starts a terminal. The returned task completes once the process was spawned (<see cref="ITerminal.ProcessId"/> is known), or with the
    /// failed terminal (it shows the error, <see cref="ITerminal.HasExited"/> true) when the program could not be started.
    /// </summary>
    ValueTask<ITerminal> CreateAsync(TerminalCreateOptions? options = null, CancellationToken ct = default);

    /// <summary>Makes <paramref name="terminal"/> the active one (null: keep the current) and brings the Terminal window forward.</summary>
    ValueTask ShowAsync(ITerminal? terminal = null, bool preserveFocus = false, CancellationToken ct = default);

    /// <summary>Makes <paramref name="terminal"/> the active one without showing the window.</summary>
    void SetActive(ITerminal terminal);

    /// <summary>Sends text to the active terminal, creating one first when there is none (VS Code's <c>sendSequence</c>/<c>runSelectedText</c>).</summary>
    ValueTask SendTextAsync(string text, bool addNewLine = true, CancellationToken ct = default);

    /// <summary>Kills the terminal's process group (SIGHUP, then SIGKILL after a grace period) and removes the terminal.</summary>
    ValueTask KillAsync(ITerminal terminal, CancellationToken ct = default);

    IEventSource<ITerminal> Created { get; }

    /// <summary>The process exited (the terminal stays open, showing its buffer and the exit code).</summary>
    IEventSource<TerminalExitedEventArgs> Exited { get; }

    /// <summary>The terminal was killed/closed and is no longer in <see cref="Terminals"/>.</summary>
    IEventSource<ITerminal> Removed { get; }

    IEventSource<ITerminal> TitleChanged { get; }

    IEventSource<ITerminal?> ActiveChanged { get; }
}

/// <summary>Command ids of the terminal (VS Code's), for menus and other modules.</summary>
public static class TerminalCommands
{
    public const string New = "workbench.action.terminal.new";
    public const string NewHere = "workbench.action.terminal.newHere";
    public const string Toggle = "workbench.action.terminal.toggleTerminal";
    public const string Focus = "workbench.action.terminal.focus";
    public const string Kill = "workbench.action.terminal.kill";
    public const string SendSequence = "workbench.action.terminal.sendSequence";
    public const string RunSelectedText = "workbench.action.terminal.runSelectedText";
    public const string RunActiveFile = "workbench.action.terminal.runActiveFile";
}
