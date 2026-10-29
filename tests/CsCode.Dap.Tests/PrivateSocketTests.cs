using System.Diagnostics;
using System.Globalization;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Transport;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>Which sockets a process holds (Linux /proc): its TCP listeners and its Unix domain sockets, for the private-socket tests.</summary>
internal static class SocketTable
{
    private static HashSet<string> SocketInodes(int pid)
    {
        var inodes = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var fd in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd"))
            {
                try
                {
                    if (new FileInfo(fd).LinkTarget is { } target && target.StartsWith("socket:[", StringComparison.Ordinal)) inodes.Add(target[8..^1]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* closed meanwhile */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return inodes;
    }

    /// <summary>The TCP sockets in LISTEN state that <paramref name="pid"/> holds ("local address:port" in /proc's hex form).</summary>
    public static List<string> TcpListeners(int pid)
    {
        var mine = SocketInodes(pid);
        var result = new List<string>();
        foreach (var table in new[] { "tcp", "tcp6" })
        {
            string[] lines;
            try { lines = File.ReadAllLines($"/proc/{pid}/net/{table}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var line in lines.Skip(1))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length > 9 && f[3] == "0A" && mine.Contains(f[9])) result.Add($"{table} {f[1]}");
            }
        }
        return result;
    }

    /// <summary>The paths of the Unix domain sockets <paramref name="pid"/> holds that are bound to a path.</summary>
    public static List<string> UnixSocketPaths(int pid)
    {
        var mine = SocketInodes(pid);
        var result = new List<string>();
        string[] lines;
        try { lines = File.ReadAllLines($"/proc/{pid}/net/unix"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return result; }
        foreach (var line in lines.Skip(1))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length > 7 && mine.Contains(f[6])) result.Add(f[7]);
        }
        return result;
    }

    /// <summary>The numeric owner and the octal mode of a path (<c>stat -c '%u %a'</c>).</summary>
    public static (int Uid, string Mode) OwnerAndMode(string path)
    {
        var psi = new ProcessStartInfo("stat") { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("%u %a");
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd().Trim().Split(' ');
        p.WaitForExit();
        return (int.Parse(text[0], CultureInfo.InvariantCulture), text[1]);
    }

    /// <summary>This process's real user id (<c>/proc/self/status</c>).</summary>
    public static int MyUid() =>
        int.Parse(File.ReadAllLines("/proc/self/status").First(l => l.StartsWith("Uid:", StringComparison.Ordinal)).Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);

    /// <summary>The pid of a process that has exited (a stale directory's creator).</summary>
    public static int DeadPid()
    {
        using var p = Process.Start(new ProcessStartInfo("/bin/true") { UseShellExecute = false })!;
        p.WaitForExit();
        return p.Id;
    }
}

/// <summary>
/// The private socket of an adapter server (<see cref="PrivateSocket"/>, <see cref="ServerProcessTransport"/>): a fresh 0700 directory, the
/// socket path handed to the server, the directory gone after every kind of end, stale directories of a killed CsCode removed at the next
/// start. Hermetic: sockets are made under a temp directory, the server is a small Node.js or shell stand-in. Linux only (/proc).
/// </summary>
[Collection(SerialCollection.Name)]
public class PrivateSocketTests
{
    private const string Prefix = "cscode-test";

    /// <summary>Listens on the path it gets, says so like js-debug, and keeps every connection open.</summary>
    private const string NodeServer = "const p=process.argv[1];require('net').createServer(c=>c.on('data',()=>{})).listen(p,()=>console.log('Debug server listening at '+p));";

    private static void SkipUnlessLinux() => Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/sh"), "needs Linux /proc and /bin/sh");

    private static string Node()
    {
        var node = JsDebugToolchain.FindNode();
        Assert.SkipWhen(node is null, "needs node on PATH (the stand-in server listens on a Unix domain socket)");
        return node!;
    }

    private static DebugAdapterServerProcess Server(string command, IReadOnlyList<string> args, string root, TimeSpan? timeout = null) =>
        new(command, args, JsDebugAdapterFactory.ListeningPattern)
        {
            DisplayName = "fake server", PrivateSocket = true, SocketRoot = root, SocketPrefix = Prefix, StartTimeout = timeout ?? TimeSpan.FromSeconds(15),
        };

    private static List<string> Dirs(TempDirectory root) => [.. Directory.EnumerateFileSystemEntries(root.Path, Prefix + "-*")];

    [Fact]
    public async Task The_server_gets_a_socket_path_in_a_fresh_0700_directory_and_the_client_connects_there()
    {
        SkipUnlessLinux();
        using var root = new TempDirectory();
        var transport = await ServerProcessTransport.StartAsync(Server(Node(), ["-e", NodeServer, DebugAdapterServerProcess.SocketPathArgument], root.Path), NullLogger.Instance, Ct);
        string dir;
        try
        {
            dir = transport.SocketDirectory!;
            var path = Assert.IsType<DebugAdapterNamedPipe>(transport.Endpoint).Path;
            Assert.Equal(Path.Combine(dir, PrivateSocket.SocketFileName), path);
            Assert.Equal(root.Path, Path.GetDirectoryName(dir));
            Assert.Matches($"^{Prefix}-{Environment.ProcessId}-[0-9]+-[0-9a-f]{{24}}$", Path.GetFileName(dir));
            Assert.Equal((SocketTable.MyUid(), "700"), SocketTable.OwnerAndMode(dir));
            Assert.Contains(path, ProcessTable.CommandLine(transport.ProcessId!.Value)); // the server was told the path…
            Assert.Contains(path, SocketTable.UnixSocketPaths(transport.ProcessId!.Value)); // …listens there…
            Assert.Empty(SocketTable.TcpListeners(transport.ProcessId!.Value)); // …and nowhere on TCP
            Assert.Equal("unix:" + path, transport.Description[(transport.Description.IndexOf("unix:", StringComparison.Ordinal))..]);
        }
        finally { await transport.DisposeAsync(); }
        Assert.False(Directory.Exists(dir), "the directory goes with the transport");
        Assert.Empty(Dirs(root));
    }

    [Fact]
    public async Task The_socket_directory_is_deleted_after_a_crash_of_the_server_and_by_a_synchronous_kill()
    {
        SkipUnlessLinux();
        using var root = new TempDirectory();
        var node = Node();

        // the server dies on its own: the session notices the closed connection and closes the transport
        var crashed = await ServerProcessTransport.StartAsync(Server(node, ["-e", NodeServer, DebugAdapterServerProcess.SocketPathArgument], root.Path), NullLogger.Instance, Ct);
        var crashedDir = crashed.SocketDirectory!;
        using (var p = Process.GetProcessById(crashed.ProcessId!.Value)) p.Kill();
        await crashed.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(File.Exists(Path.Combine(crashedDir, PrivateSocket.SocketFileName)), "a killed server leaves its socket file behind");
        await crashed.DisposeAsync();
        Assert.False(Directory.Exists(crashedDir), "deleted after the crash");

        // a container disposed synchronously (DebugService.Dispose → KillNow): deleted at once, before any async close
        var killed = await ServerProcessTransport.StartAsync(Server(node, ["-e", NodeServer, DebugAdapterServerProcess.SocketPathArgument], root.Path), NullLogger.Instance, Ct);
        var killedDir = killed.SocketDirectory!;
        killed.KillNow();
        Assert.False(Directory.Exists(killedDir), "deleted by KillNow itself");
        await killed.DisposeAsync();
        Assert.Empty(Dirs(root));
    }

    [Fact]
    public async Task A_server_that_never_listens_times_out_is_killed_and_leaves_no_directory()
    {
        SkipUnlessLinux();
        using var root = new TempDirectory();
        var probe = Guid.NewGuid().ToString("N");
        var ex = await Assert.ThrowsAsync<TimeoutException>(async () => await ServerProcessTransport.StartAsync(
            Server("/bin/sh", ["-c", "echo still booting; exec sleep 300", DebugAdapterServerProcess.SocketPathArgument], root.Path, TimeSpan.FromMilliseconds(800))
                with { Env = new Dictionary<string, string> { ["PROBE"] = probe } }, NullLogger.Instance, Ct));
        Assert.Contains("did not report that it listens on its socket", ex.Message);
        await SessionHarness.Eventually(() => ProcessTable.WithEnvironment("PROBE", probe).Count == 0, 5_000, "the silent server was killed");
        Assert.Empty(Dirs(root));

        // a server that exits at once: same
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await ServerProcessTransport.StartAsync(
            Server("/bin/sh", ["-c", "echo nope >&2; exit 3"], root.Path), NullLogger.Instance, Ct));
        Assert.Empty(Dirs(root));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")] // skipped elsewhere (SkipUnlessLinux)
    public void Stale_directories_of_a_killed_CsCode_are_removed_at_the_next_start_but_not_those_of_a_running_one_or_look_alikes()
    {
        SkipUnlessLinux();
        using var root = new TempDirectory();
        var owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        string Make(string name, UnixFileMode mode)
        {
            var dir = Path.Combine(root.Path, name);
            Directory.CreateDirectory(dir, mode);
            File.SetUnixFileMode(dir, mode);
            File.WriteAllText(Path.Combine(dir, PrivateSocket.SocketFileName), ""); // stands in for the socket file a killed server left
            return dir;
        }
        var me = Environment.ProcessId;
        var myStamp = PrivateSocket.Stamp(me)!;
        var dead = Make($"{Prefix}-{SocketTable.DeadPid()}-12345-00112233445566778899aabb", owner);         // its CsCode was killed
        var reused = Make($"{Prefix}-{me}-{long.Parse(myStamp, CultureInfo.InvariantCulture) + 1}-aabbccddeeff001122334455", owner); // pid reused by another process
        var live = Make($"{Prefix}-{me}-{myStamp}-ffeeddccbbaa998877665544", owner);                    // a CsCode that runs (this one)
        var open = Make($"{Prefix}-{SocketTable.DeadPid()}-1-0123456789abcdef01234567", owner | UnixFileMode.GroupRead | UnixFileMode.GroupExecute); // not 0700: not ours
        var other = Make($"other-{SocketTable.DeadPid()}-1-0123456789abcdef01234567", owner);           // another prefix
        var target = Make("target", owner);
        var link = Path.Combine(root.Path, $"{Prefix}-{SocketTable.DeadPid()}-1-abcdefabcdefabcdefabcdef");
        File.CreateSymbolicLink(link, target);                                                           // a link: never followed or removed

        var socket = PrivateSocket.Create(Prefix, root.Path);
        try
        {
            Assert.False(Directory.Exists(dead));
            Assert.False(Directory.Exists(reused));
            Assert.True(Directory.Exists(live));
            Assert.True(Directory.Exists(open));
            Assert.True(Directory.Exists(other));
            Assert.True(File.Exists(Path.Combine(target, PrivateSocket.SocketFileName)));
            Assert.NotNull(new FileInfo(link).LinkTarget);
            Assert.True(Directory.Exists(socket.Directory));
        }
        finally { socket.Delete(); }
        Assert.False(Directory.Exists(socket.Directory));
    }

    [Fact]
    public void A_root_too_long_for_a_socket_path_falls_back_to_tmp()
    {
        SkipUnlessLinux();
        using var root = new TempDirectory();
        var deep = Path.Combine(root.Path, new string('d', 60), new string('e', 60));
        Directory.CreateDirectory(deep);
        var socket = PrivateSocket.Create(Prefix, deep);
        try
        {
            Assert.StartsWith("/tmp/" + Prefix + "-", socket.Directory, StringComparison.Ordinal);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(socket.Path) <= PrivateSocket.MaxSocketPathBytes);
            Assert.Equal((SocketTable.MyUid(), "700"), SocketTable.OwnerAndMode(socket.Directory!));
        }
        finally { socket.Delete(); }
        Assert.False(Directory.Exists(socket.Directory));
    }

    [Fact]
    public void Sockets_go_under_XDG_RUNTIME_DIR_when_it_is_set_else_the_temp_directory()
    {
        using var runtime = new TempDirectory();
        Assert.Equal(runtime.Path, PrivateSocket.DefaultRoot(name => name == "XDG_RUNTIME_DIR" ? runtime.Path : null));
        Assert.Equal(Path.GetTempPath(), PrivateSocket.DefaultRoot(_ => null));
        Assert.Equal(Path.GetTempPath(), PrivateSocket.DefaultRoot(name => name == "XDG_RUNTIME_DIR" ? Path.Combine(runtime.Path, "missing") : null));
        Assert.Equal(Path.GetTempPath(), PrivateSocket.DefaultRoot(name => name == "XDG_RUNTIME_DIR" ? "relative/dir" : null));
    }
}
