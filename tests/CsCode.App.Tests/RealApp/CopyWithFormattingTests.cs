using Avalonia.Headless.XUnit;
using CsCode.App.Input;
using CsCode.App.Menus;
using CsCode.App.Services;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Ui.Menus;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Copy with Formatting over the REAL composition: Edit &gt; Copy with Formatting and the editor's right-click item put the text and an HTML
/// rendition with the theme's syntax colours on the system clipboard (<c>text/html</c> next to the text, through the shell's clipboard service);
/// the clipboard ring keeps only the text.
/// </summary>
public class RealAppCopyWithFormattingTests
{
    private sealed class RecordingPlatformClipboard : IPlatformClipboard
    {
        public (string Text, string? Html)? Last { get; private set; }
        public Task<string?> TryGetTextAsync() => Task.FromResult(Last?.Text);
        public Task<bool> HasTextAsync() => Task.FromResult(Last is not null);
        public Task SetAsync(string text, string? html) { Last = (text, html); return Task.CompletedTask; }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Edit_Copy_with_Formatting_and_the_editor_menu_item_write_text_and_html_with_the_theme_colours()
    {
        var platform = new RecordingPlatformClipboard();
        await using var host = RealAppHost.Create(h => h.Files.WriteFile("Program.cs", "using System;\n\nclass Program\n{\n    // hello\n}\n"));
        await host.StartAsync([host.Files.Combine("Program.cs")], services =>
            services.AddSingleton(sp => new ClipboardService(sp.GetRequiredService<Ui.Threading.IUiDispatcher>(), () => platform, () => new InMemoryPrimarySelection())));
        var tab = host.Shell.Well.EditorTabs.Single();
        Assert.True(await RealAppHost.WaitFor(() => tab.Controller is not null));
        tab.Controller!.Focus();
        tab.Controller.SetSelection(TextRange.FromLines(0, 0, 4, 12), reveal: false);
        await DevScene.SettleAsync(20); // tokens published

        // The Edit menu item: enabled while an editor that can copy has focus.
        host.Shell.Menus.Refresh();
        var edit = host.Shell.Menus.Roots.Single(r => r.Header == "_Edit");
        var item = edit.Children.Single(c => c.Header == "Copy wit_h Formatting");
        Assert.True(item.IsEnabled);
        Assert.Equal(true, host.Get<IContextKeyService>().Get(ClipboardRouter.EditorCopyKey));
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)item.Command!).ExecuteAsync(null);
        Assert.True(await RealAppHost.WaitFor(() => platform.Last?.Html is not null), "no HTML reached the clipboard");
        Assert.Equal(tab.Doc.GetText(TextRange.FromLines(0, 0, 4, 12)), platform.Last!.Value.Text);
        var html = platform.Last.Value.Html!;
        Assert.Contains("background-color: #1e1e1e;", html); // Visual Studio Dark's editor background
        Assert.Contains("<span style=\"color: #569cd6;\">using</span>", html); // its keyword colour
        Assert.Contains("<span style=\"color: #57a64a;\">// hello</span>", html); // and its comment colour
        Assert.Null(host.Get<IClipboardService>().Ring[0].Html); // the ring pastes text

        // The editor's right-click menu has the item too, and it does the same.
        var menu = host.Shell.ContextMenus.OpenEditorMenu(tab, tab.LiveView!)!;
        var entry = ContextMenus.ItemsOf(menu).Select(ContextMenus.EntryOf).Single(e => e?.AccessibleName == "Copy with Formatting")!;
        menu.Close();
        await platform.SetAsync("", null);
        tab.Controller.SetSelection(TextRange.FromLines(2, 0, 2, 13), reveal: false);
        await entry.Run!();
        Assert.True(await RealAppHost.WaitFor(() => platform.Last?.Html is not null));
        Assert.Equal("class Program", platform.Last!.Value.Text);
    }
}
