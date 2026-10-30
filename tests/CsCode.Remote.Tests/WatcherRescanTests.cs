using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Remote.Client;
using CsCode.Remote.Protocol;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>
/// The remote watcher's rescan after a reconnect (remote-1) against an in-memory persistent server whose directory listings wait for the test: the
/// reconnect (and with it terminal output, debug streams and file operations) never waits for the rescan's listings, which follow once the session
/// is published.
/// </summary>
public class WatcherRescanTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    /// <summary>The watcher's view of one client: its session (waiting for a published one, as file operations do) and its mapper.</summary>
    private sealed class ClientEndpoint(RemoteClient client, SshUriMapper mapper) : IRemoteEndpoint
    {
        public (RemoteSession Session, SshUriMapper Mapper) Resolve(Uri uri) => (client.Require(), mapper);

        public async ValueTask<(RemoteSession Session, SshUriMapper Mapper)> ResolveAsync(Uri uri, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (client.Session is not { IsOpen: true })
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("no session");
                await Task.Delay(10, ct);
            }
            return (client.Session!, mapper);
        }

        public SshUriMapper? MapperFor(Uri uri) => mapper.Handles(uri) ? mapper : null;
        public DirectoryCache Directories { get; } = new();
    }

    [Fact]
    public async Task A_reconnect_does_not_wait_for_the_rescan_whose_listing_follows_once_the_session_is_published()
    {
        using var server = new InMemoryChannelServer();
        var listingRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); // result: published by then
        var listingAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteClient? client = null;
        server.Slot.OnRequest<WatchStartParams, Empty>(RemoteMethods.WatchStart, (_, _) => ValueTask.FromResult(Empty.Value));
        server.Slot.OnRequest<WatchKeepParams, Empty>(RemoteMethods.WatchKeep, (_, _) => ValueTask.FromResult(Empty.Value));
        server.Slot.OnRequest<PathParams, ReadDirResult>(RemoteMethods.FsReadDir, async (p, _) =>
        {
            listingRequested.TrySetResult(client!.Session is { IsOpen: true } && client.Status.State == RemoteConnectionState.Connected);
            await listingAnswer.Task; // a large directory over a slow link
            return new ReadDirResult([new DirectoryEntry("a.txt", FileType.File), new DirectoryEntry("b.txt", FileType.File)]);
        });

        var link = server.Transport();
        var mapper = new SshUriMapper(new RemoteAuthority("memory"));
        await using (client = new RemoteClient("memory", () => link, mapper, "test", new ReconnectPolicy(8, () => 0.2)))
        {
            var watcher = new RemoteWatcher(new ClientEndpoint(client, mapper));
            client.AddParticipant(watcher);
            await client.ConnectAsync(Ct);
            var dir = mapper.FromRemotePath("/w/");
            var delivered = new List<FileChange>();
            using var watch = (RemoteWatcher.Handle)watcher.Watch(dir, false, null, changes => { lock (delivered) delivered.AddRange(changes); });
            await watch.Ready.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            link.Refuse = true;
            server.Cut();
            await TestEnv.WaitUntilAsync(() => client.Session is null, TimeSpan.FromSeconds(10), "the client to notice the drop");
            link.Refuse = false;
            // The listing is not answered: the reconnect must complete regardless.
            await TestEnv.WaitUntilAsync(() => client.Session is { IsOpen: true } && client.Status.State == RemoteConnectionState.Connected,
                TimeSpan.FromSeconds(15), "the reconnect to complete while the rescan's listing is still unanswered");
            Assert.True(await listingRequested.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct), "the rescan's listing went out before the session was published");
            lock (delivered) Assert.Empty(delivered);

            listingAnswer.SetResult();
            await TestEnv.WaitUntilAsync(() => { lock (delivered) return delivered.Count == 3; }, TimeSpan.FromSeconds(10), "the rescan");
            lock (delivered)
            {
                Assert.All(delivered, c => Assert.Equal(FileChangeKind.Changed, c.Kind));
                Assert.Equal([dir, mapper.FromRemotePath("/w/a.txt"), mapper.FromRemotePath("/w/b.txt")], delivered.Select(c => c.Uri));
            }
        }
    }
}
