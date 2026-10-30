using Avalonia.Headless.XUnit;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.Panels.Terminal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Panels.Terminal.Tests;

/// <summary>
/// terminal-4: running a task that is already running does what VS Code does: "The task 'build' is already active." with Terminate Task and
/// Restart Task, instead of a second instance in a second terminal.
/// </summary>
public sealed class TaskCommandTests
{
    private sealed class FakeTasks : ITaskService
    {
        public readonly List<string> Calls = new();
        public bool Active;
        public Func<string, TaskResult> Result = label => new TaskResult(label, 0);

        public ValueTask<IReadOnlyList<TaskDefinition>> GetTasksAsync(Uri? folder = null, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<TaskDefinition>>([new TaskDefinition("build", "shell", "build", IsDefault: true)]);

        public ValueTask<TaskResult> RunAsync(string taskLabel, Uri? folder = null, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add("run " + taskLabel);
            return ValueTask.FromResult(Result(taskLabel));
        }

        public bool IsActive(string taskLabel, Uri? folder = null) => Active;

        public ValueTask TerminateAsync(string taskLabel, Uri? folder = null, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add("terminate " + taskLabel);
            Active = false;
            return ValueTask.CompletedTask;
        }
    }

    [AvaloniaFact]
    public async Task Running_an_active_task_offers_terminate_and_restart_instead_of_a_second_instance()
    {
        var tasks = new FakeTasks();
        await using var rig = await new PanelRig(configure: s => s.AddSingleton<ITaskService>(tasks)).ReadyAsync();

        await rig.Commands.ExecuteAsync(TaskCommandSet.Build);
        Assert.True(await PanelRig.Pump(() => tasks.Calls.Count == 1));
        Assert.Equal(["run build"], tasks.Calls);
        Assert.Empty(rig.WindowService.Messages);

        tasks.Active = true;
        await rig.Commands.ExecuteAsync(TaskCommandSet.Build); // no answer: the running task is left alone
        Assert.True(await PanelRig.Pump(() => rig.WindowService.Messages.Count == 1));
        Assert.Equal((MessageSeverity.Warning, "The task 'build' is already active."), rig.WindowService.Messages[0]);
        await PanelRig.Pump(() => false, 50);
        Assert.Equal(["run build"], tasks.Calls);

        rig.WindowService.MessageActionResponses.Enqueue(TaskCommandSet.RestartTaskAction);
        await rig.Commands.ExecuteAsync(TaskCommandSet.RunTask, "build");
        Assert.True(await PanelRig.Pump(() => tasks.Calls.Count == 3));
        Assert.Equal(["run build", "terminate build", "run build"], tasks.Calls);

        tasks.Active = true;
        rig.WindowService.MessageActionResponses.Enqueue(TaskCommandSet.TerminateTaskAction);
        await rig.Commands.ExecuteAsync(TaskCommandSet.RunTask, "build");
        Assert.True(await PanelRig.Pump(() => tasks.Calls.Count == 4));
        await PanelRig.Pump(() => false, 50);
        Assert.Equal(["run build", "terminate build", "run build", "terminate build"], tasks.Calls);
    }

    [AvaloniaFact]
    public async Task Run_task_says_nothing_about_a_background_task_that_cannot_be_tracked_but_reports_one_that_could_not_run()
    {
        // T43: a dev server or `dotnet watch` (isBackground, no background patterns) runs on in its terminal; VS Code reports "cannot be
        // tracked" only to the debugger's preLaunchTask
        var tasks = new FakeTasks
        {
            Result = label => new TaskResult(label, -1, Error: $"The task '{label}' cannot be tracked: make sure to have a problem matcher with background patterns defined")
            {
                NotTracked = true,
            },
        };
        await using var rig = await new PanelRig(configure: s => s.AddSingleton<ITaskService>(tasks)).ReadyAsync();
        await rig.Commands.ExecuteAsync(TaskCommandSet.RunTask, "serve");
        await rig.Commands.ExecuteAsync(TaskCommandSet.Build);
        Assert.True(await PanelRig.Pump(() => tasks.Calls.Count == 2));
        await PanelRig.Pump(() => false, 100);
        Assert.Empty(rig.WindowService.Messages);

        tasks.Result = label => new TaskResult(label, -1, Error: "Could not start 'nosuch': not found");
        await rig.Commands.ExecuteAsync(TaskCommandSet.RunTask, "broken");
        Assert.True(await PanelRig.Pump(() => rig.WindowService.Messages.Count == 1));
        Assert.Equal((MessageSeverity.Error, "Could not start 'nosuch': not found"), rig.WindowService.Messages[0]);
    }
}
