using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Tests.Harness;
using Xunit;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Tests;

/// <summary>client/registerCapability and unregisterCapability: providers appear and disappear in the registry accordingly.</summary>
public class DynamicRegistrationTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<JsonElement> Register(FakeLspServer server, string id, string method, string? options = null) =>
        server.RequestAsync(P.LspMethods.ClientRegisterCapability, Json("{\"registrations\":[{\"id\":\"" + id + "\",\"method\":\"" + method + "\"" + (options is null ? "" : ",\"registerOptions\":" + options) + "}]}"));

    private static Task<JsonElement> Unregister(FakeLspServer server, string id, string method) =>
        server.RequestAsync(P.LspMethods.ClientUnregisterCapability, Json("{\"unregisterations\":[{\"id\":\"" + id + "\",\"method\":\"" + method + "\"}]}"));

    [Fact]
    public async Task Registering_and_unregistering_a_capability_adds_and_removes_the_provider()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s => s.Capabilities = new P.ServerCapabilities { TextDocumentSync = P.TextDocumentSyncKind.Incremental });
        Assert.Empty(env.Registry.GetProviders<IHoverProvider>(doc));
        var changes = new List<Type>();
        using var sub = env.Registry.ProvidersChanged.Subscribe(changes.Add);

        await Register(server, "h1", P.LspMethods.TextDocumentHover);
        Assert.Single(env.Registry.GetProviders<IHoverProvider>(doc));
        Assert.Contains(typeof(IHoverProvider), changes);

        await Unregister(server, "h1", P.LspMethods.TextDocumentHover);
        Assert.Empty(env.Registry.GetProviders<IHoverProvider>(doc));
    }

    [Fact]
    public async Task A_dynamically_registered_provider_actually_serves_requests()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { TextDocumentSync = P.TextDocumentSyncKind.Incremental };
            s.Requests[P.LspMethods.TextDocumentHover] = _ => Task.FromResult<object?>(new P.Hover { Contents = new P.MarkupContent { Kind = P.MarkupKind.PlainText, Value = "dynamic" } });
        });
        await Register(server, "h1", P.LspMethods.TextDocumentHover);
        var hover = await env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, new TextPosition(0, 0), Ct).Within();
        Assert.Equal("dynamic", hover!.Contents[0].Value);
    }

    [Fact]
    public async Task Static_and_dynamic_registrations_coexist_and_the_provider_lives_until_the_last_one_goes()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s => s.Capabilities = new P.ServerCapabilities { HoverProvider = true });
        await Register(server, "h-dyn", P.LspMethods.TextDocumentHover);
        await Unregister(server, "h-dyn", P.LspMethods.TextDocumentHover);
        Assert.Single(env.Registry.GetProviders<IHoverProvider>(doc)); // the static capability still backs it
        await Unregister(server, "static:" + P.LspMethods.TextDocumentHover, P.LspMethods.TextDocumentHover); // (server cannot normally do this; proves removal by id)
        Assert.Empty(env.Registry.GetProviders<IHoverProvider>(doc));
    }

    [Fact]
    public async Task Registration_options_such_as_trigger_characters_rebuild_the_provider()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s => s.Capabilities = new P.ServerCapabilities { TextDocumentSync = P.TextDocumentSyncKind.Incremental });
        await Register(server, "c1", P.LspMethods.TextDocumentCompletion, "{\"documentSelector\":null,\"triggerCharacters\":[\".\"]}");
        Assert.Equal(["."], env.Provider<ICompletionProvider>(doc).TriggerCharacters);
        await Register(server, "c2", P.LspMethods.TextDocumentCompletion, "{\"triggerCharacters\":[\"@\",\".\"]}");
        Assert.Equal([".", "@"], env.Provider<ICompletionProvider>(doc).TriggerCharacters.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Single(env.Registry.GetProviders<ICompletionProvider>(doc)); // one provider object, not two
    }

    [Fact]
    public async Task A_registration_selector_limits_which_documents_the_provider_answers_for()
    {
        await using var env = new LspTestEnv();
        var calls = 0;
        var (server, a) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { TextDocumentSync = P.TextDocumentSyncKind.Incremental };
            s.Requests[P.LspMethods.TextDocumentHover] = _ => { Interlocked.Increment(ref calls); return Task.FromResult<object?>(new P.Hover { Contents = new P.MarkupContent { Kind = P.MarkupKind.PlainText, Value = "x" } }); };
        }, path: "a.fake");
        var b = env.Open("b.fake", "other");
        await server.WaitForAsync(P.LspMethods.TextDocumentDidOpen, m => m.P<P.DidOpenTextDocumentParams>().TextDocument.Uri == b.Uri.AbsoluteUri);

        await Register(server, "h1", P.LspMethods.TextDocumentHover, "{\"documentSelector\":[{\"pattern\":\"**/a.fake\"}]}");
        var provider = env.Registry.GetProviders<IHoverProvider>(a)[0];
        Assert.NotNull(await provider.ProvideHoverAsync(a, new TextPosition(0, 0), Ct).Within());
        Assert.Null(await provider.ProvideHoverAsync(b, new TextPosition(0, 0), Ct).Within()); // selector excludes b.fake: no request at all
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Dynamic_sync_registration_can_turn_on_incremental_changes()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s => s.Capabilities = new P.ServerCapabilities { TextDocumentSync = P.TextDocumentSyncKind.None }, text: "abc");
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 3), "1")]);
        await Task.Delay(120, Ct);
        Assert.Equal(0, server.CountOf(P.LspMethods.TextDocumentDidChange));

        await Register(server, "sync", P.LspMethods.TextDocumentDidChange, "{\"documentSelector\":null,\"syncKind\":2}");
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 4), "2")]);
        await TestHelpers.EventuallyAsync(() => server.CountOf(P.LspMethods.TextDocumentDidChange) >= 1, "didChange after dynamic registration");
        await TestHelpers.EventuallyAsync(() => server.TextOf(doc.Uri.AbsoluteUri) == doc.GetText(),
            () => "mirror converges (full resync of the missed edit); doc=" + doc.GetText() + " server=" + server.TextOf(doc.Uri.AbsoluteUri) + " msgs=" +
                  string.Join(" | ", server.Received.Where(m => m.Method.StartsWith("textDocument/did")).Select(m => m.Method + ":" + m.Params)) + "\n" + env.Dump());
    }

    [Fact]
    public async Task Dynamic_didSave_registration_enables_save_notifications()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s => s.Capabilities = new P.ServerCapabilities { TextDocumentSync = P.TextDocumentSyncKind.Incremental });
        env.Documents.NotifySaved(doc);
        await Task.Delay(120, Ct);
        Assert.Equal(0, server.CountOf(P.LspMethods.TextDocumentDidSave));
        await Register(server, "save", P.LspMethods.TextDocumentDidSave, "{\"documentSelector\":null,\"includeText\":true}");
        env.Documents.NotifySaved(doc);
        var save = await server.WaitForAsync(P.LspMethods.TextDocumentDidSave);
        Assert.NotNull(save.P<P.DidSaveTextDocumentParams>().Text);
    }

    [Fact]
    public async Task Watched_file_registrations_filter_by_glob_and_kind_and_stop_on_unregister()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync();
        await Register(server, "w1", P.LspMethods.WorkspaceDidChangeWatchedFiles, "{\"watchers\":[{\"globPattern\":\"**/*.ts\",\"kind\":3},{\"globPattern\":{\"baseUri\":\"" + env.Dir.Uri.AbsoluteUri + "\",\"pattern\":\"conf/*.json\"}}]}");
        Assert.Equal(1, env.Workspace.ActiveWatches);

        env.Workspace.Raise(
            new FileChange(new Uri(env.Dir.Combine("src", "a.ts")), FileChangeKind.Changed),
            new FileChange(new Uri(env.Dir.Combine("src", "b.ts")), FileChangeKind.Deleted),   // kind 3 = create|change: delete is NOT wanted
            new FileChange(new Uri(env.Dir.Combine("src", "c.js")), FileChangeKind.Created),   // wrong extension
            new FileChange(new Uri(env.Dir.Combine("conf", "x.json")), FileChangeKind.Created),// relative-pattern match (all kinds)
            new FileChange(new Uri(env.Dir.Combine("other", "x.json")), FileChangeKind.Created));
        var msg = await server.WaitForAsync(P.LspMethods.WorkspaceDidChangeWatchedFiles);
        var events = msg.P<P.DidChangeWatchedFilesParams>().Changes;
        Assert.Equal(2, events.Length);
        Assert.Contains(events, e => e.Uri.EndsWith("/src/a.ts") && e.Type == P.FileChangeType.Changed);
        Assert.Contains(events, e => e.Uri.EndsWith("/conf/x.json") && e.Type == P.FileChangeType.Created);

        await Unregister(server, "w1", P.LspMethods.WorkspaceDidChangeWatchedFiles);
        Assert.Equal(0, env.Workspace.ActiveWatches);
    }

    [Fact]
    public async Task A_watched_file_registration_inside_a_cache_directory_asks_the_folder_watch_for_its_base()
    {
        // rust-analyzer watches each build script's OUT_DIR, which lies in target/: Cargo tags that as a cache directory, which the folder's
        // watch leaves out. The base is asked for as a wanted tree of the folder's watch (no watch of its own); a base outside such a part
        // (here the folder itself) is not.
        await using var env = new LspTestEnv();
        env.Dir.WriteFile("target/CACHEDIR.TAG", "Signature: 8a477f597d28d172789f06886806bc55\n");
        Directory.CreateDirectory(env.Dir.Combine("target", "debug", "build", "foo-1", "out"));
        var (server, _) = await env.StartAsync();
        var outDir = new Uri(env.Dir.Combine("target", "debug", "build", "foo-1", "out") + "/");
        await Register(server, "w2", P.LspMethods.WorkspaceDidChangeWatchedFiles,
            "{\"watchers\":[{\"globPattern\":{\"baseUri\":\"" + outDir.AbsoluteUri + "\",\"pattern\":\"**/*.rs\"}},{\"globPattern\":{\"baseUri\":\"" + env.Dir.Uri.AbsoluteUri + "\",\"pattern\":\"src/**/*.rs\"}}]}");
        Assert.Equal(1, env.Workspace.ActiveWatches);
        var wants = env.Workspace.WatchWants[^1];
        Assert.NotNull(wants);
        Assert.Equal([outDir.AbsoluteUri], wants.Trees.Select(static u => u.AbsoluteUri));
        Assert.Empty(wants.Directories);
    }

    [Fact]
    public async Task Dynamic_executeCommand_registration_bridges_commands()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync(s => s.Requests[P.LspMethods.WorkspaceExecuteCommand] = _ => Task.FromResult<object?>(1));
        Assert.False(env.Commands.Has("dyn.cmd"));
        await Register(server, "ec", P.LspMethods.WorkspaceExecuteCommand, "{\"commands\":[\"dyn.cmd\"]}");
        Assert.True(env.Commands.Has("dyn.cmd"));
        Assert.Equal(1, Assert.IsType<JsonElement>(await env.Commands.ExecuteAsync("dyn.cmd")).GetInt32());
    }

    [Fact]
    public async Task Dynamic_semantic_tokens_registration_provides_legend_and_range()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s => s.Capabilities = new P.ServerCapabilities { TextDocumentSync = P.TextDocumentSyncKind.Incremental });
        await Register(server, "st", "textDocument/semanticTokens", "{\"documentSelector\":null,\"legend\":{\"tokenTypes\":[\"keyword\",\"string\"],\"tokenModifiers\":[]},\"full\":true,\"range\":true}");
        Assert.Equal(["keyword", "string"], env.Provider<ISemanticTokensProvider>(doc).Legend.TokenTypes);
        Assert.Equal(["keyword", "string"], env.Provider<ISemanticTokensRangeProvider>(doc).Legend.TokenTypes);
    }

    [Fact]
    public async Task Dynamic_pull_diagnostics_registration_starts_pulling()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { TextDocumentSync = P.TextDocumentSyncKind.Incremental };
            s.Requests[P.LspMethods.TextDocumentDiagnostic] = _ => Task.FromResult<object?>(Json("{\"kind\":\"full\",\"items\":[{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}},\"message\":\"dyn\"}]}"));
        });
        await Register(server, "dg", P.LspMethods.TextDocumentDiagnostic, "{\"documentSelector\":null,\"interFileDependencies\":false,\"workspaceDiagnostics\":false}");
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Count == 1, "pulled after dynamic registration");
    }

    [Fact]
    public async Task Unsupported_registrations_and_unknown_unregistrations_are_harmless()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync();
        await Register(server, "x", "vendor/unknown");
        await Register(server, "y", P.LspMethods.WorkspaceDidChangeConfiguration);
        await Unregister(server, "never-registered", P.LspMethods.TextDocumentHover);
        Assert.Equal(LanguageServerState.Running, env.Manager.Servers.Single().State);
        Assert.Contains("unsupported method", env.Output.Text("Fake fake (LSP)"));
    }

    [Fact]
    public async Task Dynamic_registrations_vanish_when_the_server_stops()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync();
        await Register(server, "h", P.LspMethods.TextDocumentHover);
        await Register(server, "d", P.LspMethods.TextDocumentDefinition);
        await env.Manager.StopAsync("fake", Ct);
        Assert.Empty(env.Registry.GetAll<IHoverProvider>());
        Assert.Empty(env.Registry.GetAll<IDefinitionProvider>());
    }

    [Fact]
    public async Task Will_and_did_rename_files_are_forwarded_through_the_file_operation_participant()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities
            {
                TextDocumentSync = P.TextDocumentSyncKind.Incremental,
                Workspace = new P.ServerCapabilitiesWorkspace
                {
                    FileOperations = new P.FileOperationOptions
                    {
                        WillRename = new P.FileOperationRegistrationOptions { Filters = [new P.FileOperationFilter { Pattern = new P.FileOperationPattern { Glob = "**/*.fake" } }] },
                        DidRename = new P.FileOperationRegistrationOptions { Filters = [new P.FileOperationFilter { Pattern = new P.FileOperationPattern { Glob = "**/*.fake" } }] },
                    },
                },
            };
            s.Requests[P.LspMethods.WorkspaceWillRenameFiles] = _ => Task.FromResult<object?>(Json("{\"changes\":{\"file:///x/importer.fake\":[{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":3}},\"newText\":\"new\"}]}}"));
        });
        var files = new[] { (new Uri(env.Dir.Combine("old.fake")), new Uri(env.Dir.Combine("new.fake"))) };
        var edit = await env.Manager.WillRenameFilesAsync(files, Ct).Within();
        Assert.Equal("new", edit!.Changes[new Uri("file:///x/importer.fake")][0].NewText);
        await env.Manager.DidRenameFilesAsync(files, Ct);
        var did = await server.WaitForAsync(P.LspMethods.WorkspaceDidRenameFiles);
        Assert.EndsWith("new.fake", did.P<P.RenameFilesParams>().Files[0].NewUri);

        // files the server did not register for are not forwarded
        var other = new[] { (new Uri(env.Dir.Combine("a.txt")), new Uri(env.Dir.Combine("b.txt"))) };
        Assert.Null(await env.Manager.WillRenameFilesAsync(other, Ct).Within());
        Assert.Equal(1, server.CountOf(P.LspMethods.WorkspaceWillRenameFiles));
    }
}
