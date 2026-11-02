using CsCode.Core.Projects;
using CsCode.ProjectTemplates.Engine;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.ProjectTemplates.Tests;

public sealed class ProjectCreatorTests
{
    /// <summary>Writes a file, then fails, hangs or succeeds as told.</summary>
    private sealed class FakeGenerator(Func<CancellationToken, Task>? after = null, bool fail = false) : IProjectGenerator
    {
        public string Id => "fake";
        public string DisplayName => "Fake";
        public string Description => "";
        public string? IconKey => null;
        public string? LanguageId => null;
        public int Order => 0;
        public ValueTask<IReadOnlyList<ProjectOption>> GetOptionsAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<ProjectOption>>([]);
        public ValueTask<ProjectToolCheck> CheckToolsAsync(CancellationToken ct) => ValueTask.FromResult(ProjectToolCheck.Available("fake"));
        public string? ValidateName(string name) => ProjectNames.Folder(name);

        public async Task<ProjectResult> GenerateAsync(ProjectRequest request, IProjectGenerationProgress progress, CancellationToken ct)
        {
            await File.WriteAllTextAsync(Path.Combine(request.Directory, "main.txt"), "hello", ct);
            if (after is not null) await after(ct);
            if (fail) throw new ProjectGenerationException("tool exited with code 2");
            return new ProjectResult(request.Directory, "main.txt");
        }
    }

    private static ProjectCreator Creator(IToolRunner? runner = null) => new(runner ?? new ToolRunner());

    [Fact]
    public async Task Creates_the_folder_and_its_missing_parents()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine("a", "b", "proj");
        var outcome = await Creator().CreateAsync(new FakeGenerator(), new ProjectRequest("proj", target, new Dictionary<string, string>(), false), new RecordingProgress(), CancellationToken.None);
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.True(File.Exists(Path.Combine(target, "main.txt")));
        Assert.Equal("main.txt", outcome.Result!.MainFile);
    }

    [Fact]
    public async Task A_failure_removes_the_folder_and_the_parents_it_created()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine("new-parent", "proj");
        var progress = new RecordingProgress();
        var outcome = await Creator().CreateAsync(new FakeGenerator(fail: true), new ProjectRequest("proj", target, new Dictionary<string, string>(), false), progress, CancellationToken.None);
        Assert.Equal(ProjectCreationStatus.Failed, outcome.Status);
        Assert.Contains("code 2", outcome.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(temp.Combine("new-parent")));
        Assert.True(Directory.Exists(temp.Path)); // what existed before stays
        Assert.Contains(progress.Lines, l => l.StartsWith("Removed ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failure_in_a_folder_that_existed_removes_nothing()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine("proj");
        Directory.CreateDirectory(target);
        var outcome = await Creator().CreateAsync(new FakeGenerator(fail: true), new ProjectRequest("proj", target, new Dictionary<string, string>(), false), new RecordingProgress(), CancellationToken.None);
        Assert.Equal(ProjectCreationStatus.Failed, outcome.Status);
        Assert.True(File.Exists(Path.Combine(target, "main.txt")));
    }

    [Fact]
    public async Task Cancel_stops_the_generator_and_cleans_up()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine("proj");
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var generator = new FakeGenerator(async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); });
        var run = Creator().CreateAsync(generator, new ProjectRequest("proj", target, new Dictionary<string, string>(), false), new RecordingProgress(), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(ProjectCreationStatus.Cancelled, outcome.Status);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task Cancel_kills_a_running_tool()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("uses /bin/sleep");
        using var temp = new TempDirectory();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ToolRunner().RunAsync(new ToolRun("/bin/sleep", ["30"], temp.Path), null, cts.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_folder_with_files_is_refused_before_anything_runs()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("proj/keep.txt", "mine");
        var outcome = await Creator().CreateAsync(new FakeGenerator(), new ProjectRequest("proj", temp.Combine("proj"), new Dictionary<string, string>(), false), new RecordingProgress(), CancellationToken.None);
        Assert.Equal(ProjectCreationStatus.Failed, outcome.Status);
        Assert.Contains("not empty", outcome.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(temp.Combine("proj", "main.txt")));
        Assert.True(File.Exists(temp.Combine("proj", "keep.txt")));
    }

    [Fact]
    public void ValidateTarget_accepts_missing_and_empty_folders()
    {
        using var temp = new TempDirectory();
        Assert.Null(ProjectCreator.ValidateTarget(temp.Combine("missing", "deeper")));
        Assert.Null(ProjectCreator.ValidateTarget(temp.Path));
        temp.WriteFile("file", "x");
        Assert.NotNull(ProjectCreator.ValidateTarget(temp.Combine("file")));
        Assert.NotNull(ProjectCreator.ValidateTarget(temp.Combine("file", "below")));
        Assert.NotNull(ProjectCreator.ValidateTarget("relative/path"));
    }

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task The_git_step_makes_a_repository_with_a_first_commit()
    {
        if (ToolLocator.Find("git") is not { } git) { Assert.Skip("git is not installed"); return; }
        TestGit.Configure();
        using var temp = new TempDirectory();
        var target = temp.Combine("proj");
        var outcome = await Creator().CreateAsync(new FakeGenerator(), new ProjectRequest("proj", target, new Dictionary<string, string>(), true), new RecordingProgress(), CancellationToken.None);
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Empty(outcome.Result!.Warnings);
        var log = await new ToolRunner().RunAsync(new ToolRun(git, ["log", "--format=%s", "--name-only"], target), null, CancellationToken.None);
        Assert.Contains(ProjectCreator.FirstCommitMessage, log.Output, StringComparison.Ordinal);
        Assert.Contains("main.txt", log.Output, StringComparison.Ordinal);
    }
}
