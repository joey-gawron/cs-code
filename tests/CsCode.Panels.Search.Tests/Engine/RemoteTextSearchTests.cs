using System.Runtime.CompilerServices;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Search.Tests.Engine;

/// <summary>
/// Find in Files over folders a text search provider serves (a remote workspace: the server scans, only matches come back): the engine reads no
/// file of such a folder, filters the matches with the same include/exclude rules and result cap, and searches dirty open documents from memory.
/// </summary>
public class RemoteTextSearchTests
{
    private static readonly Uri Root = new("ssh://me@box/home/me/proj/");

    private static Uri U(string relative) => new(Root, relative);

    /// <summary>Answers from a canned list of hits, in batches; records the queries it got.</summary>
    private sealed class FakeProvider(params TextSearchHit[] hits) : ITextSearchProvider
    {
        public List<TextSearchQuery> Queries { get; } = new();
        public string Scheme => "ssh";

        public async IAsyncEnumerable<IReadOnlyList<TextSearchHit>> SearchAsync(TextSearchQuery query, [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (Queries) Queries.Add(query);
            foreach (var chunk in hits.Chunk(2))
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                yield return chunk;
            }
        }
    }

    /// <summary>A file system that fails every read: the remote folder must never be scanned locally.</summary>
    private sealed class NoReads : IFileSystem
    {
        public int Reads;
        public ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default) { Interlocked.Increment(ref Reads); throw new IOException("no reads"); }
        public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) { Interlocked.Increment(ref Reads); throw new IOException("no reads"); }
        public ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default) { Interlocked.Increment(ref Reads); throw new IOException("no reads"); }
        public ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default) => throw new IOException("no writes");
        public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) => throw new IOException();
        public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) => throw new IOException();
        public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => throw new IOException();
        public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => throw new IOException();
    }

    private static (SearchEngine Engine, TestWorkspace Ws, NoReads Fs, DocumentManager Docs) Create(FakeProvider provider, TestConfigurationService? config = null, SearchLimits? limits = null)
    {
        var fs = new NoReads();
        var ws = new TestWorkspace(Root) { FileSystem = fs };
        var docs = new DocumentManager();
        return (new SearchEngine(ws, docs, config ?? new TestConfigurationService(), limits: limits, textSearch: [provider]), ws, fs, docs);
    }

    private static TextSearchHit Hit(string relative, int line, int col, int len, string text, int textStart = 0) => new(U(relative), line, col, len, text, textStart);

    [Fact]
    public async Task Matches_come_from_the_provider_filtered_like_a_local_search_and_no_remote_file_is_read()
    {
        var provider = new FakeProvider(
            Hit("src/a.cs", 3, 8, 6, "    var needle = 1;"),
            Hit("src/a.cs", 9, 0, 6, "needle()"),
            Hit("bin/out.cs", 0, 0, 6, "needle"),           // excluded by the query
            Hit("docs/readme.md", 1, 2, 6, "a needle"));     // not included
        var config = new TestConfigurationService();
        config.Set("files.exclude", new Dictionary<string, bool> { ["**/obj"] = true, ["**/tmp"] = false });
        var (engine, _, fs, _) = Create(provider, config);
        using var _engine = engine;

        var r = await SearchHarness.Collect(engine.Start(new SearchQuery("needle", MatchCase: true, Include: "src/**, bin/**", Exclude: "bin/**", Roots: [Root])));

        Assert.Equal(SearchOutcome.Completed, r.Outcome);
        var file = Assert.Single(r.Files);
        Assert.Equal(U("src/a.cs"), file.Uri);
        Assert.False(file.FromMemory);
        Assert.Equal([new TextPosition(3, 8), new TextPosition(9, 0)], file.Matches.Select(m => m.Range.Start));
        var first = file.Matches[0];
        Assert.Equal("var needle = 1;", first.Preview); // leading blanks trimmed, as a local match's preview
        Assert.Equal("needle", first.Preview.Substring(first.PreviewMatchStart, first.PreviewMatchLength));
        Assert.Equal("needle", first.MatchedText);
        Assert.Equal(0, fs.Reads);
        var q = Assert.Single(provider.Queries);
        Assert.Equal((Root, "needle", true), (q.Folders.Single(), q.Pattern, q.MatchCase));
        Assert.Contains("**/obj", q.ExcludeGlobs); // the server prunes the excluded folders
        Assert.DoesNotContain("**/tmp", q.ExcludeGlobs);
    }

    [Fact]
    public async Task A_dirty_open_document_is_searched_from_memory_and_its_stale_remote_matches_are_dropped()
    {
        var provider = new FakeProvider(Hit("a.txt", 0, 0, 6, "needle on the server's disk"), Hit("b.txt", 2, 1, 6, " needle"));
        var (engine, _, _, docs) = Create(provider);
        using var _engine = engine;
        using var reg = docs.Register(new TestEditorDocument(U("a.txt"), "edited: no match\nbut a needle here", dirty: true));

        var r = await SearchHarness.Collect(engine.Start(new SearchQuery("needle")));

        var a = r.Files.Single(f => f.Uri == U("a.txt"));
        Assert.True(a.FromMemory);
        Assert.Equal(new TextPosition(1, 6), a.Matches.Single().Range.Start);
        Assert.Equal(new TextPosition(2, 1), r.Files.Single(f => f.Uri == U("b.txt")).Matches.Single().Range.Start);
    }

    [Fact]
    public async Task The_result_cap_applies_to_remote_matches()
    {
        var hits = Enumerable.Range(0, 50).Select(i => Hit($"f{i}.txt", 0, 0, 1, "x")).ToArray();
        var (engine, _, _, _) = Create(new FakeProvider(hits), limits: new SearchLimits { MaxResults = 7 });
        using var _engine = engine;
        var r = await SearchHarness.Collect(engine.Start(new SearchQuery("x")));
        Assert.Equal(7, r.Files.Sum(f => f.Matches.Count));
        Assert.True(r.Stats.Truncated);
    }

    [Fact]
    public void A_long_lines_preview_keeps_the_match_visible_and_its_offsets_right()
    {
        var line = new string('a', 100) + "needle" + new string('b', 400);
        // the server sent the line from offset 60 (its own preview window)
        var m = SearchEngine.RemoteMatch(new TextSearchHit(U("x"), 4, 100, 6, line[60..310], 60), 300);
        Assert.Equal(new TextRange(new TextPosition(4, 100), new TextPosition(4, 106)), m.Range);
        Assert.Equal("needle", m.Preview.Substring(m.PreviewMatchStart, m.PreviewMatchLength));
        Assert.StartsWith("…", m.Preview);
        Assert.Equal("needle", m.MatchedText);
    }
}
