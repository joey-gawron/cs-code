using System.Text.Json;
using System.Xml.Linq;
using CsCode.Core.Contributions;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Dap.Client;
using CsCode.Dap.Breakpoints;
using CsCode.Dap.Configuration;
using CsCode.Dap.Service;
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
    private readonly DapPlatform _platform;
    private readonly TaskService _tasks;
    private readonly DebugConfigurationService _configurations;

    public RepositoryDebugSetupTests()
    {
        _workspace = new RepoWorkspace(new Uri(RepoDocs.Root.TrimEnd('/') + "/"), _io);
        _platform = new DapPlatform { Workspace = _workspace, Configuration = _settings, Contributions = _contributions };
        _tasks = new TaskService(_lane, _platform);
        _configurations = new DebugConfigurationService(_lane, _platform, () => _tasks);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _configurations.DisposeAsync();
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
