using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CsCode.App.Docking;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.View;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Multi-cursor on the REAL composition (keymap presets, keyboard pipeline, Vim integration, context keys): the Visual Studio preset's keys add
/// carets, typing and Escape reach them through the window, <c>editorHasMultipleSelections</c> follows, and with Vim on the keys add nothing.
/// </summary>
public class MultiCursorTests
{
    private static async Task<(RealAppHost Host, EditorTab Tab)> StartAsync(bool vim)
    {
        var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("a.txt", "alpha\nbeta\ngamma\n");
            if (vim) return;
            var path = h.Home.Combine("config/settings.json");
            var current = File.ReadAllText(path).TrimEnd();
            File.WriteAllText(path, current[..^1].TrimEnd() + ",\n\"vim.enable\": false\n}\n");
        });
        await host.StartAsync([host.Files.Combine("a.txt")]);
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.Count == 1));
        var tab = host.Shell.Well.EditorTabs[0];
        Assert.True(await RealAppHost.WaitFor(() => tab.LiveView is CodeEditorControl { IsEffectivelyVisible: true }));
        await DevScene.SettleAsync(10);
        var view = (CodeEditorControl)tab.LiveView!;
        view.Modal.Controller.Focus();
        Assert.True(await RealAppHost.WaitFor(() => view.Modal.Controller.IsFocused));
        await DevScene.SettleAsync(3);
        return (host, tab);
    }

    /// <summary>Ctrl+Alt+Down, ⌘⌥↓ on macOS (both presets).</summary>
    private static RawInputModifiers InsertCursorModifiers => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta | RawInputModifiers.Alt : RawInputModifiers.Control | RawInputModifiers.Alt;

    private static async Task PressAsync(RealAppHost host, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        host.Window.KeyPress(key, modifiers, PhysicalKey.None, null);
        host.Window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
        await DevScene.SettleAsync(3); // a bound key runs its command asynchronously
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task With_Vim_off_the_preset_keys_add_carets_typing_reaches_them_and_Escape_removes_them()
    {
        var (host, tab) = await StartAsync(vim: false);
        await using var _ = host;
        var view = (CodeEditorControl)tab.LiveView!;
        var keys = host.Get<IContextKeyService>();
        tab.Controller!.SetSelection(TextRange.At(0, 0), reveal: false);
        await PressAsync(host, Key.Down, InsertCursorModifiers);
        await PressAsync(host, Key.Down, InsertCursorModifiers);
        Assert.True(await RealAppHost.WaitFor(() => tab.Controller!.Selections.Count == 3), $"carets: {tab.Controller!.Selections.Count}");
        Assert.True(keys.Get("editorHasMultipleSelections") is true);

        host.Window.KeyTextInput(">");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(">alpha\n>beta\n>gamma\n", view.Document.GetText().Replace("\r\n", "\n"));

        await PressAsync(host, Key.Escape);
        Assert.Single(tab.Controller!.Selections);
        Assert.True(keys.Get("editorHasMultipleSelections") is false);
        tab.Controller.Undo();
        Assert.Equal("alpha\nbeta\ngamma\n", view.Document.GetText().Replace("\r\n", "\n"));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task With_Vim_on_the_keys_add_no_caret()
    {
        var (host, tab) = await StartAsync(vim: true);
        await using var _ = host;
        var view = (CodeEditorControl)tab.LiveView!;
        Assert.True(view.Modal.IsAttached, "Vim is on by default");
        await PressAsync(host, Key.Down, InsertCursorModifiers);
        Assert.Single(tab.Controller!.Selections);
        Assert.Equal("alpha\nbeta\ngamma\n", view.Document.GetText().Replace("\r\n", "\n"));
    }
}
