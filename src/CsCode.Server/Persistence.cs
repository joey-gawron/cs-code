using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.Logging;

namespace CsCode.Server;

/// <summary>
/// Which server build this is: a short hash of the protocol version, the informational version and the module version ids of the CsCode assemblies
/// the server is made of (builds are deterministic: the same code gives the same id, any change gives a new one). A persistent server is keyed by
/// it, so a client that brings another server build never reaches a daemon of the old one — which might not even speak its protocol.
/// <c>CSCODE_SERVER_BUILD_ID</c> (4–32 lower-case hex digits) replaces it: tests stand in for another build with it.
/// </summary>
internal static partial class ServerBuild
{
    public const string OverrideVariable = "CSCODE_SERVER_BUILD_ID";

    private static readonly Lazy<string> Computed = new(Compute);

    [GeneratedRegex("^[0-9a-f]{4,32}$")]
    private static partial Regex IdPattern();

    public static string Id => Environment.GetEnvironmentVariable(OverrideVariable) is { } id && IdPattern().IsMatch(id) ? id : Computed.Value;

    private static string Compute()
    {
        var hash = 14695981039346656037UL; // FNV-1a: an identity, not a security property (and no OpenSSL needed for it)
        void Add(string text)
        {
            foreach (var b in Encoding.UTF8.GetBytes(text + "\n")) { hash ^= b; hash *= 1099511628211UL; }
        }
        Add(RemoteProtocol.Version.ToString(CultureInfo.InvariantCulture));
        Add(ServerHost.Version);
        var self = typeof(ServerBuild).Assembly;
        Add(self.ManifestModule.ModuleVersionId.ToString("N"));
        foreach (var name in self.GetReferencedAssemblies().Where(n => n.Name?.StartsWith("CsCode.", StringComparison.Ordinal) == true).OrderBy(n => n.Name, StringComparer.Ordinal))
        {
            try { Add(Assembly.Load(name).ManifestModule.ModuleVersionId.ToString("N")); }
            catch (Exception ex) when (ex is IOException or BadImageFormatException) { Add(name.FullName); }
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture)[..12];
    }
}

/// <summary>
/// Where the persistent server of a session listens: a Unix socket (mode 0600) named after the session and the server build, in a private directory
/// (0700) — the data dir's <c>run/</c>; when that path is too long for a socket address, <c>$XDG_RUNTIME_DIR/cscode-server</c>; else, for root,
/// <c>/run/cscode-server</c>; else a directory with a random name in the shared temp directory (<c>$TMPDIR</c>, or <c>/tmp</c> when that is too
/// long too), recorded in the data dir — plus a lock file only the live daemon holds, the link to the newest connection's ssh agent, and the
/// reason the daemon could not start (for the relay waiting for it). A directory is used only while it is provably this user's own entry
/// (<see cref="IsOwnDirectory"/>).
/// </summary>
/// <param name="Shared">The directory is in the shared temp directory (others can make entries next to it): it must pass <see cref="IsOwnDirectory"/>.</param>
internal sealed partial record PersistencePaths(string Directory, string Stem, bool Shared = false)
{
    private const int MaxSocketPathBytes = 100; // sun_path is 108 bytes on Linux, 104 on macOS (the agent link is connected to by its path too)
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public string Socket => Path.Combine(Directory, Stem + ".sock");
    public string Lock => Path.Combine(Directory, Stem + ".lock");

    /// <summary>
    /// A symbolic link to the ssh-agent socket of the newest connection (its relay repoints it): the daemon's SSH_AUTH_SOCK, so terminals and tasks
    /// keep reaching a forwarded agent after a reconnect, when the first connection's agent socket is gone.
    /// </summary>
    public string Agent => Path.Combine(Directory, Stem + ".agent");

    /// <summary>Why the daemon could not start: written by the daemon (its stdio is /dev/null), reported by the relay that waits for it.</summary>
    public string Failure => Path.Combine(Directory, Stem + ".failed");

    [GeneratedRegex("^[0-9a-f]{8,64}$")]
    private static partial Regex SessionPattern();

    [DllImport("libc")]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int lchown(string path, uint owner, uint group);

    /// <summary>The file in the data dir that names the socket directory made in the shared temp directory.</summary>
    internal static string SharedDirectoryRecord(string dataDir) => Path.Combine(dataDir, "run", "socket-dir");

    public static string ValidSession(string session) =>
        SessionPattern().IsMatch(session) ? session : throw new ArgumentException($"Invalid session id '{session}' (8-64 lower-case hex digits)");

    /// <param name="build">The server build (default: this one's).</param>
    /// <param name="environment">Variable lookup (tests); default: this process's environment.</param>
    public static PersistencePaths For(string dataDir, string session, string? build = null, Func<string, string?>? environment = null)
    {
        var stem = ValidSession(session) + "-" + (build ?? ServerBuild.Id);
        var env = environment ?? Environment.GetEnvironmentVariable;
        foreach (var dir in PrivateCandidates(dataDir, env))
            if (Fits(dir, stem)) return new PersistencePaths(dir, stem);
        if (SharedDirectory(dataDir, stem, env, create: true) is { } shared) return new PersistencePaths(shared, stem, Shared: true);
        throw new IOException($"No directory for the server's socket has a path short enough (data dir {dataDir})");
    }

    private static bool Fits(string dir, string stem) => Encoding.UTF8.GetByteCount(Path.Combine(dir, stem + ".agent")) <= MaxSocketPathBytes;

    private static bool IsRoot => !OperatingSystem.IsWindows() && geteuid() == 0;

    /// <summary>
    /// Socket directories in places only this user (or root) can create entries in, preferred first: the data dir's <c>run/</c>, the user's runtime
    /// dir, and for root <c>/run</c> (root's processes have no safe use for the shared temp directory, see <see cref="IsOwnDirectory"/>).
    /// </summary>
    private static IEnumerable<string> PrivateCandidates(string dataDir, Func<string, string?> environment)
    {
        yield return Path.Combine(dataDir, "run");
        if (environment("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Path.IsPathFullyQualified(runtime) && System.IO.Directory.Exists(runtime))
            yield return Path.Combine(runtime, "cscode-server");
        if (IsRoot && new[] { "/run", "/var/run" }.FirstOrDefault(System.IO.Directory.Exists) is { } run) yield return Path.Combine(run, "cscode-server");
    }

    /// <summary>
    /// The last resort: a directory in the shared temp directory (<c>$TMPDIR</c>, else <c>/tmp</c>) named at random — a fixed name could be made by
    /// anybody first and would then keep this user's persistent server from ever starting — and recorded in the data dir
    /// (<see cref="SharedDirectoryRecord"/>), so every relay and daemon of the data dir agrees on it. The recorded directory is used while it is
    /// provably ours; when it is gone (a reboot emptied /tmp) it is made again under the same name, and when that name is somebody else's now, a new
    /// directory replaces it (no daemon of ours can be in a directory that is not ours). Null for root (it has <c>/run</c>), on Windows, and when
    /// nothing is recorded and <paramref name="create"/> is false.
    /// </summary>
    private static string? SharedDirectory(string dataDir, string stem, Func<string, string?> environment, bool create)
    {
        if (OperatingSystem.IsWindows() || IsRoot) return null;
        var record = SharedDirectoryRecord(dataDir);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            string? recorded = null;
            try { recorded = File.ReadAllText(record).Trim(); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            if (recorded is { Length: > 0 } && Path.IsPathFullyQualified(recorded) && Fits(recorded, stem))
            {
                if (create && !System.IO.Directory.Exists(recorded))
                {
                    try { System.IO.Directory.CreateDirectory(recorded, Private); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                if (IsOwnDirectory(recorded)) return recorded;
            }
            if (!create) return null;
            // 48 random bits: nobody makes the name before we do (short: the socket path must stay within sun_path)
            var name = $"cscode-{geteuid().ToString(CultureInfo.InvariantCulture)}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}";
            if (SharedRoots(environment).FirstOrDefault(r => Fits(Path.Combine(r, name), stem)) is not { } root) return null;
            var made = Path.Combine(root, name);
            System.IO.Directory.CreateDirectory(made, Private);
            if (!IsOwnDirectory(made)) continue; // a name nobody could guess, taken all the same: another one
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(record)!, Private);
            var temp = record + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            File.WriteAllText(temp, made + "\n");
            try
            {
                // First one wins when there was none (two relays of the data dir starting at once): the other uses the winner's directory.
                File.Move(temp, record, overwrite: recorded is not null);
                return made;
            }
            catch (IOException)
            {
                File.Delete(temp);
                System.IO.Directory.Delete(made);
            }
        }
        return null;
    }

    /// <summary>
    /// The temp directories, preferred first: only those where other users cannot remove or rename our entries (sticky, or not writable by
    /// everybody; a group-writable one is taken as the user's own private group's, as umask 002 makes them).
    /// </summary>
    private static IEnumerable<string> SharedRoots(Func<string, string?> environment)
    {
        if (OperatingSystem.IsWindows()) yield break;
        string?[] roots = [environment("TMPDIR") is { Length: > 0 } temp && Path.IsPathFullyQualified(temp) ? temp : null, "/tmp"];
        foreach (var root in roots)
        {
            if (root is null || !System.IO.Directory.Exists(root)) continue;
            var mode = File.GetUnixFileMode(root);
            if ((mode & UnixFileMode.OtherWrite) == 0 || (mode & UnixFileMode.StickyBit) != 0) yield return root;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a directory that is this user's own entry — not somebody else's, not a symbolic link to anything. The proof
    /// is <c>lchown(path, euid, -1)</c>: a no-op the owner may do and anybody else is refused, on the entry itself (never a link's target), so it
    /// cannot be fooled by swapping a directory for a link between two checks. And in a sticky directory nobody else can rename or remove an entry
    /// of ours, so after the proof the path stays ours (then the link check is stable). Not a proof for root, which may chown anything: root uses
    /// directories only it can create entries in.
    /// </summary>
    internal static bool IsOwnDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return System.IO.Directory.Exists(path);
        if (lchown(path, geteuid(), uint.MaxValue) != 0) return false;
        var info = new DirectoryInfo(path);
        return info.LinkTarget is null && info.Exists;
    }

    /// <summary>
    /// Creates the directory owner-only, or checks and tightens an existing one. One in the shared temp directory must be this user's own entry
    /// (<see cref="IsOwnDirectory"/>: not another user's, not a symbolic link somebody planted). The others are in places only this user (or root)
    /// can make entries in; there it must not be a link and must take chmod (refused to anybody but its owner). Nobody else can reach the socket.
    /// </summary>
    public void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows()) { System.IO.Directory.CreateDirectory(Directory); return; }
        System.IO.Directory.CreateDirectory(Directory, Private);
        if (Shared ? !IsOwnDirectory(Directory) : new DirectoryInfo(Directory).LinkTarget is not null)
            throw new IOException($"{Directory} is not a directory of this user's (another user's, or a symbolic link): the server's socket does not go there");
        try { File.SetUnixFileMode(Directory, Private); }
        catch (UnauthorizedAccessException) { throw new IOException($"{Directory} belongs to another user: the server's socket does not go there"); }
    }

    /// <summary>A connection to the daemon, or null when none listens (no socket, a stale one, refused).</summary>
    public Socket? TryConnect() => TryConnect(Socket);

    private static Socket? TryConnect(string path)
    {
        if (!File.Exists(path)) return null;
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            s.Connect(new UnixDomainSocketEndPoint(path));
            return s;
        }
        catch (SocketException)
        {
            s.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Points the agent link at <paramref name="agentSocket"/> (this connection's SSH_AUTH_SOCK) with one rename, so a terminal never sees the link
    /// missing; removes it when this connection forwards no agent. Best effort: on failure, terminals keep the previous connection's agent.
    /// </summary>
    public void UpdateAgentLink(string? agentSocket)
    {
        if (OperatingSystem.IsWindows() || agentSocket == Agent) return;
        try
        {
            if (string.IsNullOrEmpty(agentSocket))
            {
                File.Delete(Agent);
                return;
            }
            var temp = Agent + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            File.Delete(temp);
            File.CreateSymbolicLink(temp, agentSocket);
            try { File.Move(temp, Agent, overwrite: true); } // rename(2)
            catch (IOException)
            {
                File.Delete(temp);
                File.Delete(Agent);
                File.CreateSymbolicLink(Agent, agentSocket);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void ReportFailure(Exception error)
    {
        try { File.WriteAllText(Failure, $"{error.GetType().Name}: {error.Message}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The reason a daemon reported (and forgets it), or null.</summary>
    public string? TakeFailure()
    {
        try
        {
            if (!File.Exists(Failure)) return null;
            var text = File.ReadAllText(Failure);
            File.Delete(Failure);
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The sockets of other daemons of the same session: other server builds (an upgraded client), and daemons from before daemons were keyed by
    /// build (<c>&lt;session&gt;.sock</c>). Only private directories of ours are looked at.
    /// </summary>
    public IReadOnlyList<string> OtherBuilds(string dataDir, string session, Func<string, string?>? environment = null)
    {
        var found = new List<string>();
        var env = environment ?? Environment.GetEnvironmentVariable;
        var dirs = PrivateCandidates(dataDir, env).Select(d => (Path: d, Shared: false))
            .Append((Path: SharedDirectory(dataDir, Stem, env, create: false)!, Shared: true)).Where(d => d.Path is not null).DistinctBy(d => d.Path, StringComparer.Ordinal);
        foreach (var (dir, shared) in dirs)
        {
            try
            {
                if (!System.IO.Directory.Exists(dir)) continue;
                if (!OperatingSystem.IsWindows())
                {
                    if (shared ? !IsOwnDirectory(dir) : new DirectoryInfo(dir).LinkTarget is not null) continue; // only ours are looked at
                    if (!shared) File.SetUnixFileMode(dir, Private); // refused unless ours (then: not looked at)
                }
                foreach (var file in System.IO.Directory.EnumerateFiles(dir, session + "*.sock"))
                {
                    var name = Path.GetFileName(file);
                    if (file != Socket && (name == session + ".sock" || (name.StartsWith(session + "-", StringComparison.Ordinal) && !name[(session.Length + 1)..^5].Contains('.'))))
                        found.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    /// <summary>Asks the daemon listening on <paramref name="socket"/> to shut down (the protocol's <c>server/shutdown</c>); bounded, best effort.</summary>
    public static void AskToShutDown(string socket)
    {
        if (TryConnect(socket) is not { } connection) return;
        using var stream = new NetworkStream(connection, ownsSocket: true);
        using var peer = new RpcPeer(stream, stream, new RpcPeerOptions { Name = "relay", Json = RemoteJson.Create(), HandlerThreads = 1 });
        peer.Start();
        try { peer.InvokeAsync<Empty, Empty>(RemoteMethods.Shutdown, Empty.Value, CancellationToken.None, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { }
    }
}

/// <summary>
/// The persistent server (R4): one per client session and server build, detached from the ssh connection that started it (its own session:
/// <c>setsid</c>; stdio on /dev/null). It listens on the session's socket and serves one client connection at a time — a newer connection replaces
/// an older one (the client reconnected while the old link was still half-open). Between connections its state stays: language servers,
/// terminals, debug adapters, task runs, open documents. It exits when a client asks it to shut down (a deliberate disconnect), on SIGTERM, or after
/// <c>--grace</c> seconds without any client. A failure to start is written where the relay that started it looks for it.
/// </summary>
internal static class ServerDaemon
{
    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    public static async Task<int> RunAsync(ServerOptions options)
    {
        if (!OperatingSystem.IsWindows()) _ = setsid(); // leave the ssh session's process group: a hang-up there does not reach us
        PersistencePaths paths;
        try
        {
            paths = PersistencePaths.For(options.DataDir, options.Daemon!);
            paths.EnsureDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 1; } // the relay ran the same checks and reported them
        FileStream? held;
        try { held = new FileStream(paths.Lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return 0; } // another daemon serves this session
        catch (UnauthorizedAccessException ex) { paths.ReportFailure(ex); return 1; }
        await using var lockFile = held;
        if (paths.TryConnect() is { } alive)
        {
            alive.Dispose();
            return 0; // raced with another daemon that already listens
        }

        Socket listener;
        ServerHost host;
        try
        {
            if (File.Exists(paths.Socket) || System.IO.Directory.Exists(paths.Socket)) File.Delete(paths.Socket); // a stale socket of a daemon that died (we hold the lock)
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(paths.Socket));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(paths.Socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                listener.Listen(4);
                host = new ServerHost(options);
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nobody sees this process's output: the relay waiting for the socket reports it to the client.
            paths.ReportFailure(ex);
            return 1;
        }

        using var listening = listener;
        var terminate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; terminate.TrySetResult(); });
        using var sighup = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => c.Cancel = true);

        await using var served = host;
        var log = ((IServiceProvider)host.Services).GetService(typeof(ILoggerFactory)) is ILoggerFactory f ? f.CreateLogger("CsCode.Server.Daemon") : null;
        log?.LogInformation("Persistent server {Session} (build {Build}) listening (pid {Pid}, grace {Grace} s)", options.Daemon, ServerBuild.Id, Environment.ProcessId,
            options.GraceSeconds);
        var grace = TimeSpan.FromSeconds(options.GraceSeconds);
        var accepting = listener.AcceptAsync();
        Task<bool>? serving = null;
        CancellationTokenSource? current = null;
        try
        {
            while (true)
            {
                var idle = serving is null ? Task.Delay(grace) : Task.Delay(Timeout.Infinite);
                var done = await Task.WhenAny(accepting, host.ShutdownRequested, terminate.Task, idle, serving ?? Task.Delay(Timeout.Infinite)).ConfigureAwait(false);
                if (done == host.ShutdownRequested || done == terminate.Task) break;
                if (done == idle)
                {
                    log?.LogInformation("No client for {Grace} s: exiting", options.GraceSeconds);
                    break;
                }
                if (serving is not null && done == serving)
                {
                    var shutdown = await serving.ConfigureAwait(false);
                    serving = null;
                    current?.Dispose();
                    current = null;
                    if (shutdown) break;
                    continue;
                }
                var socket = await accepting.ConfigureAwait(false);
                accepting = listener.AcceptAsync();
                if (current is not null)
                {
                    // The newer connection wins: the client reconnected while its old link was still half-open.
                    current.Cancel();
                    if (serving is not null) await Task.WhenAny(serving, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                    current.Dispose();
                }
                current = new CancellationTokenSource();
                var stream = new NetworkStream(socket, ownsSocket: true);
                serving = host.ServeAsync(stream, stream, current.Token);
            }
        }
        finally
        {
            current?.Cancel();
            // Nothing of this daemon is left in the (possibly shared) socket directory; the lock goes last, while we still hold it.
            foreach (var file in new[] { paths.Socket, paths.Agent, paths.Lock })
                try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return 0;
    }
}

/// <summary>
/// The stdio side of a persistent server (what ssh runs): connects to the daemon of its session and build — starting it, detached, when none runs,
/// after asking the daemons of other builds of the same session to shut down — points the daemon's agent link at this connection's ssh agent,
/// writes the greeting and relays bytes both ways until either side ends. A dropped ssh connection ends only the relay; the daemon waits for the client.
/// </summary>
internal static class ServerRelay
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    public static int Run(ServerOptions options, Stream stdin, Stream stdout)
    {
        PersistencePaths paths;
        try
        {
            paths = PersistencePaths.For(options.DataDir, options.Persist!);
            paths.EnsureDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("cscode-server: no place for the persistent server's socket: " + ex.Message);
            return 1;
        }
        // Before the daemon can start anything: its terminals and tasks reach a forwarded agent through this link (the daemon's SSH_AUTH_SOCK).
        var agent = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        paths.UpdateAgentLink(agent);
        var socket = paths.TryConnect();
        if (socket is null)
        {
            // An upgraded client: the session's daemon of the previous build cannot be reattached to (its protocol may differ); it ends now instead
            // of waiting out its grace period with its terminals and language servers.
            foreach (var other in paths.OtherBuilds(options.DataDir, options.Persist!)) PersistencePaths.AskToShutDown(other);
            paths.TakeFailure(); // an old report is not this start's
            try { StartDaemon(options, string.IsNullOrEmpty(agent) ? null : paths.Agent); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                Console.Error.WriteLine("cscode-server: could not start the persistent server: " + ex.Message);
                return 1;
            }
            var clock = Stopwatch.StartNew();
            while ((socket = paths.TryConnect()) is null && clock.Elapsed < StartTimeout)
            {
                if (paths.TakeFailure() is { } why)
                {
                    Console.Error.WriteLine("cscode-server: the persistent server could not start: " + why);
                    return 1;
                }
                Thread.Sleep(50);
            }
            if (socket is null)
            {
                Console.Error.WriteLine("cscode-server: the persistent server did not start (see its log in " + Path.Combine(options.DataDir, "logs") + ")");
                return 1;
            }
        }
        using var connection = new NetworkStream(socket, ownsSocket: true);
        var greeting = Encoding.ASCII.GetBytes("\n" + RemoteProtocol.Greeting + "\n");
        stdout.Write(greeting);
        stdout.Flush();
        var up = new Thread(() => Pump(stdin, connection, () => { try { socket.Shutdown(SocketShutdown.Send); } catch (SocketException) { } catch (ObjectDisposedException) { } }))
            { IsBackground = true, Name = "relay-up" };
        up.Start();
        Pump(connection, stdout, () => { });
        return 0;
    }

    private static void Pump(Stream from, Stream to, Action done)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int n;
            while ((n = from.Read(buffer)) > 0)
            {
                to.Write(buffer, 0, n);
                to.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
        done();
    }

    /// <summary>
    /// Starts <c>cscode-server --daemon</c> through <c>/bin/sh</c> in the background with stdio on /dev/null: sh exits at once, so the daemon is not
    /// this relay's child (a client tearing down the relay's process tree leaves it alone) and holds none of ssh's pipes (ssh can close).
    /// </summary>
    /// <param name="agentLink">The daemon's SSH_AUTH_SOCK: the link every later relay repoints (null: this connection forwards no agent).</param>
    private static void StartDaemon(ServerOptions options, string? agentLink)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("the server's own executable is unknown");
        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-c", "exec \"$0\" \"$@\" </dev/null >/dev/null 2>&1 &", self }) psi.ArgumentList.Add(a);
        // Under `dotnet cscode-server.dll` (the portable bundle, tests) argv[0] is the dll.
        if (string.Equals(Path.GetFileNameWithoutExtension(self), "dotnet", StringComparison.OrdinalIgnoreCase)
            && Environment.GetCommandLineArgs() is [{ Length: > 0 } entry, ..] && entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(entry);
        foreach (var a in new[] { "--daemon", options.Persist!, "--data-dir", options.DataDir, "--grace", options.GraceSeconds.ToString(CultureInfo.InvariantCulture) })
            psi.ArgumentList.Add(a);
        if (options.Marker is { } marker) { psi.ArgumentList.Add("--marker"); psi.ArgumentList.Add(marker); }
        // The first connection's agent socket dies with that connection: the daemon (and every terminal and task it starts) uses the link instead.
        if (agentLink is not null) psi.Environment["SSH_AUTH_SOCK"] = agentLink;
        using var sh = Process.Start(psi) ?? throw new InvalidOperationException("/bin/sh did not start");
        sh.StandardInput.Close();
        if (!sh.WaitForExit(10_000)) throw new InvalidOperationException("/bin/sh did not return");
    }
}
