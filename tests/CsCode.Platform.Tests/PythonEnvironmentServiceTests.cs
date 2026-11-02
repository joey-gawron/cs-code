using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Python;
using CsCode.Core.Terminal;
using CsCode.Platform.Io;
using CsCode.Platform.Python;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Platform.Tests;

/// <summary>Language stage 1: Python interpreter discovery, the per-folder pick and what terminals get (docs/PYTHON.md §1).</summary>
public sealed class PythonEnvironmentServiceTests : IDisposable
{
    private readonly TempDirectory _dir = new("cscode-python-");
    private readonly IoLane _io = new("test-io", 1);
    private readonly TestConfigurationService _config = new();
    private readonly FakeState _state = new();
    private readonly FakeWorkspace _workspace = new();
    private readonly Dictionary<string, string?> _env = new(StringComparer.Ordinal);
    /// <summary>What each fake interpreter answers to the probe: its prefix (and base prefix); anything not listed is not a Python.</summary>
    private readonly ConcurrentDictionary<string, (string Version, string Prefix, string Base)> _pythons = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _runs = new(StringComparer.Ordinal);
    private TaskCompletionSource? _probeGate;

    public PythonEnvironmentServiceTests()
    {
        _env["HOME"] = _dir.Combine("home");
        _env["PATH"] = "";
    }

    public void Dispose()
    {
        _io.Dispose();
        _dir.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PythonEnvironmentService Create() => new(_io, _workspace, _config, _state, null, RunAsync, name => _env.GetValueOrDefault(name));

    private async Task<PythonProcessResult> RunAsync(string file, IReadOnlyList<string> args, string? cwd, TimeSpan timeout, CancellationToken ct)
    {
        _runs.AddOrUpdate(file, 1, (_, n) => n + 1);
        if (_probeGate is { } gate) await gate.Task.WaitAsync(ct);
        if (!_pythons.TryGetValue(file, out var p)) return new PythonProcessResult(1, "");
        if (args.SequenceEqual(PythonProcess.ProbeArguments))
            return new PythonProcessResult(0, JsonSerializer.Serialize(new { v = p.Version, p = p.Prefix, b = p.Base }) + "\n");
        if (args[^1] == "pytest") return new PythonProcessResult(p.Prefix.Contains("venv", StringComparison.Ordinal) ? 0 : 3, "");
        return new PythonProcessResult(1, "");
    }

    /// <summary>A virtual environment directory with an (empty) interpreter file that the fake runner answers for.</summary>
    private string Venv(string dir, string version = "3.13.5", string basePrefix = "/base")
    {
        File.WriteAllText(Path.Combine(dir.EnsureDirectory(), "pyvenv.cfg"), "home = /base/bin\n");
        var exe = Path.Combine(Path.Combine(dir, "bin").EnsureDirectory(), "python");
        File.WriteAllText(exe, "");
        _pythons[exe] = (version, dir, basePrefix);
        return exe;
    }

    /// <summary>A global interpreter on PATH.</summary>
    private string Global(string dir, string name, string version, string prefix)
    {
        var exe = Path.Combine(dir.EnsureDirectory(), name);
        File.WriteAllText(exe, "");
        _pythons[exe] = (version, prefix, prefix);
        _env["PATH"] = string.Join(Path.PathSeparator, new[] { _env["PATH"], dir }.Where(s => !string.IsNullOrEmpty(s)));
        return exe;
    }

    [Fact]
    public async Task The_folder_s_venv_comes_first_then_VIRTUAL_ENV_then_the_first_python3_on_PATH_and_one_interpreter_is_listed_once()
    {
        var project = _dir.Combine("project");
        _workspace.Folders = [new Uri(project)];
        var other = Venv(_dir.Combine("elsewhere", "activated"), "3.12.1");
        _env["VIRTUAL_ENV"] = _dir.Combine("elsewhere", "activated");
        var global = Global(_dir.Combine("usr", "bin"), "python3", "3.11.0", "/framework/3.11");
        var sameInterpreter = Global(_dir.Combine("framework", "bin"), "python3", "3.11.0", "/framework/3.11"); // a second path to it
        var newer = Global(_dir.Combine("opt", "bin"), "python3", "3.14.7", "/framework/3.14");
        var venv = Venv(Path.Combine(project, ".venv"));
        using var svc = Create();

        var found = await svc.DiscoverAsync(new Uri(project), ct: Ct);
        Assert.Equal([venv, other, global, newer], found.Select(e => e.Executable));
        Assert.DoesNotContain(found, e => e.Executable == sameInterpreter);
        Assert.Equal(PythonEnvironmentKind.Venv, found[0].Kind);
        Assert.Equal(".venv", found[0].Name);
        Assert.Equal("3.13.5 ('.venv': venv)", found[0].Label);
        Assert.Equal("activated", found[1].Name);
        Assert.Equal(PythonEnvironmentKind.Global, found[2].Kind);
        // the default is the folder's own venv, for the folder and for a file in it
        Assert.Equal(venv, (await svc.GetActiveAsync(new Uri(Path.Combine(project, "src", "main.py")), ct: Ct))?.Executable);
    }

    [Fact]
    public async Task An_interpreter_that_does_not_answer_is_left_out()
    {
        var project = _dir.Combine("p");
        _workspace.Folders = [new Uri(project)];
        var broken = Path.Combine(_dir.Combine("p", ".venv", "bin").EnsureDirectory(), "python");
        File.WriteAllText(_dir.Combine("p", ".venv", "pyvenv.cfg"), "");
        File.WriteAllText(broken, ""); // not in _pythons: the runner says exit 1
        var global = Global(_dir.Combine("bin"), "python3", "3.13.0", "/g");
        using var svc = Create();
        var found = await svc.DiscoverAsync(null, ct: Ct);
        Assert.Equal([global], found.Select(e => e.Executable));
        Assert.Equal(1, _runs[broken]);
    }

    [Fact]
    public async Task The_pick_is_persisted_per_folder_and_wins_over_the_setting_and_the_default()
    {
        var a = _dir.Combine("a");
        var b = _dir.Combine("b");
        _workspace.Folders = [new Uri(a), new Uri(b)];
        var venvA = Venv(Path.Combine(a, ".venv"));
        var global = Global(_dir.Combine("bin"), "python3", "3.13.0", "/g");
        var custom = Global(_dir.Combine("custom"), "python3.12", "3.12.0", "/c");
        using var svc = Create();
        var changes = new List<Uri?>();
        using var _ = svc.ActiveChanged.Subscribe(changes.Add);

        Assert.Equal(venvA, (await svc.GetActiveAsync(new Uri(a), ct: Ct))?.Executable);
        Assert.Equal(global, (await svc.GetActiveAsync(new Uri(b), ct: Ct))?.Executable); // b has no venv: the first python3 on PATH

        _config.Set(PythonSettingKeys.DefaultInterpreterPath, custom);
        Assert.Equal(custom, (await svc.GetActiveAsync(new Uri(b), ct: Ct))?.Executable);
        Assert.Equal(custom, (await svc.GetActiveAsync(new Uri(a), ct: Ct))?.Executable); // the setting is above the default

        await svc.SetActiveAsync(new Uri(Path.Combine(a, "x.py")), global, ct: Ct);
        Assert.Equal(global, svc.GetActive(new Uri(a))?.Executable);
        Assert.Equal(custom, svc.GetActive(new Uri(b))?.Executable);
        Assert.Contains(changes, c => c == new Uri(a));

        using (var again = Create()) // a new session reads the pick back
            Assert.Equal(global, (await again.GetActiveAsync(new Uri(a), ct: Ct))?.Executable);

        await svc.SetActiveAsync(new Uri(a), null, ct: Ct);
        Assert.Equal(custom, (await svc.GetActiveAsync(new Uri(a), ct: Ct))?.Executable);
    }

    [Fact]
    public async Task GetActive_never_waits_and_reports_the_result_through_ActiveChanged()
    {
        var project = _dir.Combine("p");
        _workspace.Folders = [new Uri(project)];
        var venv = Venv(Path.Combine(project, ".venv"));
        _probeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var svc = Create();
        var changed = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = svc.ActiveChanged.Subscribe(u => changed.TrySetResult(u));

        var clock = Stopwatch.StartNew();
        Assert.Null(svc.GetActive(new Uri(project)));
        Assert.True(clock.ElapsedMilliseconds < 100, $"GetActive took {clock.ElapsedMilliseconds} ms");
        _probeGate.SetResult();
        Assert.Equal(new Uri(project), await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(venv, svc.GetActive(new Uri(project))?.Executable);
    }

    [Fact]
    public async Task HasModule_asks_once_per_interpreter_and_module()
    {
        var project = _dir.Combine("p");
        var venv = Venv(Path.Combine(project, ".venv"));
        var global = Global(_dir.Combine("bin"), "python3", "3.13.0", "/g");
        using var svc = Create();
        Assert.True(await svc.HasModuleAsync(venv, "pytest", ct: Ct));
        Assert.True(await svc.HasModuleAsync(venv, "pytest", ct: Ct));
        Assert.False(await svc.HasModuleAsync(global, "pytest", ct: Ct));
        Assert.Equal(1, _runs[venv]);
    }

    [Fact]
    public async Task Terminals_get_the_venv_first_on_PATH_and_VIRTUAL_ENV_but_nothing_for_a_global_interpreter_or_when_turned_off()
    {
        var project = _dir.Combine("p");
        _workspace.Folders = [new Uri(project)];
        var venv = Venv(Path.Combine(project, ".venv"));
        using var svc = Create();
        await svc.GetActiveAsync(new Uri(project), ct: Ct);
        var terminals = new PythonTerminalEnvironment(svc, _config, _workspace);

        var c = terminals.Contribute(new TerminalCreateOptions { Cwd = project });
        Assert.NotNull(c);
        Assert.Equal([Path.GetDirectoryName(venv)!], c.PathPrepend);
        Assert.Equal(Path.Combine(project, ".venv"), c.Variables["VIRTUAL_ENV"]);
        Assert.True(c.Variables.ContainsKey("PYTHONHOME") && c.Variables["PYTHONHOME"] is null);

        _config.Set(PythonSettingKeys.ActivateEnvironment, false);
        Assert.Null(terminals.Contribute(new TerminalCreateOptions { Cwd = project }));
        _config.Set(PythonSettingKeys.ActivateEnvironment, true);

        var global = Global(_dir.Combine("bin"), "python3", "3.13.0", "/g");
        await svc.SetActiveAsync(new Uri(project), global, ct: Ct);
        Assert.Null(terminals.Contribute(new TerminalCreateOptions { Cwd = project }));
    }

    [Fact]
    public void A_disposed_service_is_not_retained_by_the_configuration_or_the_workspace()
    {
        LeakAssert.NotRetained(() =>
        {
            var svc = Create();
            svc.Dispose();
            return [svc];
        });
    }

    // ---- the real thing ----

    private static string? RealPython()
    {
        foreach (var candidate in new[] { "/opt/homebrew/bin/python3", "/usr/local/bin/python3", "/usr/bin/python3" })
            if (File.Exists(candidate) && (!OperatingSystem.IsMacOS() || candidate != "/usr/bin/python3" || File.Exists("/Library/Developer/CommandLineTools/usr/bin/python3")))
                return candidate;
        return null;
    }

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task A_real_interpreter_answers_the_probe_and_a_hung_one_is_killed_at_the_deadline()
    {
        if (RealPython() is not { } python) { Assert.Skip("no python3 on this machine"); return; }
        var r = await PythonProcess.RunAsync(python, PythonProcess.ProbeArguments, null, PythonEnvironmentService.ProbeTimeout, Ct);
        Assert.Equal(0, r.ExitCode);
        var info = PythonProcess.ParseProbe(r.Output);
        Assert.NotNull(info);
        Assert.Matches(@"^3\.\d+\.\d+$", info.Value.Version);

        var clock = Stopwatch.StartNew();
        var hung = await PythonProcess.RunAsync(python, ["-c", "import time; time.sleep(60)"], null, TimeSpan.FromMilliseconds(300), Ct);
        Assert.Null(hung.ExitCode);
        Assert.True(clock.ElapsedMilliseconds < 5_000, $"took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task A_real_venv_in_the_folder_is_found_and_chosen()
    {
        if (RealPython() is not { } python) { Assert.Skip("no python3 on this machine"); return; }
        var project = _dir.Combine("real");
        Directory.CreateDirectory(project);
        var made = await PythonProcess.RunAsync(python, ["-m", "venv", "--without-pip", Path.Combine(project, ".venv")], null, TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(0, made.ExitCode);
        _workspace.Folders = [new Uri(project)];
        _env["PATH"] = Environment.GetEnvironmentVariable("PATH");
        using var svc = new PythonEnvironmentService(_io, _workspace, _config, _state, null, null, name => _env.GetValueOrDefault(name));
        var active = await svc.GetActiveAsync(new Uri(project), ct: Ct);
        Assert.NotNull(active);
        Assert.Equal(PythonEnvironmentKind.Venv, active.Kind);
        Assert.Equal(Path.Combine(project, ".venv", "bin", "python"), active.Executable);
        Assert.Equal(TempDirectory.PhysicalPath(Path.Combine(project, ".venv")), TempDirectory.PhysicalPath(active.Prefix!));
        Assert.True(await svc.HasModuleAsync(active.Executable, "json", ct: Ct));
        Assert.False(await svc.HasModuleAsync(active.Executable, "surely_not_a_module_cscode", ct: Ct));
    }

    // ---- fakes ----

    private sealed class FakeState : IStateStorage
    {
        private readonly Dictionary<(string, StorageScope), JsonElement> _values = new();
        public T? Get<T>(string key, StorageScope scope = StorageScope.Application) => _values.TryGetValue((key, scope), out var v) ? v.Deserialize<T>() : default;
        public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application) => _values[(key, scope)] = JsonSerializer.SerializeToElement(value);
        public void Remove(string key, StorageScope scope = StorageScope.Application) => _values.Remove((key, scope));
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class FakeWorkspace : IWorkspaceService
    {
        private readonly Emitter<WorkspaceFoldersChangedEventArgs> _changed = new();
        public IReadOnlyList<Uri> Folders { get; set; } = [];
        public string? Name => null;
        public ValueTask OpenFolderAsync(Uri folder, CancellationToken ct = default) => default;
        public ValueTask CloseFolderAsync(CancellationToken ct = default) => default;
        public IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged => _changed;
        public IFileSystem FileSystem => throw new NotSupportedException();
        public IFileWatcher Watcher => throw new NotSupportedException();
        public IReadOnlyList<Uri> RecentFolders => [];
        public IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, CancellationToken ct = default) => AsyncEnumerable.Empty<Uri>();
    }
}

internal static class PathTestExtensions
{
    public static string EnsureDirectory(this string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
