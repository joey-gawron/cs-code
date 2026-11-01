using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CsCode.Core.Text;
using Xunit;

namespace CsCode.Core.Tests;

/// <summary>
/// lsp-core-3 / T38: <see cref="GlobRegex"/>, the linear-time matcher behind <see cref="Glob"/> and the .gitignore / .editorconfig readers. It must
/// agree with .NET's Regex on the subset the glob translators emit, stay linear on patterns that make a backtracking regex explode, never throw
/// from <see cref="Glob.IsMatch"/>, and keep (and allocate) next to nothing.
/// </summary>
public sealed class GlobRegexTests
{
    // ---- equivalence ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"^(?:.*/)?[^/]*\.cs$", "/home/u/p/src/A.cs", true)]
    [InlineData(@"^(?:.*/)?[^/]*\.cs$", "/home/u/p/src/A.csx", false)]
    [InlineData(@"^(?:.*/)?src/(?:.*/)?[^/]*\.cs$", "/p/src/a/b/c.cs", true)]
    [InlineData(@"^[^/]*\.(?:ts|tsx)$", "a.tsx", true)]
    [InlineData(@"^[^/]*\.(?:ts|tsx)$", "a.tsy", false)]
    [InlineData(@"^[^/]*\.[^o]$", "main.o", false)]
    [InlineData(@"^[^/]*\.[^o]$", "main.c", true)]
    [InlineData(@"^file[^/]\.txt$", "file1.txt", true)]
    [InlineData(@"^a\ b\#c$", "a b#c", true)]
    [InlineData(@"^x\t$", "x\t", true)]
    [InlineData(@"^dir0/sub/.+$", "dir0/sub/x", true)]
    [InlineData(@"^dir0/sub/.+$", "dir0/sub/", false)]
    [InlineData(@"^(?:1|2|3)$", "2", true)]
    [InlineData(@"^[]a]$", "]", true)]
    [InlineData(@"^[a-]$", "-", true)]
    [InlineData(@"^[\\\[]$", "[", true)]
    [InlineData(@"^a*?b$", "aaab", true)]
    [InlineData(@"(?:ab)+", "xxababyy", true)]   // unanchored: a match anywhere
    [InlineData(@"b$", "ab", true)]
    [InlineData(@"^a", "ab", true)]
    [InlineData(@"^$", "", true)]
    [InlineData(@"^$", "a", false)]
    [InlineData(@"", "anything", true)]
    [InlineData(@"^(a|)$", "", true)]
    [InlineData(@"^\$$", "$", true)]
    [InlineData(@"^a\\$", @"a\", true)]
    public void Matches_like_dotnet_regex(string pattern, string text, bool expected)
    {
        Assert.Equal(expected, new Regex(pattern, RegexOptions.CultureInvariant).IsMatch(text)); // the reference agrees with the expectation
        Assert.Equal(expected, GlobRegex.Compile(pattern).IsMatch(text));
    }

    [Fact]
    public void Agrees_with_dotnet_regex_on_thousands_of_random_patterns_and_texts()
    {
        var random = new Random(4242);
        var checkedCases = 0;
        for (var p = 0; p < 1500; p++)
        {
            var pattern = RandomPattern(random, depth: 0);
            if (random.Next(3) == 0) pattern = "^" + pattern;
            if (random.Next(3) == 0) pattern += "$";
            var reference = new Regex(pattern, RegexOptions.CultureInvariant);
            var linear = GlobRegex.Compile(pattern);
            // The same language with 70 extra consuming states ("z?" never meets a 'z'): exercises the list-based simulation that patterns above
            // 63 consuming states use instead of the bit-parallel one.
            var padding = string.Concat(Enumerable.Repeat("(?:z?)", 70));
            var wide = GlobRegex.Compile(pattern.StartsWith('^') ? "^" + padding + pattern[1..] : padding + pattern);
            for (var t = 0; t < 12; t++)
            {
                var text = RandomText(random);
                var expected = reference.IsMatch(text);
                Assert.True(expected == linear.IsMatch(text), $"pattern {pattern} text '{text}': Regex says {expected}");
                Assert.True(expected == wide.IsMatch(text), $"padded pattern {pattern} text '{text}': Regex says {expected}");
                checkedCases++;
            }
        }
        Assert.Equal(1500 * 12, checkedCases);
    }

    private static string RandomPattern(Random r, int depth)
    {
        var sb = new StringBuilder();
        var atoms = r.Next(1, 5);
        for (var i = 0; i < atoms; i++)
        {
            var atom = r.Next(depth < 2 ? 9 : 7) switch
            {
                0 => "a",
                1 => "b",
                2 => "/",
                3 => ".",
                4 => "[^/]",
                5 => r.Next(2) == 0 ? "[ab]" : "[a-c/]",
                6 => @"\.",
                7 => "(?:" + RandomPattern(r, depth + 1) + "|" + RandomPattern(r, depth + 1) + ")",
                _ => "(?:" + RandomPattern(r, depth + 1) + ")",
            };
            sb.Append(atom);
            switch (r.Next(6))
            {
                case 0: sb.Append('*'); break;
                case 1: sb.Append('+'); break;
                case 2: sb.Append('?'); break;
            }
        }
        return sb.ToString();
    }

    private static string RandomText(Random r)
    {
        const string alphabet = "ab/.c";
        var length = r.Next(0, 9);
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = alphabet[r.Next(alphabet.Length)];
        return new string(chars);
    }

    [Fact]
    public void Every_core_glob_translation_agrees_with_the_regex_it_used_to_compile_to()
    {
        string[] patterns = ["*.cs", "*.{ts,tsx}", "**/*.cs", "src/**/*.cs", "/src/*.cs", "**/node_modules/**", "**/.git", "*.[ch]", "*.[!o]", "file?.txt",
            "**/bin/**/*.dll", "a/**/b/**/c.txt", "**", "*", "{src,tests}/**/*.json", "*.Designer.cs", "**/obj/*", ".vscode/*.json", "a b.txt", "x+y(1).md"];
        string[] paths = ["/home/u/p/src/A.cs", "src/A.cs", "/src/A.cs", "/x/node_modules/y/z.js", "/x/.git", "main.c", "main.o", "file1.txt", "/p/a/q/b/r/c.txt",
            "x.Designer.cs", "/w/.vscode/settings.json", "/w/tests/u/v.json", "a b.txt", "/d/x+y(1).md", ""];
        var toRegex = typeof(Glob).GetMethod("ToRegex", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var pattern in patterns)
        {
            var reference = new Regex((string)toRegex.Invoke(null, [pattern])!, RegexOptions.CultureInvariant);
            foreach (var path in paths)
            {
                var target = pattern.Contains('/') ? path : path[(path.LastIndexOf('/') + 1)..];
                Assert.True(reference.IsMatch(target) == Glob.IsMatch(pattern, path), $"{pattern} vs {path}");
            }
        }
    }

    // ---- linear, never throwing ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_pattern_that_explodes_a_backtracking_regex_is_matched_in_linear_time()
    {
        // The reviewer's repro: a server's documentSelector of 40 "**/" against an 8-segment path took 250 ms per call (the old timeout) and failed.
        var pattern = string.Concat(Enumerable.Repeat("**/", 40)) + "x";
        var path = "/home/u/repos/proj/src/a/b/zx";           // ends with x, so the literal-suffix shortcut cannot decide it
        var longPath = "/" + string.Join('/', Enumerable.Repeat("aaaa", 400)) + "/zx";
        Assert.False(Glob.IsMatch(pattern, path));          // warm-up (compiles)
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            Assert.False(Glob.IsMatch(pattern, path));
            Assert.True(Glob.IsMatch(pattern, "/home/u/repos/proj/src/a/b/x"));
        }
        Assert.False(Glob.IsMatch(pattern, longPath));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"201 matches took {sw.ElapsedMilliseconds} ms"); // ~1 ms here; the backtracking regex took 250 ms EACH

        var bomb = GlobRegex.Compile("^" + string.Concat(Enumerable.Repeat("a*", 40)) + "b$");
        sw.Restart();
        Assert.False(bomb.IsMatch(new string('a', 5000) + "c"));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("[z-a]")]
    [InlineData("**/[z-a].ts")]
    [InlineData("[]")]
    [InlineData("[")]
    public void A_malformed_glob_matches_nothing_and_never_throws(string pattern)
    {
        Assert.False(Glob.IsMatch(pattern, "/p/a.ts"));
        Assert.False(Glob.IsMatch(pattern, "a"));
        var selector = new DocumentSelector([new DocumentFilter(Pattern: pattern)]);
        Assert.Equal(0, selector.Score(new Uri("file:///p/a.ts"), "typescript"));
    }

    [Theory]
    [InlineData(@"\d")]
    [InlineData(@"(?=a)")]
    [InlineData(@"a{2}")]
    [InlineData(@"(a)\1")]
    [InlineData(@"a^b")]
    [InlineData(@"*a")]
    [InlineData(@"(a")]
    [InlineData(@"a)")]
    [InlineData(@"[z-a]")]
    [InlineData(@"[a-[b]]")]
    [InlineData(@"a\")]
    [InlineData(@"^a|b$")]
    public void Constructs_outside_the_subset_are_refused_at_compile_time(string pattern)
    {
        Assert.ThrowsAny<ArgumentException>(() => GlobRegex.Compile(pattern));
        Assert.Same(GlobRegex.Never, GlobRegex.CompileOrNever(pattern));
    }

    // ---- integer ranges (.editorconfig {num1..num2}, T38 follow-up) --------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 9)]
    [InlineData(0, 10)]
    [InlineData(1, 3)]
    [InlineData(3, 1)]
    [InlineData(-1, 1)]
    [InlineData(-12, -3)]
    [InlineData(-120, 37)]
    [InlineData(7, 1234)]
    [InlineData(99, 100)]
    [InlineData(199, 2001)]
    [InlineData(10000000, 10000999)]
    [InlineData(-10000999, -10000000)]
    [InlineData(int.MaxValue - 999, int.MaxValue)]
    [InlineData(int.MinValue, int.MinValue + 999)]
    public void An_integer_range_matches_exactly_the_decimal_spelling_of_its_numbers(int from, int to)
    {
        var range = GlobRegex.IntegerRange(from, to);
        var matcher = GlobRegex.Compile("^" + range + "$");
        long lo = Math.Min(from, to), hi = Math.Max(from, to);
        // every number of the range and 200 on each side, plus spellings that are not canonical
        for (var n = lo - 200; n <= hi + 200; n++)
        {
            var inRange = n >= lo && n <= hi;
            var text = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(matcher.IsMatch(text) == inRange, $"{range} on {text}: expected {inRange}");
            if (n >= 0) Assert.False(matcher.IsMatch("0" + text), $"{range} matched a leading zero: 0{text}");
            if (n > 0) Assert.False(matcher.IsMatch("+" + text), $"{range} matched +{text}");
            if (hi - lo > 1000 && n == lo + 200) n = Math.Max(n, hi - 400); // a wide range: its two ends are what can go wrong
        }
        Assert.False(matcher.IsMatch("-0"));
        Assert.False(matcher.IsMatch(""));
        Assert.False(matcher.IsMatch("-"));
        Assert.True(range.Length < 400, $"{range.Length} characters: {range}");
    }

    [Fact]
    public void Random_integer_ranges_agree_with_the_alternation_of_their_numbers()
    {
        var random = new Random(4242);
        for (var round = 0; round < 300; round++)
        {
            var scale = (int)Math.Pow(10, random.Next(1, 6));
            int a = random.Next(-scale, scale), b = random.Next(-scale, scale);
            int lo = Math.Min(a, b), hi = Math.Min(Math.Max(a, b), lo + 999);
            var reference = new Regex("^(?:" + string.Join('|', Enumerable.Range(lo, hi - lo + 1)) + ")$", RegexOptions.CultureInvariant);
            var compact = GlobRegex.Compile("^" + GlobRegex.IntegerRange(lo, hi) + "$");
            for (var n = lo - 30; n <= hi + 30; n++)
            {
                var text = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(reference.IsMatch(text) == compact.IsMatch(text), $"{lo}..{hi} on {text}");
            }
        }
    }

    [Fact]
    public void An_overlong_pattern_is_refused()
    {
        Assert.ThrowsAny<ArgumentException>(() => GlobRegex.Compile(new string('a', GlobRegex.MaxPatternLength + 1)));
        Assert.False(Glob.IsMatch(new string('a', 10_000), new string('a', 10_000)));
    }

    // ---- memory -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Matching_allocates_nothing_even_for_a_long_pattern()
    {
        var shortOne = GlobRegex.Compile(@"^(?:.*/)?[^/]*\.(?:ts|tsx)$");
        var longOne = GlobRegex.Compile("^" + string.Concat(Enumerable.Repeat("(?:.*/)?", 200)) + "x$"); // > 256 states: pooled buffers
        shortOne.IsMatch("/a/b/c.ts");
        longOne.IsMatch("/a/b/x");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            shortOne.IsMatch("/a/b/c.ts");
            longOne.IsMatch("/a/b/x");
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

/// <summary>
/// The heap measurement of <see cref="GlobRegexTests"/>: the live heap is the whole process's, so a test of another class running at the
/// same time counted its objects as the matchers' ("16194 B per compiled pattern" in a full run on a busy Mac, 912 B alone; a concurrent
/// allocating test reproduces it at 245-271 KB). It runs alone, after the parallel tests.
/// </summary>
[Collection(GlobRegexMemoryTests.Name)]
public sealed class GlobRegexMemoryTests
{
    public const string Name = "GlobRegexMemory";

    [Fact]
    public void A_compiled_matcher_keeps_a_few_hundred_bytes_after_matching()
    {
        var patterns = Enumerable.Range(0, 40).Select(i => $@"^(?:.*/)?[^/]*\.ext{i}$").ToArray();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var before = LiveHeap();
        var compiled = patterns.Select(GlobRegex.Compile).ToArray();
        foreach (var m in compiled) foreach (var p in new[] { "/a/b.ext1", "/x/y/z.cs", "q.ext39" }) m.IsMatch(p);
        var perPattern = (LiveHeap() - before) / (double)patterns.Length;
        GC.KeepAlive(compiled);
        Assert.True(perPattern < 2048, $"{perPattern:F0} B per compiled pattern"); // NonBacktracking kept ~150 KB
    }

    /// <summary>
    /// The live heap after a full collection: <see cref="GC.GetTotalMemory(bool)"/>, except on macOS, where it is inflated (4.8 KB per pattern
    /// for 0.9 KB of live objects on Apple arm64) and the collection's heap size minus fragmentation is used.
    /// </summary>
    private static long LiveHeap()
    {
        var total = GC.GetTotalMemory(forceFullCollection: true);
        if (!OperatingSystem.IsMacOS()) return total;
        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking); // that collection, not a later one another thread caused
        return info.HeapSizeBytes - info.FragmentedBytes;
    }
}

[CollectionDefinition(GlobRegexMemoryTests.Name, DisableParallelization = true)]
public sealed class GlobRegexMemoryCollection;
