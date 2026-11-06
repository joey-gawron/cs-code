using CsCode.App.Services;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>Language: Restart Language Server... and Restart All Language Servers.</summary>
public class LanguageServerCommandTests
{
    private sealed class FakeServers(params LanguageServerInfo[] servers) : ILanguageServerManager
    {
        public readonly List<string> Restarted = [];
        public string? FailOn;
        public IReadOnlyList<LanguageServerDefinition> Definitions => [];
        public IReadOnlyList<LanguageServerInfo> Servers => servers;
        public IDisposable AddDefinition(LanguageServerDefinition definition) => Disposable.Empty;
        public ValueTask StartAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask StopAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask StopAllAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public IEventSource<IReadOnlyList<LanguageServerInfo>> ServersChanged { get; } = new Emitter<IReadOnlyList<LanguageServerInfo>>();
        public ValueTask RestartAsync(string id, CancellationToken ct = default)
        {
            if (id == FailOn) return ValueTask.FromException(new InvalidOperationException("boom"));
            Restarted.Add(id);
            return ValueTask.CompletedTask;
        }
    }

    private static LanguageServerInfo Info(string id, string name, LanguageServerState state = LanguageServerState.Running, string root = "file:///w/") =>
        new(id, name, state, new Uri(root), null, null, null, null);

    private static (TestCommandService Commands, RecordingWindowService Window, FakeServers Servers) Rig(params LanguageServerInfo[] servers)
    {
        var commands = new TestCommandService();
        var window = new RecordingWindowService();
        var fake = new FakeServers(servers);
        LanguageServerCommands.Register(commands, fake, window);
        return (commands, window, fake);
    }

    [Fact]
    public async Task The_commands_are_in_the_palette_and_restart_the_picked_server_once_per_id()
    {
        var (commands, window, servers) = Rig(Info("rust-analyzer", "Rust (rust-analyzer)", root: "file:///a/"), Info("rust-analyzer", "Rust (rust-analyzer)", root: "file:///b/"),
            Info("csharp", "C# (Roslyn)", LanguageServerState.Faulted));
        Assert.Contains(commands.All, c => c.Id == LanguageServerCommands.RestartServer && c.Title == "Restart Language Server...");
        Assert.Contains(commands.All, c => c.Id == LanguageServerCommands.RestartAll);
        Assert.Equal(["C# (Roslyn)", "Rust (rust-analyzer)"], LanguageServerCommands.Known(servers).Select(k => k.Name)); // each id once, sorted
        window.QuickPickResponses.Enqueue(1); // the second row: rust-analyzer
        await commands.ExecuteAsync(LanguageServerCommands.RestartServer);
        Assert.Equal(["rust-analyzer"], servers.Restarted);
    }

    [Fact]
    public async Task Restart_all_restarts_every_server_and_one_failure_does_not_stop_the_rest()
    {
        var (commands, window, servers) = Rig(Info("csharp", "C#"), Info("rust-analyzer", "Rust"), Info("gopls", "Go"));
        servers.FailOn = "gopls";
        await commands.ExecuteAsync(LanguageServerCommands.RestartAll);
        Assert.Equal(["csharp", "rust-analyzer"], servers.Restarted);
        Assert.Contains("gopls: boom", window.Messages.Single(m => m.Severity == MessageSeverity.Error).Message);
    }

    [Fact]
    public async Task With_no_server_or_one_the_user_is_told_or_not_asked()
    {
        var (none, noneWindow, noneServers) = Rig();
        await none.ExecuteAsync(LanguageServerCommands.RestartServer);
        Assert.Empty(noneServers.Restarted);
        Assert.Contains("No language server", noneWindow.Messages.Single().Message);

        var (one, _, oneServers) = Rig(Info("gopls", "Go"));
        await one.ExecuteAsync(LanguageServerCommands.RestartServer); // a single server: no pick
        Assert.Equal(["gopls"], oneServers.Restarted);
    }
}
