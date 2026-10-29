using System.Diagnostics;
using System.Text.Json;
using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using Xunit;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Tests;

/// <summary>
/// The isolation contract: "LSP must never slow down editing or the GUI". Providers cost the caller an enqueue, nothing is posted to
/// the caller's synchronization context, language work runs on the dedicated lane, queues are bounded, slow/hung servers degrade to
/// null results and an Unresponsive state instead of stalling anything else.
/// </summary>
public class ThreadingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class ThrowingSyncContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback d, object? state) { Interlocked.Increment(ref Posts); throw new InvalidOperationException("work was posted to the caller's (UI) context"); }
        public override void Send(SendOrPostCallback d, object? state) { Interlocked.Increment(ref Posts); throw new InvalidOperationException("work was sent to the caller's (UI) context"); }
    }

    private static P.ServerCapabilities Everything() => new()
    {
        TextDocumentSync = P.TextDocumentSyncKind.Incremental,
        HoverProvider = true,
        CompletionProvider = new P.CompletionOptions { ResolveProvider = true, TriggerCharacters = ["."] },
        SignatureHelpProvider = new P.SignatureHelpOptions { TriggerCharacters = ["("] },
        DefinitionProvider = true, DeclarationProvider = true, TypeDefinitionProvider = true, ImplementationProvider = true,
        ReferencesProvider = true, DocumentHighlightProvider = true, DocumentSymbolProvider = true, WorkspaceSymbolProvider = true,
        CodeActionProvider = new P.CodeActionOptions { ResolveProvider = true },
        CodeLensProvider = new P.CodeLensOptions { ResolveProvider = true },
        ColorProvider = true, DocumentFormattingProvider = true, DocumentRangeFormattingProvider = true,
        DocumentOnTypeFormattingProvider = new P.DocumentOnTypeFormattingOptions { FirstTriggerCharacter = "}" },
        RenameProvider = new P.RenameOptions { PrepareProvider = true }, FoldingRangeProvider = true, SelectionRangeProvider = true,
        InlayHintProvider = true, LinkedEditingRangeProvider = true, CallHierarchyProvider = true, TypeHierarchyProvider = true,
        SemanticTokensProvider = new P.SemanticTokensOptions { Legend = new P.SemanticTokensLegend(), Full = new P.SemanticTokensOptionsFullOption2 { Delta = true }, Range = true },
    };

    // ------------------------------------------------------------------------------------------------------------
    // the caller pays only for an enqueue
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_provider_method_costs_the_caller_under_100_microseconds_even_when_server_and_lane_are_saturated()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s => { s.Capabilities = Everything(); s.NeverRespond = true; });
        var r = env.Registry;
        var pos = new TextPosition(0, 3);
        var range = TextRange.FromLines(0, 0, 1, 0);
        var opts = new FormattingOptions();
        var item = new CompletionItem { Label = "x" };
        var hier = new HierarchyItem("n", SymbolKind.Function, doc.Uri, range, range);

        var calls = new (string Name, Func<object> Call)[]
        {
            ("hover", () => r.GetProviders<IHoverProvider>(doc)[0].ProvideHoverAsync(doc, pos, Ct)),
            ("completion", () => r.GetProviders<ICompletionProvider>(doc)[0].ProvideCompletionAsync(doc, pos, new CompletionContext(CompletionTriggerKind.Invoked), Ct)),
            ("completion.resolve", () => r.GetProviders<ICompletionProvider>(doc)[0].ResolveCompletionAsync(item, Ct)),
            ("signatureHelp", () => r.GetProviders<ISignatureHelpProvider>(doc)[0].ProvideSignatureHelpAsync(doc, pos, new SignatureHelpContext(SignatureHelpTriggerKind.Invoked, null, false, null), Ct)),
            ("definition", () => r.GetProviders<IDefinitionProvider>(doc)[0].ProvideDefinitionAsync(doc, pos, DefinitionKind.Definition, Ct)),
            ("references", () => r.GetProviders<IReferenceProvider>(doc)[0].ProvideReferencesAsync(doc, pos, true, Ct)),
            ("highlight", () => r.GetProviders<IDocumentHighlightProvider>(doc)[0].ProvideDocumentHighlightsAsync(doc, pos, Ct)),
            ("documentSymbol", () => r.GetProviders<IDocumentSymbolProvider>(doc)[0].ProvideDocumentSymbolsAsync(doc, Ct)),
            ("workspaceSymbol", () => r.GetAll<IWorkspaceSymbolProvider>()[0].ProvideWorkspaceSymbolsAsync("q", Ct)),
            ("codeAction", () => r.GetProviders<ICodeActionProvider>(doc)[0].ProvideCodeActionsAsync(doc, range, new CodeActionContext([]), Ct)),
            ("rename", () => r.GetProviders<IRenameProvider>(doc)[0].ProvideRenameAsync(doc, pos, "n", Ct)),
            ("prepareRename", () => r.GetProviders<IRenameProvider>(doc)[0].PrepareRenameAsync(doc, pos, Ct)),
            ("formatDocument", () => r.GetProviders<IDocumentFormattingProvider>(doc)[0].FormatDocumentAsync(doc, opts, Ct)),
            ("formatRange", () => r.GetProviders<IDocumentFormattingProvider>(doc)[0].FormatRangeAsync(doc, range, opts, Ct)),
            ("formatOnType", () => r.GetProviders<IDocumentFormattingProvider>(doc)[0].FormatOnTypeAsync(doc, pos, "}", opts, Ct)),
            ("folding", () => r.GetProviders<IFoldingRangeProvider>(doc)[0].ProvideFoldingRangesAsync(doc, Ct)),
            ("selection", () => r.GetProviders<ISelectionRangeProvider>(doc)[0].ProvideSelectionRangesAsync(doc, [pos], Ct)),
            ("semanticTokens", () => r.GetProviders<ISemanticTokensProvider>(doc)[0].ProvideSemanticTokensAsync(doc, Ct)),
            ("semanticTokensRange", () => r.GetProviders<ISemanticTokensRangeProvider>(doc)[0].ProvideSemanticTokensRangeAsync(doc, range, Ct)),
            ("inlayHints", () => r.GetProviders<IInlayHintsProvider>(doc)[0].ProvideInlayHintsAsync(doc, range, Ct)),
            ("codeLens", () => r.GetProviders<ICodeLensProvider>(doc)[0].ProvideCodeLensesAsync(doc, Ct)),
            ("documentColor", () => r.GetProviders<IDocumentColorProvider>(doc)[0].ProvideDocumentColorsAsync(doc, Ct)),
            ("linkedEditing", () => r.GetProviders<ILinkedEditingRangeProvider>(doc)[0].ProvideLinkedEditingRangesAsync(doc, pos, Ct)),
            ("callHierarchy", () => r.GetProviders<ICallHierarchyProvider>(doc)[0].PrepareCallHierarchyAsync(doc, pos, Ct)),
            ("callHierarchy.incoming", () => r.GetProviders<ICallHierarchyProvider>(doc)[0].IncomingCallsAsync(hier, Ct)),
            ("typeHierarchy", () => r.GetProviders<ITypeHierarchyProvider>(doc)[0].PrepareTypeHierarchyAsync(doc, pos, Ct)),
        };
        // resolve providers once (registry lookups are the consumer's cost, not ours)
        var cached = calls.Select(c => c).ToArray();

        var slowest = ("", 0d);
        var failures = new List<string>();
        var context = new ThrowingSyncContext();
        var worker = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            using var block = new LaneBlock(env.Manager.Queue); // the lane is also fully busy
            foreach (var (name, call) in cached)
            {
                for (var i = 0; i < 20; i++) _ = call(); // warm up
                const int n = 300;
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < n; i++) _ = call();
                var avgMs = sw.Elapsed.TotalMilliseconds / n;
                if (avgMs > slowest.Item2) slowest = (name, avgMs);
                if (avgMs >= 0.1) failures.Add($"{name}: {avgMs * 1000:F1} us per call");
            }
            block.Release();
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(60)));
        Assert.Equal(0, context.Posts); // nothing was ever posted to the caller's synchronization context
        Assert.True(failures.Count == 0, "synchronous cost on the caller must stay under 0.1 ms: " + string.Join("; ", failures) + $" (slowest {slowest})");
    }

    [Fact]
    public async Task Typing_costs_the_editing_thread_almost_nothing_even_when_the_lane_is_stuck()
    {
        // Baseline: the cost of the document edit itself, with no language support attached.
        double Measure(ITextDocument d, int n)
        {
            for (var i = 0; i < 100; i++) ((InMemoryTextDocument)d).ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "w")]);
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < n; i++) ((InMemoryTextDocument)d).ApplyEdits([TextEdit.Insert(new TextPosition(0, i % 5), "x")]);
            return sw.Elapsed.TotalMilliseconds / n;
        }

        var bare = new InMemoryTextDocument(new Uri("file:///bare.fake"), "fake", "0123456789\n");
        var baseline = Measure(bare, 2000);

        await using var env = new LspTestEnv();
        var (_, doc) = await env.StartAsync(text: "0123456789\n");
        using var block = new LaneBlock(env.Manager.Queue); // the Language lane is completely stuck
        var context = new ThrowingSyncContext();
        double withLsp = 0;
        var t = new Thread(() => { SynchronizationContext.SetSynchronizationContext(context); withLsp = Measure(doc, 2000); });
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)));
        block.Release();
        Assert.Equal(0, context.Posts);
        var overhead = withLsp - baseline;
        Assert.True(overhead < 0.05, $"language-support overhead per keystroke: {overhead * 1000:F1} us (baseline {baseline * 1000:F1} us, with LSP {withLsp * 1000:F1} us)");
    }

    [Fact]
    public async Task Huge_bursts_of_edits_while_the_lane_is_stuck_keep_pending_changes_bounded_and_converge_afterwards()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(text: "");
        using (var block = new LaneBlock(env.Manager.Queue))
        {
            for (var i = 0; i < 45_000; i++) doc.ApplyEdits([TextEdit.Insert(doc.PositionAt(doc.Length), i % 10 == 0 ? "\n" : "k")]);
            block.Release();
        }
        await TestHelpers.EventuallyAsync(() => server.TextOf(doc.Uri.AbsoluteUri) == doc.GetText(), "server converges after the backlog (full resync)");
    }

    [Fact]
    public async Task Manager_start_stop_and_restart_never_run_blocking_work_on_the_calling_thread()
    {
        await using var env = new LspTestEnv();
        env.Connections.SyncBlock = TimeSpan.FromMilliseconds(400); // "Process.Start" blocks this long
        env.AddFake();
        var context = new ThrowingSyncContext();
        var callerThread = -1;
        Task start = Task.CompletedTask, stop = Task.CompletedTask, restart = Task.CompletedTask;
        double startMs = 0, stopMs = 0, restartMs = 0;
        var t = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            callerThread = Environment.CurrentManagedThreadId;
            var sw = Stopwatch.StartNew();
            start = env.Manager.StartAsync("fake", Ct).AsTask();
            startMs = sw.Elapsed.TotalMilliseconds;
        });
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)));
        Assert.True(startMs < 100, $"StartAsync returned to the caller after {startMs:F1} ms although connecting blocks for 400 ms");
        await start.Within();
        await env.WaitForStateAsync("fake", LanguageServerState.Running);

        var t2 = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var sw = Stopwatch.StartNew();
            restart = env.Manager.RestartAsync("fake", Ct).AsTask();
            restartMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
        });
        t2.Start();
        Assert.True(t2.Join(TimeSpan.FromSeconds(10)));
        Assert.True(restartMs < 100, $"RestartAsync returned after {restartMs:F1} ms");
        await restart.Within();

        var t3 = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var sw = Stopwatch.StartNew();
            stop = env.Manager.StopAsync("fake", Ct).AsTask();
            stopMs = sw.Elapsed.TotalMilliseconds;
        });
        t3.Start();
        Assert.True(t3.Join(TimeSpan.FromSeconds(10)));
        Assert.True(stopMs < 100, $"StopAsync returned after {stopMs:F1} ms");
        await stop.Within();

        Assert.Equal(0, context.Posts);
        string?[] connectThreads;
        lock (env.Connections.ConnectThreads) connectThreads = env.Connections.ConnectThreads.ToArray();
        Assert.NotEmpty(connectThreads);
        Assert.StartsWith("cscode-lsp-lane", connectThreads[0]);        // the blocking connect of the first start ran on the lane...
        Assert.All(connectThreads, name => Assert.NotNull(name));       // ...and never on the (unnamed) calling thread; continuations of a restart may use the pool
    }

    // ------------------------------------------------------------------------------------------------------------
    // language work runs on the lane
    // ------------------------------------------------------------------------------------------------------------

    private sealed class ThreadRecordingDocument(InMemoryTextDocument inner) : ITextDocument
    {
        public List<(string Member, string? Thread, bool Pool)> Accesses { get; } = new();
        private void Note(string m) { lock (Accesses) Accesses.Add((m, Thread.CurrentThread.Name, Thread.CurrentThread.IsThreadPoolThread)); }
        public Uri Uri => inner.Uri;
        public string LanguageId => inner.LanguageId;
        public int Version => inner.Version;
        public bool IsDirty => inner.IsDirty;
        public int Length => inner.Length;
        public int LineCount => inner.LineCount;
        public string GetText() { Note("GetText"); return inner.GetText(); }
        public string GetText(TextRange range) { Note("GetText(range)"); return inner.GetText(range); }
        public string GetLineText(int line) { Note("GetLineText"); return inner.GetLineText(line); }
        public int OffsetAt(TextPosition position) => inner.OffsetAt(position);
        public TextPosition PositionAt(int offset) => inner.PositionAt(offset);
        public TextPosition Validate(TextPosition position) => inner.Validate(position);
        public bool ApplyEdits(IReadOnlyList<TextEdit> edits) => inner.ApplyEdits(edits);
        public IEventSource<TextDocumentChangedEventArgs> Changed => inner.Changed;
        public IEventSource<ITextDocument> LanguageIdChanged => inner.LanguageIdChanged;
    }

    private sealed class ThreadRecordingDiagnostics : IDiagnosticService
    {
        private readonly DiagnosticService _inner = new();
        public List<(string? Thread, bool Pool)> Writes { get; } = new();
        public void Set(Uri uri, string owner, IReadOnlyList<Diagnostic> diagnostics, int? documentVersion = null) { lock (Writes) Writes.Add((Thread.CurrentThread.Name, Thread.CurrentThread.IsThreadPoolThread)); _inner.Set(uri, owner, diagnostics, documentVersion); }
        public void Clear(string owner) => _inner.Clear(owner);
        public void Clear(Uri uri, string owner) => _inner.Clear(uri, owner);
        public IReadOnlyList<Diagnostic> Get(Uri uri) => _inner.Get(uri);
        public IReadOnlyList<(Uri Uri, Diagnostic Diagnostic)> GetAll() => _inner.GetAll();
        public (int Errors, int Warnings, int Infos, int Hints) Counts => _inner.Counts;
        public IEventSource<IReadOnlyCollection<Uri>> Changed => _inner.Changed;
    }

    [Fact]
    public async Task Document_text_is_only_ever_read_on_lane_threads_never_on_the_editing_or_pool_threads()
    {
        await using var env = new LspTestEnv();
        env.AddFake();
        var recording = new ThreadRecordingDocument(new InMemoryTextDocument(new Uri(env.Dir.WriteFile("rec.fake", "hello")), "fake", "hello"));
        using var reg = env.Documents.Register(recording);
        var server = await env.Connections.WaitForServerAsync();
        await server.WaitForAsync(P.LspMethods.TextDocumentDidOpen);
        // full-sync path (forced by a stale resync) reads the text again, also on the lane
        recording.ApplyEdits([TextEdit.Insert(new TextPosition(0, 5), "!")]);
        await TestHelpers.EventuallyAsync(() => server.TextOf(recording.Uri.AbsoluteUri) == "hello!", "edit delivered");
        List<(string Member, string? Thread, bool Pool)> accesses;
        lock (recording.Accesses) accesses = recording.Accesses.ToList();
        Assert.NotEmpty(accesses);
        Assert.All(accesses, a => { Assert.False(a.Pool); Assert.StartsWith("cscode-lsp-lane", a.Thread); });
    }

    [Fact]
    public async Task Diagnostics_are_decoded_and_published_on_lane_threads()
    {
        var diagnostics = new ThreadRecordingDiagnostics();
        await using var env = new LspTestEnv(diagnostics: diagnostics);
        var (server, doc) = await env.StartAsync();
        await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, Json("{\"uri\":\"" + doc.Uri.AbsoluteUri + "\",\"diagnostics\":[{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}},\"message\":\"m\"}]}"));
        await TestHelpers.EventuallyAsync(() => diagnostics.Get(doc.Uri).Count == 1, "published");
        lock (diagnostics.Writes) Assert.All(diagnostics.Writes, w => { Assert.False(w.Pool); Assert.StartsWith("cscode-lsp-lane", w.Thread); });
    }

    // ------------------------------------------------------------------------------------------------------------
    // timeouts, cancellation, latest-wins
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_slow_server_yields_null_at_the_timeout_and_the_request_is_cancelled_on_the_wire()
    {
        await using var env = new LspTestEnv(c => c.Set(LspSettingKeys.InteractiveTimeoutMs, 250));
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { HoverProvider = true };
            s.ResponseDelay = TimeSpan.FromSeconds(5);
        });
        var sw = Stopwatch.StartNew();
        var hover = await env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, new TextPosition(0, 0), Ct).Within();
        Assert.Null(hover);
        Assert.InRange(sw.Elapsed.TotalMilliseconds, 200, 2500);
        await server.WaitForAsync("$/cancelRequest");
    }

    [Fact]
    public async Task Editing_the_document_cancels_its_in_flight_requests()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { HoverProvider = true };
            s.ResponseDelay = TimeSpan.FromSeconds(5);
        });
        var pending = env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, new TextPosition(0, 0), Ct).AsTask();
        await server.WaitForAsync(P.LspMethods.TextDocumentHover);
        var sw = Stopwatch.StartNew();
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "x")]);
        Assert.Null(await pending.Within());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"abandoned after {sw.Elapsed}");
        await server.WaitForAsync("$/cancelRequest");
    }

    [Fact]
    public async Task A_response_for_an_older_document_version_is_dropped()
    {
        await using var env = new LspTestEnv();
        var gate = new TaskCompletionSource();
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { HoverProvider = true };
            s.Requests[P.LspMethods.TextDocumentHover] = async _ =>
            {
                await gate.Task; // the server answers only after the document has changed
                return new P.Hover { Contents = new P.MarkupContent { Kind = P.MarkupKind.PlainText, Value = "stale" } };
            };
        });
        var pending = env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, new TextPosition(0, 0), Ct).AsTask();
        await server.WaitForAsync(P.LspMethods.TextDocumentHover);
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "x")]);
        gate.SetResult();
        Assert.Null(await pending.Within()); // the late answer describes text that no longer exists
    }

    [Fact]
    public async Task Latest_request_wins_per_document_and_feature()
    {
        await using var env = new LspTestEnv();
        var seen = 0;
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { CompletionProvider = new P.CompletionOptions() };
            s.Requests[P.LspMethods.TextDocumentCompletion] = async p =>
            {
                var n = Interlocked.Increment(ref seen);
                if (n == 1) await Task.Delay(1500);
                return new P.CompletionList { Items = [new P.CompletionItem { Label = "call" + n }] };
            };
        });
        var provider = env.Provider<ICompletionProvider>(doc);
        var first = provider.ProvideCompletionAsync(doc, new TextPosition(0, 1), new CompletionContext(CompletionTriggerKind.Invoked), Ct).AsTask();
        await server.WaitForAsync(P.LspMethods.TextDocumentCompletion);
        var second = provider.ProvideCompletionAsync(doc, new TextPosition(0, 2), new CompletionContext(CompletionTriggerKind.Invoked), Ct).AsTask();
        Assert.Null(await first.Within());                                // superseded: cancelled, null
        Assert.Equal("call2", (await second.Within())!.Items.Single().Label);
        await server.WaitForAsync("$/cancelRequest");
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_as_OperationCanceledException()
    {
        await using var env = new LspTestEnv();
        var (_, doc) = await env.StartAsync(s => { s.Capabilities = new P.ServerCapabilities { HoverProvider = true }; s.NeverRespond = true; });
        using var cts = new CancellationTokenSource();
        var pending = env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, new TextPosition(0, 0), cts.Token).AsTask();
        await Task.Delay(100, Ct);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.Within());
    }

    // ------------------------------------------------------------------------------------------------------------
    // circuit breaker
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Consecutive_timeouts_mark_the_server_Unresponsive_skip_requests_probe_and_recover()
    {
        await using var env = new LspTestEnv(c =>
        {
            c.Set(LspSettingKeys.InteractiveTimeoutMs, 120);
            c.Set(LspSettingKeys.BreakerThreshold, 3);
            c.Set(LspSettingKeys.BreakerProbeMs, 400);
        });
        var (server, doc) = await env.StartAsync(s => { s.Capabilities = new P.ServerCapabilities { HoverProvider = true }; s.NeverRespond = true; });
        var hover = env.Provider<IHoverProvider>(doc);
        for (var i = 0; i < 3; i++)
        {
            Assert.Null(await hover.ProvideHoverAsync(doc, new TextPosition(0, i), Ct).Within());
            doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "x")]); // fresh version each time
        }
        await env.WaitForStateAsync("fake", LanguageServerState.Unresponsive);
        Assert.Contains(env.Window.StatusMessages.ToArray(), m => m.Contains("not responding"));

        var sent = server.CountOf(P.LspMethods.TextDocumentHover);
        var sw = Stopwatch.StartNew();
        Assert.Null(await hover.ProvideHoverAsync(doc, new TextPosition(0, 0), Ct).Within());
        Assert.True(sw.ElapsedMilliseconds < 100, $"an open breaker answers immediately, took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(sent, server.CountOf(P.LspMethods.TextDocumentHover)); // nothing was sent to the stuck server

        server.NeverRespond = false; // the server wakes up
        server.Requests[P.LspMethods.TextDocumentHover] = _ => Task.FromResult<object?>(new P.Hover { Contents = new P.MarkupContent { Kind = P.MarkupKind.PlainText, Value = "back" } });
        await TestHelpers.EventuallyAsync(() => env.Manager.Servers.Single().State == LanguageServerState.Running, () => "recovers automatically\n" + env.Dump(), TimeSpan.FromSeconds(10)).WaitAsync(Ct);
    }

    [Fact]
    public async Task A_hung_server_does_not_affect_other_servers_or_their_features()
    {
        await using var env = new LspTestEnv(c => c.Set(LspSettingKeys.InteractiveTimeoutMs, 400));
        env.Connections.OnConnect = (s, i) =>
        {
            s.Capabilities = new P.ServerCapabilities { HoverProvider = true };
            if (i == 0) s.NeverRespond = true;
            else s.Requests[P.LspMethods.TextDocumentHover] = _ => Task.FromResult<object?>(new P.Hover { Contents = new P.MarkupContent { Kind = P.MarkupKind.PlainText, Value = "healthy" } });
        };
        env.Manager.AddDefinition(LspTestEnv.FakeDef("hung", "hung"));
        env.Manager.AddDefinition(LspTestEnv.FakeDef("good", "good"));
        var hungDoc = env.Open("a.hung", "x", "hung");
        await env.Connections.WaitForServerAsync(0);
        var goodDoc = env.Open("b.good", "y", "good");
        await env.Connections.WaitForServerAsync(1);
        await TestHelpers.EventuallyAsync(() => env.Manager.Servers.Count(s => s.State == LanguageServerState.Running) == 2, "both running");

        var hungHover = env.Registry.GetProviders<IHoverProvider>(hungDoc)[0].ProvideHoverAsync(hungDoc, new TextPosition(0, 0), Ct).AsTask(); // hangs for 400 ms
        var sw = Stopwatch.StartNew();
        var good = await env.Registry.GetProviders<IHoverProvider>(goodDoc)[0].ProvideHoverAsync(goodDoc, new TextPosition(0, 0), Ct).Within();
        Assert.Equal("healthy", good!.Contents[0].Value);
        Assert.True(sw.ElapsedMilliseconds < 300, $"healthy server answered in {sw.ElapsedMilliseconds} ms while the other was hung");
        Assert.Null(await hungHover.Within());
    }

    // ------------------------------------------------------------------------------------------------------------
    // bounded queues / backpressure
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_flood_of_requests_cannot_grow_the_queue_beyond_its_hard_cap()
    {
        await using var env = new LspTestEnv(c => c.Set(LspSettingKeys.QueueCapacity, 64));
        var (_, doc) = await env.StartAsync(s => s.Capabilities = new P.ServerCapabilities { CompletionProvider = new P.CompletionOptions { ResolveProvider = true } });
        var provider = env.Provider<ICompletionProvider>(doc);
        var queue = env.Manager.Queue;
        var tasks = new List<Task>();
        using (var block = new LaneBlock(queue))
        {
            for (var i = 0; i < 10_000; i++) tasks.Add(provider.ResolveCompletionAsync(new CompletionItem { Label = "i" + i }, Ct).AsTask()); // unkeyed: no latest-wins to hide behind
            Assert.True(queue.Depth <= queue.Capacity, $"depth {queue.Depth} > cap {queue.Capacity}");
            Assert.True(queue.MaxObservedDepth <= queue.Capacity, $"max depth {queue.MaxObservedDepth}");
            Assert.True(queue.ShedCount > 0);
            block.Release();
        }
        await Task.WhenAll(tasks).Within(); // everything completes (shed items complete with the unresolved item), nothing hangs
    }

    [Fact]
    public async Task Background_features_are_skipped_without_enqueueing_when_the_lane_is_backed_up()
    {
        await using var env = new LspTestEnv(c => c.Set(LspSettingKeys.QueueCapacity, 64));
        var (_, doc) = await env.StartAsync(s => s.Capabilities = Everything());
        var queue = env.Manager.Queue;
        using var block = new LaneBlock(queue);
        var resolver = env.Provider<ICompletionProvider>(doc);
        for (var i = 0; i < 60; i++) _ = resolver.ResolveCompletionAsync(new CompletionItem { Label = "x" + i }, Ct); // depth > capacity / 2
        Assert.True(queue.ShouldShed(LanePriority.Background));

        var before = queue.EnqueuedCount;
        var tokens = env.Provider<ISemanticTokensProvider>(doc).ProvideSemanticTokensAsync(doc, Ct);
        var hints = env.Provider<IInlayHintsProvider>(doc).ProvideInlayHintsAsync(doc, TextRange.FromLines(0, 0, 1, 0), Ct);
        Assert.True(tokens.IsCompleted && hints.IsCompleted);          // answered right away...
        Assert.Null(await tokens);                                      // ...with "no result"
        Assert.Equal(before, queue.EnqueuedCount);                      // and nothing was queued on their behalf
        block.Release();
    }

    [Fact]
    public async Task The_reader_never_blocks_on_a_stuck_consumer_and_notification_mailboxes_stay_bounded()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync();
        var client = env.Manager.Slots.Single().Client!;
        var sw = Stopwatch.StartNew();
        using (var block = new LaneBlock(env.Manager.Queue)) // nothing downstream can make progress
        {
            for (var i = 0; i < 6000; i++)
                await server.NotifyAsync(P.LspMethods.TextDocumentPublishDiagnostics, Json("{\"uri\":\"file:///flood/f" + i + ".fake\",\"diagnostics\":[{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}},\"message\":\"m\"}]}"));
            for (var i = 0; i < 6000; i++)
                await server.NotifyAsync(P.LspMethods.WindowLogMessage, new P.LogMessageParams { Type = P.MessageType.Log, Message = "line " + i });
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"the server could keep writing (took {sw.Elapsed})");

            // The client's reader keeps consuming the pipe (it never waits for the lane) and the mailbox holds a bounded number of files.
            await TestHelpers.EventuallyAsync(() => client.DiagnosticsBacklog.Dropped >= 5000, () => $"reader consumed the flood (backlog {client.DiagnosticsBacklog})");
            var backlog = client.DiagnosticsBacklog;
            Assert.True(backlog.Pending <= 512, $"pending {backlog.Pending}");
            Assert.Empty(env.Diagnostics.GetAll()); // and nothing was published, since the lane is stuck
            block.Release();
        }
        // Accounting: every one of the 6000 files was either published or dropped by the bounded mailbox; none vanished silently.
        await TestHelpers.EventuallyAsync(() => client.DiagnosticsBacklog.Pending == 0 && env.Diagnostics.GetAll().Count + client.DiagnosticsBacklog.Dropped == 6000,
            () => $"published {env.Diagnostics.GetAll().Count} + dropped {client.DiagnosticsBacklog.Dropped} should be 6000");
        Assert.True(env.Output.Text("Fake fake (LSP)").Length <= 2_000_000);
    }

    // ------------------------------------------------------------------------------------------------------------
    // OS-level isolation settings / memory accounting
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Process_priority_is_below_normal_by_default_and_follows_the_setting()
    {
        await using (var env = new LspTestEnv())
        {
            await env.StartAsync();
            Assert.Equal(ProcessPriority.BelowNormal, env.Connections.Requests[0].Priority);
        }
        await using (var env = new LspTestEnv(c => c.Set(LspSettingKeys.ProcessPriority, "idle")))
        {
            await env.StartAsync();
            Assert.Equal(ProcessPriority.Idle, env.Connections.Requests[0].Priority);
        }
        await using (var env = new LspTestEnv(c => c.Set(LspSettingKeys.ProcessPriority, "normal")))
        {
            await env.StartAsync();
            Assert.Equal(ProcessPriority.Normal, env.Connections.Requests[0].Priority);
        }
    }

    [Fact]
    public async Task Child_process_is_tracked_by_the_memory_monitor_and_restarted_when_over_budget()
    {
        await using var env = new LspTestEnv(memoryMonitor: true);
        env.Manager.AddDefinition(LspTestEnv.FakeDef() with { RestartAboveBytes = 123_456_789 });
        env.Connections.OnConnect = (s, _) => s.PidOverride = 4242;
        env.Open("a.fake", "x");
        var first = await env.Connections.WaitForServerAsync();
        await env.WaitForStateAsync("fake", LanguageServerState.Running);
        var tracked = Assert.Single(env.Memory!.Tracked);
        Assert.Equal(("lsp:fake", 4242, (long?)123_456_789), (tracked.Name, tracked.Pid, tracked.Limit));

        await tracked.OnOverBudget!(); // the monitor reports the server is over its cap
        var second = await env.Connections.WaitForServerAsync(1);
        await second.WaitForAsync(P.LspMethods.TextDocumentDidOpen);
        Assert.True(first.ExitReceived);
        Assert.True(env.Memory.DisposedTracking >= 1); // tracking of the old process was released
    }

    [Fact]
    public async Task Result_caches_register_as_memory_trimmables_and_release_memory_on_trim()
    {
        await using var env = new LspTestEnv(memoryMonitor: true);
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = Everything();
            s.Requests[P.LspMethods.TextDocumentSemanticTokensFull] = _ => Task.FromResult<object?>(new P.SemanticTokens { ResultId = "r", Data = new uint[50_000] });
        });
        var trimmable = Assert.Single(env.Memory!.Trimmables, t => t.Name == "lsp:semanticTokens");
        await env.Provider<ISemanticTokensProvider>(doc).ProvideSemanticTokensAsync(doc, Ct).Within();
        Assert.True(trimmable.EstimatedBytes >= 200_000);
        Assert.True(trimmable.Trim(MemoryTrimLevel.Aggressive) >= 200_000);
        Assert.Equal(0, trimmable.EstimatedBytes);
        await env.Manager.StopAsync("fake", Ct);
        Assert.Empty(env.Memory.Trimmables); // unregistered with the session
    }
}
