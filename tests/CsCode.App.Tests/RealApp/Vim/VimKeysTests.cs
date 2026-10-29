using Avalonia.Headless.XUnit;
using CsCode.Core.Text;
using CsCode.Editor.View;
using CsCode.Vim;
using Xunit;

namespace CsCode.App.Tests.RealApp.Vim;

/// <summary>Vim in the real window: keys typed into the focused editor reach the engine and edit the real document.</summary>
public class VimKeysTests
{
    [AvaloniaFact(Timeout = 120_000)]
    public async Task Vim_is_on_by_default_and_normal_mode_keys_edit_the_document()
    {
        await using var app = await VimApp.StartAsync("one two three\nfour five\n");
        Assert.True(app.Vim.Enabled);
        Assert.Equal("-- NORMAL --", app.ModeText);
        Assert.Equal(EditorCaretShape.Block, app.View.Modal.CaretShape);
        app.Keys("wdw");
        Assert.Equal("one three\nfour five\n", app.Text);
        Assert.Equal(new TextPosition(0, 4), app.Caret);
        app.Keys("ihello <Esc>");
        Assert.Equal("one hello three\nfour five\n", app.Text);
        Assert.Equal(new TextPosition(0, 9), app.Caret);
        Assert.Equal("-- NORMAL --", app.ModeText);
    }
}
