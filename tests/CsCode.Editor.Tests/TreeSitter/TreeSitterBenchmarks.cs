using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Headless.XUnit;
using CsCode.Core.Platform;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.TextMate;
using CsCode.Editor.TreeSitter;
using CsCode.Editor.TreeSitter.Highlighting;
using CsCode.Editor.TreeSitter.Native;
using CsCode.Editor.View;
using TextMateSharp.Grammars;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterTestSupport;

namespace CsCode.Editor.Tests.TreeSitter;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TreeSitterBenchCollection
{
    public const string Name = "Tree-sitter benchmarks";
}

/// <summary>
/// The measurements behind docs/TREE-SITTER-EVALUATION.md: TextMate vs tree-sitter on the editor's own scenarios. OFF by default (they take
/// minutes and want a quiet machine): <c>tools/bench-treesitter.sh</c> runs them with <c>CSCODE_BENCH_TREESITTER=1</c>, twice (timings with
/// the counting allocator off, memory with it on), and every number is written with the 1-minute load average next to it to
/// <c>.scratch/bench/treesitter-&lt;run&gt;.md</c>. The assertions only check that each scenario completed: the numbers are the result.
/// </summary>
[Collection(TreeSitterBenchCollection.Name)]
public class TreeSitterBenchmarks
{
    private static bool Enabled => Environment.GetEnvironmentVariable("CSCODE_BENCH_TREESITTER") == "1";

    private static void RequireBench() => Assert.SkipUnless(Enabled, "benchmark: run tools/bench-treesitter.sh (CSCODE_BENCH_TREESITTER=1)");

    // ---- reporting ----

    private static readonly object LogGate = new();

    private static string LogPath
    {
        get
        {
            var run = Environment.GetEnvironmentVariable("CSCODE_BENCH_RUN") ?? DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            return Path.Combine(TreeSitterQualityTests.ScratchDir("bench"), $"treesitter-{run}.md");
        }
    }

    internal static double Load1()
    {
        try
        {
            return double.Parse(File.ReadAllText("/proc/loadavg").Split(' ')[0], CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            return double.NaN;
        }
    }

    private static void Record(string scenario, string engine, string metric, double value, string unit)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"| {scenario} | {engine} | {metric} | {value:0.###} | {unit} | {Load1():0.00} | {(TsRuntime.AllocationsTracked ? "on" : "off")} |\n");
        lock (LogGate)
        {
            var path = LogPath;
            if (!File.Exists(path)) // tools/bench-treesitter.sh writes the header (machine, load, commit) itself
                File.WriteAllText(path, "| scenario | engine | metric | value | unit | load1 | alloc tracking |\n|---|---|---|---:|---|---:|---|\n");
            File.AppendAllText(path, line);
        }
    }

    // ---- inputs ----

    private static string? _corpus;

    /// <summary>Real C#: this repository's own sources, concatenated (≈ 3 MB), repeated to the size wanted.</summary>
    private static string CSharp(long bytes)
    {
        _corpus ??= string.Concat(Directory.EnumerateFiles(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src")), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText));
        var sb = new StringBuilder((int)Math.Min(bytes + _corpus.Length, int.MaxValue / 2));
        while (sb.Length < bytes) sb.Append(_corpus.AsSpan(0, (int)Math.Min(_corpus.Length, bytes - sb.Length)));
        // end on a line boundary so the repetition does not split a token
        var text = sb.ToString();
        var cut = text.LastIndexOf('\n');
        return cut > 0 ? text[..(cut + 1)] : text;
    }

    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MB";

    private static void FullGc()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    // ---- B1: engine throughput and memory, outside the editor ----

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public void B1_whole_document_parse_and_tokenise_throughput_and_memory(int megabytes)
    {
        RequireBench();
        var bundle = RequireGrammar("c_sharp");
        var size = megabytes * 1024L * 1024L;
        var text = CSharp(size);
        var input = new StringInput(text);
        var scenario = $"B1 whole document, {Mb(text.Length * 2L)} UTF-16 ({megabytes} MB of C#, {input.LineCount} lines)";

        // tree-sitter: parse (one call, no slicing), then highlight everything, then one incremental edit
        using var registry = NewRegistry();
        TsRuntime.TrimNativeHeap();
        var nativeBefore = TsRuntime.AllocatedBytes;
        var heapBefore = TsRuntime.ProcessNativeHeapBytes();
        using (var doc = new SyntaxDocument(registry, bundle))
        {
            var sw = Stopwatch.StartNew();
            Assert.Equal(ParseOutcome.Completed, doc.Parse(input, long.MaxValue));
            var parse = sw.Elapsed;
            Record(scenario, "tree-sitter", "full parse", parse.TotalMilliseconds, "ms");
            Record(scenario, "tree-sitter", "parse throughput", text.Length / 1024.0 / 1024.0 / parse.TotalSeconds, "M chars/s");
            if (TsRuntime.AllocationsTracked) Record(scenario, "tree-sitter", "tree native bytes (counted)", (TsRuntime.AllocatedBytes - nativeBefore) / 1024.0 / 1024.0, "MB");
            Record(scenario, "tree-sitter", "tree native bytes (C heap delta)", (TsRuntime.ProcessNativeHeapBytes() - heapBefore) / 1024.0 / 1024.0, "MB");
            Record(scenario, "tree-sitter", "tree bytes estimate (nodes x 48)", doc.EstimatedTreeBytes / 1024.0 / 1024.0, "MB");

            var stacks = new TestStacks();
            var paint = new PaintBuffer();
            sw.Restart();
            var limit = megabytes >= 100 ? Math.Min(input.LineCount, 200_000) : input.LineCount; // 100 MB: highlight the first 200k lines, scale
            for (var line = 0; line < limit; line += 2000)
                doc.Highlight(input, input.LineStart(line), input.LineStart(Math.Min(limit, line + 2000)) - 1, stacks, paint);
            var perLine = sw.Elapsed.TotalMilliseconds / limit;
            Record(scenario, "tree-sitter", "highlight (query + paint) per 1000 lines", perLine * 1000, "ms");

            // one keystroke in the middle: edit + incremental reparse
            var at = text.Length / 2;
            var lineOfEdit = input.LineOfOffset(at);
            var col = at - input.LineStart(lineOfEdit);
            var edited = text.Insert(at, "x");
            doc.ApplyEdit(new TextEditInfo(at, at, at + 1, lineOfEdit, col, lineOfEdit, col, lineOfEdit, col + 1));
            sw.Restart();
            doc.Parse(new StringInput(edited), long.MaxValue);
            Record(scenario, "tree-sitter", "incremental reparse after 1 char (incl. new line index)", sw.Elapsed.TotalMilliseconds, "ms");
        }
        TsRuntime.TrimNativeHeap();
        if (TsRuntime.AllocationsTracked) Record(scenario, "tree-sitter", "native bytes after dispose (counted)", (TsRuntime.AllocatedBytes - nativeBefore) / 1024.0 / 1024.0, "MB");

        // TextMate: tokenise every line the way TokenizationSession does, into a LineTokenStore (time-capped for 100 MB)
        using var host = TestHost.Create();
        var grammars = new GrammarService(host.Services);
        var grammar = grammars.GrammarForLanguage("csharp")!;
        var interner = new ScopeInterner();
        var store = new LineTokenStore();
        store.Reset(input.LineCount, 1);
        FullGc();
        var managedBefore = GC.GetTotalMemory(true);
        IStateStack? state = null;
        var batch = new List<int[]>(1000);
        var pairs = new List<int>();
        var cap = TimeSpan.FromSeconds(megabytes >= 100 ? 90 : 600);
        var clock = Stopwatch.StartNew();
        var done = 0;
        for (var line = 0; line < input.LineCount && clock.Elapsed < cap; line++)
        {
            var start = input.LineStart(line);
            var end = line + 1 < input.LineCount ? input.LineStart(line + 1) : text.Length;
            var lineText = text[start..end].TrimEnd('\r', '\n');
            var result = grammar.TokenizeLine(lineText.Length > TokenizationSession.MaxTokenizedLineLength ? lineText[..TokenizationSession.MaxTokenizedLineLength] : lineText, state, TimeSpan.FromSeconds(1));
            state = result.RuleStack;
            pairs.Clear();
            var previous = -1;
            foreach (var token in result.Tokens)
            {
                var id = interner.Intern(token.Scopes);
                if (id == previous) continue;
                pairs.Add(Math.Min(token.StartIndex, lineText.Length));
                pairs.Add(id);
                previous = id;
            }
            batch.Add(pairs.ToArray());
            if (batch.Count == 1000)
            {
                store.Publish(1, line - 999, batch, [], chain: false);
                batch.Clear();
            }
            done++;
        }
        var elapsed = clock.Elapsed;
        var fraction = done / (double)input.LineCount;
        Record(scenario, "TextMate", done == input.LineCount ? "full tokenise" : $"full tokenise (extrapolated from {fraction:P0} in {elapsed.TotalSeconds:0} s)", elapsed.TotalMilliseconds / fraction, "ms");
        Record(scenario, "TextMate", "tokenise throughput", text.Length * fraction / 1024.0 / 1024.0 / elapsed.TotalSeconds, "M chars/s");
        Record(scenario, "TextMate", "LineTokenStore estimate (tokenised part, scaled)", (store.EstimatedBytes + interner.EstimatedBytes) / fraction / 1024.0 / 1024.0, "MB");
        GC.KeepAlive(batch);
        var managedAfter = GC.GetTotalMemory(true);
        Record(scenario, "TextMate", "token store managed delta (scaled)", (managedAfter - managedBefore) / fraction / 1024.0 / 1024.0, "MB");
        GC.KeepAlive(store);
        Assert.True(done > 0);
    }

    // ---- B2/B3: in the editor: open → coloured viewport, and keystroke → re-coloured viewport ----

    public static TheoryData<int, bool> EditorCases => new()
    {
        { 1, false }, { 1, true }, { 10, false }, { 10, true },
    };

    [AvaloniaTheory]
    [MemberData(nameof(EditorCases))]
    public async Task B2_B3_open_and_typing_in_the_editor(int megabytes, bool treeSitter)
    {
        RequireBench();
        RequireGrammar("c_sharp");
        var engine = treeSitter ? "tree-sitter" : "TextMate";
        var text = CSharp(megabytes * 1024L * 1024L);
        using var host = ViewHost.Create();
        // 10 MB is above the large-file threshold (5 MB: plain text, no engine at all); raise it to compare the engines on 10 MB
        host.Config.Set(SettingKeys.EditorLargeFileOptimizationsLimit, 200L * 1024 * 1024);
        host.Config.Set(TreeSitterService.SettingKey, treeSitter);
        var scenario = $"B2/B3 editor, {megabytes} MB C# ({text.Count(c => c == '\n')} lines)";

        var sw = Stopwatch.StartNew();
        var (doc, view, _) = await host.OpenAsync(text, $"Bench{megabytes}.cs", 1000, 700);
        var opened = sw.Elapsed;
        await ViewHost.WaitFor(() => view.Tokens?.ViewportReady is { } r && r.Version == doc.Version, "coloured viewport", 300_000);
        Record(scenario, engine, "open + view (until laid out)", opened.TotalMilliseconds, "ms");
        Record(scenario, engine, "open → first coloured viewport", sw.Elapsed.TotalMilliseconds, "ms");
        var worker = host.Factory.Worker;
        Record(scenario, engine, "editor worker: longest job so far", worker.LongestJob.TotalMilliseconds, "ms");

        // wait for the read-ahead to settle, then type in the middle of the visible lines
        await ViewHost.WaitFor(() => view.Tokens!.IsQuiet && (view.Tokens.TreeSitter is null || !view.Tokens.TreeSitter.Syntax.IsParsing), "settled", 300_000);
        await Task.Delay(400);
        ViewHost.Pump();
        view.Controller.SetSelection(CsCode.Core.Text.TextRange.At(10, 0));
        var latencies = new List<double>();
        var keystrokeCost = new List<double>();
        for (var i = 0; i < 150; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            Type(view, i % 10 == 9 ? " " : "a");
            keystrokeCost.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            var version = doc.Version;
            await ViewHost.WaitFor(() => view.Tokens!.ViewportReady is { } r && r.Version >= version, "re-coloured viewport", 60_000);
            var ready = view.Tokens!.ViewportReady!;
            if (i >= 10) latencies.Add(Stopwatch.GetElapsedTime(t0, ready.Timestamp).TotalMilliseconds);
            await Task.Delay(15);
        }
        latencies.Sort();
        keystrokeCost.Sort();
        double P(List<double> l, double q) => l[Math.Min(l.Count - 1, (int)(l.Count * q))];
        Record(scenario, engine, "keystroke → viewport re-coloured (worker) p50", P(latencies, 0.5), "ms");
        Record(scenario, engine, "keystroke → viewport re-coloured (worker) p99", P(latencies, 0.99), "ms");
        Record(scenario, engine, "keystroke handling on the UI thread p50", P(keystrokeCost, 0.5), "ms");
        Record(scenario, engine, "keystroke handling on the UI thread p99", P(keystrokeCost, 0.99), "ms");
        Record(scenario, engine, "editor worker: jobs over 10 ms", worker.JobsOver10Ms, "jobs");
        if (view.Tokens!.TreeSitter is { } ts)
        {
            Record(scenario, engine, "last incremental parse (worker)", ts.LastParseTime.TotalMilliseconds, "ms");
            Record(scenario, engine, "syntax tree estimate", ts.EstimatedBytes / 1024.0 / 1024.0, "MB");
        }
        Record(scenario, engine, "LineTokenStore estimate", view.Tokens.Store.EstimatedBytes / 1024.0 / 1024.0, "MB");
        Assert.NotEmpty(latencies);
    }

    // ---- B4: memory per open document ----

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task B4_memory_per_open_document(bool treeSitter)
    {
        RequireBench();
        RequireGrammar("c_sharp");
        var engine = treeSitter ? "tree-sitter" : "TextMate";
        const int count = 20;
        var text = CSharp(100 * 1024);
        using var host = ViewHost.Create();
        host.Config.Set(TreeSitterService.SettingKey, treeSitter);
        // warm up the grammar/theme/JIT with one document, so the per-document numbers do not carry one-time costs
        var (_, warm, warmWindow) = await host.OpenAsync(text, "Warm.cs", 900, 600);
        await ViewHost.WaitFor(() => warm.Tokens?.ViewportReady is not null && warm.Tokens.IsQuiet, "warm-up", 120_000);
        await Task.Delay(500);
        FullGc();
        TsRuntime.TrimNativeHeap();
        var managed0 = GC.GetTotalMemory(true);
        var native0 = TsRuntime.AllocatedBytes;
        var heap0 = TsRuntime.ProcessNativeHeapBytes();
        var views = new List<CodeEditorControl>();
        for (var i = 0; i < count; i++)
        {
            var (_, v, _) = await host.OpenAsync(text.Replace("namespace", "namespace /*" + i + "*/", StringComparison.Ordinal), $"Doc{i}.cs", 900, 600);
            views.Add(v);
        }
        foreach (var v in views)
            await ViewHost.WaitFor(() => v.Tokens?.ViewportReady is not null && (v.Tokens.TreeSitter is null || !v.Tokens.TreeSitter.Syntax.IsParsing), "tokens", 120_000);
        await Task.Delay(600); // read-ahead after the quiet period
        await ViewHost.WaitFor(() => views.All(v => v.Tokens!.IsQuiet), "quiet", 60_000);
        await Task.Delay(800);
        ViewHost.Pump();
        FullGc();
        var scenario = $"B4 memory per open document ({count} × {text.Length / 1024} K chars of C#, one view each)";
        Record(scenario, engine, "managed per document (incl. AvaloniaEdit view)", (GC.GetTotalMemory(true) - managed0) / (double)count / 1024, "KB");
        if (TsRuntime.AllocationsTracked) Record(scenario, engine, "tree-sitter native per document (counted)", (TsRuntime.AllocatedBytes - native0) / (double)count / 1024, "KB");
        Record(scenario, engine, "C heap per document (delta)", (TsRuntime.ProcessNativeHeapBytes() - heap0) / (double)count / 1024, "KB");
        Record(scenario, engine, "LineTokenStore per document", views.Average(v => v.Tokens!.Store.EstimatedBytes) / 1024, "KB");
        Record(scenario, engine, "lines with tokens per document", views.Average(v => Enumerable.Range(0, v.Document.LineCount).Count(l => v.Tokens!.Store.GetTokens(l) is not null)), "lines");
        if (treeSitter) Record(scenario, engine, "syntax tree estimate per document", views.Average(v => v.Tokens!.TreeSitterBytes) / 1024, "KB");
        GC.KeepAlive(warmWindow);
    }

    // ---- B5: 300-document soak ----

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task B5_300_document_soak(bool treeSitter)
    {
        RequireBench();
        RequireGrammar("c_sharp");
        var engine = treeSitter ? "tree-sitter" : "TextMate";
        using var host = ViewHost.Create();
        host.Config.Set(TreeSitterService.SettingKey, treeSitter);
        var samples = TreeSitterSamples.All;
        var scenario = "B5 soak: 300 documents (8 languages, ~6-20 K chars), view + 5 keystrokes each, view destroyed, then all closed";
        FullGc();
        TsRuntime.TrimNativeHeap();
        var managed0 = GC.GetTotalMemory(true);
        var native0 = TsRuntime.AllocatedBytes;
        var sw = Stopwatch.StartNew();
        var docs = new List<IEditorDocumentHolder>();
        long peakNative = 0;
        for (var i = 0; i < 300; i++)
        {
            var s = samples[i % samples.Length];
            var text = string.Concat(Enumerable.Repeat(s.Text + "\n", 1 + i % 8));
            var (doc, view, window) = await host.OpenAsync(text, $"{i}-{s.FileName}", 700, 400);
            await ViewHost.WaitFor(() => view.Tokens is null || view.Tokens.ViewportReady is not null, "coloured", 60_000);
            Type(view, "abcde");
            if (view.Tokens is not null)
                await ViewHost.WaitFor(() => view.Tokens!.ViewportReady is { } r && r.Version == doc.Version, "re-coloured", 60_000);
            peakNative = Math.Max(peakNative, TsRuntime.AllocatedBytes - native0);
            host.Factory.DisposeView(view);
            host.Close(window);
            docs.Add(new IEditorDocumentHolder(doc));
        }
        ViewHost.Pump();
        await Task.Delay(300);
        FullGc();
        Record(scenario, engine, "elapsed (300 open/type/destroy-view)", sw.Elapsed.TotalSeconds, "s");
        Record(scenario, engine, "managed after 300 open, views destroyed", (GC.GetTotalMemory(true) - managed0) / 1024.0 / 1024.0, "MB");
        if (TsRuntime.AllocationsTracked)
        {
            Record(scenario, engine, "tree-sitter native peak", peakNative / 1024.0 / 1024.0, "MB");
            Record(scenario, engine, "tree-sitter native, views destroyed (trees released)", (TsRuntime.AllocatedBytes - native0) / 1024.0 / 1024.0, "MB");
        }
        foreach (var d in docs) await host.Factory.CloseDocumentAsync(d.Doc);
        docs.Clear();
        ViewHost.Pump();
        await Task.Delay(300);
        ViewHost.Pump();
        FullGc();
        Record(scenario, engine, "managed after closing all (vs start)", (GC.GetTotalMemory(true) - managed0) / 1024.0 / 1024.0, "MB");
        if (TsRuntime.AllocationsTracked) Record(scenario, engine, "tree-sitter native after closing all (vs start)", (TsRuntime.AllocatedBytes - native0) / 1024.0 / 1024.0, "MB");
        Assert.Equal(0, host.Factory.Tokens.SessionCount);
    }

    private sealed record IEditorDocumentHolder(CsCode.Editor.Documents.EditorDocument Doc);
}
