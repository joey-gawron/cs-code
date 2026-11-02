using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Extensions.Clangd;
using CsCode.Lsp.Tests.Harness;
using CsCode.TestDoubles;
using Xunit;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Tests;

/// <summary>clangd's extension (<see cref="ClangdExtension"/>) against the fake server standing in for the built-in clangd definition.</summary>
public class ClangdExtensionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string Source = "#include \"a.h\"\n#if 0\nint dead;\n#endif\nint live;\n";

    private static LspTestEnv Make(Action<TestConfigurationService>? configure = null, ClangdExtension? extension = null) =>
        new(c =>
        {
            // only clangd among the built-ins: the extension applies to its definition, and the fake connection answers for it
            c.Set(LspSettingKeys.LanguageServers, Manager.BuiltinLanguageServers.All.Where(d => d.Id != ClangdExtension.ServerId)
                .ToDictionary(d => d.Id, _ => (object)new { enabled = false }));
            configure?.Invoke(c);
        }, builtins: true, extensions: [extension ?? new ClangdExtension()]);

    private static async Task<(FakeLspServer Server, InMemoryTextDocument Doc)> StartAsync(LspTestEnv env, Action<FakeLspServer>? configure = null)
    {
        env.Connections.OnConnect = (s, _) => configure?.Invoke(s);
        var doc = env.Open("src/main.c", Source, "c");
        var server = await env.Connections.WaitForServerAsync();
        await server.WaitForAsync(P.LspMethods.Initialized);
        await server.WaitForAsync(P.LspMethods.TextDocumentDidOpen);
        await env.WaitForStateAsync(ClangdExtension.ServerId, LanguageServerState.Running);
        return (server, doc);
    }

    private static string UriOf(FakeLspServer server) =>
        server.Of(P.LspMethods.TextDocumentDidOpen).Single().Params.GetProperty("textDocument").GetProperty("uri").GetString()!;

    [Fact]
    public async Task Initialize_offers_inactive_regions_and_asks_for_file_status_keeping_the_users_options()
    {
        await using var env = Make(c => c.Set(LspSettingKeys.LanguageServers, Manager.BuiltinLanguageServers.All.ToDictionary(d => d.Id, d => d.Id == ClangdExtension.ServerId
            ? (object)new { initializationOptions = new { fallbackFlags = new[] { "-std=c17" } } }
            : new { enabled = false })));
        var (server, _) = await StartAsync(env);
        var init = server.Of(P.LspMethods.Initialize).Single().Params;
        var capabilities = init.GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("textDocument").GetProperty("inactiveRegionsCapabilities").GetProperty("inactiveRegions").GetBoolean());
        Assert.True(capabilities.GetProperty("textDocument").TryGetProperty("hover", out _)); // the standard capabilities are all still there
        Assert.True(init.GetProperty("initializationOptions").GetProperty("clangdFileStatus").GetBoolean());
        Assert.Equal("-std=c17", init.GetProperty("initializationOptions").GetProperty("fallbackFlags")[0].GetString());
    }

    [Fact]
    public async Task Inactive_regions_reach_the_editor_provider_and_go_when_the_server_stops()
    {
        await using var env = Make();
        var (server, doc) = await StartAsync(env);
        var provider = env.Provider<IInactiveRegionsProvider>(doc);
        var changed = new List<Uri>();
        using var sub = provider.Changed.Subscribe(u => { lock (changed) changed.Add(u); });

        await server.NotifyAsync("textDocument/inactiveRegions", new
        {
            textDocument = new { uri = UriOf(server) },
            regions = new[] { new { start = new { line = 2, character = 0 }, end = new { line = 2, character = 9 } } },
        });
        await TestHelpers.EventuallyAsync(() => provider.GetInactiveRegions(doc.Uri).Count == 1, "the region");
        Assert.Equal(TextRange.FromLines(2, 0, 2, 9), provider.GetInactiveRegions(doc.Uri)[0]);
        Assert.Equal("int dead;", doc.GetText(provider.GetInactiveRegions(doc.Uri)[0]));
        lock (changed) Assert.Contains(doc.Uri, changed);

        // the next parse found none
        await server.NotifyAsync("textDocument/inactiveRegions", new { textDocument = new { uri = UriOf(server) }, regions = Array.Empty<object>() });
        await TestHelpers.EventuallyAsync(() => provider.GetInactiveRegions(doc.Uri).Count == 0, "no region");

        await server.NotifyAsync("textDocument/inactiveRegions", new
        {
            textDocument = new { uri = UriOf(server) },
            regions = new[] { new { start = new { line = 2, character = 0 }, end = new { line = 2, character = 9 } } },
        });
        await TestHelpers.EventuallyAsync(() => provider.GetInactiveRegions(doc.Uri).Count == 1, "the region again");
        lock (changed) changed.Clear();
        await env.Manager.StopAsync(ClangdExtension.ServerId, Ct);
        Assert.Empty(provider.GetInactiveRegions(doc.Uri));
        lock (changed) Assert.Contains(doc.Uri, changed); // the editors are told
        Assert.Empty(env.Registry.GetProviders<IInactiveRegionsProvider>(doc));
    }

    [Fact]
    public async Task The_active_files_status_shows_in_the_status_bar_until_the_server_stops()
    {
        await using var env = Make();
        var (server, doc) = await StartAsync(env);
        env.Navigation.ActiveDocument = new EditorDocumentView(doc);
        var other = new Uri(Path.Combine(env.Root, "src", "other.c"));
        await server.NotifyAsync("textDocument/clangd.fileStatus", new { uri = UriOf(server), state = "building AST" });
        await TestHelpers.EventuallyAsync(() => env.StatusBar.Get(FileStatusDisplay.ItemId)?.Text == "clangd: building AST", "building AST");
        await server.NotifyAsync("textDocument/clangd.fileStatus", new { uri = other.AbsoluteUri, state = "parsing includes" }); // not the active file
        await server.NotifyAsync("textDocument/clangd.fileStatus", new { uri = UriOf(server), state = "idle" });
        await TestHelpers.EventuallyAsync(() => env.StatusBar.Get(FileStatusDisplay.ItemId)?.Text == "clangd: idle", "idle");
        await env.Manager.StopAsync(ClangdExtension.ServerId, Ct);
        await TestHelpers.EventuallyAsync(() => env.StatusBar.Get(FileStatusDisplay.ItemId) is null, "the item goes with the server");
    }

    [Fact]
    public async Task Switch_between_source_and_header_sends_the_bare_identifier_and_opens_the_answer()
    {
        await using var env = Make();
        var header = Path.Combine(env.Root, "src", "a.h");
        var (server, doc) = await StartAsync(env, s => s.Requests["textDocument/switchSourceHeader"] = p => Task.FromResult<object?>(p.TryGetProperty("uri", out _) ? new Uri(header).AbsoluteUri : null));
        env.Navigation.ActiveDocument = new EditorDocumentView(doc);
        env.Navigation.ActiveCaret = new TextPosition(0, 0);
        await env.Commands.ExecuteAsync(ClangdExtension.SwitchHeaderSourceCommand).AsTask().Within();
        Assert.Equal(new Uri(header), Assert.Single(env.Navigation.Opened).Uri);
        Assert.Equal(UriOf(server), server.Of("textDocument/switchSourceHeader").Single().Params.GetProperty("uri").GetString());

        server.Requests["textDocument/switchSourceHeader"] = _ => Task.FromResult<object?>(null); // no counterpart
        await env.Commands.ExecuteAsync(ClangdExtension.SwitchHeaderSourceCommand).AsTask().Within();
        Assert.Single(env.Navigation.Opened);
        Assert.Contains(env.Window.StatusMessages, m => m.Contains("No matching header or source file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Show_ast_asks_for_the_caret_and_opens_the_tree_read_only()
    {
        await using var env = Make();
        var (server, doc) = await StartAsync(env, s => s.Requests["textDocument/ast"] = _ => Task.FromResult<object?>(Json("""
            {"role":"declaration","kind":"Var","detail":"live","range":{"start":{"line":4,"character":0},"end":{"line":4,"character":8}},
             "children":[{"role":"type","kind":"Builtin","detail":"int","arcana":"BuiltinType 0x1 'int'"}]}
            """)));
        env.Navigation.ActiveDocument = new EditorDocumentView(doc);
        env.Navigation.ActiveCaret = new TextPosition(4, 5);
        await env.Commands.ExecuteAsync(ClangdExtension.ShowAstCommand).AsTask().Within();
        var range = server.Of("textDocument/ast").Single().Params.GetProperty("range");
        Assert.Equal((4, 5), (range.GetProperty("start").GetProperty("line").GetInt32(), range.GetProperty("start").GetProperty("character").GetInt32()));
        var opened = Assert.Single(env.Navigation.Opened).Uri;
        Assert.Equal(VirtualDocumentUri.Scheme, opened.Scheme);
        var text = (await env.ReadOnlyDocuments.ProvideAsync(opened, Ct))!.Text;
        Assert.Equal("Var live  [line 5]\n  Builtin type int\n", text);
    }

    [Fact]
    public async Task A_stopped_servers_session_and_regions_are_not_retained()
    {
        var extension = new ClangdExtension();
        await using var env = Make(extension: extension);
        var (server, doc) = await StartAsync(env);
        env.Navigation.ActiveDocument = new EditorDocumentView(doc);
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var session = Assert.Single(extension.Host!.Sessions(extension));
            var provider = env.Provider<IInactiveRegionsProvider>(doc);
            await server.NotifyAsync("textDocument/inactiveRegions", new
            {
                textDocument = new { uri = UriOf(server) },
                regions = new[] { new { start = new { line = 2, character = 0 }, end = new { line = 2, character = 9 } } },
            });
            await server.NotifyAsync("textDocument/clangd.fileStatus", new { uri = UriOf(server), state = "idle" });
            await TestHelpers.EventuallyAsync(() => provider.GetInactiveRegions(doc.Uri).Count == 1 && env.StatusBar.Get(FileStatusDisplay.ItemId) is not null, "region and status");
            await env.Manager.StopAsync(ClangdExtension.ServerId, Ct);
            return [session, provider];
        });
    }

    [Fact]
    public void The_ast_is_labelled_like_vscode_clangds_view_and_bounded()
    {
        Assert.Equal("BinaryOperator", AstText.Label("expression", "BinaryOperator"));
        Assert.Equal("Builtin type", AstText.Label("type", "Builtin"));
        var deep = """{"role":"statement","kind":"Compound","children":[""" + string.Join(",", Enumerable.Repeat("""{"role":"statement","kind":"Null"}""", AstText.MaxNodes + 5)) + "]}";
        var text = AstText.Render(Json(deep));
        Assert.Equal(AstText.MaxNodes + 1, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length); // MaxNodes nodes and the "more" line
    }
}
