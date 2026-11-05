using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Vim.Tests;

/// <summary>
/// Vim's spelling keys over CsCode's spell checker (docs/SPELLCHECK.md): the engine runs the checker's commands at the cursor. ]s and [s are
/// gated on 'spell' (Vim's E756 without it); z=, zg and zw act either way, as in Vim. Not differential: Neovim's own spell checker is not
/// the one these keys drive.
/// </summary>
public class SpellKeyTests
{
    private static (VimEngine Engine, InMemoryVimHost Host, VimSession Session) Create(bool? spellSetting = null)
    {
        var session = new VimSession();
        var config = new TestConfigurationService();
        if (spellSetting is { } s) config.Set(VimSettings.Spell, s);
        _ = new VimConfiguration(session, config, null, null);
        var host = new InMemoryVimHost("one wierd line\ntwo\nthree recieve");
        return (new VimEngine(host, session), host, session);
    }

    [Fact]
    public void Next_and_previous_misspelling_run_the_spell_checkers_commands_with_the_count()
    {
        var (engine, host, _) = Create();
        engine.TypeKeys("]s");
        Assert.Equal("cSpell.goToNextSpellingIssue", host.Commands[^1].Id);
        Assert.Equal([1], host.Commands[^1].Args!);
        engine.TypeKeys("3[s");
        Assert.Equal("cSpell.goToPreviousSpellingIssue", host.Commands[^1].Id);
        Assert.Equal([3], host.Commands[^1].Args!);
        engine.TypeKeys("]S");
        Assert.Equal("cSpell.goToNextSpellingIssue", host.Commands[^1].Id);
        Assert.Null(engine.LastError);
    }

    [Fact]
    public void Without_spell_the_jumps_are_an_error_and_set_spell_turns_them_on()
    {
        var (engine, host, session) = Create(spellSetting: false);
        Assert.False(session.Options.Spell);
        engine.TypeKeys("]s");
        Assert.Empty(host.Commands);
        Assert.Contains("E756", engine.LastError, StringComparison.Ordinal);
        engine.TypeKeys(":set spell<CR>");
        Assert.True(session.Options.Spell);
        engine.TypeKeys("[s");
        Assert.Equal("cSpell.goToPreviousSpellingIssue", host.Commands[^1].Id);
        engine.TypeKeys(":set nospell<CR>]s");
        Assert.Single(host.Commands);
    }

    [Fact]
    public void Spell_follows_the_vim_spell_setting_and_defaults_to_on()
    {
        var (_, _, session) = Create();
        Assert.True(session.Options.Spell);
        Assert.False(new VimOptions().Spell); // plain Vim's default, for hosts without settings
    }

    [Fact]
    public void Suggestions_add_and_flag_act_on_the_word_at_the_cursor_with_spell_off_too()
    {
        var (engine, host, _) = Create(spellSetting: false);
        engine.TypeKeys("wz=");
        Assert.Equal("cSpell.suggestSpellingCorrections", host.Commands[^1].Id);
        engine.TypeKeys("zg");
        Assert.Equal("cSpell.addWordToUserDictionary", host.Commands[^1].Id);
        engine.TypeKeys("zw");
        Assert.Equal("cscode.spellCheck.flagWord", host.Commands[^1].Id);
        engine.TypeKeys("zG");
        Assert.Equal("cSpell.addWordToUserDictionary", host.Commands[^1].Id);
        engine.TypeKeys("zW");
        Assert.Equal("cscode.spellCheck.flagWord", host.Commands[^1].Id);
        Assert.Equal(5, host.Commands.Count);
        Assert.Equal("one wierd line", host.Buffer.GetLine(0)); // nothing typed into the text
        Assert.Equal(VimMode.Normal, engine.Mode);
    }
}
