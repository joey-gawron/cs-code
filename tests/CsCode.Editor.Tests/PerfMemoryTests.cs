using System.Diagnostics;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// Opening big files (time, memory, UI responsiveness during the open), tokeniser memory per megabyte of source, and the leak guarantees:
/// 500 documents with views opened and closed, 500 view recreations from saved view state, a view retaining nothing after disposal.
/// The measured numbers are printed (and recorded in docs/EDITOR.md).
/// </summary>
public class PerfMemoryTests(ITestOutputHelper output)
{
    private const string CSharpBlock = """
        using System;
        using System.Collections.Generic;

        namespace Sample.Module
        {
            /// <summary>A realistic slice of C#: attributes, generics, strings, comments, lambdas.</summary>
            [Serializable]
            public sealed class Repository<TKey, TValue> where TKey : notnull
            {
                private readonly Dictionary<TKey, TValue> _items = new();
                private static readonly string Greeting = "hello, \"world\" é " + 42;

                public int Count => _items.Count; // number of items

                public bool TryGet(TKey key, out TValue? value)
                {
                    /* a block
                       comment */
                    if (_items.TryGetValue(key, out var found)) { value = found; return true; }
                    value = default;
                    return false;
                }

                public IEnumerable<TValue> Where(Func<TValue, bool> predicate)
                {
                    foreach (var item in _items.Values)
                        if (predicate(item)) yield return item;
                }
            }
        }

        """;

    private static string CSharpOfSize(int bytes)
    {
        var sb = new StringBuilder(bytes + CSharpBlock.Length);
        while (sb.Length < bytes) sb.Append(CSharpBlock);
        return sb.ToString(0, bytes);
    }

    private static async Task<List<double>> RunHeartbeat(Func<Task> work)
    {
        var latencies = new List<double>();
        var stop = false;
        var beat = Task.Run(async () =>
        {
            while (!Volatile.Read(ref stop))
            {
                var posted = Stopwatch.GetTimestamp();
                Dispatcher.UIThread.Post(() =>
                {
                    lock (latencies) latencies.Add((Stopwatch.GetTimestamp() - posted) * 1000.0 / Stopwatch.Frequency);
                }, DispatcherPriority.Normal);
                await Task.Delay(5).ConfigureAwait(false);
            }
        });
        await work();
        Volatile.Write(ref stop, true);
        await beat;
        lock (latencies) return latencies.ToList();
    }

    // ---- opening big files ----

    [AvaloniaTheory]
    [InlineData(1, 5_000)]
    [InlineData(10, 20_000)]
    [InlineData(50, 90_000)]
    public async Task Opening_a_big_file_is_quick_bounded_in_memory_and_does_not_freeze_the_ui(int megabytes, int budgetMs)
    {
        using var host = ViewHost.Create();
        var path = host.Dir.WriteFile($"big{megabytes}.cs", CSharpOfSize(megabytes * 1024 * 1024));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalMemory(true);

        EditorDocument? doc = null;
        var sw = Stopwatch.StartNew();
        var beats = await RunHeartbeat(async () => doc = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(path)));
        var openMs = sw.ElapsedMilliseconds;
        var afterOpen = GC.GetTotalMemory(true);

        sw.Restart();
        var view = (CodeEditorControl)host.Factory.CreateView(doc!);
        var window = new Avalonia.Controls.Window { Width = 900, Height = 600, Content = view };
        window.Show();
        ViewHost.Pump();
        var viewMs = sw.ElapsedMilliseconds;
        var afterView = GC.GetTotalMemory(true);

        output.WriteLine($"{megabytes} MB: open {openMs} ms; UI latency during open p99={Percentile(beats, .99):F1} ms max={beats.Max():F1} ms; view + first layout {viewMs} ms; large-file mode={doc!.IsLargeFile}");
        output.WriteLine($"{megabytes} MB: managed memory after open {(afterOpen - before) / 1048576.0:F0} MB ({(afterOpen - before) / (megabytes * 1048576.0):F2} x file size), with a view {(afterView - before) / 1048576.0:F0} MB");

        Assert.True(openMs < budgetMs, $"opening {megabytes} MB took {openMs} ms (budget {budgetMs})");
        Assert.True(beats.Max() < 250, $"the UI thread was blocked {beats.Max():F0} ms while opening {megabytes} MB");
        // (a small file also carries the fixed cost of the editor's tables: allow 16 MB on top)
        Assert.True(afterOpen - before < megabytes * 1048576L * 4.5 + 16 * 1048576L, "a document must not cost more than ~4.5x its size in managed memory (UTF-16 text + line index)");
        Assert.Equal(megabytes > 5, doc.IsLargeFile);
        Assert.True(viewMs < 6000, $"creating a view and laying out the first screen took {viewMs} ms");
        host.Factory.DisposeView(view);
        window.Close();
        await host.Factory.CloseDocumentAsync(doc);
    }

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];
    }

    [AvaloniaFact]
    public async Task A_big_file_stays_scrollable_and_editable_in_large_file_mode()
    {
        using var host = ViewHost.Create();
        var path = host.Dir.WriteFile("big.cs", CSharpOfSize(12 * 1024 * 1024));
        var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(path));
        var view = (CodeEditorControl)host.Factory.CreateView(doc);
        var window = new Avalonia.Controls.Window { Width = 900, Height = 600, Content = view };
        window.Show();
        ViewHost.Pump();
        Assert.True(doc.IsLargeFile);
        Assert.Equal("plaintext", doc.LanguageId);
        Assert.Null(view.Feature<CsCode.Editor.Features.FoldingFeature>()); // none of the language features exist for a large file
        var times = new List<double>();
        view.Controller.SetSelection(TextRange.At(doc.LineCount / 2, 0));
        for (var i = 0; i < 100; i++)
        {
            var t = Stopwatch.GetTimestamp();
            view.TextArea.PerformTextInput("x");
            times.Add((Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency);
            if (i % 10 == 0) ViewHost.Pump();
        }
        output.WriteLine($"typing in a 12 MB file: p50={Percentile(times, .5):F2} ms p99={Percentile(times, .99):F2} ms");
        Assert.True(Percentile(times, .99) < 25, $"typing in a large file took {Percentile(times, .99):F1} ms at p99");
        host.Factory.DisposeView(view);
        window.Close();
    }

    // ---- tokeniser memory ----

    [AvaloniaFact]
    public async Task Tokeniser_memory_per_megabyte_of_source_is_measured_and_bounded_and_released_with_the_view()
    {
        using var host = ViewHost.Create();
        var path = host.Dir.WriteFile("src.cs", CSharpOfSize(2 * 1024 * 1024 - 4096)); // just under the large-file threshold of 5 MB? no: 2 MB, a normal file
        var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(path));
        Assert.False(doc.IsLargeFile);
        GC.Collect();
        var before = GC.GetTotalMemory(true);
        var view = (CodeEditorControl)host.Factory.CreateView(doc);
        var window = new Avalonia.Controls.Window { Width = 900, Height = 600, Content = view };
        window.Show();
        ViewHost.Pump();
        view.Controller.SetSelection(TextRange.At(0, 0));
        var tokens = view.Tokens!;
        // scroll through the whole file so every line is tokenised, as a user reading it would
        var sw = Stopwatch.StartNew();
        var wanted = doc.LineCount;
        while (tokens.Store.ValidLines < wanted && sw.Elapsed < TimeSpan.FromSeconds(90))
        {
            view.Controller.Reveal(TextRange.At(Math.Min(wanted - 1, tokens.Store.ValidLines + 40), 0), center: false);
            await Task.Delay(20);
            ViewHost.Pump();
        }
        var tokenisedLines = tokens.Store.ValidLines;
        var storeBytes = tokens.Store.EstimatedBytes;
        var measured = GC.GetTotalMemory(true) - before;
        var mb = (2 * 1024 * 1024 - 4096) / 1048576.0;
        output.WriteLine($"tokenised {tokenisedLines}/{wanted} lines of {mb:F1} MB in {sw.Elapsed.TotalSeconds:F1} s; token store (estimate) {storeBytes / 1048576.0 / mb:F1} MB per MB of source; process delta incl. view and colour tables {measured / 1048576.0 / mb:F1} MB per MB");
        Assert.True(tokenisedLines >= wanted - 1, $"tokenisation reached {tokenisedLines} of {wanted} lines");
        Assert.True(storeBytes / 1048576.0 / mb < 80, $"the token store must stay below 80 MB per MB of source, was {storeBytes / 1048576.0 / mb:F1}");

        host.Factory.DisposeView(view);
        window.Close();
        await ViewHost.WaitFor(() => tokens.Store.EstimatedBytes < storeBytes * 0.4, "token memory released when the last view went away (the per-line slots stay: 32 B each)", 5000);
        output.WriteLine($"after the view was disposed: token store {tokens.Store.EstimatedBytes / 1024.0:F0} KB");
    }

    // ---- leaks ----

    [AvaloniaFact]
    public async Task Opening_and_closing_500_documents_with_views_retains_nothing()
    {
        using var host = ViewHost.Create();
        var window = new Avalonia.Controls.Window { Width = 800, Height = 500 };
        window.Show();
        var weak = new List<WeakReference>();
        await OpenCloseAsync(host, window, weak);
        // LeakAssert-style: every one of the 500 documents, views, editors and buffers must be collectable once closed
        await CollectAsync(weak);
        var alive = weak.Select((w, i) => (w, i)).Where(x => x.w.IsAlive).Select(x => x.i).ToList();
        Assert.True(alive.Count == 0, $"{alive.Count} of {weak.Count} closed documents/views are still reachable: indices {string.Join(",", alive.Take(20))}" + Why(host, alive.Select(i => weak[i])));
        window.Close();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task OpenCloseAsync(ViewHost host, Avalonia.Controls.Window window, List<WeakReference> weak)
    {
        for (var i = 0; i < 500; i++)
        {
            var path = host.Dir.WriteFile($"leak{i}.cs", $"class C{i} {{ void M() {{ var x = \"{i}\"; }} }}\n// document {i}\n");
            var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(path));
            var view = (CodeEditorControl)host.Factory.CreateView(doc);
            window.Content = view;
            ViewHost.Pump();
            view.TextArea.PerformTextInput("x");
            view.Services.Diagnostics.Set(doc.Uri, "t", [new CsCode.Core.Languages.Diagnostic(TextRange.FromLines(0, 0, 0, 5), "d")]);
            if (i % 50 == 0) await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines > 0, "tokens");
            window.Content = null;
            weak.Add(new WeakReference(doc));
            weak.Add(new WeakReference(view));
            weak.Add(new WeakReference(view.Editor));
            weak.Add(new WeakReference(doc.Buffer));
            host.Factory.DisposeView(view);
            await host.Factory.CloseDocumentAsync(doc);
            host.Factory.Services.Diagnostics.Clear(doc.Uri, "t");
        }
    }

    [AvaloniaFact]
    public async Task Recreating_a_view_from_its_saved_state_500_times_retains_no_old_view()
    {
        using var host = ViewHost.Create();
        var (doc, first, window) = await host.OpenAsync(string.Join('\n', Enumerable.Range(0, 400).Select(i => $"    line {i} of text {{ }}")), "state.cs");
        var state = first.Controller.GetViewState();
        host.Factory.DisposeView(first);
        var weak = new List<WeakReference>();
        await RecreateAsync(host, doc, window, state, weak);
        await CollectAsync(weak);
        var alive = weak.Count(w => w.IsAlive);
        var which = string.Join(",", weak.Select((w, i) => (w, i)).Where(x => x.w.IsAlive).Select(x => x.i));
        Assert.True(alive == 0, $"{alive} of {weak.Count} disposed views are still reachable (retained by the document, the factory or a provider): indices {which}" + Why(host, weak.Where(w => w.IsAlive)));
        // and the document itself carries no per-view residue: a new view works and is the only one registered
        var last = (CodeEditorControl)host.Factory.CreateView(doc);
        Assert.Single(host.Factory.Views);
        host.Factory.DisposeView(last);
    }

    /// <summary>For a failure message: the chain of strong references that keeps a supposedly dead object alive (from the DI container, the factory and the dispatcher).</summary>
    private static string Why(ViewHost host, IEnumerable<WeakReference> weak)
    {
        var target = weak.Select(w => w.Target).FirstOrDefault(t => t is not null);
        if (target is null) return "";
        var roots = new List<object> { host.Provider, host.Factory, host.Documents, Dispatcher.UIThread };
        if (Avalonia.Application.Current is { } app) roots.Add(app);
        return "\n retention path to " + target.GetType().Name + ":\n   " + (RetentionPath.Find(roots, target) ?? "(not reachable from the DI container / factory / dispatcher / application: held by a thread or static)");
    }

    /// <summary>Forces collections for up to 3 s: a pending debounce timer (≤ 300 ms) may still hold a view for a moment after it was disposed.</summary>
    private static async Task CollectAsync(IEnumerable<WeakReference> weak)
    {
        for (var attempt = 0; attempt < 60 && weak.Any(w => w.IsAlive); attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            ViewHost.Pump();
            await Task.Delay(50);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task RecreateAsync(ViewHost host, EditorDocument doc, Avalonia.Controls.Window window, EditorViewState state, List<WeakReference> weak)
    {
        for (var i = 0; i < 500; i++)
        {
            var view = (CodeEditorControl)host.Factory.CreateView(doc);
            window.Content = view;
            view.Controller.RestoreViewState(state);
            view.TextArea.PerformTextInput("x"); // an edit per view: the document's undo history must not pin the view that made it
            if (i % 5 == 0) ViewHost.Pump();
            if (i % 100 == 0) await Task.Delay(5);
            state = view.Controller.GetViewState();
            window.Content = null;
            host.Factory.DisposeView(view);
            weak.Add(new WeakReference(view));
            weak.Add(new WeakReference(view.Editor));
        }
    }

    [AvaloniaFact]
    public async Task A_disposed_view_is_collectable_even_with_every_popup_open_and_providers_registered()
    {
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<CsCode.Core.Languages.ICompletionProvider>(DocumentSelector.All, FakeCompletionProvider.Items("alpha", "alphabet"));
        host.Factory.Services.Features.Register<CsCode.Core.Languages.IHoverProvider>(DocumentSelector.All, new FakeHoverProvider((_, _, _) => Task.FromResult<CsCode.Core.Languages.Hover?>(new CsCode.Core.Languages.Hover([CsCode.Core.Languages.MarkupContent.Markdown("**hi**")]))));
        // (not focused: a FOCUSED AvaloniaEdit editor stays reachable from Avalonia's focus / input-method machinery until focus moves elsewhere — the
        //  same is true of a bare TextEditor — and the shell moves focus when it closes a tab)
        var path = host.Dir.WriteFile("leak.txt", "alp alpha\n");
        var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(path));
        var view = (CodeEditorControl)host.Factory.CreateView(doc);
        var window = new Avalonia.Controls.Window { Width = 800, Height = 500, Content = view };
        window.Show();
        ViewHost.Pump();
        ViewTestHelpers.Type(view, "al");
        await ViewHost.WaitFor(() => view.Feature<CsCode.Editor.Features.CompletionFeature>()!.IsOpen, "completion popup open");
        await view.Feature<CsCode.Editor.Features.HoverFeature>()!.HoverAtAsync(1);
        ViewHost.Pump();
        var weak = new[] { new WeakReference(view), new WeakReference(view.Editor), new WeakReference(view.Overlay) };
        window.Content = null;
        host.Factory.DisposeView(view);
        view = null!;
        await CollectAsync(weak);
        Assert.All(weak, w => Assert.False(w.IsAlive, "the disposed view (or a part of it) is still reachable" + Why(host, weak.Where(x => x.IsAlive))));
        Assert.NotNull(doc);
    }
}
