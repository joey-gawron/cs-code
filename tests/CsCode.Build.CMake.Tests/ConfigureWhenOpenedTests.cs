using Xunit;
using CsCode.Core.Build;
using CsCode.Core.Platform;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Build.CMake.Tests;

/// <summary>
/// <see cref="ICMakeService.ConfigureWhenOpenedAsync"/> (New Project's request for a project it just created) against configure-on-open: the
/// requested project configures with its preset and is neither asked about nor given a preset picker; any other project is asked about as before.
/// </summary>
public sealed class ConfigureWhenOpenedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(ServiceProvider Provider, CMakeService Service, CMakeHost Host, TestWorkspace Workspace, RecordingWindowService Window,
        ScriptedProcesses Processes, MemoryState State) : IDisposable
    {
        public void Dispose()
        {
            Host.Dispose();
            Service.Dispose();
            Provider.Dispose();
        }
    }

    /// <summary>A window with no folder yet (the folder opens later, as New Project opens it), presets with debug and release, cmake.configureOnOpen as given.</summary>
    private static Setup Create(TempDirectory dir, bool? configureOnOpen = null)
    {
        var bin = dir.Combine("bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "cmake"), "");
        File.WriteAllText(Path.Combine(bin, "ninja"), "");
        var processes = new ScriptedProcesses
        {
            Answer = c =>
            {
                if (c.Arguments[0] == "--build") return ([], 0);
                // cmake --preset NAME configures into the preset's binaryDir
                var preset = c.Arguments[c.Arguments.ToList().IndexOf("--preset") + 1];
                var build = dir.Combine("build", preset);
                Directory.CreateDirectory(build);
                return CMakeServiceTests.ConfigureReply(c with { Arguments = ["-S", dir.Path, "-B", build] });
            },
        };
        var config = new TestConfigurationService();
        if (configureOnOpen is { } value) config.Set(CMakeSettings.ConfigureOnOpen, value);
        var window = new RecordingWindowService();
        var workspace = new TestWorkspace();
        var state = new MemoryState();
        var services = new ServiceCollection().AddSingleton<IConfigurationService>(config).AddSingleton<IWindowService>(window).BuildServiceProvider();
        var service = new CMakeService(workspace, config, state, null, null, null, processes, null, n => n == "PATH" ? bin : null);
        var host = new CMakeHost(services, service);
        host.Start();
        return new Setup(services, service, host, workspace, window, processes, state);
    }

    private static string Question(TempDirectory dir) => $"Configure the CMake project '{Path.GetFileName(dir.Path)}'?";

    [Theory]
    [InlineData(null)] // the default: configure-on-open asks
    [InlineData(true)] // configure-on-open configures, after a preset is picked
    public async Task A_requested_project_configures_with_its_preset_when_its_folder_opens_and_nothing_is_asked(bool? configureOnOpen)
    {
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir, presets: true);
        using var s = Create(dir, configureOnOpen);
        await s.Service.Detected.WaitAsync(Ct);

        var configured = s.Service.ConfigureWhenOpenedAsync(dir.Path, "debug", Ct).AsTask();
        s.Workspace.SetFolders(dir.Path);
        var project = await configured.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.NotNull(project);
        Assert.Equal((CMakeProjectState.Configured, "debug", dir.Combine("build", "debug")), (project.State, project.ConfigurePreset, project.BuildDirectory));
        Assert.Equal(["-S", dir.Path, "--preset", "debug", "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON"], Assert.Single(s.Processes.Snapshot()).Arguments);
        await Task.Delay(300, Ct); // room for a configure-on-open that should not happen
        Assert.Empty(s.Window.Messages); // not asked
        Assert.Single(s.Processes.Snapshot()); // not configured again, so no preset was picked either
        Assert.Equal("debug", s.State.Get<Dictionary<string, PersistedProject>>(CMakeService.StateKey, StorageScope.Workspace)![dir.Path].ConfigurePreset);
    }

    [Fact]
    public async Task Another_project_is_still_asked_about_when_its_folder_opens()
    {
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir, presets: true);
        using var s = Create(dir);
        await s.Service.Detected.WaitAsync(Ct);

        s.Workspace.SetFolders(dir.Path);

        await CMakeServiceTests.WaitUntil(() => { lock (s.Window.Messages) return s.Window.Messages.Any(m => m.Message == Question(dir)); });
        Assert.Empty(s.Processes.Snapshot()); // the recording window answers nothing: "Not Now"
        Assert.Null(Assert.Single(s.Service.Projects).ConfigurePreset);
    }

    [Fact]
    public async Task A_request_for_a_preset_the_project_does_not_have_leaves_it_to_configure_on_open()
    {
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir, presets: true);
        using var s = Create(dir);
        await s.Service.Detected.WaitAsync(Ct);

        var configured = s.Service.ConfigureWhenOpenedAsync(dir.Path, "nightly", Ct).AsTask();
        s.Workspace.SetFolders(dir.Path);

        Assert.Null(await configured.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        await CMakeServiceTests.WaitUntil(() => { lock (s.Window.Messages) return s.Window.Messages.Any(m => m.Message == Question(dir)); });
        Assert.Empty(s.Processes.Snapshot());
    }
}
