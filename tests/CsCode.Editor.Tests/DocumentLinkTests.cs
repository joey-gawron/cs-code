using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests;

/// <summary>Document links (stage 3: clangd's #include targets): underlined under Ctrl/⌘, opened on Ctrl/⌘+Click, resolved when needed.</summary>
public sealed class DocumentLinkTests
{
    private sealed class Links(Uri? target, bool resolvable) : IDocumentLinkProvider
    {
        public int Requests;
        public int Resolves;

        public ValueTask<IReadOnlyList<DocumentLink>?> ProvideDocumentLinksAsync(ITextDocument document, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var text = document.GetText();
            var start = text.IndexOf("\"mathlib.h\"", StringComparison.Ordinal);
            var range = new TextRange(document.PositionAt(start), document.PositionAt(start + "\"mathlib.h\"".Length));
            return ValueTask.FromResult<IReadOnlyList<DocumentLink>?>([new DocumentLink(range, target)]);
        }

        public ValueTask<DocumentLink> ResolveDocumentLinkAsync(DocumentLink link, CancellationToken ct)
        {
            Interlocked.Increment(ref Resolves);
            return ValueTask.FromResult(resolvable ? link with { Target = new Uri("file:///src/include/mathlib.h#L3") } : link);
        }
    }

    private const string Source = "#include <stdio.h>\n#include \"mathlib.h\"\nint main(void) { return 0; }\n";

    [AvaloniaFact]
    public async Task Ctrl_click_on_an_include_opens_its_header()
    {
        using var host = ViewHost.Create();
        var links = new Links(new Uri("file:///src/include/mathlib.h"), resolvable: false);
        host.Factory.Services.Features.Register<IDocumentLinkProvider>(DocumentSelector.All, links);
        var (doc, view, window) = await host.OpenAsync(Source, "main.c");
        ViewHost.Pump();
        var offset = doc.GetText().IndexOf("mathlib", StringComparison.Ordinal);
        var point = PointOf(window, view, offset);
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        window.MouseMove(point, command); // the first hover with the modifier asks for the links
        await ViewHost.WaitFor(() => view.Editor.Cursor is not null, "hand cursor over the include while the modifier is held");
        window.MouseDown(point, MouseButton.Left, command);
        window.MouseUp(point, MouseButton.Left, command);

        await ViewHost.WaitFor(() => host.Navigation.Opened.Count == 1, "header opened");
        Assert.Equal(new Uri("file:///src/include/mathlib.h"), host.Navigation.Opened[0].Uri);
        Assert.Equal(1, links.Requests); // fetched once for this version
        Assert.Equal(0, links.Resolves); // the target was there already
    }

    [AvaloniaFact]
    public async Task A_link_without_a_target_is_resolved_on_click_and_a_line_fragment_is_honoured()
    {
        using var host = ViewHost.Create();
        var links = new Links(null, resolvable: true);
        host.Factory.Services.Features.Register<IDocumentLinkProvider>(DocumentSelector.All, links);
        var (doc, view, window) = await host.OpenAsync(Source, "main.c");
        ViewHost.Pump();
        var point = PointOf(window, view, doc.GetText().IndexOf("mathlib", StringComparison.Ordinal));
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        window.MouseMove(point, command);
        await ViewHost.WaitFor(() => view.Editor.Cursor is not null, "hand cursor");
        window.MouseDown(point, MouseButton.Left, command);
        window.MouseUp(point, MouseButton.Left, command);

        await ViewHost.WaitFor(() => host.Navigation.Opened.Count == 1, "header opened");
        Assert.Equal(1, links.Resolves);
        Assert.Equal(new Uri("file:///src/include/mathlib.h"), host.Navigation.Opened[0].Uri);
        Assert.Equal(2, host.Navigation.Opened[0].Selection?.Start.Line);
    }

    [AvaloniaFact]
    public async Task A_plain_hover_neither_fetches_nor_underlines()
    {
        using var host = ViewHost.Create();
        var links = new Links(new Uri("file:///x.h"), resolvable: false);
        host.Factory.Services.Features.Register<IDocumentLinkProvider>(DocumentSelector.All, links);
        var (doc, view, window) = await host.OpenAsync(Source, "main.c");
        ViewHost.Pump();
        window.MouseMove(PointOf(window, view, doc.GetText().IndexOf("mathlib", StringComparison.Ordinal)));
        ViewHost.Pump();
        Assert.Equal(0, links.Requests);
        Assert.Null(view.Editor.Cursor);
    }

    [Fact]
    public void Line_fragments_follow_vs_code()
    {
        Assert.Equal((11, 0), NavigationFeature.LineOfFragment("#L12"));
        Assert.Equal((11, 4), NavigationFeature.LineOfFragment("#12,5"));
        Assert.Equal((0, 0), NavigationFeature.LineOfFragment(""));
        Assert.Equal((0, 0), NavigationFeature.LineOfFragment("#section"));
    }
}
