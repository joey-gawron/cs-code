using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Languages;
using CsCode.Editor.Tests.Support;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests;

/// <summary>
/// What the editor gives the search editor (CsCode.Panels.Search): the <c>search-result</c> language with its grammar, highlighted match ranges
/// from <see cref="IMatchHighlightProvider"/>, and double-clicks a <see cref="IDoubleClickHandler"/> takes.
/// </summary>
public sealed class SearchResultEditorTests
{
    private const string Results = "# Query: alpha\n# Flags: RegExp\n\n2 results - 1 file\n\nsrc/a.cs:\n  1  using System;\n  2: class Alpha { alpha }\n";

    private sealed class Highlights : IMatchHighlightProvider
    {
        private readonly Emitter<Uri> _changed = new();
        public Func<ITextDocument, IReadOnlyList<TextRange>> Ranges = _ => [];
        public int Requests;
        public IEventSource<Uri> Changed => _changed;
        public void Fire(Uri uri) => _changed.Fire(uri);

        public ValueTask<IReadOnlyList<TextRange>?> ProvideMatchHighlightsAsync(ITextDocument document, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return ValueTask.FromResult<IReadOnlyList<TextRange>?>(Ranges(document));
        }
    }

    private sealed class Clicks(bool take) : IDoubleClickHandler
    {
        public List<TextPosition> Seen { get; } = new();
        public bool OnDoubleClick(ITextDocument document, TextPosition position)
        {
            Seen.Add(position);
            return take;
        }
    }

    /// <summary>Every occurrence of "alpha" (any case) after the header: what the search editor's provider reports.</summary>
    private static IReadOnlyList<TextRange> Alphas(ITextDocument doc)
    {
        var list = new List<TextRange>();
        for (var l = 2; l < doc.LineCount; l++)
        {
            var text = doc.GetLineText(l);
            for (var i = text.IndexOf("alpha", StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf("alpha", i + 1, StringComparison.OrdinalIgnoreCase))
                list.Add(new TextRange(new TextPosition(l, i), new TextPosition(l, i + 5)));
        }
        return list;
    }

    [AvaloniaFact]
    public async Task Code_search_files_open_as_search_results_with_their_grammar()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync(Results, "saved.code-search");
        Assert.Equal("search-result", doc.LanguageId);
        var info = BuiltinLanguageSupport.Languages.Single(l => l.Id == "search-result");
        Assert.Equal("text.searchResult", info.GrammarScope);
        Assert.True(info.HasOwnGrammar);
        Assert.Equal(LanguageConfigurationOrigin.None, info.Configuration); // VS Code's search-result language has none either

        await ViewHost.WaitFor(() => view.Tokens?.Store.ValidLines >= doc.LineCount - 1, "tokenised");
        string[] Scopes(int line, int column)
        {
            var t = view.Tokens!.Store.GetTokens(line)!;
            var idx = 0;
            for (var i = 0; i < t.Length / 2; i++) if (t[i * 2] <= column) idx = i;
            return view.Tokens.Interner.Scopes(t[idx * 2 + 1]);
        }
        Assert.Contains("entity.other.attribute-name", Scopes(0, 2));          // "# Query"
        Assert.Contains("keyword.other", Scopes(1, 10));                       // RegExp
        Assert.Contains("meta.path.search", Scopes(5, 1));                     // the path line
        Assert.Contains("meta.resultLinePrefix.matchLinePrefix.search", Scopes(7, 2));
        Assert.Contains("meta.resultLinePrefix.contextLinePrefix.search", Scopes(6, 2));
    }

    [AvaloniaFact]
    public async Task Reported_ranges_are_highlighted_follow_edits_and_are_asked_again()
    {
        using var host = ViewHost.Create();
        var highlights = new Highlights { Ranges = Alphas };
        host.Factory.Services.Features.Register<IMatchHighlightProvider>(DocumentSelector.ForLanguage("search-result"), highlights);
        var (doc, view, _) = await host.OpenAsync(Results, "saved.code-search");
        var feature = view.Feature<MatchHighlightFeature>()!;
        await ViewHost.WaitFor(() => feature.Ranges.Count == 2, "the provider's ranges");
        Assert.All(feature.Ranges, r => Assert.Equal("alpha", doc.GetText(r), StringComparer.OrdinalIgnoreCase));

        // a line inserted above: the ranges move with it at once, then the provider is asked again
        var before = highlights.Requests;
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(5, 0), "docs/x.md:\n  1: alpha\n\n")]);
        Assert.All(feature.Ranges, r => Assert.Equal("alpha", doc.GetText(r), StringComparer.OrdinalIgnoreCase));
        await ViewHost.WaitFor(() => feature.Ranges.Count == 3, "asked again after the edit");
        Assert.True(highlights.Requests > before);

        // the provider says its highlights changed without an edit
        highlights.Ranges = _ => [];
        highlights.Fire(doc.Uri);
        await ViewHost.WaitFor(() => feature.Ranges.Count == 0, "cleared");
    }

    [AvaloniaFact]
    public async Task A_document_no_provider_serves_gets_no_highlights_and_no_requests()
    {
        using var host = ViewHost.Create();
        var highlights = new Highlights { Ranges = Alphas };
        host.Factory.Services.Features.Register<IMatchHighlightProvider>(DocumentSelector.ForLanguage("search-result"), highlights);
        var (_, view, _) = await host.OpenAsync("class Alpha { }\n", "a.cs");
        await Task.Delay(MatchHighlightFeature.Delay * 3, TestContext.Current.CancellationToken);
        ViewHost.Pump();
        Assert.Empty(view.Feature<MatchHighlightFeature>()!.Ranges);
        Assert.Equal(0, highlights.Requests);
    }

    [Fact]
    public void Ranges_shift_with_edits_and_ranges_an_edit_touches_go()
    {
        TextRange R(int l, int s, int e) => new(new TextPosition(l, s), new TextPosition(l, e));
        TextContentChange Change(TextRange range, string text) => new(range, 0, 0, text);
        TextRange[] ranges = [R(1, 4, 9), R(3, 2, 7), R(3, 10, 12)];

        // typing on an earlier line: nothing moves (same array)
        Assert.Same(ranges, MatchHighlightFeature.Shift(ranges, Change(TextRange.At(0, 3), "x")));
        // typing before a range on its line moves it along the line
        Assert.Equal([R(1, 4, 9), R(3, 4, 9), R(3, 12, 14)], MatchHighlightFeature.Shift(ranges, Change(TextRange.At(3, 0), "ab")));
        // typing right after a range leaves it
        Assert.Same(ranges, MatchHighlightFeature.Shift(ranges, Change(TextRange.At(1, 9), "z")));
        // lines inserted above move everything below
        Assert.Equal([R(3, 4, 9), R(5, 2, 7), R(5, 10, 12)], MatchHighlightFeature.Shift(ranges, Change(TextRange.At(1, 0), "a\nb\n")));
        // an edit inside a range drops it; lines removed move the rest up and along the joined line
        Assert.Equal([R(1, 4, 9), R(2, 5, 7)], MatchHighlightFeature.Shift(ranges, Change(new TextRange(new TextPosition(2, 0), new TextPosition(3, 8)), "abc")));
    }

    [AvaloniaFact]
    public async Task A_double_click_a_handler_takes_puts_the_caret_there_and_selects_nothing()
    {
        using var host = ViewHost.Create();
        var clicks = new Clicks(take: true);
        host.Factory.Services.Features.Register<IDoubleClickHandler>(DocumentSelector.ForLanguage("search-result"), clicks);
        var (doc, view, window) = await host.OpenAsync(Results, "saved.code-search");
        var offset = doc.GetText().IndexOf("Alpha", StringComparison.Ordinal) + 2;
        var point = PointOf(window, view, offset);
        DoubleClick(window, point);
        var seen = Assert.Single(clicks.Seen);
        Assert.Equal(doc.PositionAt(offset), seen);
        Assert.True(view.Controller.Selections[0].IsEmpty);
        Assert.Equal(seen, view.Controller.Caret);
    }

    [AvaloniaFact]
    public async Task A_double_click_no_handler_takes_selects_the_word_as_always()
    {
        using var host = ViewHost.Create();
        var clicks = new Clicks(take: false);
        host.Factory.Services.Features.Register<IDoubleClickHandler>(DocumentSelector.ForLanguage("search-result"), clicks);
        var (doc, view, window) = await host.OpenAsync(Results, "saved.code-search");
        var offset = doc.GetText().IndexOf("Alpha", StringComparison.Ordinal) + 2;
        DoubleClick(window, PointOf(window, view, offset));
        Assert.Single(clicks.Seen);
        Assert.Equal("Alpha", doc.GetText(view.Controller.Selections[0]));
    }

    [AvaloniaFact]
    public async Task Screenshot_of_a_search_editor_with_its_matches_highlighted()
    {
        using var host = ViewHost.Create();
        await host.Factory.Themes.SetCurrentAsync("vscode.dark-plus", TestContext.Current.CancellationToken);
        host.Factory.Services.Features.Register<IMatchHighlightProvider>(DocumentSelector.ForLanguage("search-result"), new Highlights { Ranges = Alphas });
        var text = "# Query: alpha\n# Flags: WordMatch\n# Including: src\n# ContextLines: 1\n\n3 results - 2 files\n\ndocs/readme.md:\n  1: alpha and beta\n\n" +
                   "src/a.cs:\n  1  using System;\n  2: class Alpha\n  3  {\n  4:     int alpha = 1;\n  5  }\n\n";
        var (doc, view, window) = await host.OpenAsync(text, "results.code-search", 700, 360);
        var feature = view.Feature<MatchHighlightFeature>()!;
        await ViewHost.WaitFor(() => feature.Ranges.Count == 3 && view.Tokens?.Store.ValidLines >= doc.LineCount - 1, "highlights and tokens");
        ViewHost.Pump();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.scratch/screenshots"));
        Directory.CreateDirectory(dir);
        using var fs = File.Create(Path.Combine(dir, "search-editor.png"));
        frame!.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void DoubleClick(Avalonia.Controls.Window window, Avalonia.Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        ViewHost.Pump();
    }
}
