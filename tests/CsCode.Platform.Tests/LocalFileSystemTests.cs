using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CsCode.Core.Platform;
using CsCode.Platform.FileSystem;
using CsCode.Platform.Io;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Platform.Tests;

public sealed class FsFixture : IDisposable
{
    public TempDirectory Temp { get; } = new("cscode-fs-");
    public TempDirectory TrashDir { get; } = new("cscode-trash-");
    public IoLane Lane { get; } = new("test.fs", 3);
    public FreedesktopTrash Trash { get; }
    public LocalFileSystem Fs { get; }

    public FsFixture()
    {
        Trash = new FreedesktopTrash(Path.Combine(TrashDir.Path, "Trash"));
        Fs = new LocalFileSystem(Lane, Trash);
    }

    public string P(string rel) => Temp.Combine(rel.Split('/'));
    public Uri U(string rel) => FileUri.FromPath(P(rel));

    public void Dispose() { Lane.Dispose(); Temp.Dispose(); TrashDir.Dispose(); }
}

public class LocalFileSystemTests : IDisposable
{
    private readonly FsFixture _f = new();
    public void Dispose() => _f.Dispose();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static bool IsRoot => Environment.UserName == "root";

    // Guarded wrappers: the tests using them skip on Windows, and the platform analyzer can see the guard here.
    private static void Chmod(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
        else throw new PlatformNotSupportedException();
    }

    private static UnixFileMode ModeOf(string path) =>
        !OperatingSystem.IsWindows() ? File.GetUnixFileMode(path) : throw new PlatformNotSupportedException();

    private static byte[] Bytes(int length, int seed = 1)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    // ---- stat --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stat_reports_files_directories_and_missing_paths()
    {
        _f.Temp.WriteFile("a.txt", "hello");
        Directory.CreateDirectory(_f.P("d"));

        var file = await _f.Fs.StatAsync(_f.U("a.txt"), Ct);
        Assert.Equal(FileType.File, file!.Type);
        Assert.Equal(5, file.Size);
        Assert.InRange((DateTimeOffset.UtcNow - file.ModifiedUtc).TotalSeconds, -5, 30);

        Assert.Equal(FileType.Directory, (await _f.Fs.StatAsync(_f.U("d"), Ct))!.Type);
        Assert.Null(await _f.Fs.StatAsync(_f.U("nope"), Ct));
        Assert.Null(await _f.Fs.StatAsync(_f.U("nope/deeper/still"), Ct));
    }

    [Fact]
    public async Task Stat_follows_symlinks_and_flags_them()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        _f.Temp.WriteFile("target.txt", "12345678");
        Directory.CreateDirectory(_f.P("dir"));
        File.CreateSymbolicLink(_f.P("link.txt"), _f.P("target.txt"));
        Directory.CreateSymbolicLink(_f.P("dirlink"), _f.P("dir"));
        File.CreateSymbolicLink(_f.P("dangling"), _f.P("missing"));

        var link = await _f.Fs.StatAsync(_f.U("link.txt"), Ct);
        Assert.Equal(FileType.File | FileType.SymbolicLink, link!.Type);
        Assert.Equal(8, link.Size); // the size of what it points to

        Assert.Equal(FileType.Directory | FileType.SymbolicLink, (await _f.Fs.StatAsync(_f.U("dirlink"), Ct))!.Type);

        var dangling = await _f.Fs.StatAsync(_f.U("dangling"), Ct);
        Assert.Equal(FileType.SymbolicLink, dangling!.Type); // exists, but there is nothing to follow
    }

    [Fact]
    public async Task Stat_reports_read_only_files()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || IsRoot, "permission bits are not enforced for root / differ on Windows");
        var path = _f.Temp.WriteFile("ro.txt", "x");
        Chmod(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        Assert.True((await _f.Fs.StatAsync(_f.U("ro.txt"), Ct))!.IsReadOnly);
        Chmod(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.False((await _f.Fs.StatAsync(_f.U("ro.txt"), Ct))!.IsReadOnly);
    }

    // ---- readdir -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadDirectory_lists_one_level_with_types_sorted_including_hidden_entries()
    {
        _f.Temp.WriteFile("b.txt", "");
        _f.Temp.WriteFile("a.txt", "");
        _f.Temp.WriteFile(".hidden", "");
        _f.Temp.WriteFile("sub/deep/x.txt", "");
        Directory.CreateDirectory(_f.P("empty"));

        var entries = await _f.Fs.ReadDirectoryAsync(_f.U(""), Ct);

        Assert.Equal([".hidden", "a.txt", "b.txt", "empty", "sub"], entries.Select(e => e.Name).ToArray());
        Assert.Equal(FileType.Directory, entries.Single(e => e.Name == "sub").Type);
        Assert.Equal(FileType.File, entries.Single(e => e.Name == ".hidden").Type);
        Assert.Empty(await _f.Fs.ReadDirectoryAsync(_f.U("empty"), Ct));
    }

    [Fact]
    public async Task ReadDirectory_marks_symlinks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        _f.Temp.WriteFile("t.txt", "");
        Directory.CreateDirectory(_f.P("d"));
        File.CreateSymbolicLink(_f.P("flink"), _f.P("t.txt"));
        Directory.CreateSymbolicLink(_f.P("dlink"), _f.P("d"));
        File.CreateSymbolicLink(_f.P("broken"), _f.P("nowhere"));

        var e = (await _f.Fs.ReadDirectoryAsync(_f.U(""), Ct)).ToDictionary(x => x.Name, x => x.Type);
        Assert.Equal(FileType.File | FileType.SymbolicLink, e["flink"]);
        Assert.Equal(FileType.Directory | FileType.SymbolicLink, e["dlink"]);
        Assert.Equal(FileType.SymbolicLink, e["broken"]);
        Assert.Equal(FileType.File, e["t.txt"]);
    }

    [Fact]
    public async Task ReadDirectory_of_a_missing_directory_fails_clearly()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await _f.Fs.ReadDirectoryAsync(_f.U("nope"), Ct));
    }

    // ---- read / write ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65_535)]
    [InlineData(65_536)]
    [InlineData(65_537)]
    [InlineData(1_000_000)]
    public async Task Write_then_read_round_trips_across_chunk_boundaries(int size)
    {
        var data = Bytes(size, size + 7);
        await _f.Fs.WriteFileAsync(_f.U("data.bin"), data, ct: Ct);
        Assert.Equal(data, await _f.Fs.ReadFileAsync(_f.U("data.bin"), Ct));
    }

    [Fact]
    public async Task Write_creates_missing_parents_unless_told_not_to()
    {
        await _f.Fs.WriteFileAsync(_f.U("a/b/c/file.txt"), "x"u8.ToArray(), createParents: true, Ct);
        Assert.Equal("x", File.ReadAllText(_f.P("a/b/c/file.txt")));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await _f.Fs.WriteFileAsync(_f.U("q/r/s.txt"), "x"u8.ToArray(), createParents: false, Ct));
        Assert.False(Directory.Exists(_f.P("q")));
    }

    [Fact]
    public async Task Overwrite_replaces_content_and_leaves_no_temp_files()
    {
        await _f.Fs.WriteFileAsync(_f.U("f.txt"), Bytes(1000, 1), ct: Ct);
        await _f.Fs.WriteFileAsync(_f.U("f.txt"), "new"u8.ToArray(), ct: Ct);
        Assert.Equal("new", File.ReadAllText(_f.P("f.txt")));
        Assert.Equal(["f.txt"], Directory.GetFileSystemEntries(_f.Temp.Path).Select(e => Path.GetFileName(e)!).ToArray());
    }

    [Fact]
    public async Task Write_preserves_the_permissions_of_an_existing_file()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "unix permissions");
        var path = _f.Temp.WriteFile("script.sh", "old");
        Chmod(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
        var before = ModeOf(path);
        await _f.Fs.WriteFileAsync(_f.U("script.sh"), "new"u8.ToArray(), ct: Ct);
        Assert.Equal(before, ModeOf(path));
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public async Task Write_refuses_a_read_only_file_and_leaves_it_alone()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || IsRoot, "permission bits are not enforced for root / differ on Windows");
        var path = _f.Temp.WriteFile("ro.txt", "precious");
        Chmod(path, UnixFileMode.UserRead);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await _f.Fs.WriteFileAsync(_f.U("ro.txt"), "x"u8.ToArray(), ct: Ct));
        Assert.Equal("precious", File.ReadAllText(path));
    }

    [Fact]
    public async Task Write_through_a_symlink_updates_the_target_and_keeps_the_link()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        var target = _f.Temp.WriteFile("real.txt", "old");
        File.CreateSymbolicLink(_f.P("alias.txt"), target);
        await _f.Fs.WriteFileAsync(_f.U("alias.txt"), "new"u8.ToArray(), ct: Ct);
        Assert.NotNull(new FileInfo(_f.P("alias.txt")).LinkTarget); // still a link
        Assert.Equal("new", File.ReadAllText(target));
    }

    [Fact]
    public async Task Writing_onto_a_directory_fails()
    {
        Directory.CreateDirectory(_f.P("d"));
        await Assert.ThrowsAsync<IOException>(async () => await _f.Fs.WriteFileAsync(_f.U("d"), "x"u8.ToArray(), ct: Ct));
    }

    [Fact]
    public async Task Reading_a_missing_file_or_a_directory_fails_clearly()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await _f.Fs.ReadFileAsync(_f.U("missing.txt"), Ct));
        Directory.CreateDirectory(_f.P("d"));
        await Assert.ThrowsAsync<IOException>(async () => await _f.Fs.ReadFileAsync(_f.U("d"), Ct));
    }

    [Fact]
    public async Task Paths_with_spaces_hashes_percents_and_unicode_work_end_to_end()
    {
        foreach (var name in new[] { "with space.txt", "hash#tag.txt", "100%.txt", "pct%20lit.txt", "q?mark.txt", "ünï 日本語.txt", "plus+amp&.txt" })
        {
            var uri = _f.U("odd dir/" + name);
            await _f.Fs.WriteFileAsync(uri, Encoding.UTF8.GetBytes(name), ct: Ct);
            Assert.Equal(name, Encoding.UTF8.GetString(await _f.Fs.ReadFileAsync(uri, Ct)));
            Assert.True(File.Exists(_f.P("odd dir/" + name)), name);
        }
        Assert.Equal(7, (await _f.Fs.ReadDirectoryAsync(_f.U("odd dir"), Ct)).Count);
    }

    // ---- atomicity ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failure_before_the_rename_leaves_the_original_intact_and_no_temp_file()
    {
        var path = _f.Temp.WriteFile("doc.txt", "ORIGINAL");
        _f.Fs.BeforeAtomicCommit = () => throw new IOException("simulated crash after the temp file was written");

        await Assert.ThrowsAsync<IOException>(async () => await _f.Fs.WriteFileAsync(_f.U("doc.txt"), Bytes(500_000), ct: Ct));

        Assert.Equal("ORIGINAL", File.ReadAllText(path));
        Assert.Equal(["doc.txt"], Directory.GetFileSystemEntries(_f.Temp.Path).Select(e => Path.GetFileName(e)!).ToArray());
    }

    [Fact]
    public async Task A_failure_while_creating_a_new_file_leaves_nothing_behind()
    {
        _f.Fs.BeforeAtomicCommit = () => throw new IOException("boom");
        await Assert.ThrowsAsync<IOException>(async () => await _f.Fs.WriteFileAsync(_f.U("new.txt"), "x"u8.ToArray(), ct: Ct));
        Assert.Empty(Directory.GetFileSystemEntries(_f.Temp.Path));
    }

    [Fact]
    public async Task Cancelling_mid_write_leaves_the_original_untouched()
    {
        var path = _f.Temp.WriteFile("doc.txt", "ORIGINAL");
        using var cts = new CancellationTokenSource();
        _f.Fs.ChunkHook = () => cts.Cancel(); // cancel after the first 64 KB chunk of a 1 MB write
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await _f.Fs.WriteFileAsync(_f.U("doc.txt"), Bytes(1_000_000), ct: cts.Token));
        Assert.Equal("ORIGINAL", File.ReadAllText(path));
        Assert.Single(Directory.GetFileSystemEntries(_f.Temp.Path));
    }

    [Fact]
    public async Task Concurrent_readers_only_ever_see_a_complete_old_or_complete_new_file()
    {
        var a = Enumerable.Repeat((byte)'A', 300_000).ToArray();
        var b = Enumerable.Repeat((byte)'B', 300_000).ToArray();
        await _f.Fs.WriteFileAsync(_f.U("shared.bin"), a, ct: Ct);

        using var cts = new CancellationTokenSource();
        var torn = 0;
        var reads = 0;
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var data = await _f.Fs.ReadFileAsync(_f.U("shared.bin"), Ct);
                Interlocked.Increment(ref reads);
                if (data.Length != 300_000 || !(data.All(x => x == 'A') || data.All(x => x == 'B'))) Interlocked.Increment(ref torn);
            }
        }, Ct)).ToArray();

        // Keep replacing the file until the readers have done a meaningful number of reads, so reads and renames genuinely overlap.
        var writes = 0;
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref reads) < 300 && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            await _f.Fs.WriteFileAsync(_f.U("shared.bin"), writes++ % 2 == 0 ? b : a, ct: Ct);
        }
        await cts.CancelAsync();
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(reads >= 300, $"readers made only {reads} reads");
        Assert.True(writes >= 5, $"only {writes} writes overlapped the reads");
        Assert.Equal(0, torn);
    }

    [Fact]
    public async Task Cancelling_mid_read_throws_OperationCanceled()
    {
        await _f.Fs.WriteFileAsync(_f.U("big.bin"), Bytes(2_000_000), ct: Ct);
        using var cts = new CancellationTokenSource();
        _f.Fs.ChunkHook = () => cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await _f.Fs.ReadFileAsync(_f.U("big.bin"), cts.Token));
    }

    // ---- mkdir -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateDirectory_makes_nested_directories_and_is_idempotent()
    {
        await _f.Fs.CreateDirectoryAsync(_f.U("a/b/c"), Ct);
        await _f.Fs.CreateDirectoryAsync(_f.U("a/b/c"), Ct);
        Assert.True(Directory.Exists(_f.P("a/b/c")));
    }

    [Fact]
    public async Task CreateDirectory_over_a_file_fails()
    {
        _f.Temp.WriteFile("f", "x");
        await Assert.ThrowsAsync<IOException>(async () => await _f.Fs.CreateDirectoryAsync(_f.U("f"), Ct));
    }

    // ---- delete & trash ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_removes_files_and_empty_directories_permanently()
    {
        _f.Temp.WriteFile("f.txt", "x");
        Directory.CreateDirectory(_f.P("empty"));
        await _f.Fs.DeleteAsync(_f.U("f.txt"), useTrash: false, ct: Ct);
        await _f.Fs.DeleteAsync(_f.U("empty"), useTrash: false, ct: Ct);
        Assert.False(File.Exists(_f.P("f.txt")));
        Assert.False(Directory.Exists(_f.P("empty")));
    }

    [Fact]
    public async Task Delete_of_a_non_empty_directory_needs_recursive()
    {
        _f.Temp.WriteFile("d/x/y.txt", "x");
        await Assert.ThrowsAsync<IOException>(async () => await _f.Fs.DeleteAsync(_f.U("d"), recursive: false, useTrash: false, ct: Ct));
        Assert.True(File.Exists(_f.P("d/x/y.txt")));
        await _f.Fs.DeleteAsync(_f.U("d"), recursive: true, useTrash: false, ct: Ct);
        Assert.False(Directory.Exists(_f.P("d")));
    }

    [Fact]
    public async Task Delete_of_a_missing_path_throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await _f.Fs.DeleteAsync(_f.U("ghost"), useTrash: false, ct: Ct));
    }

    [Fact]
    public async Task Deleting_a_symlink_never_touches_its_target()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        _f.Temp.WriteFile("outside/precious.txt", "keep");
        Directory.CreateDirectory(_f.P("inside"));
        Directory.CreateSymbolicLink(_f.P("inside/link"), _f.P("outside"));
        File.CreateSymbolicLink(_f.P("flink"), _f.P("outside/precious.txt"));

        await _f.Fs.DeleteAsync(_f.U("inside"), recursive: true, useTrash: false, ct: Ct);   // contains a dir symlink
        await _f.Fs.DeleteAsync(_f.U("flink"), useTrash: false, ct: Ct);

        Assert.False(Directory.Exists(_f.P("inside")));
        Assert.False(File.Exists(_f.P("flink")) || new FileInfo(_f.P("flink")).LinkTarget is not null);
        Assert.Equal("keep", File.ReadAllText(_f.P("outside/precious.txt")));
    }

    [Fact]
    public async Task Recursive_delete_survives_a_symlink_cycle()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        Directory.CreateDirectory(_f.P("loop/sub"));
        Directory.CreateSymbolicLink(_f.P("loop/sub/back"), _f.P("loop"));
        await _f.Fs.DeleteAsync(_f.U("loop"), recursive: true, useTrash: false, ct: Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(Directory.Exists(_f.P("loop")));
    }

    [Fact]
    public async Task Trash_moves_the_file_and_writes_a_spec_compliant_trashinfo()
    {
        var path = _f.Temp.WriteFile("doc with space.txt", "payload");
        await _f.Fs.DeleteAsync(_f.U("doc with space.txt"), useTrash: true, ct: Ct);

        Assert.False(File.Exists(path));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(_f.Trash.FilesDir, "doc with space.txt")));
        var info = File.ReadAllText(Path.Combine(_f.Trash.InfoDir, "doc with space.txt.trashinfo"));
        var lines = info.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("[Trash Info]", lines[0]);
        Assert.Equal("Path=" + _f.Temp.Path.Replace(" ", "%20") + "/doc%20with%20space.txt", lines[1]);
        Assert.Matches(new Regex(@"^DeletionDate=\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d$"), lines[2]);
    }

    [Fact]
    public async Task Trash_names_are_made_unique_and_every_item_is_restorable()
    {
        for (var i = 0; i < 3; i++)
        {
            _f.Temp.WriteFile("same.txt", "v" + i);
            await _f.Fs.DeleteAsync(_f.U("same.txt"), useTrash: true, ct: Ct);
        }
        var files = Directory.GetFiles(_f.Trash.FilesDir).Select(f => Path.GetFileName(f)!).Order().ToArray();
        Assert.Equal(["same.2.txt", "same.3.txt", "same.txt"], files);
        // one .trashinfo per trashed item
        Assert.Equal(["same.2.txt.trashinfo", "same.3.txt.trashinfo", "same.txt.trashinfo"], Directory.GetFiles(_f.Trash.InfoDir).Select(f => Path.GetFileName(f)!).Order().ToArray());
        Assert.Equal("v0", File.ReadAllText(Path.Combine(_f.Trash.FilesDir, "same.txt")));
    }

    [Fact]
    public async Task Trashing_a_directory_moves_the_whole_tree()
    {
        _f.Temp.WriteFile("proj/src/a.cs", "a");
        _f.Temp.WriteFile("proj/b.cs", "b");
        await _f.Fs.DeleteAsync(_f.U("proj"), recursive: true, useTrash: true, ct: Ct);
        Assert.False(Directory.Exists(_f.P("proj")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(_f.Trash.FilesDir, "proj", "src", "a.cs")));
        Assert.True(File.Exists(Path.Combine(_f.Trash.InfoDir, "proj.trashinfo")));
    }

    [Fact]
    public async Task Trashinfo_paths_are_percent_encoded_and_decode_back_to_the_original()
    {
        var name = "Ünï 100%#x?.txt";
        var path = _f.Temp.WriteFile(name, "x");
        await _f.Fs.DeleteAsync(_f.U(name), useTrash: true, ct: Ct);
        var pathLine = File.ReadAllLines(Path.Combine(_f.Trash.InfoDir, name + ".trashinfo"))[1];
        Assert.StartsWith("Path=/", pathLine);
        Assert.DoesNotContain(' ', pathLine);
        Assert.DoesNotContain('#', pathLine);
        Assert.Equal(path, Uri.UnescapeDataString(pathLine["Path=".Length..]));
    }

    [Fact]
    public async Task Trashing_a_symlink_trashes_the_link_and_not_its_target()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        var target = _f.Temp.WriteFile("real.txt", "keep");
        File.CreateSymbolicLink(_f.P("alias"), target);
        await _f.Fs.DeleteAsync(_f.U("alias"), useTrash: true, ct: Ct);
        Assert.Equal("keep", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(Path.Combine(_f.Trash.FilesDir, "alias")).LinkTarget);
    }

    [Fact]
    public async Task A_trash_that_cannot_be_used_fails_loudly_and_deletes_nothing()
    {
        // trash root is a FILE: the directories cannot be created
        var bad = _f.Temp.WriteFile("not-a-dir", "x");
        var fs = new LocalFileSystem(_f.Lane, new FreedesktopTrash(Path.Combine(bad, "Trash")));
        var victim = _f.Temp.WriteFile("victim.txt", "survive");
        await Assert.ThrowsAnyAsync<IOException>(async () => await fs.DeleteAsync(_f.U("victim.txt"), useTrash: true, ct: Ct));
        Assert.Equal("survive", File.ReadAllText(victim));
    }

    [Fact]
    public async Task A_failed_move_to_trash_removes_the_claimed_trashinfo_and_keeps_the_original()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || IsRoot, "directory permission bits are not enforced for root / differ on Windows");
        var victim = _f.Temp.WriteFile("v.txt", "survive");
        Directory.CreateDirectory(_f.Trash.FilesDir);
        Directory.CreateDirectory(_f.Trash.InfoDir);
        Chmod(_f.Trash.FilesDir, UnixFileMode.UserRead | UnixFileMode.UserExecute); // not writable: the move cannot land
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(async () => await _f.Fs.DeleteAsync(_f.U("v.txt"), useTrash: true, ct: Ct));
        }
        finally
        {
            Chmod(_f.Trash.FilesDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Assert.Empty(Directory.GetFiles(_f.Trash.InfoDir));      // the claimed name was released
        Assert.Equal("survive", File.ReadAllText(victim));      // and nothing was lost
    }

    // ---- rename ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rename_moves_files_and_directories()
    {
        _f.Temp.WriteFile("a.txt", "A");
        _f.Temp.WriteFile("d/inner.txt", "I");
        await _f.Fs.RenameAsync(_f.U("a.txt"), _f.U("b.txt"), ct: Ct);
        await _f.Fs.RenameAsync(_f.U("d"), _f.U("e"), ct: Ct);
        Assert.False(File.Exists(_f.P("a.txt")));
        Assert.Equal("A", File.ReadAllText(_f.P("b.txt")));
        Assert.Equal("I", File.ReadAllText(_f.P("e/inner.txt")));
    }

    [Fact]
    public async Task Rename_onto_an_existing_target_needs_overwrite()
    {
        _f.Temp.WriteFile("a.txt", "A");
        _f.Temp.WriteFile("b.txt", "B");
        await Assert.ThrowsAsync<IOException>(async () => await _f.Fs.RenameAsync(_f.U("a.txt"), _f.U("b.txt"), ct: Ct));
        Assert.Equal("B", File.ReadAllText(_f.P("b.txt")));
        Assert.True(File.Exists(_f.P("a.txt")));

        await _f.Fs.RenameAsync(_f.U("a.txt"), _f.U("b.txt"), overwrite: true, ct: Ct);
        Assert.Equal("A", File.ReadAllText(_f.P("b.txt")));
        Assert.False(File.Exists(_f.P("a.txt")));
    }

    [Fact]
    public async Task Rename_with_overwrite_replaces_a_directory()
    {
        _f.Temp.WriteFile("src/new.txt", "N");
        _f.Temp.WriteFile("dst/old.txt", "O");
        await _f.Fs.RenameAsync(_f.U("src"), _f.U("dst"), overwrite: true, ct: Ct);
        Assert.True(File.Exists(_f.P("dst/new.txt")));
        Assert.False(File.Exists(_f.P("dst/old.txt")));
    }

    [Fact]
    public async Task Rename_edge_cases_missing_source_itself_and_into_itself()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await _f.Fs.RenameAsync(_f.U("ghost"), _f.U("x"), ct: Ct));
        _f.Temp.WriteFile("a.txt", "A");
        await _f.Fs.RenameAsync(_f.U("a.txt"), _f.U("a.txt"), ct: Ct);   // no-op
        Assert.Equal("A", File.ReadAllText(_f.P("a.txt")));
        _f.Temp.WriteFile("d/x.txt", "x");
        await Assert.ThrowsAsync<IOException>(async () => await _f.Fs.RenameAsync(_f.U("d"), _f.U("d/sub"), ct: Ct));
        Assert.True(File.Exists(_f.P("d/x.txt")));
    }

    [Fact]
    public async Task A_case_only_rename_is_a_rename()
    {
        _f.Temp.WriteFile("readme.md", "R");
        await _f.Fs.RenameAsync(_f.U("readme.md"), _f.U("README.md"), ct: Ct);
        Assert.Equal(["README.md"], Directory.GetFiles(_f.Temp.Path).Select(f => Path.GetFileName(f)!).ToArray());
    }

    // files-1: on a case-insensitive volume (macOS by default) "foo" -> "Foo" compared as "a move into itself" and failed, after Explorer had
    // already closed the folder's tabs. The volume's answer is injected, so this runs (and failed before the fix) on Linux too.
    [Fact]
    public async Task A_case_only_rename_of_a_folder_on_a_case_insensitive_volume_is_a_rename()
    {
        _f.Fs.IsCaseSensitive = _ => false;
        _f.Temp.WriteFile("src/inner.txt", "I");
        await _f.Fs.RenameAsync(_f.U("src"), _f.U("Src"), ct: Ct);
        Assert.Equal(["Src"], Directory.GetDirectories(_f.Temp.Path).Select(d => Path.GetFileName(d)!).ToArray());
        Assert.Equal("I", File.ReadAllText(_f.P("Src/inner.txt")));
    }

    // files-8: a volume that keeps "a.txt" and "A.txt" apart (Linux, a case-sensitive APFS volume) must not lose "A.txt" to a case-only rename,
    // even when the volume is wrongly believed to be case-insensitive (what macOS assumed): the two entries are told apart by their inodes.
    [Fact]
    public async Task A_case_only_rename_onto_another_entry_is_a_collision_even_when_the_volume_is_believed_case_insensitive()
    {
        _f.Temp.WriteFile("a.txt", "lower");
        _f.Temp.WriteFile("A.txt", "UPPER");
        Assert.SkipWhen(File.ReadAllText(_f.P("a.txt")) != "lower", "this volume is case-insensitive: a.txt and A.txt are one file");
        _f.Fs.IsCaseSensitive = _ => false;

        await Assert.ThrowsAsync<IOException>(async () => await _f.Fs.RenameAsync(_f.U("a.txt"), _f.U("A.txt"), ct: Ct));
        Assert.Equal("lower", File.ReadAllText(_f.P("a.txt")));
        Assert.Equal("UPPER", File.ReadAllText(_f.P("A.txt")));
    }

    [Fact]
    public void The_volume_says_whether_names_differ_by_case()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows is case-insensitive by assumption");
        _f.Temp.WriteFile("probe.txt", "p");
        var distinct = !File.Exists(_f.P("PROBE.txt"));
        Assert.Equal(distinct, FileSystemNative.IsCaseSensitive(_f.P("probe.txt")));
        Assert.Equal(distinct, FileSystemNative.IsCaseSensitive(_f.P("not/yet/there.txt"))); // asks the nearest existing ancestor
    }

    // files-8: the watcher compared every macOS path case-insensitively, so on a case-sensitive APFS volume "Foo" and "foo" were one root.
    [Fact]
    public void Paths_differing_only_in_case_are_one_path_only_on_a_case_insensitive_volume()
    {
        var asked = new List<string>();
        var sensitive = new VolumePathComparer(p => { asked.Add(p); return true; });
        var insensitive = new VolumePathComparer(_ => false);

        Assert.False(sensitive.Equals("/v/Foo", "/v/foo"));
        Assert.True(insensitive.Equals("/v/Foo", "/v/foo"));
        Assert.True(sensitive.Equals("/v/foo", "/v/foo"));
        Assert.False(insensitive.Equals("/v/foo", "/v/bar"));
        Assert.False(insensitive.Equals("/v/foo", null));
        Assert.Equal(insensitive.GetHashCode("/v/Foo"), insensitive.GetHashCode("/v/foo"));
        Assert.Equal(["/v/Foo"], asked); // only a pair that differs in nothing but case costs a question to the volume
        Assert.Equal(2, new HashSet<string>(["/v/Foo", "/v/foo"], sensitive).Count);
        Assert.Single(new HashSet<string>(["/v/Foo", "/v/foo"], insensitive));
    }

    [Fact]
    public void Entries_are_the_same_only_when_device_and_inode_match()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "no lstat on Windows");
        var a = _f.Temp.WriteFile("d/a.txt", "a");
        var b = _f.Temp.WriteFile("d/b.txt", "b");
        File.CreateSymbolicLink(_f.P("d/link"), a);
        Assert.True(FileSystemNative.SameEntry(a, Path.Combine(_f.P("d"), "..", "d", "a.txt")));
        Assert.False(FileSystemNative.SameEntry(a, b));
        Assert.False(FileSystemNative.SameEntry(a, _f.P("d/link"))); // the link itself, never its target
        Assert.Null(FileSystemNative.SameEntry(a, _f.P("d/missing")));
    }

    [Fact]
    public async Task Rename_moves_a_symlink_itself()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        var target = _f.Temp.WriteFile("t.txt", "T");
        File.CreateSymbolicLink(_f.P("l1"), target);
        await _f.Fs.RenameAsync(_f.U("l1"), _f.U("l2"), ct: Ct);
        Assert.NotNull(new FileInfo(_f.P("l2")).LinkTarget);
        Assert.Equal("T", File.ReadAllText(target));
    }

    // ---- copy --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Copy_duplicates_files_and_directories_recursively()
    {
        _f.Temp.WriteFile("src/a.txt", "A");
        _f.Temp.WriteFile("src/sub/b.txt", "B");
        await _f.Fs.CopyAsync(_f.U("src"), _f.U("dst"), ct: Ct);
        Assert.Equal("A", File.ReadAllText(_f.P("dst/a.txt")));
        Assert.Equal("B", File.ReadAllText(_f.P("dst/sub/b.txt")));
        Assert.True(File.Exists(_f.P("src/a.txt"))); // source intact

        await _f.Fs.CopyAsync(_f.U("src/a.txt"), _f.U("copy.txt"), ct: Ct);
        Assert.Equal("A", File.ReadAllText(_f.P("copy.txt")));
    }

    [Fact]
    public async Task Copy_respects_the_overwrite_flag()
    {
        _f.Temp.WriteFile("a.txt", "NEW");
        _f.Temp.WriteFile("b.txt", "OLD");
        await Assert.ThrowsAsync<IOException>(async () => await _f.Fs.CopyAsync(_f.U("a.txt"), _f.U("b.txt"), ct: Ct));
        Assert.Equal("OLD", File.ReadAllText(_f.P("b.txt")));
        await _f.Fs.CopyAsync(_f.U("a.txt"), _f.U("b.txt"), overwrite: true, ct: Ct);
        Assert.Equal("NEW", File.ReadAllText(_f.P("b.txt")));

        _f.Temp.WriteFile("d1/x.txt", "1");
        Directory.CreateDirectory(_f.P("d2"));
        await Assert.ThrowsAsync<IOException>(async () => await _f.Fs.CopyAsync(_f.U("d1"), _f.U("d2"), ct: Ct));
        await _f.Fs.CopyAsync(_f.U("d1"), _f.U("d2"), overwrite: true, ct: Ct);
        Assert.True(File.Exists(_f.P("d2/x.txt")));
    }

    [Fact]
    public async Task Copying_a_directory_into_itself_is_refused()
    {
        _f.Temp.WriteFile("d/x.txt", "x");
        await Assert.ThrowsAsync<IOException>(async () => await _f.Fs.CopyAsync(_f.U("d"), _f.U("d/inner"), ct: Ct));
        await Assert.ThrowsAsync<IOException>(async () => await _f.Fs.CopyAsync(_f.U("d"), _f.U("d"), overwrite: true, ct: Ct));
        Assert.False(Directory.Exists(_f.P("d/inner")));
    }

    [Fact]
    public async Task Copy_recreates_symlinks_instead_of_following_them_so_cycles_terminate()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        _f.Temp.WriteFile("tree/a.txt", "A");
        Directory.CreateSymbolicLink(_f.P("tree/self"), _f.P("tree"));
        await _f.Fs.CopyAsync(_f.U("tree"), _f.U("clone"), ct: Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("A", File.ReadAllText(_f.P("clone/a.txt")));
        Assert.Equal(_f.P("tree"), new FileInfo(_f.P("clone/self")).LinkTarget);
    }

    [Fact]
    public async Task Copy_preserves_the_executable_bit_for_small_and_chunked_copies()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "unix permissions");
        foreach (var (name, size) in new[] { ("small", 100), ("big", 5_000_000) })
        {
            var src = _f.P(name + ".sh");
            File.WriteAllBytes(src, Bytes(size));
            Chmod(src, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await _f.Fs.CopyAsync(_f.U(name + ".sh"), _f.U(name + ".copy"), ct: Ct);
            Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(_f.P(name + ".copy")));
            Assert.True((ModeOf(_f.P(name + ".copy")) & UnixFileMode.UserExecute) != 0, name);
        }
    }

    [Fact]
    public async Task Cancelling_a_big_copy_leaves_no_partial_file()
    {
        File.WriteAllBytes(_f.P("big.bin"), Bytes(6_000_000));
        using var cts = new CancellationTokenSource();
        _f.Fs.CloneCopies = false; // macOS: the data is copied, as on a volume that cannot clone (a clone takes no time to cancel)
        _f.Fs.ChunkHook = () => cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await _f.Fs.CopyAsync(_f.U("big.bin"), _f.U("big.copy"), ct: cts.Token));
        Assert.False(File.Exists(_f.P("big.copy")));
        // The caller's task completes as soon as the token is cancelled; the lane thread removes the temp file right after (on macOS once
        // copyfile has returned), so it is given a moment.
        string[] Files() => Directory.GetFiles(_f.Temp.Path).Select(f => Path.GetFileName(f)!).ToArray();
        for (var wait = Stopwatch.StartNew(); Files().Length > 1 && wait.Elapsed < TimeSpan.FromSeconds(5);) await Task.Delay(10, Ct);
        Assert.Equal(["big.bin"], Files());
    }

    // gap-11: Finder tags, quarantine and other extended attributes survived a copy only for files up to 4 MiB; big files and folders lost them.
    [Fact]
    public async Task On_macos_a_copy_keeps_the_extended_attributes_of_big_files_and_of_folders()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS extended attributes");
        File.WriteAllBytes(_f.P("big.bin"), Bytes(5_000_000));
        Directory.CreateDirectory(_f.P("tree/sub"));
        foreach (var p in new[] { "big.bin", "tree", "tree/sub" }) MacXattr.Set(_f.P(p), "com.example.cscode", p);

        await _f.Fs.CopyAsync(_f.U("big.bin"), _f.U("big.copy"), ct: Ct);
        await _f.Fs.CopyAsync(_f.U("tree"), _f.U("tree copy"), ct: Ct);
        _f.Fs.CloneCopies = false; // as on a volume that cannot clone
        await _f.Fs.CopyAsync(_f.U("big.bin"), _f.U("big.data-copy"), ct: Ct);

        Assert.Equal(File.ReadAllBytes(_f.P("big.bin")), File.ReadAllBytes(_f.P("big.copy")));
        Assert.Equal(File.ReadAllBytes(_f.P("big.bin")), File.ReadAllBytes(_f.P("big.data-copy")));
        Assert.Equal("big.bin", MacXattr.Get(_f.P("big.copy"), "com.example.cscode"));
        Assert.Equal("big.bin", MacXattr.Get(_f.P("big.data-copy"), "com.example.cscode"));
        Assert.Equal("tree", MacXattr.Get(_f.P("tree copy"), "com.example.cscode"));
        Assert.Equal("tree/sub", MacXattr.Get(_f.P("tree copy/sub"), "com.example.cscode"));
    }

    private static class MacXattr
    {
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
        private static extern int SetXattr(string path, string name, byte[] value, nuint size, uint position, int options);

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
        private static extern nint GetXattr(string path, string name, byte[]? value, nuint size, uint position, int options);

        public static void Set(string path, string name, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Assert.Equal(0, SetXattr(path, name, bytes, (nuint)bytes.Length, 0, 0));
        }

        public static string? Get(string path, string name)
        {
            var buffer = new byte[256];
            var n = GetXattr(path, name, buffer, (nuint)buffer.Length, 0, 0);
            return n < 0 ? null : Encoding.UTF8.GetString(buffer, 0, (int)n);
        }
    }

    [Fact]
    public async Task Copying_a_missing_source_fails()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await _f.Fs.CopyAsync(_f.U("ghost"), _f.U("x"), ct: Ct));
    }

    // ---- schemes, threading, lifetime --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Other_uri_schemes_are_not_supported_by_any_operation()
    {
        var ssh = new Uri("ssh://host/etc/passwd");
        await Assert.ThrowsAsync<NotSupportedException>(async () => await _f.Fs.StatAsync(ssh, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await _f.Fs.ReadDirectoryAsync(ssh, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await _f.Fs.ReadFileAsync(ssh, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await _f.Fs.WriteFileAsync(ssh, new byte[1], ct: Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await _f.Fs.CreateDirectoryAsync(ssh, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await _f.Fs.DeleteAsync(ssh, ct: Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await _f.Fs.RenameAsync(ssh, _f.U("x"), ct: Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await _f.Fs.CopyAsync(_f.U("x"), ssh, ct: Ct));
    }

    [Fact]
    public async Task All_work_runs_on_the_io_lane_and_never_on_the_thread_pool()
    {
        var onLane = new List<(bool Lane, bool Pool)>();
        _f.Fs.BeforeAtomicCommit = () => { lock (onLane) onLane.Add((IoLane.IsLaneThread, Thread.CurrentThread.IsThreadPoolThread)); };
        _f.Fs.ChunkHook = () => { lock (onLane) onLane.Add((IoLane.IsLaneThread, Thread.CurrentThread.IsThreadPoolThread)); };
        await _f.Fs.WriteFileAsync(_f.U("x.bin"), Bytes(200_000), ct: Ct);
        await _f.Fs.ReadFileAsync(_f.U("x.bin"), Ct);
        Assert.NotEmpty(onLane);
        Assert.All(onLane, t => { Assert.True(t.Lane); Assert.False(t.Pool); });
    }

    [Fact]
    public async Task Calls_return_immediately_when_the_io_lane_is_saturated_and_complete_later()
    {
        using var lane = new IoLane("test.sat", 1);
        var fs = new LocalFileSystem(lane, _f.Trash);
        _f.Temp.WriteFile("f.txt", "x");
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var blocker = lane.RunAsync(_ => { started.Set(); gate.Wait(); }, Ct);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), Ct));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = new List<Task>
        {
            fs.StatAsync(_f.U("f.txt"), Ct).AsTask(),
            fs.ReadFileAsync(_f.U("f.txt"), Ct).AsTask(),
            fs.ReadDirectoryAsync(_f.U(""), Ct).AsTask(),
            fs.WriteFileAsync(_f.U("g.txt"), new byte[10], ct: Ct).AsTask(),
            fs.CopyAsync(_f.U("f.txt"), _f.U("h.txt"), ct: Ct).AsTask(),
            fs.DeleteAsync(_f.U("g.txt"), useTrash: false, ct: Ct).AsTask(),
        };
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(100), $"{tasks.Count} calls took {sw.Elapsed.TotalMilliseconds:F1} ms on the caller thread");
        Assert.All(tasks, t => Assert.False(t.IsCompleted));

        gate.Set();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await blocker;
    }

    [Fact]
    public async Task A_pre_cancelled_token_cancels_without_touching_the_disk()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await _f.Fs.WriteFileAsync(_f.U("never.txt"), new byte[1], ct: cts.Token));
        Assert.False(File.Exists(_f.P("never.txt")));
    }

    [Fact]
    public async Task Many_parallel_operations_complete_without_deadlock()
    {
        var tasks = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            var u = _f.U($"p/{i % 10}/f{i}.txt");
            await _f.Fs.WriteFileAsync(u, Encoding.UTF8.GetBytes(i.ToString()), ct: Ct);
            Assert.Equal(i.ToString(), Encoding.UTF8.GetString(await _f.Fs.ReadFileAsync(u, Ct)));
            await _f.Fs.StatAsync(u, Ct);
        }, Ct)).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60), Ct);
    }

    [Fact]
    public void The_file_system_object_is_collectable_after_use()
    {
        LeakAssert.NotRetained(() =>
        {
            var fs = new LocalFileSystem(_f.Lane, _f.Trash);
            fs.StatAsync(_f.U(""), Ct).AsTask().GetAwaiter().GetResult();
            return [fs];
        });
    }
}
