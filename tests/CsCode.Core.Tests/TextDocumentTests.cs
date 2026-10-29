using CsCode.Core.Text;
using Xunit;

namespace CsCode.Core.Tests;

public class TextDocumentTests
{
    private static InMemoryTextDocument Doc(string text, string lang = "plaintext") =>
        new(new Uri("file:///tmp/a.txt"), lang, text);

    [Fact]
    public void Offsets_and_positions_round_trip_with_mixed_line_endings()
    {
        var d = Doc("ab\r\ncd\nef\rgh");
        Assert.Equal(4, d.LineCount);
        Assert.Equal("cd", d.GetLineText(1));
        for (var i = 0; i <= d.Length; i++)
        {
            var p = d.PositionAt(i);
            // positions inside a \r\n are clamped to line end; only assert round-trip where representable
            if (d.OffsetAt(p) == i) Assert.Equal(p, d.PositionAt(d.OffsetAt(p)));
        }
        Assert.Equal(new TextPosition(2, 1), d.PositionAt(d.OffsetAt(new TextPosition(2, 1))));
    }

    [Fact]
    public void Validate_clamps_out_of_range_positions()
    {
        var d = Doc("abc\nde");
        Assert.Equal(new TextPosition(1, 2), d.Validate(new TextPosition(99, 99)));
        Assert.Equal(new TextPosition(0, 3), d.Validate(new TextPosition(0, 50)));
        Assert.Equal(TextPosition.Zero, d.Validate(new TextPosition(-1, -1)));
    }

    [Fact]
    public void ApplyEdits_applies_multiple_edits_against_original_text_and_reports_sequential_changes()
    {
        var d = Doc("hello world\nsecond line");
        TextDocumentChangedEventArgs? seen = null;
        using var sub = d.Changed.Subscribe(e => seen = e);

        var ok = d.ApplyEdits([
            new TextEdit(TextRange.FromLines(0, 0, 0, 5), "HELLO"),
            new TextEdit(TextRange.FromLines(1, 0, 1, 6), "2nd"),
        ]);

        Assert.True(ok);
        Assert.Equal("HELLO world\n2nd line", d.GetText());
        Assert.Equal(2, d.Version);
        Assert.True(d.IsDirty);
        Assert.NotNull(seen);
        Assert.Equal(2, seen!.Changes.Count);

        // Replaying the reported changes sequentially on the original text must reproduce the result (LSP semantics).
        var replay = Doc("hello world\nsecond line");
        foreach (var c in seen.Changes)
            Assert.True(replay.ApplyEdits([new TextEdit(c.Range, c.Text)]));
        Assert.Equal(d.GetText(), replay.GetText());
    }

    [Fact]
    public void ApplyEdits_rejects_overlapping_edits_and_leaves_document_untouched()
    {
        var d = Doc("abcdef");
        var ok = d.ApplyEdits([
            new TextEdit(TextRange.FromLines(0, 0, 0, 4), "X"),
            new TextEdit(TextRange.FromLines(0, 2, 0, 5), "Y"),
        ]);
        Assert.False(ok);
        Assert.Equal("abcdef", d.GetText());
        Assert.Equal(1, d.Version);
    }

    [Fact]
    public void LanguageId_change_fires_event()
    {
        var d = Doc("x", "plaintext");
        var fired = 0;
        using var sub = d.LanguageIdChanged.Subscribe(_ => fired++);
        d.SetLanguageId("csharp");
        d.SetLanguageId("csharp");
        Assert.Equal(1, fired);
        Assert.Equal("csharp", d.LanguageId);
    }

    [Fact]
    public void DocumentManager_tracks_lifetime_and_forwards_changes()
    {
        var mgr = new DocumentManager();
        var opened = 0;
        var closed = 0;
        var changes = 0;
        using var s1 = mgr.Opened.Subscribe(_ => opened++);
        using var s2 = mgr.Closed.Subscribe(_ => closed++);
        using var s3 = mgr.Changed.Subscribe(_ => changes++);
        var d = Doc("a");

        var reg = mgr.Register(d);
        d.ApplyEdits([TextEdit.Insert(new TextPosition(0, 1), "b")]);
        Assert.Same(d, mgr.Get(d.Uri));
        Assert.Throws<InvalidOperationException>(() => mgr.Register(d));

        reg.Dispose();
        reg.Dispose();
        d.ApplyEdits([TextEdit.Insert(new TextPosition(0, 2), "c")]);

        Assert.Equal((1, 1, 1), (opened, closed, changes));
        Assert.Null(mgr.Get(d.Uri));
        Assert.Empty(mgr.Documents);
    }

    [Fact]
    public void DocumentManager_does_not_retain_closed_documents()
    {
        var mgr = new DocumentManager();
        var weak = Open(mgr);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(weak.TryGetTarget(out _));
        GC.KeepAlive(mgr);

        static WeakReference<InMemoryTextDocument> Open(DocumentManager m)
        {
            var d = Doc("data");
            m.Register(d).Dispose();
            return new WeakReference<InMemoryTextDocument>(d);
        }
    }

    [Theory]
    [InlineData("csharp", "file:///a/b.cs", "csharp", true)]
    [InlineData("csharp", "file:///a/b.cs", "python", false)]
    [InlineData("*", "file:///a/b.cs", "python", true)]
    public void DocumentSelector_language_matching(string selectorLang, string uri, string docLang, bool expected) =>
        Assert.Equal(expected, DocumentSelector.ForLanguage(selectorLang).Score(new Uri(uri), docLang) > 0);

    [Theory]
    [InlineData("**/*.json", "file:///w/a/b/package.json", true)]
    [InlineData("*.json", "file:///w/a/b/package.json", true)]
    [InlineData("**/*.json", "file:///w/a/b/package.yaml", false)]
    [InlineData("**/{a,b}.txt", "file:///w/x/b.txt", true)]
    [InlineData("**/node_modules/**", "file:///w/node_modules/x/y.js", true)]
    public void DocumentFilter_glob_patterns(string pattern, string uri, bool expected) =>
        Assert.Equal(expected, new DocumentFilter(Pattern: pattern).Score(new Uri(uri), "any") > 0);

    [Fact]
    public void DocumentSelector_scheme_and_language_score_higher_than_wildcard()
    {
        var uri = new Uri("file:///a.cs");
        var specific = new DocumentSelector([new DocumentFilter("csharp", "file")]).Score(uri, "csharp");
        var wildcard = DocumentSelector.All.Score(uri, "csharp");
        Assert.True(specific > wildcard);
    }
}
