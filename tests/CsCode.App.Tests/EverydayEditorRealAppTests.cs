using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using CsCode.App.QuickInput;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.View;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// The everyday editor features through the REAL composition: Alt+Z and the zoom keys through the real keyboard pipeline and keymap, View &gt; Word
/// Wrap, and the status bar's encoding, line-ending and indentation items opening their pickers (the real quick input) and following what changed.
/// </summary>
public class EverydayEditorRealAppTests
{
    private static RealAppHost Create(params (string Name, string Text)[] files) => RealAppHost.Create(h =>
    {
        var path = h.Home.Combine("config/settings.json");
        var current = File.ReadAllText(path).TrimEnd();
        File.WriteAllText(path, current[..^1].TrimEnd() + ",\n\"vim.enable\": false\n}\n");
        foreach (var (name, text) in files) h.Files.WriteFile(name, text);
    });

    private static async Task<(Docking.EditorTab Tab, CodeEditorControl View)> OpenAsync(RealAppHost host, string name)
    {
        var tab = await host.Shell.Well.OpenAsync(new Uri(host.Files.Combine(name)));
        Assert.True(await RealAppHost.WaitFor(() => tab.LiveView is CodeEditorControl), "the editor was not presented");
        var view = (CodeEditorControl)tab.LiveView!;
        tab.Controller!.Focus();
        Assert.True(await RealAppHost.WaitFor(() => tab.Controller.IsFocused), "the editor did not take the keyboard");
        return (tab, view);
    }

    private static void Press(RealAppHost host, Key key, RawInputModifiers modifiers, PhysicalKey physical)
    {
        host.Window.KeyPress(key, modifiers, physical, null);
        host.Window.KeyRelease(key, modifiers, physical, null);
    }

    private static StatusBarItemModel? Item(RealAppHost host, string id) => host.Get<IStatusBarService>().Items.FirstOrDefault(i => i.Id == id);

    /// <summary>Waits for the quick input to offer <paramref name="label"/>, highlights it and accepts it, as Enter would.</summary>
    private static async Task PickAsync(RealAppHost host, string label)
    {
        var quick = host.Get<QuickInputViewModel>();
        Assert.True(await RealAppHost.WaitFor(() => quick.IsVisible && quick.Items.Any(i => i.Label == label)), $"no '{label}' to pick: {string.Join(", ", quick.Items.Select(i => i.Label))}");
        quick.SelectedIndex = quick.Items.ToList().FindIndex(i => i.Label == label);
        quick.Accept();
        await DevScene.SettleAsync(3);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Alt_Z_toggles_word_wrap_and_the_zoom_keys_zoom_the_editor_through_the_real_keymap()
    {
        await using var host = Create(("a.txt", "some text\n"));
        await host.StartAsync();
        var (_, view) = await OpenAsync(host, "a.txt");
        var editor = view.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().First();
        var size = editor.FontSize;

        Press(host, Key.Z, RawInputModifiers.Alt, PhysicalKey.Z);
        Assert.True(await RealAppHost.WaitFor(() => editor.WordWrap), "Alt+Z did not wrap");
        Assert.True(host.Get<IContextKeyService>().Evaluate("editorWordWrap")); // View > Word Wrap's check mark
        await host.RunAsync("editor.action.toggleWordWrap"); // what View > Word Wrap runs
        Assert.False(editor.WordWrap);
        Assert.Equal("off", host.Get<IConfigurationService>().Get<string>(SettingKeys.EditorWordWrap)); // the setting was never written
        Assert.DoesNotContain("wordWrap", File.ReadAllText(host.Home.Combine("config/settings.json")), StringComparison.Ordinal);

        Press(host, Key.OemPlus, PlatformKeys.Command, PhysicalKey.Equal);
        Assert.True(await RealAppHost.WaitFor(() => editor.FontSize == size + 1), $"font {editor.FontSize}");
        Press(host, Key.OemPlus, PlatformKeys.Command | RawInputModifiers.Shift, PhysicalKey.Equal); // Ctrl+Shift+= (Ctrl++) too
        Assert.True(await RealAppHost.WaitFor(() => editor.FontSize == size + 2), $"font {editor.FontSize}");
        Press(host, Key.D0, PlatformKeys.Command, PhysicalKey.Digit0);
        Assert.True(await RealAppHost.WaitFor(() => editor.FontSize == size), $"font {editor.FontSize}");
        Press(host, Key.Subtract, PlatformKeys.Command, PhysicalKey.NumPadSubtract);
        Assert.True(await RealAppHost.WaitFor(() => editor.FontSize == size - 1), $"font {editor.FontSize}");
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task The_status_bar_items_open_their_pickers_and_follow_the_changes()
    {
        await using var host = Create(("a.txt", "one\n  two\n"));
        await host.StartAsync();
        var (tab, view) = await OpenAsync(host, "a.txt");
        var commands = host.Get<ICommandService>();
        Assert.True(await RealAppHost.WaitFor(() => Item(host, "editor.encoding")?.Text == "UTF-8"), Item(host, "editor.encoding")?.Text);
        foreach (var (id, command) in new[] { ("editor.indent", "changeEditorIndentation"), ("editor.encoding", "workbench.action.editor.changeEncoding"), ("editor.eol", "workbench.action.editor.changeEOL") })
        {
            Assert.Equal(command, Item(host, id)!.CommandId); // a click runs it
            Assert.True(commands.Has(command));
        }

        // EOL: LF -> CRLF, one undo step, the item follows
        var running = commands.ExecuteAsync("workbench.action.editor.changeEOL").AsTask();
        await PickAsync(host, "CRLF");
        await running;
        Assert.True(await RealAppHost.WaitFor(() => Item(host, "editor.eol")?.Text == "CRLF"), Item(host, "editor.eol")?.Text);
        Assert.Equal("one\r\n  two\r\n", tab.Doc.GetText());

        // indentation: the picker, then Indent Using Tabs with a tab size of 2
        running = commands.ExecuteAsync("changeEditorIndentation").AsTask();
        await PickAsync(host, "Indent Using Tabs");
        await PickAsync(host, "2");
        await running;
        Assert.True(await RealAppHost.WaitFor(() => Item(host, "editor.indent")?.Text == "Tab Size: 2"), Item(host, "editor.indent")?.Text);
        Assert.Equal(new IndentationOptions(2, false), ((EditorDocument)tab.Doc).IndentationOverride);
        Assert.True(await RealAppHost.WaitFor(() => !view.Modal.InsertSpaces && view.Modal.TabSize == 2));

        // encoding: Save with Encoding, UTF-16 LE
        running = commands.ExecuteAsync("workbench.action.editor.changeEncoding").AsTask();
        await PickAsync(host, "Save with Encoding");
        await PickAsync(host, "UTF-16 LE");
        await running;
        Assert.True(await RealAppHost.WaitFor(() => Item(host, "editor.encoding")?.Text == "UTF-16 LE"), Item(host, "editor.encoding")?.Text);
        var bytes = File.ReadAllBytes(host.Files.Combine("a.txt"));
        Assert.Equal(new byte[] { 0xFF, 0xFE, (byte)'o', 0 }, bytes[..4]);
        Assert.False(tab.Doc.IsDirty);
    }
}
