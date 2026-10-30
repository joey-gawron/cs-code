using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Terminal;
using CsCode.Dap.Service;
using CsCode.Platform;
using CsCode.Platform.Configuration;
using CsCode.Platform.Io;
using CsCode.Remote;
using CsCode.Server;
using CsCode.Terminal.Sessions;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>
/// lsp-core-1 / remote-5: the audit of every setting that names a program CsCode runs, its arguments, its environment or where it is searched for.
/// Each must be user-only (application or machine scope), in the GUI's registrations and in the remote server's composition, so a cloned
/// repository's (or a remote folder's) .vscode/settings.json cannot start anything.
/// </summary>
public sealed class SettingScopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The audit list (docs/PLATFORM.md "Setting scopes"). A new executable setting must be added here AND registered machine-scoped.</summary>
    public static readonly string[] ProgramSettings =
    [
        "cscode.lsp.searchDirs",
        "cscode.debuggers", "cscode.debug.netcoredbgPath", "cscode.debug.jsDebugPath", "cscode.debug.nodePath", "cscode.debug.gdbPath", "cscode.debug.gdbArgs",
        "cscode.debug.codelldbPath",
        "terminal.integrated.profiles.linux", "terminal.integrated.profiles.osx", "terminal.integrated.profiles.windows",
        "terminal.integrated.defaultProfile.linux", "terminal.integrated.defaultProfile.osx", "terminal.integrated.defaultProfile.windows",
        "terminal.integrated.env.linux", "terminal.integrated.env.osx", "terminal.integrated.env.windows", "terminal.integrated.inheritEnv",
        "remote.ssh.path", "remote.ssh.configFile", "remote.ssh.options", "remote.ssh.serverInstallPath", "remote.ssh.serverDataPath", "remote.ssh.serverBundles",
        "remote.ssh.hosts", "remote.docker.path", "remote.wsl.path",
    ];

    [Fact]
    public void Every_setting_that_chooses_a_program_is_registered_user_only_by_its_module()
    {
        var registered = RemoteSettings.Properties().Concat(TerminalSettingKeys.Properties).Concat(DebugService.SettingProperties())
            .ToDictionary(p => p.Key, StringComparer.Ordinal);
        foreach (var key in ProgramSettings.Where(k => k != "cscode.lsp.searchDirs"))
        {
            Assert.True(registered.TryGetValue(key, out var p), $"{key} is not registered");
            Assert.True(p!.IsUserSettingOnly, $"{key} is {p.Scope}: a workspace could set it");
        }
    }

    [Fact]
    public async Task The_remote_server_registers_the_same_scopes_and_a_remote_folder_cannot_choose_its_shell_debugger_or_language_server()
    {
        using var data = new TempDirectory("cscode-scope-server-");
        using var folder = new TempDirectory("cscode-scope-folder-");
        folder.WriteFile(".vscode/settings.json", """
            {
              "terminal.integrated.profiles.linux": { "evil": { "path": "/bin/sh", "args": ["-c", "id > /tmp/pwned"] } },
              "terminal.integrated.defaultProfile.linux": "evil",
              "terminal.integrated.env.linux": { "PROMPT_COMMAND": "id > /tmp/pwned", "SCOPE_PROBE": "workspace" },
              "cscode.debug.gdbPath": "./evil-gdb",
              "cscode.lsp.searchDirs": ["./node_modules/.bin"],
              "cscode.languageServers": { "x": { "command": "/bin/sh", "args": ["-c", "id"], "selector": ["markdown"] } },
              "terminal.integrated.scrollback": 77
            }
            """);
        var host = new ServerHost(new ServerOptions(Stdio: false, data.Path, Marker: null, ShowVersion: false, AskpassPrompt: null, Askpass: false));
        try
        {
            var sp = host.Services;
            _ = sp.GetRequiredService<ILanguageServerManager>(); // the language host creates it on start: it registers the LSP settings
            var config = sp.GetRequiredService<ConfigurationService>();
            var props = config.Properties.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            foreach (var key in ProgramSettings.Where(k => !k.StartsWith("remote.", StringComparison.Ordinal)))
                Assert.True(props.TryGetValue(key, out var p) && p.IsUserSettingOnly, $"{key} is not user-only on the server");
            Assert.Contains("*.command", props["cscode.languageServers"].MachineScopedMembers!);

            await sp.GetRequiredService<IWorkspaceService>().OpenFolderAsync(FileUri.FromDirectoryPath(folder.Path), Ct);

            Assert.Equal(77, config.Get("terminal.integrated.scrollback", 0)); // window-scoped: the folder's value applies
            Assert.Equal("", config.Get("cscode.debug.gdbPath", "unset"));
            Assert.Null(config.GetRaw("terminal.integrated.defaultProfile.linux"));
            Assert.False(config.GetRaw("cscode.languageServers")!.Value.GetProperty("x").TryGetProperty("command", out _));
            Assert.DoesNotContain("./node_modules/.bin", config.Get<string[]>("cscode.lsp.searchDirs") ?? []);

            var resolver = new TerminalLaunchResolver(config, sp.GetRequiredService<IWorkspaceService>(),
                () => new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["SHELL"] = "/bin/sh", ["HOME"] = data.Path });
            Assert.DoesNotContain(resolver.Profiles(), p => p.Name == "evil");
            var env = resolver.Environment(new TerminalCreateOptions(), null);
            Assert.False(env.ContainsKey("PROMPT_COMMAND"));
            Assert.False(env.ContainsKey("SCOPE_PROBE"));
            Assert.Contains(config.IgnoredSettings, i => i.Key == "terminal.integrated.defaultProfile.linux");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_local_folder_cannot_set_the_ssh_program_or_its_options_for_connect_to_host()
    {
        using var temp = new TempDirectory("cscode-scope-remote-");
        var paths = new AppPaths(k => k == AppPaths.HomeEnvironmentVariable ? temp.Combine("home") : null);
        using var lane = new IoLane("test.scope", 1);
        using var config = new ConfigurationService(paths, lane);
        using var reg = config.RegisterProperties(RemoteSettings.Properties());
        temp.WriteFile("ws/.vscode/settings.json", """
            { "remote.ssh.options": ["ProxyCommand=sh -c 'id > /tmp/pwned; exec nc %h %p'"], "remote.ssh.path": "./evil", "remote.docker.path": "/repo/evil",
              "remote.ssh.hosts": { "box": { "options": ["LocalCommand=id"] } } }
            """);
        await ((CsCode.Platform.Workspace.IWorkspaceBound)config).OnWorkspaceFoldersChangedAsync([FileUri.FromDirectoryPath(temp.Combine("ws"))], Ct);

        Assert.Equal("ssh", RemoteSettings.For(config, "box", RemoteSettings.SshPath, "?"));
        Assert.Empty(RemoteSettings.For(config, "box", RemoteSettings.Options, new[] { "?" }));
        Assert.Equal("docker", config.Get(RemoteSettings.DockerPath, "?"));
        Assert.Equal(4, config.IgnoredSettings.Count);
        Assert.Equal(JsonValueKind.Object, config.GetRaw(RemoteSettings.Hosts)!.Value.ValueKind);
        Assert.False(config.GetRaw(RemoteSettings.Hosts)!.Value.TryGetProperty("box", out _));
    }

    // ---- remote windows: the remote host's own machine settings (VS Code's "Remote [SSH: host]" settings) ----------------------------------

    [Fact]
    public async Task A_remote_server_takes_machine_settings_from_its_host_s_machine_file_and_the_process_language_host_from_the_client()
    {
        using var data = new TempDirectory("cscode-scope-server-");
        var machine = ServerHost.MachineSettingsPath(data.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(machine)!);
        File.WriteAllText(machine, """{ "cscode.debug.gdbPath": "/opt/gdb-14/bin/gdb", "cscode.languageServers": { "rust": { "command": "/opt/ra/rust-analyzer" } }, "editor.tabSize": 5 }""");
        var host = new ServerHost(new ServerOptions(Stdio: false, data.Path, Marker: null, ShowVersion: false, AskpassPrompt: null, Askpass: false));
        try
        {
            var sp = host.Services;
            _ = sp.GetRequiredService<ILanguageServerManager>();
            var config = sp.GetRequiredService<ConfigurationService>();
            var handlers = new WorkspaceHandlers(sp.GetRequiredService<CsCode.Platform.Workspace.WorkspaceService>(), config, sp.GetRequiredService<IFileSystem>(), machine);
            const string client = """
                { "cscode.debug.gdbPath": "/client/gdb", "cscode.languageServers": { "rust": { "command": "/client/rust-analyzer", "rootMarkers": ["Cargo.toml"] } },
                  "editor.tabSize": 3 }
                """;

            await handlers.SetUserSettingsAsync(client, remoteWindow: true, Ct); // ssh / docker / wsl
            Assert.Equal(machine, config.MachineSettingsPath);
            Assert.Equal("/opt/gdb-14/bin/gdb", config.Get("cscode.debug.gdbPath", "?"));
            var rust = config.GetRaw("cscode.languageServers")!.Value.GetProperty("rust");
            Assert.Equal("/opt/ra/rust-analyzer", rust.GetProperty("command").GetString());
            Assert.Equal("Cargo.toml", rust.GetProperty("rootMarkers")[0].GetString()); // the rest of the client's entry still applies
            Assert.Equal(5, config.Get("editor.tabSize", 0));

            await handlers.SetUserSettingsAsync(client, remoteWindow: false, Ct); // the local process language host: the client's machine is this one
            Assert.Null(config.MachineSettingsPath);
            Assert.Equal("/client/gdb", config.Get("cscode.debug.gdbPath", "?"));
            Assert.Equal(3, config.Get("editor.tabSize", 0));
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_remote_terminal_gets_the_environment_of_the_server_host_s_machine_settings_not_the_client_s()
    {
        Assert.SkipUnless(CsCode.Terminal.Pty.PtyHosts.IsSupported, "needs a PTY");
        await using var host = new RemoteTestHost(r3: true,
            userSettings: """{ "terminal.integrated.env.linux": { "SCOPE_PROBE": "client-machine", "ONLY_CLIENT": "x" } }""",
            machineSettings: """{ "terminal.integrated.env.linux": { "SCOPE_PROBE": "remote-machine" } }""");
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();
        var terminals = host.Services.GetRequiredService<TerminalService>();
        var t = (TerminalSession)await terminals.CreateAsync(TerminalProbe.Bash(), Ct);
        try
        {
            Assert.Contains(t.ProcessId!.Value, TestEnv.Descendants(host.Manager.Session!.Server.ProcessId)); // it runs on the server
            await t.SendTextAsync("echo \"probe=[$SCOPE_PROBE] only=[$ONLY_CLIENT]\"", true, Ct);
            await TerminalProbe.WaitForTextAsync(t, "probe=[remote-machine] only=[]");
        }
        finally
        {
            await terminals.KillAsync(t, Ct);
        }
    }
}
