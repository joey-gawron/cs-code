using System.Diagnostics;
using System.Text.Json.Nodes;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.Dap.Session;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Tasks;

/// <summary>
/// The task runner's terminal presentation (wave 3, additive): with an <see cref="ITaskTerminalHost"/> composed, a task's process runs in a
/// terminal named after the task, as its tasks.json <c>presentation</c> says (VS Code's options and defaults), instead of being piped into the
/// "Tasks" output channel (which then only logs start and end). The program is the same either way (a shell task: the shell with its command
/// line). Problem matchers read the task's output from the terminal (escape sequences removed) and publish to the Error List;
/// <c>reveal: silent</c> brings the terminal forward when the task fails or its matchers found errors, <c>revealProblems</c> the Error List.
/// Cancelling the run, or the app shutting down, ends the process; closing its terminal cancels the task.
/// </summary>
public sealed partial class TaskService
{
    private readonly HashSet<ITaskTerminalRun> _terminalRuns = new();

    private void TerminateTerminalRuns()
    {
        ITaskTerminalRun[] runs;
        lock (_terminalRuns) runs = [.. _terminalRuns];
        foreach (var r in runs) r.Terminate();
    }

    /// <summary>Completes once everything queued on the lane so far (output flushes) has run.</summary>
    private Task DrainLaneAsync()
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lane.Post(() => drained.TrySetResult());
        return drained.Task;
    }

    /// <summary>The task's problem matchers, their earlier problems cleared (null: none, or no diagnostics service).</summary>
    private ProblemCollector? StartProblemMatchers(TaskModel task, string? workspaceFolder, string? cwd)
    {
        if (_p.Diagnostics is not { } diagnostics || task.ProblemMatcher is null) return null;
        var matchers = ProblemMatchers.Parse(task.ProblemMatcher);
        if (matchers.Count == 0) return null;
        var collector = new ProblemCollector(matchers, diagnostics, workspaceFolder, cwd);
        collector.Start();
        return collector;
    }

    /// <summary>The top-level <c>presentation</c> of tasks.json with the task's own on top (VS Code merges them so).</summary>
    private static JsonObject? MergePresentation(JsonObject? global, JsonObject? task)
    {
        if (global is null && task is null) return null;
        var merged = global?.DeepClone() as JsonObject ?? new JsonObject();
        if (task is not null)
            foreach (var (k, v) in task) merged[k] = v?.DeepClone();
        return merged;
    }

    /// <summary>VS Code's <c>presentation</c> object (unknown values keep the defaults).</summary>
    internal static TaskPresentation PresentationOf(JsonObject? o)
    {
        var p = new TaskPresentation();
        if (o is null) return p;
        string? S(string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        bool? B(string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        return p with
        {
            Reveal = S("reveal") switch { "silent" => TaskRevealKind.Silent, "never" => TaskRevealKind.Never, _ => TaskRevealKind.Always },
            RevealProblems = S("revealProblems") switch { "onProblem" => TaskRevealProblemsKind.OnProblem, "always" => TaskRevealProblemsKind.Always, _ => TaskRevealProblemsKind.Never },
            Focus = B("focus") ?? p.Focus,
            Echo = B("echo") ?? p.Echo,
            ShowReuseMessage = B("showReuseMessage") ?? p.ShowReuseMessage,
            Panel = S("panel") switch { "dedicated" => TaskPanelKind.Dedicated, "new" => TaskPanelKind.New, _ => TaskPanelKind.Shared },
            Clear = B("clear") ?? p.Clear,
            Close = B("close") ?? p.Close,
            Group = S("group") is { Length: > 0 } g ? g : null,
        };
    }

    private async Task<TaskResult> ExecuteInTerminalAsync(ITaskTerminalHost host, TaskModel task, ProcessStartInfo psi, IReadOnlyDictionary<string, string?> env,
        IOutputChannel? channel, ProblemCollector? problems, CancellationToken ct)
    {
        var presentation = PresentationOf(task.Presentation);
        var args = psi.ArgumentList.ToArray();
        // what the user wrote: a shell task's command line (the shell's last argument), a process task's program and arguments
        var commandLine = string.Equals(task.Type, "process", StringComparison.OrdinalIgnoreCase) || args.Length == 0
            ? string.Join(' ', new[] { psi.FileName }.Concat(args.Select(ShellQuote)))
            : args[^1];
        var pump = problems is null ? null : new BoundedTextPump(_lane, problems.Append);
        var request = new TaskTerminalRequest(task.Label, psi.FileName, args, psi.WorkingDirectory is { Length: > 0 } wd ? wd : null, env, presentation)
        {
            CommandLine = commandLine,
            Output = pump is null ? null : text => pump.Append(text),
        };
        channel?.AppendLine($"> Executing task in a terminal: {task.Label} <");
        ITaskTerminalRun run;
        try { run = await host.StartAsync(request, ct); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return new TaskResult(task.Label, -1, Error: $"The task '{task.Label}' could not be started in a terminal: {ex.Message}");
        }
        lock (_terminalRuns) _terminalRuns.Add(run);
        try
        {
            if (Volatile.Read(ref _disposed) != 0) run.Terminate(); // shut down while it was starting
            TaskTerminalExit exit;
            using (ct.Register(run.Terminate)) exit = await run.Completion;
            if (pump is not null)
            {
                // the output's last flush is queued on this lane: let it run before the last line is matched
                pump.Complete();
                await DrainLaneAsync();
                problems!.Complete();
            }
            if (exit.Terminated || ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
            {
                channel?.AppendLine($"> Task '{task.Label}' was cancelled.");
                return new TaskResult(task.Label, -1, Cancelled: true);
            }
            if (presentation.Reveal == TaskRevealKind.Silent && (exit.ExitCode != 0 || problems is { Errors: > 0 })) await run.RevealAsync(CancellationToken.None);
            await RevealProblemsAsync(task, problems);
            channel?.AppendLine($"> Task '{task.Label}' finished with exit code {exit.ExitCode}.");
            return exit.ExitCode == -1
                ? new TaskResult(task.Label, -1, Error: $"Could not start '{psi.FileName}' (the terminal '{task.Label}' shows why).")
                : new TaskResult(task.Label, exit.ExitCode);
        }
        finally
        {
            lock (_terminalRuns) _terminalRuns.Remove(run);
        }
    }

    /// <summary><c>presentation.revealProblems</c>: the Error List comes forward always, or when the matchers found something.</summary>
    private async Task RevealProblemsAsync(TaskModel task, ProblemCollector? problems)
    {
        var mode = PresentationOf(task.Presentation).RevealProblems;
        if (mode == TaskRevealProblemsKind.Never || (mode == TaskRevealProblemsKind.OnProblem && problems is not { Count: > 0 })) return;
        if (_p.Views?.Invoke() is not { } views) return;
        try { await views.ShowViewAsync("errorlist"); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { _logger.LogDebug(ex, "revealProblems: the Error List could not be shown"); }
    }
}
