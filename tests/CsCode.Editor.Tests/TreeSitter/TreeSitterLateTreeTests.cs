using Avalonia.Headless.XUnit;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.Threading;
using Xunit;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterTestSupport;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// T32: a tree-sitter document whose tree is not there when folding first asks (a slow first parse: here the syntax lane is held back past the
/// provider's wait) is folded by indentation meanwhile, and gets its syntax folds as soon as the tree lands, without an edit; the tree's folds
/// merge with a language server's.
/// </summary>
[Collection(TreeSitterBenchCollection.Name)] // holds a syntax lane for seconds: kept apart from the timing-sensitive tree-sitter tests
public class TreeSitterLateTreeTests
{
    // [package] has no indentation; the array's lines are indented, so indentation folding finds only (2, 4). The tree folds the tables (0-4,
    // 7-9) and the array (2-4), each without its closing line.
    private const string Toml = """
        [package]
        name = "demo"
        authors = [
            "a",
            "b",
        ]

        [dependencies]
        serde = "1"
        tokio = "1"

        """;

    private static async Task WaitForRanges(FoldingFeature folding, string what, (int, int)[] expected, int timeoutMs = 15_000)
    {
        try
        {
            await ViewHost.WaitFor(() => folding.Ranges.SequenceEqual(expected), what, timeoutMs);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message}; installed: {string.Join(" ", folding.Ranges)}", ex);
        }
    }

    [AvaloniaFact]
    public async Task Syntax_folds_arrive_with_the_tree_after_the_first_request_gave_up_waiting()
    {
        RequireGrammar("toml");
        using var host = ViewHost.Create();
        var lane = host.Factory.Tokens.TreeSitter.Lane;
        using var hold = new ManualResetEventSlim();
        lane.Post(WorkPriority.Interactive, null, _ => hold.Wait(TimeSpan.FromSeconds(60))); // the first parse cannot run before this returns
        try
        {
            var (doc, view, _) = await host.OpenAsync(Toml, "Cargo.toml");
            var folding = view.Feature<FoldingFeature>()!;
            // the provider gives up after its wait (TreeSitterLanguageFeatures.TreeWait, 2 s): indentation folds meanwhile
            await WaitForRanges(folding, "indentation folds", [(2, 4)], 20_000);
            Assert.Null(view.Tokens!.TreeSitter?.TreeText);
            var version = doc.Version;

            hold.Set(); // the parse lands: the folds follow without an edit
            await WaitForRanges(folding, "syntax folds after the tree landed", [(0, 4), (2, 4), (7, 9)], 20_000);
            Assert.Equal(version, doc.Version);
        }
        finally
        {
            hold.Set();
        }
    }

    [AvaloniaFact]
    public async Task A_language_servers_folds_and_the_trees_are_merged_the_servers_first()
    {
        RequireGrammar("toml");
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Toml, "Cargo.toml");
        var folding = view.Feature<FoldingFeature>()!;
        await WaitForRanges(folding, "tree folds", [(0, 4), (2, 4), (7, 9)]);

        // a server that folds only [dependencies], differently: it wins its start line; the tree keeps [package] and the array
        using var registration = host.Factory.Services.Features.Register<IFoldingRangeProvider>(DocumentSelector.ForLanguage("toml"), new ServerFolds());
        await WaitForRanges(folding, "merged folds", [(0, 4), (2, 4), (7, 8)]);
    }

    private sealed class ServerFolds : IFoldingRangeProvider
    {
        public ValueTask<IReadOnlyList<FoldingRange>?> ProvideFoldingRangesAsync(ITextDocument document, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<FoldingRange>?>([new FoldingRange(7, 8)]);
    }
}
