using CsCode.Core.Languages;
using CsCode.Editor.Documents;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.TextMate;
using CsCode.Editor.Threading;
using CsCode.SpellCheck.Engine;
using CsCode.SpellCheck.Settings;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// The false-positive check of docs/SPELLCHECK.md: every C# file under this repository's <c>src/</c>, tokenised by the real C# grammar, its
/// comments spell checked with the bundled en_US and the software terms (strings and code not counted). The report — totals and every flagged
/// word with its count — goes to <c>.scratch/spellcheck/src-comments.txt</c>; the test fails when the rate climbs well past the measured one
/// (the software-terms list must keep code words out of the report without hiding typos: see the typo guard in CsCode.SpellCheck.Tests).
/// </summary>
public sealed class SpellCheckSourceTreeTests(ITestOutputHelper output) : IDisposable
{
    private readonly ThreadAffinityUiThread _ui = new();
    private readonly EditorWorker _worker = new("test-worker");
    private TokenizationManager? _manager;

    public void Dispose()
    {
        _manager?.Dispose();
        _worker.Dispose();
    }

    /// <summary>Only the comment runs (the measurement is of comments).</summary>
    private sealed class CommentsOnly(SyntaxClassification inner) : SyntaxClassification
    {
        public override SyntaxClassificationState State => inner.State;
        public override int LineCount => inner.LineCount;

        public override bool TryGetRuns(int line, List<SyntaxTextRun> runs)
        {
            if (!inner.TryGetRuns(line, runs)) return false;
            for (var i = 0; i < runs.Count; i++)
                if (runs[i].Kind != SyntaxTextKind.Comment) runs[i] = runs[i] with { Kind = SyntaxTextKind.Ignored };
            return true;
        }
    }

    [Fact]
    public void The_repositorys_own_comments_have_few_unknown_words()
    {
        var host = TestHost.Create(_ui);
        _manager = new TokenizationManager(host.Services, _worker);
        var classifier = new SyntaxClassifier(_manager, host.Documents);
        var root = RepoRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var settings = SpellSettings.Default with { MaxNumberOfProblems = int.MaxValue, MaxDuplicateProblems = int.MaxValue, CheckLimitKb = 1 << 20 };
        var captured = new List<(string File, string Text, SyntaxClassification Comments)>();
        long commentLines = 0, lines = 0;
        var runs = new List<SyntaxTextRun>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var doc = new EditorDocument(new Uri(file), "csharp", EditorDocument.CreateBuffer(text, _ui), _ui);
            using var registration = host.Documents.Register(doc);
            var session = _manager.CreateSession(doc);
            session.AttachView();
            TokeniseAll(session, doc.LineCount);
            var classification = classifier.Capture(doc.Uri);
            lines += doc.LineCount;
            for (var l = 0; l < classification.LineCount; l++)
                if (classification.TryGetRuns(l, runs) && runs.Any(r => r.Kind == SyntaxTextKind.Comment)) commentLines++;
            captured.Add((file, text, new CommentsOnly(classification)));
            session.DetachView();
            session.Dispose();
            doc.Dispose();
        }
        var tokenising = sw.Elapsed.TotalSeconds;

        string Measure(SpellDictionarySet dictionaries, string label, out double rate, out long words)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var where = new Dictionary<string, string>(StringComparer.Ordinal);
            long flagged = 0;
            words = 0;
            var checking = System.Diagnostics.Stopwatch.StartNew();
            foreach (var (file, text, comments) in captured)
            {
                var run = new CheckRun(text, comments, false, settings, dictionaries);
                while (!run.Step(TimeSpan.MaxValue, CancellationToken.None)) { }
                words += run.WordsChecked;
                foreach (var issue in run.Issues)
                {
                    flagged++;
                    counts[issue.Word] = counts.GetValueOrDefault(issue.Word) + 1;
                    where.TryAdd(issue.Word, $"{Path.GetRelativePath(root, file)}:{issue.Line + 1}");
                }
            }
            rate = flagged * 1000.0 / Math.Max(1, words);
            var summary = $"[{label}] {files.Length} files, {lines} lines, {commentLines} comment lines, {words} comment words checked: {flagged} flagged " +
                $"({counts.Count} distinct), {rate:F1} per 1000 words; tokenising {tokenising:F1} s, checking {checking.Elapsed.TotalSeconds:F1} s";
            output.WriteLine(summary);
            var report = Path.Combine(root, ".scratch", "spellcheck", $"src-comments-{label}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllLines(report, [summary, "", .. counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Value,6}  {kv.Key}  ({where[kv.Key]})")]);
            return summary;
        }

        var enUs = Measure(Dictionaries.EnUs, "en_US", out var usRate, out var usWords);
        Assert.True(usWords > 100_000, $"the tree has comments: {enUs}");
        // measured on this tree (docs/SPELLCHECK.md §8); a doubling means code words reach the report again, or the splitter broke
        Assert.True(usRate < 15, enUs);

        // the tree is written in British English: with LibreOffice's en_GB beside en_US (tools/spellcheck/fetch-dictionaries.sh), reported only
        var british = Path.Combine(root, ".scratch", "dictionaries", "en_GB.dic");
        if (File.Exists(british))
        {
            var source = new DictionarySource("en_GB", "en_gb", british, Path.ChangeExtension(british, ".aff"), new FileInfo(british).Length, DateTime.MinValue);
            var us = Dictionaries.EnUs;
            var both = new SpellDictionarySet([.. us.Sources, source], [DictionaryCatalog.Load(us.Sources[0]), DictionaryCatalog.Load(source)],
                DictionaryCatalog.LoadSoftwareTerms(), []);
            Measure(both, "en_US+en_GB", out _, out _);
        }
        classifier.Dispose();
        host.Dispose();
    }

    /// <summary>Moves the viewport down the file until every line has tokens on the valid chain (the session reads ahead only 3000 lines).</summary>
    private void TokeniseAll(TokenizationSession session, int lineCount)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (session.Store.ValidLines < lineCount && sw.ElapsedMilliseconds < 60_000)
        {
            var at = Math.Min(lineCount - 1, session.Store.ValidLines + 1500);
            session.SetViewport(Math.Max(0, at - 60), at);
            _ui.Pump();
            Thread.Sleep(1);
        }
        Assert.Equal(lineCount, session.Store.ValidLines);
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CsCode.slnx"))) return d.FullName;
        throw new InvalidOperationException("CsCode.slnx not found above " + AppContext.BaseDirectory);
    }
}
