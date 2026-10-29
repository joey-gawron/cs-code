using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Tests.Harness;
using Xunit;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Tests;

/// <summary>Position-encoding negotiation, retry/timeout semantics, tracing, workspace pull diagnostics, file notifications.</summary>
public class ProtocolBehaviourTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ------------------------------------------------------------------------------------------------------------
    // position encoding
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task By_default_only_utf16_is_offered_and_positions_are_passed_through_unchanged()
    {
        await using var env = new LspTestEnv();
        P.HoverParams? seen = null;
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { HoverProvider = true, PositionEncoding = P.PositionEncodingKind.UTF8 }; // a server picking utf-8 anyway is ignored: we never offered it
            s.Requests[P.LspMethods.TextDocumentHover] = p => { seen = JsonSerializer.Deserialize<P.HoverParams>(p, P.LspJson.Options); return Task.FromResult<object?>(null); };
        }, text: "a😀b = 1");
        Assert.Equal([P.PositionEncodingKind.UTF16], server.Of(P.LspMethods.Initialize).Single().P<P.InitializeParams>().Capabilities.General!.PositionEncodings!);
        await env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, new TextPosition(0, 3), Ct).Within();
        Assert.Equal(3, seen!.Position.Character); // UTF-16 column, untouched
    }

    [Fact]
    public async Task Auto_negotiation_offers_all_encodings_and_converts_positions_for_utf8_servers()
    {
        await using var env = new LspTestEnv(c => c.Set(LspSettingKeys.PositionEncoding, "auto"));
        P.HoverParams? seen = null;
        P.DefinitionParams? def = null;
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities
            {
                TextDocumentSync = P.TextDocumentSyncKind.Incremental, HoverProvider = true, DefinitionProvider = true, PositionEncoding = P.PositionEncodingKind.UTF8,
            };
            s.Requests[P.LspMethods.TextDocumentHover] = p =>
            {
                seen = JsonSerializer.Deserialize<P.HoverParams>(p, P.LspJson.Options);
                // the server answers in UTF-8 columns: "é" occupies 2 bytes, so `é = 1` ends at byte column 8 on the line "é = 1" + ... see below
                return Task.FromResult<object?>(new P.Hover { Contents = new P.MarkupContent { Kind = P.MarkupKind.PlainText, Value = "h" }, Range = new P.Range { Start = new P.Position { Line = 0, Character = 0 }, End = new P.Position { Line = 0, Character = 5 } } });
            };
            s.Requests[P.LspMethods.TextDocumentDefinition] = p => { def = JsonSerializer.Deserialize<P.DefinitionParams>(p, P.LspJson.Options); return Task.FromResult<object?>(null); };
        }, text: "é😀 = 1\nplain");

        var init = server.Of(P.LspMethods.Initialize).Single().P<P.InitializeParams>();
        Assert.Equal(["utf-8", "utf-16", "utf-32"], init.Capabilities.General!.PositionEncodings!.Select(e => e.Value));

        // UTF-16 columns: é=1 unit, 😀=2 units => column 3 is right after the emoji = byte offset 2 + 4 = 6
        var hover = await env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, new TextPosition(0, 3), Ct).Within();
        Assert.Equal(6, seen!.Position.Character);
        // the server's UTF-8 range [0,5) covers "é" (2 bytes) + part of the emoji; back in UTF-16 the end clamps to the emoji boundary logic
        Assert.Equal(0, hover!.Range!.Value.Start.Character);
        Assert.Equal(3, hover.Range.Value.End.Character); // byte 5 falls inside the 4-byte emoji: FromLsp advances to the end of the code point (col 3)

        await env.Provider<IDefinitionProvider>(doc).ProvideDefinitionAsync(doc, new TextPosition(1, 2), DefinitionKind.Definition, Ct).Within();
        Assert.Equal(2, def!.Position.Character); // ASCII line: encodings coincide
    }

    [Fact]
    public async Task Non_utf16_servers_get_full_text_syncs_because_incremental_ranges_cannot_be_converted_after_the_fact()
    {
        await using var env = new LspTestEnv(c => c.Set(LspSettingKeys.PositionEncoding, "auto"));
        var (server, doc) = await env.StartAsync(s => s.Capabilities = new P.ServerCapabilities
        {
            TextDocumentSync = P.TextDocumentSyncKind.Incremental, PositionEncoding = P.PositionEncodingKind.UTF8,
        }, text: "héllo");
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 5), "!")]);
        await TestHelpers.EventuallyAsync(() => server.CountOf(P.LspMethods.TextDocumentDidChange) >= 1, "didChange");
        var change = server.Of(P.LspMethods.TextDocumentDidChange).First().P<P.DidChangeTextDocumentParams>().ContentChanges.Single();
        Assert.Null(change.Range);
        Assert.Equal("héllo!", change.Text);
    }

    // ------------------------------------------------------------------------------------------------------------
    // ContentModified retry, tracing
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ContentModified_is_retried_for_background_features_but_not_for_interactive_ones()
    {
        await using var env = new LspTestEnv();
        var tokenCalls = 0;
        var hoverCalls = 0;
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities
            {
                HoverProvider = true,
                SemanticTokensProvider = new P.SemanticTokensOptions { Legend = new P.SemanticTokensLegend(), Full = true },
            };
            s.Requests[P.LspMethods.TextDocumentSemanticTokensFull] = _ =>
            {
                if (Interlocked.Increment(ref tokenCalls) <= 2) throw new LspError(-32801, "content modified");
                return Task.FromResult<object?>(new P.SemanticTokens { ResultId = "ok", Data = [0, 0, 1, 0, 0] });
            };
            s.Requests[P.LspMethods.TextDocumentHover] = _ => { Interlocked.Increment(ref hoverCalls); throw new LspError(-32801, "content modified"); };
        });
        var tokens = await env.Provider<ISemanticTokensProvider>(doc).ProvideSemanticTokensAsync(doc, Ct).Within();
        Assert.Equal(5, tokens!.Data.Count);          // two ContentModified answers were retried transparently
        Assert.Equal(3, tokenCalls);
        Assert.Null(await env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, new TextPosition(0, 0), Ct).Within());
        Assert.Equal(1, hoverCalls);                  // interactive: the text moved on, the editor will simply ask again
    }

    [Fact]
    public async Task ServerCancelled_and_RequestCancelled_yield_null_without_marking_the_server_unhealthy()
    {
        await using var env = new LspTestEnv(c => c.Set(LspSettingKeys.BreakerThreshold, 2));
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { HoverProvider = true };
            s.Requests[P.LspMethods.TextDocumentHover] = _ => throw new LspError(-32802, "server cancelled");
        });
        var hover = env.Provider<IHoverProvider>(doc);
        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await hover.ProvideHoverAsync(doc, new TextPosition(0, i), Ct).Within());
            doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "x")]);
        }
        Assert.Equal(LanguageServerState.Running, env.Manager.Servers.Single().State);
    }

    [Fact]
    public async Task Tracing_is_off_by_default_and_logs_methods_or_payloads_when_enabled()
    {
        await using (var env = new LspTestEnv())
        {
            var (_, doc) = await env.StartAsync(s => { s.Capabilities = new P.ServerCapabilities { HoverProvider = true }; s.Requests[P.LspMethods.TextDocumentHover] = _ => Task.FromResult<object?>(null); });
            await env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, new TextPosition(0, 0), Ct).Within();
            await Task.Delay(100, Ct);
            Assert.DoesNotContain("-->", env.Output.Text("Fake fake (LSP)"));
        }
        await using (var env = new LspTestEnv(c => c.Set(LspSettingKeys.Trace, "messages")))
        {
            var (_, doc) = await env.StartAsync(s => { s.Capabilities = new P.ServerCapabilities { HoverProvider = true }; s.Requests[P.LspMethods.TextDocumentHover] = _ => Task.FromResult<object?>(null); });
            await env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, new TextPosition(0, 7), Ct).Within();
            await TestHelpers.EventuallyAsync(() => env.Output.Text("Fake fake (LSP)").Contains("--> textDocument/hover"), "request traced");
            Assert.DoesNotContain("\"character\":7", env.Output.Text("Fake fake (LSP)")); // messages mode: names only
        }
        await using (var env = new LspTestEnv(c => c.Set(LspSettingKeys.Trace, "verbose")))
        {
            var (_, doc) = await env.StartAsync(s => { s.Capabilities = new P.ServerCapabilities { HoverProvider = true }; s.Requests[P.LspMethods.TextDocumentHover] = _ => Task.FromResult<object?>(null); });
            await env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, new TextPosition(0, 7), Ct).Within();
            await TestHelpers.EventuallyAsync(() => env.Output.Text("Fake fake (LSP)").Contains("\"character\":7"), "payload traced");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // workspace pull diagnostics, file notifications
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Workspace_diagnostics_are_pulled_for_files_that_are_not_open()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { TextDocumentSync = P.TextDocumentSyncKind.Incremental, DiagnosticProvider = new P.DiagnosticOptions { InterFileDependencies = true, WorkspaceDiagnostics = true } };
            s.Requests[P.LspMethods.TextDocumentDiagnostic] = _ => Task.FromResult<object?>(Json("""{"kind":"full","items":[]}"""));
            s.Requests[P.LspMethods.WorkspaceDiagnostic] = _ => Task.FromResult<object?>(Json("""
                {"items":[
                  {"kind":"full","uri":"file:///ws/closed1.fake","version":null,"items":[{"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":3}},"message":"in a closed file","severity":2}]},
                  {"kind":"unchanged","uri":"file:///ws/closed2.fake","resultId":"x"}]}
                """));
        });
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(new Uri("file:///ws/closed1.fake")).Count == 1, () => "workspace diagnostics published\n" + env.Dump());
        var d = env.Diagnostics.Get(new Uri("file:///ws/closed1.fake"))[0];
        Assert.Equal((DiagnosticSeverity.Warning, "in a closed file"), (d.Severity, d.Message));
        Assert.Empty(env.Diagnostics.Get(new Uri("file:///ws/closed2.fake"))); // "unchanged" carries nothing new
    }

    [Fact]
    public async Task Created_and_deleted_files_are_reported_only_to_servers_that_registered_for_them()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync(s => s.Capabilities = new P.ServerCapabilities
        {
            TextDocumentSync = P.TextDocumentSyncKind.Incremental,
            Workspace = new P.ServerCapabilitiesWorkspace
            {
                FileOperations = new P.FileOperationOptions
                {
                    DidCreate = new P.FileOperationRegistrationOptions { Filters = [new P.FileOperationFilter { Pattern = new P.FileOperationPattern { Glob = "**/*.fake" } }] },
                    DidDelete = new P.FileOperationRegistrationOptions { Filters = [new P.FileOperationFilter { Pattern = new P.FileOperationPattern { Glob = "**/*.fake" } }] },
                },
            },
        });
        await env.Manager.DidCreateFilesAsync([new Uri(env.Dir.Combine("n.fake")), new Uri(env.Dir.Combine("n.txt"))], Ct);
        await env.Manager.DidDeleteFilesAsync([new Uri(env.Dir.Combine("gone.fake"))], Ct);
        var created = await server.WaitForAsync(P.LspMethods.WorkspaceDidCreateFiles);
        Assert.Single(created.P<P.CreateFilesParams>().Files);
        Assert.EndsWith("n.fake", created.P<P.CreateFilesParams>().Files[0].Uri);
        var deleted = await server.WaitForAsync(P.LspMethods.WorkspaceDidDeleteFiles);
        Assert.EndsWith("gone.fake", deleted.P<P.DeleteFilesParams>().Files[0].Uri);
        Assert.Equal(0, server.CountOf(P.LspMethods.WorkspaceDidRenameFiles));
    }

    [Fact]
    public async Task A_message_announcing_an_absurd_size_is_refused_in_the_fake_transport_too()
    {
        // The same guard protects every transport (process, socket, pipe): here via the in-proc fake.
        await using var env = new LspTestEnv(c => c.Set(LspSettingKeys.MaxMessageBytes, 100_000));
        var (server, _) = await env.StartAsync();
        await server.WriteRawAsync(System.Text.Encoding.ASCII.GetBytes("Content-Length: 99999999\r\n\r\n{"));
        await TestHelpers.EventuallyAsync(() => env.Connections.ConnectCount >= 2 || env.Output.Text("Fake fake (LSP)").Contains("announced"), () => "connection dropped\n" + env.Dump());
        Assert.Contains("announced", env.Output.Text("Fake fake (LSP)"));
    }
}
