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

        public ValueTask<IReadOnlyList<TaskDefinition>> GetTasksAsync(Uri? folder = null, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<TaskDefinition>>([new TaskDefinition("build", "shell", "build", IsDefault: true)]);

        public ValueTask<TaskResult> RunAsync(string taskLabel, Uri? folder = null, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add("run " + taskLabel);
            return ValueTask.FromResult(new TaskResult(taskLabel, 0));
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
}
