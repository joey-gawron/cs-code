using Avalonia.Headless.XUnit;
using CsCode.App.Services;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// The status bar's Ln/Col and <see cref="INavigationService.ActiveCaret"/> over the REAL composition (<see cref="RealAppHost"/>: every real module,
/// real editor views, <c>CSCODE_HOME</c> in a temp directory).
/// </summary>
public class RealAppCaretTests
{
    private static string? Position(RealAppHost host) => host.Get<StatusBarService>().Items.FirstOrDefault(i => i.Id == "editor.position")?.Text;

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Ln_Col_follows_the_caret_after_command_line_navigation_tab_switches_and_programmatic_moves()
    {
        await using var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("a.txt", "first\nsecond line\nthird\n");
            h.Files.WriteFile("b.txt", "1\n2\n3\n4\n5\n");
        });
        await host.StartAsync([host.Files.Combine("a.txt") + ":2:3"]);
        var nav = host.Get<INavigationService>();
        Assert.True(await RealAppHost.WaitFor(() => Position(host) == "Ln 2, Col 3"), Position(host)); // was "Ln 1, Col 1" until the caret moved
        Assert.Equal(new TextPosition(1, 2), nav.ActiveCaret);

        await nav.OpenAsync(new Uri(host.Files.Combine("b.txt")), TextRange.At(4, 0));
        Assert.True(await RealAppHost.WaitFor(() => Position(host) == "Ln 5, Col 1"), Position(host));

        var a = host.Shell.Well.EditorTabs.Single(t => t.Title == "a.txt");
        host.Shell.Well.Activate(a);
        Assert.True(await RealAppHost.WaitFor(() => Position(host) == "Ln 2, Col 3"), Position(host));

        a.Controller!.SetSelection(TextRange.At(2, 4), reveal: true);
        Assert.True(await RealAppHost.WaitFor(() => Position(host) == "Ln 3, Col 5"), Position(host));
        Assert.Equal(new TextPosition(2, 4), nav.ActiveCaret);
    }
}
