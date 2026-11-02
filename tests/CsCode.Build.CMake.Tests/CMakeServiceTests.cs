using Xunit;
using CsCode.Core.Build;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.TestDoubles;

namespace CsCode.Build.CMake.Tests;

public sealed class CMakeServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A PATH with fake cmake and ninja programs, so command lines name stable paths and nothing real runs.</summary>
    private static Func<string, string?> FakeTools(TempDirectory dir, bool ninja = true)
    {
        var bin = dir.Combine("bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "cmake"), "");
        File.WriteAllText(Path.Combine(bin, "ctest"), "");
        if (ninja) File.WriteAllText(Path.Combine(bin, "ninja"), "");
        return name => name == "PATH" ? bin : null;
    }

    /// <summary>Answers a configure by writing a File API reply with one executable, as cmake would.</summary>
    internal static (IReadOnlyList<string>, int) ConfigureReply(CMakeCommand c, string configuration = "Debug")
    {
        var b = c.Arguments.ToList().IndexOf("-B");
        if (b < 0) return (["-- Build files have been written"], 0);
        var build = c.Arguments[b + 1];
        File.WriteAllText(Path.Combine(build, "CMakeCache.txt"), "CMAKE_BUILD_TYPE:STRING=" + configuration + "\n");
        var reply = Path.Combine(build, ".cmake", "api", "v1", "reply");
        Directory.CreateDirectory(reply);
        File.WriteAllText(Path.Combine(reply, "index-1.json"), """
            { "cmake": { "generator": { "multiConfig": false, "name": "Ninja" } }, "reply": { "client-cscode": { "codemodel-v2": { "jsonFile": "cm.json" } } } }
            """);
        File.WriteAllText(Path.Combine(reply, "cm.json"), $$"""
            { "paths": { "build": "{{build}}", "source": "{{c.Arguments[1]}}" }, "configurations": [ { "name": "{{configuration}}", "targets": [ { "name": "app", "id": "app::@1", "jsonFile": "t.json" } ] } ] }
            """);
        File.WriteAllText(Path.Combine(reply, "t.json"), """{ "name": "app", "type": "EXECUTABLE", "artifacts": [ { "path": "app" } ], "sources": [ { "path": "main.c" } ] }""");
        return (["-- Configuring done", "-- Build files have been written to: " + build], 0);
    }

    private static CMakeService Service(TempDirectory dir, ScriptedProcesses processes, TestConfigurationService? config = null, MemoryState? state = null,
        IDiagnosticService? diagnostics = null, InMemoryOutputService? output = null, bool ninja = true) =>
        new(new TestWorkspace(dir.Path), config ?? new TestConfigurationService(), state ?? new MemoryState(), diagnostics ?? new DiagnosticService(),
            output ?? new InMemoryOutputService(), null, processes, null, FakeTools(dir, ninja));

    [Fact]
    public async Task Detects_a_project_and_configures_with_ninja_debug_and_compile_commands()
    {
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir);
        var processes = new ScriptedProcesses { Answer = c => ConfigureReply(c) };
        var output = new InMemoryOutputService();
        using var service = Service(dir, processes, output: output);
        await service.Detected.WaitAsync(Ct);

        var project = Assert.Single(service.Projects);
        Assert.Equal(CMakeProjectState.NotConfigured, project.State);
        Assert.Equal(dir.Combine("build"), project.BuildDirectory);

        var configured = await service.ConfigureAsync(project, Ct);

        Assert.NotNull(configured);
        Assert.Equal(CMakeProjectState.Configured, configured.State);
        Assert.Equal("app", Assert.Single(configured.Targets).Name);
        var command = Assert.Single(processes.Snapshot());
        Assert.Equal(dir.Combine("bin", "cmake"), command.FileName);
        Assert.Equal(["-S", dir.Path, "-B", dir.Combine("build"), "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug", "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON"], command.Arguments);
        Assert.True(File.Exists(dir.Combine("build", ".cmake", "api", "v1", "query", "client-cscode", "codemodel-v2")), "the File API query is written before cmake runs");
        Assert.Contains("Configured", output.Text(CMakeService.OutputChannelName), StringComparison.Ordinal);
        Assert.Equal(1, configured.Generation);
    }

    [Fact]
    public async Task Settings_shape_the_configure_and_build_command_lines()
    {
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir);
        var config = new TestConfigurationService();
        config.Set(CMakeSettings.BuildDirectory, "${workspaceFolder}/out/${buildType}");
        config.Set(CMakeSettings.Generator, "Unix Makefiles");
        config.Set(CMakeSettings.ConfigureArgs, new[] { "--log-level=VERBOSE" });
        config.Set(CMakeSettings.ConfigureSettings, new Dictionary<string, object> { ["FEATURE"] = true, ["NAME"] = "x" });
        config.Set(CMakeSettings.BuildArgs, new[] { "--verbose" });
        config.Set(CMakeSettings.BuildToolArgs, new[] { "-k" });
        config.Set(CMakeSettings.ParallelJobs, 4);
        var processes = new ScriptedProcesses { Answer = c => c.Arguments[0] == "--build" ? ([], 0) : ConfigureReply(c, "Release") };
        using var service = Service(dir, processes, config);
        await service.Detected.WaitAsync(Ct);
        await service.SetBuildTypeAsync(dir.Path, "Release");

        var project = Assert.Single(service.Projects);
        Assert.Equal(dir.Combine("out", "Release"), project.BuildDirectory);
        var result = await service.BuildAsync(project, ["app"], Ct);

        Assert.True(result.Succeeded);
        var commands = processes.Snapshot();
        Assert.Equal(2, commands.Length); // configured first: it never was
        Assert.Equal(["-S", dir.Path, "-B", dir.Combine("out", "Release"), "-G", "Unix Makefiles", "-DCMAKE_BUILD_TYPE=Release", "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON",
            "-DFEATURE=ON", "-DNAME=x", "--log-level=VERBOSE"], commands[0].Arguments);
        Assert.Equal(["--build", dir.Combine("out", "Release"), "--target", "app", "--parallel", "4", "--verbose", "--", "-k"], commands[1].Arguments);
    }

    [Fact]
    public async Task Presets_drive_configure_and_build_and_selections_persist()
    {
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir, presets: true);
        var state = new MemoryState();
        var processes = new ScriptedProcesses { Answer = c => c.Arguments[0] == "--build" ? ([], 0) : ConfigureReply(PresetBuildDir(c, dir)) };
        using (var service = Service(dir, processes, state: state))
        {
            await service.Detected.WaitAsync(Ct);
            var view = Assert.Single(service.Views);
            Assert.True(view.UsesPresets);
            Assert.Equal(["debug", "release"], view.ConfigurePresets.Select(p => p.Name));
            Assert.Null(view.Project.ConfigurePreset);
            Assert.Null(await service.ConfigureAsync(view.Project, Ct)); // a preset must be chosen first
            Assert.Empty(processes.Snapshot());

            await service.SelectConfigurePresetAsync(dir.Path, "release");
            await service.SelectBuildPresetAsync(dir.Path, "debug"); // for another configure preset: allowed, but cleared when the configure preset changes
            await service.SelectConfigurePresetAsync(dir.Path, "debug");
            await service.SelectBuildPresetAsync(dir.Path, "debug");
            await service.SetLaunchTargetAsync(dir.Path, "app");
            var project = Assert.Single(service.Projects);
            Assert.Equal(dir.Combine("build", "debug"), project.BuildDirectory);
            Assert.Equal("Debug", project.Configuration);
            await service.BuildAsync(project, null, Ct);
            var commands = processes.Snapshot();
            Assert.Equal(["-S", dir.Path, "--preset", "debug", "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON"], commands[0].Arguments);
            Assert.Equal(["--build", "--preset", "debug"], commands[1].Arguments);
        }

        using var again = Service(dir, new ScriptedProcesses(), state: state);
        await again.Detected.WaitAsync(Ct);
        var restored = Assert.Single(again.Views);
        Assert.Equal(("debug", "debug", "app"), (restored.Project.ConfigurePreset, restored.Project.BuildPreset, restored.LaunchTarget));
        Assert.Equal(CMakeProjectState.Configured, restored.Project.State); // the reply in build/debug is read at once, without running cmake
    }

    private static CMakeCommand PresetBuildDir(CMakeCommand c, TempDirectory dir)
    {
        // cmake --preset debug configures into the preset's binaryDir: hand the fake reply writer that directory
        var preset = c.Arguments[c.Arguments.ToList().IndexOf("--preset") + 1];
        var build = dir.Combine("build", preset);
        Directory.CreateDirectory(build);
        return c with { Arguments = ["-S", dir.Path, "-B", build] };
    }

    [Fact]
    public async Task A_failed_build_reports_its_errors_and_fires_build_finished()
    {
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir);
        var main = dir.Combine("src", "main.c");
        var processes = new ScriptedProcesses
        {
            Answer = c => c.Arguments[0] == "--build" ? ([$"{main}:9:5: error: expected ';'", "ninja: build stopped: subcommand failed."], 1) : ConfigureReply(c),
        };
        var diagnostics = new DiagnosticService();
        using var service = Service(dir, processes, diagnostics: diagnostics);
        await service.Detected.WaitAsync(Ct);
        var finished = new List<CMakeBuildFinished>();
        using var sub = service.BuildFinished.Subscribe(finished.Add);

        var result = await service.BuildAsync(service.Projects[0], null, Ct);

        Assert.Equal((1, 1, false), (result.ExitCode, result.Errors, result.Succeeded));
        var problem = Assert.Single(diagnostics.Get(new Uri(main)));
        Assert.Equal("expected ';'", problem.Message);
        var done = Assert.Single(finished);
        Assert.Empty(done.Targets);
        Assert.Equal(1, done.Result.ExitCode);
    }

    [Fact]
    public async Task Stop_cancels_a_running_build_and_the_next_one_runs()
    {
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir);
        var processes = new ScriptedProcesses { Answer = c => c.Arguments[0] == "--build" ? ([], 0) : ConfigureReply(c) };
        using var service = Service(dir, processes);
        await service.Detected.WaitAsync(Ct);
        await service.ConfigureAsync(service.Projects[0], Ct);
        processes.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var building = service.BuildAsync(service.Projects[0], null, Ct).AsTask();
        await WaitUntil(() => service.Views[0].Activity is not null);
        await service.StopAsync(dir.Path);
        var result = await building.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Null(result.ExitCode);
        processes.Gate = null;
        Assert.True((await service.BuildAsync(service.Projects[0], null, Ct)).Succeeded);
        Assert.Null(service.Views[0].Activity);
    }

    [Fact]
    public async Task Calls_from_the_caller_thread_only_enqueue()
    {
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir);
        var processes = new ScriptedProcesses { Answer = c => ConfigureReply(c), Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var service = Service(dir, processes);
        await service.Detected.WaitAsync(Ct);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var configuring = service.ConfigureAsync(service.Projects[0], Ct);
        watch.Stop();
        Assert.False(configuring.IsCompleted);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(50), $"the call took {watch.Elapsed.TotalMilliseconds} ms on the caller's thread");
        processes.Gate.SetResult();
        Assert.NotNull(await configuring);
    }

    [Fact]
    public async Task Projects_follow_the_workspace_folders()
    {
        using var a = new TempDirectory();
        using var b = new TempDirectory();
        CMakeCSample.Write(a);
        var workspace = new TestWorkspace(a.Path, b.Path);
        using var service = new CMakeService(workspace, new TestConfigurationService(), new MemoryState(), null, null, null, new ScriptedProcesses(), null, FakeTools(a));
        await service.Detected.WaitAsync(Ct);
        Assert.Equal([a.Path], service.Projects.Select(p => p.SourceDirectory));
        Assert.Equal(a.Path, service.ProjectFor(a.Combine("src", "main.c"))?.SourceDirectory);
        Assert.Null(service.ProjectFor(b.Combine("x.c")));

        CMakeCSample.Write(b);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = service.ProjectsChanged.Subscribe(p => { if (p.Count == 1 && p[0].SourceDirectory == b.Path) changed.TrySetResult(); });
        workspace.SetFolders(b.Path);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task Service_is_not_retained_after_dispose()
    {
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir);
        var config = new TestConfigurationService();
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var service = new CMakeService(new TestWorkspace(dir.Path), config, new MemoryState(), null, null, null,
                new ScriptedProcesses { Answer = c => ConfigureReply(c) }, null, FakeTools(dir));
            await service.Detected;
            await service.ConfigureAsync(service.Projects[0]);
            service.Dispose();
            return [service];
        });
        Assert.Empty(config.Properties); // the settings registration went with it
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }
}
