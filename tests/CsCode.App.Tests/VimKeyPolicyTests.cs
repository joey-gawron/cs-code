using System.Text.Json;
using CsCode.App.Vim;
using CsCode.Core.Platform;
using CsCode.Vim;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>Who gets a key press while an editor with Vim has focus (docs/VIM.md, "Keys"): the table, Insert-mode popups and vim.handleKeys.</summary>
public class VimKeyPolicyTests
{
    private static KeyStroke K(string spec) => KeyStrokeParser.ParseStroke(spec);

    private static VimKeyOwner Owner(string key, VimMode mode, EditorPopups popups = EditorPopups.None, string? handleKeys = null) =>
        VimKeyPolicy.Decide(K(key), mode, popups, handleKeys is null ? null : VimKeyPolicy.ParseHandleKeys(JsonDocument.Parse(handleKeys).RootElement));

    [Theory]
    [InlineData("ctrl+d")] [InlineData("ctrl+u")] [InlineData("ctrl+f")] [InlineData("ctrl+b")] [InlineData("ctrl+e")] [InlineData("ctrl+y")]
    [InlineData("ctrl+r")] [InlineData("ctrl+o")] [InlineData("ctrl+i")] [InlineData("ctrl+v")] [InlineData("ctrl+w")] [InlineData("ctrl+a")]
    [InlineData("ctrl+x")] [InlineData("ctrl+n")] [InlineData("ctrl+p")] [InlineData("ctrl+[")] [InlineData("escape")]
    public void Vim_owns_its_ctrl_keys_and_escape_in_normal_visual_and_operator_pending_mode(string key)
    {
        foreach (var mode in new[] { VimMode.Normal, VimMode.Visual, VimMode.VisualLine, VimMode.VisualBlock, VimMode.OperatorPending })
            Assert.Equal(VimKeyOwner.Vim, Owner(key, mode));
    }

    [Theory]
    [InlineData("ctrl+shift+p")] [InlineData("ctrl+s")] [InlineData("ctrl+q")] [InlineData("ctrl+shift+f")] [InlineData("f5")] [InlineData("shift+f12")]
    [InlineData("alt+f")] [InlineData("ctrl+alt+l")] [InlineData("ctrl+space")] [InlineData("ctrl+tab")] [InlineData("ctrl+k")] [InlineData("ctrl+c")]
    [InlineData("ctrl+z")] [InlineData("ctrl+g")] [InlineData("meta+c")]
    public void Application_chords_keep_working_in_every_mode(string key)
    {
        foreach (var mode in new[] { VimMode.Normal, VimMode.Visual, VimMode.Insert, VimMode.Replace, VimMode.OperatorPending })
            Assert.Equal(VimKeyOwner.App, Owner(key, mode));
    }

    [Fact]
    public void The_clipboard_keys_shift_insert_shift_delete_and_ctrl_insert_are_the_applications_except_on_the_command_line()
    {
        foreach (var mode in new[] { VimMode.Normal, VimMode.Visual, VimMode.VisualLine, VimMode.VisualBlock, VimMode.Insert, VimMode.Replace, VimMode.OperatorPending })
            foreach (var key in new[] { "shift+insert", "shift+delete", "ctrl+insert", "ctrl+shift+v" })
                Assert.Equal(VimKeyOwner.App, Owner(key, mode));
        Assert.Equal(VimKeyOwner.Vim, Owner("shift+insert", VimMode.CommandLine));
        Assert.Equal(VimKeyOwner.Vim, Owner("shift+delete", VimMode.CommandLine));
        // the plain keys stay Vim's (Insert toggles Replace mode, Delete is x)
        Assert.Equal(VimKeyOwner.Vim, Owner("insert", VimMode.Insert));
        Assert.Equal(VimKeyOwner.Vim, Owner("delete", VimMode.Normal));
        Assert.Equal(VimKeyOwner.Vim, Owner("shift+delete", VimMode.Normal, handleKeys: "{\"<S-Del>\": true}"));
    }

    [Fact]
    public void Insert_mode_keeps_the_editors_paste_select_all_and_quick_open_and_takes_vims_editing_keys()
    {
        foreach (var key in new[] { "ctrl+v", "ctrl+a", "ctrl+x", "ctrl+p", "ctrl+e", "ctrl+y", "ctrl+f" }) Assert.Equal(VimKeyOwner.App, Owner(key, VimMode.Insert));
        foreach (var key in new[] { "ctrl+w", "ctrl+u", "ctrl+h", "ctrl+t", "ctrl+d", "ctrl+r", "ctrl+o", "ctrl+n", "ctrl+[", "escape", "enter", "tab", "backspace", "up" })
            Assert.Equal(VimKeyOwner.Vim, Owner(key, VimMode.Insert));
    }

    [Fact]
    public void While_the_completion_list_is_open_tab_enter_and_the_arrows_are_the_editors_and_ctrl_n_p_move_through_it()
    {
        foreach (var key in new[] { "tab", "enter", "up", "down", "pageup", "pagedown", "escape" })
            Assert.Equal(VimKeyOwner.App, Owner(key, VimMode.Insert, EditorPopups.Completion));
        Assert.Equal(VimKeyOwner.Vim, Owner("ctrl+n", VimMode.Insert, EditorPopups.Completion));
        Assert.Equal(VimKeyOwner.Vim, Owner("ctrl+p", VimMode.Insert, EditorPopups.Completion)); // the integration moves the list's selection
        Assert.Equal(VimKeyOwner.Vim, Owner("backspace", VimMode.Insert, EditorPopups.Completion));
        // a snippet session keeps Tab; parameter hints keep the arrows and Esc; Normal mode is unaffected by popups
        Assert.Equal(VimKeyOwner.App, Owner("tab", VimMode.Insert, EditorPopups.Snippet));
        Assert.Equal(VimKeyOwner.Vim, Owner("escape", VimMode.Insert, EditorPopups.Snippet));
        Assert.Equal(VimKeyOwner.App, Owner("down", VimMode.Insert, EditorPopups.SignatureHelp));
        Assert.Equal(VimKeyOwner.App, Owner("escape", VimMode.Insert, EditorPopups.SignatureHelp));
        Assert.Equal(VimKeyOwner.Vim, Owner("down", VimMode.Normal, EditorPopups.Completion));
    }

    [Fact]
    public void With_several_carets_insert_modes_editing_and_caret_keys_are_the_editors_which_acts_at_every_caret()
    {
        foreach (var key in new[] { "backspace", "delete", "tab", "shift+tab", "left", "right", "up", "down", "home", "end" })
            Assert.Equal(VimKeyOwner.App, Owner(key, VimMode.Insert, EditorPopups.MultipleCarets));
        // Esc and Enter stay Vim's (the integration leaves Insert mode at every caret; Enter is typed through the editor at each), and so do
        // its Insert-mode Ctrl keys; Normal mode is unaffected
        foreach (var key in new[] { "escape", "enter", "ctrl+w" })
            Assert.Equal(VimKeyOwner.Vim, Owner(key, VimMode.Insert, EditorPopups.MultipleCarets));
        foreach (var key in new[] { "backspace", "left", "down" })
            Assert.Equal(VimKeyOwner.Vim, Owner(key, VimMode.Normal, EditorPopups.MultipleCarets));
    }

    [Fact]
    public void The_command_line_takes_ctrl_keys_but_not_save()
    {
        foreach (var key in new[] { "ctrl+r", "ctrl+u", "ctrl+w", "ctrl+h", "ctrl+v", "ctrl+n", "ctrl+p", "ctrl+b", "ctrl+e" })
            Assert.Equal(VimKeyOwner.Vim, Owner(key, VimMode.CommandLine));
        Assert.Equal(VimKeyOwner.App, Owner("ctrl+s", VimMode.CommandLine));
    }

    [Fact]
    public void Handle_keys_overrides_a_key_either_way()
    {
        const string settings = """{ "<C-d>": false, "<C-c>": true, "<C-S-x>": true, "<A-j>": true, "<Esc>": false, "<F5>": true, "j": false, "<nonsense>": true }""";
        Assert.Equal(VimKeyOwner.App, Owner("ctrl+d", VimMode.Normal, handleKeys: settings));
        Assert.Equal(VimKeyOwner.Vim, Owner("ctrl+c", VimMode.Normal, handleKeys: settings));
        Assert.Equal(VimKeyOwner.Vim, Owner("ctrl+shift+x", VimMode.Normal, handleKeys: settings));
        Assert.Equal(VimKeyOwner.Vim, Owner("alt+j", VimMode.Insert, handleKeys: settings));
        Assert.Equal(VimKeyOwner.App, Owner("escape", VimMode.Insert, handleKeys: settings));
        Assert.Equal(VimKeyOwner.Vim, Owner("f5", VimMode.Normal, handleKeys: settings));
        var parsed = VimKeyPolicy.ParseHandleKeys(JsonDocument.Parse(settings).RootElement);
        Assert.Equal(6, parsed.Count); // "j" is a character (the engine's handleKeys covers it) and "<nonsense>" is no key
    }

    [Theory]
    [InlineData("a", true)] [InlineData("shift+a", true)] [InlineData("1", true)] [InlineData("space", true)] [InlineData("[", true)] [InlineData("numpad5", true)]
    [InlineData("ctrl+a", false)] [InlineData("alt+a", false)] [InlineData("escape", false)] [InlineData("enter", false)] [InlineData("f1", false)]
    public void Characters_arrive_as_text_input_and_are_left_alone_as_key_presses(string key, bool text) =>
        Assert.Equal(text, VimKeyPolicy.IsTextKey(K(key)));
}
