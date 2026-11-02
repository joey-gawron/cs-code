using CsCode.Core.Build;
using CsCode.Core.Lifecycle;
using CsCode.Core.Projects;
using CsCode.ProjectTemplates.Engine;
using CsCode.ProjectTemplates.Generators;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.ProjectTemplates.Tests;

public class ProjectFollowUpsTests
{
    private static string MakeVenv(TempDirectory temp)
    {
        var interpreter = Path.Combine(temp.Path, PythonProjectGenerator.VenvInterpreter);
        Directory.CreateDirectory(Path.GetDirectoryName(interpreter)!);
        File.WriteAllText(interpreter, "");
        return interpreter;
    }

    private static Uri Folder(TempDirectory temp) => new(Path.TrimEndingDirectorySeparator(temp.Path) + Path.DirectorySeparatorChar);

    [Fact]
    public async Task A_Python_project_s_venv_becomes_its_folder_s_interpreter()
    {
        using var temp = new TempDirectory();
        var interpreter = MakeVenv(temp);
        var python = new FakePythonEnvironments();
        var progress = new RecordingProgress();
        await new ProjectFollowUps(python).RunAsync(PythonProjectGenerator.GeneratorId, new ProjectResult(temp.Path, null), progress, TestContext.Current.CancellationToken);
        Assert.Equal(interpreter, python.Picks[Folder(temp)]);
        Assert.Contains(progress.Steps, s => s.StartsWith("Selected the interpreter 3.14.0 ('.venv': venv)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_is_picked_without_a_venv_or_for_another_generator()
    {
        using var temp = new TempDirectory();
        var python = new FakePythonEnvironments();
        var followUps = new ProjectFollowUps(python);
        await followUps.RunAsync(PythonProjectGenerator.GeneratorId, new ProjectResult(temp.Path, null), new RecordingProgress(), TestContext.Current.CancellationToken);
        MakeVenv(temp);
        await followUps.RunAsync(RustProjectGenerator.GeneratorId, new ProjectResult(temp.Path, null), new RecordingProgress(), TestContext.Current.CancellationToken);
        Assert.Empty(python.Picks);
        await new ProjectFollowUps().RunAsync(PythonProjectGenerator.GeneratorId, new ProjectResult(temp.Path, null), new RecordingProgress(), TestContext.Current.CancellationToken);
    }

    /// <summary>Records the configure requests; answers them with a configured project once <see cref="Answer"/> is set.</summary>
    private sealed class FakeCMake : ICMakeService
    {
        public List<(string Dir, string Preset)> Requests { get; } = [];
        public TaskCompletionSource<CMakeProject?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<CMakeProject> Projects => [];
        public IEventSource<IReadOnlyList<CMakeProject>> ProjectsChanged => EventSource.Never<IReadOnlyList<CMakeProject>>();
        public IEventSource<CMakeBuildFinished> BuildFinished => EventSource.Never<CMakeBuildFinished>();
        public CMakeProject? ProjectFor(string path) => null;
        public ValueTask<CMakeProject?> ConfigureAsync(CMakeProject project, CancellationToken ct = default) => ValueTask.FromResult<CMakeProject?>(null);
        public ValueTask<CMakeBuildResult> BuildAsync(CMakeProject project, IReadOnlyList<string>? targets = null, CancellationToken ct = default) =>
            ValueTask.FromResult(new CMakeBuildResult(null));
        public ValueTask<CMakeProject?> ConfigureWhenOpenedAsync(string sourceDirectory, string configurePreset, CancellationToken ct = default)
        {
            Requests.Add((sourceDirectory, configurePreset));
            return new(Answer.Task);
        }
    }

    [Theory]
    [InlineData(CMakeProjectGenerator.CId)]
    [InlineData(CMakeProjectGenerator.CppId)]
    public async Task A_CMake_project_asks_for_its_debug_configure_before_the_folder_opens(string generatorId)
    {
        using var temp = new TempDirectory();
        var cmake = new FakeCMake();
        var progress = new RecordingProgress();
        var configured = new ProjectFollowUps(cmake: cmake).BeforeOpenAsync(generatorId, new ProjectResult(temp.Path, "src/main.c"), progress);
        Assert.Equal([(temp.Path, "debug")], cmake.Requests); // on record before the folder opens
        Assert.False(configured.IsCompleted);
        var target = new CMakeTarget("app", CMakeTargetType.Executable, ["/b/app"], temp.Path, [], [], []);
        cmake.Answer.SetResult(new CMakeProject(temp.Path, Path.Combine(temp.Path, "build", "debug"), CMakeProjectState.Configured, "debug", null, "Debug", "Ninja", [target]));
        await configured.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains($"Configured CMake: 1 target in {Path.Combine(temp.Path, "build", "debug")}", progress.Steps);
    }

    [Fact]
    public async Task A_configure_that_fails_is_a_warning_and_other_projects_ask_for_none()
    {
        using var temp = new TempDirectory();
        var cmake = new FakeCMake();
        var followUps = new ProjectFollowUps(cmake: cmake);
        await followUps.BeforeOpenAsync(PythonProjectGenerator.GeneratorId, new ProjectResult(temp.Path, null), new RecordingProgress());
        Assert.Empty(cmake.Requests);
        var progress = new RecordingProgress();
        var configured = followUps.BeforeOpenAsync(CMakeProjectGenerator.CId, new ProjectResult(temp.Path, null), progress);
        cmake.Answer.SetResult(null);
        await configured.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("warning: CMake was not configured (the CMake output has the details); the first build configures it.", progress.Lines);
        await new ProjectFollowUps().BeforeOpenAsync(CMakeProjectGenerator.CId, new ProjectResult(temp.Path, null), progress); // no CMake service: nothing
    }

    [Fact]
    public async Task A_failing_service_is_a_warning_not_an_exception()
    {
        using var temp = new TempDirectory();
        MakeVenv(temp);
        var progress = new RecordingProgress();
        await new ProjectFollowUps(new FakePythonEnvironments { Throw = new IOException("state is read-only") })
            .RunAsync(PythonProjectGenerator.GeneratorId, new ProjectResult(temp.Path, null), progress, TestContext.Current.CancellationToken);
        Assert.Contains("warning: After creating the project: state is read-only", progress.Lines);
    }
}
