using CsCode.Core.Contributions;
using CsCode.Editor.Languages;
using CsCode.Editor.Snippets;
using CsCode.Editor.Tests.Support;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// CsCode's own built-in snippets (Snippets/Builtin/&lt;language&gt;.json): Python (stage 1) and Zig (stage 6); the user's snippets win. The Zig set is
/// checked against the real zig too (skips without it): every snippet's default expansion parses, and a project made of them builds and tests.
/// </summary>
public class BundledSnippetTests
{
    [Fact]
    public void Every_bundled_snippet_file_is_registered_and_every_body_parses()
    {
        var own = BuiltinLanguages.Catalog.Snippets.Where(s => s.ResourceName.StartsWith(BuiltinLanguages.OwnSnippetPrefix, StringComparison.Ordinal)).ToList();
        Assert.Contains(own, s => s.LanguageId == "python");
        var languages = BuiltinLanguages.Catalog.Languages.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var file in own)
        {
            Assert.Contains(file.LanguageId, languages); // the file is named after a language CsCode detects
            var defs = SnippetFileParser.Parse(BuiltinLanguages.ReadText(file.ResourceName)!);
            Assert.NotEmpty(defs);
            foreach (var d in defs)
            {
                Assert.NotEmpty(d.Prefixes);
                Assert.False(string.IsNullOrEmpty(d.Description), $"{file.LanguageId}/{d.Name} has no description");
                // the parser keeps what is not snippet syntax as text: a "${" left in a text node is a broken placeholder
                var parsed = SnippetParser.Parse(d.Body);
                Assert.DoesNotContain(Texts(parsed.Nodes), t => t.Contains("${", StringComparison.Ordinal));
                Assert.Contains(Flatten(parsed.Nodes), n => n is TabstopNode or PlaceholderNode); // every snippet has somewhere to type
            }
            Assert.Equal(defs.Count, defs.SelectMany(d => d.Prefixes).Distinct(StringComparer.Ordinal).Count()); // one snippet per prefix
        }
    }

    [Fact]
    public void C_gets_the_bundled_set_and_the_header_guard_is_named_after_the_file()
    {
        using var host = TestHost.Create(new ThreadAffinityUiThread());
        using var svc = new SnippetService(host.Services);
        var c = svc.GetSnippets("c");
        Assert.True(c.Count >= 25, $"only {c.Count} C snippets");
        Assert.Contains(c, s => s.Prefixes.Contains("main") && s.Body.Contains("int main(int argc, char *argv[])", StringComparison.Ordinal));
        var guard = Assert.Single(c, s => s.Prefixes.Contains("guard"));
        var text = SnippetResolver.Resolve(guard.Body, new SnippetVariableContext { FilePath = "/src/include/mathlib.h" }).Text;
        Assert.Equal("#ifndef MATHLIB_H\n#define MATHLIB_H\n\n\n\n#endif /* MATHLIB_H */", text);
        var printf = SnippetResolver.Resolve(Assert.Single(c, s => s.Prefixes.Contains("printf")).Body).Text;
        Assert.Equal("printf(\"%d\\n\", value);", printf); // the \n stays an escape in the C source
    }

    private static IEnumerable<SnippetNode> Flatten(IEnumerable<SnippetNode> nodes) =>
        nodes.SelectMany(n => n is PlaceholderNode p ? [n, .. Flatten(p.Children)] : new[] { n });

    private static IEnumerable<string> Texts(IEnumerable<SnippetNode> nodes) => Flatten(nodes).OfType<TextNode>().Select(t => t.Text);

    /// <summary>A body as inserted with every placeholder left at its default (tab stops and mirrors empty).</summary>
    private static string Expand(IEnumerable<SnippetNode> nodes) => string.Concat(nodes.Select(n => n switch
    {
        TextNode t => t.Text,
        PlaceholderNode p => Expand(p.Children),
        _ => "",
    }));

    [Fact]
    public void Cpp_gets_the_bundled_set_beside_vs_codes_region_snippets()
    {
        using var host = TestHost.Create(new ThreadAffinityUiThread());
        using var svc = new SnippetService(host.Services);
        var cpp = svc.GetSnippets("cpp");
        Assert.True(cpp.Count >= 40, $"only {cpp.Count} C++ snippets");
        string Inserted(string prefix) => Expand(SnippetParser.Parse(Assert.Single(cpp, s => s.Prefixes.Contains(prefix)).Body).Nodes);
        Assert.Equal("for (const auto& item : items) {\n\t\n}", Inserted("foreach"));
        Assert.Equal("TEST(Suite, Name) {\n\t\n}", Inserted("gtest"));
        Assert.Equal("TEST_CASE(\"name\", \"[tag]\") {\n\t\n}", Inserted("testcaset"));
        Assert.Equal("std::println(\"{}\", value);", Inserted("println")); // the escaped brace stays in the placeholder
        Assert.Equal("std::cout << value << '\\n';", Inserted("cout"));
        Assert.Contains("concept Name = requires (const & value) { value; };", Inserted("concept"), StringComparison.Ordinal);
        Assert.Contains(cpp, s => s.Prefixes.Contains("#region")); // VS Code's own C++ snippets stay
    }

    [Fact]
    public void Python_gets_the_bundled_set_and_a_user_snippet_replaces_the_built_in_one_with_its_prefix()
    {
        using var host = TestHost.Create(new ThreadAffinityUiThread());
        using var svc = new SnippetService(host.Services);
        var python = svc.GetSnippets("python");
        Assert.Contains(python, s => s.Prefixes.Contains("main") && s.Body.Contains("if __name__ == \"__main__\":", StringComparison.Ordinal));
        Assert.Contains(python, s => s.Prefixes.Contains("param") && s.Body.Contains("@pytest.mark.parametrize", StringComparison.Ordinal));
        Assert.True(python.Count >= 40, $"only {python.Count} Python snippets");

        var mine = host.Dir.WriteFile("snippets/python.json", """{ "My main": { "prefix": "main", "body": "run()$0" } }""");
        host.Services.Contributions.Contribute(ContributionSources.User, host.Dir.Combine("snippets"), new[] { new SnippetContribution("python", mine) });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!svc.GetSnippets("python").Any(s => s.Name == "My main") && sw.ElapsedMilliseconds < 5000) Thread.Sleep(10);
        var main = Assert.Single(svc.GetSnippets("python"), s => s.Prefixes.Contains("main"));
        Assert.Equal("My main", main.Name);
        Assert.Contains(svc.GetSnippets("python"), s => s.Prefixes.Contains("ifmain")); // the rest of the built-in set stays
    }

    [Fact]
    public void Zig_gets_the_bundled_set()
    {
        using var host = TestHost.Create(new ThreadAffinityUiThread());
        using var svc = new SnippetService(host.Services);
        var zig = svc.GetSnippets("zig");
        Assert.Contains(zig, s => s.Prefixes.Contains("main") && s.Body.Contains("std.process.Init", StringComparison.Ordinal));
        Assert.Contains(zig, s => s.Prefixes.Contains("test") && s.Body.StartsWith("test \"", StringComparison.Ordinal));
        Assert.Contains(zig, s => s.Prefixes.Contains("build") && s.Body.Contains("b.addExecutable", StringComparison.Ordinal));
        Assert.True(zig.Count >= 30, $"only {zig.Count} Zig snippets");
    }

    // ------------------------------------------------------------------ the Zig set against the real zig

    private static IReadOnlyDictionary<string, string> ZigExpansions()
    {
        var file = BuiltinLanguages.Catalog.Snippets.Single(s => s.LanguageId == "zig" && s.ResourceName.StartsWith(BuiltinLanguages.OwnSnippetPrefix, StringComparison.Ordinal));
        return SnippetFileParser.Parse(BuiltinLanguages.ReadText(file.ResourceName)!)
            .ToDictionary(d => d.Prefixes[0], d => SnippetResolver.Resolve(d.Body).Text, StringComparer.Ordinal);
    }

    /// <summary>Where a snippet goes: a declaration at the top level, an expression after <c>const x = </c>, a suffix after a call, else a statement.</summary>
    private static string InContext(string prefix, string code) => prefix switch
    {
        "std" or "import" or "main" or "mainv" or "fn" or "pfn" or "test" or "struct" or "enum" or "union" or "error" or "build" => code,
        "as" => $"const x = {code};\n",
        "catch" => $"fn f() !void {{\n    const x = g() {code};\n    _ = x;\n}}\n",
        _ => $"fn f() void {{\n{code}\n}}\n",
    };

    private static async Task<(int Exit, string Output)> ZigAsync(string zig, string directory, string? stdin, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(zig)
        {
            WorkingDirectory = directory, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        if (stdin is not null) await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = p.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await p.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (p.ExitCode, await stdout + await stderr);
    }

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task Every_zig_snippet_expands_to_code_zig_parses_and_a_project_made_of_them_builds_runs_and_tests()
    {
        Assert.SkipWhen(ZigTools.ZigSkipReason() is not null, ZigTools.ZigSkipReason() ?? "");
        var zig = ZigTools.FindZig()!;
        using var dir = new TempDirectory("zigsnippets-");
        var snippets = ZigExpansions();
        foreach (var (prefix, code) in snippets)
        {
            var (exit, output) = await ZigAsync(zig, dir.Path, InContext(prefix, code), "fmt", "--stdin");
            Assert.True(exit == 0, $"'{prefix}' does not parse:\n{code}\n{output}");
        }

        // the declarations whose defaults are whole code, compiled: build.zig from 'build', main.zig from 'std', 'main', 'gpa', 'list', 'map',
        // 'arena' and 'test' (each allocator in a block of its own: their defaults use the same names)
        dir.WriteFile("build.zig", snippets["build"]);
        var blocks = "{\n" + snippets["gpa"] + "\n" + snippets["list"] + "\ntry list.append(allocator, 1);\n" + snippets["map"] + "\ntry map.put(allocator, \"a\", 1);\n}\n"
            + "{\n" + snippets["arena"] + "\n_ = allocator;\n}\n" + "std.debug.print(\"snippets\\n\", .{});\n";
        dir.WriteFile("src/main.zig", snippets["std"] + "\n\n" + snippets["main"].Replace("_ = init;", "_ = init;\n" + blocks, StringComparison.Ordinal) + "\n\n" + snippets["test"] + "\n");
        var run = await ZigAsync(zig, dir.Path, null, "build", "run");
        Assert.True(run.Exit == 0 && run.Output.Contains("snippets", StringComparison.Ordinal), "zig build run:\n" + run.Output);
        var test = await ZigAsync(zig, dir.Path, null, "build", "test");
        Assert.True(test.Exit == 0, "zig build test:\n" + test.Output);
    }
}
