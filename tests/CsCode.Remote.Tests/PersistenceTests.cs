using System.Diagnostics;
using CsCode.Core.Remote;
using CsCode.Remote.Client;
using CsCode.Remote.Transport;
using CsCode.Server;
using CsCode.Terminal.Sessions;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>A loopback transport whose server process gets extra environment variables, read again for every connection (a new ssh session).</summary>
public sealed class EnvTransport(IRemoteTransport inner, Func<IReadOnlyDictionary<string, string?>> variables) : IRemoteTransport
{
    public string Description => inner.Description;

    public Task<RemoteChannel> ConnectAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (inner is not LoopbackTransport loopback) return inner.ConnectAsync(progress, ct);
        var env = new Dictionary<string, string?>(loopback.Launch.Environment ?? new Dictionary<string, string?>());
        foreach (var (k, v) in variables()) env[k] = v;
        return new LoopbackTransport(loopback.Launch with { Environment = env }).ConnectAsync(progress, ct);
    }
}

/// <summary>
/// The persistent server's socket directory, build identity and environment: a long data dir without XDG_RUNTIME_DIR (remote-8), a daemon that
/// cannot start (reported at once), an upgraded client (remote-4: never the old build's daemon, which is asked to end), and the ssh agent of the
/// newest connection (remote-7).
/// </summary>
public class PersistenceTests
{
    private static CancellationToken Ct => TestEnv.Ct;
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);
    private const string Session = "0123456789abcdef0123456789abcdef";

    /// <summary>This process's user id as the socket directory's name has it (the longest one where it cannot be read).</summary>
    private static string Uid =>
        File.Exists("/proc/self/status") && File.ReadLines("/proc/self/status").FirstOrDefault(l => l.StartsWith("Uid:", StringComparison.Ordinal)) is { } line
            ? line.Split('\t', StringSplitOptions.RemoveEmptyEntries)[2]
            : "4294967295";

    [Fact]
    public void A_socket_path_too_long_for_the_data_dir_goes_to_the_runtime_dir_else_to_a_private_temp_dir()
    {
        using var temp = new TempDirectory("cp-");
        Assert.SkipWhen(System.Text.Encoding.UTF8.GetByteCount($"{temp.Path}/cscode-{Uid}-0123456789ab/{new string('0', 32)}-0123456789ab.agent") > 100,
            $"the temp directory's path is too long for a socket directory under it ({temp.Path})");
        var longData = Path.Combine(temp.Path, new string('d', 80), "data");
        var runtime = Path.Combine(temp.Path, "xdg");
        Directory.CreateDirectory(runtime);

        var shortData = PersistencePaths.For(Path.Combine(temp.Path, "data"), Session, "0b0b0b0b", _ => null);
        Assert.Equal(Path.Combine(temp.Path, "data", "run"), shortData.Directory);
        Assert.Equal(Session + "-0b0b0b0b", Path.GetFileNameWithoutExtension(shortData.Socket));

        var xdg = PersistencePaths.For(longData, Session, "0b0b0b0b", name => name == "XDG_RUNTIME_DIR" ? runtime : null);
        Assert.Equal(Path.Combine(runtime, "cscode-server"), xdg.Directory);

        // no XDG_RUNTIME_DIR (docker exec, su, macOS, hosts without systemd): a private directory in the temp directory, never a too-long path
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess, "root's sockets go to /run, never to the shared temp directory");
        var fallback = PersistencePaths.For(longData, Session, "0b0b0b0b", name => name == "TMPDIR" ? temp.Path : null);
        Assert.StartsWith(Path.Combine(temp.Path, "cscode-"), fallback.Directory, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(fallback.Agent) <= 100, fallback.Agent);
        fallback.EnsureDirectory();
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(fallback.Directory));
        var otherData = Path.Combine(temp.Path, new string('e', 80), "data");
        var tooLongTemp = Path.Combine(temp.Path, new string('t', 90));
        var inTmp = PersistencePaths.For(otherData, Session, "0b0b0b0b", name => name == "TMPDIR" ? tooLongTemp : null).Directory;
        try { Assert.StartsWith("/tmp/cscode-", inTmp, StringComparison.Ordinal); }
        finally { Directory.Delete(inTmp); }

        // somebody planted a link where the private directory goes: refused
        var planted = Path.Combine(temp.Path, "elsewhere");
        Directory.CreateDirectory(planted);
        Directory.Delete(fallback.Directory, recursive: true);
        Directory.CreateSymbolicLink(fallback.Directory, planted);
        Assert.Throws<IOException>(fallback.EnsureDirectory);
    }

    [Fact]
    public void The_shared_temp_socket_directory_has_a_name_nobody_could_prepare_and_one_that_is_not_ours_is_never_used()
    {
        // remote-8: a fixed name in /tmp (cscode-<uid>) could be made first by another user and keep this user's persistent server from ever
        // starting; and the old check (no link, then chmod, which follows links) could be passed by swapping a directory for a link in between.
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess, "root's sockets go to /run");
        using var temp = new TempDirectory("cp-");
        var tmp = temp.Path;
        Assert.SkipWhen(System.Text.Encoding.UTF8.GetByteCount($"{tmp}/cscode-{Uid}-0123456789ab/{Session}-0b0b0b0b.agent") > 100,
            $"the temp directory's path is too long for a socket directory under it ({tmp})");
        var data = Path.Combine(temp.Path, new string('d', 80), "data");
        string? Env(string name) => name == "TMPDIR" ? tmp : null;
        var record = PersistencePaths.SharedDirectoryRecord(data);

        var first = PersistencePaths.For(data, Session, "0b0b0b0b", Env);
        Assert.Matches("^cscode-[0-9]+-[0-9a-f]{12}$", Path.GetFileName(first.Directory));
        Assert.Equal(first.Directory, File.ReadAllText(record).Trim());
        // every relay and daemon of the data dir finds the same directory; another data dir gets its own
        Assert.Equal(first.Directory, PersistencePaths.For(data, "fedcba9876543210fedcba9876543210", "0b0b0b0b", Env).Directory);
        Assert.NotEqual(first.Directory, PersistencePaths.For(Path.Combine(temp.Path, new string('e', 80), "data"), Session, "0b0b0b0b", Env).Directory);

        // gone (a reboot emptied the temp directory): made again under the recorded name
        Directory.Delete(first.Directory);
        Assert.Equal(first.Directory, PersistencePaths.For(data, Session, "0b0b0b0b", Env).Directory);
        Assert.True(PersistencePaths.IsOwnDirectory(first.Directory));

        // the recorded name was taken meanwhile by a link (here to a directory of ours: the swap that passed the old check): never used, replaced
        Directory.Delete(first.Directory);
        var mine = Path.Combine(temp.Path, "mine");
        Directory.CreateDirectory(mine);
        Directory.CreateSymbolicLink(first.Directory, mine);
        Assert.False(PersistencePaths.IsOwnDirectory(first.Directory));
        var replaced = PersistencePaths.For(data, Session, "0b0b0b0b", Env);
        Assert.NotEqual(first.Directory, replaced.Directory);
        Assert.StartsWith(tmp + "/cscode-", replaced.Directory, StringComparison.Ordinal);
        Assert.Equal(replaced.Directory, File.ReadAllText(record).Trim());

        // a recorded directory that is another user's (root's /): never used (nor chmod-ed), replaced
        File.WriteAllText(record, "/");
        Assert.False(PersistencePaths.IsOwnDirectory("/"));
        var again = PersistencePaths.For(data, Session, "0b0b0b0b", Env);
        Assert.NotEqual("/", again.Directory);
        Assert.True(PersistencePaths.IsOwnDirectory(again.Directory));
        again.EnsureDirectory();
    }

    [Fact]
    public void Daemons_of_other_builds_of_the_session_are_found_and_nothing_else()
    {
        using var temp = new TempDirectory("cp-");
        var data = Path.Combine(temp.Path, "data");
        var mine = PersistencePaths.For(data, Session, "c0ffee01", _ => null);
        mine.EnsureDirectory();
        string[] names = [Session + ".sock", Session + "-0a0a0a0a.sock", Session + "-c0ffee01.sock", Session + "-0a0a0a0a.lock", Session + "-0a0a0a0a.agent",
            "fedcba9876543210fedcba9876543210-0a0a0a0a.sock"];
        foreach (var n in names) File.WriteAllText(Path.Combine(mine.Directory, n), "");
        var others = mine.OtherBuilds(data, Session, _ => null).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal([Session + "-0a0a0a0a.sock", Session + ".sock"], others);
    }

    [Fact]
    public async Task A_persistent_server_with_a_long_data_dir_and_no_XDG_RUNTIME_DIR_starts_and_is_reached()
    {
        using var temp = new TempDirectory("cp-");
        Assert.SkipWhen(System.Text.Encoding.UTF8.GetByteCount($"{temp.Path}/cscode-{Uid}-0123456789ab/{new string('0', 32)}-0123456789ab.agent") > 100,
            $"the temp directory's path is too long for a socket directory under it ({temp.Path})");
        var data = Path.Combine(temp.Path, new string('d', 90), "data");
        Directory.CreateDirectory(data);
        var tmp = temp.Path; // the server's TMPDIR: its private cscode-<uid> socket directory goes there, not into this machine's /tmp
        var noRuntimeDir = new Dictionary<string, string?> { ["XDG_RUNTIME_DIR"] = null, ["TMPDIR"] = tmp };
        await using var host = new RemoteTestHost(wrap: t => new EnvTransport(t, () => noRuntimeDir), persistent: Grace, serverDataDir: data);
        var sw = Stopwatch.StartNew();
        await host.ConnectAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"{sw.Elapsed.TotalSeconds:0.0} s");
        Assert.Contains("--daemon", R3Tests.Cmdline(host.Manager.Session!.Server.ProcessId));
        var socket = Assert.Single(Directory.GetFiles(Directory.GetDirectories(tmp, "cscode-*").Single(), "*.sock"));
        Assert.StartsWith(host.Manager.ServerSessionId!, Path.GetFileName(socket), StringComparison.Ordinal);
        await host.Manager.DisconnectAsync(Ct);
        await TestEnv.AssertNoProcessesAsync(host.Marker, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_daemon_that_cannot_start_is_reported_by_the_relay_at_once_with_its_reason()
    {
        using var temp = new TempDirectory("cp-");
        var data = Path.Combine(temp.Path, "data");
        var marker = "cscode-test-" + Guid.NewGuid().ToString("N")[..12];
        var paths = PersistencePaths.For(data, Session, "feed1234", _ => null);
        paths.EnsureDirectory();
        Directory.CreateDirectory(paths.Socket); // something the daemon cannot replace with its socket

        var launch = LocalServer.Launch(TestEnv.ServerAssembly, data, marker, belowNormal: false, persist: (Session, 30),
            environment: new Dictionary<string, string?> { [ServerBuild.OverrideVariable] = "feed1234", ["XDG_RUNTIME_DIR"] = null, ["SSH_AUTH_SOCK"] = null });
        var psi = new ProcessStartInfo(launch.FileName) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in launch.Arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in launch.Environment!) { if (v is null) psi.Environment.Remove(k); else psi.Environment[k] = v; }
        var sw = Stopwatch.StartNew();
        using var relay = Process.Start(psi)!;
        var stderr = relay.StandardError.ReadToEndAsync(Ct);
        var exited = relay.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        try { await exited; }
        finally { if (!relay.HasExited) relay.Kill(entireProcessTree: true); }
        Assert.Equal(1, relay.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"the relay waited {sw.Elapsed.TotalSeconds:0.0} s instead of reporting the daemon's failure");
        var reason = await stderr;
        Assert.Contains("the persistent server could not start: ", reason, StringComparison.Ordinal); // the daemon's own reason, reported by the relay
        Assert.Contains(paths.Socket, reason, StringComparison.Ordinal);
        await TestEnv.AssertNoProcessesAsync(marker);
    }

    [Fact]
    public async Task An_upgraded_client_starts_its_own_server_build_and_the_old_builds_daemon_ends()
    {
        using var clientHome = new TempDirectory("cscode-remote-upgrade-");
        var session = ServerSessionRegistry.NewId();
        var crashed = new CuttableTransport.Control();
        var oldBuild = new Dictionary<string, string?> { [ServerBuild.OverrideVariable] = "0a0a0a0a" };
        // The client of the previous build connects, then "crashes" (killed, or quit while reconnecting): its daemon waits for it.
        var a = new RemoteTestHost(o => { o.ServerSession = session; o.Reconnect = new ReconnectPolicy(1, () => 0.5); },
            wrap: t => new CuttableTransport(new EnvTransport(t, () => oldBuild), crashed), persistent: Grace, clientHome: clientHome);
        await a.ConnectAsync();
        var old = a.Manager.Session!.Server;
        crashed.Refuse = true;
        await crashed.CutAsync();
        await TestEnv.WaitUntilAsync(() => a.Manager.Status.State == RemoteConnectionState.Failed, TimeSpan.FromSeconds(30), "the old client to give up");
        Assert.True(TestEnv.IsAlive(old.ProcessId));

        // The upgraded client restores the session (same id): it must not reach the old daemon, which would have to speak its protocol.
        await using (var b = new RemoteTestHost(o => o.ServerSession = session, persistent: Grace, clientHome: clientHome, serverDataDir: a.ServerDataDir, marker: a.Marker))
        {
            await b.ConnectAsync();
            var now = b.Manager.Session!.Server;
            Assert.NotEqual(old.InstanceId, now.InstanceId);
            Assert.NotEqual(old.ProcessId, now.ProcessId);
            await TestEnv.WaitUntilAsync(() => !TestEnv.IsAlive(old.ProcessId), TimeSpan.FromSeconds(20), "the old build's daemon to end");
            var file = Path.Combine(b.Workspace.Path, "upgraded.txt");
            await b.FileSystem.WriteFileAsync(b.Remote("upgraded.txt"), "new server"u8.ToArray(), true, Ct);
            Assert.Equal("new server", File.ReadAllText(file));
        }
        await TestEnv.AssertNoProcessesAsync(a.Marker);
        await a.DisposeAsync();
    }

    [Fact]
    public async Task Terminals_reach_the_ssh_agent_of_the_newest_connection_after_a_reconnect()
    {
        using var agents = new TempDirectory("cscode-agent-");
        var first = Path.Combine(agents.Path, "agent.1");
        var second = Path.Combine(agents.Path, "agent.2");
        File.WriteAllText(first, "");
        File.WriteAllText(second, "");
        var current = first;
        var control = new CuttableTransport.Control();
        await using var host = new RemoteTestHost(o => o.Reconnect = new ReconnectPolicy(10, () => 0.5),
            wrap: t => new CuttableTransport(new EnvTransport(t, () => new Dictionary<string, string?> { ["SSH_AUTH_SOCK"] = Volatile.Read(ref current) }), control),
            r3: true, persistent: Grace);
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();
        var terminals = host.Services.GetRequiredService<TerminalService>();
        var t = (TerminalSession)await terminals.CreateAsync(TerminalProbe.Bash() with { InitialSize = (200, 30) }, Ct);
        await t.SendTextAsync("echo \"sock=$SSH_AUTH_SOCK\"; echo \"agent=$(readlink \"$SSH_AUTH_SOCK\")\"", true, Ct);
        var screen = await TerminalProbe.WaitForTextAsync(t, "\nagent=" + first);
        Assert.Contains(".agent\n", screen.Replace("\r", ""), StringComparison.Ordinal); // the daemon's stable link, not the connection's own socket

        // The network drops; the new ssh session forwards its agent at another path (the first one's socket is gone).
        Volatile.Write(ref current, second);
        var connects = control.Connects;
        await control.CutAsync();
        await TestEnv.WaitUntilAsync(() => control.Connects > connects && host.Manager.Session is { IsOpen: true } && host.Manager.Status.IsConnected,
            TimeSpan.FromSeconds(60), "the reconnect");
        await t.SendTextAsync("echo \"again=$(readlink \"$SSH_AUTH_SOCK\")\"", true, Ct);
        await TerminalProbe.WaitForTextAsync(t, "\nagain=" + second);
        var fresh = (TerminalSession)await terminals.CreateAsync(TerminalProbe.Bash() with { InitialSize = (200, 30) }, Ct);
        await fresh.SendTextAsync("echo \"new=$(readlink \"$SSH_AUTH_SOCK\")\"", true, Ct);
        await TerminalProbe.WaitForTextAsync(fresh, "\nnew=" + second);
        await terminals.KillAsync(t, Ct);
        await terminals.KillAsync(fresh, Ct);
    }
}
