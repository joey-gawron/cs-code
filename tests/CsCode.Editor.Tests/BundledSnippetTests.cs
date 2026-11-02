using CsCode.Core.Contributions;
using CsCode.Editor.Languages;
using CsCode.Editor.Snippets;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>Language stage 1: CsCode's own built-in snippets (Snippets/Builtin/&lt;language&gt;.json), Python first; the user's snippets win.</summary>
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
}
