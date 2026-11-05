using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Remote.Protocol;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>Every Core model (and protocol message) survives client → server → client with URIs mapped both ways.</summary>
public class ProtocolCodecTests
{
    private static readonly SshUriMapper Mapper = new(new RemoteAuthority("box", "me", 2222));
    private static readonly JsonSerializerOptions Client = RemoteJson.Create(Mapper);
    private static readonly JsonSerializerOptions Server = RemoteJson.Create();

    private static Uri U(string path) => Mapper.FromRemotePath(path);
    private static readonly TextRange R = TextRange.FromLines(1, 2, 3, 4);
    private static readonly JsonElement Data = JsonDocument.Parse("{\"opaque\":[1,\"two\",{\"three\":3}],\"uri\":\"file:///not/mapped\"}").RootElement.Clone();

    private static T RoundTrip<T>(T value)
    {
        var wire = JsonSerializer.Serialize(value, Client);
        Assert.DoesNotContain("ssh://", wire); // the server only ever sees its own file: URIs
        var onServer = JsonSerializer.Deserialize<T>(wire, Server)!;
        var back = JsonSerializer.Serialize(onServer, Server);
        Assert.Equal(wire, back);
        var onClient = JsonSerializer.Deserialize<T>(back, Client)!;
        Assert.Equal(wire, JsonSerializer.Serialize(onClient, Client));
        return onClient;
    }

    [Fact]
    public void Diagnostics_with_related_information_tags_and_links()
    {
        var d = new Diagnostic(R, "msg", DiagnosticSeverity.Warning, "TS2322", "ts", [DiagnosticTag.Unnecessary, DiagnosticTag.Deprecated],
            [new DiagnosticRelatedInformation(new Location(U("/src/a b.ts"), R), "see here")], new Uri("https://example.com/TS2322"));
        var back = RoundTrip(d);
        Assert.Equal(U("/src/a b.ts"), back.RelatedInformation![0].Location.Uri);
        Assert.Equal(new Uri("https://example.com/TS2322"), back.CodeDescriptionHref); // non-file URIs are left alone
        Assert.Equal(d.Tags, back.Tags);
        RoundTrip(new DiagPublishParams([new DiagnosticsEntry(U("/x.ts"), [d], 7)]));
    }

    [Fact]
    public void Completion_items_lists_and_contexts()
    {
        var item = new CompletionItem
        {
            Label = "greet", LabelDetail = "(name)", LabelDescription = "util", Kind = CompletionItemKind.Function, Detail = "fn", Documentation = MarkupContent.Markdown("**doc**"),
            SortText = "a", FilterText = "gr", InsertText = "greet($1)", InsertTextFormat = InsertTextFormat.Snippet, TextEdit = new TextEdit(R, "greet"),
            AdditionalTextEdits = [TextEdit.Insert(TextPosition.Zero, "import x;\n")], CommitCharacters = ["("], Preselect = true, Deprecated = true,
            Command = new Command("Trigger", "editor.action.triggerParameterHints", [Data]), Data = Data,
        };
        var back = RoundTrip(new CompletionList([item, item with { Label = "other" }], IsIncomplete: true));
        Assert.Equal("greet", back.Items[0].Label);
        Assert.Equal(Data.GetRawText(), back.Items[0].Data!.Value.GetRawText()); // opaque data (even URI-looking strings) is untouched
        RoundTrip(new CompletionContext(CompletionTriggerKind.TriggerCharacter, "."));
        RoundTrip(new ResolvableResult<CompletionList>("r1", back));
    }

    [Fact]
    public void Hover_signature_help_and_parameter_label_ranges()
    {
        RoundTrip(new Hover([MarkupContent.Plain("a"), MarkupContent.Markdown("`b`")], R));
        var sig = new SignatureInformation("f(a, b)", MarkupContent.Plain("doc"), [new ParameterInformation("a", null, (2, 3)), new ParameterInformation("b")], 1);
        var help = RoundTrip(new SignatureHelp([sig], 0, 1));
        Assert.Equal((2, 3), help.Signatures[0].Parameters![0].LabelRange); // tuples are arrays on the wire, not {}
        RoundTrip(new SignatureHelpContext(SignatureHelpTriggerKind.Invoked, null, true, help));
    }

    [Fact]
    public void Locations_links_highlights_and_symbols()
    {
        RoundTrip(new Location(U("/a.ts"), R));
        var link = RoundTrip(new LocationLink(U("/b c/d.ts"), R, R, R));
        Assert.Equal(U("/b c/d.ts"), link.TargetUri);
        RoundTrip<IReadOnlyList<LocationLink>>([link, new LocationLink(U("/e.ts"), R, R)]);
        RoundTrip(new DocumentHighlight(R, DocumentHighlightKind.Write));
        var child = new DocumentSymbol("inner", SymbolKind.Method, R, R, "detail");
        RoundTrip(new DocumentSymbol("outer", SymbolKind.Class, R, R, null, [child, child with { Name = "two", Children = [child] }], Deprecated: true));
        RoundTrip(new WorkspaceSymbol("Sym", SymbolKind.Function, new Location(U("/w.ts"), R), "container"));
        RoundTrip(new FoldingRange(1, 9, FoldingRangeKind.Region, "..."));
        RoundTrip(new SelectionRange(R, new SelectionRange(TextRange.FromLines(0, 0, 9, 0), new SelectionRange(TextRange.FromLines(0, 0, 99, 0)))));
    }

    [Fact]
    public void Workspace_edits_with_uri_keys_and_every_resource_operation()
    {
        var edit = new WorkspaceEdit(
            new Dictionary<Uri, IReadOnlyList<TextEdit>> { [U("/a.ts")] = [new TextEdit(R, "x")], [U("/dir with space/b.ts")] = [TextEdit.Delete(R)] },
            [new CreateFileOperation(U("/new.ts"), Overwrite: true), new RenameFileOperation(U("/old.ts"), U("/renamed.ts"), IgnoreIfExists: true),
             new DeleteFileOperation(U("/gone/"), Recursive: true, IgnoreIfNotExists: true)]);
        var back = RoundTrip(edit);
        Assert.Contains(U("/dir with space/b.ts"), back.Changes.Keys);
        Assert.IsType<CreateFileOperation>(back.ResourceOperations![0]);
        Assert.Equal(U("/renamed.ts"), Assert.IsType<RenameFileOperation>(back.ResourceOperations[1]).NewUri);
        Assert.True(Assert.IsType<DeleteFileOperation>(back.ResourceOperations[2]).Recursive);
        RoundTrip(new NavApplyEditParams(edit, "Rename"));
    }

    [Fact]
    public void Code_actions_lenses_tokens_hints_colors_and_hierarchies()
    {
        var diag = new Diagnostic(R, "d");
        var action = new CodeAction("Fix", "quickfix", [diag], true, "disabled because", new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>> { [U("/f.ts")] = [new TextEdit(R, "")] }),
            new Command("Run", "cmd.id", [Data]), Data);
        RoundTrip(action);
        RoundTrip(new ResolvableResult<IReadOnlyList<CodeAction>>("id", [action]));
        RoundTrip(new CodeActionContext([diag], ["quickfix", "refactor"], CodeActionTriggerKind.Automatic));
        RoundTrip(new PrepareRenameResult(R, "old"));
        RoundTrip(new FormattingOptions(2, false, true, true));
        RoundTrip(new SemanticTokensLegend(["class", "function"], ["static"]));
        RoundTrip(new SemanticTokens([0, 1, 2, 3, 4, uint.MaxValue], "r1"));
        RoundTrip(new InlayHint(new TextPosition(3, 4), ": string", InlayHintKind.Type, true, true, MarkupContent.Plain("tip")));
        RoundTrip(new CodeLens(R, new Command("3 refs", "show.refs", [Data]), Data));
        RoundTrip(new ColorInformation(R, 0.1, 0.2, 0.3, 1));
        RoundTrip<IReadOnlyList<ColorPresentation>>([new ColorPresentation("rgb(1, 2, 3)"), new ColorPresentation("#010203", new TextEdit(R, "#010203"), [new TextEdit(R, "@use x;")])]);
        RoundTrip(new LangArgs { Color = new ColorInformation(R, 1, 0.5, 0, 0.25) });
        RoundTrip(new LinkedEditingRanges([R, R], "[a-z]+"));
        var item = new HierarchyItem("f", SymbolKind.Function, U("/h.ts"), R, R, "detail", Data);
        RoundTrip(new CallHierarchyIncomingCall(item, [R]));
        RoundTrip(new CallHierarchyOutgoingCall(item, [R, R]));
    }

    [Fact]
    public void Language_server_definitions_and_infos()
    {
        var def = new LanguageServerDefinition("ts", "TypeScript", DocumentSelector.ForLanguages("typescript", "javascript"), "typescript-language-server", ["--stdio"],
            new Dictionary<string, string> { ["A"] = "b" }, "/w", ["tsconfig.json"], Data, Data, "cscode.languageServers.ts", false, 1024);
        var back = RoundTrip(def);
        Assert.Equal(2, back.Selector.Filters.Count);
        RoundTrip(new LspServersParams([new LanguageServerInfo("ts", "TypeScript", LanguageServerState.Unresponsive, U("/root/"), 42, "tsls", "5", "err")], [def]));
        RoundTrip(new DocumentSelector([new DocumentFilter("typescript", "file", "**/*.ts")]));
    }

    [Fact]
    public void Language_calls_documents_and_file_operations()
    {
        var args = new LangArgs
        {
            Position = new TextPosition(1, 2), Range = R, Positions = [TextPosition.Zero], Text = "new", Flag = true, DefinitionKind = DefinitionKind.TypeDefinition,
            CompletionContext = new CompletionContext(CompletionTriggerKind.Invoked), Formatting = new FormattingOptions(), Origin = new OriginToken("r", 3),
            HierarchyItem = new HierarchyItem("x", SymbolKind.Class, U("/x.ts"), R, R),
        };
        var back = RoundTrip(new LangInvokeParams(5, LangOps.Definition, U("/doc.ts"), 9, args));
        Assert.Equal(U("/doc.ts"), back.Uri);
        Assert.Equal(U("/x.ts"), back.Args.HierarchyItem!.Uri);
        RoundTrip(new DocOpenParams(U("/doc.ts"), "typescript", 3, "text\r\nwith\nlines"));
        RoundTrip(new DocChangeParams(U("/doc.ts"), 4, [new TextContentChange(R, 10, 5, "abc")]));
        var ops = RoundTrip(new FileOperationParams("willRename", [(U("/a.ts"), U("/b.ts"))], [U("/c.ts")]));
        Assert.Equal((U("/a.ts"), U("/b.ts")), ops.Renames![0]);
        RoundTrip(new WillSaveParams(U("/a.ts"), DocumentSaveReason.AfterDelay));
        RoundTrip(new LangRegistration(1, nameof(ICompletionProvider), DocumentSelector.ForLanguage("ts"), "owner",
            new ProviderInfo(["."], [","], [";"], new SemanticTokensLegend(["a"], ["b"]))));
    }

    [Fact]
    public void File_system_watch_search_and_window_messages()
    {
        RoundTrip(new WireStat(FileType.File | FileType.SymbolicLink, 12, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), true, "1-12"));
        RoundTrip(new ReadDirResult([new DirectoryEntry("a b", FileType.Directory), new DirectoryEntry("x", FileType.File)]));
        RoundTrip(new ReadResult(Payload.Encode(new byte[] { 1, 2, 3 }, false), 3, "e"));
        RoundTrip(new WatchChangesParams("w", [new WireFileChange("/a", FileChangeKind.Created), new WireFileChange("/b", FileChangeKind.Deleted)]));
        RoundTrip(new SearchBatch(["/a", "/b"], [new TextSearchMatch("/a", 1, 2, 3, "line")], true, null, 5));
        RoundTrip(new SearchTextParams(["/w"], "fo+", true, true, true, "**/*.ts", ["**/node_modules"], false, 10, 1000));
        RoundTrip(new ShowMessageParams(MessageSeverity.Warning, "m", [new MessageAction("Yes"), new MessageAction("No", true)]));
        RoundTrip(new QuickPickParams([new WireQuickPickItem("a", "d", "x", true, false)], new QuickPickOptions("t", "p", true, false, true), true));
        RoundTrip(new NavOpenParams(U("/n.ts"), R, true, false));
        RoundTrip(new HelloResult(1, "v", "Linux", "x64", 1, "/home", "/data", ["gzip"]));
    }
}
