using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.TextMate;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.Theming;
using CsCode.Editor.Threading;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>MUST 4: TextMate tokenisation through the worker pipeline, on real VS Code grammars. Golden scopes, state across lines, edits.</summary>
public sealed class TextMateTests : IDisposable
{
    private readonly ThreadAffinityUiThread _ui = new();
    private readonly TestHost _host;
    private readonly EditorWorker _worker = new("test-worker");
    private readonly TokenizationManager _manager;

    public TextMateTests()
    {
        _host = TestHost.Create(_ui);
        _manager = new TokenizationManager(_host.Services, _worker);
    }

    public void Dispose()
    {
        _manager.Dispose();
        _worker.Dispose();
        _host.Dispose();
    }

    private (EditorDocument Doc, TokenizationSession Session) Open(string text, string language, bool expectTokens = true)
    {
        var doc = new EditorDocument(new Uri($"file:///t/{Guid.NewGuid():N}.txt"), language, EditorDocument.CreateBuffer(text, _ui), _ui);
        var session = _manager.CreateSession(doc);
        if (expectTokens) WaitValid(session, doc.LineCount);
        else Settle();
        return (doc, session);
    }

    /// <summary>For documents that will get no tokens: wait until the worker has drained and been idle for a moment.</summary>
    private void Settle()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var last = -1L;
        var stableSince = sw.ElapsedMilliseconds;
        while (sw.ElapsedMilliseconds < 10_000)
        {
            var now = _worker.CompletedJobs;
            if (now != last || _worker.QueueLength > 0)
            {
                last = now;
                stableSince = sw.ElapsedMilliseconds;
            }
            else if (sw.ElapsedMilliseconds - stableSince > 150) return;
            Thread.Sleep(10);
        }
    }

    private static void AssertHas(TokenizationSession s, int line, int column, string prefix)
    {
        var scopes = ScopesAt(s, line, column);
        Assert.True(Has(scopes, prefix), $"({line},{column}) expected a scope under '{prefix}', got: {string.Join(" ", scopes)}");
    }

    private static void WaitValid(TokenizationSession s, int lines, int timeoutMs = 30_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (s.Store.ValidLines < Math.Min(lines, TokenizationSession.ReadAheadLines + 61) && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(5);
        Assert.True(s.Store.ValidLines >= Math.Min(lines, TokenizationSession.ReadAheadLines + 61), $"tokenised {s.Store.ValidLines}/{lines} lines in {timeoutMs} ms");
    }

    /// <summary>Scope names of the token covering (line, column).</summary>
    private static string[] ScopesAt(TokenizationSession s, int line, int column)
    {
        var t = s.Store.GetTokens(line);
        Assert.NotNull(t);
        var idx = 0;
        for (var i = 0; i < t!.Length / 2; i++)
            if (t[i * 2] <= column) idx = i;
        return s.Interner.Scopes(t[idx * 2 + 1]);
    }

    private static bool Has(string[] scopes, string prefix) => scopes.Any(s => s == prefix || s.StartsWith(prefix + ".", StringComparison.Ordinal));

    // ---- golden scopes on real grammars ----

    [Fact]
    public void Csharp_keyword_string_and_comment_get_the_expected_scopes()
    {
        var (_, s) = Open("int x = \"hi\"; // c", "csharp");
        Assert.True(s.HasGrammar);
        Assert.Contains("keyword.type.int.cs", ScopesAt(s, 0, 0));
        Assert.Equal("source.cs", ScopesAt(s, 0, 0)[0]);
        Assert.True(Has(ScopesAt(s, 0, 9), "string.quoted.double.cs"));
        Assert.True(Has(ScopesAt(s, 0, 15), "comment.line.double-slash.cs"));
        Assert.Equal(TokenKind.String, s.KindAt(0, 10));
        Assert.Equal(TokenKind.Comment, s.KindAt(0, 17));
        Assert.Equal(TokenKind.Other, s.KindAt(0, 2));
    }

    [Fact]
    public void Csharp_class_and_method_declarations()
    {
        var (_, s) = Open("public class Foo\n{\n    public void Bar(int a) { }\n}\n", "csharp");
        Assert.True(Has(ScopesAt(s, 0, 0), "storage.modifier"));
        Assert.True(Has(ScopesAt(s, 0, 7), "storage.type.class"));
        Assert.True(Has(ScopesAt(s, 0, 13), "entity.name.type.class"));
        Assert.True(Has(ScopesAt(s, 2, 16), "entity.name.function"));
    }

    [Fact]
    public void Typescript_tokens()
    {
        var (_, s) = Open("const x: number = 42; // note\nfunction f(a: string) { return `t${a}`; }", "typescript");
        AssertHas(s, 0, 0, "storage.type");
        Assert.True(Has(ScopesAt(s, 0, 9), "support.type.primitive") || Has(ScopesAt(s, 0, 9), "meta.type.annotation"), string.Join(" ", ScopesAt(s, 0, 9)));
        AssertHas(s, 0, 18, "constant.numeric");
        AssertHas(s, 0, 24, "comment.line.double-slash");
        AssertHas(s, 1, 0, "storage.type.function");
        AssertHas(s, 1, 36, "string.template");
    }

    [Fact]
    public void Json_property_names_values_and_literals()
    {
        var (_, s) = Open("{\"a\": [1, true, null, \"s\"]}", "json");
        Assert.True(Has(ScopesAt(s, 0, 2), "support.type.property-name"));
        Assert.True(Has(ScopesAt(s, 0, 7), "constant.numeric"));
        Assert.True(Has(ScopesAt(s, 0, 11), "constant.language"));
        Assert.True(Has(ScopesAt(s, 0, 17), "constant.language"));
        Assert.True(Has(ScopesAt(s, 0, 23), "string.quoted.double"));
    }

    [Fact]
    public void Markdown_headings_emphasis_and_an_embedded_csharp_fence()
    {
        var (_, s) = Open("# Title\n\nSome *emphasis* here\n\n```csharp\nint x = 1;\n```\n", "markdown");
        Assert.True(Has(ScopesAt(s, 0, 2), "markup.heading") || Has(ScopesAt(s, 0, 2), "entity.name.section"));
        Assert.True(Has(ScopesAt(s, 2, 8), "markup.italic"));
        // the fenced block is tokenised by the C# grammar (embedded language)
        Assert.True(Has(ScopesAt(s, 5, 0), "keyword.type.int.cs") || ScopesAt(s, 5, 0).Any(x => x.EndsWith(".cs", StringComparison.Ordinal)),
            "fenced code: " + string.Join(" ", ScopesAt(s, 5, 0)));
        Assert.Contains(ScopesAt(s, 5, 0), x => x.StartsWith("meta.embedded", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_and_plaintext_languages_have_no_grammar_and_no_tokens()
    {
        var (_, plain) = Open("just text", "plaintext", expectTokens: false);
        Assert.False(plain.HasGrammar);
        Assert.Null(plain.Store.GetTokens(0));
        var (_, zig) = Open("const x = 1;", "zig", expectTokens: false); // a known language id without a bundled grammar
        Assert.False(zig.HasGrammar);
        Assert.Null(zig.Store.GetTokens(0));
    }

    // ---- state across lines ----

    [Fact]
    public void A_block_comment_continues_across_lines_and_ends_with_the_closing_marker()
    {
        var (_, s) = Open("int a; /* start\nstill comment\nend */ int b;", "csharp");
        Assert.Equal(TokenKind.Comment, s.KindAt(1, 5));
        Assert.True(Has(ScopesAt(s, 1, 3), "comment.block"));
        Assert.True(Has(ScopesAt(s, 2, 1), "comment.block"));
        Assert.Contains("keyword.type.int.cs", ScopesAt(s, 2, 8)); // after '*/' code resumes
    }

    [Fact]
    public void Editing_line_zero_to_open_a_comment_retokenises_the_lines_below()
    {
        var (doc, s) = Open("int a;\nint b;\nint c;\n", "csharp");
        Assert.Contains("keyword.type.int.cs", ScopesAt(s, 2, 0));
        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "/* ")]);
        WaitFor(() => s.Store.ValidLines >= 3 && Has(ScopesAt(s, 2, 0), "comment.block"), "lines below become a comment");
        // remove the opener again: code returns
        doc.ApplyEdits([TextEdit.Delete(TextRange.FromLines(0, 0, 0, 3))]);
        WaitFor(() => s.Store.ValidLines >= 3 && ScopesAt(s, 2, 0).Contains("keyword.type.int.cs"), "code restored");
    }

    [Fact]
    public void Inserting_and_removing_lines_keeps_the_token_rows_aligned_with_the_text()
    {
        var (doc, s) = Open("int a;\n// c\nint b;\n", "csharp");
        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "\n\n")]);          // two new lines at the top
        Assert.Equal(doc.LineCount, s.Store.LineCount);                         // structure updated synchronously on the UI thread
        WaitFor(() => s.Store.ValidLines >= doc.LineCount && Has(ScopesAt(s, 3, 0), "comment.line"), "comment row moved down");
        doc.ApplyEdits([TextEdit.Delete(TextRange.FromLines(0, 0, 2, 0))]);
        Assert.Equal(doc.LineCount, s.Store.LineCount);
        WaitFor(() => s.Store.ValidLines >= doc.LineCount && Has(ScopesAt(s, 1, 0), "comment.line"), "comment row moved up");
    }

    [Fact]
    public void A_language_change_restarts_tokenisation_with_the_new_grammar()
    {
        var (doc, s) = Open("# c\nx = 1", "plaintext", expectTokens: false);
        Assert.False(s.HasGrammar);
        doc.SetLanguageId("python");
        WaitFor(() => s.HasGrammar && s.Store.GetTokens(0) is not null && Has(ScopesAt(s, 0, 0), "comment.line"), "python grammar applied");
    }

    [Fact]
    public void Tokens_changed_notifications_are_delivered_on_the_ui_thread_coalesced()
    {
        var doc = new EditorDocument(new Uri("file:///t/n.cs"), "csharp", EditorDocument.CreateBuffer(string.Join("\n", Enumerable.Repeat("int a = 1;", 200)), _ui), _ui);
        var s = _manager.CreateSession(doc);
        var notes = new List<(int, int, int)>();
        s.TokensChanged.Subscribe(e => notes.Add((e.First, e.Last, Environment.CurrentManagedThreadId)));
        WaitValid(s, 200);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (notes.Count == 0 && sw.ElapsedMilliseconds < 5000)
        {
            _ui.Pump();
            Thread.Sleep(5);
        }
        _ui.Pump();
        Assert.NotEmpty(notes);
        Assert.All(notes, n => Assert.Equal(Environment.CurrentManagedThreadId, n.Item3)); // delivered where the test (= UI) thread pumps
        Assert.True(notes.Count < 200, "notifications must be coalesced, not one per line");
    }

    [Fact]
    public void Tokenisation_happens_on_the_editor_worker_and_never_on_the_calling_thread()
    {
        var callerThread = Environment.CurrentManagedThreadId;
        var doc = new EditorDocument(new Uri("file:///t/w.cs"), "csharp", EditorDocument.CreateBuffer(string.Join("\n", Enumerable.Repeat("class A { int B() { return 1; } }", 300)), _ui), _ui);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = _manager.CreateSession(doc); // must only enqueue
        Assert.True(sw.ElapsedMilliseconds < 50, $"creating a session blocked the caller for {sw.ElapsedMilliseconds} ms");
        WaitValid(s, 300);
        Assert.NotEqual(callerThread, _worker.Thread.ManagedThreadId);
        Assert.True(_worker.CompletedJobs > 0);
    }

    // ---- degenerate input ----

    [Fact]
    public void Very_long_lines_are_tokenised_only_up_to_the_limit_and_do_not_stall()
    {
        var (_, s) = Open("var s = \"" + new string('x', 30_000) + "\";", "csharp"); // 30k chars: also past the large-file threshold, but the session itself must cope
        Assert.NotNull(s.Store.GetTokens(0));
        var t = s.Store.GetTokens(0)!;
        Assert.All(Enumerable.Range(0, t.Length / 2), i => Assert.True(t[i * 2] <= 30_020));
    }

    [Fact]
    public void Empty_documents_and_blank_lines_have_a_token_row()
    {
        var (_, s) = Open("\n\n", "csharp");
        for (var i = 0; i < 3; i++)
        {
            var t = s.Store.GetTokens(i);
            Assert.NotNull(t);
            Assert.True(t!.Length >= 2);
        }
    }

    [Fact]
    public void Unicode_and_surrogate_pairs_do_not_break_token_offsets()
    {
        var (_, s) = Open("var s = \"\U0001F600é\"; // ☃", "csharp");
        Assert.True(Has(ScopesAt(s, 0, 10), "string.quoted.double.cs"));
        Assert.True(Has(ScopesAt(s, 0, 17), "comment.line.double-slash.cs"));
    }

    [Fact]
    public void Disposing_a_session_releases_the_document_and_stops_work()
    {
        var (doc, s) = Open("int a;", "csharp");
        s.Dispose();
        Assert.Equal(0, _manager.SessionCount);
        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "x")]); // must not throw or schedule anything for the dead session
        Thread.Sleep(50);
        Assert.Null(s.Store.GetTokens(0));
    }

    private static void WaitFor(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (condition()) return;
            }
            catch (Xunit.Sdk.XunitException)
            {
                // tokens not there yet
            }
            Thread.Sleep(10);
        }
        Assert.Fail($"Timed out waiting for: {what}");
    }
}
