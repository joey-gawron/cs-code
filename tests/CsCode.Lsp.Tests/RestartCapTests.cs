using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Lsp.Manager;
using CsCode.Lsp.Protocol;
using CsCode.Lsp.Tests.Harness;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// T39: the servers built on the TypeScript engine carry a restart cap (on their process tree, lsp-core-4); a user can change or remove it. The
/// cap restarts a server a bounded number of times, says so each time, and then stops enforcing it with one message, instead of killing a project
/// whose normal size is above it forever.
/// </summary>
public sealed class RestartCapTests
{
    private const long Cap = 1_000_000;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_typescript_engine_servers_restart_above_vs_code_s_tsserver_heap_plus_the_rest_of_the_tree_and_the_others_have_no_cap()
    {
        var capped = BuiltinLanguageServers.All.Where(d => d.RestartAboveBytes is not null).Select(d => d.Id).Order().ToArray();
        Assert.Equal(["svelte", "typescript", "vue"], capped);
        // VS Code's maxTsServerMemory (3072 MB of V8 old space per tsserver) + 1 GiB for the launcher, the syntax server, the typings installer and
        // the node runtime pages each process of the tree counts again.
        Assert.All(BuiltinLanguageServers.All.Where(d => d.RestartAboveBytes is not null), d => Assert.Equal(4L * 1024 * 1024 * 1024, d.RestartAboveBytes));
    }

    [Theory]
    [InlineData("""{"typescript": {"restartAboveBytes": 1000000000}}""", 1_000_000_000L)]
    [InlineData("""{"typescript": {"restartAboveBytes": null}}""", null)]
    [InlineData("""{"typescript": {"restartAboveBytes": 0}}""", null)]
    [InlineData("""{"typescript": {"settings": {}}}""", BuiltinLanguageServers.TypeScriptEngineCap)]
    public void The_cap_follows_the_user_s_override(string setting, long? expected)
    {
        var effective = DefinitionOverrides.Apply(BuiltinLanguageServers.All, JsonDocument.Parse(setting).RootElement);
        Assert.Equal(expected, effective["typescript"].Definition.RestartAboveBytes);
    }

    [Fact]
    public async Task The_cap_restarts_a_server_at_most_three_times_then_stops_enforcing_it_with_one_message_and_a_restart_rearms_it()
    {
        await using var env = await StartCappedAsync();
        for (var i = 1; i <= ServerSlot.MaxMemoryRestarts; i++)
        {
            var tracked = await TrackingAsync(env, i - 1);
            Assert.Equal(Cap, tracked.Limit);
            await tracked.OnOverBudget!(); // the monitor: the tree is over the cap
            var next = await env.Connections.WaitForServerAsync(i);
            await next.WaitForAsync(LspMethods.TextDocumentDidOpen);
        }
        await TestHelpers.EventuallyAsync(() => StatusMessages(env).Count(m => m.Contains("memory cap", StringComparison.Ordinal)) == ServerSlot.MaxMemoryRestarts,
            "one status-bar message per memory restart");

        // The cap fires once more: no fourth restart. The running session is re-tracked without a cap and one warning explains it.
        var fourth = await TrackingAsync(env, ServerSlot.MaxMemoryRestarts);
        await fourth.OnOverBudget!();
        var untracked = await TrackingAsync(env, ServerSlot.MaxMemoryRestarts + 1);
        Assert.Null(untracked.Limit);
        Assert.Null(untracked.OnOverBudget);
        await TestHelpers.EventuallyAsync(() => Warnings(env).Length == 1, "the cap-suspended warning");
        Assert.Contains("restartAboveBytes", Warnings(env)[0], StringComparison.Ordinal);
        await Task.Delay(200, Ct);
        Assert.Equal(ServerSlot.MaxMemoryRestarts + 1, env.Connections.ConnectCount);
        Assert.Single(env.Manager.Servers, s => s.Id == "fake" && s.State == LanguageServerState.Running);
        await fourth.OnOverBudget!(); // a late callback of the old tracking changes nothing
        await Task.Delay(100, Ct);
        Assert.Equal(ServerSlot.MaxMemoryRestarts + 1, env.Connections.ConnectCount);
        Assert.Single(Warnings(env));

        // A manual restart enforces the cap again, with a fresh count.
        await env.Manager.RestartAsync("fake", Ct);
        await env.Connections.WaitForServerAsync(ServerSlot.MaxMemoryRestarts + 1);
        var rearmed = await TrackingAsync(env, ServerSlot.MaxMemoryRestarts + 2);
        Assert.Equal(Cap, rearmed.Limit);
        await rearmed.OnOverBudget!();
        await env.Connections.WaitForServerAsync(ServerSlot.MaxMemoryRestarts + 2);
    }

    [Fact]
    public async Task A_memory_restart_does_not_reset_the_crash_policy()
    {
        // Two crashes are allowed in the window. Before the fix a memory restart went through the user's Restart path, which cleared the crash
        // history: a server that crashes and goes over its cap in turn was then restarted forever.
        await using var env = await StartCappedAsync(c => c.Set(LspSettingKeys.MaxRestarts, 2));
        for (var i = 0; i < 2; i++)
        {
            env.Connections.Servers[i].Crash();
            var next = await env.Connections.WaitForServerAsync(i + 1);
            await next.WaitForAsync(LspMethods.TextDocumentDidOpen);
        }
        var tracked = await TrackingAsync(env, 2);
        await tracked.OnOverBudget!();
        var afterCap = await env.Connections.WaitForServerAsync(3);
        await afterCap.WaitForAsync(LspMethods.TextDocumentDidOpen);
        await TrackingAsync(env, 3);

        afterCap.Crash(); // the third crash in the window: Faulted, no fifth session
        await env.WaitForStateAsync("fake", LanguageServerState.Faulted);
        await Task.Delay(200, Ct);
        Assert.Equal(4, env.Connections.ConnectCount);
    }

    [Fact]
    public async Task Changing_the_cap_retracks_the_running_server_without_a_restart_and_rearms_a_suspended_cap()
    {
        await using var env = await StartCappedAsync();
        for (var i = 0; i < ServerSlot.MaxMemoryRestarts; i++)
        {
            await (await TrackingAsync(env, i)).OnOverBudget!();
            await (await env.Connections.WaitForServerAsync(i + 1)).WaitForAsync(LspMethods.TextDocumentDidOpen);
        }
        await (await TrackingAsync(env, ServerSlot.MaxMemoryRestarts)).OnOverBudget!();
        Assert.Null((await TrackingAsync(env, ServerSlot.MaxMemoryRestarts + 1)).Limit); // suspended

        var overrides = BuiltinLanguageServers.All.ToDictionary(d => d.Id, _ => (object)new { enabled = false });
        overrides["fake"] = new { restartAboveBytes = 5_000_000 };
        env.Config.Set(LspSettingKeys.LanguageServers, overrides);

        var retracked = await TrackingAsync(env, ServerSlot.MaxMemoryRestarts + 2);
        Assert.Equal(5_000_000, retracked.Limit);
        Assert.Equal(ServerSlot.MaxMemoryRestarts + 1, env.Connections.ConnectCount); // the cap is not part of the launch: no restart
        await retracked.OnOverBudget!();
        await env.Connections.WaitForServerAsync(ServerSlot.MaxMemoryRestarts + 1); // enforced again
    }

    private static async Task<LspTestEnv> StartCappedAsync(Action<CsCode.TestDoubles.TestConfigurationService>? configure = null)
    {
        var env = new LspTestEnv(configure, memoryMonitor: true);
        env.Connections.OnConnect = (s, _) => s.PidOverride = 4242; // the fake connection has no process: give the monitor a pid to track
        env.Manager.AddDefinition(LspTestEnv.FakeDef() with { RestartAboveBytes = Cap });
        env.Open("a.fake", "x");
        await (await env.Connections.WaitForServerAsync()).WaitForAsync(LspMethods.TextDocumentDidOpen);
        await TrackingAsync(env, 0);
        return env;
    }

    /// <summary>The <paramref name="index"/>-th TrackChildProcess call (one per session start, one per cap change).</summary>
    private static async Task<(string Name, int Pid, long? Limit, Func<ValueTask>? OnOverBudget)> TrackingAsync(LspTestEnv env, int index)
    {
        var memory = env.Memory!;
        await TestHelpers.EventuallyAsync(() => { lock (memory.Tracked) return memory.Tracked.Count > index; }, () => $"tracking #{index} (have {memory.Tracked.Count})\n{env.Dump()}");
        lock (memory.Tracked) return memory.Tracked[index];
    }

    private static string[] StatusMessages(LspTestEnv env) { lock (env.Window.StatusMessages) return env.Window.StatusMessages.ToArray(); }

    private static string[] Warnings(LspTestEnv env)
    {
        lock (env.Window.Messages) return env.Window.Messages.Where(m => m.Severity == MessageSeverity.Warning).Select(m => m.Message).ToArray();
    }
}
