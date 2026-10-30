using System.Text.Json;
using CsCode.Lsp.Manager;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>T39: the servers built on the TypeScript engine carry a restart cap (on their process tree, lsp-core-4); a user can change or remove it.</summary>
public sealed class RestartCapTests
{
    [Fact]
    public void The_typescript_engine_servers_restart_above_vs_code_s_tsserver_memory_and_the_others_have_no_cap()
    {
        var capped = BuiltinLanguageServers.All.Where(d => d.RestartAboveBytes is not null).Select(d => d.Id).Order().ToArray();
        Assert.Equal(["svelte", "typescript", "vue"], capped);
        Assert.All(BuiltinLanguageServers.All.Where(d => d.RestartAboveBytes is not null), d => Assert.Equal(3L * 1024 * 1024 * 1024, d.RestartAboveBytes));
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
}
