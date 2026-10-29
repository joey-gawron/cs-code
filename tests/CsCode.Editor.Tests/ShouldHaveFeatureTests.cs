using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests;

/// <summary>The SHOULD items that are implemented: breadcrumbs data, Ctrl+Click on URLs, accessibility names.</summary>
public class ShouldHaveFeatureTests
{
    // ---- breadcrumbs ----

    private static DocumentSymbol Sym(string name, SymbolKind kind, int l1, int c1, int l2, int c2, params DocumentSymbol[] children) =>
        new(name, kind, TextRange.FromLines(l1, c1, l2, c2), TextRange.FromLines(l1, c1, l1, c1 + name.Length), Children: children.Length == 0 ? null : children);

    private sealed class FakeSymbols(Func<IReadOnlyList<DocumentSymbol>?> provide) : IDocumentSymbolProvider
    {
        public int Calls;

        public ValueTask<IReadOnlyList<DocumentSymbol>?> ProvideDocumentSymbolsAsync(ITextDocument document, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult(provide());
        }
    }

    [Fact]
    public void The_path_follows_the_innermost_containing_symbol_at_every_level()
    {
        var symbols = new[]
        {
            Sym("Demo", SymbolKind.Namespace, 0, 0, 30, 1,
                Sym("Program", SymbolKind.Class, 2, 0, 28, 1,
                    Sym("Main", SymbolKind.Method, 4, 4, 12, 5),
                    Sym("Compute", SymbolKind.Method, 14, 4, 20, 5))),
            Sym("Other", SymbolKind.Class, 31, 0, 40, 1),
        };
        Assert.Equal(["Demo", "Program", "Compute"], BreadcrumbFeature.PathTo(symbols, new TextPosition(16, 8)));
        Assert.Equal(["Demo", "Program"], BreadcrumbFeature.PathTo(symbols, new TextPosition(13, 0))); // between members
        Assert.Equal(["Other"], BreadcrumbFeature.PathTo(symbols, new TextPosition(35, 2)));
        Assert.Empty(BreadcrumbFeature.PathTo(symbols, new TextPosition(100, 0)));
        Assert.Empty(BreadcrumbFeature.PathTo([], TextPosition.Zero));
    }

    [AvaloniaFact]
    public async Task The_controller_returns_the_symbol_path_for_the_caret_and_caches_per_document_version()
    {
        using var host = ViewHost.Create();
        var provider = new FakeSymbols(() => [Sym("Demo", SymbolKind.Namespace, 0, 0, 5, 1, Sym("Program", SymbolKind.Class, 1, 0, 4, 1, Sym("Main", SymbolKind.Method, 2, 4, 3, 5)))]);
        host.Factory.Services.Features.Register<IDocumentSymbolProvider>(DocumentSelector.All, provider);
        var (doc, view, _) = await host.OpenAsync("namespace Demo {\nclass Program {\n    void Main() {\n    }\n}\n}\n", "a.cs");
        view.Controller.SetSelection(TextRange.At(2, 8));
        Assert.Equal(["Demo", "Program", "Main"], await view.Controller.GetBreadcrumbsAsync());
        view.Controller.SetSelection(TextRange.At(1, 3));
        Assert.Equal(["Demo", "Program"], await view.Controller.GetBreadcrumbsAsync());
        Assert.Equal(1, provider.Calls); // the second answer came from the cache: same document version

        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "// edit\n")]);
        await view.Controller.GetBreadcrumbsAsync();
        Assert.Equal(2, provider.Calls); // a new version asks again
    }

    [AvaloniaFact]
    public async Task Without_a_symbol_provider_or_with_a_failing_one_the_breadcrumbs_are_empty()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("abc", "a.cs");
        Assert.Empty(await view.Controller.GetBreadcrumbsAsync());
        host.Factory.Services.Features.Register<IDocumentSymbolProvider>(DocumentSelector.All, new FakeSymbols(() => throw new InvalidOperationException("provider bug")));
        Assert.Empty(await view.Controller.GetBreadcrumbsAsync());
    }

    // ---- inlay hints ----

    private sealed class FakeInlayHints(Func<TextRange, IReadOnlyList<InlayHint>?> provide) : IInlayHintsProvider
    {
        public int Calls;

        public ValueTask<IReadOnlyList<InlayHint>?> ProvideInlayHintsAsync(ITextDocument document, TextRange range, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult(provide(range));
        }
    }

    [AvaloniaFact]
    public async Task Inlay_hints_appear_inline_follow_edits_and_go_away_when_disabled()
    {
        using var host = ViewHost.Create();
        var provider = new FakeInlayHints(_ =>
        [
            new InlayHint(new TextPosition(0, 8), "first:", InlayHintKind.Parameter, PaddingRight: true),
            new InlayHint(new TextPosition(0, 11), "second:", InlayHintKind.Parameter, PaddingRight: true),
            new InlayHint(new TextPosition(99, 0), "beyond the document", InlayHintKind.Type),
        ]);
        host.Factory.Services.Features.Register<IInlayHintsProvider>(DocumentSelector.All, provider);
        var (doc, view, _) = await host.OpenAsync("Compute(1, 2);\n", "a.cs");
        var feature = view.Feature<InlayHintFeature>()!;
        await ViewHost.WaitFor(() => feature.Placements.Count == 2, "hints placed (after the 400 ms settle)");
        Assert.Equal([(8, "first:"), (11, "second:")], feature.Placements.ToArray());
        ViewHost.Pump();
        Assert.Equal(2, view.TextView.VisualLines[0].Elements.OfType<AvaloniaEdit.Rendering.InlineObjectElement>().Count());

        view.Controller.SetSelection(TextRange.At(0, 0));
        Type(view, "x"); // an edit before the hints: the anchors move with the text immediately
        Assert.Equal([(9, "first:"), (12, "second:")], feature.Placements.ToArray());

        host.Config.Set("editor.inlayHints.enabled", "off");
        await ViewHost.WaitFor(() => feature.Placements.Count == 0, "hints removed when disabled");
        ViewHost.Pump();
        Assert.Empty(view.TextView.VisualLines[0].Elements.OfType<AvaloniaEdit.Rendering.InlineObjectElement>());
    }

    [AvaloniaFact]
    public async Task A_hung_inlay_hint_provider_costs_nothing()
    {
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<IInlayHintsProvider>(DocumentSelector.All, new HangingInlayHints());
        var (_, view, _) = await host.OpenAsync("abc\n", "a.cs");
        Type(view, "x");
        await Task.Delay(600);
        ViewHost.Pump();
        Assert.Empty(view.Feature<InlayHintFeature>()!.Placements);
        Assert.StartsWith("xabc", view.Document.GetText());
    }

    private sealed class HangingInlayHints : IInlayHintsProvider
    {
        public async ValueTask<IReadOnlyList<InlayHint>?> ProvideInlayHintsAsync(ITextDocument document, TextRange range, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return null;
        }
    }

    // ---- URLs ----

    [AvaloniaFact]
    public async Task Ctrl_click_on_a_url_opens_it_through_the_window_service_and_a_plain_click_does_not()
    {
        var external = new ExternalOpenRecorder(new CsCode.TestDoubles.RecordingWindowService());
        using var host = ViewHost.Create(s => s.AddSingleton<CsCode.Core.Platform.IWindowService>(external));
        var (doc, view, window) = await host.OpenAsync("// see https://example.com/docs?x=1, then relax\nplain text\n", "a.cs");
        ViewHost.Pump();
        var offset = doc.GetText().IndexOf("example", StringComparison.Ordinal);
        var nav = view.Feature<NavigationFeature>()!;
        var url = nav.UrlAt(offset)!.Value;
        Assert.Equal("https://example.com/docs?x=1", url.Uri.ToString()); // the trailing comma is not part of the URL
        Assert.Null(nav.UrlAt(doc.GetText().IndexOf("plain", StringComparison.Ordinal)));

        var point = PointOf(window, view, offset);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        ViewHost.Pump();
        Assert.Empty(external.OpenedExternally);

        window.MouseMove(point, RawInputModifiers.Control);
        await ViewHost.WaitFor(() => view.Editor.Cursor is not null, "hand cursor over the URL while Ctrl is held");
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.Control);
        await ViewHost.WaitFor(() => external.OpenedExternally.Count == 1, "URL opened");
        Assert.Equal(new Uri("https://example.com/docs?x=1"), external.OpenedExternally[0]);
    }

    [AvaloniaFact]
    public async Task Only_http_and_https_urls_are_links()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("file:///etc/passwd javascript:alert(1) ftp://example.com http://ok.example\n", "a.txt");
        var nav = view.Feature<NavigationFeature>()!;
        Assert.Null(nav.UrlAt(doc.GetText().IndexOf("etc", StringComparison.Ordinal)));
        Assert.Null(nav.UrlAt(doc.GetText().IndexOf("alert", StringComparison.Ordinal)));
        Assert.Null(nav.UrlAt(doc.GetText().IndexOf("ftp", StringComparison.Ordinal)));
        Assert.NotNull(nav.UrlAt(doc.GetText().IndexOf("ok.example", StringComparison.Ordinal)));
    }

    // ---- accessibility ----

    [AvaloniaFact]
    public async Task The_editor_and_its_widgets_carry_accessible_names()
    {
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<ICompletionProvider>(DocumentSelector.All, FakeCompletionProvider.Items("alpha"));
        var (_, view, _) = await host.OpenAsync("al", "named.cs");
        Assert.Equal("Editor: named.cs", AutomationProperties.GetName(view.Editor));
        var names = new List<string?>
        {
            AutomationProperties.GetName(view.Feature<HoverFeature>()!.PopupRoot),
            AutomationProperties.GetName(view.Feature<SignatureFeature>()!.PopupRoot),
        };
        Assert.Equal(["Hover", "Parameter hints"], names);
    }
}
