using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Search;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// T20 over the REAL composition: Edit &gt; Find and Ctrl+F (through the keyboard pipeline) put the keyboard in the editor's find box at once,
/// headless included, so what is typed next (and Enter, which a macro records) goes to the box.
/// </summary>
public class FindFocusTests
{
    private static bool InFindBox(RealAppHost host) =>
        (host.Window.FocusManager?.GetFocusedElement() as Avalonia.Visual)?.FindAncestorOfType<SearchPanel>() is not null;

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Find_and_Ctrl_F_focus_the_find_box_without_a_layout_pass()
    {
        await using var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("a.txt", "alpha beta\n");
            var settings = h.Home.Combine("config", "settings.json"); // Vim (on by default) keeps Ctrl+F in Normal mode
            File.WriteAllText(settings, File.ReadAllText(settings).Replace("\"workbench.startupEditor\": \"none\"", "\"workbench.startupEditor\": \"none\",\n  \"vim.enable\": false", StringComparison.Ordinal));
        });
        await host.StartAsync([host.Files.Combine("a.txt")]);
        var tab = host.Shell.Well.EditorTabs.Single();
        Assert.True(await RealAppHost.WaitFor(() => tab.Controller is not null));
        tab.Controller!.Focus();

        await host.Get<ICommandService>().ExecuteAsync(KnownCommands.Find);
        Assert.True(InFindBox(host), "Edit > Find did not put the keyboard in the find box");

        tab.Controller.Focus();
        Assert.False(InFindBox(host));
        host.Window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        host.Window.KeyRelease(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        Dispatcher.UIThread.RunJobs(); // the bound command runs asynchronously
        Assert.True(InFindBox(host), "Ctrl+F did not put the keyboard in the find box");
        host.Window.KeyTextInput("beta");
        Assert.Equal("alpha beta\n", tab.Doc.GetText());
    }
}
