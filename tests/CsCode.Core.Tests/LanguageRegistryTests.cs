using CsCode.Core.Contributions;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using Xunit;

namespace CsCode.Core.Tests;

public class LanguageRegistryTests
{
    private sealed class Hover(string tag) : IHoverProvider
    {
        public string Tag { get; } = tag;
        public ValueTask<Languages.Hover?> ProvideHoverAsync(ITextDocument d, TextPosition p, CancellationToken ct) =>
            ValueTask.FromResult<Languages.Hover?>(null);
    }

    private static readonly Uri CsUri = new("file:///x/a.cs");

    [Fact]
    public void Registry_orders_by_specificity_then_recency_and_filters_by_type_and_selector()
    {
        var reg = new LanguageFeatureRegistry();
        var wildcard = new Hover("wildcard");
        var csOld = new Hover("cs-old");
        var csNew = new Hover("cs-new");
        var py = new Hover("py");
        reg.Register(DocumentSelector.All, wildcard);
        reg.Register(DocumentSelector.ForLanguage("csharp"), csOld);
        reg.Register(DocumentSelector.ForLanguage("csharp"), csNew);
        reg.Register(DocumentSelector.ForLanguage("python"), py);

        var result = reg.GetProviders<IHoverProvider>(CsUri, "csharp").Cast<Hover>().Select(h => h.Tag).ToArray();

        Assert.Equal(["cs-new", "cs-old", "wildcard"], result);
        Assert.Empty(reg.GetProviders<ICompletionProvider>(CsUri, "csharp"));
        Assert.Equal(4, reg.GetAll<IHoverProvider>().Count);
    }

    [Fact]
    public void Disposing_registration_removes_provider_and_signals_change()
    {
        var reg = new LanguageFeatureRegistry();
        var changed = new List<Type>();
        using var sub = reg.ProvidersChanged.Subscribe(changed.Add);
        var token = reg.Register(DocumentSelector.All, new Hover("h"));

        token.Dispose();
        token.Dispose();

        Assert.Empty(reg.GetAll<IHoverProvider>());
        Assert.Equal([typeof(IHoverProvider), typeof(IHoverProvider)], changed);
    }

    [Fact]
    public void DiagnosticService_merges_owners_counts_severities_and_clears()
    {
        var svc = new DiagnosticService();
        var touched = new List<Uri>();
        using var sub = svc.Changed.Subscribe(u => touched.AddRange(u));
        var r = TextRange.At(0, 0);
        svc.Set(CsUri, "lsp:omnisharp", [new Diagnostic(r, "e1"), new Diagnostic(r, "w1", DiagnosticSeverity.Warning)]);
        svc.Set(CsUri, "ext:eslint", [new Diagnostic(r, "h1", DiagnosticSeverity.Hint)]);

        Assert.Equal(3, svc.Get(CsUri).Count);
        Assert.Equal((1, 1, 0, 1), svc.Counts);

        svc.Clear("lsp:omnisharp");
        Assert.Single(svc.Get(CsUri));
        svc.Clear(CsUri, "ext:eslint");
        Assert.Empty(svc.GetAll());
        Assert.NotEmpty(touched);
    }

    [Fact]
    public void ContributionRegistry_adds_removes_and_tracks_by_extension()
    {
        var reg = new ContributionRegistry();
        var set = reg.Of<LanguageContribution>();
        var notifications = 0;
        using var sub = set.Changed.Subscribe(_ => notifications++);

        LanguageContribution Lang(string id) => new(id, [], [".x"], [], []);
        var t1 = reg.Contribute("pub.ext1", "/e1", [Lang("a"), Lang("b")]);
        reg.Contribute("pub.ext2", "/e2", [Lang("c")]);
        Assert.Equal(3, set.Items.Count);

        t1.Dispose();
        Assert.Equal(["c"], set.Items.Select(i => i.Value.Id));

        reg.RemoveSource("pub.ext2");
        Assert.Empty(set.Items);
        Assert.Equal(4, notifications);
        Assert.Empty(reg.Of<GrammarContribution>().Items);
    }

    [Theory]
    [InlineData("#fff", 255, 255, 255, 255)]
    [InlineData("#1e1e1e", 30, 30, 30, 255)]
    [InlineData("#007ACC80", 0, 122, 204, 128)]
    [InlineData("#abcd", 170, 187, 204, 221)]
    public void Rgba_parses_vscode_color_formats(string hex, int r, int g, int b, int a)
    {
        Assert.True(Rgba.TryParse(hex, out var c));
        Assert.Equal((r, g, b, a), ((int)c.R, (int)c.G, (int)c.B, (int)c.A));
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("#gggggg")]
    [InlineData("red")]
    public void Rgba_rejects_garbage(string hex) => Assert.False(Rgba.TryParse(hex, out _));

    [Fact]
    public void Rgba_round_trips_through_hex() => Assert.Equal("#007ACC", Rgba.Parse("#007acc").ToHex());
}
