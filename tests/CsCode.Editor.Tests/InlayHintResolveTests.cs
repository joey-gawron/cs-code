using Avalonia.Headless.XUnit;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>Inlay hints' label parts, resolved lazily (lang-2: rust-analyzer's <c>inlayHint/resolve</c>), and following a part's location.</summary>
public class InlayHintResolveTests
{
    private sealed class ResolvingHints(Uri target) : IInlayHintsProvider
    {
        public int Resolves;

        public ValueTask<IReadOnlyList<InlayHint>?> ProvideInlayHintsAsync(ITextDocument document, TextRange range, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<InlayHint>?>(
            [
                new InlayHint(new TextPosition(0, 5), ": Vec<i32>", InlayHintKind.Type)
                {
                    LabelParts = [new(": "), new("Vec"), new("<i32>")],
                    CanResolve = true,
                },
            ]);

        public ValueTask<InlayHint> ResolveInlayHintAsync(InlayHint hint, CancellationToken ct)
        {
            Interlocked.Increment(ref Resolves);
            return ValueTask.FromResult(hint with
            {
                LabelParts = [new(": "), new("Vec", MarkupContent.Markdown("A contiguous growable array type"), new Location(target, TextRange.At(437, 11))), new("<i32>")],
            });
        }
    }

    [AvaloniaFact]
    public async Task A_hint_is_resolved_once_and_a_part_with_a_location_opens_it()
    {
        using var host = ViewHost.Create();
        var target = new Uri("file:///toolchain/alloc/src/vec/mod.rs");
        var provider = new ResolvingHints(target);
        host.Factory.Services.Features.Register<IInlayHintsProvider>(DocumentSelector.All, provider);
        var (_, view, _) = await host.OpenAsync("let v = vec![1, 2, 3];\n", "main.rs");
        var hints = view.Feature<InlayHintFeature>()!;
        await ViewHost.WaitFor(() => hints.Placements.Count == 1, "the hint placed");
        Assert.Equal((5, ": Vec<i32>"), hints.Placements[0]);

        var resolved = await hints.ResolveForTestAsync(0);
        await hints.ResolveForTestAsync(0); // the pointer rests on it again: no second request
        Assert.Equal(1, provider.Resolves);
        Assert.Equal(target, resolved.LabelParts![1].Location!.Uri);
        Assert.False(hints.PlacedHints[0].CanResolve);

        await hints.FollowForTestAsync(0, 1);
        await ViewHost.WaitFor(() => host.Navigation.Opened.Any(o => o.Uri == target), "the type's definition opened");
        Assert.Equal(TextRange.At(437, 11), host.Navigation.Opened.Last(o => o.Uri == target).Selection);
    }

    [AvaloniaFact]
    public async Task A_hint_keeps_its_row_the_height_of_the_text()
    {
        // AvaloniaEdit read the hint's baseline as 0 and hung it below the text: its row grew and the gutter glyphs drifted off their numbers
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<IInlayHintsProvider>(DocumentSelector.All, new ResolvingHints(new Uri("file:///x.rs")));
        var (_, view, _) = await host.OpenAsync("let v = vec![1, 2, 3];\nlet w = 4;\n", "main.rs");
        await ViewHost.WaitFor(() => view.Feature<InlayHintFeature>()!.Placements.Count == 1, "the hint placed");
        ViewHost.Pump();
        var tv = view.TextView;
        var hinted = tv.GetVisualLine(1)!;
        var plain = tv.GetVisualLine(2)!;
        Assert.Equal(plain.Height, hinted.Height, 0.5);
        Assert.Equal(tv.DefaultLineHeight, hinted.Height, 0.5);
    }
}
