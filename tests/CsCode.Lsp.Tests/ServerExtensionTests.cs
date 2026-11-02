using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Extensions;
using CsCode.Lsp.Tests.Harness;
using CsCode.TestDoubles;
using Xunit;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Tests;

/// <summary>The per-server extension pattern (<see cref="LanguageServerExtension"/>) against the fake server.</summary>
public class ServerExtensionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Task<object?> Reply(object? value) => Task.FromResult(value);
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>An extension for the fake server: a capability, a notification, a command, and every hook recorded.</summary>
    private sealed class ProbeExtension : LanguageServerExtension
    {
        public const string Command = "probe.echo";
        public readonly List<(string Status, int ThreadId)> Statuses = [];
        public readonly List<string> Calls = [];
        public int Initialized, Stopped;
        public bool ThrowInAttach;
        public ServerExtensionHost? Host;
        public WeakReference<ServerExtensionSession>? LastSession;

        public override string Name => "probe";
        public override bool AppliesTo(LanguageServerDefinition definition) => definition.Id == "fake";
        public override void AddExperimentalCapabilities(JsonObject experimental) => experimental["probeStatus"] = true;

        public override void Register(ServerExtensionHost host)
        {
            Host = host;
            host.Disposables.Add(host.Services.Commands!.Register(Command, async inv =>
            {
                var session = host.ActiveSession(this) ?? throw new InvalidOperationException("no session");
                var doc = host.Services.Navigation!.ActiveDocument!;
                return await session.RequestAsync("probe/echo", doc, (id, _) => new { textDocument = id, text = inv.Arg<string>(0) }, inv.Cancellation);
            }));
        }

        public override void Attach(ServerExtensionSession session)
        {
            LastSession = new WeakReference<ServerExtensionSession>(session);
            session.State = new byte[1024]; // per-server state that must die with it
            session.OnNotification("probe/status", p =>
            {
                lock (Statuses) Statuses.Add((p.GetProperty("s").GetString()!, Environment.CurrentManagedThreadId));
            });
            session.OnNotification("probe/done", p => { lock (Statuses) Statuses.Add(("done:" + p.ValueKind, Environment.CurrentManagedThreadId)); });
            session.OnRequest("probe/ping", (p, _) => Task.FromResult<object?>(new { kind = p.ValueKind.ToString() }));
            session.OnRequest("probe/ask", (p, _) => Task.FromResult<object?>(new { answer = p.GetProperty("q").GetInt32() * 2, thread = Environment.CurrentManagedThreadId }));
            if (ThrowInAttach) throw new InvalidOperationException("attach failed on purpose");
        }

        public override void OnInitialized(ServerExtensionSession session) => Interlocked.Increment(ref Initialized);
        public override void OnStopped(ServerExtensionSession session) => Interlocked.Increment(ref Stopped);

        public override bool WantsRawHover => true;

        public override Hover? AdjustHover(ServerExtensionSession session, JsonElement raw, Hover? hover, Uri document) =>
            raw.TryGetProperty("extra", out var extra) && hover is not null
                ? hover with { Contents = [.. hover.Contents, new MarkupContent(extra.GetString()!, true) { IsTrusted = true }] }
                : hover;

        public override IReadOnlyList<CodeLens> AdjustCodeLenses(ServerExtensionSession session, IReadOnlyList<CodeLens> lenses, Uri document) =>
            [.. lenses.Where(l => l.Command?.CommandId != "drop.me")];

        public override bool RewritesAnswerOf(string method) => method == P.LspMethods.TextDocumentCodeAction;

        public override JsonElement RewriteAnswer(ServerExtensionSession session, string method, JsonElement raw)
        {
            lock (Calls) Calls.Add("rewrite:" + method);
            return JsonSerializer.SerializeToElement(JsonNode.Parse(raw.GetRawText()!.Replace("before", "after")));
        }
    }

    [Fact]
    public async Task The_extensions_experimental_capabilities_reach_initialize()
    {
        var ext = new ProbeExtension();
        await using var env = new LspTestEnv(extensions: [ext]);
        var (server, _) = await env.StartAsync();
        var init = server.Of(P.LspMethods.Initialize).Single();
        Assert.True(init.Params.GetProperty("capabilities").GetProperty("experimental").GetProperty("probeStatus").GetBoolean());
        await TestHelpers.EventuallyAsync(() => Volatile.Read(ref ext.Initialized) == 1, "OnInitialized");
    }

    [Fact]
    public async Task A_server_without_an_extension_gets_none()
    {
        var ext = new ProbeExtension();
        await using var env = new LspTestEnv(extensions: [ext]);
        env.Connections.OnConnect = (_, _) => { };
        env.Manager.AddDefinition(LspTestEnv.FakeDef("other", "other"));
        env.Open("b.other", "x", "other");
        var server = await env.Connections.WaitForServerAsync();
        var init = await server.WaitForAsync(P.LspMethods.Initialize);
        Assert.False(init.Params.GetProperty("capabilities").TryGetProperty("experimental", out var e) && e.TryGetProperty("probeStatus", out _));
        Assert.Null(ext.LastSession);
    }

    [Fact]
    public async Task Custom_notifications_reach_the_extension_on_the_language_lane()
    {
        var ext = new ProbeExtension();
        await using var env = new LspTestEnv(extensions: [ext]);
        var (server, _) = await env.StartAsync();
        await server.NotifyAsync("probe/status", new { s = "busy" });
        await server.NotifyAsync("probe/status", new { s = "ready" });
        await TestHelpers.EventuallyAsync(() => { lock (ext.Statuses) return ext.Statuses.Count == 2; }, "two statuses");
        lock (ext.Statuses)
        {
            Assert.Equal(["busy", "ready"], ext.Statuses.Select(s => s.Status));
            Assert.All(ext.Statuses, s => Assert.NotEqual(Environment.CurrentManagedThreadId, s.ThreadId));
        }
    }

    [Fact]
    public async Task Notifications_reach_the_extension_one_at_a_time_in_the_order_sent()
    {
        var ext = new ProbeExtension();
        await using var env = new LspTestEnv(extensions: [ext]);
        var (server, _) = await env.StartAsync();
        for (var i = 0; i < 200; i++) await server.NotifyAsync("probe/status", new { s = i.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        await TestHelpers.EventuallyAsync(() => { lock (ext.Statuses) return ext.Statuses.Count == 200; }, "200 statuses");
        lock (ext.Statuses) Assert.Equal(Enumerable.Range(0, 200).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), ext.Statuses.Select(s => s.Status));
    }

    [Fact]
    public async Task A_server_to_client_request_is_answered_by_the_extension_on_the_language_lane()
    {
        var ext = new ProbeExtension();
        await using var env = new LspTestEnv(extensions: [ext]);
        var (server, _) = await env.StartAsync();
        var answer = await server.RequestAsync("probe/ask", new { q = 21 }).Within();
        Assert.Equal(42, answer.GetProperty("answer").GetInt32());
        Assert.NotEqual(Environment.CurrentManagedThreadId, answer.GetProperty("thread").GetInt32());
    }

    [Fact]
    public async Task A_notification_or_request_without_params_reaches_the_extension()
    {
        var ext = new ProbeExtension();
        await using var env = new LspTestEnv(extensions: [ext]);
        var (server, _) = await env.StartAsync();
        await server.NotifyAsync("probe/done", null); // no "params" member at all (Roslyn's workspace/projectInitializationComplete)
        await TestHelpers.EventuallyAsync(() => { lock (ext.Statuses) return ext.Statuses.Any(s => s.Status == "done:Undefined"); }, "the notification without params");
        var answer = await server.RequestAsync("probe/ping", null).Within();
        Assert.Equal("Undefined", answer.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_command_registered_once_asks_the_server_of_the_active_document()
    {
        var ext = new ProbeExtension();
        await using var env = new LspTestEnv(extensions: [ext]);
        var (server, doc) = await env.StartAsync(s => s.Requests["probe/echo"] = p => Reply(new { echoed = p.GetProperty("text").GetString(), uri = p.GetProperty("textDocument").GetProperty("uri").GetString() }));
        env.Navigation.ActiveDocument = new EditorDocumentView(doc);
        env.Navigation.ActiveCaret = new TextPosition(0, 0);
        var answer = (JsonElement?)await env.Commands.ExecuteAsync(ProbeExtension.Command, "hi").AsTask().Within();
        Assert.Equal("hi", answer!.Value.GetProperty("echoed").GetString());
        Assert.Equal(server.Of(P.LspMethods.TextDocumentDidOpen).Single().Params.GetProperty("textDocument").GetProperty("uri").GetString(), answer.Value.GetProperty("uri").GetString());
        Assert.Same(ext.Host!.SessionFor(ext, doc.Uri), ext.Host.ActiveSession(ext));
    }

    [Fact]
    public async Task Hover_lens_and_answer_hooks_see_the_raw_answers()
    {
        var ext = new ProbeExtension();
        await using var env = new LspTestEnv(extensions: [ext]);
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { HoverProvider = true, CodeLensProvider = new P.CodeLensOptions(), CodeActionProvider = true };
            s.Requests[P.LspMethods.TextDocumentHover] = _ => Reply(Json("""{"contents":{"kind":"markdown","value":"hover"},"extra":"[link](command:x)"}"""));
            s.Requests[P.LspMethods.TextDocumentCodeLens] = _ => Reply(Json("""
                [{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"command":{"title":"keep","command":"keep.me"}},
                 {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"command":{"title":"drop","command":"drop.me"}}]
                """));
            s.Requests[P.LspMethods.TextDocumentCodeAction] = _ => Reply(Json("""[{"title":"before"}]"""));
        });
        var hover = await env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, new TextPosition(0, 1), Ct).Within();
        Assert.Equal(["hover", "[link](command:x)"], hover!.Contents.Select(c => c.Value));
        Assert.Equal([false, true], hover.Contents.Select(c => c.IsTrusted));

        var lenses = await env.Provider<ICodeLensProvider>(doc).ProvideCodeLensesAsync(doc, Ct).Within();
        Assert.Equal(["keep.me"], lenses!.Select(l => l.Command!.CommandId));

        var actions = await env.Provider<ICodeActionProvider>(doc).ProvideCodeActionsAsync(doc, TextRange.FromLines(0, 0, 0, 1), new CodeActionContext([]), Ct).Within();
        Assert.Equal("after", Assert.Single(actions!).Title);
        lock (ext.Calls) Assert.Contains("rewrite:" + P.LspMethods.TextDocumentCodeAction, ext.Calls);
    }

    [Fact]
    public async Task A_failing_hook_is_logged_and_the_server_still_works()
    {
        var ext = new ProbeExtension { ThrowInAttach = true };
        await using var env = new LspTestEnv(extensions: [ext]);
        var (server, _) = await env.StartAsync();
        await server.NotifyAsync("probe/status", new { s = "still" }); // added before the throw: still handled
        await TestHelpers.EventuallyAsync(() => { lock (ext.Statuses) return ext.Statuses.Count == 1; }, "status after a failed attach");
        Assert.Contains("probe Attach failed: attach failed on purpose", env.Output.Text("Fake fake (LSP)"));
    }

    [Fact]
    public async Task A_stopped_server_releases_its_session_and_the_command_finds_none()
    {
        var ext = new ProbeExtension();
        await using var env = new LspTestEnv(extensions: [ext]);
        var (_, doc) = await env.StartAsync();
        Assert.Single(ext.Host!.Sessions(ext));
        await env.Manager.StopAsync("fake", Ct).AsTask().Within();
        await TestHelpers.EventuallyAsync(() => Volatile.Read(ref ext.Stopped) == 1, "OnStopped");
        Assert.Empty(ext.Host.Sessions(ext));
        Assert.Null(ext.Host.SessionFor(ext, doc.Uri));
        // the connection's read loop ends shortly after the stop: give it a moment, collecting meanwhile
        await TestHelpers.EventuallyAsync(() =>
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            return !ext.LastSession!.TryGetTarget(out _);
        }, "the stopped server's extension session to be collected", TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Disposing_the_manager_removes_the_extensions_commands()
    {
        var ext = new ProbeExtension();
        var env = new LspTestEnv(extensions: [ext]);
        Assert.True(env.Commands.Has(ProbeExtension.Command));
        await env.DisposeAsync();
        Assert.False(env.Commands.Has(ProbeExtension.Command));
    }

    [Fact]
    public async Task Read_only_documents_are_bounded_read_only_and_readable_by_uri()
    {
        var store = new ReadOnlyDocuments();
        var uri = store.Put("rust-analyzer", "expand-macro", "vec!.rs", "expanded");
        Assert.Equal("lsp-readonly:/rust-analyzer/expand-macro/vec!.rs", uri.ToString());
        Assert.Equal("expanded", System.Text.Encoding.UTF8.GetString(await store.ReadFileAsync(uri, Ct)));
        Assert.True((await store.StatAsync(uri, Ct))!.IsReadOnly);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.WriteFileAsync(uri, new byte[1], ct: Ct).AsTask());
        for (var i = 0; i < ReadOnlyDocuments.Capacity; i++) store.Put("p", "k", $"f{i}.txt", "x");
        Assert.Null(await store.StatAsync(uri, Ct)); // the oldest went
    }
}
