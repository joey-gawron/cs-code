using CsCode.Core.Remote;
using CsCode.Remote.Client;
using CsCode.Remote.Protocol;
using CsCode.Remote.Transport;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>A clock tests move by hand (Task.Delay(delay, clock) and every timer created from it fire only on <see cref="Advance"/>).</summary>
public sealed class ManualClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
    public override long GetTimestamp() { lock (_gate) return _now.UtcTicks; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public int PendingTimers { get { lock (_gate) return _timers.Count(t => t.Due is not null); } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state);
        lock (_gate) _timers.Add(t);
        t.Change(dueTime, period);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.Due <= _now).ToList();
            foreach (var t in due) t.Due = null;
        }
        foreach (var t in due) t.Fire();
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due;
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate) Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
            return true;
        }
        public void Dispose() { lock (clock._gate) { Due = null; clock._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>An in-memory "server" that answers the handshake; tests drop it, refuse connections, and count what happened.</summary>
public sealed class FakeTransport : IRemoteTransport
{
    private readonly object _gate = new();
    private RpcPeer? _server;
    public int Connects;
    public bool Refuse;
    /// <summary>The failure of the next connection attempts (instead of "connection refused" when <see cref="Refuse"/>).</summary>
    public Exception? Failure;
    public string Description => "fake";

    public Task<RemoteChannel> ConnectAsync(IProgress<string>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref Connects);
        if (Failure is { } failure) return Task.FromException<RemoteChannel>(failure);
        if (Refuse) return Task.FromException<RemoteChannel>(new IOException("connection refused"));
        var toServer = new MemoryPipe();
        var toClient = new MemoryPipe();
        var server = new RpcPeer(toServer.Reader, toClient.Writer, new RpcPeerOptions { Name = "fake-server" });
        server.OnRequest<HelloParams, HelloResult>(RemoteMethods.Hello, (_, _) =>
            ValueTask.FromResult(new HelloResult(RemoteProtocol.Version, "fake", "Linux", "x64", 1, "/home/fake", "/data", [])));
        server.OnRequest<Empty, Empty>(RemoteMethods.Shutdown, (_, _) => ValueTask.FromResult(Empty.Value));
        server.Start();
        lock (_gate) _server = server;
        return Task.FromResult(new RemoteChannel(toClient.Reader, toServer.Writer, "fake", _ =>
        {
            server.Close();
            toServer.Close();
            toClient.Close();
            return Task.CompletedTask;
        }));
    }

    /// <summary>The server end goes away (network drop / crash).</summary>
    public void Drop() { lock (_gate) _server?.Close(); }
}

public sealed class RecordingParticipant : IRemoteParticipant
{
    public int Attached, Connected, Disconnected;
    public void Attach(RpcPeer peer) => Interlocked.Increment(ref Attached);
    public Task OnConnectedAsync(RemoteSession session, CancellationToken ct) { Interlocked.Increment(ref Connected); return Task.CompletedTask; }
    public void OnDisconnected() => Interlocked.Increment(ref Disconnected);
}

public class ConnectionStateTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    private static (RemoteClient Client, FakeTransport Transport, ManualClock Clock, RecordingParticipant Participant, List<RemoteConnectionStatus> Log) Create(int attempts = 3)
    {
        var transport = new FakeTransport();
        var clock = new ManualClock();
        var client = new RemoteClient("fake-host", () => transport, new SshUriMapper(new RemoteAuthority("fake-host")), "test", new ReconnectPolicy(attempts, () => 0.5), clock);
        var participant = new RecordingParticipant();
        client.AddParticipant(participant);
        var log = new List<RemoteConnectionStatus>();
        client.StatusChanged.Subscribe(s => { lock (log) log.Add(s); });
        return (client, transport, clock, participant, log);
    }

    private static async Task WaitForStateAsync(RemoteClient c, RemoteConnectionState state) =>
        await TestEnv.WaitUntilAsync(() => c.Status.State == state, TimeSpan.FromSeconds(10), state.ToString());

    [Fact]
    public void Backoff_doubles_with_jitter_and_is_capped()
    {
        var p = new ReconnectPolicy(10, () => 0.5);
        Assert.Equal(TimeSpan.FromMilliseconds(500), p.Delay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), p.Delay(2));
        Assert.Equal(TimeSpan.FromSeconds(30), p.Delay(10));
        Assert.Null(p.Delay(11));
        Assert.Equal(TimeSpan.FromMilliseconds(400), new ReconnectPolicy(3, () => 0).Delay(1)); // -20 %
        Assert.Equal(TimeSpan.FromMilliseconds(600), new ReconnectPolicy(3, () => 1).Delay(1)); // +20 %
    }

    [Fact]
    public async Task A_dropped_connection_goes_through_reconnecting_back_to_connected()
    {
        var (client, transport, clock, participant, log) = Create();
        await using var _ = client;
        await client.ConnectAsync(Ct);
        Assert.Equal(RemoteConnectionState.Connected, client.Status.State);
        Assert.Equal(1, participant.Connected);

        transport.Refuse = true;
        transport.Drop();
        await WaitForStateAsync(client, RemoteConnectionState.Reconnecting);
        Assert.Equal(1, participant.Disconnected);
        Assert.Equal(1, client.Status.Attempt);
        await TestEnv.WaitUntilAsync(() => clock.PendingTimers > 0, TimeSpan.FromSeconds(5), "backoff timer");
        clock.Advance(TimeSpan.FromMilliseconds(500)); // attempt 1 fails (refused)
        await TestEnv.WaitUntilAsync(() => client.Status.Attempt == 2, TimeSpan.FromSeconds(5), "attempt 2");
        Assert.Equal(2, transport.Connects);

        transport.Refuse = false;
        await TestEnv.WaitUntilAsync(() => clock.PendingTimers > 0, TimeSpan.FromSeconds(5), "backoff timer 2");
        clock.Advance(TimeSpan.FromSeconds(1)); // attempt 2 succeeds
        await WaitForStateAsync(client, RemoteConnectionState.Connected);
        Assert.Equal(2, participant.Connected); // participants resynchronised on the new session
        List<RemoteConnectionState> states;
        lock (log) states = log.Select(x => x.State).ToList();
        var connected = states.IndexOf(RemoteConnectionState.Connected);
        var reconnecting = states.IndexOf(RemoteConnectionState.Reconnecting, connected);
        var back = states.IndexOf(RemoteConnectionState.Connected, reconnecting);
        Assert.True(states[0] == RemoteConnectionState.Connecting && connected > 0 && reconnecting > connected && back > reconnecting, string.Join(" → ", states));
    }

    [Fact]
    public async Task Spent_attempts_end_in_failed_and_a_disconnect_stops_a_waiting_reconnect()
    {
        var (client, transport, clock, _, _) = Create(attempts: 2);
        await using var _ = client;
        await client.ConnectAsync(Ct);
        transport.Refuse = true;
        transport.Drop();
        for (var i = 0; i < 2; i++)
        {
            await TestEnv.WaitUntilAsync(() => clock.PendingTimers > 0, TimeSpan.FromSeconds(5), "backoff timer");
            clock.Advance(TimeSpan.FromSeconds(30));
        }
        await WaitForStateAsync(client, RemoteConnectionState.Failed);
        Assert.Contains("reconnect attempts failed", client.Status.Message);
        Assert.Throws<RemoteDisconnectedException>(() => client.Require());

        // connect again explicitly, drop, and disconnect while the backoff waits: no further attempt is made
        transport.Refuse = false;
        await client.ConnectAsync(Ct);
        transport.Refuse = true;
        transport.Drop();
        await WaitForStateAsync(client, RemoteConnectionState.Reconnecting);
        var connects = transport.Connects;
        await client.DisconnectAsync();
        Assert.Equal(RemoteConnectionState.Disconnected, client.Status.State);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(200, Ct);
        Assert.Equal(connects, transport.Connects);
        Assert.Equal(RemoteConnectionState.Disconnected, client.Status.State);
    }

    [Theory]
    [InlineData("protocol-lab")]
    [InlineData("bundle-ci@checksum.example")]
    public async Task A_transient_start_failure_keeps_retrying_whatever_the_host_is_called(string host)
    {
        // remote-9: the transient "server did not start" message carries the host name (and ssh's stderr): words in it must not make it permanent
        var transport = new FakeTransport();
        var clock = new ManualClock();
        await using var client = new RemoteClient(host, () => transport, new SshUriMapper(RemoteAuthority.Parse(host)), "test", new ReconnectPolicy(4, () => 0.5), clock);
        await client.ConnectAsync(Ct);
        transport.Failure = new RemoteBootstrapException(
            $"The server did not start (ssh {host}, exit code 255): ssh: connect to host {host} port 22: Network is unreachable (protocol, bundle, checksum)");
        transport.Drop();
        await WaitForStateAsync(client, RemoteConnectionState.Reconnecting);
        await TestEnv.WaitUntilAsync(() => clock.PendingTimers > 0, TimeSpan.FromSeconds(5), "backoff timer");
        clock.Advance(TimeSpan.FromMilliseconds(500)); // attempt 1 fails with the transient error
        await TestEnv.WaitUntilAsync(() => client.Status.Attempt == 2 || client.Status.State == RemoteConnectionState.Failed, TimeSpan.FromSeconds(5), "attempt 2");
        Assert.Equal(RemoteConnectionState.Reconnecting, client.Status.State);
        Assert.Equal(2, client.Status.Attempt);

        // a failure flagged permanent where it was thrown (no bundle for the platform) ends the loop at once
        transport.Failure = new RemoteBootstrapException("No server bundle for FreeBSD amd64") { IsPermanent = true };
        await TestEnv.WaitUntilAsync(() => clock.PendingTimers > 0, TimeSpan.FromSeconds(5), "backoff timer 2");
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForStateAsync(client, RemoteConnectionState.Failed);
        Assert.Contains("FreeBSD", client.Status.Message);
        Assert.Equal(3, transport.Connects);
    }

    [Fact]
    public async Task A_failed_first_connect_reports_failed_and_rethrows()
    {
        var (client, transport, _, participant, _) = Create();
        await using var _ = client;
        transport.Refuse = true;
        await Assert.ThrowsAsync<IOException>(() => client.ConnectAsync(Ct));
        Assert.Equal(RemoteConnectionState.Failed, client.Status.State);
        Assert.Contains("refused", client.Status.Message);
        Assert.Equal(0, participant.Connected);
    }

    [Fact]
    public async Task Connect_disconnect_cycles_leave_nothing_behind()
    {
        var threadsBefore = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var (client, _, _, _, _) = Create();
            var sessions = new List<object>();
            for (var i = 0; i < 50; i++)
            {
                await client.ConnectAsync(Ct);
                sessions.Add(client.Session!);
                sessions.Add(client.Session!.Peer);
                await client.DisconnectAsync();
            }
            await client.DisposeAsync();
            return [.. sessions, client];
        });
        await Task.Delay(200, Ct);
        var threadsAfter = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
        Assert.True(threadsAfter - threadsBefore < 10, $"threads grew from {threadsBefore} to {threadsAfter}");
    }
}
