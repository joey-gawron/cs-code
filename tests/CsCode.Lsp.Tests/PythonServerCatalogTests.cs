using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Python;
using CsCode.Lsp.Manager;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>Language stage 1: the Python servers in the catalog (Pyright, Ruff, basedpyright off by default) and Pyright's python.pythonPath.</summary>
public class PythonServerCatalogTests
{
    private static Dictionary<string, bool> Enabled(string setting) =>
        DefinitionOverrides.Apply(BuiltinLanguageServers.All, JsonDocument.Parse(setting).RootElement).ToDictionary(kv => kv.Key, kv => kv.Value.Enabled);

    [Theory]
    [InlineData("{}", true, true, false)]
    [InlineData("""{"basedpyright": {"settings": {"basedpyright": {}}}}""", true, true, false)] // tuning it does not turn it on
    [InlineData("""{"basedpyright": {"enabled": true}, "python": {"enabled": false}}""", false, true, true)]
    [InlineData("""{"ruff": {"enabled": false}}""", true, false, false)]
    public void Pyright_and_Ruff_run_by_default_and_basedpyright_only_when_turned_on(string setting, bool pyright, bool ruff, bool basedpyright)
    {
        var enabled = Enabled(setting);
        Assert.Equal((pyright, ruff, basedpyright), (enabled["python"], enabled["ruff"], enabled["basedpyright"]));
    }

    [Fact]
    public void Ruff_runs_its_native_server_once_per_workspace_and_formats_python()
    {
        var ruff = BuiltinLanguageServers.Find("ruff")!;
        Assert.Equal("ruff", ruff.Definition.Command);
        Assert.Equal(["server"], ruff.Definition.Args);
        Assert.Equal(["pyproject.toml", "ruff.toml", ".ruff.toml"], ruff.Definition.RootMarkers);
        Assert.Equal(RootStrategy.Outermost, ruff.Roots);
        Assert.True(ruff.FromPythonEnvironment);
        Assert.StartsWith("Ruff", ruff.Formatter, StringComparison.Ordinal);
        Assert.Contains("brew install ruff", ruff.Install.MacOS, StringComparison.Ordinal);
        Assert.Contains("pipx install ruff", ruff.Install.Linux, StringComparison.Ordinal);
    }

    private sealed class OneInterpreter(PythonEnvironment? env) : IPythonEnvironmentService
    {
        public readonly CsCode.Core.Lifecycle.Emitter<Uri?> Changed = new();
        public PythonEnvironment? GetActive(Uri? resource) => env;
        public ValueTask<PythonEnvironment?> GetActiveAsync(Uri? resource, CancellationToken ct = default) => ValueTask.FromResult(env);
        public ValueTask<IReadOnlyList<PythonEnvironment>> DiscoverAsync(Uri? resource, bool refresh = false, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<PythonEnvironment>>([]);
        public ValueTask SetActiveAsync(Uri? resource, string? executable, CancellationToken ct = default) => default;
        public ValueTask<PythonEnvironment?> ProbeAsync(string executable, CancellationToken ct = default) => default;
        public ValueTask<bool> HasModuleAsync(string executable, string module, CancellationToken ct = default) => default;
        public CsCode.Core.Lifecycle.IEventSource<Uri?> ActiveChanged => Changed;
    }

    [Fact]
    public void Pyright_and_basedpyright_get_the_selected_interpreter_as_python_pythonPath_and_hear_when_it_changes()
    {
        var python = new OneInterpreter(new PythonEnvironment("/p/.venv/bin/python", PythonEnvironmentKind.Venv, "3.13.5", "/p/.venv", ".venv"));
        using var settings = new PythonServerSettings(python);
        Assert.Equal("/p/.venv/bin/python", settings.Contribute("python", "python", new Uri("file:///p/"))!["pythonPath"].GetString());
        Assert.NotNull(settings.Contribute("basedpyright", "python", null));
        Assert.Null(settings.Contribute("ruff", "python", null));
        Assert.Null(settings.Contribute("python", "python.analysis", null));
        var told = new List<string?>();
        using var _ = settings.Changed.Subscribe(told.Add);
        python.Changed.Fire(null);
        Assert.Equal(["python", "basedpyright"], told.Order(StringComparer.Ordinal).Reverse());
        Assert.Null(new PythonServerSettings(null).Contribute("python", "python", null));
    }
}
