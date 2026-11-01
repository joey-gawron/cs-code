using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using CsCode.App.Docking;
using CsCode.App.Input;
using CsCode.App.Markdown;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Editor.Markdown.Preview;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// The Markdown preview in the REAL app (docs/MARKDOWN.md): VS Code's commands and keys (Ctrl+Shift+V / Cmd+Shift+V, Ctrl+K V / Cmd+K V) over
/// the real keymap, one preview tab per file, the preview-to-the-side group and focus, the context key and Show Source, scroll sync with the
/// editor both ways, links into the editor, the clipboard, and release of the preview when its tab closes.
/// </summary>
public class MarkdownPreviewTests
{
    private static string Long(int sections) =>
        string.Concat(Enumerable.Range(0, sections).Select(i => $"## Section {i}\n\nText of section {i}, long enough to wrap maybe.\n\n"));

    private static async Task<RealAppHost> StartAsync(string text, string name = "README.md")
    {
        var host = RealAppHost.Create(h =>
        {
            h.Home.WriteFile("config/settings.json", "{ \"" + MarkdownPreviewSettings.UpdateDelay + "\": 10 }");
            h.Files.WriteFile(name, text);
            h.Files.WriteFile("notes.txt", "plain text\n");
            h.Files.WriteFile("docs/other.md", "# Other\n\nline 2\nline 3\nline 4\n");
        });
        await host.StartAsync([host.Files.Combine(name)]);
        await RealAppHost.WaitFor(() => host.Shell.Well.ActiveEditor is { Controller: not null });
        await DevScene.SettleAsync(10);
        return host;
    }

    private static KeyDispatch Press(RealAppHost host, string linux, string mac)
    {
        var dispatch = KeyDispatch.PassThrough;
        foreach (var stroke in PlatformKeys.Stroke(linux, mac).Split(' ')) dispatch = host.Shell.Keyboard.Handle(KeyStrokeParser.ParseStroke(stroke));
        return dispatch;
    }

    private static MarkdownPreviewHost? PreviewHost(ShellDocument? tab) => tab?.LiveView as MarkdownPreviewHost;

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Open_Preview_key_opens_one_preview_tab_per_file_sets_the_context_key_and_the_same_key_shows_the_source()
    {
        await using var host = await StartAsync("# Hello\n\nworld\n");
        var well = host.Shell.Well;
        var keys = host.Get<IContextKeyService>();
        var editor = well.ActiveEditor!;

        Assert.Equal(KeyDispatch.Handled, Press(host, "ctrl+shift+v", "cmd+shift+v"));
        Assert.True(await RealAppHost.WaitFor(() => MarkdownPreviewIntegration.IsPreviewTab(well.Active)), "no preview tab");
        var preview = well.Active!;
        Assert.Equal("Preview README.md", preview.Title);
        Assert.Equal(editor.Uri, MarkdownPreviewIntegration.SourceOf(preview));
        Assert.Equal(true, keys.Get(MarkdownPreviewIntegration.PreviewFocusKey));
        Assert.True(await RealAppHost.WaitFor(() => PreviewHost(preview)?.View.Document.Blocks.Count == 2), "the preview did not render");

        // From the preview, the same key goes back to the source (markdown.showSource)
        Assert.Equal(KeyDispatch.Handled, Press(host, "ctrl+shift+v", "cmd+shift+v"));
        Assert.True(await RealAppHost.WaitFor(() => ReferenceEquals(well.Active, editor)), "Show Source did not activate the editor");
        Assert.Equal(false, keys.Get(MarkdownPreviewIntegration.PreviewFocusKey));

        // Opening the preview again activates the existing tab
        await host.RunAsync(MarkdownPreviewIntegration.ShowPreview);
        Assert.Same(preview, well.Active);
        Assert.Single(well.Tabs, MarkdownPreviewIntegration.IsPreviewTab);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Open_Preview_to_the_Side_puts_the_preview_in_a_group_on_the_right_and_keeps_the_editor_active()
    {
        await using var host = await StartAsync("# Side\n\ntext\n");
        var well = host.Shell.Well;
        var factory = host.Get<ShellDockFactory>();
        var editor = well.ActiveEditor!;

        Assert.Equal(KeyDispatch.Handled, Press(host, "ctrl+k v", "cmd+k v"));
        Assert.True(await RealAppHost.WaitFor(() => factory.DocumentGroups().Count == 2), "no second group");
        var preview = well.Tabs.Single(MarkdownPreviewIntegration.IsPreviewTab);
        Assert.NotSame(factory.GroupOf(editor), factory.GroupOf(preview));
        Assert.Same(editor, well.Active);
        Assert.True(await RealAppHost.WaitFor(() => editor.Controller?.IsFocused == true), "the editor lost the keyboard");
        Assert.True(await RealAppHost.WaitFor(() => PreviewHost(preview)?.View.Document.Blocks.Count == 2), "the side preview did not render");
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_preview_commands_do_nothing_for_a_file_that_is_not_Markdown()
    {
        await using var host = await StartAsync("# md\n");
        var well = host.Shell.Well;
        await host.Get<INavigationService>().OpenAsync(new Uri(host.Files.Combine("notes.txt")));
        await RealAppHost.WaitFor(() => well.ActiveEditor?.Title == "notes.txt");
        Press(host, "ctrl+shift+v", "cmd+shift+v"); // outside Markdown the key keeps its other meaning (Visual Studio's Cycle Clipboard Ring)
        await host.RunAsync(MarkdownPreviewIntegration.ShowPreview);
        Assert.DoesNotContain(well.Tabs, MarkdownPreviewIntegration.IsPreviewTab);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Editor_and_preview_scroll_together_by_source_line_without_feeding_back()
    {
        await using var host = await StartAsync(Long(400));
        var well = host.Shell.Well;
        var editor = well.ActiveEditor!;
        await host.RunAsync(MarkdownPreviewIntegration.ShowPreviewToSide);
        var preview = PreviewHost(well.Tabs.Single(MarkdownPreviewIntegration.IsPreviewTab))!;
        Assert.True(await RealAppHost.WaitFor(() => preview.View.Document.Blocks.Count == 800 && preview.Editor is not null), "preview not coupled");

        // editor → preview: section 100's heading is line 400
        editor.Controller!.ScrollToLine(400);
        Assert.True(await RealAppHost.WaitFor(() => Math.Abs(preview.View.TopLine - 400) < 1.5, 5000), $"preview at line {preview.View.TopLine}");
        await DevScene.SettleAsync(10);
        Assert.InRange(editor.Controller.TopLine, 399.5, 400.5); // the preview's move did not scroll the editor back

        // preview → editor (the user scrolls the preview)
        var scroller = preview.View.Scroller;
        scroller.Offset = new Avalonia.Vector(0, scroller.Offset.Y + 2000);
        Assert.True(await RealAppHost.WaitFor(() => editor.Controller.TopLine > 420, 5000), $"editor at line {editor.Controller.TopLine}");
        await DevScene.SettleAsync(10);
        Assert.InRange(Math.Abs(editor.Controller.TopLine - preview.View.TopLine), 0, 1.5);
        var settled = (editor.Controller.TopLine, preview.View.TopLine);
        await DevScene.SettleAsync(20);
        Assert.Equal(settled.Item1, editor.Controller.TopLine, 3); // nothing keeps moving: no feedback loop
        Assert.Equal(settled.Item2, preview.View.TopLine, 3);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task A_relative_link_opens_the_file_in_the_editor_at_a_GitHub_line_fragment()
    {
        await using var host = await StartAsync("See [other](docs/other.md#L3).\n");
        var well = host.Shell.Well;
        await host.RunAsync(MarkdownPreviewIntegration.ShowPreview);
        var preview = PreviewHost(well.Active)!;
        Assert.True(await RealAppHost.WaitFor(() => preview.View.Document.Blocks.Count == 1));

        await preview.OpenLinkAsync("docs/other.md#L3");
        Assert.True(await RealAppHost.WaitFor(() => well.ActiveEditor?.Title == "other.md"), "the link did not open other.md");
        Assert.True(await RealAppHost.WaitFor(() => well.ActiveEditor!.Controller?.Caret.Line == 2), "not at line 3");
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void Local_links_resolve_against_the_previewed_file()
    {
        var source = new Uri("file:///work/docs/README.md");
        Assert.Equal((new Uri("file:///work/docs/guide/a.md"), "part"), MarkdownPreviewHost.ResolveLocal(source, "guide/a.md#part"));
        Assert.Equal((new Uri("file:///work/other.md"), null), MarkdownPreviewHost.ResolveLocal(source, "../other.md"));
        Assert.Equal((source, "top"), MarkdownPreviewHost.ResolveLocal(source, "#top"));
        Assert.Equal((new Uri("file:///work/docs/my%20file.md"), null), MarkdownPreviewHost.ResolveLocal(source, "my file.md?x=1"));
        var remote = new Uri("ssh://box/home/me/README.md");
        Assert.Equal((new Uri("ssh://box/home/me/src/x.md"), null), MarkdownPreviewHost.ResolveLocal(remote, "src/x.md"));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Edit_Copy_copies_the_text_selected_in_the_preview()
    {
        await using var host = await StartAsync("Copy **this** text\n");
        var well = host.Shell.Well;
        await host.RunAsync(MarkdownPreviewIntegration.ShowPreview);
        var preview = PreviewHost(well.Active)!;
        Assert.True(await RealAppHost.WaitFor(() => preview.View.Blocks.RealizedControl(0) is SelectableTextBlock));
        var text = (SelectableTextBlock)preview.View.Blocks.RealizedControl(0)!;
        text.Focus();
        text.SelectAll();
        Assert.True(preview.CanCopy);
        await host.RunAsync("editor.action.clipboardCopyAction");
        var clipboard = host.Get<IClipboardService>();
        var copied = await clipboard.ReadAsync();
        Assert.Equal("Copy this text", copied?.Text);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Closing_the_preview_tab_releases_the_preview()
    {
        await using var host = await StartAsync("# Bye\n\n```csharp\nclass C { }\n```\n");
        var well = host.Shell.Well;
        var weak = await OpenAndCloseAsync(host, well);
        for (var round = 0; round < 10 && weak.TryGetTarget(out _); round++)
        {
            await DevScene.SettleAsync(5);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        // (it used to stay reachable through KeyboardNavigation.TabOnceActiveElement of a Dock container: the preview had the keyboard)
        Assert.False(weak.TryGetTarget(out _), "the closed preview is still reachable");
        Assert.Equal(false, host.Get<IContextKeyService>().Get(MarkdownPreviewIntegration.PreviewFocusKey));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<MarkdownPreviewView>> OpenAndCloseAsync(RealAppHost host, DocumentWell well)
    {
        await host.RunAsync(MarkdownPreviewIntegration.ShowPreview);
        var tab = well.Active!;
        var preview = PreviewHost(tab)!;
        Assert.True(await RealAppHost.WaitFor(() => preview.View.Document.Blocks.Count == 2));
        var weak = new WeakReference<MarkdownPreviewView>(preview.View);
        await well.CloseAsync(tab);
        await DevScene.SettleAsync(5);
        return weak;
    }
}
