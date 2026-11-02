using CsCode.App.Python;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Python;
using CsCode.Lsp.Manager;
using CsCode.TestDoubles;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// Language stage 1: what Run Selection sends to the Python REPL, how Run Python File quotes its command line, and what Python: Select Language
/// Server… writes.
/// </summary>
public class PythonIntegrationTests
{
    [Fact]
    public void Code_for_the_REPL_is_dedented_has_no_blank_lines_inside_and_ends_an_indented_block()
    {
        Assert.Equal("x = 1", PythonIntegration.ForRepl("    x = 1\n"));
        Assert.Equal("for i in range(3):\n    print(i)\n", PythonIntegration.ForRepl("    for i in range(3):\n\n        print(i)"));
        Assert.Equal("a = 1\nb = 2", PythonIntegration.ForRepl("a = 1\r\n\r\nb = 2\r\n"));
    }

    [Fact]
    public void The_command_line_quotes_only_what_needs_it()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal("/v/bin/python /p/main.py", PythonIntegration.CommandLine("/v/bin/python", "/p/main.py"));
        Assert.Equal("/v/bin/python '/my proj/it'\\''s.py'", PythonIntegration.CommandLine("/v/bin/python", "/my proj/it's.py"));
    }

    private sealed class NoInterpreters : IPythonEnvironmentService
    {
        private readonly Emitter<Uri?> _changed = new();
        public PythonEnvironment? GetActive(Uri? resource) => null;
        public ValueTask<PythonEnvironment?> GetActiveAsync(Uri? resource, CancellationToken ct = default) => default;
        public ValueTask<IReadOnlyList<PythonEnvironment>> DiscoverAsync(Uri? resource, bool refresh = false, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<PythonEnvironment>>([]);
        public ValueTask SetActiveAsync(Uri? resource, string? executable, CancellationToken ct = default) => default;
        public ValueTask<PythonEnvironment?> ProbeAsync(string executable, CancellationToken ct = default) => default;
        public ValueTask<bool> HasModuleAsync(string executable, string module, CancellationToken ct = default) => default;
        public IEventSource<Uri?> ActiveChanged => _changed;
    }

    [Fact]
    public async Task Select_Language_Server_writes_the_python_choice_and_keeps_the_other_groups_choices()
    {
        var config = new TestConfigurationService();
        config.Set("cscode.lsp.serverChoice", new Dictionary<string, string> { ["csharp"] = "csharp" });
        var window = new RecordingWindowService();
        window.QuickPickResponses.Enqueue(1); // Pyright (basedpyright, the default, is first)
        var commands = new TestCommandService();
        await using var sp = new ServiceCollection()
            .AddSingleton<IUiDispatcher>(new ImmediateUiDispatcher())
            .AddSingleton<IPythonEnvironmentService>(new NoInterpreters())
            .AddSingleton<ICommandService>(commands)
            .AddSingleton<IWindowService>(window)
            .AddSingleton<IConfigurationService>(config)
            .BuildServiceProvider();
        using var python = PythonIntegration.TryCreate(sp)!;
        python.Start();

        await commands.ExecuteAsync(PythonIntegration.SelectLanguageServer);
        var choice = BuiltinLanguageServers.ReadChoice(config.GetRaw("cscode.lsp.serverChoice"));
        Assert.Equal(("csharp", "python"), (choice["csharp"], choice["python"]));
        Assert.Contains(window.StatusMessages, m => m.Contains("Pyright", StringComparison.Ordinal));
    }
}
