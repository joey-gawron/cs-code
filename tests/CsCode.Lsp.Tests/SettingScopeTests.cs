using CsCode.Core.Platform;
using CsCode.Lsp.Runtime;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>lsp-core-1: what a language server runs comes from user settings, and a relative command never resolves inside the workspace.</summary>
public sealed class SettingScopeTests
{
    [Fact]
    public void The_search_dirs_and_the_launch_members_of_a_server_are_registered_machine_scoped()
    {
        var props = LspSettings.Properties().ToDictionary(p => p.Key);
        Assert.Equal(ConfigurationScope.Machine, props[LspSettingKeys.SearchDirs].Scope);
        Assert.Equal(["*.command", "*.args", "*.env", "*.workingDirectory"], props[LspSettingKeys.LanguageServers].MachineScopedMembers);
        Assert.False(props[LspSettingKeys.LanguageServers].IsUserSettingOnly); // the rest of an entry (settings, selector…) stays a workspace's to tune
    }

    [Fact]
    public async Task The_manager_registers_its_settings_before_reading_them()
    {
        var config = new OrderRecordingConfiguration();
        await using var env = new CsCode.Lsp.Tests.Harness.LspTestEnv();
        await using var manager = new CsCode.Lsp.Manager.LanguageServerManager(new LspServices(env.Documents, env.Registry, env.Diagnostics) { Configuration = config });
        Assert.True(config.FirstReadAfterRegistration, "cscode.lsp.* was read before its scopes were registered");
    }

    [Fact]
    public void A_relative_command_is_not_resolved_against_the_server_root_but_home_and_absolute_paths_are()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("unix permission bits"); return; }
        using var dir = new TempDirectory("cscode-relcmd-");
        var script = dir.WriteFile(".vscode/run.sh", "#!/bin/sh\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Assert.Null(ToolResolver.Resolve("./.vscode/run.sh", null, dir.Path, null));
        Assert.Null(ToolResolver.Resolve(".vscode/run.sh", null, dir.Path, null));
        Assert.Equal(script, ToolResolver.Resolve(script, null, dir.Path, null));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (script.StartsWith(home + "/", StringComparison.Ordinal))
            Assert.Equal(script, ToolResolver.Resolve("~/" + script[(home.Length + 1)..], null, null, null));
    }

    private sealed class OrderRecordingConfiguration : IConfigurationService
    {
        private readonly TestConfigurationService _inner = new();
        private bool _registered;
        private bool? _firstReadAfterRegistration;

        public bool FirstReadAfterRegistration => _firstReadAfterRegistration ?? false;

        private void Read(string key)
        {
            if (key.StartsWith("cscode.lsp.", StringComparison.Ordinal)) _firstReadAfterRegistration ??= _registered;
        }

        public T Get<T>(string key, T defaultValue) { Read(key); return _inner.Get(key, defaultValue); }
        public T? Get<T>(string key) { Read(key); return _inner.Get<T>(key); }
        public System.Text.Json.JsonElement? GetRaw(string key) { Read(key); return _inner.GetRaw(key); }
        public ValueTask UpdateAsync(string key, object? value, ConfigurationTarget target = ConfigurationTarget.User, CancellationToken ct = default) => _inner.UpdateAsync(key, value, target, ct);
        public IDisposable RegisterProperties(IEnumerable<ConfigurationProperty> properties) { _registered = true; return _inner.RegisterProperties(properties); }
        public IReadOnlyList<ConfigurationProperty> Properties => _inner.Properties;
        public CsCode.Core.Lifecycle.IEventSource<ConfigurationChangedEventArgs> Changed => _inner.Changed;
        public string UserSettingsPath => _inner.UserSettingsPath;
    }
}
