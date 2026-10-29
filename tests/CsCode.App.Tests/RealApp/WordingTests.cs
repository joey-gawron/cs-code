using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CsCode.App.QuickInput;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.ToolWindows.ErrorList;
using CsCode.App.Views;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>What the user reads in the REAL composition (<see cref="RealAppHost"/>): palette titles, the Quick Launch box, Error List counts.</summary>
public class RealAppWordingTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_palette_shows_titles_never_raw_command_ids()
    {
        await using var host = RealAppHost.Create(h => h.Files.WriteFile("a.txt", "x\n"));
        await host.StartAsync([host.Files.Combine("a.txt")]);
        var palette = await host.OpenPaletteAsync(KnownCommands.CommandPalette);
        var quick = host.Get<QuickInputViewModel>();
        Assert.True(await RealAppHost.WaitFor(() => quick.Items.Count > 50));
        var ids = host.Get<ICommandService>().All.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var raw = quick.Items.Select(e => e.Label).Where(l => ids.Contains(l) || System.Text.RegularExpressions.Regex.IsMatch(l, @"^[\w-]+(\.[\w-]+)+$")).ToArray();
        Assert.True(raw.Length == 0, "raw ids in the palette: " + string.Join(", ", raw));
        // VS Code's categories for the panels' commands.
        Assert.Contains(quick.Items, e => e.Label == "Search: Find in Files");
        Assert.Contains(quick.Items, e => e.Label == "Preferences: Open Keyboard Shortcuts");
        await host.ClosePaletteAsync(palette);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_Quick_Launch_box_reads_Search_Ctrl_Q_and_the_workspace_name_stands_beside_it()
    {
        await using var host = RealAppHost.Create(h => h.Files.WriteFile("src/Program.cs", "class P {}\n"));
        await host.StartAsync([host.Files.Path]);
        var bar = host.Window.GetVisualDescendants().OfType<TitleBarView>().Single();
        var folder = Path.GetFileName(host.Files.Path.TrimEnd('/'));
        Assert.True(await RealAppHost.WaitFor(() => bar.WorkspaceName == folder), bar.WorkspaceName);
        Assert.Equal("Search (Ctrl+Q)", bar.QuickLaunchText); // it used to show the folder name instead
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Error_List_counts_are_worded_in_the_singular_and_the_plural()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        host.Get<IDiagnosticService>().Set(new Uri(host.Files.Combine("x.cs")), "test",
        [
            new Diagnostic(TextRange.At(0, 0), "one error", DiagnosticSeverity.Error),
            new Diagnostic(TextRange.At(1, 0), "a message", DiagnosticSeverity.Information),
        ]);
        var view = host.Shell.Tools.Get("errorlist")!.EnsureView(); // visible by default
        string[] Chips() => view.GetVisualDescendants().OfType<ToggleButton>()
            .SelectMany(b => b.GetVisualDescendants().OfType<TextBlock>()).Select(t => t.Text ?? "").Where(t => t.Length > 0 && char.IsDigit(t[0])).ToArray();
        Assert.True(await RealAppHost.WaitFor(() => Chips().Contains("1 Error")), string.Join(" | ", Chips())); // was "1 Errors"
        Assert.Contains("0 Warnings", Chips());
        Assert.Contains("1 Message", Chips());
        Assert.Equal("2 Messages", ErrorListView.CountText(2, "Message"));
    }
}
