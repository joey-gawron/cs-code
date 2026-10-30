using CsCode.Editor.Tests.Support;
using CsCode.Editor.TextMate;
using TextMateSharp.Grammars;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// T37: TextMateSharp kept every grammar's raw JSON tree after compiling it (15–20 MB of the heap with 20 documents, docs/MEMORY.md §7). The
/// trees are dropped once a grammar is compiled; the compiled rules keep tokenising, embedded languages included, and a grammar compiled
/// after the drop reads what it needs again. Serial: it measures the process's heap.
/// </summary>
[Collection(MemoryMeasurementCollection.Name)]
public sealed class RawGrammarReleaseTests(ITestOutputHelper output) : IDisposable
{
    private readonly TestHost _host = TestHost.Create(new ThreadAffinityUiThread());

    public void Dispose()
    {
        GrammarService.ReleaseRawGrammars = true;
        _host.Dispose();
    }

    private static string[][] Tokenise(IGrammar grammar, params string[] lines)
    {
        IStateStack? state = null;
        var result = new List<string[]>();
        foreach (var line in lines)
        {
            var r = grammar.TokenizeLine(line, state, TimeSpan.FromSeconds(5));
            state = r.RuleStack;
            foreach (var t in r.Tokens) result.Add([line[Math.Min(t.StartIndex, line.Length)..], .. t.Scopes]);
        }
        return result.ToArray();
    }

    private static string[] ScopesAt(string[][] tokens, string startsWith) =>
        tokens.First(t => t[0].StartsWith(startsWith, StringComparison.Ordinal))[1..];

    [Fact]
    public void Compiled_grammars_hold_no_raw_trees_and_still_colour_embedded_languages()
    {
        Assert.True(RawGrammarRelease.Supported, "TextMateSharp's private layout changed: the raw grammar trees are no longer released");
        var service = new GrammarService(_host.Services);

        var markdown = service.GrammarForScope("text.html.markdown")!;
        Assert.Equal((0, false, 0), RawGrammarRelease.Held(service.RegistryForTests!, markdown));
        var md = Tokenise(markdown, "# Title", "", "```js", "let a = 1;", "```", "", "```csharp", "var b = 2;", "```");
        Assert.Contains(ScopesAt(md, "let"), s => s.StartsWith("storage.type", StringComparison.Ordinal));
        Assert.Contains(ScopesAt(md, "var"), s => s.StartsWith("storage.type", StringComparison.Ordinal) || s.StartsWith("keyword", StringComparison.Ordinal));

        // compiled after the registry dropped its trees: HTML reads its include closure (JavaScript, CSS) again
        var html = service.GrammarForScope("text.html.basic")!;
        Assert.Equal((0, false, 0), RawGrammarRelease.Held(service.RegistryForTests!, html));
        var page = Tokenise(html, "<script>let c = 3;</script><style>p { color: red; }</style>");
        Assert.Contains(ScopesAt(page, "let"), s => s.StartsWith("storage.type", StringComparison.Ordinal));
        Assert.Contains(ScopesAt(page, "color"), s => s.StartsWith("support.type.property-name", StringComparison.Ordinal));

        var csharp = service.GrammarForScope("source.cs")!;
        Assert.Contains(ScopesAt(Tokenise(csharp, "class A { int B; }"), "class"), s => s.StartsWith("storage.type", StringComparison.Ordinal) || s.StartsWith("keyword", StringComparison.Ordinal));
        Assert.Equal("source.cs", csharp.GetScopeName());
        Assert.False(string.IsNullOrEmpty(csharp.GetName()));
    }

    [Fact]
    public void The_grammars_of_twenty_documents_retain_far_less_heap()
    {
        string[] scopes = ["source.cs", "text.html.markdown", "source.json", "source.shell"];
        long Retained(bool release)
        {
            GrammarService.ReleaseRawGrammars = release;
            var before = LiveHeap.Bytes();
            var service = new GrammarService(_host.Services);
            foreach (var scope in scopes) Tokenise(service.GrammarForScope(scope)!, "x = 1 // y");
            var after = LiveHeap.Bytes();
            GC.KeepAlive(service);
            return after - before;
        }
        Retained(release: true); // static caches (built-in catalog, regex engine) warmed for both
        var kept = Retained(release: false);
        var released = Retained(release: true);
        output.WriteLine($"C#, Markdown, JSON, shell grammars: {kept / 1048576.0:F1} MB retained with the raw trees, {released / 1048576.0:F1} MB without");
        Assert.True(released < kept / 2, $"{released / 1048576.0:F1} MB retained without the raw trees vs {kept / 1048576.0:F1} MB with them");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MemoryMeasurementCollection
{
    /// <summary>Tests that measure the process's managed heap: run alone (other tests' allocations would land in their numbers).</summary>
    public const string Name = "Heap measurements (alone)";
}
