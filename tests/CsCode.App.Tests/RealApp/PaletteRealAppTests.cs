using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CsCode.App.QuickInput;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Views;
using CsCode.Core.Platform;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>The command palette over the REAL composition (every module's commands): every command is listed, and the list stays virtualised.</summary>
public class PaletteRealAppTests(ITestOutputHelper output)
{
    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_palette_lists_every_command_and_its_list_realises_only_the_visible_rows()
    {
        await using var host = RealAppHost.Create(h => h.Files.WriteFile("a.txt", "x\n"));
        await host.StartAsync([host.Files.Combine("a.txt")]);
        var commands = host.Get<ICommandService>();
        var keys = host.Get<IContextKeyService>();
        var expected = commands.All.Count(c => !string.IsNullOrEmpty(c.Title) && keys.Evaluate(c.Enablement));
        var sw = Stopwatch.StartNew();
        var palette = await host.OpenPaletteAsync(KnownCommands.CommandPalette);
        var quick = host.Get<QuickInputViewModel>();
        Assert.True(await RealAppHost.WaitFor(() => quick.Items.Count == expected), $"{quick.Items.Count} of {expected} commands listed");
        output.WriteLine($"{expected} commands listed ({commands.All.Count} registered), palette open in {sw.ElapsedMilliseconds} ms");
        Assert.True(expected > PaletteSource.MaxResults, $"only {expected} commands: the old 100-row cap would not show");
        // The ones that fell past the old cap.
        Assert.Contains(quick.Items, e => e.Label == "Search: Find in Files");
        Assert.Contains(quick.Items, e => e.Label == "Preferences: Open Keyboard Shortcuts");

        var view = host.Window.GetVisualDescendants().OfType<QuickInputView>().Single();
        var list = view.GetVisualDescendants().OfType<ListBox>().Single();
        await DevScene.SettleAsync(5);
        var realized = list.GetRealizedContainers().Count();
        output.WriteLine($"realized rows: {realized} of {quick.Items.Count}");
        Assert.InRange(realized, 1, 40); // a few hundred rows, a screenful of containers

        // The last command can be reached (End of the list) and is then realised, still with few containers.
        quick.SelectedIndex = quick.Items.Count - 1;
        await DevScene.SettleAsync(10);
        Assert.Contains(list.GetRealizedContainers().OfType<ListBoxItem>(), c => ReferenceEquals(c.DataContext, quick.Items[^1]));
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 40);
        await host.ClosePaletteAsync(palette);
    }
}
