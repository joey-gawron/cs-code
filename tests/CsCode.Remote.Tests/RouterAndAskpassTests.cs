using System.Text;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Platform;
using CsCode.Platform.FileSystem;
using CsCode.Platform.Io;
using CsCode.Platform.Watching;
using CsCode.Platform.Workspace;
using CsCode.Remote.Askpass;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>Platform's scheme routing (the additive hook remote editing plugs into).</summary>
public class RouterTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    /// <summary>An in-memory provider for a test scheme.</summary>
    private sealed class MemoryProvider(string scheme) : IFileSystemProvider, IFileWatcherProvider, IFileSearchProvider
    {
        public readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
        public readonly List<Uri> Watched = new();
        public string Scheme { get; } = scheme;
        public ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default) =>
            ValueTask.FromResult<FileStat?>(uri.AbsolutePath.EndsWith('/') ? new FileStat(FileType.Directory, 0, DateTimeOffset.UnixEpoch)
                : Files.TryGetValue(uri.AbsolutePath, out var b) ? new FileStat(FileType.File, b.Length, DateTimeOffset.UnixEpoch) : null);
        public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<DirectoryEntry>>(Files.Keys.Select(k => new DirectoryEntry(k.TrimStart('/'), FileType.File)).ToArray());
        public ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default) =>
            Files.TryGetValue(uri.AbsolutePath, out var b) ? ValueTask.FromResult(b) : ValueTask.FromException<byte[]>(new FileNotFoundException(uri.ToString()));
        public ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default) { Files[uri.AbsolutePath] = content.ToArray(); return ValueTask.CompletedTask; }
        public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) { Files.Remove(uri.AbsolutePath); return ValueTask.CompletedTask; }
        public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) { Files[target.AbsolutePath] = Files[source.AbsolutePath]; Files.Remove(source.AbsolutePath); return ValueTask.CompletedTask; }
        public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) { Files[target.AbsolutePath] = Files[source.AbsolutePath]; return ValueTask.CompletedTask; }
        public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges) { Watched.Add(root); return Disposable.Empty; }
        public async IAsyncEnumerable<Uri> FindFilesAsync(Uri folder, FileSearchOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var k in Files.Keys) { await Task.Yield(); yield return new Uri(folder, k.TrimStart('/')); }
        }
    }

    [Fact]
    public async Task Calls_go_to_the_provider_of_the_uris_scheme_and_providers_are_resolved_lazily()
    {
        using var lane = new IoLane("t", 1);
        var resolved = 0;
        var mem = new MemoryProvider("mem");
        var router = new FileSystemRouter(new LocalFileSystem(lane), () => { resolved++; return [mem]; });
        using var dir = new TempDirectory();
        var local = FileUri.FromPath(dir.WriteFile("a.txt", "local"));
        Assert.Equal("local", Encoding.UTF8.GetString(await router.ReadFileAsync(local, Ct)));
        Assert.Equal(0, resolved); // file: never resolves providers (a provider may depend on services that depend on the file system)

        await router.WriteFileAsync(new Uri("mem://x/b.txt"), Encoding.UTF8.GetBytes("remote"), ct: Ct);
        Assert.Equal(1, resolved);
        Assert.Equal("remote", Encoding.UTF8.GetString(mem.Files["/b.txt"]));
        Assert.True(router.Handles(new Uri("mem://x/b.txt")));
        Assert.False(router.Handles(new Uri("nope://x/b.txt")));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await router.StatAsync(new Uri("nope://x/y"), Ct)); // as the local file system always did

        // across schemes: a file is copied/moved by its bytes; a directory cannot cross
        await router.CopyAsync(new Uri("mem://x/b.txt"), FileUri.FromPath(dir.Combine("copied.txt")), ct: Ct);
        Assert.Equal("remote", File.ReadAllText(dir.Combine("copied.txt")));
        await router.RenameAsync(local, new Uri("mem://x/moved.txt"), ct: Ct);
        Assert.False(File.Exists(dir.Combine("a.txt")));
        Assert.Equal("local", Encoding.UTF8.GetString(mem.Files["/moved.txt"]));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await router.CopyAsync(FileUri.FromDirectoryPath(dir.Path), new Uri("mem://x/dir/"), ct: Ct));
        await Assert.ThrowsAnyAsync<IOException>(async () => await router.CopyAsync(new Uri("mem://x/moved.txt"), FileUri.FromPath(dir.Combine("copied.txt")), overwrite: false, Ct));
    }

    [Fact]
    public void Watches_route_by_scheme_and_unknown_schemes_get_an_inert_watch()
    {
        var mem = new MemoryProvider("mem");
        var local = new RecordingLocalWatcher();
        var router = new FileWatcherRouter(local, () => [mem]);
        using (router.Watch(new Uri("file:///tmp/"), true, null, _ => { })) { }
        using (router.Watch(new Uri("mem://x/root/"), true, null, _ => { })) { }
        using (router.Watch(new Uri("nope://x/root/"), true, null, _ => { })) { }
        Assert.Equal([new Uri("file:///tmp/")], local.Roots);
        Assert.Equal([new Uri("mem://x/root/")], mem.Watched);
    }

    private sealed class RecordingLocalWatcher : IFileWatcher
    {
        public readonly List<Uri> Roots = new();
        public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges) { Roots.Add(root); return Disposable.Empty; }
    }

    [Fact]
    public async Task Workspace_search_asks_the_provider_for_folders_of_its_scheme()
    {
        using var lane = new IoLane("t", 2);
        var mem = new MemoryProvider("mem");
        mem.Files["/one.ts"] = [];
        mem.Files["/two.ts"] = [];
        var config = new TestConfigurationService();
        var fs = new FileSystemRouter(new LocalFileSystem(lane), () => [mem]);
        using var ws = new WorkspaceService(fs, new FileWatcherRouter(new RecordingLocalWatcher(), () => [mem]), new NullState(), config, lane, null, new PlatformOptions(), null, () => [mem]);
        await ws.OpenFolderAsync(new Uri("mem://x/"), Ct);
        var found = new List<Uri>();
        await foreach (var u in ws.FindFilesAsync(null, 0, Ct)) found.Add(u);
        Assert.Equal(["mem://x/one.ts", "mem://x/two.ts"], found.Select(u => u.AbsoluteUri).Order(StringComparer.Ordinal));
        found.Clear();
        await foreach (var u in ws.FindFilesAsync(null, 1, Ct)) found.Add(u);
        Assert.Single(found); // maxResults spans providers
    }

    private sealed class NullState : IStateStorage
    {
        public T? Get<T>(string key, StorageScope scope = StorageScope.Application) => default;
        public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application) { }
        public void Remove(string key, StorageScope scope = StorageScope.Application) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}

public class DirectoryCacheTests
{
    [Fact]
    public void Listings_expire_are_invalidated_by_changes_are_bounded_and_trimmable()
    {
        var clock = new ManualClock();
        var cache = new CsCode.Remote.Client.DirectoryCache(TimeSpan.FromSeconds(3), clock);
        DirectoryEntry[] entries = [new("a", FileType.File)];
        cache.Put("/w/src/", entries);
        Assert.True(cache.TryGet("/w/src", out var hit)); // trailing slash does not matter
        Assert.Same(entries, hit);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(cache.TryGet("/w/src", out _)); // TTL

        cache.Put("/w", entries);
        cache.Put("/w/src", entries);
        cache.Put("/w/src/deep", entries);
        cache.InvalidatePath("/w/src/new.txt"); // a created/deleted entry changes its parent's listing
        Assert.False(cache.TryGet("/w/src", out _));
        Assert.True(cache.TryGet("/w", out _));
        cache.InvalidateTree("/w"); // a directory went away
        Assert.Equal(0, cache.Count);

        for (var i = 0; i < CsCode.Remote.Client.DirectoryCache.Capacity + 100; i++) cache.Put("/d" + i, entries);
        Assert.Equal(CsCode.Remote.Client.DirectoryCache.Capacity, cache.Count); // LRU-bounded
        Assert.True(cache.TryGet("/d" + (CsCode.Remote.Client.DirectoryCache.Capacity + 99), out _));
        Assert.False(cache.TryGet("/d0", out _));
        Assert.True(cache.EstimatedBytes > 0);
        Assert.True(cache.Trim(Core.Diagnostics.MemoryTrimLevel.Moderate) > 0);
        Assert.Equal(0, cache.Count);
    }
}

public class AskpassTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    [Fact]
    public async Task A_prompt_reaches_the_window_service_and_the_answer_comes_back_once_per_valid_token()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix sockets / askpass are Unix-only here"); return; }
        using var dir = new TempDirectory("cscode-askpass-test-");
        var window = new RecordingWindowService();
        window.InputResponses.Enqueue("s3cret; $(rm -rf ~)");
        using var server = AskpassServer.Start(window, ["/opt/cscode it's/cscode"], runtimeDir: dir.Path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(server.Directory)); // owner-only
        Assert.Contains("exec '/opt/cscode it'\\''s/cscode' --askpass \"$@\"", File.ReadAllText(server.ScriptPath)); // quoted program path
        var env = server.Environment;
        Assert.Equal("force", env["SSH_ASKPASS_REQUIRE"]);

        var stdout = new StringWriter();
        var code = await Task.Run(() => AskpassClient.Run("Enter passphrase for key '/k':", stdout, TextWriter.Null, n => env.TryGetValue(n, out var v) ? v : null), Ct);
        Assert.Equal(0, code);
        Assert.Equal("s3cret; $(rm -rf ~)\n", stdout.ToString());
        Assert.Equal(1, server.Prompts);

        // a wrong token is refused without asking anyone
        var forged = new Dictionary<string, string?>(env) { [AskpassServer.TokenVariable] = new string('0', 64) };
        Assert.Equal(1, await Task.Run(() => AskpassClient.Run("x", new StringWriter(), TextWriter.Null, n => forged.TryGetValue(n, out var v) ? v : null), Ct));
        Assert.Equal(1, server.Prompts);
        // cancelled by the user
        Assert.Equal(1, await Task.Run(() => AskpassClient.Run("Password:", new StringWriter(), TextWriter.Null, n => env.TryGetValue(n, out var v) ? v : null), Ct));

        server.Dispose();
        Assert.False(Directory.Exists(server.Directory)); // nothing outlives the attempt
        Assert.Equal(1, AskpassClient.Run("x", new StringWriter(), new StringWriter(), _ => null)); // not started by CsCode
    }

    [Theory]
    [InlineData("Are you sure you want to continue connecting (yes/no/[fingerprint])?", true, false)]
    [InlineData("Enter passphrase for key '/home/me/.ssh/id_ed25519':", false, true)]
    [InlineData("me@box's password:", false, true)]
    [InlineData("Verification code:", false, false)]
    [InlineData("Enter PIN for ECDSA-SK key:", false, true)]
    public void Prompts_are_classified(string prompt, bool confirmation, bool secret)
    {
        Assert.Equal(confirmation, AskpassServer.IsConfirmation(prompt));
        if (!confirmation) Assert.Equal(secret, AskpassServer.IsSecret(prompt));
    }
}
