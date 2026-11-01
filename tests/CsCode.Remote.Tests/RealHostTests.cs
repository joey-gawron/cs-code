using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Core.Tasks;
using CsCode.Core.Text;
using CsCode.Remote.Client;
using CsCode.Remote.Ssh;
using CsCode.Terminal.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>
/// An opt-in real remote host reached with the system ssh (a VM, a server): never run unless <c>CSCODE_TEST_SSH_HOST</c> names it, so CI never needs
/// one. Everything the suite creates on the host — the server install, its data dir (machine settings, logs, the persistent server's socket) and the
/// workspaces — lives in ONE temp directory there (<c>CSCODE_TEST_SSH_DIR</c>, default <c>/tmp/cscode-realhost-&lt;random&gt;</c>), whose server
/// processes are ended and which is removed at the end. docs/REMOTE.md "Testing against a real host" has the VM recipe.
/// <list type="bullet">
/// <item><c>CSCODE_TEST_SSH_HOST</c>: the ssh destination (<c>[user@]host[:port]</c> or a Host of the config file).</item>
/// <item><c>CSCODE_TEST_SSH_CONFIG</c>: an ssh config file for it (<c>remote.ssh.configFile</c>, <c>ssh -F</c>); optional.</item>
/// <item><c>CSCODE_TEST_SSH_DIR</c>: the host's temp directory (absolute, its name containing <c>cscode-realhost</c>); optional.</item>
/// <item><c>CSCODE_TEST_SSH_RUST_ANALYZER</c>: a rust-analyzer on the host, for the language-server test (set as the host's machine setting); optional.</item>
/// <item>the server bundles (<c>tools/publish-server.sh</c>, or <c>CSCODE_SERVER_BUNDLES</c>) with one for the host's platform.</item>
/// </list>
/// </summary>
public sealed class RealHostFixture : IAsyncLifetime
{
    public string? SkipReason { get; private set; }
    public string Host { get; } = Environment.GetEnvironmentVariable("CSCODE_TEST_SSH_HOST") ?? "";
    public string? ConfigFile { get; } = Environment.GetEnvironmentVariable("CSCODE_TEST_SSH_CONFIG") is { Length: > 0 } c ? c : null;
    public string? RustAnalyzer { get; } = Environment.GetEnvironmentVariable("CSCODE_TEST_SSH_RUST_ANALYZER") is { Length: > 0 } r ? r : null;
    public string BaseDir { get; } = Environment.GetEnvironmentVariable("CSCODE_TEST_SSH_DIR") is { Length: > 0 } d
        ? d.TrimEnd('/')
        : "/tmp/cscode-realhost-" + Guid.NewGuid().ToString("N")[..8]; // short: the persistent server's socket path must fit a sockaddr_un
    public string InstallRoot => BaseDir + "/install";
    public string DataDir => BaseDir + "/data";
    private bool _reached;
    public string Os { get; private set; } = "";
    public string Machine { get; private set; } = "";

    /// <summary>The bundle the host needs (uname -sm → rid).</summary>
    public string? Rid => (Os, Machine) switch
    {
        ("Linux", "x86_64" or "amd64") => "linux-x64",
        ("Linux", "aarch64" or "arm64") => "linux-arm64",
        ("Darwin", "x86_64") => "osx-x64",
        ("Darwin", "arm64") => "osx-arm64",
        _ => null,
    };

    public async ValueTask InitializeAsync()
    {
        if (Host.Length == 0) { SkipReason = "no real host: set CSCODE_TEST_SSH_HOST (and CSCODE_TEST_SSH_CONFIG) to run this suite (docs/REMOTE.md)"; return; }
        if (OperatingSystem.IsWindows()) { SkipReason = "the real-host suite drives the system ssh of a Unix client"; return; }
        if (!Regex.IsMatch(BaseDir, "^/[A-Za-z0-9._/-]+$") || !BaseDir.Contains("cscode-realhost", StringComparison.Ordinal) || BaseDir.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException($"CSCODE_TEST_SSH_DIR must be an absolute plain path whose name contains 'cscode-realhost' (it is removed afterwards): {BaseDir}");
        _reached = true; // from here on the directory may exist: DisposeAsync removes it whatever happens next
        var (exit, output) = await RunAsync("uname -s; uname -m; mkdir -p \"$1\" && echo made", BaseDir);
        if (exit != 0) { SkipReason = $"ssh {Host} failed ({exit}): {output}"; return; }
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 3 || lines[^1] != "made") { SkipReason = $"cannot make {BaseDir} on {Host}: {output}"; return; }
        (Os, Machine) = (lines[^3], lines[^2]);
        var bundles = InstallerTests.PublishedBundles();
        if (bundles is null) SkipReason = "no server bundles: run tools/publish-server.sh (writes .scratch/server-bundles)";
        else if (Rid is null || bundles.SelfContained(Rid) is null) SkipReason = $"no server bundle for {Os} {Machine} ({Rid ?? "unsupported"}): tools/publish-server.sh --rid {Rid}";
    }

    /// <summary>Runs a fixed script on the host (<c>sh -c script sh args…</c>, every argument quoted): exit code and stdout (stderr when it failed).</summary>
    public async Task<(int Exit, string Output)> RunAsync(string script, params string[] args)
    {
        var psi = new ProcessStartInfo("ssh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        if (ConfigFile is not null) { psi.ArgumentList.Add("-F"); psi.ArgumentList.Add(ConfigFile); }
        // a connection of its own: a helper must never ride (or hang on) a shared ControlMaster connection the test is freezing
        foreach (var a in new[] { "-o", "ControlPath=none", "-o", "BatchMode=yes", "-T", "--", Host }) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(string.Join(' ', new[] { "sh", "-c", PosixShell.Quote(script), "sh" }.Concat(args.Select(PosixShell.Quote))));
        psi.Environment["SSH_AUTH_SOCK"] = "";
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return (p.ExitCode, p.ExitCode == 0 ? await stdout : (await stdout) + (await stderr)); // ssh's own notices ("Warning: Permanently added …") go to stderr
    }

    /// <summary>The client settings every test connects with: the host's install root and data dir inside the temp directory.</summary>
    public string ClientSettings(int reconnectGraceSeconds = 60, string[]? sshOptions = null) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["remote.ssh.options"] = sshOptions ?? [],
        ["remote.ssh.configFile"] = ConfigFile ?? "",
        ["remote.ssh.serverInstallPath"] = InstallRoot,
        ["remote.ssh.serverDataPath"] = DataDir,
        ["remote.ssh.useAskpass"] = false,
        ["remote.ssh.connectTimeout"] = 15,
        ["remote.ssh.reconnectGracePeriod"] = reconnectGraceSeconds,
    });

    public async ValueTask DisposeAsync()
    {
        if (!_reached) return; // never asked the host for anything
        // End our server processes (relays, daemons, language servers they started from the install) and remove the directory. The pattern's first
        // character is bracketed so it does not match this very command line.
        var pattern = "[" + BaseDir[0] + "]" + BaseDir[1..] + "/";
        await RunAsync("pkill -f -- \"$1\"; sleep 1; pkill -9 -f -- \"$1\"; rm -rf -- \"$2\"; test ! -e \"$2\"", pattern, BaseDir);
    }
}

public sealed class RealHostTests(RealHostFixture fx) : IClassFixture<RealHostFixture>
{
    private static CancellationToken Ct => TestEnv.Ct;

    private void Require() => Assert.SkipWhen(fx.SkipReason is not null, fx.SkipReason ?? "");

    private RemoteTestHost NewHost(bool persistent = true, Action<RemoteOptions>? options = null, string[]? sshOptions = null) => new(o =>
        {
            o.Bundles = InstallerTests.PublishedBundles();
            options?.Invoke(o);
        }, loopback: false, userSettings: fx.ClientSettings(sshOptions: sshOptions), r3: true, persistent: persistent ? TimeSpan.FromSeconds(60) : null);

    /// <summary>The host's sshd process (the user's own, per connection) behind our relay: freezing it silences that connection.</summary>
    private async Task<string> RelaySshdAsync()
    {
        var (found, sshd) = await fx.RunAsync("""
            relay=$(pgrep -f -- "$1" | head -n 1); p=$relay
            while [ -n "$p" ] && [ "$p" != 1 ]; do
              case "$(ps -o comm= -p "$p")" in sshd*) echo "$p"; exit 0 ;; esac
              p=$(ps -o ppid= -p "$p" | tr -d ' ')
            done
            exit 1
            """, "[" + fx.BaseDir[0] + "]" + fx.BaseDir[1..] + "/install/.*--stdio");
        Assert.True(found == 0, $"no sshd found for the relay: {sshd}");
        return sshd.Trim();
    }

    /// <summary>A fresh workspace directory on the host, inside the suite's temp directory.</summary>
    private async Task<Uri> WorkspaceAsync(RemoteTestHost host, string name)
    {
        var folder = host.Manager.ToUri($"{fx.BaseDir}/ws-{name}-{Guid.NewGuid().ToString("N")[..6]}/")!;
        await host.FileSystem.CreateDirectoryAsync(folder, Ct);
        return folder;
    }

    /// <summary><see cref="TestEnv.WaitUntilAsync"/> whose failure carries the client's Remote log and the server's logs as they are THEN.</summary>
    private static async Task WaitAsync(RemoteTestHost host, Func<bool> condition, TimeSpan timeout, string what)
    {
        try { await TestEnv.WaitUntilAsync(condition, timeout, what); }
        catch (TimeoutException ex) { throw new TimeoutException(ex.Message + "\n" + host.Dump(), ex); }
    }

    private static Uri In(Uri folder, string relative) => new(folder, relative);

    private static string RemotePath(Uri uri) => Uri.UnescapeDataString(uri.AbsolutePath);

    [Fact]
    public async Task Bootstrap_installs_the_host_s_bundle_then_files_round_trip_and_changes_made_on_the_host_are_watched()
    {
        Require();
        await using var host = NewHost();
        await host.ConnectAsync(fx.Host);
        var server = host.Manager.Session!.Server;
        Assert.Equal(fx.Os, server.Os);
        var bundle = InstallerTests.PublishedBundles()!.SelfContained(fx.Rid!)!;
        var (exit, listing) = await fx.RunAsync("ls \"$1\"/bin", fx.InstallRoot);
        Assert.True(exit == 0 && listing.Contains(bundle.Hash, StringComparison.Ordinal), $"the {fx.Rid} bundle ({bundle.Hash}) is not installed: {listing}\n{host.Dump()}");
        Assert.Equal(fx.DataDir, server.DataDir);

        var ws = await WorkspaceAsync(host, "files");
        var fs = host.FileSystem;
        await fs.WriteFileAsync(In(ws, "hello world.txt"), Encoding.UTF8.GetBytes("over real ssh\n"), ct: Ct);
        Assert.Equal("over real ssh\n", RemoteTestHost.Text(await fs.ReadFileAsync(In(ws, "hello%20world.txt"), Ct)));
        var big = new byte[3 * 1024 * 1024 + 17];
        new Random(42).NextBytes(big);
        await fs.WriteFileAsync(In(ws, "big.bin"), big, ct: Ct);           // chunked upload, committed with one atomic write
        Assert.Equal(big, await fs.ReadFileAsync(In(ws, "big.bin"), Ct));  // chunked, pipelined read
        var (_, sum) = await fx.RunAsync("wc -c < \"$1\"", RemotePath(In(ws, "big.bin")));
        Assert.Equal(big.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), sum.Trim()); // really on the host's disk
        await fs.CreateDirectoryAsync(In(ws, "sub/"), Ct);
        await fs.RenameAsync(In(ws, "big.bin"), In(ws, "sub/moved.bin"), overwrite: false, Ct);
        Assert.Equal(["hello world.txt", "sub"], (await fs.ReadDirectoryAsync(ws, Ct)).Select(e => e.Name).Order(StringComparer.Ordinal));
        await fs.DeleteAsync(In(ws, "sub/"), recursive: true, useTrash: false, Ct);
        Assert.Null(await fs.StatAsync(In(ws, "sub/"), Ct));

        // A change made on the host by another program (not through CsCode) reaches a watcher.
        var changes = new List<FileChange>();
        using (var watch = host.Services.GetRequiredService<IFileWatcher>().Watch(ws, true, null, c => { lock (changes) changes.AddRange(c); }))
        {
            await ((RemoteWatcher.Handle)watch).Ready.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal(0, (await fx.RunAsync("echo from-the-host > \"$1\"", RemotePath(In(ws, "touched.txt")))).Exit);
            await WaitAsync(host, () => { lock (changes) return changes.Any(c => c.Uri.AbsolutePath.EndsWith("/touched.txt", StringComparison.Ordinal)); },
                TimeSpan.FromSeconds(30), "a watch event for a file written on the host");
        }
        await host.Manager.DisconnectAsync(Ct);
    }

    [Fact]
    public async Task A_remote_workspace_gets_quick_open_a_terminal_and_tasks_that_run_on_the_host()
    {
        Require();
        await using var host = NewHost();
        await host.ConnectAsync(fx.Host);
        var ws = await WorkspaceAsync(host, "term");
        var fs = host.FileSystem;
        await fs.WriteFileAsync(In(ws, "src/a.c"), "int main(void) { return 0; }\n"u8.ToArray(), createParents: true, Ct);
        await fs.WriteFileAsync(In(ws, ".vscode/tasks.json"), Encoding.UTF8.GetBytes("""
            { "version": "2.0.0", "tasks": [ { "label": "build", "type": "shell", "command": "uname -m > built.txt; echo task-output-line; exit 3" } ] }
            """), createParents: true, Ct);
        await host.WorkspaceService.OpenFolderAsync(ws, Ct);

        var found = new List<Uri>();
        await foreach (var u in host.WorkspaceService.FindFilesAsync("**/*.c", 0, Ct)) found.Add(u);
        Assert.Equal(In(ws, "src/a.c"), Assert.Single(found)); // walked on the host

        // Find in Files runs on the host: only the matches cross the link
        var search = host.Services.GetServices<ITextSearchProvider>().Single(p => p.Scheme == "ssh");
        var hits = new List<TextSearchHit>();
        await foreach (var batch in search.SearchAsync(new TextSearchQuery([ws], "return 0", false, true, false, [], true, 100, 1 << 20), Ct)) hits.AddRange(batch);
        var hit = Assert.Single(hits);
        Assert.Equal((In(ws, "src/a.c"), 0, 17), (hit.Uri, hit.Line, hit.Column));

        // the user's own default shell on the host (its login profile, the server's shell integration), not a test bash
        var terminals = host.Services.GetRequiredService<TerminalService>();
        var login = (TerminalSession)await terminals.CreateAsync(new Core.Terminal.TerminalCreateOptions { Show = false, InitialSize = (100, 30) }, Ct);
        await login.SendTextAsync("echo default-$((6*7))", true, Ct);
        await TerminalProbe.WaitForTextAsync(login, "default-42", TimeSpan.FromSeconds(60));
        Assert.False(login.HasExited);
        await terminals.KillAsync(login, Ct);

        var t = (TerminalSession)await terminals.CreateAsync(TerminalProbe.Bash(), Ct);
        await t.SendTextAsync("echo marker-$((40+2)); uname -sm; pwd", true, Ct);
        var screen = await TerminalProbe.WaitForTextAsync(t, "marker-42", TimeSpan.FromSeconds(60));
        await TerminalProbe.WaitForTextAsync(t, $"{fx.Os} {fx.Machine}");
        Assert.Contains(RemotePath(ws).TrimEnd('/'), await TerminalProbe.ScreenAsync(t)); // started in the workspace folder, on the host
        Assert.DoesNotContain("No such file", screen);
        await terminals.KillAsync(t, Ct);

        var result = await host.Services.GetRequiredService<ITaskService>().RunAsync("build", null, Ct);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(fx.Machine, RemoteTestHost.Text(await fs.ReadFileAsync(In(ws, "built.txt"), Ct)).Trim()); // the task ran on the host
        await host.Manager.DisconnectAsync(Ct);
    }

    [Fact]
    public async Task A_language_server_on_the_host_set_in_its_machine_settings_publishes_diagnostics_through_the_bridge()
    {
        Require();
        Assert.SkipWhen(fx.RustAnalyzer is null, "no language server named for the host: set CSCODE_TEST_SSH_RUST_ANALYZER to a rust-analyzer there");
        await using var host = NewHost();
        await host.ConnectAsync(fx.Host);
        // The host's own program, chosen in the host's machine settings (the Remote settings tab's file): the client's PATH is not the host's.
        await host.Services.GetRequiredService<IRemoteMachineSettings>().UpdateAsync("cscode.languageServers",
            new Dictionary<string, object> { ["rust-analyzer"] = new Dictionary<string, object> { ["command"] = fx.RustAnalyzer! } }, Ct);
        var ws = await WorkspaceAsync(host, "rust");
        var fs = host.FileSystem;
        await fs.WriteFileAsync(In(ws, "Cargo.toml"), "[package]\nname = \"probe\"\nversion = \"0.1.0\"\nedition = \"2021\"\n\n[dependencies]\n"u8.ToArray(), ct: Ct);
        const string main = "fn main() {\n    let n: i32 = \"not a number\";\n    println!(\"{n}\");\n}\n";
        await fs.WriteFileAsync(In(ws, "src/main.rs"), Encoding.UTF8.GetBytes(main), createParents: true, Ct);
        await host.WorkspaceService.OpenFolderAsync(ws, Ct);

        var doc = new InMemoryTextDocument(In(ws, "src/main.rs"), "rust", main);
        using var reg = host.Services.GetRequiredService<IDocumentManager>().Register(doc);
        var diagnostics = host.Services.GetRequiredService<IDiagnosticService>();
        await WaitAsync(host, () => diagnostics.Get(doc.Uri).Any(d => d.Range.Start.Line == 1), TimeSpan.FromSeconds(240),
            "rust-analyzer's diagnostic for line 2, mirrored from the host");
        // a request answered by the host's server: the type of `n` (line 2, col 9)
        var hover = host.Services.GetRequiredService<ILanguageFeatureRegistry>().GetProviders<IHoverProvider>(doc);
        Assert.NotEmpty(hover);
        Hover? answer = null;
        await TestEnv.WaitUntilAsync(() =>
        {
            answer = hover[0].ProvideHoverAsync(doc, new TextPosition(1, 9), Ct).AsTask().GetAwaiter().GetResult();
            return answer?.Contents.Any(c => c.Value.Contains("i32", StringComparison.Ordinal)) == true;
        }, TimeSpan.FromSeconds(120), "a hover naming i32 from rust-analyzer on the host");
        await host.Manager.DisconnectAsync(Ct);
    }

    [Fact]
    public async Task Killing_the_ssh_connection_reconnects_to_the_same_server_and_a_running_terminal_continues()
    {
        Require();
        await using var host = NewHost(persistent: true);
        await host.ConnectAsync(fx.Host);
        var ws = await WorkspaceAsync(host, "cut");
        await host.FileSystem.WriteFileAsync(In(ws, "kept.txt"), "kept\n"u8.ToArray(), ct: Ct);
        await host.WorkspaceService.OpenFolderAsync(ws, Ct);
        var first = host.Manager.Session!;
        var terminals = host.Services.GetRequiredService<TerminalService>();
        var t = (TerminalSession)await terminals.CreateAsync(TerminalProbe.Bash() with { InitialSize = (80, 400) }, Ct);
        await t.SendTextAsync("for i in $(seq 1 150); do echo line-$i; sleep 0.02; done; read x; echo got-$x", true, Ct);
        await TerminalProbe.WaitForTextAsync(t, "line-20", TimeSpan.FromSeconds(60));

        using (var ssh = Process.GetProcessById(first.Channel.ProcessId)) ssh.Kill(); // the network connection dies (the local ssh process)
        Assert.True(t.Input("typed-while-down\r"u8));
        await WaitAsync(host, () => host.Manager.Session is { IsOpen: true } s && !ReferenceEquals(s, first) && host.Manager.Status.IsConnected,
            TimeSpan.FromSeconds(90), "the client to reconnect over a new ssh connection");
        var second = host.Manager.Session!;
        Assert.Equal(first.Server.InstanceId, second.Server.InstanceId); // the same server process on the host: resumed
        Assert.True(second.Resumed);

        var screen = await TerminalProbe.WaitForTextAsync(t, "\ngot-typed-while-down", TimeSpan.FromSeconds(90));
        var lines = Regex.Matches(screen, @"^line-(\d+)$", RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.True(Enumerable.Range(1, 150).SequenceEqual(lines), $"lost or repeated terminal output across the reconnect:\n{screen}");
        Assert.Equal("kept\n", RemoteTestHost.Text(await host.FileSystem.ReadFileAsync(In(ws, "kept.txt"), Ct)));
        await terminals.KillAsync(t, Ct);
        await host.Manager.DisconnectAsync(Ct);
        // a deliberate disconnect ends the persistent server on the host
        var gone = false;
        for (var deadline = DateTime.UtcNow.AddSeconds(30); !gone && DateTime.UtcNow < deadline; await Task.Delay(500, Ct))
            gone = (await fx.RunAsync("pgrep -f -- \"$1\" >/dev/null && echo alive || echo gone", "[" + fx.BaseDir[0] + "]" + fx.BaseDir[1..] + "/install/")).Output.Trim() == "gone";
        Assert.True(gone, "the persistent server did not exit after the disconnect");
    }

    [Fact]
    public async Task A_link_that_goes_silent_is_noticed_and_the_session_resumes_on_a_new_connection()
    {
        // A network that stops carrying packets (a sleeping laptop, a dropped VPN) kills no process: only ssh's keep-alive (ServerAliveInterval 15 s,
        // 4 misses) notices. Freezing the host's sshd process of our connection does exactly that to the link.
        Require();
        await using var host = NewHost(persistent: true);
        await host.ConnectAsync(fx.Host);
        var ws = await WorkspaceAsync(host, "silent");
        await host.WorkspaceService.OpenFolderAsync(ws, Ct);
        var first = host.Manager.Session!;
        var terminals = host.Services.GetRequiredService<TerminalService>();
        var t = (TerminalSession)await terminals.CreateAsync(TerminalProbe.Bash(), Ct);
        await t.SendTextAsync("echo before-$((1+1))", true, Ct);
        await TerminalProbe.WaitForTextAsync(t, "before-2", TimeSpan.FromSeconds(60));

        var pid = await RelaySshdAsync();
        try
        {
            Assert.Equal(0, (await fx.RunAsync("kill -STOP \"$1\"", pid)).Exit);
            await WaitAsync(host, () => host.Manager.Session is { IsOpen: true } s && !ReferenceEquals(s, first) && host.Manager.Status.IsConnected,
                TimeSpan.FromSeconds(150), "the client to notice the silent link and reconnect");
            Assert.Equal(first.Server.InstanceId, host.Manager.Session!.Server.InstanceId);
            await t.SendTextAsync("echo after-$((2+2))", true, Ct);
            await TerminalProbe.WaitForTextAsync(t, "after-4", TimeSpan.FromSeconds(60)); // the same shell, over the new connection
        }
        finally
        {
            await fx.RunAsync("kill -KILL \"$1\" 2>/dev/null; true", pid); // our frozen connection's sshd: never left stopped on the host
        }
        await terminals.KillAsync(t, Ct);
        await host.Manager.DisconnectAsync(Ct);
    }

    [Fact]
    public async Task A_stale_shared_ssh_connection_is_noticed_and_gone_around()
    {
        // The user's ssh multiplexes (ControlMaster) and the master was started by another ssh — `ssh box` in a terminal — without keep-alive. Its
        // link dies silently: our session over it hangs (the heartbeat notices), and so does every new session through it (the attempt times out;
        // the next ones connect on their own with -o ControlPath=none). Before, the client stayed "connected" with every call timing out.
        Require();
        var socket = "/tmp/cscode-rh-" + Guid.NewGuid().ToString("N")[..8] + ".sock"; // a socket path stays short (104 bytes on macOS)
        var master = new ProcessStartInfo("ssh") { UseShellExecute = false };
        if (fx.ConfigFile is not null) { master.ArgumentList.Add("-F"); master.ArgumentList.Add(fx.ConfigFile); }
        foreach (var a in new[] { "-o", "ControlMaster=yes", "-o", "ControlPath=" + socket, "-o", "ControlPersist=yes", "-o", "ServerAliveInterval=0", "-o", "BatchMode=yes", "-N", "-f", "--", fx.Host })
            master.ArgumentList.Add(a);
        master.Environment["SSH_AUTH_SOCK"] = "";
        using (var p = Process.Start(master)!) await p.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct); // -f: returns once the master runs
        string? pid = null;
        try
        {
            Assert.True(File.Exists(socket), "the shared ssh connection did not start");
            await using var host = NewHost(options: o =>
            {
                o.Heartbeat = new HeartbeatPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
                o.StartTimeout = TimeSpan.FromSeconds(20);
            }, sshOptions: ["ControlMaster=auto", "ControlPath=" + socket]);
            await host.ConnectAsync(fx.Host);
            var ws = await WorkspaceAsync(host, "stale");
            await host.WorkspaceService.OpenFolderAsync(ws, Ct);
            var first = host.Manager.Session!;
            var terminals = host.Services.GetRequiredService<TerminalService>();
            var t = (TerminalSession)await terminals.CreateAsync(TerminalProbe.Bash(), Ct);
            await t.SendTextAsync("echo before-$((1+1))", true, Ct);
            await TerminalProbe.WaitForTextAsync(t, "before-2", TimeSpan.FromSeconds(60));

            pid = await RelaySshdAsync(); // the master's connection: our session rides it
            Assert.Equal(0, (await fx.RunAsync("kill -STOP \"$1\"", pid)).Exit);
            await WaitAsync(host, () => host.Manager.Session is { IsOpen: true } s && !ReferenceEquals(s, first) && host.Manager.Status.IsConnected,
                TimeSpan.FromSeconds(150), "the client to notice the stale shared connection and reconnect around it");
            var log = host.Output.Text(RemoteConnectionManager.OutputChannel);
            Assert.Contains("No answer from " + fx.Host, log);       // the heartbeat
            Assert.Contains("-o ControlPath=none", log);              // the attempt that went around the master
            Assert.Equal(first.Server.InstanceId, host.Manager.Session!.Server.InstanceId);
            await t.SendTextAsync("echo after-$((2+2))", true, Ct);
            await TerminalProbe.WaitForTextAsync(t, "after-4", TimeSpan.FromSeconds(60));
            await terminals.KillAsync(t, Ct);
            await host.Manager.DisconnectAsync(Ct);
        }
        finally
        {
            if (pid is not null) await fx.RunAsync("kill -KILL \"$1\" 2>/dev/null; true", pid);
            var exit = new ProcessStartInfo("ssh") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-o", "ControlPath=" + socket, "-O", "exit", "--", fx.Host }) exit.ArgumentList.Add(a);
            using (var p = Process.Start(exit)!) await p.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);
            File.Delete(socket);
        }
    }

    [Fact]
    public async Task The_host_s_machine_settings_file_is_edited_through_the_remote_settings_service()
    {
        Require();
        await using var host = NewHost();
        await host.ConnectAsync(fx.Host);
        var settings = host.Services.GetRequiredService<IRemoteMachineSettings>();
        Assert.Equal(fx.DataDir + "/machine/settings.json", RemotePath(settings.SettingsUri!));
        var changes = 0;
        using var sub = settings.Changed.Subscribe(_ => Interlocked.Increment(ref changes));
        await settings.UpdateAsync("editor.tabSize", 3, Ct);
        var (_, text) = await fx.RunAsync("cat \"$1\"", fx.DataDir + "/machine/settings.json");
        Assert.Contains("\"editor.tabSize\": 3", text);

        var seen = Volatile.Read(ref changes);
        await fx.RunAsync("printf '{ \"editor.tabSize\": 6 }\\n' > \"$1\"", fx.DataDir + "/machine/settings.json"); // edited on the host
        await WaitAsync(host, () => Volatile.Read(ref changes) > seen, TimeSpan.FromSeconds(30), "a Changed for the edit made on the host");
        Assert.Contains("6", RemoteTestHost.Text(await host.FileSystem.ReadFileAsync(settings.SettingsUri!, Ct)));
        await host.Manager.DisconnectAsync(Ct);
    }
}
