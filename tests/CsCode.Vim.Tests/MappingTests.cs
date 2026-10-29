using Xunit;

namespace CsCode.Vim.Tests;

/// <summary>Mappings and timeoutlen disambiguation with a fake timer (nothing here waits for real time).</summary>
public class MappingTests
{
    private static (VimEngine Engine, InMemoryVimHost Host, VimSession Session) Create(string text, params string[] setup)
    {
        var host = new InMemoryVimHost(text);
        var session = new VimSession();
        var engine = new VimEngine(host, session);
        foreach (var s in setup) Assert.True(engine.ExecuteEx(s), s + ": " + engine.LastError);
        return (engine, host, session);
    }

    private static string Text(InMemoryVimHost h) => string.Join("\n", Enumerable.Range(0, h.Buffer.LineCount).Select(h.Buffer.GetLine));

    [Fact]
    public void A_complete_mapping_with_no_longer_candidate_fires_immediately()
    {
        var (e, h, _) = Create("abc\ndef", "nnoremap Q dd");
        e.TypeKeys("Q");
        Assert.Equal("def", Text(h));
        Assert.Equal(0, h.ManualTimer!.PendingCount);
    }

    [Fact]
    public void An_ambiguous_prefix_waits_for_timeoutlen_and_then_uses_the_shorter_mapping()
    {
        var (e, h, _) = Create("abcdef", "nnoremap g x", "nnoremap gx $");
        e.TypeKeys("g");
        Assert.Equal("abcdef", Text(h));
        Assert.Equal(1, h.ManualTimer!.PendingCount);
        h.ManualTimer.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal("abcdef", Text(h));
        h.ManualTimer.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("bcdef", Text(h));
        Assert.Equal(0, h.ManualTimer.PendingCount);
    }

    [Fact]
    public void Completing_the_longer_mapping_before_the_timeout_cancels_the_wait()
    {
        var (e, h, _) = Create("abcdef", "nnoremap g x", "nnoremap gx $");
        e.TypeKeys("g");
        h.ManualTimer!.Advance(TimeSpan.FromMilliseconds(500));
        e.TypeKeys("x");
        Assert.Equal("abcdef", Text(h));
        Assert.Equal(5, e.Cursor.Character);
        Assert.Equal(0, h.ManualTimer.PendingCount);
        h.ManualTimer.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("abcdef", Text(h));
    }

    [Fact]
    public void A_key_that_breaks_the_longer_mapping_applies_the_shorter_one_and_keeps_the_key()
    {
        var (e, h, _) = Create("abcdef", "nnoremap g x", "nnoremap gx $");
        e.TypeKeys("gl");
        Assert.Equal("bcdef", Text(h));
        Assert.Equal(1, e.Cursor.Character);   // g -> x, then l moved right
    }

    [Fact]
    public void Nowait_makes_the_shorter_mapping_win_without_waiting()
    {
        var (e, h, _) = Create("abcdef", "nnoremap <nowait> g x", "nnoremap gx $");
        e.TypeKeys("g");
        Assert.Equal("bcdef", Text(h));
        Assert.Equal(0, h.ManualTimer!.PendingCount);
    }

    [Fact]
    public void Notimeout_waits_until_another_key_arrives()
    {
        var (e, h, _) = Create("abcdef", "set notimeout", "nnoremap g x", "nnoremap gx $");
        e.TypeKeys("g");
        Assert.Equal(0, h.ManualTimer!.PendingCount);
        h.ManualTimer.Advance(TimeSpan.FromHours(1));
        Assert.Equal("abcdef", Text(h));
        e.TypeKeys("x");
        Assert.Equal(5, e.Cursor.Character);
    }

    [Fact]
    public void Timeoutlen_is_honoured()
    {
        var (e, h, _) = Create("abcdef", "set timeoutlen=200", "nnoremap g x", "nnoremap gx $");
        e.TypeKeys("g");
        h.ManualTimer!.Advance(TimeSpan.FromMilliseconds(199));
        Assert.Equal("abcdef", Text(h));
        h.ManualTimer.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("bcdef", Text(h));
    }

    [Fact]
    public void A_recursive_mapping_stops_with_E223_and_the_engine_stays_usable()
    {
        var (e, h, _) = Create("abc\ndef", "nmap a b", "nmap b a");
        e.TypeKeys("a");
        Assert.Contains("E223", e.LastError);
        e.TypeKeys("x");
        Assert.Equal("bc\ndef", Text(h));
    }

    [Fact]
    public void Noremap_does_not_apply_other_mappings_to_its_result()
    {
        var (e, h, _) = Create("abc\ndef", "nmap Q W", "nnoremap W x");
        e.TypeKeys("Q");     // Q -> W (remapped) -> x
        Assert.Equal("bc\ndef", Text(h));
        var (e2, h2, _) = Create("abc def", "nnoremap Q W", "nnoremap W x");
        e2.TypeKeys("Q");    // Q -> W (not remapped): move a word
        Assert.Equal("abc def", Text(h2));
        Assert.Equal(4, e2.Cursor.Character);
    }

    [Fact]
    public void A_mapping_whose_rhs_starts_with_its_lhs_does_not_recurse()
    {
        var (e, h, _) = Create("abc\ndef", "nmap j jx");
        e.TypeKeys("j");
        Assert.Equal("abc\nef", Text(h));
    }

    [Fact]
    public void Leader_expands_when_the_mapping_is_defined()
    {
        var (e, h, _) = Create("abc\ndef", "let mapleader=','", "nnoremap <leader>d dd", "let mapleader=';'");
        e.TypeKeys(",d");
        Assert.Equal("def", Text(h));
        Assert.Equal(0, h.ManualTimer!.PendingCount);
    }

    [Fact]
    public void Mappings_are_per_mode()
    {
        var (e, h, _) = Create("abc def", "nnoremap Q dw", "inoremap Q X", "vnoremap Q d", "onoremap Q w", "cnoremap Q 1");
        e.TypeKeys("Q");
        Assert.Equal("def", Text(h));
        e.TypeKeys("iQ<Esc>");
        Assert.Equal("Xdef", Text(h));
        e.TypeKeys("vlQ");
        Assert.Equal("ef", Text(h));
        e.TypeKeys("dQ");
        Assert.Equal("", Text(h));
        e.TypeKeys(":Q<C-c>");
        Assert.Equal(VimMode.Normal, e.Mode);
    }

    [Fact]
    public void Unmap_and_mapclear_remove_mappings()
    {
        var (e, h, session) = Create("abc", "nnoremap Q x", "nnoremap R x", "unmap Q");
        e.TypeKeys("Q");
        Assert.Equal("abc", Text(h));
        e.TypeKeys("R");
        Assert.Equal("bc", Text(h));
        e.ExecuteEx("mapclear");
        Assert.Empty(session.Mappings.Items);
        Assert.False(e.ExecuteEx("unmap Z"));
        Assert.Contains("E31", e.LastError);
    }

    [Fact]
    public void Mappings_are_shared_by_all_engines_of_a_session()
    {
        var session = new VimSession();
        var h1 = new InMemoryVimHost("abc");
        var h2 = new InMemoryVimHost("xyz");
        using var e1 = new VimEngine(h1, session);
        using var e2 = new VimEngine(h2, session);
        e1.ExecuteEx("nnoremap Q x");
        e2.TypeKeys("Q");
        Assert.Equal("yz", Text(h2));
    }

    [Fact]
    public void Macros_record_the_typed_keys_not_the_mapping_expansion()
    {
        var (e, h, session) = Create("abc\ndef\nghi", "nnoremap Q x");
        e.TypeKeys("qaQjq");
        Assert.Equal("Qj", session.Registers.ForRead('a')!.Lines[0]);
        e.TypeKeys("@a");
        Assert.Equal("bc\nef\nghi", Text(h));
    }

    [Fact]
    public void Mappings_expand_inside_macros_and_count_as_one_undo_step_with_the_macro()
    {
        var (e, h, _) = Create("abc\ndef\nghi", "nnoremap Q dd");
        e.TypeKeys("qaQq@a");
        Assert.Equal("ghi", Text(h));
        e.TypeKeys("u");
        Assert.Equal("def\nghi", Text(h));
    }

    [Fact]
    public void A_pending_mapping_does_not_swallow_a_failing_command_argument()
    {
        var (e, h, _) = Create("abc", "nnoremap ,d x");
        e.TypeKeys("f,");        // the argument of f is never mapped
        Assert.Equal("abc", Text(h));
        e.TypeKeys(",d");
        Assert.Equal("bc", Text(h));
    }

    [Fact]
    public void Settings_style_bindings_run_commands_and_keys()
    {
        var (e, h, session) = Create("abc\ndef");
        session.Mappings.Add(new Mapping
        {
            Lhs = [(int)'Z'], Rhs = [(int)'j'], Modes = MapModes.Normal, NoRemap = true, LhsText = "Z", RhsText = "j",
            Commands = [new VimBindingCommand("editor.action.revealDefinition", ["arg"])], Origin = "settings",
        });
        e.TypeKeys("Z");
        Assert.Equal("editor.action.revealDefinition", h.Commands[^1].Id);
        Assert.Equal(1, e.Cursor.Line);
    }

    [Fact]
    public void Unhandled_keys_pass_through_to_the_editor()
    {
        var (e, _, session) = Create("abc");
        Assert.Equal(VimKeyResult.PassThrough, e.HandleKey(0x13));   // Ctrl-S is not a Vim key
        Assert.Equal(VimKeyResult.Handled, e.HandleKey('l'));
        session.UnhandledKeys.Add('l');
        Assert.Equal(VimKeyResult.PassThrough, e.HandleKey('l'));
        e.Enabled = false;
        Assert.Equal(VimKeyResult.PassThrough, e.HandleKey('x'));
    }

    [Fact]
    public void Ex_mapping_listing_and_errors()
    {
        var (e, _, _) = Create("abc");
        Assert.True(e.ExecuteEx("nnoremap"));   // a listing is not an error
        Assert.True(e.ExecuteEx("nnoremap Q x") && e.ExecuteEx("nnoremap Q"));
    }
}
