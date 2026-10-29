using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Tests.Harness;
using Xunit;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Tests;

/// <summary>publishDiagnostics + pull diagnostics into IDiagnosticService.</summary>
public class DiagnosticsTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static string U(ITextDocument d) => d.Uri.AbsoluteUri;

    private static string Publish(string uri, string diagnostics, int? version = null) =>
        "{\"uri\":\"" + uri + "\"" + (version is null ? "" : ",\"version\":" + version) + ",\"diagnostics\":[" + diagnostics + "]}";

    private const string One = "{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":5}},\"message\":\"msg\"}";

    [Fact]
    public async Task Pushed_diagnostics_are_mapped_with_severity_code_tags_related_info_and_description()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync();
        await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, Json(Publish(U(doc), """
            {"range":{"start":{"line":0,"character":1},"end":{"line":0,"character":4}},"message":"Cannot find name 'x'.","severity":1,"code":2304,"source":"ts",
             "codeDescription":{"href":"https://example.test/2304"},"tags":[1,2],
             "relatedInformation":[{"location":{"uri":"file:///x/other.fake","range":{"start":{"line":2,"character":0},"end":{"line":2,"character":3}}},"message":"declared here"}]},
            {"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":2}},"message":"hint","severity":4,"code":"STR"},
            {"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":2}},"message":"no severity means error"}
            """)));
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Count == 3, "3 diagnostics");
        var list = env.Diagnostics.Get(doc.Uri);
        var d = list[0];
        Assert.Equal(TextRange.FromLines(0, 1, 0, 4), d.Range);
        Assert.Equal("Cannot find name 'x'.", d.Message);
        Assert.Equal((DiagnosticSeverity.Error, "2304", "ts"), (d.Severity, d.Code, d.Source));
        Assert.Equal(new Uri("https://example.test/2304"), d.CodeDescriptionHref);
        Assert.Equal([DiagnosticTag.Unnecessary, DiagnosticTag.Deprecated], d.Tags!);
        var related = Assert.Single(d.RelatedInformation!);
        Assert.Equal("declared here", related.Message);
        Assert.Equal(new Uri("file:///x/other.fake"), related.Location.Uri);
        Assert.Equal((DiagnosticSeverity.Hint, "STR"), (list[1].Severity, list[1].Code));
        Assert.Equal(DiagnosticSeverity.Error, list[2].Severity);
        Assert.Equal((2, 0, 0, 1), env.Diagnostics.Counts); // error, hint, error (no severity defaults to error)
    }

    [Fact]
    public async Task Diagnostics_for_an_older_document_version_are_dropped()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync();
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "a")]);
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "b")]);
        Assert.True(doc.Version >= 3);

        await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, Json(Publish(U(doc), One, version: 1)));      // stale
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(env.Diagnostics.Get(doc.Uri));

        await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, Json(Publish(U(doc), One, version: doc.Version))); // current
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Count == 1, "current-version diagnostics accepted");
    }

    [Fact]
    public async Task Diagnostics_without_a_version_and_for_closed_files_are_accepted()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync();
        await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, Json(Publish(U(doc), One)));
        await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, Json(Publish("file:///not/open.fake", One, version: 1)));
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Count == 1 && env.Diagnostics.Get(new Uri("file:///not/open.fake")).Count == 1, "both accepted");
    }

    [Fact]
    public async Task An_empty_publish_clears_the_documents_diagnostics()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync();
        await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, Json(Publish(U(doc), One)));
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Count == 1, "set");
        await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, Json(Publish(U(doc), "")));
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Count == 0, "cleared");
    }

    [Fact]
    public async Task Closing_a_document_clears_its_diagnostics_for_push_only_servers()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync();
        var (extra, reg) = env.OpenWithToken("closing.fake", "abc");
        await server.WaitForAsync(P.LspMethods.TextDocumentDidOpen, m => m.P<P.DidOpenTextDocumentParams>().TextDocument.Uri == U(extra));
        await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, Json(Publish(U(extra), One)));
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(extra.Uri).Count == 1, "set");
        reg.Dispose();
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(extra.Uri).Count == 0, "cleared on close");
    }

    [Fact]
    public async Task Stopping_the_server_clears_everything_it_published_including_closed_files()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync();
        await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, Json(Publish(U(doc), One)));
        await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, Json(Publish("file:///workspace/wide.fake", One)));
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.GetAll().Count == 2, "two published");
        await env.Manager.StopAsync("fake", TestContext.Current.CancellationToken);
        Assert.Empty(env.Diagnostics.GetAll());
    }

    [Fact]
    public async Task A_crash_clears_diagnostics_too()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync();
        await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, Json(Publish(U(doc), One)));
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Count == 1, "set");
        server.Crash();
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Count == 0 || env.Connections.ConnectCount > 1, "cleared or restarted");
    }

    [Fact]
    public async Task A_flood_of_diagnostics_for_one_file_ends_with_the_last_value_and_stays_bounded()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync();
        for (var i = 0; i < 3000; i++)
            await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, Json(Publish(U(doc), "{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}},\"message\":\"m" + i + "\"}")));
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri) is [{ Message: "m2999" }], "last message wins");
    }

    [Fact]
    public async Task A_huge_diagnostic_list_is_capped_per_file()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync();
        var many = string.Join(',', Enumerable.Repeat(One, 5000));
        await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, Json(Publish(U(doc), many)));
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Count > 0, "published");
        Assert.Equal(2000, env.Diagnostics.Get(doc.Uri).Count);
    }

    [Fact]
    public async Task Pull_diagnostics_request_full_reports_then_use_previousResultId_and_related_documents()
    {
        await using var env = new LspTestEnv();
        var calls = new List<string?>();
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { TextDocumentSync = P.TextDocumentSyncKind.Incremental, DiagnosticProvider = new P.DiagnosticOptions { Identifier = "fake", InterFileDependencies = true } };
            s.Requests[P.LspMethods.TextDocumentDiagnostic] = p =>
            {
                var prev = p.TryGetProperty("previousResultId", out var r) ? r.GetString() : null;
                calls.Add(prev);
                return Task.FromResult<object?>(prev is null
                    ? Json("""{"kind":"full","resultId":"R1","items":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":2}},"message":"pulled"}],"relatedDocuments":{"file:///x/rel.fake":{"kind":"full","items":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"message":"related"}]}}}""")
                    : Json("""{"kind":"unchanged","resultId":"R1"}"""));
            };
        });
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Count == 1, "pulled diagnostics appear");
        Assert.Equal("pulled", env.Diagnostics.Get(doc.Uri)[0].Message);
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(new Uri("file:///x/rel.fake")).Count == 1, "related document diagnostics");
        Assert.Equal("fake", server.Of(P.LspMethods.TextDocumentDiagnostic).First().Params.GetProperty("identifier").GetString());

        // an edit schedules another pull (debounced), which now carries previousResultId and may answer "unchanged"
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "z")]);
        await TestHelpers.EventuallyAsync(() => calls.Count >= 2, "second pull after edit");
        Assert.Equal("R1", calls[1]);
        Assert.Single(env.Diagnostics.Get(doc.Uri)); // "unchanged" keeps what we had
    }

    [Fact]
    public async Task Pull_diagnostics_are_debounced_across_a_burst_of_edits()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { TextDocumentSync = P.TextDocumentSyncKind.Incremental, DiagnosticProvider = new P.DiagnosticOptions { InterFileDependencies = false, WorkspaceDiagnostics = false } };
            s.Requests[P.LspMethods.TextDocumentDiagnostic] = _ => Task.FromResult<object?>(Json("""{"kind":"full","items":[]}"""));
        });
        await TestHelpers.EventuallyAsync(() => server.CountOf(P.LspMethods.TextDocumentDiagnostic) >= 1, "initial pull");
        var before = server.CountOf(P.LspMethods.TextDocumentDiagnostic);
        for (var i = 0; i < 40; i++) { doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "x")]); await Task.Delay(5, TestContext.Current.CancellationToken); }
        await TestHelpers.EventuallyAsync(() => server.CountOf(P.LspMethods.TextDocumentDiagnostic) > before, "pull after burst");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.InRange(server.CountOf(P.LspMethods.TextDocumentDiagnostic) - before, 1, 4); // not 40
    }
}
