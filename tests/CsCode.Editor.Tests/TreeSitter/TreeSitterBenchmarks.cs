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

    private static string[]? _files;

    /// <summary>
    /// Real C#: this repository's own sources (about 4.5 MB), whole files only, repeated to the size wanted. Files the C# grammar cannot parse are
    /// left out (one today: a list pattern with a slice designation, <c>['f', .. var n]</c>); their effect is measured on its own in B1c.
    /// </summary>
    private static string CSharp(long bytes)
    {
        if (_files is null)
        {
            var bundle = RequireGrammar("c_sharp");
            using var registry = NewRegistry();
            var kept = new List<string>();
            foreach (var file in Directory.EnumerateFiles(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src")), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal))
            {
                var text = File.ReadAllText(file) + "\n";
                using var doc = new SyntaxDocument(registry, bundle);
                doc.Parse(new StringInput(text), long.MaxValue);
                if (!doc.RootHasError) kept.Add(text);
                else DroppedFiles.Add(Path.GetFileName(file));
            }
            _files = [.. kept];
        }
        // whole files only (a file cut in half would leave an unterminated construct that the parser recovers from at great cost)
        var sb = new StringBuilder((int)Math.Min(bytes + 512 * 1024, int.MaxValue / 2));
        while (sb.Length < bytes)
            foreach (var f in _files)
            {
                if (sb.Length + f.Length > bytes && sb.Length > 0) return sb.ToString();
                sb.Append(f);
            }
        return sb.ToString();
    }

    private static readonly List<string> DroppedFiles = new();

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
        var scenario = $"B1 whole document, {megabytes} MB of C# ({text.Length / 1024 / 1024.0:0.#} M chars, {input.LineCount} lines; repo files the grammar cannot parse left out: {string.Join(", ", DroppedFiles)})";

        // tree-sitter: parse (one call, no slicing), viewport highlights at three places, incremental edits at three places
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
            Record(scenario, "tree-sitter", "tree has ERROR nodes (1 = yes)", doc.RootHasError ? 1 : 0, "flag");
            if (TsRuntime.AllocationsTracked) Record(scenario, "tree-sitter", "tree native bytes (counted)", (TsRuntime.AllocatedBytes - nativeBefore) / 1024.0 / 1024.0, "MB");
            Record(scenario, "tree-sitter", "tree native bytes (C heap delta)", (TsRuntime.ProcessNativeHeapBytes() - heapBefore) / 1024.0 / 1024.0, "MB");
            Record(scenario, "tree-sitter", "tree bytes estimate (nodes x 48)", doc.EstimatedTreeBytes / 1024.0 / 1024.0, "MB");

            var stacks = new TestStacks();
            var paint = new PaintBuffer();
            doc.Highlight(input, 0, input.LineStart(60), stacks, paint); // warm-up (first query execution, symbol-name cache)
            foreach (var at in new[] { 0.05, 0.5, 0.95 })
            {
                var first = (int)(input.LineCount * at);
                sw.Restart();
                doc.Highlight(input, input.LineStart(first), input.LineStart(Math.Min(input.LineCount - 1, first + 60)), stacks, paint);
                Record(scenario, "tree-sitter", $"highlight a 60-line viewport at {at:P0} of the document", sw.Elapsed.TotalMilliseconds, "ms");
            }

            // one keystroke inside an identifier at three places: edit + incremental reparse (the new text's line index is built outside the timing)
            var current = text;
            var times = new List<double>();
            foreach (var at in new[] { 0.25, 0.5, 0.75 })
            {
                var offset = (int)(current.Length * at);
                while (offset < current.Length - 1 && !(char.IsAsciiLetter(current[offset]) && char.IsAsciiLetter(current[offset - 1]))) offset++;
                var before = new StringInput(current);
                var line = before.LineOfOffset(offset);
                var col = offset - before.LineStart(line);
                current = current.Insert(offset, "x");
                var after = new StringInput(current);
                doc.ApplyEdit(new TextEditInfo(offset, offset, offset + 1, line, col, line, col, line, col + 1));
                sw.Restart();
                doc.Parse(after, long.MaxValue);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Record(scenario, "tree-sitter", "incremental reparse after 1 char (median of 3)", times[1], "ms");
            Record(scenario, "tree-sitter", "incremental reparse after 1 char (max of 3)", times[2], "ms");
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
        IStateStack? state = null;
        var batch = new List<int[]>(1000);
        var pairs = new List<int>();
        var cap = TimeSpan.FromSeconds(megabytes >= 100 ? 60 : 600);
        var clock = Stopwatch.StartNew();
        var done = 0;
        var batchStart = 0;
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
                store.Publish(1, batchStart, batch, [], chain: false);
                batch.Clear();
                batchStart = line + 1;
            }
            done++;
        }
        if (batch.Count > 0) store.Publish(1, batchStart, batch, [], chain: false);
        var elapsed = clock.Elapsed;
        var fraction = done / (double)input.LineCount;
        Record(scenario, "TextMate", done == input.LineCount ? "full tokenise" : $"full tokenise (extrapolated from {fraction:P0} in {elapsed.TotalSeconds:0} s)", elapsed.TotalMilliseconds / fraction, "ms");
        Record(scenario, "TextMate", "tokenise throughput", text.Length * fraction / 1024.0 / 1024.0 / elapsed.TotalSeconds, "M chars/s");
        Record(scenario, "TextMate", "LineTokenStore estimate (scaled to the whole document)", (store.EstimatedBytes + interner.EstimatedBytes) / fraction / 1024.0 / 1024.0, "MB");
        GC.KeepAlive(store);
        Assert.True(done > 0);
    }

    // ---- B1c: error recovery ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void B1c_one_unparsable_construct_and_incremental_reparsing(bool withUnparsable)
    {
        RequireBench();
        var bundle = RequireGrammar("c_sharp");
        // with the construct the parse can take many minutes: every parse here is capped (sliced with a deadline) and reported as "> cap"
        var cap = TimeSpan.FromSeconds(120);
        foreach (var classes in new[] { 2_000, 20_000 })
        {
            var body = string.Concat(Enumerable.Range(0, classes).Select(i => $"public class C{i} {{ int F{i}(int a) {{ return a * {i}; }} }}\n"));
            var text = (withUnparsable ? TreeSitterGuardTests.Unparsable : "namespace N;\n\n") + body;
            var scenario = $"B1c {text.Length / 1024} K chars of simple C#, {(withUnparsable ? "WITH" : "without")} one unparsable list pattern at the top";
            using var registry = NewRegistry();
            using var doc = new SyntaxDocument(registry, bundle);
            var sw = Stopwatch.StartNew();
            var outcome = doc.Parse(new StringInput(text), Stopwatch.GetTimestamp() + (long)(cap.TotalSeconds * Stopwatch.Frequency));
            if (outcome == ParseOutcome.Halted)
            {
                Record(scenario, "tree-sitter", $"full parse (did not finish: stopped at the {cap.TotalSeconds:0} s cap)", sw.Elapsed.TotalMilliseconds, "ms");
                continue;
            }
            Record(scenario, "tree-sitter", "full parse", sw.Elapsed.TotalMilliseconds, "ms");
            Record(scenario, "tree-sitter", "tree has ERROR nodes (1 = yes)", doc.RootHasError ? 1 : 0, "flag");
            var offset = text.IndexOf(" F", text.Length / 2, StringComparison.Ordinal) + 2;
            var before = new StringInput(text);
            var line = before.LineOfOffset(offset);
            var col = offset - before.LineStart(line);
            text = text.Insert(offset, "x");
            var after = new StringInput(text);
            doc.ApplyEdit(new TextEditInfo(offset, offset, offset + 1, line, col, line, col, line, col + 1));
            sw.Restart();
            outcome = doc.Parse(after, Stopwatch.GetTimestamp() + (long)(cap.TotalSeconds * Stopwatch.Frequency));
            Record(scenario, "tree-sitter", outcome == ParseOutcome.Completed ? "incremental reparse after 1 char" : $"incremental reparse after 1 char (stopped at the {cap.TotalSeconds:0} s cap)", sw.Elapsed.TotalMilliseconds, "ms");
        }
    }

    // ---- B1b: per-language throughput (1 MB of each evaluated language: its sample repeated) ----

    public static TheoryData<string> Languages => [.. TreeSitterSamples.All.Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(Languages))]
    public void B1b_per_language_parse_and_tokenise_throughput(string language)
    {
        RequireBench();
        var sample = TreeSitterSamples.All.Single(s => s.Name == language);
        var bundle = RequireGrammar(sample.Grammar);
        var sb = new StringBuilder();
        while (sb.Length < 1024 * 1024) sb.Append(sample.Text).Append('\n');
        var text = sb.ToString();
        var input = new StringInput(text);
        var scenario = $"B1b 1 MB of {language} (sample repeated, {input.LineCount} lines)";

        using var registry = NewRegistry();
        using (var doc = new SyntaxDocument(registry, bundle))
        {
            var sw = Stopwatch.StartNew();
            doc.Parse(input, long.MaxValue);
            Record(scenario, "tree-sitter", "full parse", sw.Elapsed.TotalMilliseconds, "ms");
            var stacks = new TestStacks();
            var paint = new PaintBuffer();
            sw.Restart();
            for (var line = 0; line < input.LineCount; line += 2000)
                doc.Highlight(input, input.LineStart(line), input.LineStart(Math.Min(input.LineCount - 1, line + 2000)), stacks, paint);
            Record(scenario, "tree-sitter", "highlight everything (incl. injections)", sw.Elapsed.TotalMilliseconds, "ms");
            Record(scenario, "tree-sitter", "tree bytes estimate", doc.EstimatedTreeBytes / 1024.0 / 1024.0, "MB");
        }

        var languageId = language switch { "tsx" => "typescriptreact", var l => l };
        using var host = TestHost.Create();
        var grammar = new GrammarService(host.Services).GrammarForLanguage(languageId);
        Assert.NotNull(grammar);
        IStateStack? state = null;
        var clock = Stopwatch.StartNew();
        for (var line = 0; line < input.LineCount; line++)
        {
            var start = input.LineStart(line);
            var end = line + 1 < input.LineCount ? input.LineStart(line + 1) : text.Length;
            state = grammar!.TokenizeLine(text[start..end].TrimEnd('\r', '\n'), state, TimeSpan.FromSeconds(1)).RuleStack;
        }
        Record(scenario, "TextMate", "full tokenise", clock.Elapsed.TotalMilliseconds, "ms");
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
        // 10 MB is above the large-file threshold (5 MB: plain text, no engine at all), and the repository's generated icon table has lines
        // over the 20,000-character long-line limit: raise both to compare the engines on 10 MB
        host.Config.Set(SettingKeys.EditorLargeFileOptimizationsLimit, 200L * 1024 * 1024);
        host.Config.Set("cscode.editor.largeFileLongLineChars", 100_000_000);
        host.Config.Set(TreeSitterService.SettingKey, treeSitter);
        // the give-up guard (5 s of parsing without a tree) would hand a 10 MB C# file to TextMate: measure tree-sitter itself here
        var guard = TreeSitterHighlighter.MaxParseTimeWithoutTree;
        TreeSitterHighlighter.MaxParseTimeWithoutTree = TimeSpan.FromMinutes(10);
        using var restoreGuard = CsCode.Core.Lifecycle.Disposable.Create(() => TreeSitterHighlighter.MaxParseTimeWithoutTree = guard);
        var scenario = $"B2/B3 editor, {megabytes} MB C# ({text.Count(c => c == '\n')} lines)";

        // warm-up: the first window of the process pays for Avalonia/Skia/font start-up and JIT; keep that out of the numbers
        var (warmDoc, warm, _) = await host.OpenAsync(TreeSitterViewTests.CSharp, "Warm.cs", 600, 300);
        await ViewHost.WaitFor(() => warm.Tokens?.ViewportReady is { } w && w.Version == warmDoc.Version, "warm-up coloured", 120_000);

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
        var count = megabytes >= 10 ? 60 : 150;
        for (var i = 0; i < count; i++)
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
        // both engines end up with tokens for every line of a 2,000-line document (it is inside the 3,000-line read-ahead): compare like with like
        foreach (var v in views)
            await ViewHost.WaitFor(() => v.Tokens is { } t && t.Store.HasTokens(0, v.Document.LineCount - 1) && (t.TreeSitter is null || !t.TreeSitter.Syntax.IsParsing), "every line tokenised", 300_000);
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
            await ViewHost.WaitFor(() => view.Tokens is null || view.Tokens.Store.HasTokens(0, Math.Min(10, doc.LineCount - 1)), "coloured", 60_000);
            Type(view, "abcde");
            // let the engine catch up with the edit (a soak, not a latency measurement: never fail on this wait)
            var settle = Stopwatch.StartNew();
            while (view.Tokens is { } t && t.ViewportReady?.Version != doc.Version && settle.ElapsedMilliseconds < 3000)
            {
                ViewHost.Pump();
                await Task.Delay(10);
            }
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
