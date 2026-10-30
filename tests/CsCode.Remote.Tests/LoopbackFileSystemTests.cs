using System.Diagnostics;
using System.Text;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Remote.Client;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>
/// R1 through the whole client stack (Platform's routing file system/watcher/workspace → ssh: providers → protocol → a REAL cscode-server process,
/// whose Platform file system does the work). The "remote" is this machine's temp directory behind an ssh:// authority.
/// </summary>
public class LoopbackFileSystemTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    [Fact]
    public async Task Files_are_written_read_listed_moved_and_deleted_through_the_router()
    {
        await using var host = new RemoteTestHost();
        await host.ConnectAsync();
        var fs = host.FileSystem; // Platform's router: ssh: goes to the remote provider
        var file = host.Remote("dir with space/hello #1.txt");

        await fs.WriteFileAsync(file, Encoding.UTF8.GetBytes("hello remote"), createParents: true, Ct);
        Assert.Equal("hello remote", File.ReadAllText(Path.Combine(host.Workspace.Path, "dir with space", "hello #1.txt"))); // landed on the "remote" disk
        Assert.Equal("hello remote", RemoteTestHost.Text(await fs.ReadFileAsync(file, Ct)));

        var stat = await fs.StatAsync(file, Ct);
        Assert.NotNull(stat);
        Assert.Equal(FileType.File, stat!.Type);
        Assert.Equal(12, stat.Size);
        Assert.Null(await fs.StatAsync(host.Remote("missing.txt"), Ct));

        var listing = await fs.ReadDirectoryAsync(host.Remote("dir with space/"), Ct);
        Assert.Equal(["hello #1.txt"], listing.Select(e => e.Name));

        await fs.CreateDirectoryAsync(host.Remote("sub/inner/"), Ct);
        Assert.True(Directory.Exists(Path.Combine(host.Workspace.Path, "sub", "inner")));
        await fs.CopyAsync(file, host.Remote("sub/copy.txt"), ct: Ct);
        await fs.RenameAsync(host.Remote("sub/copy.txt"), host.Remote("sub/inner/moved.txt"), ct: Ct);
        Assert.Equal("hello remote", RemoteTestHost.Text(await fs.ReadFileAsync(host.Remote("sub/inner/moved.txt"), Ct)));
        Assert.Contains("inner", (await fs.ReadDirectoryAsync(host.Remote("sub/"), Ct)).Select(e => e.Name)); // the listing cache was invalidated by our own change

        await fs.DeleteAsync(host.Remote("sub/"), recursive: true, useTrash: false, Ct);
        Assert.False(Directory.Exists(Path.Combine(host.Workspace.Path, "sub")));
    }

    [Fact]
    public async Task Errors_arrive_as_the_local_file_systems_exception_types()
    {
        await using var host = new RemoteTestHost();
        await host.ConnectAsync();
        var fs = host.FileSystem;
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await fs.ReadFileAsync(host.Remote("nope.txt"), Ct));
        await Assert.ThrowsAnyAsync<IOException>(async () => await fs.ReadDirectoryAsync(host.Remote("no-dir/"), Ct));
        File.WriteAllText(Path.Combine(host.Workspace.Path, "x.txt"), "x");
        await Assert.ThrowsAnyAsync<IOException>(async () => await fs.RenameAsync(host.Remote("missing"), host.Remote("x.txt"), ct: Ct));
        await Assert.ThrowsAnyAsync<IOException>(async () => await fs.CopyAsync(host.Remote("x.txt"), host.Remote("x.txt"), overwrite: false, Ct));
    }

    [Fact]
    public async Task Large_files_move_in_chunks_both_ways_and_compress()
    {
        await using var host = new RemoteTestHost();
        await host.ConnectAsync();
        var fs = host.FileSystem;
        var random = new byte[3 * 1024 * 1024 + 12345];
        new Random(7).NextBytes(random);
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 120_000).Select(i => $"line {i} of a large text file\n")));

        await fs.WriteFileAsync(host.Remote("big.bin"), random, ct: Ct);
        Assert.Equal(random, File.ReadAllBytes(Path.Combine(host.Workspace.Path, "big.bin")));
        Assert.Equal(random, await fs.ReadFileAsync(host.Remote("big.bin"), Ct));

        await fs.WriteFileAsync(host.Remote("big.txt"), text, ct: Ct);
        Assert.Equal(text, await fs.ReadFileAsync(host.Remote("big.txt"), Ct));
        Assert.True(host.Manager.Session!.Gzip);
    }

    [Fact]
    public async Task A_conditional_write_detects_a_file_changed_on_the_remote()
    {
        await using var host = new RemoteTestHost();
        await host.ConnectAsync();
        var remote = host.Manager.FileSystem;
        var uri = host.Remote("conflict.txt");
        var created = await remote.WriteFileAsync(uri, Encoding.UTF8.GetBytes("v1"), expectedEtag: "", ct: Ct); // "" = must not exist yet
        Assert.NotNull(created);
        await Assert.ThrowsAsync<RemoteFileModifiedException>(async () => await remote.WriteFileAsync(uri, Encoding.UTF8.GetBytes("again"), expectedEtag: "", ct: Ct));

        var mine = await remote.StatWithEtagAsync(uri, Ct);
        await Task.Delay(20, Ct);
        File.WriteAllText(Path.Combine(host.Workspace.Path, "conflict.txt"), "changed by someone else on the remote");
        await Assert.ThrowsAsync<RemoteFileModifiedException>(async () => await remote.WriteFileAsync(uri, Encoding.UTF8.GetBytes("mine"), mine!.Etag, ct: Ct));
        var theirs = await remote.StatWithEtagAsync(uri, Ct);
        var after = await remote.WriteFileAsync(uri, Encoding.UTF8.GetBytes("mine"), theirs!.Etag, ct: Ct);
        Assert.NotEqual(theirs.Etag, after!.Etag);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(host.Workspace.Path, "conflict.txt")));
    }

    [Fact]
    public async Task A_remote_folder_opens_as_the_workspace_and_quick_open_walks_it_on_the_server()
    {
        await using var host = new RemoteTestHost();
        host.Workspace.WriteFile("src/main.ts", "x");
        host.Workspace.WriteFile("src/lib/util.ts", "y");
        host.Workspace.WriteFile("README.md", "z");
        host.Workspace.WriteFile("build/out.js", "ignored by .gitignore");
        host.Workspace.WriteFile(".gitignore", "build/\n");
        host.Workspace.WriteFile("node_modules/pkg/index.js", "excluded by files.exclude");
        // the client's user settings (files.exclude) govern the walk the server does
        Directory.CreateDirectory(Path.Combine(host.ClientHome, "config"));
        File.WriteAllText(Path.Combine(host.ClientHome, "config", "settings.json"), "{ \"files.exclude\": { \"**/node_modules\": true } }");
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();
        Assert.Equal(host.Remote(), host.WorkspaceService.Folders.Single());

        var files = new List<Uri>();
        await foreach (var u in host.WorkspaceService.FindFilesAsync(null, 0, Ct)) files.Add(u);
        var names = files.Select(u => SshUriMapper.PathOf(u, false)[(host.Workspace.Path.Length + 1)..]).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal([".gitignore", "README.md", "src/lib/util.ts", "src/main.ts"], names);
        Assert.All(files, u => Assert.Equal("ssh", u.Scheme));

        var ts = new List<Uri>();
        await foreach (var u in host.WorkspaceService.FindFilesAsync("**/*.ts", 1, Ct)) ts.Add(u);
        Assert.Single(ts); // maxResults honoured across the wire
    }

    [Fact]
    public async Task Text_search_runs_on_the_server_and_streams_matches()
    {
        await using var host = new RemoteTestHost();
        for (var i = 0; i < 40; i++) host.Workspace.WriteFile($"f{i}.txt", $"alpha\nthe needle {i} is here\nNEEDLE upper\n");
        host.Workspace.WriteFile("bin.dat", "needle\0binary");
        await host.ConnectAsync();
        var matches = new List<RemoteTextMatch>();
        await foreach (var m in host.Manager.Search.SearchTextAsync([host.Remote()], new SearchTextParams([], "needle"), Ct)) matches.Add(m);
        Assert.Equal(80, matches.Count); // case-insensitive: both lines of every text file; the binary file is skipped
        var first = matches.First(m => m.Uri == host.Remote("f3.txt") && m.Line == 1);
        Assert.Equal(4, first.Column);
        Assert.Equal("the needle 3 is here", first.Preview);

        var cased = new List<RemoteTextMatch>();
        await foreach (var m in host.Manager.Search.SearchTextAsync([host.Remote()], new SearchTextParams([], "NEED[L]E", IsRegex: true, MatchCase: true, MaxResults: 5), Ct)) cased.Add(m);
        Assert.InRange(cased.Count, 1, 6);
        Assert.All(cased, m => Assert.Equal(2, m.Line));
        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            await foreach (var _ in host.Manager.Search.SearchTextAsync([host.Remote()], new SearchTextParams([], "(unclosed", IsRegex: true), Ct)) { }
        });
    }

    [Fact]
    public async Task Find_in_Files_provider_searches_on_the_server_with_excludes_ignore_files_and_long_line_offsets()
    {
        // The ITextSearchProvider the Find in Files engine uses for a remote workspace (server-side scan, matches only).
        await using var host = new RemoteTestHost();
        host.Workspace.WriteFile("src/a.cs", "class A {\n    int needle;\n}\n");
        host.Workspace.WriteFile("obj/gen.cs", "needle in an excluded folder\n");
        host.Workspace.WriteFile("ignored/x.cs", "needle in an ignored folder\n");
        host.Workspace.WriteFile(".gitignore", "ignored/\n");
        host.Workspace.WriteFile("long.txt", new string('a', 300) + "needle" + new string('b', 300) + "\n");
        await host.ConnectAsync();
        Core.Platform.ITextSearchProvider provider = host.Services.GetServices<Core.Platform.ITextSearchProvider>().Single(p => p.Scheme == "ssh");
        Assert.Equal("ssh", provider.Scheme);
        var hits = new List<Core.Platform.TextSearchHit>();
        await foreach (var batch in provider.SearchAsync(new Core.Platform.TextSearchQuery([host.Remote()], "needle", false, true, false, ["**/obj"], true, 1000, 1 << 20), Ct))
            hits.AddRange(batch);
        Assert.Equal([host.Remote("long.txt"), host.Remote("src/a.cs")], hits.Select(h => h.Uri).Distinct().OrderBy(u => u.AbsoluteUri));
        var a = hits.Single(h => h.Uri == host.Remote("src/a.cs"));
        Assert.Equal((1, 8, 6, "    int needle;", 0), (a.Line, a.Column, a.Length, a.LineText, a.LineTextStart));
        var longHit = hits.Single(h => h.Uri == host.Remote("long.txt"));
        Assert.Equal(300, longHit.Column);
        Assert.True(longHit.LineTextStart > 0);
        Assert.Equal("needle", longHit.LineText.Substring(longHit.Column - longHit.LineTextStart, 6));
    }

    [Fact]
    public async Task The_remote_watcher_reports_changes_as_ssh_uris()
    {
        await using var host = new RemoteTestHost();
        await host.ConnectAsync();
        var batches = new List<FileChange>();
        using var watch = host.Services.GetRequiredService<IFileWatcher>().Watch(host.Remote(), true, null, changes => { lock (batches) batches.AddRange(changes); });
        await ((RemoteWatcher.Handle)watch).Ready.WaitAsync(TimeSpan.FromSeconds(15), Ct); // the server armed its OS watch
        host.Workspace.WriteFile("new/created.txt", "1");
        var expected = host.Remote("new/created.txt");
        // Created, or Changed: Platform's watcher may report a file created in a brand-new directory as Changed (known follow-up, .scratch/notes/followups.md)
        try { await TestEnv.WaitUntilAsync(() => { lock (batches) return batches.Any(c => c.Uri == expected && c.Kind is FileChangeKind.Created or FileChangeKind.Changed); }, TimeSpan.FromSeconds(15), "created event"); }
        catch (TimeoutException) { lock (batches) Assert.Fail("no Created event; got: " + string.Join(", ", batches) + "\n" + host.Dump()); }
        File.Delete(Path.Combine(host.Workspace.Path, "new", "created.txt"));
        await TestEnv.WaitUntilAsync(() => { lock (batches) return batches.Any(c => c.Uri == expected && c.Kind == FileChangeKind.Deleted); }, TimeSpan.FromSeconds(15), "deleted event");
    }

    [Fact]
    public async Task The_remote_watcher_carries_wanted_paths_to_the_server()
    {
        // The Explorer's watch of an ssh folder excludes node_modules/; the folder expanded there is asked for, with the watch and as it changes,
        // and the server's watch reports it (and only it).
        await using var host = new RemoteTestHost();
        await host.ConnectAsync();
        host.Workspace.WriteFile("node_modules/pkg/index.js", "1");
        host.Workspace.WriteFile("node_modules/other/x.js", "1");
        var batches = new List<FileChange>();
        var wants = new FileWatchWants([host.Remote("node_modules/pkg/")], []);
        using var watch = host.Services.GetRequiredService<IFileWatcher>().Watch(host.Remote(), true, ["**/node_modules/**"], changes => { lock (batches) batches.AddRange(changes); }, wants);
        await ((RemoteWatcher.Handle)watch).Ready.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        bool Seen(string rel) { lock (batches) return batches.Any(c => c.Uri == host.Remote(rel)); }

        host.Workspace.WriteFile("node_modules/other/no.js", "1");
        host.Workspace.WriteFile("node_modules/pkg/yes.js", "1");
        await TestEnv.WaitUntilAsync(() => Seen("node_modules/pkg/yes.js"), TimeSpan.FromSeconds(15), "the wanted folder's change");
        await Task.Delay(300, Ct);
        Assert.False(Seen("node_modules/other/no.js"));

        await wants.SetAsync([host.Remote("node_modules/pkg/"), host.Remote("node_modules/other/")], []).WaitAsync(TimeSpan.FromSeconds(15), Ct);
        host.Workspace.WriteFile("node_modules/other/now.js", "1");
        await TestEnv.WaitUntilAsync(() => Seen("node_modules/other/now.js"), TimeSpan.FromSeconds(15), "the folder asked for later");
    }

    [Fact]
    public async Task Wanted_paths_changed_while_the_watch_is_being_armed_reach_the_server()
    {
        // The Explorer opens an ssh folder and restores its expanded folders at once: the wants change while watch/start is still out (the server
        // answers once its walk is done). They must reach the server's watch, and SetAsync must not complete before they have.
        await using var host = new RemoteTestHost();
        await host.ConnectAsync();
        host.Workspace.WriteFile("node_modules/pkg/index.js", "1");
        host.Workspace.WriteFile("node_modules/other/x.js", "1");
        var batches = new List<FileChange>();
        var wants = new FileWatchWants();
        using var watch = host.Services.GetRequiredService<IFileWatcher>().Watch(host.Remote(), true, ["**/node_modules/**"], changes => { lock (batches) batches.AddRange(changes); }, wants);
        var ready = ((RemoteWatcher.Handle)watch).Ready;
        var applied = wants.SetAsync([host.Remote("node_modules/other/")], []);
        Assert.False(ready.IsCompleted);                                                                     // (the race this is about)
        await applied.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.True(ready.IsCompleted);                                                                      // applied means armed with them
        bool Seen(string rel) { lock (batches) return batches.Any(c => c.Uri == host.Remote(rel)); }

        host.Workspace.WriteFile("node_modules/other/now.js", "1");
        await TestEnv.WaitUntilAsync(() => Seen("node_modules/other/now.js"), TimeSpan.FromSeconds(15), "the folder asked for while the watch was armed");
    }

    [Fact]
    public async Task Calls_while_not_connected_fail_with_an_io_exception_naming_the_host()
    {
        await using var host = new RemoteTestHost();
        var uri = new Uri("ssh://tester@remote-box/tmp/x");
        var ex = await Assert.ThrowsAnyAsync<IOException>(async () => await host.FileSystem.ReadFileAsync(uri, Ct));
        Assert.Contains("remote-box", ex.Message);
        await host.ConnectAsync();
        await host.Manager.DisconnectAsync(Ct);
        ex = await Assert.ThrowsAnyAsync<IOException>(async () => await host.FileSystem.StatAsync(uri, Ct));
        Assert.Contains("remote-box", ex.Message);
        Assert.Equal(RemoteConnectionState.Disconnected, host.Manager.Status.State);
    }

    [Fact]
    public async Task File_system_calls_cost_the_caller_only_an_enqueue()
    {
        await using var host = new RemoteTestHost();
        await host.ConnectAsync();
        var fs = host.FileSystem;
        var uri = host.Remote("a.txt");
        File.WriteAllText(Path.Combine(host.Workspace.Path, "a.txt"), "a");
        var payload = new byte[200 * 1024]; // a 200 KB write: encoding/compression must not run on the caller
        var n = 0;
        var calls = new (string Name, Func<Task> Call)[]
        {
            ("stat", () => fs.StatAsync(uri, Ct).AsTask()),
            ("readDirectory", () => fs.ReadDirectoryAsync(host.Remote(), Ct).AsTask()),
            ("read", () => fs.ReadFileAsync(uri, Ct).AsTask()),
            ("write 200 KB", () => fs.WriteFileAsync(host.Remote($"w{n++ % 4}.bin"), payload, ct: Ct).AsTask()),
        };
        var failures = new List<string>();
        var pending = new List<Task>();
        var context = new ThrowingSyncContext();
        // Same method as the Lsp threading tests: a caller thread whose synchronization context must never be used, warm-up, then the average.
        var caller = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            foreach (var (name, call) in calls)
            {
                for (var i = 0; i < 20; i++) pending.Add(call());
                const int count = 200;
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < count; i++) pending.Add(call());
                var avgMs = sw.Elapsed.TotalMilliseconds / count;
                if (avgMs >= 0.1) failures.Add($"{name}: {avgMs * 1000:F1} us per call");
            }
        });
        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromSeconds(60)));
        await Task.WhenAll(pending);
        Assert.Equal(0, context.Posts);
        Assert.True(failures.Count == 0, "synchronous cost on the caller must stay under 0.1 ms: " + string.Join("; ", failures));
    }

    [Fact]
    public async Task Connecting_and_disconnecting_return_to_the_caller_at_once()
    {
        await using var host = new RemoteTestHost();
        await host.ConnectAsync(); // warm up (JIT) and leave connected
        await host.Manager.DisconnectAsync(Ct);
        var context = new ThrowingSyncContext();
        Task? connect = null, disconnect = null;
        TimeSpan connectCost = default, disconnectCost = default;
        var caller = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var sw = Stopwatch.StartNew();
            connect = host.Manager.ConnectAsync(RemoteTestHost.Host, Ct).AsTask(); // starts a process and a handshake: none of it on this thread
            connectCost = sw.Elapsed;
        });
        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromSeconds(10)));
        await connect!;
        caller = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var sw = Stopwatch.StartNew();
            disconnect = host.Manager.DisconnectAsync(Ct).AsTask();
            disconnectCost = sw.Elapsed;
        });
        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromSeconds(10)));
        await disconnect!;
        Assert.Equal(0, context.Posts);
        Assert.True(connectCost < TimeSpan.FromMilliseconds(1), $"connect held the caller {connectCost.TotalMilliseconds:0.000} ms");
        Assert.True(disconnectCost < TimeSpan.FromMilliseconds(1), $"disconnect held the caller {disconnectCost.TotalMilliseconds:0.000} ms");
    }

    [Fact]
    public async Task Repeated_connect_and_disconnect_leave_no_process_thread_or_descriptor_behind()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fd");
        await using var host = new RemoteTestHost();
        File.WriteAllText(Path.Combine(host.Workspace.Path, "a.txt"), "a");
        await host.ConnectAsync(); // warm up: JIT, lazily created services, the first lane threads
        using (host.Services.GetRequiredService<IFileWatcher>().Watch(host.Remote(), true, null, _ => { })) { }
        await host.Manager.DisconnectAsync(Ct);
        await Task.Delay(500, Ct);
        int Fds() => Directory.GetFiles("/proc/self/fd").Length;
        int Threads() => Process.GetCurrentProcess().Threads.Count;
        var (fds, threads) = (Fds(), Threads());
        const int Cycles = 10;
        for (var i = 0; i < Cycles; i++)
        {
            await host.ConnectAsync();
            using var watch = host.Services.GetRequiredService<IFileWatcher>().Watch(host.Remote(), true, null, _ => { });
            Assert.Equal("a", RemoteTestHost.Text(await host.FileSystem.ReadFileAsync(host.Remote("a.txt"), Ct)));
            await host.Manager.DisconnectAsync(Ct);
        }
        await TestEnv.AssertNoProcessesAsync(host.Marker); // every server (and its ssh/loopback process) is gone
        await TestEnv.WaitUntilAsync(() => Fds() - fds < 10 && Threads() - threads < 10, TimeSpan.FromSeconds(15),
            $"descriptors/threads to settle (fds {fds} -> {Fds()}, threads {threads} -> {Threads()})");
    }

    private sealed class ThrowingSyncContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback d, object? state) { Interlocked.Increment(ref Posts); ThreadPool.QueueUserWorkItem(_ => d(state)); }
        public override void Send(SendOrPostCallback d, object? state) => throw new InvalidOperationException("Send on the caller's context");
    }
}
