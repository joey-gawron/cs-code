using System.Reflection;
using System.Text.RegularExpressions;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Profiling.Testing;
using Xunit;

namespace CsCode.MemoryTests.Benchmarks;

/// <summary>
/// MUST 6's Core optimisations stay behaviour-preserving: the glob engine change (backtracking interpreter instead of NonBacktracking, docs/MEMORY.md)
/// matches exactly like the NonBacktracking regex of the same translation over a corpus, cached globs no longer hold the symbolic engine's caches,
/// the registry keeps its best-first order, and the hot paths allocate what the benchmarks report.
/// </summary>
public sealed class CoreHotPathTests
{
    private static readonly MethodInfo ToRegex = typeof(Glob).GetMethod("ToRegex", BindingFlags.Static | BindingFlags.NonPublic)!;

    public static readonly string[] Patterns =
    [
        "*.cs", "*.{ts,tsx}", "**/*.cs", "src/**/*.cs", "/src/*.cs", "**/node_modules/**", "**/.git", "*.[ch]", "*.[!o]", "file?.txt", "**/bin/**/*.dll",
        "a/**/b/**/c.txt", "**", "*", "**/*.memfake", "test/**", "{src,tests}/**/*.json", "*.Designer.cs", "**/obj/*", ".vscode/*.json",
    ];

    public static readonly string[] Paths =
    [
        "/home/u/p/src/A.cs", "/home/u/p/src/sub/B.cs", "src/A.cs", "/src/A.cs", "/x/node_modules/y/z.js", "/x/.git", "/x/.git/config", "main.c", "main.h",
        "main.o", "file1.txt", "file12.txt", "/p/bin/Debug/x.dll", "/p/a/q/b/r/c.txt", "/p/a/b/c.txt", "C:\\\\work\\\\src\\\\A.cs", "x.Designer.cs", "/p/obj/y",
        "/w/.vscode/settings.json", "/w/tests/u/v.json", "/w/src/v.json", "a.memfake", "/deep/" + string.Join('/', Enumerable.Repeat("d", 60)) + "/f.cs",
    ];

    [Fact]
    public void Glob_matches_exactly_like_the_nonbacktracking_translation()
    {
        foreach (var pattern in Patterns)
        {
            var p = pattern.Replace('\\', '/');
            var reference = new Regex((string)ToRegex.Invoke(null, [p])!, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            foreach (var path in Paths)
            {
                var normalized = path.Replace('\\', '/');
                var target = p.Contains('/') ? normalized : normalized[(normalized.LastIndexOf('/') + 1)..];
                Assert.True(reference.IsMatch(target) == Glob.IsMatch(pattern, path), $"{pattern} vs {path}");
            }
        }
    }

    [Fact]
    public void A_pathological_glob_against_a_long_path_is_decided_in_linear_time()
    {
        // lsp-core-3: no timeout any more — the matcher is linear, so the answer is exact (it used to be "no match" after 250 ms of backtracking).
        var path = "/" + string.Join('/', Enumerable.Repeat("aaaa", 400)) + "/zx";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(Glob.IsMatch("**/**/**/**/**/**/**/**/x", path));
        Assert.True(Glob.IsMatch("**/**/**/**/**/**/**/**/x", path[..^2] + "x"));
        Assert.True(sw.ElapsedMilliseconds < 500, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Cached_globs_retain_kilobytes_not_the_symbolic_engines_caches()
    {
        var patterns = Enumerable.Range(0, 40).Select(i => $"**/*.ext{i}").ToArray();
        FullGc.Collect();
        var before = GC.GetTotalMemory(true);
        foreach (var p in patterns) foreach (var path in Paths) Glob.IsMatch(p, path);
        FullGc.Collect();
        var perPattern = (GC.GetTotalMemory(true) - before) / (double)patterns.Length;
        // NonBacktracking kept ~150 KB per pattern once used (6 MB for ~40 in the app); the interpreter keeps a few KB.
        Assert.True(perPattern < 20 * 1024, $"{perPattern / 1024:F1} KB retained per cached glob");
    }

    private sealed class Hover : IHoverProvider
    {
        public ValueTask<CsCode.Core.Languages.Hover?> ProvideHoverAsync(ITextDocument document, TextPosition position, CancellationToken ct) => default;
    }

    [Fact]
    public void GetProviders_orders_best_score_first_then_newest_registration_first()
    {
        var registry = new LanguageFeatureRegistry();
        var doc = new InMemoryTextDocument(new Uri("file:///p/a.cs"), "csharp");
        var all = new Hover();
        var cs1 = new Hover();
        var pattern = new Hover();
        var cs2 = new Hover();
        var other = new Hover();
        using var r1 = registry.Register<IHoverProvider>(DocumentSelector.All, all);                                   // 5
        using var r2 = registry.Register<IHoverProvider>(DocumentSelector.ForLanguage("csharp"), cs1);                 // 10
        using var r3 = registry.Register<IHoverProvider>(new DocumentSelector([new DocumentFilter(Pattern: "**/*.cs")]), pattern); // 5
        using var r4 = registry.Register<IHoverProvider>(DocumentSelector.ForLanguage("csharp"), cs2);                 // 10, newer
        using var r5 = registry.Register<IHoverProvider>(DocumentSelector.ForLanguage("python"), other);                // 0
        Assert.Equal([cs2, cs1, pattern, all], registry.GetProviders<IHoverProvider>(doc));
        Assert.Empty(registry.GetProviders<IHoverProvider>(new Uri("file:///p/a.rs"), "rust").Except([all, pattern]));
    }

    [Fact]
    public void Hot_paths_allocate_what_the_benchmarks_report()
    {
        var doc = new InMemoryTextDocument(new Uri("file:///a.cs"), "csharp", string.Concat(Enumerable.Repeat("int x = 1;\n", 100)));
        var edit = new TextEdit[] { new(TextRange.FromLines(50, 4, 50, 5), "y") };
        doc.ApplyEdits(edit);
        Glob.IsMatch("*.cs", "/home/u/p/A.cs");
        var selector = new DocumentSelector([new DocumentFilter(Language: "csharp"), new DocumentFilter(Pattern: "**/*.csx")]);
        var uri = new Uri("file:///home/u/p/A.cs");
        selector.Score(uri, "csharp");

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) Glob.IsMatch("*.cs", "/home/u/p/A.cs");
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before); // no substring for the file name any more
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) selector.Score(uri, "csharp");
        var perScore = (GC.GetAllocatedBytesForCurrentThread() - before) / 100.0;
        Assert.True(perScore <= 128, $"DocumentSelector.Score allocated {perScore} B (LINQ Max is gone; Uri.LocalPath remains)");
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) doc.ApplyEdits(edit);
        var perEdit = (GC.GetAllocatedBytesForCurrentThread() - before) / 100.0;
        Assert.True(perEdit < 6000, $"one edit of a 100-line document allocated {perEdit} B");
        Assert.Equal(101, doc.LineCount);
        Assert.Equal("int y = 1;", doc.GetLineText(50));
    }
}
