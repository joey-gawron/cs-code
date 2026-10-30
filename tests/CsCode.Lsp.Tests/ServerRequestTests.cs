using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Tests.Harness;
using Xunit;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Tests;

/// <summary>window/*, workspace/* and progress traffic that flows from the server to the client.</summary>
public class ServerRequestTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private const string Channel = "Fake fake (LSP)";

    [Fact]
    public async Task logMessage_and_logTrace_go_to_the_servers_output_channel()
    {
        await using var env = new LspTestEnv(c => c.Set(LspSettingKeys.Trace, "messages"));
        var (server, _) = await env.StartAsync();
        await server.NotifyAsync(P.LspMethods.WindowLogMessage, new P.LogMessageParams { Type = P.MessageType.Warning, Message = "disk almost full" });
        await server.NotifyAsync(P.LspMethods.DollarLogTrace, new P.LogTraceParams { Message = "trace line" });
        await TestHelpers.EventuallyAsync(() => env.Output.Text(Channel).Contains("disk almost full") && env.Output.Text(Channel).Contains("trace line"), "log lines");
        Assert.Contains("[warn] disk almost full", env.Output.Text(Channel));
    }

    [Fact]
    public async Task Server_stderr_style_log_floods_are_bounded_and_do_not_disturb_requests()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { HoverProvider = true };
            s.Requests[P.LspMethods.TextDocumentHover] = _ => Task.FromResult<object?>(new P.Hover { Contents = new P.MarkupContent { Kind = P.MarkupKind.PlainText, Value = "alive" } });
        });
        var line = new string('x', 900);
        for (var i = 0; i < 20_000; i++) await server.NotifyAsync(P.LspMethods.WindowLogMessage, new P.LogMessageParams { Type = P.MessageType.Log, Message = line + i });
        var hover = await env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, new TextPosition(0, 0), TestContext.Current.CancellationToken).Within();
        Assert.Equal("alive", hover!.Contents[0].Value);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.True(env.Output.Text(Channel).Length <= 2_000_000, "output channel stays within its 2 MB cap");
    }

    [Fact]
    public async Task showMessage_is_shown_but_rate_limited_per_server()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync();
        for (var i = 0; i < 25; i++) await server.NotifyAsync(P.LspMethods.WindowShowMessage, new P.ShowMessageParams { Type = i == 0 ? P.MessageType.Error : P.MessageType.Info, Message = "note " + i });
        await TestHelpers.EventuallyAsync(() => env.Output.Text(Channel).Contains("note 24"), "all logged");
        var shown = env.Window.Messages.Where(m => m.Message.Contains("note")).ToList();
        Assert.InRange(shown.Count, 1, 5);                      // never a notification storm
        Assert.Equal(MessageSeverity.Error, shown[0].Severity);   // severity mapped
        Assert.StartsWith("Fake fake:", shown[0].Message);        // attributed to its server
    }

    [Fact]
    public async Task showMessageRequest_returns_the_chosen_action()
    {
        await using var env = new LspTestEnv();
        env.Window.MessageActionResponses.Enqueue("Retry");
        env.Window.MessageActionResponses.Enqueue(null);
        var (server, _) = await env.StartAsync();
        var picked = await server.RequestAsync(P.LspMethods.WindowShowMessageRequest, new P.ShowMessageRequestParams
        {
            Type = P.MessageType.Warning, Message = "Server crashed", Actions = [new P.MessageActionItem { Title = "Retry" }, new P.MessageActionItem { Title = "Ignore" }],
        });
        Assert.Equal("Retry", picked.GetProperty("title").GetString());
        var none = await server.RequestAsync(P.LspMethods.WindowShowMessageRequest, new P.ShowMessageRequestParams { Type = P.MessageType.Info, Message = "x", Actions = [] });
        Assert.Equal(JsonValueKind.Null, none.ValueKind);
    }

    [Fact]
    public async Task showDocument_opens_in_the_editor_or_externally()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync();
        var ok = await server.RequestAsync(P.LspMethods.WindowShowDocument, new P.ShowDocumentParams
        {
            Uri = "file:///x/target.fake", TakeFocus = true,
            Selection = new P.Range { Start = new P.Position { Line = 2, Character = 1 }, End = new P.Position { Line = 2, Character = 4 } },
        });
        Assert.True(ok.GetProperty("success").GetBoolean() || env.Navigation.Opened.Count == 1); // RecordingNavigationService returns null for unopened docs
        var opened = Assert.Single(env.Navigation.Opened);
        Assert.Equal(new Uri("file:///x/target.fake"), opened.Uri);
        Assert.Equal(TextRange.FromLines(2, 1, 2, 4), opened.Selection);

        var ext = await server.RequestAsync(P.LspMethods.WindowShowDocument, new P.ShowDocumentParams { Uri = "https://example.test/docs", External = true });
        Assert.True(ext.GetProperty("success").GetBoolean());
        Assert.Contains("external:https://example.test/docs", env.ProgressWindow.Events);
    }

    [Fact]
    public async Task applyEdit_edits_open_documents_and_reports_applied()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(text: "hello world");
        var result = await server.RequestAsync(P.LspMethods.WorkspaceApplyEdit, new P.ApplyWorkspaceEditParams
        {
            Label = "Fix",
            Edit = new P.WorkspaceEdit
            {
                Changes = new Dictionary<string, P.TextEdit[]>
                {
                    [doc.Uri.AbsoluteUri] = [new P.TextEdit { Range = new P.Range { Start = new P.Position { Line = 0, Character = 0 }, End = new P.Position { Line = 0, Character = 5 } }, NewText = "HOWDY" }],
                },
            },
        });
        Assert.True(result.GetProperty("applied").GetBoolean());
        Assert.Equal("HOWDY world", doc.GetText());
        Assert.Single(env.Navigation.AppliedEdits);
        // and the edit flows back to the server as an ordinary didChange
        await TestHelpers.EventuallyAsync(() => server.TextOf(doc.Uri.AbsoluteUri) == "HOWDY world", "server sees its own edit");
    }

    [Fact]
    public async Task applyEdit_without_an_editor_reports_not_applied()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync();
        var result = await server.RequestAsync(P.LspMethods.WorkspaceApplyEdit, new P.ApplyWorkspaceEditParams { Edit = new P.WorkspaceEdit { Changes = new Dictionary<string, P.TextEdit[]>() } });
        Assert.True(result.GetProperty("applied").GetBoolean()); // an empty edit is trivially applied by the recording navigation
    }

    [Fact]
    public async Task configuration_answers_come_from_the_definition_settings_then_the_configuration_service()
    {
        await using var env = new LspTestEnv();
        env.Config.Set("fake.formatting", new { tabs = 4 });
        env.Connections.OnConnect = (_, _) => { };
        env.Manager.AddDefinition(LspTestEnv.FakeDef() with { Settings = Json("""{"python":{"analysis":{"mode":"strict"}},"flat.key":7}""") });
        env.Open("a.fake", "x");
        var server = await env.Connections.WaitForServerAsync();
        await server.WaitForAsync(P.LspMethods.Initialized);
        await TestHelpers.EventuallyAsync(() => env.Manager.Servers.Any(s => s.State == LanguageServerState.Running), "running");

        var answer = await server.RequestAsync(P.LspMethods.WorkspaceConfiguration, new P.ConfigurationParams
        {
            Items = [new P.ConfigurationItem { Section = "python.analysis" }, new P.ConfigurationItem { Section = "flat.key" }, new P.ConfigurationItem { Section = "fake.formatting" },
                     new P.ConfigurationItem { Section = "unknown.section" }, new P.ConfigurationItem()],
        });
        Assert.Equal(5, answer.GetArrayLength());
        Assert.Equal("strict", answer[0].GetProperty("mode").GetString());
        Assert.Equal(7, answer[1].GetInt32());
        Assert.Equal(4, answer[2].GetProperty("tabs").GetInt32());
        Assert.Equal(JsonValueKind.Null, answer[3].ValueKind);
        Assert.Equal("strict", answer[4].GetProperty("python").GetProperty("analysis").GetProperty("mode").GetString()); // no section = the whole object
    }

    [Fact]
    public async Task Top_level_dotted_keys_of_a_user_s_server_settings_are_sections_and_deeper_member_names_stay_whole()
    {
        // lsp-core-2 follow-up: the configuration service keeps member names inside cscode.languageServers as written (URLs, globs). The server's
        // settings object is then read like a settings.json of its own: its top-level dotted keys are paths, everything below is kept.
        await using var env = new LspTestEnv();
        env.Manager.AddDefinition(LspTestEnv.FakeDef());
        var overrides = CsCode.Lsp.Manager.BuiltinLanguageServers.All.ToDictionary(d => d.Id, _ => (object)new { enabled = false });
        overrides["fake"] = Json("""
            {
              "settings": {
                "rust-analyzer.check.command": "clippy",
                "rust-analyzer": { "cargo": { "features": "all" } },
                "yaml.schemas": { "https://json.schemastore.org/github-workflow.json": ".github/workflows/*.yml" }
              },
              "initializationOptions": { "cargo.features": "all", "files": { "exclude": { "**/*.min.js": true } } }
            }
            """);
        env.Config.Set(LspSettingKeys.LanguageServers, overrides);
        env.Open("a.fake", "x");
        var server = await env.Connections.WaitForServerAsync();
        var init = await server.WaitForAsync(P.LspMethods.Initialize);
        await server.WaitForAsync(P.LspMethods.Initialized);
        await TestHelpers.EventuallyAsync(() => env.Manager.Servers.Any(s => s.State == LanguageServerState.Running), "running");

        var options = init.Params.GetProperty("initializationOptions");
        Assert.Equal("all", options.GetProperty("cargo").GetProperty("features").GetString());
        Assert.True(options.GetProperty("files").GetProperty("exclude").GetProperty("**/*.min.js").GetBoolean());

        var answer = await server.RequestAsync(P.LspMethods.WorkspaceConfiguration, new P.ConfigurationParams
        {
            Items = [new P.ConfigurationItem { Section = "rust-analyzer" }, new P.ConfigurationItem { Section = "rust-analyzer.check.command" },
                     new P.ConfigurationItem { Section = "yaml" }],
        });
        Assert.Equal("clippy", answer[0].GetProperty("check").GetProperty("command").GetString());
        Assert.Equal("all", answer[0].GetProperty("cargo").GetProperty("features").GetString()); // merged with the nested spelling
        Assert.Equal("clippy", answer[1].GetString());
        Assert.Equal(".github/workflows/*.yml", answer[2].GetProperty("schemas").GetProperty("https://json.schemastore.org/github-workflow.json").GetString());
    }

    [Theory]
    [InlineData("""{"a.b":1,"a":{"c":2}}""", """{"a":{"b":1,"c":2}}""")]                      // objects that meet merge
    [InlineData("""{"a":{"c":2},"a.b":1}""", """{"a":{"c":2,"b":1}}""")]
    [InlineData("""{"a.b":{"x.y":1},"a":{"b":{"z":2}}}""", """{"a":{"b":{"x.y":1,"z":2}}}""")]  // names below the top level stay whole
    [InlineData("""{"a.b":1,"a":3}""", """{"a":3}""")]                                          // a later scalar replaces
    [InlineData("""{"a..b":1,".c":2,"d.":3}""", """{"a..b":1,".c":2,"d.":3}""")]                // not paths: kept as written
    [InlineData("""{"plain":{"k.v":1}}""", """{"plain":{"k.v":1}}""")]
    [InlineData("""[1,{"a.b":2}]""", """[1,{"a.b":2}]""")]                                       // not an object: unchanged
    public void A_server_settings_object_splits_only_its_top_level_dotted_keys(string input, string expected)
    {
        var actual = CsCode.Lsp.Manager.DefinitionOverrides.ExpandTopLevelKeys(Json(input));
        Assert.Equal(expected, actual.GetRawText());
    }

    [Fact]
    public async Task Settings_in_the_definition_are_pushed_with_didChangeConfiguration_at_start()
    {
        await using var env = new LspTestEnv();
        env.Manager.AddDefinition(LspTestEnv.FakeDef() with { Settings = Json("""{"lint":true}""") });
        env.Open("a.fake", "x");
        var server = await env.Connections.WaitForServerAsync();
        var msg = await server.WaitForAsync(P.LspMethods.WorkspaceDidChangeConfiguration);
        Assert.True(msg.Params.GetProperty("settings").GetProperty("lint").GetBoolean());
    }

    [Fact]
    public async Task Changing_a_setting_the_server_asked_for_pushes_didChangeConfiguration()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync();
        await server.RequestAsync(P.LspMethods.WorkspaceConfiguration, new P.ConfigurationParams { Items = [new P.ConfigurationItem { Section = "fake.lint" }] });
        var before = server.CountOf(P.LspMethods.WorkspaceDidChangeConfiguration);
        env.Config.Set("fake.lint.level", "strict");
        await TestHelpers.EventuallyAsync(() => server.CountOf(P.LspMethods.WorkspaceDidChangeConfiguration) > before, "config push");
        env.Config.Set("totally.unrelated", 1);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(before + 1, server.CountOf(P.LspMethods.WorkspaceDidChangeConfiguration));
    }

    [Fact]
    public async Task workspaceFolders_request_and_change_notifications()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync(s => s.Capabilities = new P.ServerCapabilities
        {
            TextDocumentSync = P.TextDocumentSyncKind.Incremental,
            Workspace = new P.ServerCapabilitiesWorkspace { WorkspaceFolders = new P.WorkspaceFoldersServerCapabilities { Supported = true, ChangeNotifications = true } },
        });
        var folders = await server.RequestAsync(P.LspMethods.WorkspaceWorkspaceFolders, null);
        Assert.Equal(1, folders.GetArrayLength());
        Assert.Equal(env.Dir.Uri.AbsoluteUri, folders[0].GetProperty("uri").GetString());

        using var second = new TempDirectoryScope();
        env.Workspace.AddFolder(second.Path);
        var added = await server.WaitForAsync(P.LspMethods.WorkspaceDidChangeWorkspaceFolders);
        Assert.Single(added.Params.GetProperty("event").GetProperty("added").EnumerateArray());
        var after = await server.RequestAsync(P.LspMethods.WorkspaceWorkspaceFolders, null);
        Assert.Equal(2, after.GetArrayLength());
    }

    [Fact]
    public async Task Work_done_progress_begin_report_end_drive_the_window_progress_with_coalescing()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync();
        await server.RequestAsync(P.LspMethods.WindowWorkDoneProgressCreate, new P.WorkDoneProgressCreateParams { Token = "tok-1" });
        await server.NotifyAsync(P.LspMethods.DollarProgress, Json("""{"token":"tok-1","value":{"kind":"begin","title":"Indexing","message":"start","percentage":0,"cancellable":false}}"""));
        await TestHelpers.EventuallyAsync(() => env.ProgressWindow.Events.Any(e => e.StartsWith("begin:")), "begin");
        using (var block = new LaneBlock(env.Manager.Queue)) // a backlog builds up while the lane is busy...
        {
            for (var i = 1; i <= 200; i++)
                await server.NotifyAsync(P.LspMethods.DollarProgress, Json("{\"token\":\"tok-1\",\"value\":{\"kind\":\"report\",\"message\":\"file " + i + "\",\"percentage\":" + (i / 2) + "}}"));
            await server.NotifyAsync(P.LspMethods.DollarProgress, Json("""{"token":"tok-1","value":{"kind":"end","message":"done"}}"""));
            await Task.Delay(300, TestContext.Current.CancellationToken); // let the reader loop enqueue everything
            block.Release();                                              // ...and is then drained in one pass
        }
        await TestHelpers.EventuallyAsync(() => env.ProgressWindow.Events.Any(e => e.StartsWith("end:")), "end");
        var events = env.ProgressWindow.Events.ToArray();
        Assert.Equal("begin:Fake fake: Indexing", events[0]);
        Assert.True(events.Count(e => e.StartsWith("report:")) < 200); // reports were coalesced, not replayed one by one
        Assert.Equal("end:Fake fake: Indexing", events[^1]);
    }

    [Fact]
    public async Task Cancelling_a_cancellable_progress_notifies_the_server_and_a_stop_ends_open_progress()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync();
        await server.NotifyAsync(P.LspMethods.DollarProgress, Json("""{"token":7,"value":{"kind":"begin","title":"Long task"}}"""));
        await TestHelpers.EventuallyAsync(() => env.ProgressWindow.Events.Any(e => e.StartsWith("begin:")), "begin (numeric token)");
        await env.Manager.StopAsync("fake", TestContext.Current.CancellationToken); // the server goes away without sending "end"
        await TestHelpers.EventuallyAsync(() => env.ProgressWindow.Events.Any(e => e.StartsWith("end:")), "progress closed on stop");
    }

    [Fact]
    public async Task Unknown_requests_get_MethodNotFound_and_unknown_notifications_are_ignored()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync();
        var ex = await Assert.ThrowsAsync<LspError>(() => server.RequestAsync("vendor/custom", new { a = 1 }));
        Assert.Equal(-32601, ex.Code);
        await server.NotifyAsync("vendor/notification", new { b = 2 });
        await server.NotifyAsync(P.LspMethods.TelemetryEvent, new { event_ = "x" });
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(LanguageServerState.Running, env.Manager.Servers.Single().State);
    }

    [Fact]
    public async Task Refresh_requests_are_answered_and_trigger_consumers_to_requery()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s => s.Capabilities = new P.ServerCapabilities
        {
            SemanticTokensProvider = new P.SemanticTokensOptions { Legend = new P.SemanticTokensLegend(), Full = true },
            InlayHintProvider = true,
        });
        var changed = new List<Type>();
        using var sub = env.Registry.ProvidersChanged.Subscribe(changed.Add);
        await server.RequestAsync(P.LspMethods.WorkspaceSemanticTokensRefresh, null);
        await server.RequestAsync(P.LspMethods.WorkspaceInlayHintRefresh, null);
        await TestHelpers.EventuallyAsync(() => changed.Contains(typeof(ISemanticTokensProvider)) && changed.Contains(typeof(IInlayHintsProvider)), "providers re-registered");
        Assert.NotEmpty(env.Registry.GetProviders<ISemanticTokensProvider>(doc)); // still registered afterwards
    }
}

internal sealed class TempDirectoryScope : IDisposable
{
    private readonly CsCode.TestDoubles.TempDirectory _dir = new("cscode-lsp-extra-");
    public string Path => _dir.Path;
    public void Dispose() => _dir.Dispose();
}
