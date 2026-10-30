using System.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Remote.Bridge;
using CsCode.Remote.Client;
using CsCode.Remote.Protocol;
using CsCode.Remote.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>ARCHITECTURE §6 for the bridge: provider calls and typing cost the caller an enqueue, whatever the server does (here: never answers).</summary>
public class BridgeThreadingTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    private sealed class ThrowingSyncContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback d, object? state) { Interlocked.Increment(ref Posts); ThreadPool.QueueUserWorkItem(_ => d(state)); }
        public override void Send(SendOrPostCallback d, object? state) => throw new InvalidOperationException("Send on the caller's context");
    }

    /// <summary>A server end that registers one provider of every feature and never answers a call.</summary>
    private static async Task<(RemoteSession Session, LanguageBridge Bridge, LanguageFeatureRegistry Registry, DocumentSync Sync, DocumentManager Docs, Func<Task> Dispose)> ConnectAsync(SshUriMapper mapper)
    {
        var registry = new LanguageFeatureRegistry();
        var docs = new DocumentManager();
        var bridge = new LanguageBridge(registry, mapper);
        var sync = new DocumentSync(docs, mapper);
        var transport = new HangingServer();
        var session = await RemoteSession.ConnectAsync(transport, mapper, "test", p => { bridge.Attach(p); sync.Attach(p); }, null, NullLogger.Instance, Ct);
        await bridge.OnConnectedAsync(session, Ct);
        await sync.OnConnectedAsync(session, Ct);
        return (session, bridge, registry, sync, docs, async () => { bridge.Dispose(); sync.Dispose(); await session.DisposeAsync(); });
    }

    private sealed class HangingServer : IRemoteTransport
    {
        public string Description => "hanging";
        public Task<RemoteChannel> ConnectAsync(IProgress<string>? progress, CancellationToken ct)
        {
            var toServer = new MemoryPipe();
            var toClient = new MemoryPipe();
            var server = new RpcPeer(toServer.Reader, toClient.Writer, new RpcPeerOptions { Name = "hanging-server" });
            string[] features = [nameof(ICompletionProvider), nameof(IHoverProvider), nameof(IDefinitionProvider), nameof(IReferenceProvider), nameof(IDocumentHighlightProvider),
                nameof(IRenameProvider), nameof(IDocumentFormattingProvider), nameof(ISignatureHelpProvider), nameof(ICodeActionProvider), nameof(IDocumentSymbolProvider),
                nameof(IFoldingRangeProvider), nameof(ISelectionRangeProvider), nameof(ISemanticTokensProvider), nameof(IInlayHintsProvider), nameof(ICodeLensProvider),
                nameof(IDocumentColorProvider), nameof(ILinkedEditingRangeProvider)];
            var regs = features.Select((f, i) => new LangRegistration(i + 1, f, DocumentSelector.ForLanguage("ts"), "s", new ProviderInfo(["."], [","], [";"], new SemanticTokensLegend([], [])))).ToArray();
            server.OnRequest<HelloParams, HelloResult>(RemoteMethods.Hello, (_, _) => ValueTask.FromResult(new HelloResult(RemoteProtocol.Version, "t", "Linux", "x64", 1, "/", "/", [])));
            server.OnRequest<Empty, IReadOnlyList<LangRegistration>>("lang/list", (_, _) => ValueTask.FromResult<IReadOnlyList<LangRegistration>>(regs));
            server.OnRequest<LangInvokeParams, Empty>(RemoteMethods.LangInvoke, async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Empty.Value; });
            server.OnRequest<Empty, Empty>(RemoteMethods.Shutdown, (_, _) => ValueTask.FromResult(Empty.Value));
            server.OnNotification<DocChangeParams>(RemoteMethods.DocChange, _ => Thread.Sleep(5)); // a slow consumer: must not reach the typist
            server.Start();
            return Task.FromResult(new RemoteChannel(toClient.Reader, toServer.Writer, "hanging", _ => { server.Close(); toServer.Close(); toClient.Close(); return Task.CompletedTask; }));
        }
    }

    [Fact]
    public async Task Every_bridged_provider_method_costs_the_caller_under_100_microseconds()
    {
        var mapper = new SshUriMapper(new RemoteAuthority("box"));
        var (_, _, registry, _, docs, dispose) = await ConnectAsync(mapper);
        try
        {
            var doc = new InMemoryTextDocument(mapper.FromRemotePath("/w/a.ts"), "ts", "let a = 1;\n");
            using var reg = docs.Register(doc);
            await TestEnv.WaitUntilAsync(() => registry.GetProviders<ICodeLensProvider>(doc).Count > 0, TimeSpan.FromSeconds(5), "bridges");
            var p = TextPosition.Zero;
            var r = TextRange.At(0, 0);
            // providers are looked up once: the registry's own lookup is not what is measured here
            var pICodeActionProvider = registry.GetProviders<ICodeActionProvider>(doc)[0];
            var pICodeLensProvider = registry.GetProviders<ICodeLensProvider>(doc)[0];
            var pICompletionProvider = registry.GetProviders<ICompletionProvider>(doc)[0];
            var pIDefinitionProvider = registry.GetProviders<IDefinitionProvider>(doc)[0];
            var pIDocumentColorProvider = registry.GetProviders<IDocumentColorProvider>(doc)[0];
            var pIDocumentFormattingProvider = registry.GetProviders<IDocumentFormattingProvider>(doc)[0];
            var pIDocumentHighlightProvider = registry.GetProviders<IDocumentHighlightProvider>(doc)[0];
            var pIDocumentSymbolProvider = registry.GetProviders<IDocumentSymbolProvider>(doc)[0];
            var pIFoldingRangeProvider = registry.GetProviders<IFoldingRangeProvider>(doc)[0];
            var pIHoverProvider = registry.GetProviders<IHoverProvider>(doc)[0];
            var pIInlayHintsProvider = registry.GetProviders<IInlayHintsProvider>(doc)[0];
            var pILinkedEditingRangeProvider = registry.GetProviders<ILinkedEditingRangeProvider>(doc)[0];
            var pIReferenceProvider = registry.GetProviders<IReferenceProvider>(doc)[0];
            var pIRenameProvider = registry.GetProviders<IRenameProvider>(doc)[0];
            var pISelectionRangeProvider = registry.GetProviders<ISelectionRangeProvider>(doc)[0];
            var pISemanticTokensProvider = registry.GetProviders<ISemanticTokensProvider>(doc)[0];
            var pISignatureHelpProvider = registry.GetProviders<ISignatureHelpProvider>(doc)[0];

            var calls = new (string, Func<object>)[]
            {
                ("completion", () => pICompletionProvider.ProvideCompletionAsync(doc, p, new CompletionContext(CompletionTriggerKind.Invoked), CancellationToken.None)),
                ("resolve", () => pICompletionProvider.ResolveCompletionAsync(new CompletionItem { Label = "x" }, CancellationToken.None)),
                ("hover", () => pIHoverProvider.ProvideHoverAsync(doc, p, CancellationToken.None)),
                ("definition", () => pIDefinitionProvider.ProvideDefinitionAsync(doc, p, DefinitionKind.Definition, CancellationToken.None)),
                ("references", () => pIReferenceProvider.ProvideReferencesAsync(doc, p, true, CancellationToken.None)),
                ("highlights", () => pIDocumentHighlightProvider.ProvideDocumentHighlightsAsync(doc, p, CancellationToken.None)),
                ("rename", () => pIRenameProvider.ProvideRenameAsync(doc, p, "b", CancellationToken.None)),
                ("format", () => pIDocumentFormattingProvider.FormatDocumentAsync(doc, new FormattingOptions(), CancellationToken.None)),
                ("signature", () => pISignatureHelpProvider.ProvideSignatureHelpAsync(doc, p, new SignatureHelpContext(SignatureHelpTriggerKind.Invoked, null, false, null), CancellationToken.None)),
                ("codeActions", () => pICodeActionProvider.ProvideCodeActionsAsync(doc, r, new CodeActionContext([]), CancellationToken.None)),
                ("symbols", () => pIDocumentSymbolProvider.ProvideDocumentSymbolsAsync(doc, CancellationToken.None)),
                ("folding", () => pIFoldingRangeProvider.ProvideFoldingRangesAsync(doc, CancellationToken.None)),
                ("selection", () => pISelectionRangeProvider.ProvideSelectionRangesAsync(doc, [p], CancellationToken.None)),
                ("semanticTokens", () => pISemanticTokensProvider.ProvideSemanticTokensAsync(doc, CancellationToken.None)),
                ("inlayHints", () => pIInlayHintsProvider.ProvideInlayHintsAsync(doc, r, CancellationToken.None)),
                ("codeLens", () => pICodeLensProvider.ProvideCodeLensesAsync(doc, CancellationToken.None)),
                ("colors", () => pIDocumentColorProvider.ProvideDocumentColorsAsync(doc, CancellationToken.None)),
                ("linkedEditing", () => pILinkedEditingRangeProvider.ProvideLinkedEditingRangesAsync(doc, p, CancellationToken.None)),
            };
            var failures = new List<string>();
            var context = new ThrowingSyncContext();
            var caller = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(context);
                foreach (var (name, call) in calls)
                {
                    for (var i = 0; i < 20; i++) _ = call();
                    const int n = 300;
                    var sw = Stopwatch.StartNew();
                    for (var i = 0; i < n; i++) _ = call();
                    var avg = sw.Elapsed.TotalMilliseconds / n;
                    if (avg >= 0.1) failures.Add($"{name}: {avg * 1000:F1} us");
                }
            });
            caller.Start();
            Assert.True(caller.Join(TimeSpan.FromSeconds(60)));
            Assert.Equal(0, context.Posts);
            Assert.True(failures.Count == 0, "synchronous cost of bridged providers must stay under 0.1 ms: " + string.Join("; ", failures));
        }
        finally { await dispose(); }
    }

    [Fact]
    public async Task Typing_costs_the_editing_thread_an_enqueue_while_the_server_is_slow()
    {
        var mapper = new SshUriMapper(new RemoteAuthority("box"));
        var (_, _, _, sync, docs, dispose) = await ConnectAsync(mapper);
        try
        {
            double Measure(InMemoryTextDocument d, int n)
            {
                for (var i = 0; i < 100; i++) d.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "w")]);
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < n; i++) d.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "w")]);
                return sw.Elapsed.TotalMicroseconds / n;
            }
            var unsynced = new InMemoryTextDocument(new Uri("file:///local.ts"), "ts", "");
            using (docs.Register(unsynced)) { }
            var baseline = Measure(new InMemoryTextDocument(new Uri("file:///x.ts"), "ts", ""), 2000);
            var doc = new InMemoryTextDocument(mapper.FromRemotePath("/w/typed.ts"), "ts", "");
            using var reg = docs.Register(doc);
            await TestEnv.WaitUntilAsync(() => sync.SyncedCountAsync().Result == 1, TimeSpan.FromSeconds(5), "doc synced");
            var synced = Measure(doc, 2000); // the server sleeps 5 ms per change: a waiting typist would show it
            Assert.True(synced - baseline < 50, $"sync costs {synced - baseline:0.0} us per keystroke (baseline {baseline:0.0} us)");
        }
        finally { await dispose(); }
    }
}
