using System.Diagnostics;
using System.Text.Json;
using Xunit;
using CsCode.Core.Build;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.TestDoubles;

namespace CsCode.Build.CMake.Tests;

/// <summary>CMake's detected tasks (<see cref="CMakeTaskProvider"/>) and the File API reply watch that follows a configure run as a task.</summary>
public sealed class CMakeTaskProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A file watcher the test fires by hand.</summary>
    private sealed class ManualWatcher : IFileWatcher
    {
        private readonly List<(Uri Root, Action<IReadOnlyList<FileChange>> OnChanges)> _watches = [];
        public IReadOnlyList<Uri> Roots { get { lock (_watches) return [.. _watches.Select(w => w.Root)]; } }

        public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges)
        {
            var entry = (root, onChanges);
            lock (_watches) _watches.Add(entry);
            return Disposable.Create(() => { lock (_watches) _watches.Remove(entry); });
        }

        public void Fire() { foreach (var (_, on) in _watches.ToArray()) on([]); }
    }

    private static (CMakeService Service, ScriptedProcesses Processes, string Bin) Make(TempDirectory dir, IFileWatcher? watcher = null)
    {
        var bin = dir.Combine("bin");
        Directory.CreateDirectory(bin);
        foreach (var tool in new[] { "cmake", "ctest", "ninja" }) File.WriteAllText(Path.Combine(bin, tool), "");
        var processes = new ScriptedProcesses { Answer = c => CMakeServiceTests.ConfigureReply(c) };
        var service = new CMakeService(new TestWorkspace(dir.Path), new TestConfigurationService(), new MemoryState(), null, null, null, processes, null,
            n => n == "PATH" ? bin : null, watcher);
        return (service, processes, bin);
    }

    [Fact]
    public async Task The_folders_project_gets_cmake_tools_tasks_with_the_status_bars_command_lines()
    {
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir);
        var (service, _, bin) = Make(dir);
        using var _ = service;
        await service.ConfigureAsync((await Detected(service)).Project, Ct);
        var provider = new CMakeTaskProvider(service);

        var tasks = await provider.ProvideTasksAsync(new Uri(dir.Path + "/"), Ct);

        Assert.Equal(["CMake: configure", "CMake: build", "CMake: clean", "CMake: clean rebuild", "CMake: install", "CMake: test"], tasks.Select(t => t.Label));
        var cmake = Path.Combine(bin, "cmake");
        var build = tasks.Single(t => t.Name == "build");
        var expected = await service.BuildCommandAsync(dir.Path, []);
        Assert.Equal((cmake, dir.Path, "build"), (build.Command, build.Cwd!, build.Group!));
        Assert.Equal(expected!.Arguments, build.Args);
        Assert.Equal(["$gcc"], build.ProblemMatchers);
        Assert.Equal("""{"type":"cmake","command":"build"}""", build.Definition!.Value.GetRawText());
        Assert.Equal([.. expected.Arguments, "--clean-first"], tasks.Single(t => t.Name == "clean rebuild").Args);
        Assert.Equal(["--build", dir.Combine("build"), "--target", "clean"], tasks.Single(t => t.Name == "clean").Args);
        Assert.Equal((await service.ConfigureCommandAsync(dir.Path))!.Arguments, tasks.Single(t => t.Name == "configure").Args);
        Assert.Equal(["--install", dir.Combine("build"), "--config", "Debug"], tasks.Single(t => t.Name == "install").Args);
        var test = tasks.Single(t => t.Name == "test");
        Assert.Equal((Path.Combine(bin, "ctest"), "test", dir.Combine("build")), (test.Command, test.Group, test.Cwd!));
        Assert.Equal(["-C", "Debug", "--output-on-failure"], test.Args);
        Assert.Empty(await provider.ProvideTasksAsync(new Uri(dir.Combine("elsewhere") + "/"), Ct)); // a folder without the project
    }

    [Fact]
    public async Task A_tasks_json_entry_written_for_cmake_tools_runs_as_written()
    {
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir);
        var (service, _, bin) = Make(dir);
        using var _ = service;
        await Detected(service);
        var provider = new CMakeTaskProvider(service);
        var folder = new Uri(dir.Path + "/");

        // CMake Tools' own template: the active preset by command variable, the "all" target
        var template = await provider.ResolveTaskAsync(folder, Json("""
            {"type":"cmake","label":"CMake: build","command":"build","targets":["all"],"preset":"${command:cmake.activeBuildPresetName}","group":"build","problemMatcher":[]}
            """), Ct);
        Assert.Equal(["--build", dir.Combine("build"), "--target", "all"], template!.Args);

        var preset = await provider.ResolveTaskAsync(folder, Json("""
            {"type":"cmake","command":"build","targets":["app"],"preset":"debug","args":["--verbose"],"options":{"cwd":"sub","environment":{"CFLAGS":"-Wall"}}}
            """), Ct);
        Assert.Equal(Path.Combine(bin, "cmake"), preset!.Command);
        Assert.Equal(["--build", "--preset", "debug", "--target", "app", "--verbose"], preset.Args);
        Assert.Equal((dir.Combine("sub"), "-Wall", "build app"), (preset.Cwd!, preset.Env!["CFLAGS"], preset.Name));

        Assert.Equal(["-S", dir.Path, "--preset", "debug"], (await provider.ResolveTaskAsync(folder, Json("""{"type":"cmake","command":"configure","preset":"debug"}"""), Ct))!.Args);
        Assert.Null(await provider.ResolveTaskAsync(folder, Json("""{"type":"cmake","command":"workflow"}"""), Ct)); // not run by CsCode: the runner reports it
    }

    [Fact]
    public async Task A_configure_run_outside_cscode_brings_its_targets_through_the_reply_watch()
    {
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir);
        var watcher = new ManualWatcher();
        var (service, _, _) = Make(dir, watcher);
        using var _ = service;
        var view = await Detected(service);
        Assert.Equal(CMakeProjectState.NotConfigured, view.Project.State);
        Assert.Contains(new Uri(dir.Combine("build", ".cmake", "api", "v1", "reply")), watcher.Roots); // watched before it exists
        var tasks = await new CMakeTaskProvider(service).ProvideTasksAsync(new Uri(dir.Path + "/"), Ct);
        Assert.True(Directory.Exists(dir.Combine("build", ".cmake", "api", "v1", "query", "client-cscode")), "listing the tasks writes the File API query");

        // the "CMake: configure" task runs cmake in a terminal: cmake writes the reply, the watch reads it
        var configure = tasks.Single(t => t.Name == "configure");
        Directory.CreateDirectory(dir.Combine("build"));
        CMakeServiceTests.ConfigureReply(new CMakeCommand(configure.Command, configure.Args, configure.Cwd!));
        watcher.Fire();

        await CMakeServiceTests.WaitUntil(() => service.Projects[0].State == CMakeProjectState.Configured);
        Assert.Equal("app", Assert.Single(service.Projects[0].Targets).Name);
    }

    [Fact]
    public async Task The_reply_watch_is_released_with_the_service()
    {
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir);
        var watcher = new ManualWatcher();
        var (service, _, _) = Make(dir, watcher);
        await Detected(service);
        Assert.Single(watcher.Roots);
        service.Dispose();
        Assert.Empty(watcher.Roots);
    }

    [Fact]
    [Trait("Category", CMakeCSample.Category)]
    public async Task The_real_configure_and_build_tasks_run_and_a_compiler_error_names_the_absolute_source_for_gcc()
    {
        if (CMakeCSample.SkipReason() is { } why) Assert.Skip(why);
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir);
        var main = dir.WriteFile("src/main.c", "int main(void) {\n    return missing_symbol;\n}\n");
        using var service = new CMakeService(new TestWorkspace(dir.Path), new TestConfigurationService(), new MemoryState(), null, null, null, new CMakeProcesses(), null, null);
        await service.Detected.WaitAsync(Ct);
        var tasks = await new CMakeTaskProvider(service).ProvideTasksAsync(new Uri(dir.Path + "/"), Ct);

        var (configureExit, configureOutput) = await RunAsync(tasks.Single(t => t.Name == "configure"));
        Assert.True(configureExit == 0, configureOutput);
        Assert.True(Directory.EnumerateFiles(dir.Combine("build", ".cmake", "api", "v1", "reply"), "index-*.json").Any(), "the task's configure left a reply CsCode reads");

        var (buildExit, buildOutput) = await RunAsync(tasks.Single(t => t.Name == "build"));
        Assert.NotEqual(0, buildExit);
        // $gcc: "file:line:col: error: message" with the source's absolute path, so autoDetect finds it whatever the task's directory
        var error = buildOutput.Split('\n').First(l => l.Contains("error:", StringComparison.Ordinal) && l.Contains("missing_symbol", StringComparison.Ordinal));
        Assert.StartsWith(TempDirectory.PhysicalPath(main) + ":2:", TempDirectory.PhysicalPath(error[..error.IndexOf(':')]) + error[error.IndexOf(':')..], StringComparison.Ordinal);
    }

    private static async Task<(int Exit, string Output)> RunAsync(ProvidedTask task)
    {
        var info = new ProcessStartInfo(task.Command) { WorkingDirectory = task.Cwd!, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in task.Args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var stdout = p.StandardOutput.ReadToEndAsync(Ct);
        var stderr = p.StandardError.ReadToEndAsync(Ct);
        await p.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromMinutes(2), Ct);
        return (p.ExitCode, await stdout + await stderr);
    }

    private static async Task<CMakeProjectView> Detected(CMakeService service)
    {
        await service.Detected.WaitAsync(Ct);
        return Assert.Single(service.Views);
    }
}
