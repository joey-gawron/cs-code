using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Tests.Harness;
using Xunit;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Tests;

/// <summary>Every language feature end to end through the registry: request mapping, response mapping, capability gating.</summary>
public class FeatureProviderTests
{
    private static Task<object?> Reply(object? value) => Task.FromResult(value);
    private static P.Range R(int sl, int sc, int el, int ec) => new() { Start = new P.Position { Line = sl, Character = sc }, End = new P.Position { Line = el, Character = ec } };
    private static P.Position Pos(int l, int c) => new() { Line = l, Character = c };
    private static string U(ITextDocument d) => d.Uri.AbsoluteUri;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static P.ServerCapabilities Caps(Action<P.ServerCapabilities>? _ = null) => new() { TextDocumentSync = P.TextDocumentSyncKind.Incremental };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ------------------------------------------------------------------------------------------------------------
    // capability gating
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Providers_are_registered_only_for_advertised_capabilities_and_removed_on_stop()
    {
        await using var env = new LspTestEnv();
        var (_, doc) = await env.StartAsync(s => s.Capabilities = new P.ServerCapabilities { TextDocumentSync = P.TextDocumentSyncKind.Incremental, HoverProvider = true, DefinitionProvider = true });
        Assert.Single(env.Registry.GetProviders<IHoverProvider>(doc));
        Assert.Single(env.Registry.GetProviders<IDefinitionProvider>(doc));
        Assert.Empty(env.Registry.GetProviders<ICompletionProvider>(doc));
        Assert.Empty(env.Registry.GetProviders<IReferenceProvider>(doc));
        Assert.Empty(env.Registry.GetProviders<IRenameProvider>(doc));
        Assert.Empty(env.Registry.GetProviders<IDocumentFormattingProvider>(doc));
        await env.Manager.StopAsync("fake", Ct);
        Assert.Empty(env.Registry.GetAll<IHoverProvider>());
        Assert.Empty(env.Registry.GetAll<IDefinitionProvider>());
    }

    [Fact]
    public async Task Provider_is_only_offered_for_documents_the_definition_selects()
    {
        await using var env = new LspTestEnv();
        var (_, doc) = await env.StartAsync(s => s.Capabilities = new P.ServerCapabilities { HoverProvider = true });
        Assert.Single(env.Registry.GetProviders<IHoverProvider>(doc));
        Assert.Empty(env.Registry.GetProviders<IHoverProvider>(new Uri("file:///other.txt"), "plaintext"));
    }

    // ------------------------------------------------------------------------------------------------------------
    // hover / signature help
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Hover_maps_markup_markedstring_and_range_and_sends_the_position()
    {
        await using var env = new LspTestEnv();
        P.HoverParams? seen = null;
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { HoverProvider = true };
            s.Requests[P.LspMethods.TextDocumentHover] = p =>
            {
                seen = JsonSerializer.Deserialize<P.HoverParams>(p, P.LspJson.Options);
                return Reply(new P.Hover
                {
                    Contents = new P.MarkupContent { Kind = P.MarkupKind.Markdown, Value = "**bold**" },
                    Range = R(0, 0, 0, 5),
                });
            };
        });
        var hover = await env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, new TextPosition(1, 3), Ct).Within();
        Assert.NotNull(hover);
        Assert.Equal(new TextPosition(1, 3), new TextPosition(seen!.Position.Line, seen.Position.Character));
        Assert.Equal(U(doc), seen.TextDocument.Uri);
        var content = Assert.Single(hover.Contents);
        Assert.True(content.IsMarkdown);
        Assert.Equal("**bold**", content.Value);
        Assert.Equal(TextRange.FromLines(0, 0, 0, 5), hover.Range);
    }

    [Fact]
    public async Task Hover_marked_string_array_becomes_markdown_with_code_fences()
    {
        await using var env = new LspTestEnv();
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { HoverProvider = true };
            s.Requests[P.LspMethods.TextDocumentHover] = _ => Reply(Json("""{"contents":["plain *md*",{"language":"ts","value":"let x: number"}]}"""));
        });
        var hover = await env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, new TextPosition(0, 0), Ct).Within();
        Assert.Equal(2, hover!.Contents.Count);
        Assert.Equal("plain *md*", hover.Contents[0].Value);
        Assert.Equal("```ts\nlet x: number\n```", hover.Contents[1].Value);
    }

    [Fact]
    public async Task Hover_null_result_and_server_errors_yield_null_not_exceptions()
    {
        await using var env = new LspTestEnv();
        var calls = 0;
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { HoverProvider = true };
            s.Requests[P.LspMethods.TextDocumentHover] = _ =>
            {
                var n = Interlocked.Increment(ref calls);
                if (n == 2) throw new LspError(-32801, "content modified");
                if (n == 3) throw new LspError(-32603, "internal error");
                return Reply(null);
            };
        });
        var hover = env.Provider<IHoverProvider>(doc);
        Assert.Null(await hover.ProvideHoverAsync(doc, new TextPosition(0, 0), Ct).Within()); // JSON null
        Assert.Null(await hover.ProvideHoverAsync(doc, new TextPosition(0, 1), Ct).Within()); // ContentModified
        Assert.Null(await hover.ProvideHoverAsync(doc, new TextPosition(0, 2), Ct).Within()); // generic error
        Assert.Equal(LanguageServerState.Running, env.Manager.Servers.Single().State);          // an error answer proves the server alive
    }

    [Fact]
    public async Task SignatureHelp_maps_label_offsets_and_exposes_trigger_characters()
    {
        await using var env = new LspTestEnv();
        P.SignatureHelpParams? seen = null;
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { SignatureHelpProvider = new P.SignatureHelpOptions { TriggerCharacters = ["(", ","], RetriggerCharacters = [")"] } };
            s.Requests[P.LspMethods.TextDocumentSignatureHelp] = p =>
            {
                seen = JsonSerializer.Deserialize<P.SignatureHelpParams>(p, P.LspJson.Options);
                return Reply(Json("""
                    {"signatures":[{"label":"add(a: number, b: number)","documentation":{"kind":"markdown","value":"adds"},
                      "parameters":[{"label":[4,13]},{"label":"b: number","documentation":"second"}]}],
                     "activeSignature":0,"activeParameter":1}
                    """));
            };
        });
        var provider = env.Provider<ISignatureHelpProvider>(doc);
        Assert.Equal(["(", ","], provider.TriggerCharacters);
        Assert.Equal([")"], provider.RetriggerCharacters);
        var help = await provider.ProvideSignatureHelpAsync(doc, new TextPosition(0, 4),
            new SignatureHelpContext(SignatureHelpTriggerKind.TriggerCharacter, "(", false, null), Ct).Within();
        Assert.Equal(P.SignatureHelpTriggerKind.TriggerCharacter, seen!.Context!.TriggerKind);
        Assert.Equal("(", seen.Context.TriggerCharacter);
        Assert.Equal(1, help!.ActiveParameter);
        var sig = help.Signatures[0];
        Assert.Equal("a: number", sig.Parameters![0].Label);
        Assert.Equal((4, 13), sig.Parameters[0].LabelRange);
        Assert.Equal("b: number", sig.Parameters[1].Label);
        Assert.Equal("second", sig.Parameters[1].Documentation!.Value);
        Assert.True(sig.Documentation!.IsMarkdown);
    }

    // ------------------------------------------------------------------------------------------------------------
    // completion
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Completion_maps_every_item_field()
    {
        await using var env = new LspTestEnv();
        P.CompletionParams? seen = null;
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { CompletionProvider = new P.CompletionOptions { TriggerCharacters = [".", ":"], ResolveProvider = true } };
            s.Requests[P.LspMethods.TextDocumentCompletion] = p =>
            {
                seen = JsonSerializer.Deserialize<P.CompletionParams>(p, P.LspJson.Options);
                return Reply(Json("""
                    {"isIncomplete":false,"items":[
                      {"label":"toString","labelDetails":{"detail":"()","description":"string"},"kind":2,"detail":"(): string",
                       "documentation":{"kind":"markdown","value":"Returns a string"},"sortText":"0001","filterText":"toStr",
                       "insertText":"toString()","insertTextFormat":2,"preselect":true,"tags":[1],"commitCharacters":["("],
                       "textEdit":{"range":{"start":{"line":0,"character":2},"end":{"line":0,"character":4}},"newText":"toString($0)"},
                       "additionalTextEdits":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}},"newText":"import x;\n"}],
                       "command":{"title":"Trigger","command":"editor.action.triggerParameterHints"},"data":{"id":7}}]}
                    """));
            };
        });
        var provider = env.Provider<ICompletionProvider>(doc);
        Assert.Equal([".", ":"], provider.TriggerCharacters);
        var list = await provider.ProvideCompletionAsync(doc, new TextPosition(0, 4), new CompletionContext(CompletionTriggerKind.TriggerCharacter, "."), Ct).Within();

        Assert.Equal(P.CompletionTriggerKind.TriggerCharacter, seen!.Context!.TriggerKind);
        Assert.Equal(".", seen.Context.TriggerCharacter);
        Assert.False(list!.IsIncomplete);
        var item = Assert.Single(list.Items);
        Assert.Equal("toString", item.Label);
        Assert.Equal("()", item.LabelDetail);
        Assert.Equal("string", item.LabelDescription);
        Assert.Equal(CompletionItemKind.Method, item.Kind);
        Assert.Equal("(): string", item.Detail);
        Assert.True(item.Documentation!.IsMarkdown);
        Assert.Equal("0001", item.SortText);
        Assert.Equal("toStr", item.FilterText);
        Assert.Equal("toString()", item.InsertText);
        Assert.Equal(InsertTextFormat.Snippet, item.InsertTextFormat);
        Assert.True(item.Preselect);
        Assert.True(item.Deprecated);
        Assert.Equal(["("], item.CommitCharacters);
        Assert.Equal(new TextEdit(TextRange.FromLines(0, 2, 0, 4), "toString($0)"), item.TextEdit);
        Assert.Equal("import x;\n", item.AdditionalTextEdits![0].NewText);
        Assert.Equal("editor.action.triggerParameterHints", item.Command!.CommandId);
        Assert.Equal(7, item.Data!.Value.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Completion_applies_list_item_defaults_and_insert_replace_edits()
    {
        await using var env = new LspTestEnv();
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { CompletionProvider = new P.CompletionOptions() };
            s.Requests[P.LspMethods.TextDocumentCompletion] = _ => Reply(Json("""
                {"isIncomplete":false,
                 "itemDefaults":{"commitCharacters":[";"],"editRange":{"start":{"line":0,"character":1},"end":{"line":0,"character":3}},"insertTextFormat":2,"data":{"d":1}},
                 "items":[{"label":"plain"},
                          {"label":"with-edit-text","textEditText":"custom"},
                          {"label":"ir","textEdit":{"newText":"ir","insert":{"start":{"line":0,"character":0},"end":{"line":0,"character":2}},"replace":{"start":{"line":0,"character":0},"end":{"line":0,"character":5}}}}]}
                """));
        });
        var list = await env.Provider<ICompletionProvider>(doc).ProvideCompletionAsync(doc, new TextPosition(0, 2), new CompletionContext(CompletionTriggerKind.Invoked), Ct).Within();
        var plain = list!.Items[0];
        Assert.Equal([";"], plain.CommitCharacters);
        Assert.Equal(InsertTextFormat.Snippet, plain.InsertTextFormat);
        Assert.Equal(new TextEdit(TextRange.FromLines(0, 1, 0, 3), "plain"), plain.TextEdit);
        Assert.Equal(1, plain.Data!.Value.GetProperty("d").GetInt32());
        Assert.Equal("custom", list.Items[1].TextEdit!.NewText);
        Assert.Equal(TextRange.FromLines(0, 0, 0, 2), list.Items[2].TextEdit!.Range); // insert range wins (VS Code default insert mode)
    }

    [Fact]
    public async Task Huge_completion_lists_are_capped_and_marked_incomplete()
    {
        await using var env = new LspTestEnv(c => c.Set(LspSettingKeys.MaxCompletionItems, 100));
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { CompletionProvider = new P.CompletionOptions() };
            s.Requests[P.LspMethods.TextDocumentCompletion] = _ => Reply(new P.CompletionList
            {
                IsIncomplete = false,
                Items = Enumerable.Range(0, 12_000).Select(i => new P.CompletionItem { Label = "item" + i }).ToArray(),
            });
        });
        var list = await env.Provider<ICompletionProvider>(doc).ProvideCompletionAsync(doc, new TextPosition(0, 0), new CompletionContext(CompletionTriggerKind.Invoked), Ct).Within();
        Assert.Equal(100, list!.Items.Count);
        Assert.True(list.IsIncomplete); // the consumer re-queries as the user types instead of receiving 12k items
    }

    [Fact]
    public async Task Completion_resolve_echoes_the_original_item_and_merges_the_result()
    {
        await using var env = new LspTestEnv();
        P.CompletionItem? resolveInput = null;
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { CompletionProvider = new P.CompletionOptions { ResolveProvider = true } };
            s.Requests[P.LspMethods.TextDocumentCompletion] = _ => Reply(Json("""{"isIncomplete":false,"items":[{"label":"foo","kind":3,"data":{"token":"abc"},"sortText":"s"}]}"""));
            s.Requests[P.LspMethods.CompletionItemResolve] = p =>
            {
                resolveInput = JsonSerializer.Deserialize<P.CompletionItem>(p, P.LspJson.Options);
                return Reply(Json("""{"label":"foo","detail":"resolved detail","documentation":"resolved docs","data":{"token":"abc"},"additionalTextEdits":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}},"newText":"import foo;\n"}]}"""));
            };
        });
        var provider = env.Provider<ICompletionProvider>(doc);
        var list = await provider.ProvideCompletionAsync(doc, new TextPosition(0, 0), new CompletionContext(CompletionTriggerKind.Invoked), Ct).Within();
        var resolved = await provider.ResolveCompletionAsync(list!.Items[0], Ct).Within();
        Assert.Equal("foo", resolveInput!.Label);
        Assert.Equal(P.CompletionItemKind.Function, resolveInput.Kind); // the ORIGINAL item went back, not a lossy rebuild
        Assert.Equal("abc", resolveInput.Data!.Value.GetProperty("token").GetString());
        Assert.Equal("resolved detail", resolved.Detail);
        Assert.Equal("resolved docs", resolved.Documentation!.Value);
        Assert.Equal("import foo;\n", resolved.AdditionalTextEdits![0].NewText);
        Assert.Equal(CompletionItemKind.Function, resolved.Kind); // fields the resolve did not change survive
    }

    [Fact]
    public async Task Completion_resolve_is_a_no_op_when_the_server_does_not_resolve()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { CompletionProvider = new P.CompletionOptions() };
            s.Requests[P.LspMethods.TextDocumentCompletion] = _ => Reply(Json("""{"isIncomplete":false,"items":[{"label":"x"}]}"""));
        });
        var provider = env.Provider<ICompletionProvider>(doc);
        var list = await provider.ProvideCompletionAsync(doc, new TextPosition(0, 0), new CompletionContext(CompletionTriggerKind.Invoked), Ct).Within();
        var same = await provider.ResolveCompletionAsync(list!.Items[0], Ct).Within();
        Assert.Same(list.Items[0], same);
        Assert.Equal(0, server.CountOf(P.LspMethods.CompletionItemResolve));
    }

    // ------------------------------------------------------------------------------------------------------------
    // navigation
    // ------------------------------------------------------------------------------------------------------------

    private const string Loc = """{"uri":"file:///x/other.fake","range":{"start":{"line":3,"character":1},"end":{"line":3,"character":6}}}""";

    [Fact]
    public async Task Definition_handles_Location_Location_array_and_LocationLink_array()
    {
        await using var env = new LspTestEnv();
        var shape = 0;
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { DefinitionProvider = true };
            s.Requests[P.LspMethods.TextDocumentDefinition] = _ => Reply(Interlocked.Increment(ref shape) switch
            {
                1 => Json(Loc),
                2 => Json("[" + Loc + "," + Loc + "]"),
                3 => Json("""[{"originSelectionRange":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"targetUri":"file:///x/t.fake","targetRange":{"start":{"line":1,"character":0},"end":{"line":9,"character":0}},"targetSelectionRange":{"start":{"line":1,"character":4},"end":{"line":1,"character":8}}}]"""),
                _ => null,
            });
        });
        var def = env.Provider<IDefinitionProvider>(doc);
        var single = await def.ProvideDefinitionAsync(doc, new TextPosition(0, 0), DefinitionKind.Definition, Ct).Within();
        Assert.NotNull(single);
        Assert.Equal(new Uri("file:///x/other.fake"), single.Single().TargetUri);
        Assert.Equal(TextRange.FromLines(3, 1, 3, 6), single[0].TargetRange);
        Assert.Equal(single[0].TargetRange, single[0].TargetSelectionRange);

        var many = await def.ProvideDefinitionAsync(doc, new TextPosition(0, 1), DefinitionKind.Definition, Ct).Within();
        Assert.Equal(2, many!.Count);

        var links = await def.ProvideDefinitionAsync(doc, new TextPosition(0, 2), DefinitionKind.Definition, Ct).Within();
        Assert.NotNull(links);
        Assert.Equal(TextRange.FromLines(1, 4, 1, 8), links.Single().TargetSelectionRange);
        Assert.Equal(TextRange.FromLines(1, 0, 9, 0), links[0].TargetRange);

        Assert.Null(await def.ProvideDefinitionAsync(doc, new TextPosition(0, 3), DefinitionKind.Definition, Ct).Within());
    }

    [Fact]
    public async Task Declaration_TypeDefinition_and_Implementation_use_their_own_methods_and_unadvertised_kinds_return_null()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { TypeDefinitionProvider = true, ImplementationProvider = true };
            s.Requests[P.LspMethods.TextDocumentTypeDefinition] = _ => Reply(Json(Loc));
            s.Requests[P.LspMethods.TextDocumentImplementation] = _ => Reply(Json("[" + Loc + "]"));
        });
        var def = env.Provider<IDefinitionProvider>(doc);
        Assert.NotNull(await def.ProvideDefinitionAsync(doc, new TextPosition(0, 0), DefinitionKind.TypeDefinition, Ct).Within());
        Assert.NotNull(await def.ProvideDefinitionAsync(doc, new TextPosition(0, 0), DefinitionKind.Implementation, Ct).Within());
        Assert.Null(await def.ProvideDefinitionAsync(doc, new TextPosition(0, 0), DefinitionKind.Definition, Ct).Within());
        Assert.Null(await def.ProvideDefinitionAsync(doc, new TextPosition(0, 0), DefinitionKind.Declaration, Ct).Within());
        Assert.Equal(0, server.CountOf(P.LspMethods.TextDocumentDefinition));
        Assert.Equal(0, server.CountOf(P.LspMethods.TextDocumentDeclaration));
    }

    [Fact]
    public async Task References_pass_includeDeclaration_and_are_capped()
    {
        await using var env = new LspTestEnv(c => c.Set(LspSettingKeys.MaxReferences, 50));
        P.ReferenceParams? seen = null;
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { ReferencesProvider = true };
            s.Requests[P.LspMethods.TextDocumentReferences] = p =>
            {
                seen = JsonSerializer.Deserialize<P.ReferenceParams>(p, P.LspJson.Options);
                return Reply(Enumerable.Range(0, 400).Select(i => new P.Location { Uri = "file:///x/r" + i + ".fake", Range = R(i, 0, i, 1) }).ToArray());
            };
        });
        var refs = await env.Provider<IReferenceProvider>(doc).ProvideReferencesAsync(doc, new TextPosition(0, 2), includeDeclaration: true, Ct).Within();
        Assert.True(seen!.Context.IncludeDeclaration);
        Assert.Equal(50, refs!.Count);
        Assert.Equal(new Uri("file:///x/r0.fake"), refs[0].Uri);
    }

    [Fact]
    public async Task DocumentHighlights_map_kinds()
    {
        await using var env = new LspTestEnv();
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { DocumentHighlightProvider = true };
            s.Requests[P.LspMethods.TextDocumentDocumentHighlight] = _ => Reply(Json("""[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":3}},"kind":2},{"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":3}},"kind":3},{"range":{"start":{"line":2,"character":0},"end":{"line":2,"character":3}}}]"""));
        });
        var h = await env.Provider<IDocumentHighlightProvider>(doc).ProvideDocumentHighlightsAsync(doc, new TextPosition(0, 1), Ct).Within();
        Assert.Equal([DocumentHighlightKind.Read, DocumentHighlightKind.Write, DocumentHighlightKind.Text], h!.Select(x => x.Kind));
    }

    [Fact]
    public async Task DocumentSymbols_hierarchical_and_flat_fallback()
    {
        await using var env = new LspTestEnv();
        var flat = false;
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { DocumentSymbolProvider = true };
            s.Requests[P.LspMethods.TextDocumentDocumentSymbol] = _ => Reply(flat
                ? Json("""[{"name":"Flat","kind":12,"containerName":"Mod","location":{"uri":"file:///x/a.fake","range":{"start":{"line":1,"character":0},"end":{"line":2,"character":0}}}}]""")
                : Json("""[{"name":"Outer","kind":5,"detail":"class","range":{"start":{"line":0,"character":0},"end":{"line":9,"character":0}},"selectionRange":{"start":{"line":0,"character":6},"end":{"line":0,"character":11}},"tags":[1],"children":[{"name":"inner","kind":6,"range":{"start":{"line":1,"character":0},"end":{"line":2,"character":0}},"selectionRange":{"start":{"line":1,"character":2},"end":{"line":1,"character":7}}}]}]"""));
        });
        var provider = env.Provider<IDocumentSymbolProvider>(doc);
        var tree = await provider.ProvideDocumentSymbolsAsync(doc, Ct).Within();
        var outer = tree!.Single();
        Assert.Equal(("Outer", SymbolKind.Class, "class", true), (outer.Name, outer.Kind, outer.Detail, outer.Deprecated));
        Assert.Equal("inner", outer.Children!.Single().Name);
        Assert.Equal(SymbolKind.Method, outer.Children![0].Kind);
        Assert.Equal(TextRange.FromLines(0, 6, 0, 11), outer.SelectionRange);

        flat = true;
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), " ")]); // new version: not a stale/superseded request
        var list = await provider.ProvideDocumentSymbolsAsync(doc, Ct).Within();
        Assert.Equal(("Flat", SymbolKind.Function, "Mod"), (list![0].Name, list[0].Kind, list[0].Detail));
    }

    [Fact]
    public async Task WorkspaceSymbols_query_the_server_and_accept_both_result_shapes()
    {
        await using var env = new LspTestEnv();
        var shape = 0;
        string? query = null;
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { WorkspaceSymbolProvider = true };
            s.Requests[P.LspMethods.WorkspaceSymbol] = p =>
            {
                query = p.GetProperty("query").GetString();
                return Reply(Interlocked.Increment(ref shape) == 1
                    ? Json("""[{"name":"Sym","kind":5,"containerName":"ns","location":{"uri":"file:///x/s.fake","range":{"start":{"line":4,"character":0},"end":{"line":4,"character":3}}}}]""")
                    : Json("""[{"name":"Lazy","kind":12,"location":{"uri":"file:///x/lazy.fake"}}]"""));
            };
        });
        var provider = env.Registry.GetAll<IWorkspaceSymbolProvider>().Single();
        var a = await provider.ProvideWorkspaceSymbolsAsync("Sy", Ct).Within();
        Assert.Equal("Sy", query);
        Assert.Equal(("Sym", SymbolKind.Class, "ns"), (a![0].Name, a[0].Kind, a[0].ContainerName));
        Assert.Equal(TextRange.FromLines(4, 0, 4, 3), a[0].Location.Range);
        var b = await provider.ProvideWorkspaceSymbolsAsync("La", Ct).Within();
        Assert.Equal(new Uri("file:///x/lazy.fake"), b![0].Location.Uri); // range-less workspace symbol (resolved lazily by the shell)
    }

    // ------------------------------------------------------------------------------------------------------------
    // code actions / rename / formatting
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CodeActions_mix_commands_and_actions_and_pass_diagnostics_back_unchanged()
    {
        await using var env = new LspTestEnv();
        P.CodeActionParams? seen = null;
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { CodeActionProvider = new P.CodeActionOptions { ResolveProvider = true } };
            s.Requests[P.LspMethods.TextDocumentCodeAction] = p =>
            {
                seen = JsonSerializer.Deserialize<P.CodeActionParams>(p, P.LspJson.Options);
                return Reply(Json("""
                    [{"title":"Run tests","command":"tests.run","arguments":[1,"a"]},
                     {"title":"Fix it","kind":"quickfix","isPreferred":true,"data":{"k":9},
                      "diagnostics":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"message":"bad"}]},
                     {"title":"Disabled","kind":"refactor","disabled":{"reason":"not here"}}]
                    """));
            };
            s.Requests[P.LspMethods.CodeActionResolve] = p => Reply(Json("""{"title":"Fix it","kind":"quickfix","edit":{"changes":{"file:///x/a.fake":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"newText":"fixed"}]}}}"""));
            s.Requests[P.LspMethods.WorkspaceExecuteCommand] = _ => Reply(null);
        });
        // a diagnostic as the language client itself published it (with server-private `data` that Core cannot carry)
        await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, Json("{\"uri\":\"" + U(doc) + "\",\"diagnostics\":[{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":5}},\"message\":\"oops\",\"code\":\"E1\",\"data\":{\"fixId\":42}}]}"));
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Count == 1, "diagnostic published");
        var diag = env.Diagnostics.Get(doc.Uri)[0];

        var provider = env.Provider<ICodeActionProvider>(doc);
        var actions = await provider.ProvideCodeActionsAsync(doc, TextRange.FromLines(0, 0, 0, 5), new CodeActionContext([diag], ["quickfix"], CodeActionTriggerKind.Invoked), Ct).Within();

        Assert.Equal(P.CodeActionTriggerKind.Invoked, seen!.Context.TriggerKind);
        Assert.Equal("quickfix", seen.Context.Only![0].Value);
        Assert.Equal(42, seen.Context.Diagnostics[0].Data!.Value.GetProperty("fixId").GetInt32()); // server-private data survived the round trip

        Assert.Equal(3, actions!.Count);
        Assert.Equal(("Run tests", "tests.run"), (actions[0].Title, actions[0].Command!.CommandId));
        Assert.Equal(2, actions[0].Command!.Arguments!.Count);
        Assert.True(actions[1].IsPreferred);
        Assert.Equal("quickfix", actions[1].Kind);
        Assert.Equal("not here", actions[2].DisabledReason);

        var resolved = await provider.ResolveCodeActionAsync(actions[1], Ct).Within();
        Assert.Equal("fixed", resolved.Edit!.Changes.Single().Value[0].NewText);
    }

    [Fact]
    public async Task PrepareRename_three_shapes_and_rename_workspace_edit_with_resource_operations()
    {
        await using var env = new LspTestEnv();
        var shape = 0;
        string docUri = "";
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { RenameProvider = new P.RenameOptions { PrepareProvider = true } };
            s.Requests[P.LspMethods.TextDocumentPrepareRename] = _ => Reply(Interlocked.Increment(ref shape) switch
            {
                1 => Json("""{"start":{"line":0,"character":0},"end":{"line":0,"character":5}}"""),
                2 => Json("""{"range":{"start":{"line":0,"character":1},"end":{"line":0,"character":4}},"placeholder":"foo"}"""),
                3 => Json("""{"defaultBehavior":true}"""),
                _ => null,
            });
            s.Requests[P.LspMethods.TextDocumentRename] = p => Reply(Json(
                "{\"documentChanges\":[" +
                "{\"kind\":\"create\",\"uri\":\"file:///x/new.fake\"}," +
                "{\"textDocument\":{\"uri\":\"" + docUri + "\",\"version\":null},\"edits\":[{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":5}},\"newText\":\"renamed\"}]}," +
                "{\"kind\":\"rename\",\"oldUri\":\"file:///x/old.fake\",\"newUri\":\"file:///x/renamed.fake\"}," +
                "{\"kind\":\"delete\",\"uri\":\"file:///x/gone.fake\",\"options\":{\"recursive\":true}}]}"));
        });
        docUri = U(doc);
        var rename = env.Provider<IRenameProvider>(doc);
        var a = await rename.PrepareRenameAsync(doc, new TextPosition(0, 1), Ct).Within();
        Assert.Equal((TextRange.FromLines(0, 0, 0, 5), (string?)null), (a!.Range, a.Placeholder));
        var b = await rename.PrepareRenameAsync(doc, new TextPosition(0, 2), Ct).Within();
        Assert.Equal((TextRange.FromLines(0, 1, 0, 4), "foo"), (b!.Range, b.Placeholder));
        var c = await rename.PrepareRenameAsync(doc, new TextPosition(0, 3), Ct).Within();
        Assert.True(c!.Range.IsEmpty); // defaultBehavior: the editor picks the word at the caret
        Assert.Null(await rename.PrepareRenameAsync(doc, new TextPosition(0, 4), Ct).Within());

        var edit = await rename.ProvideRenameAsync(doc, new TextPosition(0, 1), "renamed", Ct).Within();
        Assert.Equal("renamed", edit!.Changes[doc.Uri][0].NewText);
        Assert.IsType<CreateFileOperation>(edit.ResourceOperations![0]);
        var rn = Assert.IsType<RenameFileOperation>(edit.ResourceOperations[1]);
        Assert.Equal(new Uri("file:///x/renamed.fake"), rn.NewUri);
        Assert.True(Assert.IsType<DeleteFileOperation>(edit.ResourceOperations[2]).Recursive);
    }

    [Fact]
    public async Task Rename_without_prepareProvider_does_not_call_prepareRename()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { RenameProvider = true };
            s.Requests[P.LspMethods.TextDocumentRename] = _ => Reply(Json("""{"changes":{}}"""));
        });
        Assert.Null(await env.Provider<IRenameProvider>(doc).PrepareRenameAsync(doc, new TextPosition(0, 0), Ct).Within());
        Assert.Equal(0, server.CountOf(P.LspMethods.TextDocumentPrepareRename));
    }

    [Fact]
    public async Task Formatting_document_range_and_ontype()
    {
        await using var env = new LspTestEnv();
        P.DocumentFormattingParams? seen = null;
        P.DocumentOnTypeFormattingParams? onType = null;
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities
            {
                DocumentFormattingProvider = true,
                DocumentRangeFormattingProvider = true,
                DocumentOnTypeFormattingProvider = new P.DocumentOnTypeFormattingOptions { FirstTriggerCharacter = "}", MoreTriggerCharacter = [";"] },
            };
            s.Requests[P.LspMethods.TextDocumentFormatting] = p => { seen = JsonSerializer.Deserialize<P.DocumentFormattingParams>(p, P.LspJson.Options); return Reply(Json("""[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":5}},"newText":"HELLO"}]""")); };
            s.Requests[P.LspMethods.TextDocumentRangeFormatting] = _ => Reply(Json("""[{"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":3}},"newText":"RNG"}]"""));
            s.Requests[P.LspMethods.TextDocumentOnTypeFormatting] = p => { onType = JsonSerializer.Deserialize<P.DocumentOnTypeFormattingParams>(p, P.LspJson.Options); return Reply(Json("[]")); };
        });
        var fmt = env.Provider<IDocumentFormattingProvider>(doc);
        Assert.Equal(["}", ";"], fmt.OnTypeTriggerCharacters);
        var opts = new FormattingOptions(2, false, true, true);
        var edits = await fmt.FormatDocumentAsync(doc, opts, Ct).Within();
        Assert.Equal("HELLO", edits![0].NewText);
        Assert.Equal((2, false, true, true), (seen!.Options.TabSize, seen.Options.InsertSpaces, seen.Options.TrimTrailingWhitespace, seen.Options.InsertFinalNewline));
        var range = await fmt.FormatRangeAsync(doc, TextRange.FromLines(1, 0, 1, 3), opts, Ct).Within();
        Assert.Equal(TextRange.FromLines(1, 0, 1, 3), range![0].Range);
        var typed = await fmt.FormatOnTypeAsync(doc, new TextPosition(0, 2), "}", opts, Ct).Within();
        Assert.Empty(typed!);
        Assert.Equal("}", onType!.Ch);
    }

    [Fact]
    public async Task Unadvertised_formatting_variants_return_null_without_a_request()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { DocumentFormattingProvider = true };
            s.Requests[P.LspMethods.TextDocumentFormatting] = _ => Reply(Json("[]"));
        });
        var fmt = env.Provider<IDocumentFormattingProvider>(doc);
        Assert.Null(await fmt.FormatRangeAsync(doc, TextRange.FromLines(0, 0, 0, 1), new FormattingOptions(), Ct).Within());
        Assert.Null(await fmt.FormatOnTypeAsync(doc, new TextPosition(0, 0), "}", new FormattingOptions(), Ct).Within());
        Assert.Empty(fmt.OnTypeTriggerCharacters);
        Assert.Equal(0, server.CountOf(P.LspMethods.TextDocumentRangeFormatting));
    }

    // ------------------------------------------------------------------------------------------------------------
    // structure: folding, selection, semantic tokens, hints, lenses, colours
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FoldingRanges_map_kinds()
    {
        await using var env = new LspTestEnv();
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { FoldingRangeProvider = true };
            s.Requests[P.LspMethods.TextDocumentFoldingRange] = _ => Reply(Json("""[{"startLine":0,"endLine":3,"kind":"comment"},{"startLine":4,"endLine":6,"kind":"imports"},{"startLine":7,"endLine":9,"kind":"region","collapsedText":"..."},{"startLine":10,"endLine":12}]"""));
        });
        var r = await env.Provider<IFoldingRangeProvider>(doc).ProvideFoldingRangesAsync(doc, Ct).Within();
        Assert.NotNull(r);
        Assert.Equal([FoldingRangeKind.Comment, FoldingRangeKind.Imports, FoldingRangeKind.Region, null], r.Select(x => x.Kind));
        Assert.Equal("...", r[2].CollapsedText);
    }

    [Fact]
    public async Task SelectionRanges_build_the_parent_chain()
    {
        await using var env = new LspTestEnv();
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { SelectionRangeProvider = true };
            s.Requests[P.LspMethods.TextDocumentSelectionRange] = _ => Reply(Json("""[{"range":{"start":{"line":0,"character":2},"end":{"line":0,"character":4}},"parent":{"range":{"start":{"line":0,"character":0},"end":{"line":1,"character":0}}}}]"""));
        });
        var r = await env.Provider<ISelectionRangeProvider>(doc).ProvideSelectionRangesAsync(doc, [new TextPosition(0, 3)], Ct).Within();
        Assert.Equal(TextRange.FromLines(0, 2, 0, 4), r![0].Range);
        Assert.Equal(TextRange.FromLines(0, 0, 1, 0), r[0].Parent!.Range);
        Assert.Null(r[0].Parent!.Parent);
    }

    [Fact]
    public async Task SemanticTokens_full_delta_and_range_with_legend()
    {
        await using var env = new LspTestEnv();
        var calls = new List<string>();
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities
            {
                SemanticTokensProvider = new P.SemanticTokensOptions
                {
                    Legend = new P.SemanticTokensLegend { TokenTypes = ["class", "function"], TokenModifiers = ["static"] },
                    Full = new P.SemanticTokensOptionsFullOption2 { Delta = true },
                    Range = true,
                },
            };
            s.Requests[P.LspMethods.TextDocumentSemanticTokensFull] = _ => { calls.Add("full"); return Reply(Json("""{"resultId":"r1","data":[0,0,5,1,0,0,6,3,2,1]}""")); };
            s.Requests[P.LspMethods.TextDocumentSemanticTokensFullDelta] = p =>
            {
                calls.Add("delta:" + p.GetProperty("previousResultId").GetString());
                return Reply(Json("""{"resultId":"r2","edits":[{"start":5,"deleteCount":5,"data":[1,0,4,0,0]}]}"""));
            };
            s.Requests[P.LspMethods.TextDocumentSemanticTokensRange] = _ => { calls.Add("range"); return Reply(Json("""{"data":[9,9,9,9,9]}""")); };
        });
        var provider = env.Provider<ISemanticTokensProvider>(doc);
        Assert.Equal(["class", "function"], provider.Legend.TokenTypes);
        Assert.Equal(["static"], provider.Legend.TokenModifiers);

        var first = await provider.ProvideSemanticTokensAsync(doc, Ct).Within();
        Assert.Equal(new uint[] { 0, 0, 5, 1, 0, 0, 6, 3, 2, 1 }, first!.Data.ToArray());
        Assert.Equal("r1", first.ResultId);

        var second = await provider.ProvideSemanticTokensAsync(doc, Ct).Within(); // same version: delta against r1, applied locally
        Assert.Equal(new uint[] { 0, 0, 5, 1, 0, 1, 0, 4, 0, 0 }, second!.Data.ToArray());
        Assert.Equal("r2", second.ResultId);
        Assert.Equal(["full", "delta:r1"], calls);

        var rangeProvider = env.Provider<ISemanticTokensRangeProvider>(doc);
        var range = await rangeProvider.ProvideSemanticTokensRangeAsync(doc, TextRange.FromLines(0, 0, 1, 0), Ct).Within();
        Assert.Equal(5, range!.Data.Count);
        Assert.Equal(["class", "function"], rangeProvider.Legend.TokenTypes);
    }

    [Fact]
    public async Task SemanticTokens_are_capped_and_never_used_as_a_delta_base_when_truncated()
    {
        await using var env = new LspTestEnv(c => c.Set(LspSettingKeys.MaxSemanticTokens, 100));
        var calls = new List<string>();
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities
            {
                SemanticTokensProvider = new P.SemanticTokensOptions { Legend = new P.SemanticTokensLegend(), Full = new P.SemanticTokensOptionsFullOption2 { Delta = true } },
            };
            s.Requests[P.LspMethods.TextDocumentSemanticTokensFull] = _ => { calls.Add("full"); return Reply(new P.SemanticTokens { ResultId = "big", Data = new uint[5 * 1_000_000 / 100] }); };
            s.Requests[P.LspMethods.TextDocumentSemanticTokensFullDelta] = _ => { calls.Add("delta"); return Reply(null); };
        });
        var provider = env.Provider<ISemanticTokensProvider>(doc);
        var t = await provider.ProvideSemanticTokensAsync(doc, Ct).Within();
        Assert.Equal(500, t!.Data.Count); // 100 tokens x 5
        await provider.ProvideSemanticTokensAsync(doc, Ct).Within();
        Assert.Equal(["full", "full"], calls); // the truncated array is not a valid delta base
    }

    [Fact]
    public async Task InlayHints_flatten_label_parts_and_map_kind_and_padding()
    {
        await using var env = new LspTestEnv();
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { InlayHintProvider = true };
            s.Requests[P.LspMethods.TextDocumentInlayHint] = _ => Reply(Json("""
                [{"position":{"line":0,"character":3},"label":": number","kind":1,"paddingLeft":true},
                 {"position":{"line":1,"character":0},"label":[{"value":"count"},{"value":":"}],"kind":2,"paddingRight":true,"tooltip":{"kind":"plaintext","value":"tip"}}]
                """));
        });
        var hints = await env.Provider<IInlayHintsProvider>(doc).ProvideInlayHintsAsync(doc, TextRange.FromLines(0, 0, 5, 0), Ct).Within();
        Assert.Equal(": number", hints![0].Label);
        Assert.Equal((InlayHintKind.Type, true, false), (hints[0].Kind, hints[0].PaddingLeft, hints[0].PaddingRight));
        Assert.Equal("count:", hints[1].Label);
        Assert.Equal((InlayHintKind.Parameter, false, true), (hints[1].Kind, hints[1].PaddingLeft, hints[1].PaddingRight));
        Assert.Equal("tip", hints[1].Tooltip!.Value);
    }

    [Fact]
    public async Task CodeLens_and_resolve()
    {
        await using var env = new LspTestEnv();
        P.CodeLens? resolveInput = null;
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { CodeLensProvider = new P.CodeLensOptions { ResolveProvider = true } };
            s.Requests[P.LspMethods.TextDocumentCodeLens] = _ => Reply(Json("""[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":3}},"data":{"n":1}},{"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":3}},"command":{"title":"3 references","command":"refs.show"}}]"""));
            s.Requests[P.LspMethods.CodeLensResolve] = p =>
            {
                resolveInput = JsonSerializer.Deserialize<P.CodeLens>(p, P.LspJson.Options);
                return Reply(Json("""{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":3}},"command":{"title":"resolved","command":"c.run"}}"""));
            };
        });
        var provider = env.Provider<ICodeLensProvider>(doc);
        var lenses = await provider.ProvideCodeLensesAsync(doc, Ct).Within();
        Assert.Null(lenses![0].Command);
        Assert.Equal("3 references", lenses[1].Command!.Title);
        var resolved = await provider.ResolveCodeLensAsync(lenses[0], Ct).Within();
        Assert.Equal(1, resolveInput!.Data!.Value.GetProperty("n").GetInt32());
        Assert.Equal("resolved", resolved.Command!.Title);
        Assert.Same(lenses[1], await provider.ResolveCodeLensAsync(lenses[1], Ct).Within()); // already has a command
    }

    [Fact]
    public async Task DocumentColors_and_linked_editing_and_hierarchies()
    {
        await using var env = new LspTestEnv();
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { ColorProvider = true, LinkedEditingRangeProvider = true, CallHierarchyProvider = true, TypeHierarchyProvider = true };
            s.Requests[P.LspMethods.TextDocumentDocumentColor] = _ => Reply(Json("""[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":7}},"color":{"red":1,"green":0.5,"blue":0,"alpha":1}}]"""));
            s.Requests[P.LspMethods.TextDocumentLinkedEditingRange] = _ => Reply(Json("""{"ranges":[{"start":{"line":0,"character":1},"end":{"line":0,"character":4}},{"start":{"line":0,"character":8},"end":{"line":0,"character":11}}],"wordPattern":"[a-z]+"}"""));
            s.Requests[P.LspMethods.TextDocumentPrepareCallHierarchy] = _ => Reply(Json("""[{"name":"main","kind":12,"uri":"file:///x/m.fake","range":{"start":{"line":0,"character":0},"end":{"line":5,"character":0}},"selectionRange":{"start":{"line":0,"character":4},"end":{"line":0,"character":8}},"data":{"id":1}}]"""));
            s.Requests[P.LspMethods.CallHierarchyIncomingCalls] = p => Reply(Json("""[{"from":{"name":"caller","kind":12,"uri":"file:///x/c.fake","range":{"start":{"line":2,"character":0},"end":{"line":3,"character":0}},"selectionRange":{"start":{"line":2,"character":0},"end":{"line":2,"character":6}}},"fromRanges":[{"start":{"line":2,"character":8},"end":{"line":2,"character":12}}]}]"""));
            s.Requests[P.LspMethods.CallHierarchyOutgoingCalls] = _ => Reply(Json("[]"));
            s.Requests[P.LspMethods.TextDocumentPrepareTypeHierarchy] = _ => Reply(Json("""[{"name":"Base","kind":5,"uri":"file:///x/b.fake","range":{"start":{"line":0,"character":0},"end":{"line":9,"character":0}},"selectionRange":{"start":{"line":0,"character":6},"end":{"line":0,"character":10}}}]"""));
            s.Requests[P.LspMethods.TypeHierarchySupertypes] = _ => Reply(Json("[]"));
            s.Requests[P.LspMethods.TypeHierarchySubtypes] = _ => Reply(Json("""[{"name":"Derived","kind":5,"uri":"file:///x/d.fake","range":{"start":{"line":0,"character":0},"end":{"line":9,"character":0}},"selectionRange":{"start":{"line":0,"character":6},"end":{"line":0,"character":13}}}]"""));
        });
        var colors = await env.Provider<IDocumentColorProvider>(doc).ProvideDocumentColorsAsync(doc, Ct).Within();
        Assert.Equal((1d, 0.5d, 0d, 1d), (colors![0].Red, colors[0].Green, colors[0].Blue, colors[0].Alpha));

        var linked = await env.Provider<ILinkedEditingRangeProvider>(doc).ProvideLinkedEditingRangesAsync(doc, new TextPosition(0, 2), Ct).Within();
        Assert.Equal(2, linked!.Ranges.Count);
        Assert.Equal("[a-z]+", linked.WordPattern);

        var calls = env.Provider<ICallHierarchyProvider>(doc);
        var items = await calls.PrepareCallHierarchyAsync(doc, new TextPosition(0, 5), Ct).Within();
        Assert.Equal(("main", SymbolKind.Function), (items![0].Name, items[0].Kind));
        var incoming = await calls.IncomingCallsAsync(items[0], Ct).Within();
        Assert.Equal("caller", incoming![0].From.Name);
        Assert.Equal(TextRange.FromLines(2, 8, 2, 12), incoming[0].FromRanges[0]);
        Assert.Empty((await calls.OutgoingCallsAsync(items[0], Ct).Within())!);

        var types = env.Provider<ITypeHierarchyProvider>(doc);
        var bases = await types.PrepareTypeHierarchyAsync(doc, new TextPosition(0, 7), Ct).Within();
        var subs = await types.SubtypesAsync(bases![0], Ct).Within();
        Assert.Equal("Derived", subs![0].Name);
        Assert.Empty((await types.SupertypesAsync(bases[0], Ct).Within())!);
    }

    [Fact]
    public async Task Server_commands_are_bridged_into_the_command_service_and_executed()
    {
        await using var env = new LspTestEnv();
        P.ExecuteCommandParams? seen = null;
        await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { ExecuteCommandProvider = new P.ExecuteCommandOptions { Commands = ["fake.doIt", "fake.other"] } };
            s.Requests[P.LspMethods.WorkspaceExecuteCommand] = p =>
            {
                seen = JsonSerializer.Deserialize<P.ExecuteCommandParams>(p, P.LspJson.Options);
                return Reply(Json("""{"done":true}"""));
            };
        });
        Assert.True(env.Commands.Has("fake.doIt"));
        Assert.True(env.Commands.Has("fake.other"));
        var result = await env.Commands.ExecuteAsync("fake.doIt", 5, "x");
        Assert.Equal("fake.doIt", seen!.Command);
        Assert.Equal(2, seen.Arguments!.Length);
        Assert.Equal(5, seen.Arguments[0].GetInt32());
        Assert.True(Assert.IsType<JsonElement>(result).GetProperty("done").GetBoolean());
    }
}
