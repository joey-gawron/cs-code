using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using Xunit;

namespace CsCode.Panels.Search.Tests.Engine;

public class DocumentSearchTests : IDisposable
{
    private readonly SearchHarness _h = new();
    private readonly List<IDisposable> _regs = [];

    public void Dispose()
    {
        foreach (var r in _regs) r.Dispose();
        _h.Dispose();
    }

    private TestEditorDocument Open(Uri uri, string text, bool dirty)
    {
        var doc = new TestEditorDocument(uri, text, dirty);
        _regs.Add(_h.Documents.Register(doc));
        return doc;
    }

    [Fact]
    public async Task A_dirty_open_document_is_searched_from_memory_not_from_the_stale_disk_copy()
    {
        _h.Write("a.txt", "disk only\nold text");
        Open(_h.UriOf("a.txt"), "fresh needle in memory", dirty: true);
        var r = await _h.RunAsync(new SearchQuery("needle"));
        var f = r.File("a.txt");
        Assert.True(f.FromMemory);
        Assert.Equal(new TextPosition(0, 6), f.Matches.Single().Range.Start);
    }

    [Fact]
    public async Task Text_that_only_exists_on_disk_is_not_reported_while_the_document_is_dirty()
    {
        _h.Write("a.txt", "needle on disk");
        Open(_h.UriOf("a.txt"), "nothing to see", dirty: true);
        var r = await _h.RunAsync(new SearchQuery("needle"));
        Assert.Empty(r.Files);
    }

    [Fact]
    public async Task A_clean_open_document_is_read_from_disk()
    {
        _h.Write("a.txt", "needle");
        Open(_h.UriOf("a.txt"), "needle", dirty: false);
        var r = await _h.RunAsync(new SearchQuery("needle"));
        Assert.False(r.File("a.txt").FromMemory);
    }

    [Fact]
    public async Task The_searched_document_version_is_reported_for_replace_staleness_checks()
    {
        _h.Write("a.txt", "x");
        var doc = Open(_h.UriOf("a.txt"), "needle", dirty: true);
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "needle ")]);
        var r = await _h.RunAsync(new SearchQuery("needle"));
        Assert.Equal(doc.Version, r.File("a.txt").DocumentVersion);
    }

    [Fact]
    public async Task Dirty_documents_still_obey_include_and_exclude_globs()
    {
        _h.Write("a.cs", "x"); _h.Write("b.md", "x");
        Open(_h.UriOf("a.cs"), "needle", dirty: true);
        Open(_h.UriOf("b.md"), "needle", dirty: true);
        var r = await _h.RunAsync(new SearchQuery("needle", Include: "*.cs"));
        Assert.Equal(["a.cs"], r.FileNames);
    }

    [Fact]
    public async Task Open_editors_only_searches_just_the_open_documents()
    {
        _h.Write("open.txt", "needle"); _h.Write("closed.txt", "needle");
        Open(_h.UriOf("open.txt"), "needle", dirty: false);
        var r = await _h.RunAsync(new SearchQuery("needle", OpenEditorsOnly: true));
        Assert.Equal(["open.txt"], r.FileNames);
        Assert.Equal(0, _h.Workspace.FindFilesCalls); // the workspace is not even enumerated
    }

    [Fact]
    public async Task Untitled_documents_are_searched_in_open_editors_mode()
    {
        Open(new Uri("untitled:Untitled-1"), "scratch needle", dirty: true);
        var r = await _h.RunAsync(new SearchQuery("needle", OpenEditorsOnly: true));
        Assert.Equal("untitled:Untitled-1", r.Files.Single().Uri.ToString());
        Assert.True(r.Files.Single().FromMemory);
    }

    [Fact]
    public async Task Open_editors_only_respects_include_globs()
    {
        _h.Write("a.cs", "x"); _h.Write("b.md", "x");
        Open(_h.UriOf("a.cs"), "needle", dirty: false);
        Open(_h.UriOf("b.md"), "needle", dirty: false);
        _h.Write("a.cs", "needle"); _h.Write("b.md", "needle");
        var r = await _h.RunAsync(new SearchQuery("needle", OpenEditorsOnly: true, Include: "*.md"));
        Assert.Equal(["b.md"], r.FileNames);
    }

    [Fact]
    public async Task Search_in_selection_is_confined_to_the_range_and_keeps_document_coordinates()
    {
        var uri = _h.UriOf("s.txt");
        _h.Write("s.txt", "needle\nxx needle yy needle\nneedle");
        Open(uri, "needle\nxx needle yy needle\nneedle", dirty: false);
        // select "needle yy" on line 1 (columns 3..12)
        var r = await _h.RunAsync(new SearchQuery("needle", Selection: new SelectionScope(uri, TextRange.FromLines(1, 3, 1, 12))));
        var m = Assert.Single(r.Files.Single().Matches);
        Assert.Equal(new TextRange(new(1, 3), new(1, 9)), m.Range);
    }

    [Fact]
    public async Task Search_in_a_multi_line_selection_offsets_only_the_first_line()
    {
        var uri = _h.UriOf("s.txt");
        Open(uri, "aaa needle\nbbb needle\nccc needle", dirty: false);
        var r = await _h.RunAsync(new SearchQuery("needle", Selection: new SelectionScope(uri, TextRange.FromLines(0, 4, 1, 10))));
        Assert.Equal([new TextPosition(0, 4), new TextPosition(1, 4)], r.Files.Single().Matches.Select(m => m.Range.Start));
    }

    [Fact]
    public async Task Search_in_selection_of_an_unknown_document_finds_nothing()
    {
        var r = await _h.RunAsync(new SearchQuery("needle", Selection: new SelectionScope(_h.UriOf("nope.txt"), TextRange.FromLines(0, 0, 1, 0))));
        Assert.Empty(r.Files);
        Assert.Equal(SearchOutcome.Completed, r.Outcome);
    }
}
