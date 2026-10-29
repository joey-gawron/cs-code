using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CsCode.Core.Debugging;
using CsCode.Dap.Tests.Fakes;
using CsCode.Dap.Transport;
using CsCode.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CsCode.Dap.Tests;

[Collection(SerialCollection.Name)]
public class ProcessTransportTests
{
    private static void NeedsShell() => Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "needs /bin/sh");

    private static IDapTransport Start(string script) =>
        ProcessTransport.Start(new DebugAdapterExecutable("/bin/sh", ["-c", script]), NullLogger.Instance);

    private static byte[] Frame(string json) => Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}");

    [Fact]
    public async Task Stdio_round_trips_framed_messages_through_a_child_process()
    {
        NeedsShell();
        await using var t = Start("cat");
        Assert.NotNull(t.ProcessId);
        await t.Output.WriteAsync(Frame("""{"seq":1,"type":"request","command":"x"}"""), Ct);
        await t.Output.FlushAsync(Ct);
        var reader = new DapFrameReader(t.Input);
        Assert.Equal(DapFrameStatus.Frame, await reader.ReadAsync(Ct));
        Assert.Equal("""{"seq":1,"type":"request","command":"x"}""", Encoding.UTF8.GetString(reader.Payload.Span));
    }

    [Fact]
    public async Task Closing_stdin_lets_a_well_behaved_adapter_exit_by_itself()
    {
        NeedsShell();
        await using var t = Start("cat");
        var pid = t.ProcessId!.Value;
        var sw = Stopwatch.StartNew();
        await t.CloseAsync(TimeSpan.FromSeconds(10));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"cat should exit as soon as stdin closes ({sw.Elapsed})");
        await t.Completion.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.False(SessionReverseRequestTests.ProcessAlive(pid));
    }

    [Fact]
    public async Task An_adapter_that_ignores_stdin_closing_is_killed_after_the_grace_period_with_its_children()
    {
        NeedsShell();
        await using var t = Start("sleep 300 & echo $! 1>&2; wait");
        var pid = t.ProcessId!.Value;
        await SessionHarness.Eventually(() => int.TryParse(t.StandardErrorTail.Trim(), out _), 5000, "child pid on stderr");
        var childPid = int.Parse(t.StandardErrorTail.Trim());
        Assert.True(SessionReverseRequestTests.ProcessAlive(pid) && SessionReverseRequestTests.ProcessAlive(childPid));

        var sw = Stopwatch.StartNew();
        await t.CloseAsync(TimeSpan.FromMilliseconds(300));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        await SessionHarness.Eventually(() => !SessionReverseRequestTests.ProcessAlive(pid), 5000, "adapter must be killed");
        await SessionHarness.Eventually(() => !SessionReverseRequestTests.ProcessAlive(childPid), 5000, "its child (process TREE) must be killed too");
    }

    [Fact]
    public async Task Dispose_kills_a_hung_adapter_and_never_leaves_a_zombie()
    {
        NeedsShell();
        var t = Start("sleep 300");
        var pid = t.ProcessId!.Value;
        await t.DisposeAsync();
        await SessionHarness.Eventually(() => !SessionReverseRequestTests.ProcessAlive(pid), 5000, "no zombie, no orphan");
        await t.DisposeAsync(); // idempotent
    }

    [Fact]
    public async Task Process_exit_completes_Completion_without_anyone_closing_it()
    {
        NeedsShell();
        await using var t = Start("exit 0");
        await t.Completion.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task Stderr_is_captured_but_bounded()
    {
        NeedsShell();
        await using var t = Start("echo first-line 1>&2; head -c 200000 /dev/zero | tr '\\0' 'x' 1>&2; echo last-line 1>&2");
        await t.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await SessionHarness.Eventually(() => t.StandardErrorTail.Contains("last-line"), 5000);
        var tail = t.StandardErrorTail;
        Assert.True(tail.Length <= 16 * 1024, $"stderr tail holds {tail.Length} chars");
        Assert.DoesNotContain("first-line", tail); // oldest dropped
    }

    [Fact]
    public void A_missing_executable_throws_at_start_with_the_command_named()
    {
        var ex = Assert.ThrowsAny<Exception>(() => ProcessTransport.Start(new DebugAdapterExecutable("/definitely/not/an/adapter", []), NullLogger.Instance));
        Assert.True(ex is System.ComponentModel.Win32Exception or InvalidOperationException, ex.GetType().Name);
    }

    [Fact]
    public async Task Cwd_args_and_env_of_the_descriptor_are_honoured()
    {
        NeedsShell();
        using var dir = new TempDirectory();
        var exe = new DebugAdapterExecutable("/bin/sh", ["-c", "pwd 1>&2; echo \"$CSCODE_ADAPTER_ENV $0\" 1>&2", "argzero"],
            new Dictionary<string, string> { ["CSCODE_ADAPTER_ENV"] = "from-descriptor" }, dir.Path);
        await using var t = ProcessTransport.Start(exe, NullLogger.Instance);
        await t.Completion.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await SessionHarness.Eventually(() => t.StandardErrorTail.Contains("from-descriptor argzero"));
        Assert.Contains(Path.GetFileName(dir.Path), t.StandardErrorTail);
    }

    [Fact]
    public async Task Factory_opens_executable_descriptors()
    {
        NeedsShell();
        await using var t = await DapTransportFactory.OpenAsync(new DebugAdapterExecutable("/bin/sh", ["-c", "cat"]), NullLogger.Instance, TimeSpan.FromSeconds(1), Ct);
        Assert.IsType<ProcessTransport>(t);
    }
}

public class SocketTransportTests
{
    [Fact]
    public async Task Tcp_connects_round_trips_and_closes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync(Ct);
            var s = c.GetStream();
            var buf = new byte[64];
            var n = await s.ReadAsync(buf, Ct);
            await s.WriteAsync(buf.AsMemory(0, n), Ct);
        }, Ct);

        await using var t = await TcpTransport.ConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(5), Ct);
        await t.Output.WriteAsync("ping"u8.ToArray(), Ct);
        var got = new byte[4];
        Assert.Equal(4, await t.Input.ReadAsync(got, Ct));
        Assert.Equal("ping", Encoding.ASCII.GetString(got));
        await server;
        Assert.Contains(port.ToString(), t.Description);
        await t.CloseAsync(TimeSpan.Zero);
        await t.Completion.WaitAsync(TimeSpan.FromSeconds(2), Ct);
    }

    [Fact]
    public async Task Tcp_retries_until_the_adapter_starts_listening()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop(); // nobody listens yet
        var connect = TcpTransport.ConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(10), Ct).AsTask();
        await Task.Delay(400, Ct);
        Assert.False(connect.IsCompleted);
        using var late = new TcpListener(IPAddress.Loopback, port);
        late.Start();
        await using var t = await connect;
        Assert.NotNull(t);
    }

    [Fact]
    public async Task Tcp_gives_up_with_a_clear_timeout()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TimeoutException>(async () => await TcpTransport.ConnectAsync("127.0.0.1", port, TimeSpan.FromMilliseconds(400), Ct));
        Assert.Contains(port.ToString(), ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Tcp_connect_can_be_cancelled()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await TcpTransport.ConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task Unix_socket_transport_round_trips()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "unix domain sockets");
        using var dir = new TempDirectory("sock-");
        var path = Path.Combine(dir.Path, "adapter.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptAsync(Ct);
            var buf = new byte[64];
            var n = await c.ReceiveAsync(buf, Ct);
            await c.SendAsync(buf.AsMemory(0, n), Ct);
        }, Ct);

        await using var t = await PipeTransport.ConnectAsync(path, TimeSpan.FromSeconds(5), Ct);
        await t.Output.WriteAsync("pong"u8.ToArray(), Ct);
        var got = new byte[4];
        Assert.Equal(4, await t.Input.ReadAsync(got, Ct));
        Assert.Equal("pong", Encoding.ASCII.GetString(got));
        await server;
    }

    [Fact]
    public async Task Unix_socket_waits_for_the_socket_file_and_times_out_clearly()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "unix domain sockets");
        using var dir = new TempDirectory("sock-");
        var ex = await Assert.ThrowsAsync<TimeoutException>(async () => await PipeTransport.ConnectAsync(Path.Combine(dir.Path, "missing.sock"), TimeSpan.FromMilliseconds(300), Ct));
        Assert.Contains("missing.sock", ex.Message);
    }

    [Fact]
    public async Task Factory_dispatches_server_and_pipe_descriptors()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () => { using var _ = await listener.AcceptTcpClientAsync(Ct); await Task.Delay(500, Ct); }, Ct);
        await using var tcp = await DapTransportFactory.OpenAsync(new DebugAdapterServer("127.0.0.1", port), NullLogger.Instance, TimeSpan.FromSeconds(3), Ct);
        Assert.IsType<TcpTransport>(tcp);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            using var dir = new TempDirectory("sock-");
            var path = Path.Combine(dir.Path, "a.sock");
            using var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            unix.Bind(new UnixDomainSocketEndPoint(path));
            unix.Listen(1);
            _ = Task.Run(async () => { using var _ = await unix.AcceptAsync(Ct); await Task.Delay(500, Ct); }, Ct);
            await using var pipe = await DapTransportFactory.OpenAsync(new DebugAdapterNamedPipe(path), NullLogger.Instance, TimeSpan.FromSeconds(3), Ct);
            Assert.IsType<PipeTransport>(pipe);
        }
    }
}
