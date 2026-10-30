using Avalonia.Headless.XUnit;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.TreeSitter;
using CsCode.Editor.TreeSitter.Native;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterTestSupport;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// Lifetime of the tree-sitter engine: documents, views, sessions and engines are collectable after close, and the NATIVE memory (parsers,
/// trees, cursors, injection trees) comes back — counted exactly by the allocation-tracking allocator. Serial (native accounting is
/// process-wide).
/// </summary>
[Collection(TreeSitterBenchCollection.Name)]
public class TreeSitterLeakTests
{
    [AvaloniaFact]
    public async Task Opening_editing_and_closing_documents_releases_managed_and_native_memory()
    {
        RequireGrammar("c_sharp");
        RequireGrammar("markdown");
        Assert.SkipUnless(TsRuntime.AllocationsTracked, "allocation tracking is off (CSCODE_TREESITTER_TRACK_ALLOC=0)");
        using var host = ViewHost.Create();
        host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TreeSitter);

        // warm-up: grammars, queries, injection parsers and the stack cache are allocated once per process/registry
        await Cycle(host, 8);
        await DrainWorker(host);
        var baseline = TsRuntime.AllocatedBytes;

        var weak = await Released(host, 40);
        // Avalonia keeps the FOCUSED view reachable until focus moves (docs/EDITOR.md): give focus to another view first
        var (sinkDoc, sink, sinkWindow) = await host.OpenAsync("// focus sink\n", "sink.cs", 300, 200);
        sink.Editor.TextArea.Focus();
        ViewHost.Pump();
        await DrainWorker(host);
        for (var i = 0; i < 10 && weak.Any(w => w.TryGetTarget(out _)); i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            ViewHost.Pump();
        }
        var alive = weak.Select(w => w.TryGetTarget(out var t) ? t.GetType().Name : null).Where(n => n is not null).ToArray();
        Assert.True(alive.Length == 0, "still reachable: " + string.Join(", ", alive));
        host.Factory.DisposeView(sink);
        host.Close(sinkWindow);
        await host.Factory.CloseDocumentAsync(sinkDoc);
        await DrainWorker(host);
        var after = TsRuntime.AllocatedBytes;
        Assert.Equal(0, host.Factory.Tokens.SessionCount);
        Assert.True(after - baseline < 64 * 1024, $"tree-sitter native bytes: baseline {baseline}, after 40 documents {after}");
    }

    [AvaloniaFact]
    public async Task Destroying_the_last_view_releases_the_syntax_tree_and_a_new_view_reparses()
    {
        RequireGrammar("rust");
        using var host = ViewHost.Create();
        host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TreeSitter);
        var text = string.Concat(Enumerable.Range(0, 300).Select(i => $"fn f{i}() -> i32 {{ {i} }}\n"));
        var (doc, view, window) = await host.OpenAsync(text, "keep.rs");
        await TreeSitterViewTests.WaitForTreeSitter(view);
        var session = view.Tokens!;
        Assert.True(session.TreeSitterBytes > 0);
        host.Factory.DisposeView(view);
        host.Close(window);
        await ViewHost.WaitFor(() => session.TreeSitter is null, "engine released with the last view");
        Assert.Equal(0, session.TreeSitterBytes);

        var again = (Editor.View.CodeEditorControl)host.Factory.CreateView(doc);
        var w2 = new Avalonia.Controls.Window { Width = 800, Height = 500, Content = again };
        w2.Show();
        await TreeSitterViewTests.WaitForTreeSitter(again);
        Assert.True(Has(TreeSitterViewTests.ScopesAt(again, 0, 0), "storage.type.function"));
        w2.Close();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<object>[]> Released(ViewHost host, int count) =>
        (await Cycle(host, count)).Select(o => new WeakReference<object>(o)).ToArray();

    private static async Task<object[]> Cycle(ViewHost host, int count)
    {
        var released = new List<object>();
        for (var i = 0; i < count; i++)
        {
            var sample = i % 2 == 0 ? TreeSitterSamples.All.Single(s => s.Name == "csharp") : TreeSitterSamples.All.Single(s => s.Name == "markdown");
            var (doc, view, window) = await host.OpenAsync(sample.Text, $"cycle{i}-{sample.FileName}", 700, 400);
            await TreeSitterViewTests.WaitForTreeSitter(view, 5);
            Type(view, "xy");
            await ViewHost.WaitFor(() => view.Tokens!.ViewportReady is { } r && r.Version == doc.Version, "re-coloured");
            var engine = view.Tokens!.TreeSitter!;
            released.Add(view);
            released.Add(engine);
            released.Add(view.Tokens);
            host.Factory.DisposeView(view);
            host.Close(window);
            await host.Factory.CloseDocumentAsync(doc);
            released.Add(doc);
        }
        ViewHost.Pump();
        return [.. released];
    }

    /// <summary>Engine disposal is posted to the editor worker: wait until it has run everything queued.</summary>
    private static async Task DrainWorker(ViewHost host)
    {
        var worker = host.Factory.Worker;
        for (var i = 0; i < 3; i++)
        {
            await worker.RunAsync(Editor.Threading.WorkPriority.Idle, _ => 0);
            ViewHost.Pump();
        }
    }
}
