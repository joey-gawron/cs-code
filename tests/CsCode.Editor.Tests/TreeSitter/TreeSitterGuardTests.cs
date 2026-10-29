using Avalonia.Headless.XUnit;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.TreeSitter;
using Xunit;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterTestSupport;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// The guard against grammars whose error recovery explodes: a document that parses for longer than
/// <see cref="TreeSitterHighlighter.MaxParseTimeWithoutTree"/> without producing a tree is handed to TextMate. Serial: the limit is
/// process-wide and lowered here.
/// </summary>
[Collection(TreeSitterBenchCollection.Name)]
public class TreeSitterGuardTests
{
    /// <summary>Valid C# 11 the C# grammar (v0.23.5) does not parse: a list pattern with a slice designation.</summary>
    internal const string Unparsable = """
        namespace N;

        public static class K
        {
            public static string F(string s) => s switch
            {
                ['f', .. var n] when int.TryParse(n, out _) => "F" + n,
                _ => s,
            };
        }

        """;

    [AvaloniaFact]
    public async Task A_document_the_grammar_cannot_parse_in_time_is_handed_to_TextMate_and_a_normal_one_is_not()
    {
        RequireGrammar("c_sharp");
        var previous = TreeSitterHighlighter.MaxParseTimeWithoutTree;
        TreeSitterHighlighter.MaxParseTimeWithoutTree = TimeSpan.FromMilliseconds(150);
        try
        {
            using var host = ViewHost.Create();
            host.Config.Set(TreeSitterService.SettingKey, true);
            var (_, small, _) = await host.OpenAsync(TreeSitterViewTests.CSharp, "Small.cs");
            await TreeSitterViewTests.WaitForTreeSitter(small);

            var body = string.Concat(Enumerable.Range(0, 3000).Select(i => $"public class C{i} {{ int F{i}(int a) {{ return a * {i}; }} }}\n"));
            var (_, view, _) = await host.OpenAsync(Unparsable + body, "Pathological.cs");
            await ViewHost.WaitFor(() => view.Tokens is { TreeSitterGaveUp: true } t && t.TreeSitter is null && t.Store.ValidLines > 10, "TextMate takes over", 60_000);
            Assert.Equal("TextMate", view.SyntaxEngine);
            Assert.Equal("tree-sitter", small.SyntaxEngine); // the guard is per document
        }
        finally
        {
            TreeSitterHighlighter.MaxParseTimeWithoutTree = previous;
        }
    }
}
