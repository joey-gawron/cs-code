using CsCode.Core.Contributions;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.Terminal.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Terminal;

/// <summary>
/// VS Code's task commands (the Terminal menu's Run Task... and Run Build Task..., Ctrl+Shift+B): a quick pick of tasks.json's tasks run through
/// <see cref="ITaskService"/>, which presents them in terminals named after them; Terminate Task ends one that is running. Registered only
/// when a task runner is composed.
/// </summary>
public static class TaskCommandSet
{
    public const string RunTask = "workbench.action.tasks.runTask";
    public const string Build = "workbench.action.tasks.build";
    public const string Test = "workbench.action.tasks.test";
    public const string Terminate = "workbench.action.tasks.terminate";

    public static readonly string[] Ids = [RunTask, Build, Test, Terminate];

    public static readonly KeybindingContribution[] Keybindings =
    [
        new(Build, "ctrl+shift+b"),
    ];

    public static IDisposable Register(ICommandService commands, TerminalPanelRuntime runtime)
    {
        var store = new DisposableStore();
        void Add(string id, string title, Func<CommandInvocation, Task> run, string? icon = null) =>
            store.Add(commands.Register(id, async inv => { await run(inv).ConfigureAwait(true); return null; }, new CommandMetadata(id, title, "Tasks", icon)));

        Add(RunTask, "Run Task...", async inv => await RunAsync(runtime, inv.Arg<string>(0), group: null).ConfigureAwait(true), "play");
        Add(Build, "Run Build Task", async _ => await RunAsync(runtime, null, group: "build").ConfigureAwait(true), "tools");
        Add(Test, "Run Test Task", async _ => await RunAsync(runtime, null, group: "test").ConfigureAwait(true));
        Add(Terminate, "Terminate Task...", async _ => await TerminateAsync(runtime).ConfigureAwait(true), "stop");
        return store;
    }

    private static ITaskService? Tasks(TerminalPanelRuntime runtime) => runtime.ServiceProvider.GetService<ITaskService>();

    /// <summary>
    /// Runs a task: the one named, else (a group) the group's default task or a pick among the group's tasks, else a pick among all tasks.
    /// Returns once the task started; failures to start are reported in a message.
    /// </summary>
    private static async Task RunAsync(TerminalPanelRuntime runtime, string? label, string? group)
    {
        if (Tasks(runtime) is not { } tasks || runtime.Window is not { } window) return;
        if (label is null)
        {
            IReadOnlyList<TaskDefinition> all;
            try { all = await tasks.GetTasksAsync().ConfigureAwait(true); }
            catch (InvalidOperationException ex)
            {
                await window.ShowMessageAsync(MessageSeverity.Error, ex.Message).ConfigureAwait(true);
                return;
            }
            var candidates = group is null ? all : all.Where(t => string.Equals(t.Group, group, StringComparison.OrdinalIgnoreCase)).ToList();
            if (group is not null && candidates.FirstOrDefault(t => t.IsDefault) is { } defaultTask) label = defaultTask.Label;
            else if (candidates.Count == 0)
            {
                var what = group is null ? "No tasks are defined" : $"No {group} task to run found";
                window.SetStatusBarMessage($"{what} (.cscode/tasks.json or .vscode/tasks.json).", TimeSpan.FromSeconds(5));
                return;
            }
            else if (candidates.Count == 1 && group is not null) label = candidates[0].Label;
            else
            {
                var items = candidates.Select(t => new QuickPickItem(t.Label, t.Group, t.Detail, Tag: t.Label)).ToList();
                var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Title: group is null ? "Run Task" : $"Run {group} task",
                    Placeholder: "Select the task to run")).ConfigureAwait(true);
                label = picked?.Tag as string;
            }
            if (label is null) return;
        }
        if (tasks.IsActive(label))
        {
            // VS Code: a task runs once at a time (runOptions.instanceLimit) and a second run says so instead of starting it again
            _ = PromptAlreadyActiveAsync(window, tasks, label);
            return;
        }
        _ = ReportAsync(window, tasks.RunAsync(label));
    }

    public const string TerminateTaskAction = "Terminate Task";
    public const string RestartTaskAction = "Restart Task";

    /// <summary>VS Code's "The task 'build' is already active." with Terminate Task and Restart Task (the run is left alone otherwise).</summary>
    private static async Task PromptAlreadyActiveAsync(IWindowService window, ITaskService tasks, string label)
    {
        string? choice;
        try
        {
            choice = await window.ShowMessageAsync(MessageSeverity.Warning, $"The task '{label}' is already active.",
                [new MessageAction(TerminateTaskAction), new MessageAction(RestartTaskAction)]).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException) { return; }
        if (choice is not (TerminateTaskAction or RestartTaskAction)) return;
        await tasks.TerminateAsync(label).ConfigureAwait(true);
        if (choice == RestartTaskAction) _ = ReportAsync(window, tasks.RunAsync(label));
    }

    /// <summary>A task that could not run at all (unknown, invalid tasks.json, a missing program) says why; its own failures show in its terminal.</summary>
    private static async Task ReportAsync(IWindowService window, ValueTask<TaskResult> run)
    {
        TaskResult result;
        try { result = await run.ConfigureAwait(true); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException) { return; }
        if (result.Error is { } error) await window.ShowMessageAsync(MessageSeverity.Error, error).ConfigureAwait(true);
    }

    /// <summary>Terminate Task: the running task, or a pick when several run.</summary>
    private static async Task TerminateAsync(TerminalPanelRuntime runtime)
    {
        if (runtime.ServiceProvider.GetService<TaskTerminalHost>() is not { } host || runtime.Window is not { } window) return;
        var running = host.Running;
        if (running.Count == 0)
        {
            window.SetStatusBarMessage("No task is running.", TimeSpan.FromSeconds(4));
            return;
        }
        var target = running.Count == 1 ? running[0]
            : (await window.ShowQuickPickAsync(running.Select(r => new QuickPickItem(r.Request.Label, Tag: r)).ToList(),
                new QuickPickOptions(Title: "Terminate Task", Placeholder: "Select the task to terminate")).ConfigureAwait(true))?.Tag as TaskTerminalHost.Run;
        target?.Terminate();
    }
}
