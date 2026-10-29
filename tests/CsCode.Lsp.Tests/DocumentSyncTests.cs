using IHoverProvider = CsCode.Core.Languages.IHoverProvider;
using DocumentSaveReason = CsCode.Core.Languages.DocumentSaveReason;
using CsCode.Core.Text;
using CsCode.Lsp.Protocol;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using Xunit;
using TextEdit = CsCode.Core.Text.TextEdit;

namespace CsCode.Lsp.Tests;

/// <summary>didOpen / didChange / didSave / didClose against the fake server's text mirror, byte for byte.</summary>
public class DocumentSyncTests
{
    private static string UriOf(ITextDocument d) => d.Uri.AbsoluteUri;

    /// <summary>Occupies every lane thread until released so a burst of edits can be observed coalescing deterministically.</summary>
    private sealed class LaneBlock : IDisposable
    {
        private readonly ManualResetEventSlim _gate = new(false);
        public LaneBlock(LanguageWorkQueue q)
        {
            var entered = new CountdownEvent(q.ThreadCount);
            var ct = TestContext.Current.CancellationToken;
            for (var i = 0; i < q.ThreadCount; i++) q.Post(LanePriority.Interactive, () => { entered.Signal(); _gate.Wait(ct); }, exempt: true);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), ct));
        }
        public void Release() => _gate.Set();
        public void Dispose() => _gate.Set();
    }

    [Fact]
    public async Task Incremental_changes_reproduce_the_document_exactly_on_the_server()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(text: "alpha\nbeta\ngamma\n");
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(1, 4), "XYZ")]);
        doc.ApplyEdits([TextEdit.Delete(TextRange.FromLines(0, 0, 0, 2))]);
        doc.ApplyEdits([new TextEdit(TextRange.FromLines(2, 0, 3, 0), "replaced\nlines\n")]);
        await TestHelpers.EventuallyAsync(() => server.TextOf(UriOf(doc)) == doc.GetText(), "server mirror equals document");
        Assert.Equal(doc.Version, server.Documents[UriOf(doc)].Version);
    }

    [Fact]
    public async Task Random_edit_storm_keeps_server_mirror_identical_and_versions_monotonic()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(text: "line one\nline two\nline three\n");
        var rnd = new Random(12345);
        for (var i = 0; i < 400; i++)
        {
            var text = doc.GetText();
            var offset = rnd.Next(0, text.Length + 1);
            var p = doc.PositionAt(offset);
            var kind = rnd.Next(4);
            TextEdit edit = kind switch
            {
                0 => TextEdit.Insert(p, ((char)('a' + rnd.Next(26))).ToString()),
                1 => TextEdit.Insert(p, "new\nline"),
                2 when offset < text.Length => TextEdit.Delete(new TextRange(p, doc.PositionAt(Math.Min(text.Length, offset + rnd.Next(1, 6))))),
                _ => new TextEdit(new TextRange(p, doc.PositionAt(Math.Min(text.Length, offset + rnd.Next(0, 4)))), "é𝒳"), // non-BMP: surrogate pairs stay intact in UTF-16
            };
            doc.ApplyEdits([edit]);
            if (i % 37 == 0) await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        await TestHelpers.EventuallyAsync(() => server.TextOf(UriOf(doc)) == doc.GetText(), "mirror converges");
        var versions = server.Of(LspMethods.TextDocumentDidChange).Select(m => m.P<DidChangeTextDocumentParams>().TextDocument.Version).ToList();
        Assert.Equal(versions.OrderBy(v => v), versions);
        Assert.Equal(versions.Distinct().Count(), versions.Count); // strictly increasing
    }

    [Fact]
    public async Task A_burst_of_edits_coalesces_into_a_single_didChange_with_merged_typing()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(text: "");
        using var block = new LaneBlock(env.Manager.Queue);
        for (var i = 0; i < 300; i++) doc.ApplyEdits([TextEdit.Insert(doc.PositionAt(doc.Length), "x")]);
        block.Release();
        await TestHelpers.EventuallyAsync(() => server.TextOf(UriOf(doc)) == new string('x', 300), "burst delivered");
        var changes = server.Of(LspMethods.TextDocumentDidChange).ToList();
        Assert.Single(changes); // one message for the whole burst
        Assert.Single(changes[0].P<DidChangeTextDocumentParams>().ContentChanges); // and consecutive typing merged into one change
    }

    [Fact]
    public async Task Typing_a_newline_in_the_middle_of_a_burst_is_not_merged_incorrectly()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(text: "ab");
        using var block = new LaneBlock(env.Manager.Queue);
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 1), "1\n")]);   // a1|\nb  -> cursor at (1,0)
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(1, 0), "2")]);     // continues exactly where the previous insertion ended
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "<")]);     // elsewhere: must stay a separate change
        block.Release();
        await TestHelpers.EventuallyAsync(() => server.TextOf(UriOf(doc)) == doc.GetText(), "mirror matches");
        Assert.Equal("<a1\n2b", doc.GetText());
    }

    [Fact]
    public async Task Full_sync_servers_receive_the_whole_text()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s => s.Capabilities = new ServerCapabilities { TextDocumentSync = TextDocumentSyncKind.Full }, text: "abc");
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 3), "d")]);
        await TestHelpers.EventuallyAsync(() => server.Of(LspMethods.TextDocumentDidChange).Any(), "didChange");
        var change = server.Of(LspMethods.TextDocumentDidChange).First().P<DidChangeTextDocumentParams>().ContentChanges.Single();
        Assert.Null(change.Range);
        Assert.Equal("abcd", change.Text);
    }

    [Fact]
    public async Task Sync_kind_None_sends_open_but_no_changes()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s => s.Capabilities = new ServerCapabilities { TextDocumentSync = TextDocumentSyncKind.None });
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "q")]);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(0, server.CountOf(LspMethods.TextDocumentDidChange));
        Assert.Equal(1, server.CountOf(LspMethods.TextDocumentDidOpen));
    }

    [Fact]
    public async Task Detailed_sync_options_openClose_false_means_the_document_is_never_opened()
    {
        await using var env = new LspTestEnv();
        env.Connections.OnConnect = (s, _) => s.Capabilities = new ServerCapabilities
        {
            TextDocumentSync = new TextDocumentSyncOptions { OpenClose = false, Change = TextDocumentSyncKind.Incremental },
        };
        env.AddFake();
        env.Open("a.fake", "x");
        var server = await env.Connections.WaitForServerAsync();
        await server.WaitForAsync(LspMethods.Initialized);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(0, server.CountOf(LspMethods.TextDocumentDidOpen));
    }

    [Fact]
    public async Task Close_sends_didClose_and_forgets_the_document()
    {
        await using var env = new LspTestEnv();
        await env.StartAsync();
        var (doc, reg) = env.OpenWithToken("b.fake", "second");
        var server = env.Connections.Last;
        await server.WaitForAsync(LspMethods.TextDocumentDidOpen, m => m.P<DidOpenTextDocumentParams>().TextDocument.Uri == UriOf(doc));
        reg.Dispose();
        var close = await server.WaitForAsync(LspMethods.TextDocumentDidClose);
        Assert.Equal(UriOf(doc), close.P<DidCloseTextDocumentParams>().TextDocument.Uri);
        Assert.Equal(1, env.Manager.Slots.Single().DocumentCount);
    }

    [Fact]
    public async Task Edits_after_close_do_not_reach_the_server()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync();
        var (extra, reg) = env.OpenWithToken("c.fake", "abc");
        await server.WaitForAsync(LspMethods.TextDocumentDidOpen, m => m.P<DidOpenTextDocumentParams>().TextDocument.Uri == UriOf(extra));
        reg.Dispose();
        await server.WaitForAsync(LspMethods.TextDocumentDidClose);
        var before = server.CountOf(LspMethods.TextDocumentDidChange);
        extra.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "zzz")]);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(before, server.CountOf(LspMethods.TextDocumentDidChange));
    }

    [Fact]
    public async Task Language_id_change_closes_and_reopens_with_the_new_language()
    {
        await using var env = new LspTestEnv();
        env.Connections.OnConnect = (_, _) => { };
        env.AddFake(language: "fake");
        env.Manager.AddDefinition(LspTestEnv.FakeDef("other", "fake2")); // a different definition for the new language
        var doc = env.Open("a.fake", "text", "fake");
        var first = await env.Connections.WaitForServerAsync(0);
        await first.WaitForAsync(LspMethods.TextDocumentDidOpen);

        doc.SetLanguageId("fake2");
        var second = await env.Connections.WaitForServerAsync(1);
        await second.WaitForAsync(LspMethods.TextDocumentDidOpen);
        Assert.Equal("fake2", second.Of(LspMethods.TextDocumentDidOpen).Single().P<DidOpenTextDocumentParams>().TextDocument.LanguageId);
        await first.WaitForAsync(LspMethods.TextDocumentDidClose); // detached from the server that no longer matches
    }

    [Fact]
    public async Task Documents_larger_than_the_threshold_are_not_sent()
    {
        await using var env = new LspTestEnv(c => c.Set(LspSettingKeys.MaxDocumentBytes, 4096));
        env.AddFake();
        env.Open("small.fake", "tiny");
        var server = await env.Connections.WaitForServerAsync();
        await server.WaitForAsync(LspMethods.TextDocumentDidOpen);
        var big = env.Open("big.fake", new string('x', 10_000));
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.Single(server.Of(LspMethods.TextDocumentDidOpen));
        Assert.DoesNotContain(server.Of(LspMethods.TextDocumentDidOpen), m => m.P<DidOpenTextDocumentParams>().TextDocument.Uri == UriOf(big));
        Assert.Contains("maxDocumentBytes", env.Output.Text("Fake fake (LSP)"));
    }

    [Fact]
    public async Task Save_notifies_the_server_with_text_only_when_requested()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s => s.Capabilities = new ServerCapabilities
        {
            TextDocumentSync = new TextDocumentSyncOptions { OpenClose = true, Change = TextDocumentSyncKind.Incremental, Save = new SaveOptions { IncludeText = true } },
        }, text: "saved text");
        env.Documents.NotifySaved(doc);
        var save = await server.WaitForAsync(LspMethods.TextDocumentDidSave);
        var p = save.P<DidSaveTextDocumentParams>();
        Assert.Equal(UriOf(doc), p.TextDocument.Uri);
        Assert.Equal("saved text", p.Text);
    }

    [Fact]
    public async Task Save_without_text_option_sends_no_text_and_unadvertised_save_sends_nothing()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s => s.Capabilities = new ServerCapabilities
        {
            TextDocumentSync = new TextDocumentSyncOptions { OpenClose = true, Change = TextDocumentSyncKind.Incremental, Save = true },
        });
        env.Documents.NotifySaved(doc);
        var save = await server.WaitForAsync(LspMethods.TextDocumentDidSave);
        Assert.Null(save.P<DidSaveTextDocumentParams>().Text);

        await using var env2 = new LspTestEnv();
        var (server2, doc2) = await env2.StartAsync(s => s.Capabilities = new ServerCapabilities { TextDocumentSync = TextDocumentSyncKind.Incremental });
        env2.Documents.NotifySaved(doc2);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(0, server2.CountOf(LspMethods.TextDocumentDidSave));
    }

    [Fact]
    public async Task WillSaveWaitUntil_edits_are_returned_to_the_shell_and_willSave_is_sent()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new ServerCapabilities { TextDocumentSync = new TextDocumentSyncOptions { OpenClose = true, Change = TextDocumentSyncKind.Incremental, WillSave = true, WillSaveWaitUntil = true } };
            s.Requests[LspMethods.TextDocumentWillSaveWaitUntil] = _ => Task.FromResult<object?>(new[] { new Protocol.TextEdit { NewText = "// header\n", Range = new Protocol.Range { Start = new Position(), End = new Position() } } });
        });
        var edits = await env.Manager.OnWillSaveAsync(doc, DocumentSaveReason.Manual, TestContext.Current.CancellationToken);
        Assert.NotNull(edits);
        Assert.Equal("// header\n", edits![0].NewText);
        var will = server.Of(LspMethods.TextDocumentWillSave).Single().P<WillSaveTextDocumentParams>();
        Assert.Equal(TextDocumentSaveReason.Manual, will.Reason);
    }

    [Fact]
    public async Task WillSaveWaitUntil_never_blocks_saving_on_a_hung_server()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new ServerCapabilities { TextDocumentSync = new TextDocumentSyncOptions { OpenClose = true, Change = TextDocumentSyncKind.Incremental, WillSaveWaitUntil = true } };
            s.Requests[LspMethods.TextDocumentWillSaveWaitUntil] = async _ => { await Task.Delay(TimeSpan.FromSeconds(30)); return null; };
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var edits = await env.Manager.OnWillSaveAsync(doc, DocumentSaveReason.Manual, TestContext.Current.CancellationToken);
        Assert.Null(edits);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Edits_made_between_registering_and_didOpen_are_never_lost_or_duplicated()
    {
        for (var round = 0; round < 15; round++)
        {
            await using var env = new LspTestEnv();
            env.AddFake();
            var doc = env.Open("race.fake", "start");
            for (var i = 0; i < 20; i++) doc.ApplyEdits([TextEdit.Insert(doc.PositionAt(doc.Length), i.ToString())]); // racing the lane's didOpen
            var server = await env.Connections.WaitForServerAsync();
            await TestHelpers.EventuallyAsync(() => server.Documents.TryGetValue(UriOf(doc), out var d) && d.Text == doc.GetText(), $"round {round} converges: {(server.Documents.TryGetValue(UriOf(doc), out var x) ? x.Text : "<none>")} vs {doc.GetText()}");
        }
    }

    [Fact]
    public async Task Server_notified_in_order_open_then_changes_then_close()
    {
        await using var env = new LspTestEnv();
        var (doc, reg) = env.OpenWithToken("o.fake", "1");
        env.AddFake();
        await env.Manager.StartAsync("fake", TestContext.Current.CancellationToken);
        var server = await env.Connections.WaitForServerAsync();
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 1), "2")]);
        await TestHelpers.EventuallyAsync(() => server.CountOf(LspMethods.TextDocumentDidChange) >= 1, "change");
        reg.Dispose();
        await server.WaitForAsync(LspMethods.TextDocumentDidClose);
        var order = server.Received.Select(m => m.Method).Where(m => m.StartsWith("textDocument/did")).ToList();
        Assert.Equal([LspMethods.TextDocumentDidOpen, LspMethods.TextDocumentDidChange, LspMethods.TextDocumentDidClose], order);
    }

    [Fact]
    public async Task Request_after_edit_sees_the_edited_text_on_the_server()
    {
        await using var env = new LspTestEnv();
        string? textSeenByHover = null;
        FakeLspServer? srv = null;
        var (server, doc) = await env.StartAsync(s =>
        {
            srv = s;
            s.Capabilities = new ServerCapabilities { TextDocumentSync = TextDocumentSyncKind.Incremental, HoverProvider = true };
            s.Requests[LspMethods.TextDocumentHover] = p =>
            {
                textSeenByHover = srv!.Documents.Values.Single().Text; // what the server believes at the time it answers
                return Task.FromResult<object?>(new Hover { Contents = new MarkupContent { Kind = MarkupKind.PlainText, Value = "h" } });
            };
        }, text: "abc");
        var hover = env.Provider<IHoverProvider>(doc);
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 3), "def")]);
        var result = await hover.ProvideHoverAsync(doc, new TextPosition(0, 1), TestContext.Current.CancellationToken).Within();
        Assert.NotNull(result);
        Assert.Equal("abcdef", textSeenByHover);
    }
}
