using Avalonia.Headless.XUnit;
using CsCode.Core.Text;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.TextMate;
using CsCode.Editor.TreeSitter;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// T10, the TextMate livelock fixed in 10c2a72: after an edit inside the viewport, a slice restarted from the viewport's top every time, so
/// when re-tokenising from there to the edit took longer than one slice (a loaded machine, a slow grammar) the viewport never caught up.
/// The fix resumes at the first viewport line that still needs work and always gets one line past it. Reproduced deterministically with a
/// tiny slice budget (every slice "runs out" after its first line); serial, because the budget is process-wide.
/// </summary>
[Collection(TreeSitterBenchCollection.Name)]
public class TextMateLivelockTests
{
    [AvaloniaFact]
    public async Task After_an_edit_inside_the_viewport_tokenisation_catches_up_even_when_every_slice_runs_out_at_once()
    {
        var previous = TokenizationSession.SliceBudget;
        try
        {
            using var host = ViewHost.Create();
            host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TextMate);
            var text = string.Concat(Enumerable.Range(0, 400).Select(i => $"int Value{i} = {i}; // line {i}\n"));
            var (_, view, _) = await host.OpenAsync(text, "Lines.cs", 900, 400);
            view.Controller.Reveal(TextRange.At(150, 0), center: true);
            ViewHost.Pump();
            var (first, last) = view.Tokens!.Viewport;
            Assert.InRange(150, first, last);
            await ViewHost.WaitFor(() => view.Tokens.Store.ValidLines > last, "the viewport tokenised");

            TokenizationSession.SliceBudget = TimeSpan.FromTicks(1); // a machine so loaded that no slice gets past its first line in time
            view.Controller.SetSelection(TextRange.At(150, 0));
            Type(view, "/* x */ ");
            await ViewHost.WaitFor(() => view.Tokens.Store.ValidLines > last, $"the viewport re-tokenised after the edit (valid {view.Tokens.Store.ValidLines}, viewport {first}-{last})");
            Assert.Contains(view.Tokens.Interner.Scopes(view.Tokens.Store.GetTokens(150)![1]), s => s.StartsWith("comment", StringComparison.Ordinal));
        }
        finally
        {
            TokenizationSession.SliceBudget = previous;
        }
    }
}
