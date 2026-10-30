using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Platform.Configuration;
using CsCode.Platform.Io;
using CsCode.Platform.Workspace;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Platform.Tests;

/// <summary>
/// lsp-core-1 / remote-5: VS Code's setting scopes. Application and machine settings (and the machine-scoped members of an object setting, such as a
/// language server's command) come from the defaults and the USER file only, so a cloned repository's .vscode/settings.json cannot choose the
/// programs CsCode starts. lsp-core-2: only top-level keys are split on dots. lsp-core-7: every folder's settings are watched.
/// </summary>
public sealed class ConfigurationScopeTests : IDisposable
{
    private readonly ConfigFixture _f = new();
    public void Dispose() => _f.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ConfigurationProperty[] Registry =
    [
        new("remote.ssh.path", JsonSerializer.SerializeToElement("ssh"), "ssh client") { Scope = ConfigurationScope.Machine },
        new("remote.ssh.options", JsonSerializer.SerializeToElement(Array.Empty<string>()), "ssh -o") { Scope = ConfigurationScope.Machine },
        new("remote.ssh.connectTimeout", JsonSerializer.SerializeToElement(30), "seconds"), // window scope: a workspace may set it
        new("cscode.lsp.searchDirs", JsonSerializer.SerializeToElement(new[] { "~/.cargo/bin" }), "dirs") { Scope = ConfigurationScope.Machine },
        new("cscode.languageServers", null, "servers") { MachineScopedMembers = ["*.command", "*.args", "*.env", "*.workingDirectory"] },
    ];

    private ConfigurationService Create(RecordingWindowService? window = null)
    {
        var svc = new ConfigurationService(_f.Paths, _f.Lane, _f.Watcher, _f.Options, windowService: window is null ? null : () => window);
        svc.RegisterProperties(Registry);
        return svc;
    }

    // ---- lsp-core-1: workspace files cannot start programs ---------------------------------------------------------------------------

    [Fact]
    public async Task A_workspace_cannot_define_a_language_server_command_args_env_or_working_directory()
    {
        // The reviewer's repro: a committed .vscode/settings.json that defines a new server running a script of the repository.
        _f.WriteWorkspace(".vscode", """
            {
              "cscode.languageServers": {
                "x": { "command": "./.vscode/run.sh", "args": ["-c", "id"], "selector": ["markdown"], "settings": { "a": 1 } },
                "typescript": { "env": { "NODE_OPTIONS": "--require ./.vscode/x.js" }, "workingDirectory": ".vscode", "initializationOptions": { "b": 2 } }
              }
            }
            """);
        var window = new RecordingWindowService();
        using var svc = Create(window);
        await _f.OpenWorkspace(svc);

        var servers = svc.GetRaw("cscode.languageServers")!.Value;
        var x = servers.GetProperty("x");
        Assert.False(x.TryGetProperty("command", out _));
        Assert.False(x.TryGetProperty("args", out _));
        Assert.Equal("markdown", x.GetProperty("selector")[0].GetString()); // the rest of the entry is a workspace's to tune
        Assert.Equal(1, x.GetProperty("settings").GetProperty("a").GetInt32());
        var ts = servers.GetProperty("typescript");
        Assert.False(ts.TryGetProperty("env", out _));
        Assert.False(ts.TryGetProperty("workingDirectory", out _));
        Assert.Equal(2, ts.GetProperty("initializationOptions").GetProperty("b").GetInt32());

        Assert.Equal(
            ["cscode.languageServers.typescript.env", "cscode.languageServers.typescript.workingDirectory", "cscode.languageServers.x.args", "cscode.languageServers.x.command"],
            svc.IgnoredSettings.Select(i => i.Key).Order(StringComparer.Ordinal));
        Assert.All(svc.IgnoredSettings, i => Assert.EndsWith(Path.Combine(".vscode", "settings.json"), i.Path));
        var notice = Assert.Single(window.Messages);
        Assert.Equal(MessageSeverity.Warning, notice.Severity);
        Assert.Contains("cscode.languageServers.x.command", notice.Message);
        Assert.Contains("user settings", notice.Message);
    }

    [Fact]
    public async Task The_user_s_own_server_command_survives_a_workspace_that_overrides_it()
    {
        _f.WriteUser("""{ "cscode.languageServers": { "mine": { "command": "/opt/mine/server", "args": ["--stdio"], "selector": ["foo"] } } }""");
        _f.WriteWorkspace(".cscode", """
            { "cscode.languageServers.mine.command": "/bin/sh", "cscode.languageServers.mine.args": ["-c", "evil"], "cscode.languageServers.mine.rootMarkers": ["x.json"] }
            """);
        using var svc = Create();
        await _f.OpenWorkspace(svc);

        var mine = svc.GetRaw("cscode.languageServers")!.Value.GetProperty("mine");
        Assert.Equal("/opt/mine/server", mine.GetProperty("command").GetString());
        Assert.Equal("--stdio", Assert.Single(mine.GetProperty("args").EnumerateArray()).GetString());
        Assert.Equal("x.json", mine.GetProperty("rootMarkers")[0].GetString());
        Assert.Equal(2, svc.IgnoredSettings.Count);
    }

    [Fact]
    public async Task Machine_settings_come_from_user_settings_in_every_spelling_a_workspace_could_use()
    {
        _f.WriteUser("""{ "remote.ssh.path": "/usr/local/bin/ssh", "cscode.lsp.searchDirs": ["/home/me/bin"] }""");
        _f.WriteWorkspace(".vscode", """{ "remote.ssh.path": "./evil", "remote": { "ssh": { "options": ["ProxyCommand=sh -c 'id'"] } }, "remote.ssh.connectTimeout": 5 }""");
        _f.WriteWorkspace(".cscode", """{ "cscode": { "lsp": { "searchDirs": ["./node_modules/.bin"] } }, "[go]": { "cscode.lsp.searchDirs": ["./x"] } }""");
        using var svc = Create();
        await _f.OpenWorkspace(svc);

        Assert.Equal("/usr/local/bin/ssh", svc.Get<string>("remote.ssh.path"));
        Assert.Empty(svc.Get<string[]>("remote.ssh.options")!);                      // the default, not the workspace's ProxyCommand
        Assert.Equal(["/home/me/bin"], svc.Get<string[]>("cscode.lsp.searchDirs")!);
        Assert.Equal(5, svc.Get<int>("remote.ssh.connectTimeout"));                  // window scope: the workspace wins as before
        Assert.False(svc.GetRaw("[go]") is { } go && go.TryGetProperty("cscode", out _));      // nor inside a language block
        Assert.Equal(
            ["[go].cscode.lsp.searchDirs", "cscode.lsp.searchDirs", "remote.ssh.options", "remote.ssh.path"],
            svc.IgnoredSettings.Select(i => i.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_workspace_that_replaces_a_parent_object_with_a_scalar_does_not_hide_the_user_s_value()
    {
        _f.WriteUser("""{ "remote.ssh.path": "/usr/local/bin/ssh" }""");
        _f.WriteWorkspace(".cscode", """{ "remote": 1 }""");
        using var svc = Create();
        await _f.OpenWorkspace(svc);
        Assert.Equal("/usr/local/bin/ssh", svc.Get<string>("remote.ssh.path"));
        Assert.NotNull(svc.Get<string[]>("remote.ssh.options")); // machine-scoped: restored from the defaults (window-scoped siblings are the workspace's)
        Assert.Contains(svc.IgnoredSettings, i => i.Key == "remote.ssh.path");
    }

    [Fact]
    public async Task Additional_folders_are_restricted_too()
    {
        var second = _f.Temp.Combine("ws2");
        Directory.CreateDirectory(Path.Combine(second, ".cscode"));
        File.WriteAllText(Path.Combine(second, ".cscode", "settings.json"), """{ "remote.ssh.path": "/tmp/evil", "remote.ssh.connectTimeout": 9 }""");
        using var svc = Create();
        await _f.OpenWorkspace(svc, _f.WorkspaceDir, second);
        Assert.Equal("ssh", svc.Get<string>("remote.ssh.path"));
        Assert.Equal(9, svc.Get<int>("remote.ssh.connectTimeout"));
        Assert.Equal(Path.Combine(second, ".cscode", "settings.json"), Assert.Single(svc.IgnoredSettings).Path);
    }

    [Fact]
    public async Task A_late_registration_restricts_values_that_were_already_loaded()
    {
        _f.WriteWorkspace(".cscode", """{ "cscode.debug.gdbPath": "./gdb" }""");
        using var svc = new ConfigurationService(_f.Paths, _f.Lane, _f.Watcher, _f.Options);
        await _f.OpenWorkspace(svc);
        Assert.Equal("./gdb", svc.Get<string>("cscode.debug.gdbPath")); // nobody registered it yet: window scope

        using var reg = svc.RegisterProperties([new("cscode.debug.gdbPath", JsonSerializer.SerializeToElement(""), "gdb") { Scope = ConfigurationScope.Machine }]);
        Assert.Equal("", svc.Get<string>("cscode.debug.gdbPath"));
    }

    [Fact]
    public async Task The_notice_is_shown_once_per_file_and_key_not_on_every_reload()
    {
        _f.WriteWorkspace(".cscode", """{ "remote.ssh.path": "./evil" }""");
        var window = new RecordingWindowService();
        using var svc = Create(window);
        var changes = new List<IReadOnlyList<IgnoredSetting>>();
        using var sub = svc.IgnoredSettingsChanged.Subscribe(changes.Add);
        await _f.OpenWorkspace(svc);
        _f.WriteWorkspace(".cscode", """{ "remote.ssh.path": "./evil", "editor.tabSize": 2 }""");
        await svc.ReloadAsync(Ct);
        _f.WriteWorkspace(".cscode", """{ "remote.ssh.path": "./evil", "remote.ssh.options": ["-v"] }""");
        await svc.ReloadAsync(Ct);

        Assert.Equal(2, window.Messages.Count); // remote.ssh.path once, remote.ssh.options once
        Assert.Contains("remote.ssh.options", window.Messages[1].Message);
        Assert.DoesNotContain("remote.ssh.path", window.Messages[1].Message);
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public async Task Writing_a_user_only_setting_into_workspace_settings_is_refused_but_removing_one_is_allowed()
    {
        using var svc = Create();
        await _f.OpenWorkspace(svc);
        var ex = Assert.Throws<InvalidOperationException>(() => { _ = svc.UpdateAsync("remote.ssh.path", "./evil", ConfigurationTarget.Workspace, Ct); });
        Assert.Contains("user settings", ex.Message);
        Assert.Throws<InvalidOperationException>(() => { _ = svc.UpdateAsync("cscode.languageServers.x.command", "sh", ConfigurationTarget.Workspace, Ct); });
        Assert.Throws<InvalidOperationException>(() => { _ = svc.UpdateAsync("cscode.languageServers.x.env.PATH", "/x", ConfigurationTarget.Workspace, Ct); });

        await svc.UpdateAsync("cscode.languageServers.x.settings", new { a = 1 }, ConfigurationTarget.Workspace, Ct); // not machine-scoped
        await svc.UpdateAsync("remote.ssh.path", null, ConfigurationTarget.Workspace, Ct);                       // a removal is always fine
        await svc.UpdateAsync("remote.ssh.path", "/opt/ssh", ConfigurationTarget.User, Ct);
        Assert.Equal("/opt/ssh", svc.Get<string>("remote.ssh.path"));
    }

    [Fact]
    public async Task Closing_the_folder_clears_the_ignored_list()
    {
        _f.WriteWorkspace(".cscode", """{ "remote.ssh.path": "./evil" }""");
        using var svc = Create();
        await _f.OpenWorkspace(svc);
        Assert.Single(svc.IgnoredSettings);
        await ((IWorkspaceBound)svc).OnWorkspaceFoldersChangedAsync([], Ct);
        Assert.Empty(svc.IgnoredSettings);
    }

    // ---- lsp-core-2: member names inside values are never split ---------------------------------------------------------------------

    [Fact]
    public void Globs_urls_and_server_ids_with_dots_inside_values_are_kept_as_written()
    {
        _f.WriteUser("""
            {
              "files.associations": { "*.conf": "nix", "Dockerfile*": "dockerfile" },
              "yaml.schemas": { "https://json.schemastore.org/github-workflow.json": ".github/workflows/*" },
              "cscode.languageServers": { "my.server": { "command": "srv", "settings": { "yaml": { "schemas": { "https://a.b/c.json": "*.y" } } } } },
              "[markdown]": { "files.associations": { "*.md.txt": "markdown" } }
            }
            """);
        using var svc = Create();

        var assoc = svc.GetRaw("files.associations")!.Value;
        Assert.Equal("nix", assoc.GetProperty("*.conf").GetString());
        Assert.Equal("dockerfile", assoc.GetProperty("Dockerfile*").GetString());
        Assert.Equal(".github/workflows/*", svc.GetRaw("yaml")!.Value.GetProperty("schemas").GetProperty("https://json.schemastore.org/github-workflow.json").GetString());
        var server = svc.GetRaw("cscode.languageServers")!.Value.GetProperty("my.server");
        Assert.Equal("srv", server.GetProperty("command").GetString());
        Assert.Equal("*.y", server.GetProperty("settings").GetProperty("yaml").GetProperty("schemas").GetProperty("https://a.b/c.json").GetString());
        Assert.Equal("markdown", svc.GetRaw("[markdown]")!.Value.GetProperty("files").GetProperty("associations").GetProperty("*.md.txt").GetString());
    }

    [Fact]
    public async Task A_nested_member_change_still_reports_its_setting()
    {
        _f.WriteUser("""{ "files.associations": { "*.conf": "nix" } }""");
        using var svc = Create();
        var seen = new List<string>();
        using var sub = svc.Changed.Subscribe(e => { lock (seen) seen.AddRange(e.AffectedKeys); });
        _f.WriteUser("""{ "files.associations": { "*.conf": "ini" } }""");
        await svc.ReloadAsync(Ct);
        Assert.Contains(seen, k => new ConfigurationChangedEventArgs([k]).Affects("files.associations"));
    }

    // ---- remote windows: the remote host's machine settings (VS Code's remote user settings) ------------------------------------------

    private static readonly ConfigurationProperty[] RemoteRegistry =
    [
        new("cscode.debug.gdbPath", JsonSerializer.SerializeToElement(""), "gdb") { Scope = ConfigurationScope.Machine },
        new("terminal.integrated.inheritEnv", JsonSerializer.SerializeToElement(true), "inherit") { Scope = ConfigurationScope.Application },
    ];

    [Fact]
    public async Task A_remote_window_takes_machine_settings_from_the_host_s_machine_file_and_ignores_the_client_s()
    {
        // The reviewer's case: the remote gdb is /opt/gdb-14/bin/gdb, the client's user settings name the client's own gdb. VS Code reads machine
        // settings in a remote window from the remote host's machine settings file (Remote [SSH: host] tab), and drops the local user values.
        _f.WriteUser("""
            {
              "cscode.debug.gdbPath": "/client/bin/gdb",
              "cscode.lsp.searchDirs": ["/client/tools"],
              "cscode.languageServers": { "rust": { "command": "/client/rust-analyzer", "settings": { "a": 1 } }, "go": { "command": "/client/gopls" } },
              "terminal.integrated.inheritEnv": false,
              "editor.tabSize": 3,
              "editor.fontSize": 11
            }
            """);
        var machine = _f.Temp.Combine("server-data", "machine", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(machine)!);
        File.WriteAllText(machine, """
            {
              "cscode.debug.gdbPath": "/opt/gdb-14/bin/gdb",
              "cscode.languageServers": { "rust": { "command": "/opt/ra/rust-analyzer" } },
              "terminal.integrated.inheritEnv": true,
              "editor.tabSize": 5
            }
            """);
        _f.WriteWorkspace(".vscode", """{ "cscode.debug.gdbPath": "./evil-gdb", "cscode.languageServers": { "rust": { "command": "./evil" } }, "editor.fontSize": 14 }""");
        using var svc = Create();
        using var reg = svc.RegisterProperties(RemoteRegistry);
        await _f.OpenWorkspace(svc);
        Assert.Equal("/client/bin/gdb", svc.Get("cscode.debug.gdbPath", "?")); // a local window: the user's own machine

        await svc.SetRemoteMachineSettingsAsync(machine, Ct);
        Assert.Equal(machine, svc.MachineSettingsPath);
        Assert.Equal("/opt/gdb-14/bin/gdb", svc.Get("cscode.debug.gdbPath", "?"));    // machine scope: the host's file, not the client's, not the workspace's
        var servers = svc.GetRaw("cscode.languageServers")!.Value;
        Assert.Equal("/opt/ra/rust-analyzer", servers.GetProperty("rust").GetProperty("command").GetString());
        Assert.Equal(1, servers.GetProperty("rust").GetProperty("settings").GetProperty("a").GetInt32()); // a window-scoped member of the client's entry stays
        Assert.False(servers.GetProperty("go").TryGetProperty("command", out _));     // the client's gopls path is not this host's
        Assert.Equal(["~/.cargo/bin"], svc.Get<string[]>("cscode.lsp.searchDirs") ?? []);   // the default: the client's dirs are not this host's
        Assert.False(svc.Get("terminal.integrated.inheritEnv", true));                // application scope: the user file only
        Assert.Equal(5, svc.Get("editor.tabSize", 0));                                 // window scope: user < machine
        Assert.Equal(14, svc.Get("editor.fontSize", 0));                               //               machine < workspace
        Assert.Contains(svc.IgnoredSettings, i => i.Key == "cscode.debug.gdbPath");   // the workspace's value is still reported

        await svc.SetRemoteMachineSettingsAsync(null, Ct); // the process language host: the client's machine is this one
        Assert.Null(svc.MachineSettingsPath);
        Assert.Equal("/client/bin/gdb", svc.Get("cscode.debug.gdbPath", "?"));
        Assert.Equal("/client/gopls", svc.GetRaw("cscode.languageServers")!.Value.GetProperty("go").GetProperty("command").GetString());
        Assert.Equal(3, svc.Get("editor.tabSize", 0));
    }

    [Fact]
    public async Task A_remote_host_without_a_machine_file_uses_the_defaults_and_picks_up_one_written_later()
    {
        _f.WriteUser("""{ "cscode.debug.gdbPath": "/client/bin/gdb" }""");
        var machine = _f.Temp.Combine("server-data", "machine", "settings.json");
        using var svc = Create();
        using var reg = svc.RegisterProperties(RemoteRegistry);
        await svc.SetRemoteMachineSettingsAsync(machine, Ct);
        Assert.Equal("", svc.Get("cscode.debug.gdbPath", "?"));
        Assert.Contains(_f.Watcher.ActiveRoots, r => r == Path.GetDirectoryName(machine));

        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = svc.Changed.Subscribe(e => { if (e.Affects("cscode.debug.gdbPath")) fired.TrySetResult(); });
        File.WriteAllText(machine, """{ "cscode.debug.gdbPath": "/opt/gdb-14/bin/gdb" }""");
        _f.Watcher.Raise(machine);
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("/opt/gdb-14/bin/gdb", svc.Get("cscode.debug.gdbPath", "?"));

        File.WriteAllText(machine, """{ "cscode.debug.gdbPath": """); // malformed: the last good value stays, the error is reported
        await svc.ReloadAsync(Ct);
        Assert.Equal("/opt/gdb-14/bin/gdb", svc.Get("cscode.debug.gdbPath", "?"));
        Assert.Equal(machine, svc.LastError?.Path);

        var count = _f.Watcher.ActiveCount;
        await svc.SetRemoteMachineSettingsAsync(null, Ct);
        Assert.Equal(count - 1, _f.Watcher.ActiveCount); // the machine directory is no longer watched
        Assert.Null(svc.LastError);
    }

    // ---- lsp-core-7: every folder's settings are watched ----------------------------------------------------------------------------

    [Fact]
    public async Task An_edit_to_an_additional_folder_s_settings_is_applied_live()
    {
        var second = _f.Temp.Combine("ws2");
        var file = Path.Combine(second, ".cscode", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{ "probe.value": 1 }""");
        using var svc = Create();
        await _f.OpenWorkspace(svc, _f.WorkspaceDir, second);
        Assert.Equal(1, svc.Get<int>("probe.value"));
        Assert.Contains(_f.Watcher.ActiveRoots, r => r == Path.Combine(second, ".cscode"));
        Assert.Contains(_f.Watcher.ActiveRoots, r => r == second);

        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = svc.Changed.Subscribe(e => { if (e.Affects("probe.value")) fired.TrySetResult(); });
        File.WriteAllText(file, """{ "probe.value": 2 }""");
        _f.Watcher.Raise(file);
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(2, svc.Get<int>("probe.value"));
    }

    [Fact]
    public async Task A_dot_cscode_directory_created_later_in_an_additional_folder_is_discovered()
    {
        var second = _f.Temp.Combine("ws3");
        Directory.CreateDirectory(second);
        using var svc = Create();
        await _f.OpenWorkspace(svc, _f.WorkspaceDir, second);
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = svc.Changed.Subscribe(e => { if (e.Affects("probe.value")) fired.TrySetResult(); });

        var file = Path.Combine(second, ".cscode", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{ "probe.value": 3 }""");
        _f.Watcher.Raise(Path.GetDirectoryName(file)!, FileChangeKind.Created);
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(3, svc.Get<int>("probe.value"));
        for (var i = 0; i < 100 && !_f.Watcher.ActiveRoots.Contains(Path.Combine(second, ".cscode")); i++) await Task.Delay(20, Ct);
        Assert.Contains(Path.Combine(second, ".cscode"), _f.Watcher.ActiveRoots);
    }

    [Fact]
    public async Task Changing_folders_does_not_accumulate_watches()
    {
        var second = _f.Temp.Combine("ws4");
        Directory.CreateDirectory(Path.Combine(second, ".cscode"));
        using var svc = Create();
        await _f.OpenWorkspace(svc, _f.WorkspaceDir, second);
        var count = _f.Watcher.ActiveCount;
        await _f.OpenWorkspace(svc, _f.WorkspaceDir, second);
        Assert.Equal(count, _f.Watcher.ActiveCount);
        await _f.OpenWorkspace(svc, _f.WorkspaceDir);
        Assert.True(_f.Watcher.ActiveCount < count);
    }
}
