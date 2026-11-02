using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using CsCode.Dap.Tests.Fakes;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>Language stage 1: <c>envFile</c> for every debugger, and the debugpy factory / provider without a real adapter.</summary>
public class DebugpyAndEnvFileTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void EnvFile_parses_dotenv_lines_quotes_comments_and_references()
    {
        var env = EnvFile.Parse("""
            # a comment
            PLAIN=one
            export EXPORTED = two
            SPACED=three # trailing comment
            DOUBLE="a \"quoted\"\nline"
            SINGLE='${PLAIN} stays literal'
            REF=${PLAIN}-${env:HOME_X}-${UNKNOWN}
            not an assignment
            EMPTY=
            PLAIN=again
            """, name => name == "HOME_X" ? "/home/x" : null);
        Assert.Equal("again", env["PLAIN"]); // a later assignment wins
        Assert.Equal("two", env["EXPORTED"]);
        Assert.Equal("three", env["SPACED"]);
        Assert.Equal("a \"quoted\"\nline", env["DOUBLE"]);
        Assert.Equal("${PLAIN} stays literal", env["SINGLE"]);
        Assert.Equal("one-/home/x-", env["REF"]); // the earlier PLAIN, the environment, nothing
        Assert.Equal("", env["EMPTY"]);
        Assert.Equal(7, env.Count);
    }

    [Fact]
    public void Env_wins_over_the_file_and_a_null_in_env_still_removes()
    {
        var merged = EnvFile.Merge(JsonDocument.Parse("""{"A":"from env","GONE":null}""").RootElement, new Dictionary<string, string> { ["A"] = "file", ["B"] = "file", ["GONE"] = "file" });
        Assert.Equal("from env", merged.GetProperty("A").GetString());
        Assert.Equal("file", merged.GetProperty("B").GetString());
        Assert.Equal(JsonValueKind.Null, merged.GetProperty("GONE").ValueKind);
    }

    [Fact]
    public async Task Any_debugger_gets_its_envFile_merged_into_env_before_the_providers()
    {
        await using var h = new ServiceHarness();
        await File.WriteAllTextAsync(Path.Combine(h.Dir.Path, "dev.env"), "FROM_FILE=${env:CSCODE_NO_SUCH_VAR}x\nBOTH=file\n", Ct);
        var config = ServiceHarness.Config("c", "coreclr", "launch", new Dictionary<string, object?>
        {
            ["envFile"] = "${workspaceFolder}/dev.env",
            ["env"] = new Dictionary<string, string> { ["BOTH"] = "env" },
        }, h.Dir.Uri);
        var resolved = await h.Configurations.ResolveVariablesAsync(config, Ct);
        var env = resolved.Properties["env"];
        Assert.Equal("x", env.GetProperty("FROM_FILE").GetString());
        Assert.Equal("env", env.GetProperty("BOTH").GetString());

        var missing = await h.Configurations.ResolveVariablesAsync(ServiceHarness.Config("c", "coreclr", "launch", new Dictionary<string, object?> { ["envFile"] = "nope.env" }, h.Dir.Uri), Ct);
        Assert.False(missing.Properties.ContainsKey("env")); // a missing file is skipped, not an error
    }

    private static async Task<(ServiceHarness H, DebugpyAdapterFactory F)> DebugpyAsync(string python = "/venv/bin/python")
    {
        var h = new ServiceHarness(python: new FakePythonForProvider(python));
        await Task.Yield();
        return (h, new DebugpyAdapterFactory(h.Platform));
    }

    [Fact]
    public async Task The_provider_fills_in_the_interpreter_cwd_console_and_the_folder_s_env_file_and_rewrites_the_legacy_type()
    {
        var (h, f) = await DebugpyAsync();
        await using var _ = h;
        await File.WriteAllTextAsync(Path.Combine(h.Dir.Path, ".env"), "TOKEN=abc\n", Ct);
        var config = ServiceHarness.Config("py", "python", "launch", new Dictionary<string, object?> { ["program"] = "main.py" }, h.Dir.Uri);
        var resolved = (await f.ResolveAsync(h.Dir.Uri, config, Ct))!;
        Assert.Equal("debugpy", resolved.Type);
        Assert.Equal("debugpy", resolved.GetString("type"));
        Assert.Equal("/venv/bin/python", resolved.GetString("python"));
        Assert.Equal(DapPlatform.NormalizeDir(h.Dir.Path), resolved.GetString("cwd"));
        Assert.Equal("internalConsole", resolved.GetString("console")); // no terminal in this composition
        Assert.Equal("abc", resolved.Properties["env"].GetProperty("TOKEN").GetString());

        // an explicit python and envFile are left alone
        var own = ServiceHarness.Config("py", "debugpy", "launch", new Dictionary<string, object?> { ["program"] = "m.py", ["python"] = "/other/python", ["envFile"] = "x.env" }, h.Dir.Uri);
        var kept = (await f.ResolveAsync(h.Dir.Uri, own, Ct))!;
        Assert.Equal("/other/python", kept.GetString("python"));
        Assert.False(kept.Properties.ContainsKey("env"));
    }

    [Fact]
    public async Task Attach_configurations_get_a_numeric_pid_or_a_default_connect_and_connect_talks_to_the_listening_adapter()
    {
        var (h, f) = await DebugpyAsync();
        await using var _ = h;
        var byPid = (await f.ResolveAsync(h.Dir.Uri, ServiceHarness.Config("a", "debugpy", "attach", new Dictionary<string, object?> { ["processId"] = "4242" }, h.Dir.Uri), Ct))!;
        Assert.Equal(JsonValueKind.Number, byPid.Properties["processId"].ValueKind);
        Assert.Equal("/venv/bin/python", byPid.GetString("python"));

        var bare = (await f.ResolveAsync(h.Dir.Uri, ServiceHarness.Config("a", "debugpy", "attach", null, h.Dir.Uri), Ct))!;
        Assert.Equal(5678, bare.Properties["connect"].GetProperty("port").GetInt32());

        var server = Assert.IsType<DebugAdapterServer>(await f.CreateDescriptorAsync(ServiceHarness.Config("a", "debugpy", "attach",
            new Dictionary<string, object?> { ["connect"] = new { host = "localhost", port = 5679 } }, h.Dir.Uri), Ct));
        Assert.Equal(("127.0.0.1", 5679), (server.Host, server.Port));
        var legacy = Assert.IsType<DebugAdapterServer>(await f.CreateDescriptorAsync(ServiceHarness.Config("a", "python", "attach",
            new Dictionary<string, object?> { ["host"] = "10.0.0.2", ["port"] = "5680" }, h.Dir.Uri), Ct));
        Assert.Equal(("10.0.0.2", 5680), (legacy.Host, legacy.Port));
    }

    [Fact]
    public async Task An_interpreter_with_debugpy_runs_its_own_adapter()
    {
        var (h, f) = await DebugpyAsync();
        await using var _ = h;
        var exe = Assert.IsType<DebugAdapterExecutable>(await f.CreateDescriptorAsync(ServiceHarness.Config("l", "debugpy", "launch",
            new Dictionary<string, object?> { ["program"] = "m.py", ["python"] = "/venv/bin/python" }, h.Dir.Uri), Ct));
        Assert.Equal("/venv/bin/python", exe.Command);
        Assert.Equal(["-m", "debugpy.adapter"], exe.Args);
    }

    [Fact]
    public void The_debugpyAttach_event_starts_an_attach_child_and_other_events_do_not()
    {
        var body = JsonDocument.Parse("""{"name":"Subprocess 7","connect":{"host":"127.0.0.1","port":5000},"subProcessId":7}""").RootElement;
        var child = DebugpyAdapterQuirks.Instance.ChildSessionFromEvent("debugpyAttach", body);
        Assert.NotNull(child);
        Assert.Equal("attach", child.Value.Request);
        Assert.Equal("attach", child.Value.Configuration["request"]!.GetValue<string>());
        Assert.Equal(7, child.Value.Configuration["subProcessId"]!.GetValue<int>());
        Assert.Null(DebugpyAdapterQuirks.Instance.ChildSessionFromEvent("debugpySockets", body));
    }

    [Fact]
    public async Task Initial_configurations_and_the_current_file()
    {
        var (h, f) = await DebugpyAsync();
        await using var _ = h;
        Directory.CreateDirectory(Path.Combine(h.Dir.Path, "src", "tool"));
        await File.WriteAllTextAsync(Path.Combine(h.Dir.Path, "src", "tool", "__main__.py"), "", Ct);
        var initial = await f.ProvideInitialAsync(h.Dir.Uri, Ct);
        Assert.Equal(["Python Debugger: Current File", "Python Debugger: Module", "Python Debugger: Attach", "Python Debugger: Attach using Process Id"], initial.Select(c => c.Name));
        Assert.Equal("${file}", initial[0].GetString("program"));
        Assert.Equal("tool", initial[1].GetString("module"));
        Assert.Equal("${command:pickProcess}", initial[3].GetString("processId"));

        var current = f.ForCurrentFile(new Uri(Path.Combine(h.Dir.Path, "a.py")), "python", h.Dir.Uri)!;
        Assert.Equal(Path.Combine(h.Dir.Path, "a.py"), current.GetString("program"));
        Assert.Null(f.ForCurrentFile(new Uri(Path.Combine(h.Dir.Path, "a.rs")), "rust", h.Dir.Uri));
    }

    private sealed class FakePythonForProvider(string executable) : CsCode.Core.Python.IPythonEnvironmentService
    {
        private readonly CsCode.Core.Lifecycle.Emitter<Uri?> _changed = new();
        private CsCode.Core.Python.PythonEnvironment Env => new(executable, CsCode.Core.Python.PythonEnvironmentKind.Venv, "3.13", "/venv", "venv");
        public CsCode.Core.Python.PythonEnvironment? GetActive(Uri? resource) => Env;
        public ValueTask<CsCode.Core.Python.PythonEnvironment?> GetActiveAsync(Uri? resource, CancellationToken ct = default) => ValueTask.FromResult<CsCode.Core.Python.PythonEnvironment?>(Env);
        public ValueTask<IReadOnlyList<CsCode.Core.Python.PythonEnvironment>> DiscoverAsync(Uri? resource, bool refresh = false, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<CsCode.Core.Python.PythonEnvironment>>([Env]);
        public ValueTask SetActiveAsync(Uri? resource, string? exe, CancellationToken ct = default) => default;
        public ValueTask<CsCode.Core.Python.PythonEnvironment?> ProbeAsync(string exe, CancellationToken ct = default) => default;
        public ValueTask<bool> HasModuleAsync(string exe, string module, CancellationToken ct = default) => ValueTask.FromResult(module == "debugpy");
        public CsCode.Core.Lifecycle.IEventSource<Uri?> ActiveChanged => _changed;
    }
}
