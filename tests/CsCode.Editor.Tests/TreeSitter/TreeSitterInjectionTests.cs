using System.Diagnostics;
using CsCode.Editor.TreeSitter;
using CsCode.Editor.TreeSitter.Grammars;
using CsCode.Editor.TreeSitter.Highlighting;
using CsCode.Editor.TreeSitter.Queries;
using CsCode.TestDoubles;
using Xunit;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterTestSupport;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// Injections whose language comes from the DOCUMENT (a Markdown fence's info string, an rst <c>code-block</c> argument, an HTML
/// <c>&lt;script type&gt;</c>, a Nix <c>/* lang */</c> comment): the name is rewritten by the query's <c>#gsub!</c>/<c>#trim!</c>, then only ever
/// used as the name of an installed grammar — never as a path, never handed to the native loader, never cached unboundedly — and an injected
/// parse is bounded like the highlight it belongs to. Serial: the query time limit is process-wide.
/// </summary>
[Collection(TreeSitterBenchCollection.Name)]
public class TreeSitterInjectionTests
{
    /// <summary>A shared object whose ELF constructor writes <paramref name="marker"/>: what a document must never get CsCode to dlopen.</summary>
    private static string BuildPayload(TempDirectory dir, string relativePath, string marker)
    {
        Assert.SkipUnless(File.Exists("/usr/bin/gcc") || File.Exists("/usr/bin/cc"), "needs gcc to build the payload library");
        var source = dir.WriteFile("payload.c",
            "#include <stdio.h>\n" +
            "__attribute__((constructor)) static void ran(void) { FILE *f = fopen(\"" + marker + "\", \"w\"); if (f) { fputs(\"constructor ran\", f); fclose(f); } }\n");
        var lib = Path.Combine(dir.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(lib)!);
        using var gcc = Process.Start(new ProcessStartInfo(File.Exists("/usr/bin/gcc") ? "/usr/bin/gcc" : "/usr/bin/cc", ["-shared", "-fPIC", "-o", lib, source]) { RedirectStandardError = true })!;
        gcc.WaitForExit();
        Assert.SkipUnless(gcc.ExitCode == 0 && File.Exists(lib), "gcc could not build the payload: " + gcc.StandardError.ReadToEnd());
        return lib;
    }

    private static int HighlightWith(GrammarRegistry registry, string grammar, string text)
    {
        var bundle = registry.Get(grammar, out var error);
        Assert.True(bundle is not null, error);
        using var doc = new SyntaxDocument(registry, bundle!);
        var input = new StringInput(text);
        Assert.Equal(ParseOutcome.Completed, doc.Parse(input, long.MaxValue));
        return doc.Highlight(input, 0, text.Length, new TestStacks(), new PaintBuffer());
    }

    [Fact]
    public void A_document_cannot_name_a_library_path_as_its_injection_language()
    {
        var bundle = RequireBundle();
        RequireGrammar("rst");
        using var dir = new TempDirectory("cscode-ts-inj-");
        var marker = Path.Combine(dir.Path, "constructor-ran");
        var absolute = Path.GetDirectoryName(BuildPayload(dir, Path.Combine("payload", "parser"), marker))!;
        BuildPayload(dir, Path.Combine("payload2", "parser"), marker);
        var userRoot = Path.Combine(dir.Path, "grammars");
        Directory.CreateDirectory(userRoot);

        // (1) no bundle gate (an installation without a bundle, the user's grammar folder, CSCODE_TREESITTER_GRAMMARS): a rooted name
        using (var ungated = new GrammarRegistry(new GrammarLocator([bundle.GrammarsRoot], useNixStore: false), TreeSitterLanguages.RootScopeFor))
        {
            HighlightWith(ungated, "rst", $"Title\n=====\n\n.. code-block:: {absolute}\n\n   x = 1\n");
            HighlightWith(ungated, "markdown", $"# t\n\n```{absolute}\nx = 1\n```\n");
            HighlightWith(ungated, "html", $"<script type=\"{absolute}\">x = 1</script>\n");
        }
        // (2) the verified bundle first, then a user folder: a relative name that climbs out of the user folder
        using (var gated = new GrammarRegistry(new GrammarLocator([bundle.GrammarsRoot, userRoot], useNixStore: false, bundle), TreeSitterLanguages.RootScopeFor))
            HighlightWith(gated, "rst", "Title\n=====\n\n.. code-block:: ../payload2\n\n   x = 1\n");

        Assert.False(File.Exists(marker), "a library named by the document was loaded (its constructor ran)");
    }

    [Theory]
    [InlineData("/proc/self/cwd/payload")]
    [InlineData("../../../payload")]
    [InlineData("rust/../../x")]
    [InlineData("c:\\x")]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("toml.so")]
    public void Names_that_are_not_grammar_names_are_refused_before_any_lookup(string name)
    {
        Assert.False(GrammarLocator.IsValidName(name));
        Assert.Null(InjectionLanguages.Resolve(name));
        var locator = new GrammarLocator([Path.GetTempPath()], useNixStore: false);
        Assert.Null(locator.Find(name));
        Assert.Empty(locator.QueryDirectories(name));
        Assert.False(locator.IsKnown(name));
    }

    [Fact]
    public void Document_invented_language_names_do_not_grow_the_registry()
    {
        RequireGrammar("markdown");
        using var registry = NewRegistry();
        var text = "# t\n\n" + string.Concat(Enumerable.Range(0, 500).Select(i => $"```zzlang{i}\nx\n```\n\n")) + "```rust\nfn main() {}\n```\n";
        HighlightWith(registry, "markdown", text);
        // markdown, markdown_inline, rust (and what they inject): never one entry per invented name
        Assert.True(registry.CachedNames < 20, $"{registry.CachedNames} names cached");
        Assert.False(registry.IsKnown("zzlang1"));
        Assert.True(registry.IsKnown("rust"));
    }

    [Theory]
    [InlineData("/%*%s*([%w%p]+)%s*%*/", "%1", "/* bash */", "bash")]
    [InlineData("/%*%s*([%w%p]+)%s*%*/", "%1", "/*python*/", "python")]
    [InlineData("#%s*([%w%p]+)%s*", "%1", "# bash", "bash")]
    [InlineData("(.+)/(.+)", "%2", "text/javascript", "javascript")]
    [InlineData("(.+)/(.+)", "%2", "module", "module")]
    [InlineData("a", "%0%0", "banana", "baanaanaa")]
    [InlineData("%s+", "%%", "a b  c", "a%b%c")]
    public void Gsub_rewrites_text_like_Lua(string pattern, string replacement, string input, string expected)
    {
        var regex = LuaPattern.CompileWithCaptures(pattern);
        Assert.NotNull(regex);
        Assert.Equal(expected, new TextDirective(regex, replacement).Apply(input));
    }

    [Fact]
    public void HTML_script_type_is_rewritten_to_the_language_by_gsub()
    {
        var bundle = RequireGrammar("html");
        RequireGrammar("javascript");
        const string text = "<script type=\"text/javascript\">const a = 1;</script>\n<script>const b = 2;</script>\n";
        var scopes = Highlight(bundle, text);
        Assert.Contains("source.js", ScopesOf(text, scopes, "const a"));
        Assert.Contains("source.js", ScopesOf(text, scopes, "const b"));
    }

    [Fact]
    public void Nix_strings_take_the_language_their_comment_names()
    {
        var bundle = RequireGrammar("nix");
        RequireGrammar("bash");
        RequireGrammar("python");
        const string text = "{\n  foo = /* bash */ ''\n    echo hi\n  '';\n  bar = /* python */ ''\n    import os\n  '';\n  baz = # bash\n    ''\n    ls -la\n  '';\n}\n";
        var scopes = Highlight(bundle, text);
        var bash = TreeSitterLanguages.RootScopeFor("bash");
        var python = TreeSitterLanguages.RootScopeFor("python");
        Assert.Contains(bash, ScopesOf(text, scopes, "echo"));
        Assert.Contains(python, ScopesOf(text, scopes, "import"));
        Assert.Contains(bash, ScopesOf(text, scopes, "ls -la"));
    }

    /// <summary>
    /// An injected block whose parse would run for minutes (the C# grammar's error recovery on a C# 11 list pattern; see
    /// <see cref="TreeSitterGuardTests"/>) is stopped by the highlight's own time limit instead of running under the hang watchdog, which
    /// would quarantine the healthy C# grammar and abandon the lane. The host is still highlighted, and the block is not tried again on
    /// every following highlight.
    /// </summary>
    [Fact]
    public void An_injected_parse_is_bounded_by_the_highlight_time_limit()
    {
        var bundle = RequireGrammar("rst");
        RequireGrammar("c_sharp");
        var previous = SyntaxDocument.MaxQueryTime;
        try
        {
            SyntaxDocument.MaxQueryTime = TimeSpan.FromMilliseconds(150);
            // unsliced, the C# grammar needs seconds for this (TreeSitterGuardTests: ~4 s; a 371 KB README.rst ran past the 10 s watchdog)
            var csharp = TreeSitterGuardTests.Unparsable + string.Concat(Enumerable.Range(0, 10_000).Select(i => $"public class C{i} {{ int F{i}(int a) {{ return a * {i}; }} }}\n"));
            var block = string.Concat(csharp.Split('\n').Select(l => l.Length == 0 ? "\n" : "   " + l + "\n"));
            var text = "Title\n=====\n\n.. code-block:: csharp\n\n" + block + "\nAfter *the* block.\n";
            using var registry = NewRegistry();
            using var doc = new SyntaxDocument(registry, bundle);
            var input = new StringInput(text);
            Assert.Equal(ParseOutcome.Completed, doc.Parse(input, long.MaxValue));
            var paint = new PaintBuffer();
            var sw = Stopwatch.StartNew();
            doc.Highlight(input, 0, text.Length, new TestStacks(), paint);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"the highlight took {sw.Elapsed.TotalSeconds:0.0} s (load {TreeSitterBenchmarks.Load1():0.0})");
            Assert.Equal(1, doc.InjectionParsesStopped);
            Assert.True(doc.LastQueryTimedOut, "a stopped injection makes the pass partial (the query guard counts it)");

            // the next tree (an edit clears the injection trees) does not pay for the same block again, even with the full default limit
            SyntaxDocument.MaxQueryTime = previous;
            doc.DropTree();
            Assert.Equal(ParseOutcome.Completed, doc.Parse(input, long.MaxValue));
            doc.Highlight(input, 0, text.Length, new TestStacks(), paint);
            Assert.Equal(1, doc.InjectionParsesStopped);
            Assert.False(doc.LastQueryTimedOut);
        }
        finally
        {
            SyntaxDocument.MaxQueryTime = previous;
        }
    }
}
