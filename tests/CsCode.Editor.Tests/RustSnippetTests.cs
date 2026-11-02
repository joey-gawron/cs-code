using CsCode.Editor.Languages;
using CsCode.Editor.Snippets;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>Language stage 2: Rust's bundled snippets (Snippets/Builtin/rust.json). BundledSnippetTests checks every file's syntax.</summary>
public class RustSnippetTests
{
    private static readonly SnippetIndentation Rust = new(BaseIndent: "", TabSize: 4, InsertSpaces: true);

    [Fact]
    public void Rust_gets_its_bundled_set()
    {
        Assert.Contains(BuiltinLanguages.Catalog.Snippets, s => s.LanguageId == "rust" && s.ResourceName == BuiltinLanguages.OwnSnippetPrefix + "rust.json");
        using var host = TestHost.Create(new ThreadAffinityUiThread());
        using var svc = new SnippetService(host.Services);
        var rust = svc.GetSnippets("rust");
        Assert.True(rust.Count >= 50, $"only {rust.Count} Rust snippets");
        foreach (var prefix in new[] { "fn", "main", "struct", "impl", "match", "iflet", "tests", "test", "derive", "println" })
            Assert.Contains(rust, s => s.Prefixes.Contains(prefix));
        Assert.DoesNotContain(svc.GetSnippets("python"), s => s.Prefixes.Contains("iflet")); // Rust's set stays with Rust
    }

    [Fact]
    public void The_test_module_expands_as_rustfmt_lays_it_out()
    {
        var tests = Body("tests");
        var r = SnippetResolver.Resolve(tests, new SnippetVariableContext(), Rust);
        Assert.Equal("#[cfg(test)]\nmod tests {\n    use super::*;\n\n    #[test]\n    fn it_works() {\n        \n    }\n}", r.Text);
    }

    [Fact]
    public void Print_debug_repeats_the_name_it_prints()
    {
        var r = SnippetResolver.Resolve(Body("printd"), new SnippetVariableContext(), Rust);
        Assert.Equal("println!(\"value = {:?}\", value);", r.Text);
    }

    private static string Body(string prefix)
    {
        using var host = TestHost.Create(new ThreadAffinityUiThread());
        using var svc = new SnippetService(host.Services);
        return Assert.Single(svc.GetSnippets("rust"), s => s.Prefixes.Contains(prefix)).Body;
    }
}
