using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.Logging;

namespace CsCode.Server;

/// <summary>
/// Where a persistent server of a session listens: a Unix socket (mode 0600) in a private directory (0700) — the data dir's <c>run/</c>, or
/// <c>$XDG_RUNTIME_DIR/cscode-server</c> when that path would be too long for a socket address — plus a lock file that only the live daemon holds.
/// </summary>
internal sealed partial record PersistencePaths(string Directory, string Socket, string Lock)
{
    private const int MaxSocketPathBytes = 100; // sun_path is 108 bytes on Linux, 104 on macOS

    [GeneratedRegex("^[0-9a-f]{8,64}$")]
    private static partial Regex SessionPattern();

    public static string ValidSession(string session) =>
        SessionPattern().IsMatch(session) ? session : throw new ArgumentException($"Invalid session id '{session}' (8-64 lower-case hex digits)");

    public static PersistencePaths For(string dataDir, string session)
    {
        ValidSession(session);
        var dir = Path.Combine(dataDir, "run");
        if (Encoding.UTF8.GetByteCount(Path.Combine(dir, session + ".sock")) > MaxSocketPathBytes
            && Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && System.IO.Directory.Exists(runtime))
            dir = Path.Combine(runtime, "cscode-server");
        return new PersistencePaths(dir, Path.Combine(dir, session + ".sock"), Path.Combine(dir, session + ".lock"));
    }

    /// <summary>Creates the directory owner-only (and tightens an existing one): nobody else can reach the socket.</summary>
    public void EnsureDirectory()
    {
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (OperatingSystem.IsWindows()) { System.IO.Directory.CreateDirectory(Directory); return; }
        System.IO.Directory.CreateDirectory(Directory, Private);
        File.SetUnixFileMode(Directory, Private);
    }

    /// <summary>A connection to the daemon, or null when none listens (no socket, a stale one, refused).</summary>
    public Socket? TryConnect()
    {
        if (!File.Exists(Socket)) return null;
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            s.Connect(new UnixDomainSocketEndPoint(Socket));
            return s;
        }
        catch (SocketException)
        {
            s.Dispose();
            return null;
        }
    }
}

/// <summary>
/// The persistent server (R4): one per client session, detached from the ssh connection that started it (its own session: <c>setsid</c>; stdio on
/// /dev/null). It listens on the session's socket and serves one client connection at a time — a newer connection replaces an older one (the client
/// reconnected while the old link was still half-open). Between connections its state stays: language servers, terminals, debug adapters, task
/// runs, open documents. It exits when a client asks it to shut down (a deliberate disconnect), on SIGTERM, or after <c>--grace</c> seconds without
/// any client.
/// </summary>
internal static class ServerDaemon
{
    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    public static async Task<int> RunAsync(ServerOptions options)
    {
        if (!OperatingSystem.IsWindows()) _ = setsid(); // leave the ssh session's process group: a hang-up there does not reach us
        var paths = PersistencePaths.For(options.DataDir, options.Daemon!);
        paths.EnsureDirectory();
        FileStream? held;
        try { held = new FileStream(paths.Lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return 0; } // another daemon serves this session
        await using var lockFile = held;
        if (paths.TryConnect() is { } alive)
        {
            alive.Dispose();
            return 0; // raced with another daemon that already listens
        }
        try { File.Delete(paths.Socket); } catch (IOException) { } // a stale socket of a daemon that died (we hold the lock)
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(paths.Socket));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(paths.Socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(4);

        var terminate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; terminate.TrySetResult(); });
        using var sighup = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => c.Cancel = true);

        await using var host = new ServerHost(options);
        var log = ((IServiceProvider)host.Services).GetService(typeof(ILoggerFactory)) is ILoggerFactory f ? f.CreateLogger("CsCode.Server.Daemon") : null;
        log?.LogInformation("Persistent server {Session} listening (pid {Pid}, grace {Grace} s)", options.Daemon, Environment.ProcessId, options.GraceSeconds);
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
            try { File.Delete(paths.Socket); } catch (IOException) { }
        }
        return 0;
    }
}

/// <summary>
/// The stdio side of a persistent server (what ssh runs): connects to the session's daemon — starting it, detached, when none runs — writes the
/// greeting and relays bytes both ways until either side ends. A dropped ssh connection ends only the relay; the daemon waits for the client.
/// </summary>
internal static class ServerRelay
{
    public static int Run(ServerOptions options, Stream stdin, Stream stdout)
    {
        var paths = PersistencePaths.For(options.DataDir, options.Persist!);
        paths.EnsureDirectory();
        var socket = paths.TryConnect();
        if (socket is null)
        {
            try { StartDaemon(options); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                Console.Error.WriteLine("cscode-server: could not start the persistent server: " + ex.Message);
                return 1;
            }
            var clock = Stopwatch.StartNew();
            while ((socket = paths.TryConnect()) is null && clock.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(50);
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
    private static void StartDaemon(ServerOptions options)
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
        foreach (var a in new[] { "--daemon", options.Persist!, "--data-dir", options.DataDir, "--grace", options.GraceSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            psi.ArgumentList.Add(a);
        if (options.Marker is { } marker) { psi.ArgumentList.Add("--marker"); psi.ArgumentList.Add(marker); }
        using var sh = Process.Start(psi) ?? throw new InvalidOperationException("/bin/sh did not start");
        sh.StandardInput.Close();
        if (!sh.WaitForExit(10_000)) throw new InvalidOperationException("/bin/sh did not return");
    }
}
