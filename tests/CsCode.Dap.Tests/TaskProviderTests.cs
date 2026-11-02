using System.Text.Json;
using CsCode.Core.Tasks;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Tasks;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>The <see cref="ITaskProvider"/> contract as the task runner honours it (detected tasks, customisation from tasks.json).</summary>
public class TaskProviderTests
{
    private sealed class FakeProvider(string type, params ProvidedTask[] tasks) : ITaskProvider
    {
        public int Calls;
        public TimeSpan Delay = TimeSpan.Zero;
        public string Type => type;

        public async ValueTask<IReadOnlyList<ProvidedTask>> ProvideTasksAsync(Uri folder, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return tasks;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public TempDirectory Dir { get; } = new("daptaskprov-");
        public DebugLane Lane { get; } = new("taskprov-lane");
        public InMemoryOutputService Output { get; } = new();
        public TaskService Service { get; }

        public Rig(string? tasksJson, params ITaskProvider[] providers)
        {
            if (tasksJson is not null) Dir.WriteFile(".cscode/tasks.json", tasksJson);
            Service = new TaskService(Lane, new DapPlatform { Workspace = new FakeWorkspace(Dir.Uri), Output = Output, TaskProviders = () => providers });
        }

        public ValueTask DisposeAsync()
        {
            Service.Dispose();
            Lane.Dispose();
            Dir.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static JsonElement Def(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ProvidedTask Echo(string name, string text, string? group = null) =>
        new("fake", name, "/bin/sh", ["-c", $"echo {text}"]) { Group = group, Definition = Def($$"""{"type":"fake","task":"{{name}}"}"""), ProblemMatchers = ["$rustc"] };

    private static void NeedsShell() => Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "needs /bin/sh");

    [Fact]
    public async Task Detected_tasks_are_listed_after_tasks_json_with_their_source()
    {
        await using var rig = new Rig("""{"tasks":[{"label":"mine","command":"true","group":"build"}]}""", new FakeProvider("fake", Echo("build", "b", "build"), Echo("test", "t", "test")));
        var tasks = await rig.Service.GetTasksAsync(null, TestToken.Ct);
        Assert.Equal(["mine", "fake: build", "fake: test"], tasks.Select(t => t.Label));
        Assert.Null(tasks[0].Source);
        Assert.Equal(("fake", "fake", "build", false), (tasks[1].Source, tasks[1].Type, tasks[1].Group, tasks[1].IsDefault));
        Assert.Equal("sh -c 'echo b'", tasks[1].Detail); // the command line when the provider gives no detail
    }

    [Fact]
    public async Task A_detected_task_runs_by_label_without_a_shell_and_its_arguments_unsubstituted()
    {
        NeedsShell();
        var task = new ProvidedTask("fake", "dollar", "/bin/sh", ["-c", "printf '%s\\n' '${workspaceFolder}' \"$0\"", "arg zero"]);
        await using var rig = new Rig(null, new FakeProvider("fake", task));
        var r = await rig.Service.RunAsync("fake: dollar", null, TestToken.Ct);
        Assert.True(r.Succeeded, r.Error);
        Assert.Contains("${workspaceFolder}", rig.Output.Text(TaskService.ChannelName)); // passed as it is
        Assert.Contains("arg zero", rig.Output.Text(TaskService.ChannelName)); // one argument, not split by a shell
    }

    [Fact]
    public async Task A_detected_task_can_be_a_preLaunch_style_dependency_of_a_tasks_json_task()
    {
        NeedsShell();
        await using var rig = new Rig("""{"tasks":[{"label":"after","command":"echo after-ran","dependsOn":"fake: build"}]}""", new FakeProvider("fake", Echo("build", "build-ran")));
        var r = await rig.Service.RunAsync("after", null, TestToken.Ct);
        Assert.True(r.Succeeded, r.Error);
        var text = rig.Output.Text(TaskService.ChannelName);
        Assert.True(text.IndexOf("build-ran", StringComparison.Ordinal) < text.IndexOf("after-ran", StringComparison.Ordinal), text);
    }

    [Fact]
    public async Task A_tasks_json_entry_of_the_providers_type_customises_the_detected_task_and_hides_it()
    {
        NeedsShell();
        await using var rig = new Rig("""
            {"tasks":[{"type":"fake","task":"build","label":"My build","group":{"kind":"build","isDefault":true},"options":{"env":{"EXTRA":"x"}}}]}
            """, new FakeProvider("fake", Echo("build", "custom-build-ran"), Echo("test", "t")));
        var tasks = await rig.Service.GetTasksAsync(null, TestToken.Ct);
        Assert.Equal(["My build", "fake: test"], tasks.Select(t => t.Label)); // "fake: build" is the customised one now
        Assert.True(tasks[0].IsDefaultBuild);
        Assert.Null(tasks[0].Source);
        var r = await rig.Service.RunAsync("My build", null, TestToken.Ct);
        Assert.True(r.Succeeded, r.Error);
        Assert.Contains("custom-build-ran", rig.Output.Text(TaskService.ChannelName));
    }

    [Fact]
    public async Task A_customisation_without_a_label_keeps_the_detected_label()
    {
        await using var rig = new Rig("""{"tasks":[{"type":"fake","task":"build","group":"test"}]}""", new FakeProvider("fake", Echo("build", "b", "build")));
        var tasks = await rig.Service.GetTasksAsync(null, TestToken.Ct);
        Assert.Equal(["fake: build"], tasks.Select(t => t.Label));
        Assert.Equal("test", tasks[0].Group); // the entry's group wins
    }

    [Fact]
    public async Task An_entry_no_detected_task_matches_reports_why_when_run()
    {
        await using var rig = new Rig("""{"tasks":[{"type":"fake","task":"nope","label":"broken"}]}""", new FakeProvider("fake", Echo("build", "b")));
        var r = await rig.Service.RunAsync("broken", null, TestToken.Ct);
        Assert.False(r.Succeeded);
        Assert.Contains("does not match any task", r.Error);
    }

    [Fact]
    public async Task Without_a_provider_an_unlabelled_entry_of_another_type_is_still_left_out()
    {
        await using var rig = new Rig("""{"tasks":[{"type":"npm","script":"build"},{"label":"kept","command":"true"}]}""");
        Assert.Equal(["kept"], (await rig.Service.GetTasksAsync(null, TestToken.Ct)).Select(t => t.Label));
    }

    [Fact]
    public async Task A_tasks_json_label_wins_over_a_detected_task_of_the_same_label()
    {
        await using var rig = new Rig("""{"tasks":[{"label":"fake: build","command":"true","detail":"mine"}]}""", new FakeProvider("fake", Echo("build", "b")));
        var tasks = await rig.Service.GetTasksAsync(null, TestToken.Ct);
        Assert.Equal("mine", Assert.Single(tasks).Detail);
    }

    [Fact]
    public async Task A_slow_provider_is_left_out_and_the_others_still_answer()
    {
        var old = TaskService.ProviderTimeout;
        TaskService.ProviderTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            var slow = new FakeProvider("slow", Echo("x", "x")) { Delay = TimeSpan.FromSeconds(30) };
            await using var rig = new Rig(null, slow, new FakeProvider("fake", Echo("build", "b")));
            var tasks = await rig.Service.GetTasksAsync(null, TestToken.Ct);
            Assert.Equal(["fake: build"], tasks.Select(t => t.Label));
        }
        finally { TaskService.ProviderTimeout = old; }
    }

    [Fact]
    public async Task The_default_resolve_matches_every_member_of_the_definition()
    {
        ITaskProvider provider = new FakeProvider("fake", Echo("build", "b"), Echo("test", "t"));
        var folder = new Uri("file:///tmp/");
        Assert.Equal("fake: test", (await provider.ResolveTaskAsync(folder, Def("""{"type":"fake","task":"test","label":"x"}"""), TestToken.Ct))?.Label);
        Assert.Null(await provider.ResolveTaskAsync(folder, Def("""{"type":"fake"}"""), TestToken.Ct));
    }
}
