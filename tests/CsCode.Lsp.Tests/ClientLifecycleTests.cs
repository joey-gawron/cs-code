using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Lsp.Protocol;
using CsCode.Lsp.Tests.Harness;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>Start-up handshake, lazy start, shutdown sequence, crash/restart/backoff, missing binaries.</summary>
public class ClientLifecycleTests
{
    [Fact]
    public async Task Server_starts_lazily_on_first_matching_document_with_the_correct_handshake_order()
    {
        await using var env = new LspTestEnv();
        env.AddFake();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, env.Connections.ConnectCount); // nothing starts before a matching document exists

        env.Open("other.txt", "x", language: "plaintext");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, env.Connections.ConnectCount); // non-matching language: still nothing

        env.Open("a.fake", "hi");
        var server = await env.Connections.WaitForServerAsync();
        await server.WaitForAsync(LspMethods.TextDocumentDidOpen);

        var order = server.Received.Select(m => m.Method).ToList();
        Assert.Equal([LspMethods.Initialize, LspMethods.Initialized, LspMethods.TextDocumentDidOpen], order);
        Assert.Equal(1, env.Connections.ConnectCount);
    }

    [Fact]
    public async Task Initialize_params_carry_process_root_folders_and_honest_capabilities()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync();
        var init = server.Of(LspMethods.Initialize).Single().P<InitializeParams>();

        Assert.Equal(Environment.ProcessId, init.ProcessId);
        Assert.Equal("CsCode", init.ClientInfo!.Name);
        Assert.StartsWith("file://", init.RootUri);
        Assert.Single(init.WorkspaceFolders!);
        Assert.Equal(init.RootUri, init.WorkspaceFolders![0].Uri);
        Assert.Equal([PositionEncodingKind.UTF16], init.Capabilities.General!.PositionEncodings!);
        Assert.True(init.Capabilities.TextDocument!.Synchronization!.DidSave);
        Assert.True(init.Capabilities.Window!.WorkDoneProgress);
        Assert.True(init.Capabilities.Workspace!.Configuration);
        Assert.True(init.Capabilities.TextDocument.Completion!.CompletionItem!.SnippetSupport);
        Assert.True(init.Capabilities.TextDocument.Rename!.PrepareSupport);
        Assert.NotNull(init.Capabilities.TextDocument.SemanticTokens);
        Assert.Equal(TraceValues.Off, init.Trace);
    }

    [Fact]
    public async Task Required_but_null_initialize_fields_are_written_explicitly()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync();
        var raw = server.Of(LspMethods.Initialize).Single().Params;
        Assert.True(raw.TryGetProperty("processId", out _));
        Assert.True(raw.TryGetProperty("rootUri", out var rootUri) && rootUri.ValueKind != System.Text.Json.JsonValueKind.Undefined);
        Assert.True(raw.TryGetProperty("capabilities", out _));
    }

    [Fact]
    public async Task Server_info_and_state_are_surfaced_through_the_manager()
    {
        await using var env = new LspTestEnv();
        await env.StartAsync();
        var info = env.Manager.Servers.Single();
        Assert.Equal("fake", info.Id);
        Assert.Equal(LanguageServerState.Running, info.State);
        Assert.Equal("fake-server", info.ServerName);
        Assert.Equal("1.0", info.ServerVersion);
        Assert.NotNull(info.RootUri);
        Assert.Null(info.LastError);
    }

    [Fact]
    public async Task Stop_runs_shutdown_then_exit_and_leaves_state_stopped()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync();
        await env.Manager.StopAsync("fake", TestContext.Current.CancellationToken);
        Assert.True(server.Shutdown);
        Assert.True(server.ExitReceived);
        var methods = server.Received.Select(m => m.Method).ToList();
        Assert.True(methods.IndexOf(LspMethods.Shutdown) < methods.IndexOf(LspMethods.Exit));
        Assert.Equal(LanguageServerState.Stopped, env.Manager.Servers.Single().State);
    }

    [Fact]
    public async Task A_stopped_server_is_not_auto_restarted_by_new_documents_until_started_again()
    {
        await using var env = new LspTestEnv();
        await env.StartAsync();
        await env.Manager.StopAsync("fake", TestContext.Current.CancellationToken);
        env.Open("b.fake", "more");
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(1, env.Connections.ConnectCount);
        await env.Manager.StartAsync("fake", TestContext.Current.CancellationToken);
        Assert.Equal(2, env.Connections.ConnectCount);
        await env.WaitForStateAsync("fake", LanguageServerState.Running);
    }

    [Fact]
    public async Task Restart_creates_a_new_session_and_reopens_documents()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(text: "restart me");
        await env.Manager.RestartAsync("fake", TestContext.Current.CancellationToken);
        var second = await env.Connections.WaitForServerAsync(1);
        await second.WaitForAsync(LspMethods.TextDocumentDidOpen);
        Assert.True(server.Shutdown);
        Assert.Equal("restart me", second.TextOf(doc.Uri.AbsoluteUri));
        Assert.Equal(LanguageServerState.Running, (await env.WaitForStateAsync("fake", LanguageServerState.Running)).State);
    }

    [Fact]
    public async Task Crash_triggers_automatic_restart_and_documents_are_resynced()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(text: "survive");
        doc.ApplyEdits([new CsCode.Core.Text.TextEdit(CsCode.Core.Text.TextRange.At(0, 7), "!")]);
        server.Crash();
        var second = await env.Connections.WaitForServerAsync(1);
        await second.WaitForAsync(LspMethods.TextDocumentDidOpen);
        Assert.Equal("survive!", second.TextOf(doc.Uri.AbsoluteUri));
        await env.WaitForStateAsync("fake", LanguageServerState.Running);
    }

    [Fact]
    public async Task Repeated_crashes_back_off_then_fault_with_one_restart_message()
    {
        await using var env = new LspTestEnv(c => c.Set(LspSettingKeys.MaxRestarts, 3));
        env.Connections.OnConnect = (s, _) => { /* each server crashes right after initialize */ s.NotificationHandlers[LspMethods.Initialized] = _ => s.Crash(); };
        env.AddFake();
        env.Open("a.fake", "x");

        var info = await env.WaitForStateAsync("fake", LanguageServerState.Faulted);
        Assert.NotNull(info.LastError);
        Assert.Equal(4, env.Connections.ConnectCount); // initial start + exactly 3 restarts
        await TestHelpers.EventuallyAsync(() => env.Window.Messages.Count >= 1, "fault message");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Single(env.Window.Messages, m => m.Severity == MessageSeverity.Error); // one message, not a storm
        Assert.Equal(4, env.Connections.ConnectCount);
    }

    [Fact]
    public async Task Choosing_Restart_on_the_fault_message_restarts_the_server()
    {
        await using var env = new LspTestEnv(c => c.Set(LspSettingKeys.MaxRestarts, 1));
        // Sessions 0 and 1 crash (1 restart allowed => Faulted after the 2nd crash); session 2, started by choosing "Restart", is healthy.
        env.Connections.OnConnect = (s, i) => { if (i < 2) s.NotificationHandlers[LspMethods.Initialized] = _ => s.Crash(); };
        env.Window.MessageActionResponses.Enqueue("Restart");
        env.AddFake();
        env.Open("a.fake", "x");
        // A session that crashes right after `initialized` is transiently "Running": wait for the third (healthy) session itself.
        await env.WaitForAsync(() => env.Connections.ConnectCount == 3 ? env.Manager.Servers.FirstOrDefault(s => s.State == LanguageServerState.Running) : null, "third session Running");
        Assert.Equal(3, env.Connections.ConnectCount);
        Assert.Single(env.Window.Messages, m => m.Severity == MessageSeverity.Error && m.Message.Contains("keeps crashing"));
    }

    [Fact]
    public async Task Missing_binary_leaves_server_stopped_with_one_friendly_message()
    {
        await using var env = new LspTestEnv();
        env.Connections.FailNext(100);
        env.AddFake();
        env.Open("a.fake", "x");
        await TestHelpers.EventuallyAsync(() => env.Window.Messages.Count == 1, "friendly message");
        env.Open("b.fake", "y");
        env.Open("c.fake", "z");
        await Task.Delay(200, TestContext.Current.CancellationToken);
        var info = env.Manager.Servers.Single();
        Assert.Equal(LanguageServerState.Stopped, info.State);
        Assert.Contains("was not found", info.LastError);
        Assert.Single(env.Window.Messages);
        Assert.Contains("fake-server-fake", env.Window.Messages[0].Message);
        Assert.Single(env.Connections.Requests); // no retry storm either
    }

    [Fact]
    public async Task Initialize_failure_is_treated_as_a_crash_and_retried()
    {
        await using var env = new LspTestEnv();
        env.Connections.OnConnect = (s, i) =>
        {
            if (i == 0) s.InitializeHook = _ => throw new LspError(-32603, "boom");
        };
        env.AddFake();
        env.Open("a.fake", "x");
        await env.Connections.WaitForServerAsync(1);
        await env.WaitForStateAsync("fake", LanguageServerState.Running);
    }

    [Fact]
    public async Task Initialize_timeout_is_reported_and_retried()
    {
        await using var env = new LspTestEnv(c => c.Set(LspSettingKeys.InitializeTimeoutMs, 300));
        env.Connections.OnConnect = (s, i) =>
        {
            if (i == 0) s.InitializeHook = async _ => { await Task.Delay(5000); return null; };
        };
        env.AddFake();
        env.Open("a.fake", "x");
        await env.Connections.WaitForServerAsync(1);
        await env.WaitForStateAsync("fake", LanguageServerState.Running);
    }

    [Fact]
    public async Task Disposing_the_manager_shuts_servers_down()
    {
        var env = new LspTestEnv();
        var (server, _) = await env.StartAsync();
        await env.DisposeAsync();
        Assert.True(server.ExitReceived);
        Assert.True(server.ClosedTask.IsCompleted);
    }

    [Fact]
    public async Task Definitions_can_be_overridden_and_disabled_from_the_setting()
    {
        await using var env = new LspTestEnv();
        env.AddFake();
        Assert.Contains(env.Manager.Definitions, d => d.Id == "fake" && d.Command == "fake-server-fake");
        env.Config.Set(LspSettingKeys.LanguageServers, new
        {
            fake = new { command = "my-fake", args = new[] { "--x" }, env = new { A = "1" } },
            brand_new = new { command = "other", selector = new[] { "lang2" }, displayName = "Brand New" },
            typescript = new { enabled = false },
        });
        await TestHelpers.EventuallyAsync(() => env.Manager.Definitions.Any(d => d.Id == "brand_new"), "override applied");
        var fake = env.Manager.Definitions.Single(d => d.Id == "fake");
        Assert.Equal("my-fake", fake.Command);
        Assert.Equal(["--x"], fake.Args);
        Assert.Equal("1", fake.Env!["A"]);
        var created = env.Manager.Definitions.Single(d => d.Id == "brand_new");
        Assert.Equal("Brand New", created.DisplayName);
        Assert.True(created.Selector.Score(new Uri("file:///x"), "lang2") > 0);
    }

    [Fact]
    public async Task Removing_a_definition_stops_its_servers()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync();
        // dispose the registration token returned by AddDefinition (the first one registered by StartAsync)
        var token = env.Manager.AddDefinition(LspTestEnv.FakeDef("temp", "templang"));
        env.Open("t.tmp", "x", "templang");
        var temp = await env.Connections.WaitForServerAsync(1);
        await temp.WaitForAsync(LspMethods.TextDocumentDidOpen);
        token.Dispose();
        await TestHelpers.EventuallyAsync(() => temp.ExitReceived, "temp server stopped");
        Assert.DoesNotContain(env.Manager.Servers, s => s.Id == "temp");
        Assert.False(server.ExitReceived); // the other definition is untouched
    }
}
