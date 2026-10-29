using System.Text.Json;
using CsCode.Lsp.Protocol;
using Xunit;
using Range = CsCode.Lsp.Protocol.Range;

namespace CsCode.Lsp.Tests;

/// <summary>Protocol-type tests against the shapes the LSP 3.17 specification itself documents (unions, optionals, unknown enum values).</summary>
public class ProtocolRoundTripTests
{
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, LspJson.Options)!;
    private static string Write<T>(T value) => JsonSerializer.Serialize(value, LspJson.Options);

    [Fact]
    public void Completion_result_array_or_list()
    {
        var arr = Read<OneOf<CompletionItem[], CompletionList>?>("""[{"label":"a"},{"label":"b","kind":3}]""");
        Assert.True(arr!.Value.IsT0);
        Assert.Equal(2, arr.Value.AsT0.Length);
        Assert.Equal(CompletionItemKind.Function, arr.Value.AsT0[1].Kind);

        var list = Read<OneOf<CompletionItem[], CompletionList>?>("""{"isIncomplete":true,"items":[{"label":"x"}]}""");
        Assert.True(list!.Value.IsT1);
        Assert.True(list.Value.AsT1.IsIncomplete);
        Assert.Single(list.Value.AsT1.Items);

        Assert.Null(Read<OneOf<CompletionItem[], CompletionList>?>("null"));
    }

    [Fact]
    public void CompletionItem_textEdit_is_TextEdit_or_InsertReplaceEdit()
    {
        var te = Read<CompletionItem>("""{"label":"a","textEdit":{"range":{"start":{"line":1,"character":2},"end":{"line":1,"character":4}},"newText":"abc"}}""");
        Assert.True(te.TextEdit!.Value.IsT0);
        Assert.Equal("abc", te.TextEdit.Value.AsT0.NewText);

        var ire = Read<CompletionItem>("""{"label":"a","textEdit":{"insert":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"replace":{"start":{"line":0,"character":0},"end":{"line":0,"character":3}},"newText":"zzz"}}""");
        Assert.True(ire.TextEdit!.Value.IsT1);
        Assert.Equal(3, ire.TextEdit.Value.AsT1.Replace.End.Character);
    }

    [Theory]
    [InlineData("""{"contents":{"kind":"markdown","value":"**x**"}}""", 0)]
    [InlineData("""{"contents":"plain"}""", 1)]
    [InlineData("""{"contents":{"language":"ts","value":"let a"}}""", 1)]
    [InlineData("""{"contents":["one",{"language":"ts","value":"two"}]}""", 2)]
    public void Hover_contents_union(string json, int expectedArm)
    {
        var h = Read<Hover>(json);
        Assert.Equal(expectedArm, h.Contents.Index);
    }

    [Fact]
    public void Definition_result_shapes()
    {
        const string loc = """{"uri":"file:///a.ts","range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}}}""";
        var single = Read<OneOf<OneOf<Location, Location[]>, LocationLink[]>?>(loc);
        Assert.True(single!.Value.AsT0.IsT0);
        var many = Read<OneOf<OneOf<Location, Location[]>, LocationLink[]>?>("[" + loc + "]");
        Assert.True(many!.Value.AsT0.IsT1);
        const string link = """{"targetUri":"file:///b.ts","targetRange":{"start":{"line":1,"character":0},"end":{"line":2,"character":0}},"targetSelectionRange":{"start":{"line":1,"character":1},"end":{"line":1,"character":2}}}""";
        var links = Read<OneOf<OneOf<Location, Location[]>, LocationLink[]>?>("[" + link + "]");
        Assert.True(links!.Value.IsT1);
        Assert.Equal("file:///b.ts", links.Value.AsT1[0].TargetUri);
    }

    [Fact]
    public void DocumentSymbol_hierarchical_vs_flat()
    {
        const string r = """{"start":{"line":0,"character":0},"end":{"line":1,"character":0}}""";
        var hier = Read<OneOf<SymbolInformation[], DocumentSymbol[]>?>(
            "[{\"name\":\"A\",\"kind\":5,\"range\":" + r + ",\"selectionRange\":" + r + ",\"children\":[{\"name\":\"m\",\"kind\":6,\"range\":" + r + ",\"selectionRange\":" + r + "}]}]");
        Assert.True(hier!.Value.IsT1);
        Assert.Single(hier.Value.AsT1[0].Children!);
        var flat = Read<OneOf<SymbolInformation[], DocumentSymbol[]>?>(
            "[{\"name\":\"A\",\"kind\":5,\"location\":{\"uri\":\"file:///a\",\"range\":" + r + "}}]");
        Assert.True(flat!.Value.IsT0);
    }

    [Fact]
    public void CodeAction_or_Command_array()
    {
        var items = Read<OneOf<Command, CodeAction>[]>("""[{"title":"Run","command":"x.run"},{"title":"Fix","kind":"quickfix","command":{"title":"Fix","command":"x.fix"}}]""");
        Assert.True(items[0].IsT0);
        Assert.Equal("x.run", items[0].AsT0.CommandValue);
        Assert.True(items[1].IsT1);
        Assert.Equal("quickfix", items[1].AsT1.Kind!.Value.Value);
        Assert.Equal("x.fix", items[1].AsT1.Command!.CommandValue);
    }

    [Fact]
    public void ServerCapabilities_union_properties()
    {
        var caps = Read<ServerCapabilities>("""
            {"textDocumentSync":2,"hoverProvider":true,"definitionProvider":{"workDoneProgress":true},
             "completionProvider":{"triggerCharacters":[".","("],"resolveProvider":true},
             "renameProvider":{"prepareProvider":true},
             "semanticTokensProvider":{"legend":{"tokenTypes":["class"],"tokenModifiers":[]},"full":{"delta":true},"range":true},
             "workspace":{"workspaceFolders":{"supported":true,"changeNotifications":true}}}
            """);
        Assert.Equal(TextDocumentSyncKind.Incremental, caps.TextDocumentSync!.Value.AsT1);
        Assert.True(caps.HoverProvider!.Value.AsT0);
        Assert.True(caps.DefinitionProvider!.Value.IsT1);
        Assert.Equal(["." , "("], caps.CompletionProvider!.TriggerCharacters!);
        Assert.True(caps.RenameProvider!.Value.AsT1.PrepareProvider);
        Assert.True(caps.SemanticTokensProvider!.Value.AsT0.Full!.Value.AsT1.Delta);

        var obj = Read<ServerCapabilities>("""{"textDocumentSync":{"openClose":true,"change":1,"save":{"includeText":true}}}""");
        Assert.Equal(TextDocumentSyncKind.Full, obj.TextDocumentSync!.Value.AsT0.Change);
    }

    [Fact]
    public void WorkspaceEdit_documentChanges_mixed_arms()
    {
        var we = Read<WorkspaceEdit>("""
            {"documentChanges":[
              {"textDocument":{"uri":"file:///a.ts","version":3},"edits":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}},"newText":"x"}]},
              {"kind":"create","uri":"file:///new.ts"},
              {"kind":"rename","oldUri":"file:///a.ts","newUri":"file:///b.ts","options":{"overwrite":true}},
              {"kind":"delete","uri":"file:///gone.ts"}]}
            """);
        var ch = we.DocumentChanges!;
        Assert.Equal([0, 1, 2, 3], ch.Select(c => c.Index).ToArray());
        Assert.Equal(3, ch[0].AsT0.TextDocument.Version);
        Assert.True(ch[2].AsT2.Options!.Overwrite);
    }

    [Fact]
    public void Unknown_enum_values_are_tolerated()
    {
        var kind = Read<CodeActionKind>("\"my.custom.kind\"");
        Assert.Equal("my.custom.kind", kind.Value);
        Assert.Equal("\"my.custom.kind\"", Write(kind));
        // numeric enums: unknown values pass through instead of throwing
        var item = Read<CompletionItem>("""{"label":"x","kind":99}""");
        Assert.Equal(99, (int)item.Kind!.Value);
        var d = Read<Diagnostic>("""{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"message":"m","severity":7}""");
        Assert.Equal(7, (int)d.Severity!.Value);
        Assert.Equal(SemanticTokenTypes.Class, Read<SemanticTokenTypes>("\"class\""));
        Assert.Equal("weird", Read<PositionEncodingKind>("\"weird\"").Value);
    }

    [Fact]
    public void Optional_properties_are_omitted_and_required_nullable_are_written()
    {
        var p = new InitializeParams { ProcessId = null, RootUri = null, Capabilities = new ClientCapabilities() };
        var json = Write(p);
        Assert.Contains("\"processId\":null", json);
        Assert.Contains("\"rootUri\":null", json);
        Assert.DoesNotContain("rootPath", json);
        Assert.DoesNotContain("trace", json);
        Assert.Equal("""{"line":1,"character":2}""", Write(new Position { Line = 1, Character = 2 }));
    }

    [Fact]
    public void ParameterInformation_label_string_or_offsets()
    {
        var a = Read<ParameterInformation>("""{"label":"x: number"}""");
        Assert.Equal("x: number", a.Label.AsT0);
        var b = Read<ParameterInformation>("""{"label":[3,9]}""");
        Assert.Equal([3, 9], b.Label.AsT1);
    }

    [Fact]
    public void ProgressToken_int_or_string()
    {
        Assert.Equal(5, Read<ProgressParams>("""{"token":5,"value":{"kind":"begin"}}""").Token.AsT0);
        Assert.Equal("tok", Read<ProgressParams>("""{"token":"tok","value":{"kind":"end"}}""").Token.AsT1);
        Assert.Equal("{\"token\":7,\"value\":{\"kind\":\"end\"}}", Write(new ProgressParams { Token = 7, Value = JsonDocument.Parse("""{"kind":"end"}""").RootElement }));
    }

    [Fact]
    public void SemanticTokens_data_is_uint_array()
    {
        var t = Read<SemanticTokens>("""{"resultId":"1","data":[0,0,5,1,0,0,6,3,2,1]}""");
        Assert.Equal(10, t.Data.Length);
        Assert.IsType<uint[]>(t.Data);
        var delta = Read<OneOf<SemanticTokens, SemanticTokensDelta>?>("""{"resultId":"2","edits":[{"start":0,"deleteCount":5,"data":[1,2]}]}""");
        Assert.True(delta!.Value.IsT1);
        Assert.Equal(5, delta.Value.AsT1.Edits[0].DeleteCount);
    }

    [Fact]
    public void PrepareRename_three_arms()
    {
        var a = Read<OneOf<Range, PrepareRenameResultOption2, PrepareRenameResultOption3>?>("""{"start":{"line":0,"character":0},"end":{"line":0,"character":3}}""");
        Assert.Equal(0, a!.Value.Index);
        var b = Read<OneOf<Range, PrepareRenameResultOption2, PrepareRenameResultOption3>?>("""{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":3}},"placeholder":"foo"}""");
        Assert.Equal(1, b!.Value.Index);
        var c = Read<OneOf<Range, PrepareRenameResultOption2, PrepareRenameResultOption3>?>("""{"defaultBehavior":true}""");
        Assert.Equal(2, c!.Value.Index);
    }

    [Fact]
    public void DocumentSelector_filters_merge_language_scheme_pattern()
    {
        var sel = Read<DocumentFilter[]>("""[{"language":"typescript","scheme":"file"},{"pattern":"**/*.tsx"},{"notebook":"jupyter","language":"python"}]""");
        Assert.Equal("typescript", sel[0].Language);
        Assert.Equal("**/*.tsx", sel[1].Pattern);
        Assert.Equal("jupyter", sel[2].Notebook!.Value.AsT0);
    }

    [Fact]
    public void TextDocumentContentChangeEvent_incremental_and_full()
    {
        var inc = new TextDocumentContentChangeEvent { Range = new Range { Start = new Position { Line = 1, Character = 0 }, End = new Position { Line = 1, Character = 2 } }, Text = "hi" };
        Assert.Equal("""{"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":2}},"text":"hi"}""", Write(inc));
        Assert.Equal("""{"text":"all"}""", Write(new TextDocumentContentChangeEvent { Text = "all" }));
    }

    [Fact]
    public void Diagnostic_code_and_related_information()
    {
        var d = Read<Diagnostic>("""
            {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"message":"bad","severity":1,
             "code":2304,"codeDescription":{"href":"https://x/y"},"source":"ts","tags":[1,2],
             "relatedInformation":[{"location":{"uri":"file:///r","range":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}}},"message":"see"}],
             "data":{"k":1}}
            """);
        Assert.Equal(2304, d.Code!.Value.AsT0);
        Assert.Equal("https://x/y", d.CodeDescription!.Href);
        Assert.Equal(2, d.Tags!.Length);
        Assert.Equal(1, d.Data!.Value.GetProperty("k").GetInt32());
        Assert.Equal("see", d.RelatedInformation![0].Message);
        Assert.Equal("ts", Read<Diagnostic>("""{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"message":"m","code":"E1","source":"ts"}""").Source);
    }

    [Fact]
    public void Registration_options_deserialize_by_method()
    {
        Assert.Equal(typeof(HoverRegistrationOptions), LspRegistrationOptionTypes.ByMethod[LspMethods.TextDocumentHover]);
        var reg = Read<RegistrationParams>("""{"registrations":[{"id":"1","method":"textDocument/completion","registerOptions":{"documentSelector":[{"language":"x"}],"triggerCharacters":["."]}}]}""");
        var opts = reg.Registrations[0].RegisterOptions!.Value.Deserialize<CompletionRegistrationOptions>(LspJson.Options)!;
        Assert.Equal("x", opts.DocumentSelector![0].Language);
        Assert.Equal(["."], opts.TriggerCharacters!);
    }

    [Fact]
    public void Typed_request_descriptors_exist_for_all_core_methods()
    {
        Assert.Equal("textDocument/completion", LspRequests.TextDocumentCompletion.Method);
        Assert.Equal("textDocument/didChange", LspNotifications.TextDocumentDidChange.Method);
        Assert.Equal("$/progress", LspNotifications.DollarProgress.Method);
        Assert.Equal("initialize", LspMethods.Initialize);
    }

    [Fact]
    public void Initialize_result_real_world_sample()
    {
        var r = Read<InitializeResult>("""
            {"capabilities":{"textDocumentSync":2,"completionProvider":{"triggerCharacters":[".","\"","'","/","@","<"],"resolveProvider":true},
              "codeActionProvider":{"codeActionKinds":["quickfix","refactor","source"],"resolveProvider":true},
              "executeCommandProvider":{"commands":["_typescript.applyWorkspaceEdit"]},"documentFormattingProvider":true,"inlayHintProvider":true,
              "signatureHelpProvider":{"triggerCharacters":["(",",","<"],"retriggerCharacters":[")"]}},
             "serverInfo":{"name":"typescript-language-server","version":"4.3.3"}}
            """);
        Assert.Equal("typescript-language-server", r.ServerInfo!.Name);
        Assert.Equal(3, r.Capabilities.CodeActionProvider!.Value.AsT1.CodeActionKinds!.Length);
        Assert.True(r.Capabilities.InlayHintProvider!.Value.IsT0);
    }
}
