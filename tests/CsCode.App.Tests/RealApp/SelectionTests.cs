using Avalonia.Headless.XUnit;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Search;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary><see cref="IActiveSelectionProvider"/> over the REAL composition (<see cref="RealAppHost"/>): Dap's <c>${selectedText}</c> and the Find in Files seed.</summary>
public class RealAppSelectionTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_active_editors_selection_is_the_selected_text_for_debugging_and_seeds_Find_in_Files()
    {
        await using var host = await RealAppHost.Create(h => h.Files.WriteFile("a.txt", "var answer = compute(42);\nsecond line\n")).StartAsync();
        var selection = host.Get<IActiveSelectionProvider>();
        Assert.Null(selection.SelectedText);

        await host.Get<INavigationService>().OpenAsync(new Uri(host.Files.Combine("a.txt")), TextRange.FromLines(0, 4, 0, 20));
        await DevScene.SettleAsync(20);
        // Dap reads ${selectedText} on its own lane: the provider must answer off the UI thread.
        Assert.Equal("answer = compute", await Task.Run(() => selection.SelectedText));

        await host.RunAsync(SearchPanelsHost.FindInFiles);
        Assert.Equal("answer = compute", host.Get<SearchPanelsHost>().LiveFind?.Query); // not just the word at the caret

        host.Shell.Well.ActiveEditor!.Controller!.SetSelection(TextRange.At(1, 3), reveal: false);
        Assert.Null(selection.SelectedText); // an empty selection is no selected text
        await host.Shell.Well.CloseAllAsync();
        Assert.Null(selection.SelectedText);
    }
}
