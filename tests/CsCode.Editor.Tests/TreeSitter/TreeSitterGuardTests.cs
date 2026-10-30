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
            host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TreeSitter);
            var (_, small, _) = await host.OpenAsync(TreeSitterViewTests.CSharp, "Small.cs");
            await TreeSitterViewTests.WaitForTreeSitter(small);

            // The explosion is not deterministic under slicing: unsliced, this parse takes ~4 s; in the engine's 6 ms slices (each halt and
            // resume changes the error-recovery work) 3,000 classes took 90-876 ms over 8 runs, twice under the 150 ms limit (the parse then
            // simply completes and the test failed: "parse time 0 ms" with a finished tree), and 10,000 classes took 272-952 ms (W1b, T16).
            var body = string.Concat(Enumerable.Range(0, 10_000).Select(i => $"public class C{i} {{ int F{i}(int a) {{ return a * {i}; }} }}\n"));
            var (_, view, _) = await host.OpenAsync(Unparsable + body, "Pathological.cs");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!(view.Tokens is { TreeSitterGaveUp: true } t && t.TreeSitter is null && t.Store.ValidLines > 10))
            {
                // T16: say where it stands if it does not get there (a load miss looks different from a broken guard)
                if (clock.Elapsed > TimeSpan.FromSeconds(60))
                    Assert.Fail($"TextMate did not take over in 60 s (load {TreeSitterBenchmarks.Load1():0.0}): gave up {view.Tokens?.TreeSitterGaveUp}, " +
                        $"wanted {view.Tokens?.TreeSitterWanted}, engine {(view.Tokens?.TreeSitter is { } e ? $"parse time {e.ParseTimeSinceTree.TotalMilliseconds:0} ms" : "none")}, " +
                        $"valid lines {view.Tokens?.Store.ValidLines}");
                ViewHost.Pump();
                await Task.Delay(15);
            }
            TestContext.Current.TestOutputHelper?.WriteLine($"handed to TextMate after {clock.Elapsed.TotalMilliseconds:0} ms (load {TreeSitterBenchmarks.Load1():0.0})");
            Assert.Equal("TextMate", view.SyntaxEngine);
            Assert.Equal("tree-sitter", small.SyntaxEngine); // the guard is per document
        }
        finally
        {
            TreeSitterHighlighter.MaxParseTimeWithoutTree = previous;
        }
    }
}
