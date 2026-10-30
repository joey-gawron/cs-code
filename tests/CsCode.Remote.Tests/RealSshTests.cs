using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Core.Text;
using CsCode.Remote.Transport;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>
/// Our OWN sshd (Nix store or /usr/sbin), run as the current user on a free loopback port with a config, host key, client keys and authorized_keys
/// generated in a temp dir. The session's HOME, XDG dirs and PATH are set by the server config (SetEnv) to temp/test values, so nothing on the
/// "remote" side touches the user's home. Never reads ~/.ssh, never uses the agent; stopped by PID.
/// </summary>
public sealed class SshdFixture : IAsyncLifetime
{
    public const string Passphrase = "correct horse; $(battery) 'staple'";
    private Process? _sshd;

    public string? SkipReason { get; private set; }
    public TempDirectory Dir { get; } = new("cscode-sshd-");
    public int Port { get; private set; }
    public string SshConfig => Dir.Combine("ssh_config");
    public string RemoteHome => Dir.Combine("remote-home");
    public string SshdLog => Dir.Combine("sshd.log");
    public int? Pid => _sshd?.Id;

    private static string? FindSshd()
    {
        if (File.Exists("/usr/sbin/sshd")) return "/usr/sbin/sshd";
        if (!Directory.Exists("/nix/store")) return null;
        return Directory.EnumerateDirectories("/nix/store", "*-openssh-*")
            .Where(d => !d.EndsWith("-man", StringComparison.Ordinal) && File.Exists(Path.Combine(d, "bin", "sshd")) && Directory.Exists(Path.Combine(d, "libexec")))
            .OrderByDescending(d => d, StringComparer.Ordinal).Select(d => Path.Combine(d, "bin", "sshd")).FirstOrDefault();
    }

    private static void Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("SSH_AUTH_SOCK");
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
    }

    public async ValueTask InitializeAsync()
    {
        if (OperatingSystem.IsWindows()) { SkipReason = "the sshd fixture is written for Unix"; return; }
        var sshd = FindSshd();
        if (sshd is null) { SkipReason = "no sshd: neither /usr/sbin/sshd nor an openssh in /nix/store"; return; }
        if (!File.Exists("/usr/bin/ssh") || !File.Exists("/usr/bin/ssh-keygen")) { SkipReason = "no ssh client / ssh-keygen in /usr/bin"; return; }
        var keygen = "/usr/bin/ssh-keygen";
        Run(keygen, "-q", "-t", "ed25519", "-N", "", "-f", Dir.Combine("host_key"));
        Run(keygen, "-q", "-t", "ed25519", "-N", "", "-C", "cscode-test", "-f", Dir.Combine("client_key"));
        Run(keygen, "-q", "-t", "ed25519", "-N", Passphrase, "-C", "cscode-test-pass", "-f", Dir.Combine("client_key_pass"));
        File.WriteAllText(Dir.Combine("authorized_keys"), File.ReadAllText(Dir.Combine("client_key.pub")) + File.ReadAllText(Dir.Combine("client_key_pass.pub")));
        File.SetUnixFileMode(Dir.Combine("authorized_keys"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.CreateDirectory(RemoteHome);

        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        var user = Environment.UserName;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        File.WriteAllText(Dir.Combine("sshd_config"), $"""
            Port {Port}
            ListenAddress 127.0.0.1
            HostKey {Dir.Combine("host_key")}
            PidFile {Dir.Combine("sshd.pid")}
            AuthorizedKeysFile {Dir.Combine("authorized_keys")}
            AllowUsers {user}
            StrictModes no
            PasswordAuthentication no
            KbdInteractiveAuthentication no
            PubkeyAuthentication yes
            UsePAM no
            PermitUserEnvironment no
            SetEnv HOME={RemoteHome} XDG_CONFIG_HOME={RemoteHome}/.config XDG_CACHE_HOME={RemoteHome}/.cache XDG_DATA_HOME={RemoteHome}/.local/share XDG_STATE_HOME={RemoteHome}/.local/state PATH={path}
            LogLevel VERBOSE
            """);
        File.WriteAllText(SshConfig, $"""
            Host cscode-test
              HostName 127.0.0.1
              Port {Port}
              User {user}
              IdentityFile {Dir.Combine("client_key")}
              IdentitiesOnly yes
              UserKnownHostsFile {Dir.Combine("known_hosts")}
              GlobalKnownHostsFile /dev/null
              StrictHostKeyChecking accept-new
              UpdateHostKeys no
              BatchMode yes
              ControlMaster no
              ControlPath none

            Host cscode-askpass
              HostName 127.0.0.1
              Port {Port}
              User {user}
              IdentityFile {Dir.Combine("client_key_pass")}
              IdentitiesOnly yes
              UserKnownHostsFile {Dir.Combine("known_hosts_askpass")}
              GlobalKnownHostsFile /dev/null
              StrictHostKeyChecking ask
              UpdateHostKeys no
              PreferredAuthentications publickey
              ControlMaster no
              ControlPath none
            """);

        var psi = new ProcessStartInfo(sshd) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-D", "-e", "-f", Dir.Combine("sshd_config"), "-p", Port.ToString(System.Globalization.CultureInfo.InvariantCulture) }) psi.ArgumentList.Add(a);
        psi.Environment.Remove("SSH_AUTH_SOCK");
        _sshd = Process.Start(psi)!;
        var log = new StringBuilder();
        _sshd.ErrorDataReceived += (_, e) => { if (e.Data is { } l) lock (log) { log.AppendLine(l); try { File.AppendAllText(SshdLog, l + "\n"); } catch (IOException) { } } };
        _sshd.BeginErrorReadLine();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (_sshd.HasExited) { lock (log) SkipReason = "sshd exited at start: " + log; return; }
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, Port);
                return;
            }
            catch (SocketException) { await Task.Delay(100); }
        }
        SkipReason = "sshd did not start listening";
    }

    public async ValueTask DisposeAsync()
    {
        if (_sshd is { HasExited: false } p)
        {
            p.Kill(); // our own sshd, by its PID
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        _sshd?.Dispose();
        Dir.Dispose();
    }
}

public class RealSshTests(SshdFixture sshd) : IClassFixture<SshdFixture>
{
    private static CancellationToken Ct => TestEnv.Ct;

    private string Settings(bool askpass = false) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["remote.ssh.configFile"] = sshd.SshConfig,
        ["remote.ssh.serverInstallPath"] = sshd.Dir.Combine(askpass ? "remote-install-askpass" : "remote-install"),
        ["remote.ssh.serverDataPath"] = sshd.Dir.Combine(askpass ? "remote-data-askpass" : "remote-data"),
        ["remote.ssh.useAskpass"] = askpass,
        ["remote.ssh.connectTimeout"] = 10,
    });

    private void RequireSshd()
    {
        Assert.SkipWhen(sshd.SkipReason is not null, sshd.SkipReason ?? "");
        _ = InstallerTests.RequireBundles();
    }

    [Fact]
    public async Task Bootstrap_over_real_ssh_then_files_search_watch_and_language_features()
    {
        RequireSshd();
        await using var host = new RemoteTestHost(o => o.Bundles = InstallerTests.PublishedBundles(), loopback: false, userSettings: Settings());
        LanguageBridgeTests.WriteProject(host.Workspace);
        await host.ConnectAsync("cscode-test");
        var output = host.Output.Text(Client.RemoteConnectionManager.OutputChannel);
        Assert.Contains("Installing the server", output); // uploaded over the ssh channel (fresh install root)
        Assert.Contains($"Resolved cscode-test: {Environment.UserName}@127.0.0.1:{sshd.Port}", output); // ssh -G with the temp config
        Assert.Equal(RemoteConnectionState.Connected, host.Manager.Status.State);
        Assert.Equal(sshd.RemoteHome, host.Manager.Session!.Server.Home); // the session ran with the fixture's environment
        Assert.True(File.Exists(Path.Combine(sshd.Dir.Combine("remote-install"), "bin", InstallerTests.PublishedBundles()!.SelfContained("linux-x64")!.Hash, "cscode-server")));

        // file operations through the router
        var fs = host.FileSystem;
        await fs.WriteFileAsync(host.Remote("hello.txt"), Encoding.UTF8.GetBytes("over ssh"), ct: Ct);
        Assert.Equal("over ssh", RemoteTestHost.Text(await fs.ReadFileAsync(host.Remote("hello.txt"), Ct)));
        Assert.Contains("src", (await fs.ReadDirectoryAsync(host.Remote(), Ct)).Select(e => e.Name));

        // remote workspace + quick open on the server
        await host.OpenWorkspaceAsync();
        var files = new List<Uri>();
        await foreach (var u in host.WorkspaceService.FindFilesAsync("**/*.ts", 0, Ct)) files.Add(u);
        Assert.Equal(2, files.Count);

        // watcher
        var changes = new List<FileChange>();
        using (var watch = host.Services.GetRequiredService<IFileWatcher>().Watch(host.Remote(), true, null, c => { lock (changes) changes.AddRange(c); }))
        {
            await ((Client.RemoteWatcher.Handle)watch).Ready.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            File.WriteAllText(Path.Combine(host.Workspace.Path, "touched.txt"), "x");
            await TestEnv.WaitUntilAsync(() => { lock (changes) return changes.Any(c => c.Uri == host.Remote("touched.txt")); }, TimeSpan.FromSeconds(20), "watch event over ssh");
        }

        // LSP smoke: real typescript-language-server on the "remote", diagnostics through the mirror
        if (LanguageBridgeTests.FindOnPath("typescript-language-server") is not null && LanguageBridgeTests.FindOnPath("node") is not null)
        {
            var documents = host.Services.GetRequiredService<IDocumentManager>();
            var main = new InMemoryTextDocument(host.Remote("src/main.ts"), "typescript", LanguageBridgeTests.MainTs);
            using var reg = documents.Register(main);
            var diagnostics = host.Services.GetRequiredService<IDiagnosticService>();
            await TestEnv.WaitUntilAsync(() => diagnostics.Get(main.Uri).Any(d => d.Code == "2322"), TimeSpan.FromSeconds(120), "TS2322 over ssh\n" + host.Dump());
            Assert.NotEmpty(host.Services.GetRequiredService<ILanguageFeatureRegistry>().GetProviders<IHoverProvider>(main));
        }

        // a clean disconnect leaves no ssh and no server process (marker check in DisposeAsync)
        await host.Manager.DisconnectAsync(Ct);
        await TestEnv.AssertNoProcessesAsync(host.Marker);
    }

    [Fact]
    public async Task Askpass_routes_the_host_key_question_and_the_key_passphrase_to_the_gui()
    {
        RequireSshd();
        Assert.SkipUnless(OperatingSystem.IsLinux(), "askpass forwarding is Unix-only");
        File.Delete(sshd.Dir.Combine("known_hosts_askpass")); // the host is unknown: ssh asks (StrictHostKeyChecking=ask)
        var window = new PromptRecordingWindow();
        window.Answers.Enqueue(SshdFixture.Passphrase); // start (not installed)
        window.Answers.Enqueue(SshdFixture.Passphrase); // install
        window.Answers.Enqueue(SshdFixture.Passphrase); // start again
        await using var host = new RemoteTestHost(o =>
        {
            o.Bundles = InstallerTests.PublishedBundles();
            o.AskpassCommand = [LocalServer.FindDotnetHost(), TestEnv.ServerAssembly]; // the server binary's own --askpass mode
        }, loopback: false, userSettings: Settings(askpass: true), window: window);
        await host.ConnectAsync("cscode-askpass");
        Assert.Equal(RemoteConnectionState.Connected, host.Manager.Status.State);
        Assert.Contains(window.Confirmations, p => p.Contains("continue connecting", StringComparison.OrdinalIgnoreCase)); // StrictHostKeyChecking=ask
        Assert.Contains(window.Prompts, p => p.Prompt.Contains("passphrase", StringComparison.OrdinalIgnoreCase) && p.Password);
        Assert.Empty(Directory.GetDirectories(host.ClientHome, "cscode-askpass-*")); // the socket directory died with the attempt
        await host.Manager.DisconnectAsync(Ct);
    }

    [Fact]
    public async Task A_wrong_passphrase_fails_with_sshs_reason()
    {
        RequireSshd();
        var window = new PromptRecordingWindow();
        for (var i = 0; i < 6; i++) window.Answers.Enqueue("wrong");
        await using var host = new RemoteTestHost(o =>
        {
            o.Bundles = InstallerTests.PublishedBundles();
            o.AskpassCommand = [LocalServer.FindDotnetHost(), TestEnv.ServerAssembly];
        }, loopback: false, userSettings: Settings(askpass: true), window: window);
        var ex = await Assert.ThrowsAnyAsync<IOException>(() => host.ConnectAsync("cscode-askpass"));
        Assert.Contains("Permission denied", ex.Message + host.Dump());
        Assert.Equal(RemoteConnectionState.Failed, host.Manager.Status.State);
    }

    [Fact]
    public async Task A_broken_ssh_configuration_fails_with_sshs_message()
    {
        Assert.SkipUnless(File.Exists("/usr/bin/ssh"), "no ssh client");
        using var dir = new TempDirectory("cscode-badcfg-");
        var cfg = dir.WriteFile("ssh_config", "Host x\n  NoSuchOption yes\n");
        await using var host = new RemoteTestHost(loopback: false, userSettings: JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["remote.ssh.configFile"] = cfg, ["remote.ssh.useAskpass"] = false,
        }));
        var ex = await Assert.ThrowsAnyAsync<IOException>(() => host.ConnectAsync("x"));
        Assert.Contains("ssh rejected the configuration", ex.Message);
        Assert.Contains("NoSuchOption", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Answers confirmations "Yes" and input boxes from a queue, recording what was asked.</summary>
    private sealed class PromptRecordingWindow : IWindowService
    {
        public readonly Queue<string> Answers = new();
        public readonly List<(string Prompt, bool Password)> Prompts = new();
        public readonly List<string> Confirmations = new();

        public ValueTask<string?> ShowMessageAsync(MessageSeverity severity, string message, IReadOnlyList<MessageAction>? actions = null, CancellationToken ct = default)
        {
            lock (Confirmations) Confirmations.Add(message);
            return ValueTask.FromResult<string?>(actions?.FirstOrDefault()?.Title);
        }

        public ValueTask<string?> ShowInputBoxAsync(InputBoxOptions? options = null, CancellationToken ct = default)
        {
            lock (Prompts)
            {
                Prompts.Add((options?.Prompt ?? "", options?.Password ?? false));
                return ValueTask.FromResult<string?>(Answers.Count > 0 ? Answers.Dequeue() : null);
            }
        }

        public ValueTask<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) => ValueTask.FromResult<QuickPickItem?>(null);
        public ValueTask<IReadOnlyList<QuickPickItem>?> ShowQuickPickManyAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<QuickPickItem>?>(null);
        public ValueTask<IReadOnlyList<Uri>?> ShowOpenDialogAsync(OpenDialogOptions options, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Uri>?>(null);
        public ValueTask<Uri?> ShowSaveDialogAsync(SaveDialogOptions options, CancellationToken ct = default) => ValueTask.FromResult<Uri?>(null);
        public async Task<T> WithProgressAsync<T>(string title, bool cancellable, Func<IProgressReporter, CancellationToken, Task<T>> work) => await work(new NoProgress(), CancellationToken.None);
        public IDisposable SetStatusBarMessage(string text, TimeSpan? timeout = null) => Core.Lifecycle.Disposable.Empty;
        public ValueTask<bool> OpenExternalAsync(Uri uri) => ValueTask.FromResult(false);
        public ValueTask CopyToClipboardAsync(string text) => ValueTask.CompletedTask;
        private sealed class NoProgress : IProgressReporter { public void Report(string? message = null, double? percent = null) { } }
    }
}
