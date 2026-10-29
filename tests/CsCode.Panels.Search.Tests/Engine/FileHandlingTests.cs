using System.Text;
using CsCode.Core.Platform;
using CsCode.Panels.Search.Engine;
using Xunit;

namespace CsCode.Panels.Search.Tests.Engine;

public class FileHandlingTests : IDisposable
{
    private readonly SearchHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task A_file_with_a_nul_byte_in_the_first_8k_is_binary_and_skipped()
    {
        _h.WriteBytes("bin.dat", [.. "needle"u8.ToArray(), 0, 1, 2, .. "needle"u8.ToArray()]);
        _h.Write("ok.txt", "needle");
        var r = await _h.RunAsync(new SearchQuery("needle"));
        Assert.Equal(["ok.txt"], r.FileNames);
        Assert.Equal(1, r.Stats.SkippedBinary);
    }

    [Fact]
    public async Task A_nul_after_the_sniffed_prefix_does_not_make_a_text_file_binary()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('a', 9000) + "\0 needle");
        _h.WriteBytes("late-nul.txt", bytes);
        var r = await _h.RunAsync(new SearchQuery("needle"));
        Assert.Equal(["late-nul.txt"], r.FileNames);
    }

    [Fact]
    public async Task Utf8_bom_is_skipped_and_does_not_shift_columns()
    {
        _h.WriteBytes("bom.txt", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("hello needle")]);
        var r = await _h.RunAsync(new SearchQuery("needle"));
        var f = r.File("bom.txt");
        Assert.Equal(6, f.Matches[0].Column);
        Assert.Equal("utf-8-bom", f.Encoding);
    }

    [Fact]
    public async Task Utf16_little_endian_with_bom_is_decoded_not_treated_as_binary()
    {
        _h.WriteBytes("u16le.txt", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("line one\nthe needle\n")]);
        var r = await _h.RunAsync(new SearchQuery("needle"));
        var f = r.File("u16le.txt");
        Assert.Equal(new CsCode.Core.Text.TextPosition(1, 4), f.Matches.Single().Range.Start);
        Assert.Equal("utf-16le", f.Encoding);
        Assert.Equal(0, r.Stats.SkippedBinary);
    }

    [Fact]
    public async Task Utf16_big_endian_with_bom_is_decoded()
    {
        _h.WriteBytes("u16be.txt", [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes("héllo needle")]);
        var r = await _h.RunAsync(new SearchQuery("needle"));
        Assert.Equal("utf-16be", r.File("u16be.txt").Encoding);
        Assert.Equal(6, r.File("u16be.txt").Matches.Single().Column);
    }

    [Fact]
    public async Task Bytes_that_are_not_valid_utf8_are_searched_as_latin1()
    {
        _h.WriteBytes("latin.txt", [.. "caf"u8.ToArray(), 0xE9, .. " needle"u8.ToArray()]);
        var r = await _h.RunAsync(new SearchQuery("café"));
        Assert.Equal("latin1", r.File("latin.txt").Encoding);
    }

    [Fact]
    public async Task A_latin1_rescan_discards_the_matches_from_the_failed_utf8_attempt()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 100; i++) sb.Append("needle line\n");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString()).Concat(new byte[] { 0xE9, (byte)'\n' }).Concat("needle tail\n"u8.ToArray()).ToArray();
        _h.WriteBytes("mixed.txt", bytes);
        var r = await _h.RunAsync(new SearchQuery("needle"));
        Assert.Equal(101, r.MatchCount);              // no duplicates from the first pass
        Assert.Equal(101, r.Stats.MatchCount);        // and the global counter was given back
    }

    [Fact]
    public async Task Valid_multibyte_utf8_is_never_mistaken_for_latin1()
    {
        _h.Write("u8.txt", "naïve – “needle” ☃");
        var r = await _h.RunAsync(new SearchQuery("“needle”"));
        Assert.Equal("utf-8", r.File("u8.txt").Encoding);
    }

    [Fact]
    public async Task Files_over_the_size_limit_are_skipped_and_counted()
    {
        using var h = new SearchHarness(new SearchLimits { MaxFileSizeBytes = 100 });
        h.Write("big.txt", new string('x', 200) + " needle");
        h.Write("small.txt", "needle");
        var r = await h.RunAsync(new SearchQuery("needle"));
        Assert.Equal(["small.txt"], r.FileNames);
        Assert.Equal(1, r.Stats.SkippedTooLarge);
    }

    [Fact]
    public async Task The_size_limit_comes_from_the_search_maxFileSizeBytes_setting()
    {
        _h.Config.Set(SearchLimits.MaxFileSizeKey, 50);
        _h.Write("big.txt", new string('x', 80) + " needle");
        var r = await _h.RunAsync(new SearchQuery("needle"));
        Assert.Empty(r.Files);
        Assert.Equal(1, r.Stats.SkippedTooLarge);
    }

    [Fact]
    public async Task Empty_and_newline_only_files_are_fine()
    {
        _h.Write("empty.txt", "");
        _h.Write("nl.txt", "\n\n\n");
        var r = await _h.RunAsync(new SearchQuery("needle"));
        Assert.Empty(r.Files);
        Assert.Equal(SearchOutcome.Completed, r.Outcome);
    }

    [Fact]
    public async Task A_file_that_vanishes_between_listing_and_scanning_is_counted_unreadable_not_fatal()
    {
        _h.Write("real.txt", "needle");
        _h.Workspace.Files = [_h.UriOf("gone.txt"), _h.UriOf("real.txt")];
        var r = await _h.RunAsync(new SearchQuery("needle"));
        Assert.Equal(["real.txt"], r.FileNames);
        Assert.Equal(1, r.Stats.SkippedUnreadable);
        Assert.Equal(SearchOutcome.Completed, r.Outcome);
    }

    [Fact]
    public async Task An_unreadable_file_is_counted_and_the_rest_still_searched()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("chmod semantics"); return; }
        var path = _h.Write("secret.txt", "needle");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            try { using var probe = File.OpenRead(path); Assert.Skip("running with privileges that ignore file modes"); return; }
            catch (UnauthorizedAccessException) { }
            _h.Write("open.txt", "needle");
            var r = await _h.RunAsync(new SearchQuery("needle"));
            Assert.Equal(["open.txt"], r.FileNames);
            Assert.Equal(1, r.Stats.SkippedUnreadable);
        }
        finally { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    [Fact]
    public async Task A_file_being_written_while_searched_does_not_throw_a_sharing_violation()
    {
        var path = _h.Write("live.txt", "needle one\n");
        await using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var r = await _h.RunAsync(new SearchQuery("needle"));
        Assert.Equal(["live.txt"], r.FileNames);
    }

    [Fact]
    public async Task Non_file_uris_are_read_through_the_file_system_and_size_checked()
    {
        var mem = new MemoryFileSystem();
        mem.Files["mem:///proj/a.txt"] = Encoding.UTF8.GetBytes("a needle here\nand another needle");
        mem.Files["mem:///proj/huge.txt"] = new byte[10_000];
        var ws = new TestWorkspace(new Uri("mem:///proj/")) { FileSystem = mem };
        var source = new ListSource([new Uri("mem:///proj/a.txt"), new Uri("mem:///proj/huge.txt"), new Uri("mem:///proj/missing.txt")]);
        using var engine = new SearchEngine(ws, null, null, source, new SearchLimits { MaxFileSizeBytes = 1000 });
        var r = await SearchHarness.Collect(engine.Start(new SearchQuery("needle")));
        Assert.Equal(2, r.MatchCount);
        Assert.Equal(1, r.Stats.SkippedTooLarge);
        Assert.Equal(1, r.Stats.SkippedUnreadable);
    }

    [Fact]
    public async Task Uris_with_special_characters_round_trip()
    {
        _h.Write("sp ace/#hash%percent/ünï.txt", "needle");
        var r = await _h.RunAsync(new SearchQuery("needle"));
        Assert.True(File.Exists(r.Files.Single().Uri.LocalPath));
    }

    // ----------------------------------------------------------------------------------- test fakes

    private sealed class ListSource(IReadOnlyList<Uri> uris) : ISearchFileSource
    {
        public async IAsyncEnumerable<Uri> EnumerateAsync(FileSourceRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            foreach (var u in uris) yield return u;
        }
    }

    private sealed class MemoryFileSystem : IFileSystem
    {
        public Dictionary<string, byte[]> Files { get; } = new();

        public ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default) =>
            ValueTask.FromResult(Files.TryGetValue(uri.ToString(), out var b) ? new FileStat(FileType.File, b.Length, DateTimeOffset.UtcNow) : null);

        public ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default) => ValueTask.FromResult(Files[uri.ToString()]);
        public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
