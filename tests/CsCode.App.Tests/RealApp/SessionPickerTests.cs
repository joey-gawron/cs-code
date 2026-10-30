using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CsCode.App.Background;
using CsCode.App.Menus;
using CsCode.App.QuickInput;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Views;
using CsCode.Core.Platform;
using CsCode.Sessions;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Saved sessions (docs/SESSIONS.md) on the Welcome page and in Quick Launch's <c>session:</c> mode, over the REAL composition. The sessions are real
/// session files in the test's <c>CSCODE_HOME</c>; File ▸ Sessions ▸ Open Session (registered by session processes only) is stood in for by a
/// recording command, so what the page and the palette ask for is checked here, and opening is the session process's own (BackgroundTests).
/// </summary>
public class SessionPickerTests
{
    private static RealAppHost Seed() => RealAppHost.Create(h =>
    {
        var store = new SessionStore(h.Home.Combine("data"));
        void Save(string name, string folder, int daysAgo) => store.Save(new SessionDocument
        {
            Name = name, LastUsedUtc = DateTimeOffset.UtcNow.AddDays(-daysAgo), Folders = [new Uri(h.Files.Combine(folder) + "/").ToString()],
        });
        Directory.CreateDirectory(h.Files.Combine("work"));
        Save("work", "work", 1);
        Save("oss", "oss-project", 3);
        Save("current", "here", 0);
    });

    private static List<object?> RegisterOpenSession(RealAppHost host)
    {
        var opened = new List<object?>();
        host.Get<ICommandService>().Register(SessionCommandIds.OpenSession, inv => { opened.Add(inv.Args.Count > 0 ? inv.Args[0] : null); return ValueTask.FromResult<object?>(null); },
            new CommandMetadata(SessionCommandIds.OpenSession, "Open Session...", "Session"));
        return opened;
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_Welcome_page_lists_the_other_saved_sessions_most_recent_first_and_opens_one()
    {
        await using var host = await Seed().StartAsync();
        Assert.Equal(host.Home.Combine("data"), host.Get<IAppPaths>().UserDataDir); // the store the session processes use
        var opened = RegisterOpenSession(host);
        host.Window.SessionName = "current"; // this window's own session is not offered
        await host.RunAsync(ShellMenus.Welcome);
        var welcome = host.Window.GetVisualDescendants().OfType<WelcomeView>().Single();
        await welcome.SessionsLoaded;
        await DevScene.SettleAsync(5);
        var texts = welcome.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        var at = texts.IndexOf("Sessions");
        Assert.True(at >= 0, string.Join(" | ", texts));
        Assert.Equal(["work", "oss"], texts.Skip(at + 1).Where(t => t is "work" or "oss" or "current").ToList());
        Assert.Contains(texts, t => t is not null && t.Contains(host.Files.Combine("work"), StringComparison.Ordinal) && t.Contains("last used", StringComparison.Ordinal));

        var link = welcome.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "oss");
        var row = (Control)link.GetVisualParent()!;
        row.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(row, new Avalonia.Input.Pointer(1, Avalonia.Input.PointerType.Mouse, true), host.Window,
            default, 0, new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed),
            Avalonia.Input.KeyModifiers.None));
        Assert.True(await RealAppHost.WaitFor(() => opened.Count == 1));
        Assert.Equal("oss", opened[0]);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Quick_Launch_session_mode_lists_filters_and_opens_saved_sessions()
    {
        await using var host = await Seed().StartAsync();
        var opened = RegisterOpenSession(host);
        host.Window.SessionName = "current";
        var quick = host.Get<QuickInputViewModel>();
        var running = await host.OpenPaletteAsync(KnownCommands.QuickOpen);
        quick.Text = "session:";
        Assert.True(await RealAppHost.WaitFor(() => quick.Items.Count == 2), string.Join(", ", quick.Items.Select(i => i.Label)));
        Assert.Equal(["work", "oss"], quick.Items.Select(i => i.Label));
        Assert.Contains(host.Files.Combine("work"), quick.Items[0].Description);

        quick.Text = "session: os";
        Assert.True(await RealAppHost.WaitFor(() => quick.Items.Count == 1 && quick.Items[0].Label == "oss"));
        quick.SelectedIndex = 0;
        quick.Accept();
        await running;
        Assert.True(await RealAppHost.WaitFor(() => opened.Count == 1));
        Assert.Equal("oss", opened[0]);

        // '?' lists the mode.
        running = await host.OpenPaletteAsync(KnownCommands.QuickOpen);
        quick.Text = "?";
        Assert.True(await RealAppHost.WaitFor(() => quick.Items.Any(i => i.Label == "session:")));
        await host.ClosePaletteAsync(running);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task A_window_without_background_mode_offers_no_sessions_and_says_why()
    {
        await using var host = await Seed().StartAsync(); // no Open Session command: a --foreground window
        await host.RunAsync(ShellMenus.Welcome);
        var welcome = host.Window.GetVisualDescendants().OfType<WelcomeView>().Single();
        await welcome.SessionsLoaded;
        Assert.DoesNotContain(welcome.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Sessions");

        var quick = host.Get<QuickInputViewModel>();
        var running = await host.OpenPaletteAsync(KnownCommands.QuickOpen);
        quick.Text = "session:";
        Assert.True(await RealAppHost.WaitFor(() => quick.Items.Count == 1));
        Assert.Contains("background mode", quick.Items[0].Label);
        await host.ClosePaletteAsync(running);
    }
}
