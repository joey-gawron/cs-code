using System.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Tests.Harness;
using CsCode.TestDoubles;
using Xunit;
using TextEdit = CsCode.Core.Text.TextEdit;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Tests;

/// <summary>Nothing may be retained once its owner is gone; threads, handles and memory stay flat under churn.</summary>
public class LeakAndSoakTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int OpenFileDescriptors() => OperatingSystem.IsLinux() ? Directory.GetFileSystemEntries("/proc/self/fd").Length : 0;

    [Fact]
    public async Task A_closed_document_is_not_retained_by_the_language_client_after_requests_edits_and_diagnostics()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { TextDocumentSync = P.TextDocumentSyncKind.Incremental, HoverProvider = true, CompletionProvider = new P.CompletionOptions() };
            s.Requests[P.LspMethods.TextDocumentHover] = _ => Task.FromResult<object?>(new P.Hover { Contents = new P.MarkupContent { Kind = P.MarkupKind.PlainText, Value = "h" } });
            s.Requests[P.LspMethods.TextDocumentCompletion] = _ => Task.FromResult<object?>(new P.CompletionList { Items = [new P.CompletionItem { Label = "c" }] });
        });

        await LeakAssert.NotRetainedAsync(async () =>
        {
            var doc = new InMemoryTextDocument(new Uri(env.Dir.WriteFile("leak.fake", "some text")), "fake", "some text");
            var reg = env.Documents.Register(doc);
            await server.WaitForAsync(P.LspMethods.TextDocumentDidOpen, m => m.P<P.DidOpenTextDocumentParams>().TextDocument.Uri == doc.Uri.AbsoluteUri);
            for (var i = 0; i < 5; i++) doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "x")]);
            await env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, new TextPosition(0, 0), Ct).Within();
            await env.Provider<ICompletionProvider>(doc).ProvideCompletionAsync(doc, new TextPosition(0, 1), new CompletionContext(CompletionTriggerKind.Invoked), Ct).Within();
            await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, System.Text.Json.JsonDocument.Parse("{\"uri\":\"" + doc.Uri.AbsoluteUri + "\",\"diagnostics\":[{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}},\"message\":\"m\"}]}").RootElement.Clone());
            await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Count == 1, "diagnostic");
            reg.Dispose();
            await server.WaitForAsync(P.LspMethods.TextDocumentDidClose, m => m.P<P.DidCloseTextDocumentParams>().TextDocument.Uri == doc.Uri.AbsoluteUri);
            await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Count == 0, "diagnostics cleared");
            return [doc];
        });
        Assert.Equal(1, env.Manager.Slots.Single().DocumentCount); // only the document opened by StartAsync remains
    }

    [Fact]
    public async Task A_stopped_session_releases_its_client_providers_and_registrations()
    {
        await using var env = new LspTestEnv();
        env.AddFake();
        env.Connections.OnConnect = (s, _) => s.Capabilities = new P.ServerCapabilities { HoverProvider = true, DefinitionProvider = true, CompletionProvider = new P.CompletionOptions() };
        var doc = env.Open("a.fake", "x");

        await LeakAssert.NotRetainedAsync(async () =>
        {
            await TestHelpers.EventuallyAsync(() => env.Manager.Servers.Any(s => s.State == LanguageServerState.Running), "running");
            var provider = env.Provider<IHoverProvider>(doc);
            var completion = env.Provider<ICompletionProvider>(doc);
            var client = env.Manager.Slots.Single().Client!;
            await provider.ProvideHoverAsync(doc, new TextPosition(0, 0), Ct).Within();
            await env.Manager.StopAsync("fake", Ct);
            return [provider, completion, client];
        });
        Assert.Empty(env.Registry.GetAll<IHoverProvider>());
        Assert.Empty(env.Registry.GetAll<ICompletionProvider>());
    }

    [Fact]
    public async Task Open_change_close_two_thousand_documents_keeps_everything_flat()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync(s => s.Capabilities = new P.ServerCapabilities { TextDocumentSync = P.TextDocumentSyncKind.Incremental, HoverProvider = true });
        var slot = env.Manager.Slots.Single();
        var queue = env.Manager.Queue;
        GC.Collect(); GC.WaitForPendingFinalizers();
        var threadsBefore = Process.GetCurrentProcess().Threads.Count;
        var fdsBefore = OpenFileDescriptors();
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);

        // Churn in batches: documents stay open until the server has seen them (one closed before the lane routed it is simply never opened).
        const int N = 2000, Batch = 100;
        for (var batch = 0; batch < N / Batch; batch++)
        {
            var docs = new List<(InMemoryTextDocument Doc, IDisposable Reg)>(Batch);
            for (var i = 0; i < Batch; i++)
            {
                var n = batch * Batch + i;
                var doc = new InMemoryTextDocument(new Uri("file:///soak/doc" + n + ".fake"), "fake", "line one\nline two\n" + n);
                docs.Add((doc, env.Documents.Register(doc)));
            }
            await TestHelpers.EventuallyAsync(() => slot.DocumentCount == 1 + Batch && server.CountOf(P.LspMethods.TextDocumentDidOpen) >= 1 + (batch + 1) * Batch,
                () => $"batch {batch} opened (docs {slot.DocumentCount}, opens {server.CountOf(P.LspMethods.TextDocumentDidOpen)})", TimeSpan.FromSeconds(30));
            foreach (var (doc, _) in docs) doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "// ")]);
            foreach (var (_, reg) in docs) reg.Dispose();
            await TestHelpers.EventuallyAsync(() => slot.DocumentCount == 1, () => $"batch {batch} closed (docs {slot.DocumentCount})", TimeSpan.FromSeconds(30));
        }

        await TestHelpers.EventuallyAsync(() => server.CountOf(P.LspMethods.TextDocumentDidClose) >= N, () => $"all closes delivered ({server.CountOf(P.LspMethods.TextDocumentDidClose)}/{N}, depth {queue.Depth})", TimeSpan.FromSeconds(60));
        await TestHelpers.EventuallyAsync(() => queue.Depth == 0 && slot.DocumentCount == 1, () => $"drained: depth {queue.Depth}, docs {slot.DocumentCount}");
        Assert.Equal(1, env.Manager.RoutedDocumentCount);                                       // only the original document is routed
        Assert.Equal(N + 1, server.Documents.Count);                                              // the server saw them all...
        Assert.Equal(N, server.Documents.Values.Count(d => !d.Open));                             // ...and everything but the original was closed
        Assert.Equal(N + 1, server.Of(P.LspMethods.TextDocumentDidOpen).Count());
        Assert.Equal(LanguageServerState.Running, slot.State);

        var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        var threadsAfter = Process.GetCurrentProcess().Threads.Count;
        var fdsAfter = OpenFileDescriptors();
        Assert.True(threadsAfter - threadsBefore <= 8, $"threads {threadsBefore} -> {threadsAfter}");
        if (OperatingSystem.IsLinux()) Assert.True(fdsAfter - fdsBefore <= 10, $"file descriptors {fdsBefore} -> {fdsAfter}");
        Assert.True(memoryAfter - memoryBefore < 40L * 1024 * 1024, $"managed memory grew by {(memoryAfter - memoryBefore) / 1024.0 / 1024.0:F1} MB over {N} documents");
    }

    [Fact]
    public async Task Starting_and_stopping_a_server_thirty_times_leaves_no_threads_handles_clients_or_registrations()
    {
        await using var env = new LspTestEnv();
        env.Connections.OnConnect = (s, _) => s.Capabilities = new P.ServerCapabilities { HoverProvider = true, TextDocumentSync = P.TextDocumentSyncKind.Incremental };
        env.AddFake();
        await env.Manager.StartAsync("fake", Ct);
        await env.Manager.StopAsync("fake", Ct); // warm up (JIT, lazily created statics)
        GC.Collect(); GC.WaitForPendingFinalizers();
        var threadsBefore = Process.GetCurrentProcess().Threads.Count;
        var fdsBefore = OpenFileDescriptors();
        var weakClients = new List<WeakReference>();

        for (var i = 0; i < 30; i++)
        {
            await env.Manager.StartAsync("fake", Ct);
            await env.WaitForStateAsync("fake", LanguageServerState.Running);
            weakClients.Add(new WeakReference(env.Manager.Slots.Single().Client));
            Assert.Single(env.Registry.GetAll<IHoverProvider>());
            await env.Manager.StopAsync("fake", Ct);
            Assert.Empty(env.Registry.GetAll<IHoverProvider>());
        }

        Assert.All(env.Connections.Servers, s => Assert.True(s.ExitReceived || s.ClosedTask.IsCompleted));
        for (var i = 0; i < 10 && weakClients.Any(w => w.IsAlive); i++) { GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); await Task.Delay(20, Ct); }
        Assert.Equal(0, weakClients.Count(w => w.IsAlive));

        var threadsAfter = Process.GetCurrentProcess().Threads.Count;
        var fdsAfter = OpenFileDescriptors();
        Assert.True(threadsAfter - threadsBefore <= 8, $"threads {threadsBefore} -> {threadsAfter}");
        if (OperatingSystem.IsLinux()) Assert.True(fdsAfter - fdsBefore <= 10, $"file descriptors {fdsBefore} -> {fdsAfter}");
        Assert.Single(env.Manager.Slots);
    }

    [Fact]
    public async Task Disposing_the_manager_releases_it_completely()
    {
        var weak = await Task.Run(async () =>
        {
            var env = new LspTestEnv();
            await env.StartAsync();
            var manager = new WeakReference(env.Manager);
            await env.DisposeAsync();
            return manager;
        }, Ct);
        for (var i = 0; i < 10 && weak.IsAlive; i++) { GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); await Task.Delay(20, Ct); }
        Assert.False(weak.IsAlive, "the manager (and with it the lane threads, slots, clients and subscriptions) must be collectable after disposal");
    }
}
