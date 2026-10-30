using Avalonia.Headless.XUnit;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// T32: folding asks every folding provider and merges their ranges as VS Code does (ranked by provider, one range per start line, nesting kept),
/// plus the language's #region markers; indentation only when no provider has anything. T31: breadcrumbs follow the symbol providers that come
/// and go after a file was opened, without waiting for an edit. Real headless views, the real FoldingFeature and AvaloniaEdit's FoldingManager.
/// </summary>
public class FoldingProvidersTests
{
    private const string CSharp = """
        namespace Demo
        {
            #region Fields
            class A
            {
                int x;
            }
            #endregion
            class B
            {
                void M()
                {
                }
            }
        }

        """;

    private sealed class FakeFolds(Func<CancellationToken, Task<IReadOnlyList<FoldingRange>?>> provide) : IFoldingRangeProvider
    {
        public int Calls;

        public FakeFolds(params FoldingRange[] ranges) : this(_ => Task.FromResult<IReadOnlyList<FoldingRange>?>(ranges)) { }

        public async ValueTask<IReadOnlyList<FoldingRange>?> ProvideFoldingRangesAsync(ITextDocument document, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return await provide(ct).ConfigureAwait(false);
        }
    }

    private static FoldRange R(int start, int end) => new(start, end);

    private static async Task WaitForRanges(FoldingFeature folding, params (int, int)[] expected)
    {
        try
        {
            await ViewHost.WaitFor(() => folding.Ranges.SequenceEqual(expected), $"folds {string.Join(" ", expected)}");
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message}; installed: {string.Join(" ", folding.Ranges)}", ex);
        }
    }

    // ---- the merge ----

    [Fact]
    public void Merge_keeps_the_best_ranked_range_per_start_line_and_nests_the_rest()
    {
        var server = new[] { R(3, 5), R(8, 12), R(10, 11) };
        var other = new[] { R(0, 13), R(8, 13) };
        var markers = new[] { R(2, 7) };
        var merged = FoldComputer.Merge([server, other, markers], lineCount: 15);
        Assert.Equal([R(0, 13), R(2, 7), R(3, 5), R(8, 12), R(10, 11)], merged);

        // the same start line: the first source wins whichever is larger
        Assert.Equal([R(1, 3)], FoldComputer.Merge([[R(1, 3)], [R(1, 9)]], 10));
        Assert.Equal([R(1, 9)], FoldComputer.Merge([[R(1, 9)], [R(1, 3)]], 10));
    }

    [Fact]
    public void Merge_drops_invalid_ranges_backs_out_of_closed_levels_keeps_overlaps_and_is_capped()
    {
        // invalid: empty, inverted, negative, past the end
        Assert.Empty(FoldComputer.Merge([[R(2, 2), R(5, 4), R(-1, 3), R(3, 10)]], 10));
        // after a nested range closes, a later range nests in the level that still contains it
        Assert.Equal([R(0, 9), R(1, 2), R(4, 6), R(5, 6)], FoldComputer.Merge([[R(0, 9), R(1, 2), R(4, 6), R(5, 6)]], 10));
        // overlapping without nesting (two providers disagreeing): kept, as VS Code keeps it
        Assert.Equal([R(0, 4), R(2, 6)], FoldComputer.Merge([[R(0, 4)], [R(2, 6)]], 10));
        var many = Enumerable.Range(0, FoldComputer.MaxRanges + 50).Select(i => R(i * 2, i * 2 + 1)).ToList();
        Assert.Equal(FoldComputer.MaxRanges, FoldComputer.Merge([many], int.MaxValue).Count);
    }

    // ---- the view ----

    [AvaloniaFact]
    public async Task Every_providers_ranges_and_the_region_markers_are_merged_in_rank_order()
    {
        using var host = ViewHost.Create();
        var features = host.Factory.Services.Features;
        var server = new FakeFolds(new FoldingRange(3, 5), new FoldingRange(8, 12), new FoldingRange(10, 11));
        var other = new FakeFolds(new FoldingRange(0, 13), new FoldingRange(8, 13));
        using var r1 = features.Register<IFoldingRangeProvider>(DocumentSelector.ForLanguage("csharp"), server); // ranks first
        using var r2 = features.Register<IFoldingRangeProvider>(DocumentSelector.All, other);
        var (_, view, _) = await host.OpenAsync(CSharp, "a.cs");
        var folding = view.Feature<FoldingFeature>()!;
        // the #region pair (2-7) is there although both providers answered; (8,13) lost to the server's (8,12) on the same line
        await WaitForRanges(folding, (0, 13), (2, 7), (3, 5), (8, 12), (10, 11));
        Assert.True(server.Calls > 0 && other.Calls > 0);
    }

    [AvaloniaFact]
    public async Task A_provider_with_nothing_a_failing_one_or_a_hung_one_leaves_the_others_ranges()
    {
        using var host = ViewHost.Create();
        var features = host.Factory.Services.Features;
        var starting = new FakeFolds(_ => Task.FromResult<IReadOnlyList<FoldingRange>?>(null));          // a server still starting
        var broken = new FakeFolds(_ => throw new InvalidOperationException("provider bug"));
        var hung = new FakeFolds(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return null;
        });
        var tree = new FakeFolds(new FoldingRange(8, 12));
        using var r1 = features.Register<IFoldingRangeProvider>(DocumentSelector.ForLanguage("csharp"), starting);
        using var r2 = features.Register<IFoldingRangeProvider>(DocumentSelector.ForLanguage("csharp"), broken);
        using var r3 = features.Register<IFoldingRangeProvider>(DocumentSelector.ForLanguage("csharp"), hung);
        using var r4 = features.Register<IFoldingRangeProvider>(DocumentSelector.All, tree);
        var (_, view, _) = await host.OpenAsync(CSharp, "a.cs");
        var folding = view.Feature<FoldingFeature>()!;
        await WaitForRanges(folding, (2, 7), (8, 12)); // after the hung one's deadline: the last provider's range and the markers
    }

    [AvaloniaFact]
    public async Task Without_any_answer_indentation_and_markers_fold_and_a_provider_that_arrives_later_is_merged_in()
    {
        using var host = ViewHost.Create();
        var features = host.Factory.Services.Features;
        using var silent = features.Register<IFoldingRangeProvider>(DocumentSelector.All, new FakeFolds(_ => Task.FromResult<IReadOnlyList<FoldingRange>?>([])));
        var (_, view, _) = await host.OpenAsync(CSharp, "a.cs");
        var folding = view.Feature<FoldingFeature>()!;
        // the editor's own fallback: indentation plus markers (the marker pair replaces the indentation region on its line)
        await WaitForRanges(folding, (1, 13), (2, 7), (4, 5), (9, 12));

        // a language server registers later: its ranges (plus the markers) replace the fallback, no edit needed
        var server = new FakeFolds(new FoldingRange(8, 12));
        using var late = features.Register<IFoldingRangeProvider>(DocumentSelector.ForLanguage("csharp"), server);
        await WaitForRanges(folding, (2, 7), (8, 12));
    }

    // ---- breadcrumbs (T31) ----

    private sealed class FakeSymbols(string name, Func<CancellationToken, Task>? gate = null) : IDocumentSymbolProvider
    {
        public int Calls;

        public async ValueTask<IReadOnlyList<DocumentSymbol>?> ProvideDocumentSymbolsAsync(ITextDocument document, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (gate is not null) await gate(ct).ConfigureAwait(false);
            return [new DocumentSymbol(name, SymbolKind.Class, TextRange.FromLines(0, 0, 14, 1), TextRange.At(0, 0))];
        }
    }

    [AvaloniaFact]
    public async Task Breadcrumbs_ask_again_when_symbol_providers_come_or_go_without_an_edit()
    {
        using var host = ViewHost.Create();
        var features = host.Factory.Services.Features;
        using var tree = features.Register<IDocumentSymbolProvider>(DocumentSelector.All, new FakeSymbols("from the tree"));
        var (doc, view, _) = await host.OpenAsync(CSharp, "a.cs");
        view.Controller.SetSelection(TextRange.At(5, 4));
        Assert.Equal(["from the tree"], await view.Controller.GetBreadcrumbsAsync());
        var version = doc.Version;

        var server = new FakeSymbols("from the server");
        var registration = features.Register<IDocumentSymbolProvider>(DocumentSelector.ForLanguage("csharp"), server); // a server that started later
        Assert.Equal(["from the server"], await view.Controller.GetBreadcrumbsAsync());
        Assert.Equal(["from the server"], await view.Controller.GetBreadcrumbsAsync());
        Assert.Equal(1, server.Calls); // cached again for this set of providers
        registration.Dispose();                                                                                      // and it stopped
        Assert.Equal(["from the tree"], await view.Controller.GetBreadcrumbsAsync());
        Assert.Equal(version, doc.Version); // no edit anywhere
    }

    [AvaloniaFact]
    public async Task An_answer_in_flight_when_a_provider_arrives_is_asked_for_again()
    {
        using var host = ViewHost.Create();
        var features = host.Factory.Services.Features;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slow = features.Register<IDocumentSymbolProvider>(DocumentSelector.All, new FakeSymbols("old", ct => release.Task.WaitAsync(ct)));
        var (_, view, _) = await host.OpenAsync(CSharp, "a.cs");
        view.Controller.SetSelection(TextRange.At(5, 4));
        var asking = view.Controller.GetBreadcrumbsAsync().AsTask();
        using var server = features.Register<IDocumentSymbolProvider>(DocumentSelector.ForLanguage("csharp"), new FakeSymbols("new"));
        release.SetResult();
        await ViewHost.WaitFor(() => asking.IsCompleted, "the breadcrumbs");
        Assert.Equal(["new"], await asking);
    }
}
