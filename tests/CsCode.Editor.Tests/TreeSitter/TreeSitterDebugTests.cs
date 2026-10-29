using System.Diagnostics;
using CsCode.Editor.TreeSitter.Highlighting;
using Xunit;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterTestSupport;

namespace CsCode.Editor.Tests.TreeSitter;

public class TreeSitterDebugTests
{
    [Fact]
    public void Dump()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TS_DEBUG") == "1", "debug");
        var bundle = RequireGrammar("c_sharp");
        var corpus = string.Concat(Directory.EnumerateFiles(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src")), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(File.ReadAllText));
        var text = corpus[..(1024 * 1024)];
        text = text[..(text.LastIndexOf('\n') + 1)];
        var lines = new List<string>();
        using var registry = NewRegistry();
        var input = new StringInput(text);
        foreach (var where in new[] { "middle", "identifier" })
        {
            using var doc = new SyntaxDocument(registry, bundle);
            var sw = Stopwatch.StartNew();
            doc.Parse(input, long.MaxValue);
            lines.Add($"full parse {text.Length} chars: {sw.Elapsed.TotalMilliseconds:0} ms, error={doc.RootHasError}");
            var at = text.Length / 2;
            if (where == "identifier") while (!char.IsLetter(text[at]) || !char.IsLetter(text[at - 1])) at++;
            var line = input.LineOfOffset(at);
            var col = at - input.LineStart(line);
            lines.Add($"{where}: edit at {at} line {line} col {col}: '{text.Substring(at - 20, 40).Replace("\n", "\\n")}'");
            doc.ApplyEdit(new TextEditInfo(at, at, at + 1, line, col, line, col, line, col + 1));
            var edited = new StringInput(text.Insert(at, "x"));
            sw.Restart();
            doc.Parse(edited, long.MaxValue);
            lines.Add($"  incremental: {sw.Elapsed.TotalMilliseconds:0.0} ms, slices {doc.LastParseSlices}, changed {string.Join(",", doc.ChangedRanges.Select(r => $"{r.Start}-{r.End}"))}");
            var paint = new PaintBuffer();
            var stacks = new TestStacks();
            sw.Restart();
            doc.Highlight(edited, edited.LineStart(line), edited.LineStart(line + 60), stacks, paint);
            lines.Add($"  highlight 60 lines at {line}: {sw.Elapsed.TotalMilliseconds:0.0} ms");
            sw.Restart();
            doc.Highlight(edited, 0, edited.LineStart(60), stacks, paint);
            lines.Add($"  highlight first 60 lines: {sw.Elapsed.TotalMilliseconds:0.0} ms");
        }
        File.WriteAllLines("/tmp/claude-1000/-home-joeyg-repos-cs-code/1772ea45-46f9-4f13-8d70-0f273a0d41c9/scratchpad/ts334-5/incr.txt", lines);
    }
}
