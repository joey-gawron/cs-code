using System.Text.Json;
using System.Xml.Linq;
using CsCode.Core.Contributions;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Dap.Client;
using CsCode.Dap.Breakpoints;
using CsCode.Dap.Configuration;
using CsCode.Dap.Adapters;
using CsCode.Dap.Service;
using CsCode.Dap.Session;
using CsCode.Dap.Tasks;
using CsCode.Platform.FileSystem;
using CsCode.Platform.Io;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Docs.Tests;

/// <summary>
/// F5 on the CsCode repository debugs CsCode (docs/RUN-AND-DEBUG.md, "Debugging CsCode itself"): the repository's <c>.vscode/launch.json</c>
/// and <c>tasks.json</c>, read by the REAL debug configuration service and task runner over the repository folder, name the app CsCode builds,
/// run it in this process with a sandboxed CSCODE_HOME, and build it first. Also the guide's <c>cscode.debuggers</c> example for lldb-dap.
/// </summary>
public sealed class RepositoryDebugSetupTests : IAsyncLifetime
{
    private const string Sandboxed = "CsCode (sandboxed window)";
    private const string Attach = "CsCode: attach to a process";

    private readonly DebugLane _lane = new("docs-tests");
    private readonly IoLane _io = new("docs-tests.io", 1);
    private readonly RepoWorkspace _workspace;
    private readonly TestConfigurationService _settings = new();
    private readonly ContributionRegistry _contributions = new();
    private readonly RecordingWindowService _window = new();
    private readonly DapPlatform _platform;
    private readonly TaskService _tasks;
    private readonly DebugConfigurationService _configurations;

    public RepositoryDebugSetupTests()
    {
        _workspace = new RepoWorkspace(new Uri(RepoDocs.Root.TrimEnd('/') + "/"), _io);
        _platform = new DapPlatform { Workspace = _workspace, Configuration = _settings, Contributions = _contributions, Window = _window };
        _tasks = new TaskService(_lane, _platform);
        _configurations = new DebugConfigurationService(_lane, _platform, () => _tasks);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _configurations.DisposeAsync();
        _tasks.Dispose(); // kills a preLaunchTask (the build F5 runs) a failed or cancelled test left running
        _lane.Dispose();
        _io.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string AppProperty(string name)
    {
        string? Find(string file) => XDocument.Load(RepoDocs.PathOf(file)).Descendants(name).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
        return Find("src/CsCode.App/CsCode.App.csproj") ?? Find("Directory.Build.props") ?? throw new InvalidOperationException(name + " not set");
    }

    private static JsonElement TasksJson()
    {
        using var doc = JsonDocument.Parse(RepoDocs.Read(".vscode/tasks.json"), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return doc.RootElement.Clone();
    }

    private static JsonElement Task(string label) =>
        TasksJson().GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("label").GetString() == label);

    [Fact]
    public async Task The_repositorys_launch_json_starts_the_built_app_in_this_process_with_a_sandboxed_home()
    {
        var configs = await _configurations.GetLaunchConfigurationsAsync(null, Ct);
        var launch = Assert.Single(configs, c => c.Name == Sandboxed);
        Assert.Equal(("coreclr", "launch"), (launch.Type, launch.Request));

        // the dll `dotnet build` writes for the app: its AssemblyName and target framework
        Assert.Equal($"${{workspaceFolder}}/src/CsCode.App/bin/Debug/{AppProperty("TargetFramework")}/{AppProperty("AssemblyName")}.dll", launch.GetString("program"));
        // --foreground: without it the debugged process is the launcher, which hands the window to a background process and exits
        Assert.Contains("--foreground", launch.Properties["args"].EnumerateArray().Select(a => a.GetString()));
        // its own CSCODE_HOME, in the git-ignored .scratch, so it never meets the CsCode that debugs it
        var home = launch.Properties["env"].GetProperty("CSCODE_HOME").GetString()!;
        Assert.StartsWith("${workspaceFolder}/.scratch/", home, StringComparison.Ordinal);
        Assert.Contains(".scratch/", RepoDocs.Read(".gitignore").Split('\n').Select(l => l.Trim()));

        var attach = Assert.Single(configs, c => c.Name == Attach);
        Assert.Equal(("coreclr", "attach", "${command:pickProcess}"), (attach.Type, attach.Request, attach.GetString("processId")));
        Assert.Equal(2, configs.Count);
    }

    [Fact]
    public async Task The_launchs_preLaunchTask_builds_the_app_and_the_default_build_task_builds_the_solution()
    {
        var configs = await _configurations.GetLaunchConfigurationsAsync(null, Ct);
        var launch = Assert.Single(configs, c => c.Name == Sandboxed);
        var tasks = await _tasks.GetTasksAsync(null, Ct); // the real runner's reading of the file
        Assert.Contains(tasks, t => t.Label == launch.PreLaunchTask);

        static string[] Args(JsonElement task) => [.. task.GetProperty("args").EnumerateArray().Select(a => a.GetString()!)];
        var build = Task(launch.PreLaunchTask!);
        Assert.Equal("dotnet", build.GetProperty("command").GetString());
        Assert.Equal(["build", "${workspaceFolder}/src/CsCode.App/CsCode.App.csproj"], Args(build)[..2]);
        Assert.True(File.Exists(RepoDocs.PathOf("src/CsCode.App/CsCode.App.csproj")));

        var defaultBuild = Assert.Single(tasks, t => t.IsDefaultBuild);
        Assert.Equal(["build", "${workspaceFolder}/CsCode.slnx"], Args(Task(defaultBuild.Label))[..2]);
    }

    /// <summary>
    /// The quit task quits the background sessions of the sandboxed window's home (--quit --all under the launch configuration's CSCODE_HOME);
    /// the debugged --foreground window has no session endpoint, so it cannot reach that (ForegroundProcessTests in CsCode.App.Tests).
    /// </summary>
    [Fact]
    public async Task The_quit_task_uses_the_sandboxed_windows_home()
    {
        var launch = Assert.Single(await _configurations.GetLaunchConfigurationsAsync(null, Ct), c => c.Name == Sandboxed);
        var quit = Task("quit sandboxed background sessions");
        var args = quit.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToArray();
        Assert.Equal(["--quit", "--all"], args[^2..]);
        Assert.Contains("--no-build", args);
        Assert.Equal(launch.Properties["env"].GetProperty("CSCODE_HOME").GetString(), quit.GetProperty("options").GetProperty("env").GetProperty("CSCODE_HOME").GetString());

        // The guide names the task and says what stops the debugged window, which the task cannot reach.
        var guide = RepoDocs.Read("docs/RUN-AND-DEBUG.md");
        var paragraph = guide[guide.IndexOf("`.vscode/tasks.json` also has", StringComparison.Ordinal)..];
        paragraph = paragraph[..paragraph.IndexOf("\n\n", StringComparison.Ordinal)];
        Assert.Contains("`quit sandboxed background sessions`", paragraph);
        Assert.Contains("stop it with Shift+F5", paragraph);
    }

    /// <summary>The guide's lldb-dap example, put in settings, becomes a debugger a new launch.json can be created for.</summary>
    [Fact]
    public async Task The_guides_lldb_dap_example_becomes_a_debugger_type()
    {
        var guide = RepoDocs.Read("docs/RUN-AND-DEBUG.md");
        var block = RepoDocs.CodeBlockAfter(guide, "**lldb-dap**", "json");
        using var setting = JsonDocument.Parse("{" + block + "}");
        _settings.Set(DebugService.DebuggersSetting, setting.RootElement.GetProperty(DebugService.DebuggersSetting).Clone());

        await using var breakpoints = new BreakpointManager(null, _workspace);
        await using var service = new DebugService(_lane, _platform, breakpoints, _configurations, _tasks);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!service.DebuggerTypes.Any(t => t.Type == "lldb-dap") && DateTime.UtcNow < deadline) await System.Threading.Tasks.Task.Delay(20, Ct);
        var type = Assert.Single(service.DebuggerTypes, t => t.Type == "lldb-dap");
        Assert.Equal("LLDB (lldb-dap)", type.Label);
        Assert.Contains("rust", type.Languages);
        var debugger = Assert.Single(_contributions.Of<DebuggerContribution>().Items).Value;
        Assert.Equal("/Library/Developer/CommandLineTools/usr/bin/lldb-dap", debugger.Program);

        // and the guide's configuration for it is a launch configuration of that type
        var config = RepoDocs.CodeBlockAfter(guide, "\"languages\": [\"c\", \"cpp\", \"rust\"]", "json");
        var parsed = Assert.Single(LaunchJson.Parse("{\"configurations\":[" + config + "]}", null, "guide").Configurations);
        Assert.Equal(("lldb-dap", "launch"), (parsed.Type, parsed.Request));
    }

    /// <summary>
    /// F5 on "CsCode (sandboxed window)" end to end, through the real debug service, task runner and netcoredbg: the preLaunchTask builds the
    /// app, the debugged process stops at a breakpoint in Program.RunApp, continues to a running window (a real one: Avalonia.Native on macOS,
    /// X11 on Linux) that writes no crash log, and Stop (Shift+F5) ends it. Only the CSCODE_HOME is a temp folder instead of .scratch/debug-home.
    /// Skips without netcoredbg (tools/fetch-netcoredbg.sh) or, on Linux, without a DISPLAY.
    /// </summary>
    [Fact]
    [Trait("Category", "RealAdapter")]
    public async Task F5_on_the_sandboxed_window_builds_stops_at_a_breakpoint_runs_the_window_and_stop_ends_it()
    {
        var netcoredbg = FindNetCoreDbg();
        Assert.SkipUnless(netcoredbg is not null, "netcoredbg is not available: run tools/fetch-netcoredbg.sh (expects .scratch/tools/netcoredbg/) or set CSCODE_NETCOREDBG");
        Assert.SkipWhen(OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")), "DISPLAY is not set: no X server for the debugged window");
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "netcoredbg's Unix build");
        _settings.Set(NetCoreDbgAdapterFactory.SettingPath, netcoredbg);
        using var home = new TempDirectory("cscode-f5-");

        var launch = Assert.Single(await _configurations.GetLaunchConfigurationsAsync(null, Ct), c => c.Name == Sandboxed);
        var properties = new Dictionary<string, JsonElement>(launch.Properties)
        {
            ["env"] = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["CSCODE_HOME"] = home.Path }),
        };
        var config = launch with { Properties = properties };

        var program = RepoDocs.PathOf("src/CsCode.App/Program.cs");
        var line = Array.FindIndex(File.ReadAllLines(program), l => l.Trim() == "BackgroundHost? host = null;");
        Assert.True(line >= 0, "Program.RunApp's first statement after the crash handler moved: update this test");

        await using var breakpoints = new BreakpointManager(null, _workspace);
        breakpoints.Toggle(new Uri(program), line);
        await using var service = new DebugService(_lane, _platform, breakpoints, _configurations, _tasks,
            NetCoreDbgAdapterFactory.Builtins(new NetCoreDbgAdapterFactory(_platform))); // as the app registers it
        var session = (DebugSession?)await service.StartAsync(config, Ct);
        Assert.True(session is not null, "F5 started no session (the preLaunchTask failed or the adapter did not start): " + string.Join(" | ", _window.Messages.Select(m => m.Message)));
        var stops = 0;
        var terminated = false;
        session.Stopped.Subscribe(_ => Interlocked.Increment(ref stops));
        session.Terminated.Subscribe(_ => terminated = true);
        try
        {
            await Eventually(() => Volatile.Read(ref stops) >= 1 && service.StopLocation is not null, 120_000, "the stop at Program.RunApp");
            Assert.Equal(program, service.StopLocation!.Uri?.LocalPath);
            Assert.Equal(line, service.StopLocation.Line);
            var pid = session.DebuggeeProcessId;
            Assert.NotNull(pid);

            await session.ContinueAsync(ct: Ct);
            await System.Threading.Tasks.Task.Delay(10_000, Ct); // services, the main window: all up well within this
            Assert.False(terminated, "the debugged window exited instead of running");
            Assert.True(IsRunning((int)pid.Value), "the debugged process is gone");
            var logs = Path.Combine(home.Path, "logs");
            var crashes = Directory.Exists(logs) ? Directory.GetFiles(logs, "crash-*.log") : [];
            Assert.True(crashes.Length == 0, "the debugged window reported a crash:\n" + string.Join("\n", crashes.Select(File.ReadAllText)));

            await service.StopAllAsync(Ct); // Shift+F5 (Stop Debugging)
            await Eventually(() => terminated && !IsRunning((int)pid.Value), 30_000, "the end of the debugged window after Stop");
        }
        finally
        {
            // the debuggee, and anything it started in the test's home (without --foreground: the background session it handed the window to)
            foreach (var left in ProcessTable.WithEnvironment("CSCODE_HOME", home.Path).Append((int)(session.DebuggeeProcessId ?? 0)).Where(p => p > 0 && IsRunning(p)))
                try { using var p = System.Diagnostics.Process.GetProcessById(left); p.Kill(); } catch (InvalidOperationException) { } catch (ArgumentException) { }
        }
    }

    private static bool IsRunning(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static async System.Threading.Tasks.Task Eventually(Func<bool> condition, int timeoutMs, string what)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting for " + what);
            await System.Threading.Tasks.Task.Delay(50, Ct);
        }
    }

    /// <summary>netcoredbg: CSCODE_NETCOREDBG, else .scratch/tools/netcoredbg/ in a folder above the tests (where tools/fetch-netcoredbg.sh puts it).</summary>
    private static string? FindNetCoreDbg()
    {
        if (Environment.GetEnvironmentVariable("CSCODE_NETCOREDBG") is { Length: > 0 } fromEnv && File.Exists(fromEnv)) return fromEnv;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".scratch", "tools", "netcoredbg", NetCoreDbgAdapterFactory.ExecutableName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The repository folder on the real file system; no file watching (the tests read the files once).</summary>
    private sealed class RepoWorkspace(Uri folder, IoLane io) : IWorkspaceService
    {
        private readonly Emitter<WorkspaceFoldersChangedEventArgs> _folders = new();
        public IReadOnlyList<Uri> Folders { get; } = [folder];
        public string? Name => Path.GetFileName(folder.LocalPath.TrimEnd('/'));
        public IFileSystem FileSystem { get; } = new LocalFileSystem(io);
        public IFileWatcher Watcher { get; } = new NoWatcher();
        public IReadOnlyList<Uri> RecentFolders => [];
        public IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged => _folders;
        public ValueTask OpenFolderAsync(Uri f, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask CloseFolderAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await System.Threading.Tasks.Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoWatcher : IFileWatcher
    {
        public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges) => Disposable.Empty;
    }
}
