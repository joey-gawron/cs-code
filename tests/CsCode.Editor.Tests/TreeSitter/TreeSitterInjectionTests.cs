using System.Diagnostics;
using Avalonia.Headless.XUnit;
using CsCode.Editor.Tests.Support;
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
            // Far below the block's seconds, and enough for the rst host's own query over the 600 KB document, which spends the same clock.
            SyntaxDocument.MaxQueryTime = TimeSpan.FromMilliseconds(500);
            // unsliced, the C# grammar needs seconds for this (TreeSitterGuardTests: ~4 s; a 371 KB README.rst ran past the 10 s watchdog)
            var csharp = TreeSitterGuardTests.Unparsable + string.Concat(Enumerable.Range(0, 10_000).Select(i => $"public class C{i} {{ int F{i}(int a) {{ return a * {i}; }} }}\n"));
            var block = string.Concat(csharp.Split('\n').Select(l => l.Length == 0 ? "\n" : "   " + l + "\n"));
            var text = "Title\n=====\n\n.. code-block:: csharp\n\n" + block + "\nAfter *the* block.\n";
            using var registry = NewRegistry();
            Assert.NotNull(registry.Get("c_sharp", out _)); // loaded before the highlight (a first load inside it would spend its clock)
            using var doc = new SyntaxDocument(registry, bundle);
            var input = new StringInput(text);
            Assert.Equal(ParseOutcome.Completed, doc.Parse(input, long.MaxValue));
            var paint = new PaintBuffer();
            // A pass whose host query already spent the clock defers the block instead of starting it (by design: the next pass has a clock of
            // its own). On a slow moment that is the first pass or two, so up to three passes; the one that starts the block must stop it.
            for (var pass = 1; doc.InjectionParsesStopped == 0; pass++)
            {
                var deferred = doc.InjectionParsesDeferred;
                var sw = Stopwatch.StartNew();
                doc.Highlight(input, 0, text.Length, new TestStacks(), paint);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"pass {pass} took {sw.Elapsed.TotalSeconds:0.0} s (load {TreeSitterBenchmarks.Load1():0.0})");
                if (doc.InjectionParsesStopped == 0)
                {
                    Assert.True(doc.InjectionParsesDeferred > deferred, $"pass {pass}: the block was neither stopped nor deferred (it parsed within the limit?)");
                    Assert.True(pass < 3, $"the block was deferred {pass} times (load {TreeSitterBenchmarks.Load1():0.0})");
                }
            }
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

    /// <summary>
    /// editor-3 (review): the installed-grammar listing behind the name check was taken once per registry, so a grammar the user copied into
    /// {UserDataDir}/tree-sitter/grammars after the first tree-sitter document was refused for its own language until restart (before the
    /// check, the first lookup of the name probed for it). The listing is taken again when a search root changed: at once for a document's
    /// own language, within a second for names from document text.
    /// </summary>
    [Fact]
    public void A_grammar_copied_into_the_user_folder_while_running_is_found()
    {
        var bundle = RequireBundle();
        RequireGrammar("toml");
        using var dir = new TempDirectory("cscode-ts-user-");
        var user = Path.Combine(dir.Path, "grammars");
        Directory.CreateDirectory(user);
        using var registry = new GrammarRegistry(new GrammarLocator([user], useNixStore: false), TreeSitterLanguages.RootScopeFor);
        Assert.Null(registry.Get("toml", out var error)); // nothing there yet (the listing is taken now)
        Assert.Contains("no compiled tree-sitter grammar 'toml'", error, StringComparison.Ordinal);
        Assert.False(registry.IsKnown("toml"));

        // the user installs a grammar: its library and its queries
        File.Copy(Path.Combine(bundle.GrammarsRoot, "toml.so"), Path.Combine(user, "toml.so"));
        var queries = Path.Combine(user, "queries", "toml");
        Directory.CreateDirectory(queries);
        foreach (var scm in Directory.GetFiles(Path.Combine(bundle.GrammarsRoot, "queries", "toml"), "*.scm")) File.Copy(scm, Path.Combine(queries, Path.GetFileName(scm)));

        Assert.NotNull(registry.Get("toml", out error)); // a document of the language: at once
        Assert.Null(error);

        // names from document text (injections) see a new grammar too, within a second
        var other = Path.Combine(dir.Path, "other");
        Directory.CreateDirectory(other);
        var locator = new GrammarLocator([other], useNixStore: false);
        Assert.False(locator.IsKnown("toml"));
        File.Copy(Path.Combine(bundle.GrammarsRoot, "toml.so"), Path.Combine(other, "toml.so"));
        var clock = Stopwatch.StartNew();
        while (!locator.IsKnown("toml") && clock.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(50);
        Assert.True(locator.IsKnown("toml"), "an injection's name check never saw the grammar copied in");
        Assert.False(locator.IsKnown("sql")); // and a miss after that is still only a listing lookup
    }

    /// <summary>
    /// The regions of one highlight share its clock. Once a pathological block spent it, every later region stopped at its first progress
    /// check and was remembered as slow under ITS language and size, so ordinary Python and C# blocks of that size stayed uncoloured for the
    /// document's life. A region is remembered only when its own parse had the time; the others are parsed by the next highlight.
    /// </summary>
    [Fact]
    public void A_region_that_only_ran_out_of_another_region_s_time_is_not_remembered_as_slow()
    {
        var bundle = RequireGrammar("rst");
        RequireGrammar("c_sharp");
        RequireGrammar("python");
        var previous = SyntaxDocument.MaxQueryTime;
        try
        {
            // Far below the block's seconds, and enough for the rst host's own query over the 600 KB document, which spends the same clock.
            SyntaxDocument.MaxQueryTime = TimeSpan.FromMilliseconds(500);
            static string Indent(string code) => string.Concat(code.Split('\n').Select(l => l.Length == 0 ? "\n" : "   " + l + "\n"));
            var big = TreeSitterGuardTests.Unparsable + string.Concat(Enumerable.Range(0, 10_000).Select(i => $"public class C{i} {{ int F{i}(int a) {{ return a * {i}; }} }}\n"));
            var python = string.Concat(Enumerable.Range(0, 15).Select(i => $"def pyfunc{i}(a):\n    return a + {i}\n"));
            const string small = "class Small {\n    int F(int a) {\n        return a;\n    }\n}\n// end\n";
            var text = "Title\n=====\n\n.. code-block:: csharp\n\n" + Indent(big)
                + "\n.. code-block:: python\n\n" + Indent(python)
                + "\n.. code-block:: csharp\n\n" + Indent(small) + "\nAfter *the* blocks.\n";
            using var registry = NewRegistry();
            Assert.NotNull(registry.Get("c_sharp", out _)); // loaded before the highlight (a first load inside it would spend its clock)
            Assert.NotNull(registry.Get("python", out _));
            using var doc = new SyntaxDocument(registry, bundle);
            var input = new StringInput(text);
            Assert.Equal(ParseOutcome.Completed, doc.Parse(input, long.MaxValue));
            var stacks = new TestStacks();
            var paint = new PaintBuffer();
            string[] ScopesAt(string needle) => stacks.Scopes(paint.StackAt(text.IndexOf(needle, StringComparison.Ordinal)));
            var pythonRoot = TreeSitterLanguages.RootScopeFor("python")!;
            var csharpRoot = TreeSitterLanguages.RootScopeFor("c_sharp")!;

            doc.Highlight(input, 0, text.Length, stacks, paint);
            Assert.Equal(1, doc.InjectionParsesStopped); // the pathological block
            Assert.True(doc.InjectionParsesDeferred >= 2, $"{doc.InjectionParsesDeferred} deferred"); // the blocks after it: not started, the clock was spent
            Assert.True(doc.LastHighlightDeferred);
            Assert.True(doc.LastQueryTimedOut);
            Assert.Null(doc.SlowInjectionSize("python"));
            Assert.True(doc.SlowInjectionSize("c_sharp") > 100_000, $"c_sharp remembered at {doc.SlowInjectionSize("c_sharp")} chars"); // the big block's size, not the 6-line one's

            // the next highlight (a scroll, the next slice) colours them; the pathological block is not tried again
            doc.Highlight(input, 0, text.Length, stacks, paint);
            Assert.Equal(1, doc.InjectionParsesStopped);
            Assert.Contains(pythonRoot, ScopesAt("pyfunc3"));
            Assert.Contains(csharpRoot, ScopesAt("class Small"));
            Assert.DoesNotContain(csharpRoot, ScopesAt("public class C5 "));
            Assert.False(doc.LastQueryTimedOut);

            // and after an edit (new tree, injection trees dropped) Python and small C# blocks are still parsed
            doc.DropTree();
            Assert.Equal(ParseOutcome.Completed, doc.Parse(input, long.MaxValue));
            doc.Highlight(input, 0, text.Length, stacks, paint);
            Assert.Contains(pythonRoot, ScopesAt("pyfunc3"));
            Assert.Contains(csharpRoot, ScopesAt("class Small"));
            Assert.Equal(1, doc.InjectionParsesStopped);
        }
        finally
        {
            SyntaxDocument.MaxQueryTime = previous;
        }
    }

    /// <summary>
    /// The same in a view: the highlighter highlights the viewport again when its pass left injected blocks for the next one, so the blocks
    /// after a slow one are coloured without a scroll or an edit (the pass after the slow one does not try it again).
    /// </summary>
    [AvaloniaFact]
    public async Task Blocks_left_by_a_slow_block_are_coloured_by_the_next_pass_without_any_input()
    {
        RequireGrammar("rst");
        RequireGrammar("c_sharp");
        RequireGrammar("python");
        var previous = SyntaxDocument.MaxQueryTime;
        SyntaxDocument.MaxQueryTime = TimeSpan.FromMilliseconds(300);
        // a block whose parse runs out of time: it starts after its own limit has passed, so it stops at its first progress check
        SyntaxDocument.InjectionParseHookForTests = (language, chars) =>
        {
            if (language == "c_sharp" && chars > 1_000) Thread.Sleep(400);
        };
        try
        {
            using var host = ViewHost.Create();
            host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TreeSitter);
            static string Indent(string code) => string.Concat(code.Split('\n').Select(l => l.Length == 0 ? "\n" : "   " + l + "\n"));
            var slow = string.Concat(Enumerable.Range(0, 30).Select(i => $"class C{i} {{ int F{i}(int a) {{ return a * {i}; }} }}\n"));
            var text = "Title\n=====\n\n.. code-block:: csharp\n\n" + Indent(slow)
                + "\n.. code-block:: python\n\n" + Indent("def pyfunc(a):\n    return a + 1\n")
                + "\n.. code-block:: csharp\n\n" + Indent("class Small { }\n") + "\nAfter.\n";
            var lines = text.Split('\n');
            var pyLine = Array.FindIndex(lines, l => l.Contains("def pyfunc", StringComparison.Ordinal));
            var smallLine = Array.FindIndex(lines, l => l.Contains("class Small", StringComparison.Ordinal));
            var (_, view, _) = await host.OpenAsync(text, "notes.rst", 900, 1600);
            Assert.True(view.Tokens!.Viewport.Last >= smallLine, $"viewport {view.Tokens.Viewport}");
            var python = TreeSitterLanguages.RootScopeFor("python")!;
            var csharp = TreeSitterLanguages.RootScopeFor("c_sharp")!;
            await ViewHost.WaitFor(() => TreeSitterViewTests.ScopesAt(view, pyLine, 4).Contains(python) && TreeSitterViewTests.ScopesAt(view, smallLine, 4).Contains(csharp),
                "the Python block and the small C# block coloured", 20_000);
            Assert.DoesNotContain(csharp, TreeSitterViewTests.ScopesAt(view, Array.FindIndex(lines, l => l.Contains("class C5 ", StringComparison.Ordinal)), 4));
            Assert.False(view.Tokens.TreeSitterGaveUp);
        }
        finally
        {
            SyntaxDocument.InjectionParseHookForTests = null;
            SyntaxDocument.MaxQueryTime = previous;
        }
    }
}
