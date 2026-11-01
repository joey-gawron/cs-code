using System.Text.Json;
using CsCode.Core.Contributions;
using CsCode.Core.Platform;
using CsCode.Platform.Configuration;
using CsCode.Platform.Context;
using CsCode.Platform.Io;
using CsCode.Platform.Keybindings;
using CsCode.Platform.Tests.Support;
using CsCode.TestDoubles;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CsCode.Platform.Tests;

public class KeyBindingFormatterTests
{
    [Theory]
    [InlineData("ctrl+k ctrl+c", HostOs.Linux, "Ctrl+K, Ctrl+C")]
    [InlineData("ctrl+k ctrl+c", HostOs.Windows, "Ctrl+K, Ctrl+C")]
    [InlineData("ctrl+shift+f5", HostOs.Windows, "Ctrl+Shift+F5")]
    [InlineData("shift+alt+up", HostOs.Linux, "Shift+Alt+Up")]
    [InlineData("ctrl+alt+shift+meta+k", HostOs.Windows, "Ctrl+Shift+Alt+Win+K")]
    [InlineData("meta+k", HostOs.Linux, "Super+K")]
    [InlineData("escape", HostOs.Linux, "Esc")]
    [InlineData("ctrl+pagedown", HostOs.Linux, "Ctrl+PageDown")]
    [InlineData("ctrl+\\", HostOs.Linux, "Ctrl+\\")]
    [InlineData("ctrl+`", HostOs.Linux, "Ctrl+`")]
    [InlineData("ctrl+,", HostOs.Linux, "Ctrl+,")]
    [InlineData("f12", HostOs.Linux, "F12")]
    [InlineData("ctrl+space", HostOs.Linux, "Ctrl+Space")]
    [InlineData("backspace", HostOs.Linux, "Backspace")]
    [InlineData("cmd+s", HostOs.MacOs, "⌘S")]
    [InlineData("cmd+k cmd+c", HostOs.MacOs, "⌘K ⌘C")]
    [InlineData("ctrl+alt+shift+cmd+k", HostOs.MacOs, "⌃⌥⇧⌘K")]
    [InlineData("shift+up", HostOs.MacOs, "⇧↑")]
    [InlineData("cmd+enter", HostOs.MacOs, "⌘↩")]
    [InlineData("escape", HostOs.MacOs, "⎋")]
    [InlineData("cmd+f5", HostOs.MacOs, "⌘F5")]
    public void Chords_format_per_platform(string chord, HostOs os, string expected) =>
        Assert.Equal(expected, KeyBindingFormatter.Format(KeyStrokeParser.ParseChord(chord), os));

    [Theory]
    [InlineData("ctrl+s")]
    [InlineData("ctrl+k ctrl+d")]
    [InlineData("shift+alt+f5")]
    [InlineData("ctrl+shift+/")]
    [InlineData("alt+left")]
    public void Parse_then_format_then_parse_is_stable(string chord)
    {
        var parsed = KeyStrokeParser.ParseChord(chord);
        var label = KeyBindingFormatter.Format(parsed, HostOs.Linux);
        var reparsed = KeyStrokeParser.ParseChord(label.Replace(", ", " "));
        Assert.Equal(parsed, reparsed);
    }

    [Fact]
    public void Core_parser_handles_plus_aliases_and_rejects_garbage()
    {
        Assert.Equal(new KeyStroke("+", Ctrl: true), KeyStrokeParser.ParseStroke("ctrl++"));
        Assert.Equal(new KeyStroke("escape"), KeyStrokeParser.ParseStroke("Esc"));
        Assert.Equal(new KeyStroke("s", Meta: true), KeyStrokeParser.ParseStroke("cmd+s"));
        Assert.Throws<FormatException>(() => KeyStrokeParser.ParseChord("   "));
        Assert.Throws<FormatException>(() => KeyStrokeParser.ParseStroke("ctrl+"));
        Assert.Throws<FormatException>(() => KeyStrokeParser.ParseStroke("a+b"));
    }
}

public sealed class KeybindingFixture : IDisposable
{
    public TempDirectory Temp { get; } = new("cscode-kb-");
    public IoLane Lane { get; } = new("test.kb", 2);
    public FakeFileWatcher Watcher { get; } = new();
    public ManualTimeProvider Clock { get; } = new();
    public ContextKeyService Context { get; } = new();
    public ContributionRegistry Registry { get; } = new();
    public CapturingLogger<KeybindingService> Log { get; } = new();
    public AppPaths Paths { get; }
    public PlatformOptions Options { get; }
    public string UserFile => Path.Combine(Paths.ConfigDir, "keybindings.json");

    public KeybindingFixture()
    {
        Paths = new AppPaths(k => k == "CSCODE_HOME" ? Temp.Combine("home") : null, HostOs.Linux, "/home/test");
        Options = new PlatformOptions { TimeProvider = Clock, ChordTimeout = TimeSpan.FromSeconds(5), ConfigReloadDebounce = TimeSpan.FromMilliseconds(20) };
    }

    public KeybindingService Create(HostOs os = HostOs.Linux, IConfigurationService? config = null, IStateStorage? state = null, bool watch = false) =>
        new(Context, Paths, Lane, Registry, config, state, watch ? Watcher : null, Options, os, Log);

    public void WriteUser(string text) { Directory.CreateDirectory(Paths.ConfigDir); File.WriteAllText(UserFile, text); }

    public void Dispose() { Lane.Dispose(); Temp.Dispose(); }
}

public class KeybindingServiceTests : IDisposable
{
    private readonly KeybindingFixture _f = new();
    public void Dispose() => _f.Dispose();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Feeds every stroke of the chord text and returns the LAST result.</summary>
    private static KeyResolveResult Press(IKeybindingService svc, string chord)
    {
        KeyResolveResult? last = null;
        foreach (var s in KeyStrokeParser.ParseChord(chord)) last = svc.Resolve(s);
        return last!;
    }

    private static string? Cmd(KeyResolveResult r) => r.Kind == KeyResolution.Matched ? r.Binding!.Command : null;

    // ---- the documented default shortcuts ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("f5", "workbench.action.debug.start", "")]
    [InlineData("f5", "workbench.action.debug.continue", "inDebugMode")]
    [InlineData("ctrl+f5", "workbench.action.debug.run", "")]
    [InlineData("shift+f5", "workbench.action.debug.stop", "inDebugMode")]
    [InlineData("ctrl+shift+f5", "workbench.action.debug.restart", "inDebugMode")]
    [InlineData("f9", "editor.debug.action.toggleBreakpoint", "editorTextFocus")]
    [InlineData("f10", "workbench.action.debug.stepOver", "inDebugMode")]
    [InlineData("f11", "workbench.action.debug.stepInto", "inDebugMode")]
    [InlineData("shift+f11", "workbench.action.debug.stepOut", "inDebugMode")]
    [InlineData("f12", "editor.action.revealDefinition", "editorTextFocus")]
    [InlineData("shift+f12", "editor.action.goToReferences", "editorTextFocus")]
    [InlineData("f2", "editor.action.rename", "editorTextFocus")]
    [InlineData("ctrl+k ctrl+d", "editor.action.formatDocument", "editorTextFocus")]
    [InlineData("ctrl+k ctrl+c", "editor.action.addCommentLine", "editorTextFocus")]
    [InlineData("ctrl+k ctrl+u", "editor.action.removeCommentLine", "editorTextFocus")]
    [InlineData("ctrl+.", "editor.action.quickFix", "editorTextFocus")]
    [InlineData("ctrl+space", "editor.action.triggerSuggest", "editorTextFocus")]
    [InlineData("ctrl+shift+space", "editor.action.triggerParameterHints", "editorTextFocus")]
    [InlineData("ctrl+q", "workbench.action.showCommands", "")]
    [InlineData("ctrl+t", "workbench.action.showAllSymbols", "")]
    [InlineData("ctrl+,", "workbench.action.showAllSymbols", "")]
    [InlineData("ctrl+shift+p", "workbench.action.showCommands", "")]
    [InlineData("ctrl+p", "workbench.action.quickOpen", "")]
    [InlineData("ctrl+shift+f", "workbench.action.findInFiles", "")]
    [InlineData("ctrl+f", "actions.find", "")]
    [InlineData("ctrl+h", "editor.action.startFindReplace", "")]
    [InlineData("ctrl+g", "workbench.action.gotoLine", "")]
    [InlineData("ctrl+s", "workbench.action.files.save", "")]
    [InlineData("ctrl+shift+s", "workbench.action.files.saveAll", "")]
    [InlineData("ctrl+tab", "workbench.action.quickOpenPreviousRecentlyUsedEditorInGroup", "")]
    [InlineData("ctrl+w c", "workbench.action.closeActiveEditor", "")]
    [InlineData("ctrl+f4", "workbench.action.closeActiveEditor", "")]
    [InlineData("ctrl+alt+l", "workbench.view.explorer", "")]
    [InlineData("ctrl+alt+o", "workbench.action.output.toggleOutput", "")]
    [InlineData("ctrl+\\ ctrl+e", "workbench.actions.view.problems", "")]
    [InlineData("ctrl+`", "workbench.action.terminal.toggleTerminal", "")]
    [InlineData("alt+up", "editor.action.moveLinesUpAction", "editorTextFocus")]
    [InlineData("alt+down", "editor.action.moveLinesDownAction", "editorTextFocus")]
    [InlineData("ctrl+d", "editor.action.copyLinesDownAction", "editorTextFocus")]
    [InlineData("ctrl+l", "editor.action.deleteLines", "editorTextFocus")]
    [InlineData("ctrl+/", "editor.action.commentLine", "editorTextFocus")]
    public void The_visual_studio_preset_binds_the_documented_shortcuts(string chord, string command, string requiredKey)
    {
        using var svc = _f.Create();
        Assert.Equal(VisualStudioPresetName, svc.ActivePreset);
        _f.Context.Set("editorTextFocus", true);
        if (requiredKey == "inDebugMode") _f.Context.Set("inDebugMode", true);
        Assert.Equal(command, Cmd(Press(svc, chord)));
    }

    private const string VisualStudioPresetName = "visualstudio";

    // ---- Ctrl+W: Vim's window commands with Vim off ------------------------------------------------------------------------------

    /// <summary>Every Ctrl+W chord: the second key with and without Ctrl, as in Vim (Ctrl+W v == Ctrl+W Ctrl+V).</summary>
    private static readonly (string Chord, string Command)[] WindowChords =
    {
        ("ctrl+w v", "workbench.action.splitEditorRight"), ("ctrl+w ctrl+v", "workbench.action.splitEditorRight"),
        ("ctrl+w s", "workbench.action.splitEditorDown"), ("ctrl+w shift+s", "workbench.action.splitEditorDown"), ("ctrl+w ctrl+s", "workbench.action.splitEditorDown"),
        ("ctrl+w h", "workbench.action.focusLeftGroup"), ("ctrl+w ctrl+h", "workbench.action.focusLeftGroup"),
        ("ctrl+w j", "workbench.action.focusBelowGroup"), ("ctrl+w ctrl+j", "workbench.action.focusBelowGroup"),
        ("ctrl+w k", "workbench.action.focusAboveGroup"), ("ctrl+w ctrl+k", "workbench.action.focusAboveGroup"),
        ("ctrl+w l", "workbench.action.focusRightGroup"), ("ctrl+w ctrl+l", "workbench.action.focusRightGroup"),
        ("ctrl+w w", "workbench.action.focusNextGroup"), ("ctrl+w ctrl+w", "workbench.action.focusNextGroup"),
        ("ctrl+w shift+w", "workbench.action.focusPreviousGroup"), ("ctrl+w p", "workbench.action.focusPreviousGroup"), ("ctrl+w ctrl+p", "workbench.action.focusPreviousGroup"),
        ("ctrl+w o", "workbench.action.closeEditorsInOtherGroups"), ("ctrl+w ctrl+o", "workbench.action.closeEditorsInOtherGroups"),
        ("ctrl+w n", "cscode.shell.newUntitledFileBelow"), ("ctrl+w ctrl+n", "cscode.shell.newUntitledFileBelow"),
        ("ctrl+w c", "workbench.action.closeActiveEditor"), ("ctrl+w q", "workbench.action.closeActiveEditor"), ("ctrl+w ctrl+q", "workbench.action.closeActiveEditor"),
    };

    public static TheoryData<string, HostOs> PresetsOnLinuxAndMac =>
        new() { { "visualstudio", HostOs.Linux }, { "visualstudio", HostOs.MacOs }, { "vscode", HostOs.Linux }, { "vscode", HostOs.MacOs } };

    [Theory]
    [MemberData(nameof(PresetsOnLinuxAndMac))]
    public async Task Ctrl_W_chords_run_the_window_commands_in_the_editor_with_Vim_off(string preset, HostOs os)
    {
        using var svc = _f.Create(os);
        await svc.SetPresetAsync(preset, Ct);
        _f.Context.Set("editorTextFocus", true);
        foreach (var (chord, command) in WindowChords)
        {
            Assert.Equal(KeyResolution.Pending, svc.Resolve(KeyStrokeParser.ParseStroke("ctrl+w")).Kind); // Control on a Mac too, as in Vim
            Assert.Equal(command, Cmd(svc.Resolve(KeyStrokeParser.ParseChord(chord)[1])));
            Assert.Empty(svc.PendingChord);
        }
        Assert.Equal(os == HostOs.MacOs ? "⌃W ⌃V" : "Ctrl+W, Ctrl+V", svc.GetLabel("workbench.action.splitEditorRight"));
        Assert.Equal(os == HostOs.MacOs ? "⌃W ⌃H" : "Ctrl+W, Ctrl+H", svc.GetLabel("workbench.action.focusLeftGroup"));
        Assert.Equal(os == HostOs.MacOs ? "⌘W" : "Ctrl+F4", svc.GetLabel(KnownCommands.CloseActiveEditor));
        Assert.DoesNotContain(_f.Log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Theory]
    [MemberData(nameof(PresetsOnLinuxAndMac))]
    public async Task Ctrl_W_starts_no_chord_outside_the_editor_or_with_Vim_on(string preset, HostOs os)
    {
        using var svc = _f.Create(os);
        await svc.SetPresetAsync(preset, Ct);
        // the integrated terminal has the keyboard: Ctrl+W (delete word) is the terminal's
        _f.Context.Set("terminalFocus", true);
        _f.Context.Set("editorTextFocus", false);
        Assert.Equal(KeyResolution.None, svc.Resolve(KeyStrokeParser.ParseStroke("ctrl+w")).Kind);
        Assert.Empty(svc.PendingChord);
        // the editor with Vim on: the engine's own Ctrl-W does all of it
        _f.Context.Set("terminalFocus", false);
        _f.Context.Set("editorTextFocus", true);
        _f.Context.Set("vim.active", true);
        Assert.Equal(KeyResolution.None, svc.Resolve(KeyStrokeParser.ParseStroke("ctrl+w")).Kind);
        Assert.Empty(svc.PendingChord);
        _f.Context.Set("vim.active", false);
        Assert.Equal(KeyResolution.Pending, svc.Resolve(KeyStrokeParser.ParseStroke("ctrl+w")).Kind);
    }

    [Theory]
    [MemberData(nameof(PresetNames))]
    public async Task Off_the_Mac_Ctrl_W_starts_the_chord_and_Ctrl_F4_closes_the_editor_on_the_Mac_Cmd_W_does(string preset)
    {
        using var linux = _f.Create(HostOs.Linux);
        using var mac = _f.Create(HostOs.MacOs);
        await linux.SetPresetAsync(preset, Ct);
        await mac.SetPresetAsync(preset, Ct);
        _f.Context.Set("editorTextFocus", true);
        Assert.Equal(KnownCommands.CloseActiveEditor, Cmd(Press(linux, "ctrl+f4")));
        Assert.Equal(KeyResolution.Pending, linux.Resolve(KeyStrokeParser.ParseStroke("ctrl+w")).Kind); // closes nothing at once
        linux.ResetChord();
        Assert.Equal(KnownCommands.CloseActiveEditor, Cmd(Press(mac, "cmd+w")));
        Assert.Equal(KnownCommands.CloseActiveEditor, Cmd(Press(mac, "cmd+f4")));
        Assert.DoesNotContain(linux.GetBindingsFor(KnownCommands.CloseActiveEditor), b => b.Chord.Count == 1 && b.Chord[0] == new KeyStroke("w", Ctrl: true));
        // anywhere else (Explorer, a tool window) Ctrl+W is nothing at all off the Mac; Ctrl+F4 and Cmd+W still close
        _f.Context.Set("editorTextFocus", false);
        Assert.Equal(KeyResolution.None, linux.Resolve(KeyStrokeParser.ParseStroke("ctrl+w")).Kind);
        Assert.Equal(KnownCommands.CloseActiveEditor, Cmd(Press(linux, "ctrl+f4")));
        Assert.Equal(KnownCommands.CloseActiveEditor, Cmd(Press(mac, "cmd+w")));
    }

    [Fact]
    public void An_unbound_second_key_after_Ctrl_W_ends_the_chord()
    {
        using var svc = _f.Create();
        _f.Context.Set("editorTextFocus", true);
        Assert.Equal(KeyResolution.Pending, svc.Resolve(KeyStrokeParser.ParseStroke("ctrl+w")).Kind);
        Assert.Equal(KeyResolution.None, svc.Resolve(KeyStrokeParser.ParseStroke("x")).Kind);
        Assert.Empty(svc.PendingChord);
        Assert.Equal("workbench.action.files.save", Cmd(Press(svc, "ctrl+s")));
    }

    [Fact]
    public void The_default_preset_is_visual_studio_and_the_command_ids_match_KnownCommands()
    {
        using var svc = _f.Create();
        string[] mustBind =
        [
            KnownCommands.CommandPalette, KnownCommands.QuickOpen, KnownCommands.OpenFolder, KnownCommands.OpenFile, KnownCommands.Save, KnownCommands.SaveAll,
            KnownCommands.NewUntitledFile, KnownCommands.CloseActiveEditor, KnownCommands.Find, KnownCommands.Replace, KnownCommands.GotoDefinition,
            KnownCommands.FindReferences, KnownCommands.Rename, KnownCommands.FormatDocument, KnownCommands.TriggerSuggest, KnownCommands.TriggerParameterHints,
            KnownCommands.QuickFix, KnownCommands.DebugStart, KnownCommands.DebugRun, KnownCommands.DebugStop, KnownCommands.DebugRestart, KnownCommands.DebugContinue,
            KnownCommands.DebugPause, KnownCommands.DebugStepOver, KnownCommands.DebugStepInto, KnownCommands.DebugStepOut, KnownCommands.DebugToggleBreakpoint,
            KnownCommands.ToggleTerminal,
        ];
        foreach (var id in mustBind) Assert.NotEmpty(svc.GetBindingsFor(id));
    }

    [Fact]
    public async Task Both_presets_bind_the_core_commands_and_parse_without_a_single_warning()
    {
        string[] shared =
        [
            KnownCommands.CommandPalette, KnownCommands.QuickOpen, KnownCommands.OpenFile, KnownCommands.Save, KnownCommands.CloseActiveEditor, KnownCommands.Find,
            KnownCommands.Replace, KnownCommands.GotoDefinition, KnownCommands.FindReferences, KnownCommands.Rename, KnownCommands.TriggerSuggest, KnownCommands.QuickFix,
            KnownCommands.DebugStart, KnownCommands.DebugStop, KnownCommands.DebugStepOver, KnownCommands.DebugStepInto, KnownCommands.DebugStepOut,
            KnownCommands.DebugToggleBreakpoint, KnownCommands.ToggleTerminal, KnownCommands.NewUntitledFile,
        ];
        using var svc = _f.Create();
        foreach (var preset in KeybindingService.Presets)
        {
            await svc.SetPresetAsync(preset, Ct);
            Assert.Equal(preset, svc.ActivePreset);
            foreach (var id in shared) Assert.True(svc.GetBindingsFor(id).Count > 0, $"{preset}: nothing bound to {id}");
            // every chord valid, every when-clause valid, no exact duplicates
            foreach (var b in svc.All) Assert.Null(WhenClause.Validate(b.When));
            var dups = svc.All.GroupBy(b => (string.Join(' ', b.Chord), b.When, b.Command)).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            Assert.Empty(dups);
        }
        Assert.DoesNotContain(_f.Log.Entries, e => e.Level >= LogLevel.Warning);
    }

    // ---- chords ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_chord_prefix_is_pending_and_the_second_stroke_completes_it()
    {
        using var svc = _f.Create();
        _f.Context.Set("editorTextFocus", true);

        var first = svc.Resolve(new KeyStroke("k", Ctrl: true));
        Assert.Equal(KeyResolution.Pending, first.Kind);
        Assert.Equal([new KeyStroke("k", Ctrl: true)], svc.PendingChord);

        var second = svc.Resolve(new KeyStroke("d", Ctrl: true));
        Assert.Equal(KeyResolution.Matched, second.Kind);
        Assert.Equal("editor.action.formatDocument", second.Binding!.Command);
        Assert.Empty(svc.PendingChord);
    }

    [Fact]
    public void An_unknown_second_stroke_resets_the_chord_and_reports_None()
    {
        using var svc = _f.Create();
        _f.Context.Set("editorTextFocus", true);
        svc.Resolve(new KeyStroke("k", Ctrl: true));
        var r = svc.Resolve(new KeyStroke("x"));
        Assert.Equal(KeyResolution.None, r.Kind);
        Assert.Empty(svc.PendingChord);
        // ...and the next stroke starts fresh
        Assert.Equal("workbench.action.files.save", Cmd(Press(svc, "ctrl+s")));
    }

    [Fact]
    public void Escape_cancels_a_pending_chord()
    {
        using var svc = _f.Create();
        _f.Context.Set("editorTextFocus", true);
        svc.Resolve(new KeyStroke("k", Ctrl: true));
        Assert.Equal(KeyResolution.None, svc.Resolve(new KeyStroke("escape")).Kind);
        Assert.Empty(svc.PendingChord);
    }

    [Fact]
    public void A_chord_times_out_and_the_late_stroke_is_treated_as_a_fresh_first_stroke()
    {
        using var svc = _f.Create();
        _f.Context.Set("editorTextFocus", true);
        svc.Resolve(new KeyStroke("k", Ctrl: true));
        _f.Clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Single(svc.PendingChord);                          // still waiting

        _f.Clock.Advance(TimeSpan.FromSeconds(2));                 // 6 s > 5 s timeout
        Assert.Empty(svc.PendingChord);                            // expired lazily

        // Ctrl+D now means "duplicate line" (a first stroke), not the second half of Ctrl+K Ctrl+D.
        Assert.Equal("editor.action.copyLinesDownAction", Cmd(svc.Resolve(new KeyStroke("d", Ctrl: true))));
    }

    [Fact]
    public void A_late_second_stroke_after_the_timeout_does_not_complete_the_old_chord()
    {
        using var svc = _f.Create();
        _f.Context.Set("editorTextFocus", true);
        svc.Resolve(new KeyStroke("k", Ctrl: true));
        _f.Clock.Advance(TimeSpan.FromSeconds(10));
        var r = svc.Resolve(new KeyStroke("c", Ctrl: true));       // would have been Ctrl+K Ctrl+C
        Assert.NotEqual("editor.action.addCommentLine", Cmd(r));
    }

    [Fact]
    public void ResetChord_abandons_a_pending_chord()
    {
        using var svc = _f.Create();
        _f.Context.Set("editorTextFocus", true);
        svc.Resolve(new KeyStroke("k", Ctrl: true));
        svc.ResetChord();
        Assert.Empty(svc.PendingChord);
        // Ctrl+D is now a fresh first stroke (duplicate line), not the end of Ctrl+K Ctrl+D
        Assert.Equal("editor.action.copyLinesDownAction", Cmd(svc.Resolve(new KeyStroke("d", Ctrl: true))));
    }

    [Fact]
    public void A_chord_whose_when_clause_fails_is_not_started()
    {
        using var svc = _f.Create();
        // Every Ctrl+K chord in the preset needs editorTextFocus
        Assert.Equal(KeyResolution.None, svc.Resolve(new KeyStroke("k", Ctrl: true)).Kind);
        _f.Context.Set("editorTextFocus", true);
        Assert.Equal(KeyResolution.Pending, svc.Resolve(new KeyStroke("k", Ctrl: true)).Kind);
    }

    [Fact]
    public void When_clauses_select_between_bindings_on_the_same_key()
    {
        using var svc = _f.Create();
        Assert.Equal("workbench.action.debug.start", Cmd(Press(svc, "f5")));
        _f.Context.Set("inDebugMode", true);
        Assert.Equal("workbench.action.debug.continue", Cmd(Press(svc, "f5")));
        _f.Context.Set("inDebugMode", false);
        Assert.Equal("workbench.action.debug.start", Cmd(Press(svc, "f5")));
    }

    [Fact]
    public void Unbound_keys_resolve_to_None_so_typing_reaches_the_editor()
    {
        using var svc = _f.Create();
        Assert.Equal(KeyResolution.None, svc.Resolve(new KeyStroke("a")).Kind);
        Assert.Equal(KeyResolution.None, svc.Resolve(new KeyStroke("a", Shift: true)).Kind);
        Assert.Equal(KeyResolution.None, svc.Resolve(new KeyStroke("enter")).Kind);
    }

    [Fact]
    public void Absurdly_long_input_never_overflows_the_chord_buffer()
    {
        using var svc = _f.Create();
        for (var i = 0; i < 1000; i++) svc.Resolve(new KeyStroke("k", Ctrl: true));
        Assert.True(svc.PendingChord.Count <= 8);
    }

    [Fact]
    public void A_three_stroke_chord_from_a_user_file_works()
    {
        _f.WriteUser("""[ { "key": "ctrl+x ctrl+y ctrl+z", "command": "my.triple" } ]""");
        using var svc = _f.Create();
        Assert.Equal(KeyResolution.Pending, svc.Resolve(KeyStrokeParser.ParseStroke("ctrl+x")).Kind);
        Assert.Equal(KeyResolution.Pending, svc.Resolve(KeyStrokeParser.ParseStroke("ctrl+y")).Kind);
        Assert.Equal("my.triple", Cmd(svc.Resolve(KeyStrokeParser.ParseStroke("ctrl+z"))));
    }

    [Fact]
    public void A_single_stroke_binding_and_a_chord_sharing_the_prefix_prefer_the_higher_precedence_one()
    {
        // user binds Ctrl+K alone: it outranks the default Ctrl+K chords, which therefore can no longer start
        _f.WriteUser("""[ { "key": "ctrl+k", "command": "my.k" } ]""");
        using var svc = _f.Create();
        _f.Context.Set("editorTextFocus", true);
        Assert.Equal("my.k", Cmd(svc.Resolve(KeyStrokeParser.ParseStroke("ctrl+k"))));
    }

    // ---- precedence, negatives, platforms ----------------------------------------------------------------------------------------

    [Fact]
    public void User_bindings_beat_default_bindings_and_the_default_remains_for_other_keys()
    {
        _f.WriteUser("""[ { "key": "ctrl+s", "command": "my.save" } ]""");
        using var svc = _f.Create();
        Assert.Equal("my.save", Cmd(Press(svc, "ctrl+s")));
        Assert.Equal("workbench.action.files.saveAll", Cmd(Press(svc, "ctrl+shift+s")));
        Assert.Equal(KeyBindingSource.User, Press(svc, "ctrl+s").Binding!.Source);
    }

    [Fact]
    public void Later_entries_in_the_same_source_win()
    {
        _f.WriteUser("""[ { "key": "ctrl+9", "command": "first" }, { "key": "ctrl+9", "command": "second" } ]""");
        using var svc = _f.Create();
        Assert.Equal("second", Cmd(Press(svc, "ctrl+9")));
    }

    [Fact]
    public void A_user_binding_with_a_failing_when_falls_back_to_the_default()
    {
        _f.WriteUser("""[ { "key": "ctrl+s", "command": "my.save", "when": "myMode" } ]""");
        using var svc = _f.Create();
        Assert.Equal("workbench.action.files.save", Cmd(Press(svc, "ctrl+s")));
        _f.Context.Set("myMode", true);
        Assert.Equal("my.save", Cmd(Press(svc, "ctrl+s")));
    }

    [Fact]
    public void A_negative_binding_removes_the_matching_default()
    {
        _f.WriteUser("""[ { "key": "ctrl+s", "command": "-workbench.action.files.save" } ]""");
        using var svc = _f.Create();
        Assert.Equal(KeyResolution.None, Press(svc, "ctrl+s").Kind);
        Assert.Empty(svc.GetBindingsFor("workbench.action.files.save"));
        Assert.Equal("workbench.action.files.saveAll", Cmd(Press(svc, "ctrl+shift+s")));
    }

    [Fact]
    public void A_negative_binding_for_a_different_key_removes_nothing()
    {
        _f.WriteUser("""[ { "key": "ctrl+shift+9", "command": "-workbench.action.files.save" } ]""");
        using var svc = _f.Create();
        Assert.Equal("workbench.action.files.save", Cmd(Press(svc, "ctrl+s")));
    }

    [Fact]
    public void A_keyless_negative_binding_removes_every_binding_of_the_command()
    {
        _f.WriteUser("""[ { "command": "-workbench.action.closeActiveEditor" } ]""");
        using var svc = _f.Create();
        Assert.Empty(svc.GetBindingsFor("workbench.action.closeActiveEditor"));
        Assert.Equal(KeyResolution.None, Press(svc, "ctrl+f4").Kind);
        // Ctrl+W c / q / Ctrl+Q close the editor too (in the editor, Vim off): those go, Ctrl+W's other window chords stay
        _f.Context.Set("editorTextFocus", true);
        foreach (var chord in new[] { "ctrl+w c", "ctrl+w q", "ctrl+w ctrl+q" })
        {
            Assert.Equal(KeyResolution.None, Press(svc, chord).Kind);
            Assert.Empty(svc.PendingChord);
        }
        Assert.Equal("workbench.action.splitEditorRight", Cmd(Press(svc, "ctrl+w v")));
    }

    [Fact]
    public void A_negative_binding_can_be_scoped_by_when_and_only_removes_a_compatible_entry()
    {
        _f.WriteUser("""
            [
              { "key": "f5", "command": "-workbench.action.debug.continue", "when": "inDebugMode" },
              { "key": "f5", "command": "-workbench.action.debug.start", "when": "somethingElse" }
            ]
            """);
        using var svc = _f.Create();
        _f.Context.Set("inDebugMode", true);
        Assert.Equal(KeyResolution.None, Press(svc, "f5").Kind);          // continue was removed (when equal)
        _f.Context.Set("inDebugMode", false);
        Assert.Equal("workbench.action.debug.start", Cmd(Press(svc, "f5"))); // start survives (when differs)
    }

    [Fact]
    public void A_negative_only_removes_entries_declared_before_it()
    {
        _f.WriteUser("""
            [
              { "key": "ctrl+9", "command": "-my.cmd" },
              { "key": "ctrl+9", "command": "my.cmd" },
              { "key": "ctrl+8", "command": "other" },
              { "key": "ctrl+8", "command": "-other" }
            ]
            """);
        using var svc = _f.Create();
        Assert.Equal("my.cmd", Cmd(Press(svc, "ctrl+9")));   // declared after the removal: stays
        Assert.Equal(KeyResolution.None, Press(svc, "ctrl+8").Kind); // declared before the removal: gone
    }

    [Fact]
    public async Task Removing_a_higher_binding_lets_a_lower_precedence_one_on_the_same_key_show_through()
    {
        _f.WriteUser("""
            [ { "key": "ctrl+shift+9", "command": "low" } ]
            """);
        using var svc = _f.Create();
        // add a higher one then remove it: the lower one is active again
        _f.WriteUser("""
            [ { "key": "ctrl+shift+9", "command": "low" },
              { "key": "ctrl+shift+9", "command": "high" },
              { "key": "ctrl+shift+9", "command": "-high" } ]
            """);
        await svc.ReloadAsync(Ct);
        Assert.Equal("low", Cmd(Press(svc, "ctrl+shift+9")));
    }

    [Fact]
    public void Platform_specific_keys_replace_the_generic_key_in_the_preset()
    {
        using var linux = _f.Create(HostOs.Linux);
        using var mac = _f.Create(HostOs.MacOs);
        Assert.Equal("workbench.action.files.save", Cmd(Press(linux, "ctrl+s")));
        Assert.Equal(KeyResolution.None, Press(mac, "ctrl+s").Kind);
        Assert.Equal("workbench.action.files.save", Cmd(Press(mac, "cmd+s")));
        Assert.Equal("⌘S", mac.GetLabel("workbench.action.files.save"));
        Assert.Equal("Ctrl+S", linux.GetLabel("workbench.action.files.save"));
    }

    [Fact]
    public void Platform_specific_keys_in_the_user_file_are_honoured()
    {
        _f.WriteUser("""[ { "key": "ctrl+9", "linux": "alt+9", "win": "shift+9", "mac": "cmd+9", "command": "x" } ]""");
        using var linux = _f.Create(HostOs.Linux);
        using var win = _f.Create(HostOs.Windows);
        using var mac = _f.Create(HostOs.MacOs);
        Assert.Equal("x", Cmd(Press(linux, "alt+9")));
        Assert.Equal("x", Cmd(Press(win, "shift+9")));
        Assert.Equal("x", Cmd(Press(mac, "cmd+9")));
        Assert.Equal(KeyResolution.None, Press(linux, "ctrl+9").Kind);
    }

    [Fact]
    public void An_entry_with_only_another_platforms_key_binds_nothing_here_and_is_not_a_keyless_removal()
    {
        _f.WriteUser("""
            [ { "mac": "cmd+9", "command": "x" },
              { "mac": "cmd+s", "command": "-workbench.action.files.save" } ]
            """);
        using var linux = _f.Create(HostOs.Linux);
        using var mac = _f.Create(HostOs.MacOs);
        Assert.Empty(linux.LoadWarnings);
        Assert.Empty(linux.GetBindingsFor("x"));
        Assert.Equal("workbench.action.files.save", Cmd(Press(linux, "ctrl+s"))); // the mac-only removal leaves Linux's Ctrl+S alone
        Assert.Equal("x", Cmd(Press(mac, "cmd+9")));
        Assert.Equal(KeyResolution.None, Press(mac, "cmd+s").Kind);
    }

    public static TheoryData<string> PresetNames => [.. KeybindingService.Presets];

    /// <summary>
    /// A Ctrl binding without a "mac" key leaves a Mac user pressing Control where every other Mac app wants Cmd (audit ui-3 / files-11). Keys
    /// that stay on Control on a Mac (Ctrl+Space, Ctrl+Tab, Ctrl+`, ...) say so with an explicit "mac" key.
    /// </summary>
    [Theory]
    [MemberData(nameof(PresetNames))]
    public void Every_ctrl_binding_in_the_presets_has_a_mac_key(string preset)
    {
        using var stream = typeof(KeybindingService).Assembly.GetManifestResourceStream($"CsCode.Platform.Keymaps.{preset}.json")!;
        var root = (JsoncArray)JsoncParser.Parse(new StreamReader(stream).ReadToEnd())!;
        var missing = new List<string>();
        foreach (var o in root.Items.Cast<JsoncObject>())
        {
            string? Str(string name) => (o.Members.LastOrDefault(m => m.Name == name)?.Value as JsoncPrimitive)?.StringValue;
            if (Str("key") is { } key && KeyStrokeParser.ParseChord(key).Any(s => s.Ctrl) && Str("mac") is null)
                missing.Add($"{key} → {Str("command")}");
        }
        Assert.True(missing.Count == 0, $"{preset}: Ctrl bindings without a \"mac\" key: {string.Join("; ", missing)}");
    }

    [Theory]
    [MemberData(nameof(PresetNames))]
    public async Task On_macOS_the_presets_bind_Cmd_open_settings_on_Cmd_comma_and_leave_the_system_keys_alone(string preset)
    {
        using var mac = _f.Create(HostOs.MacOs);
        await mac.SetPresetAsync(preset, Ct);
        Assert.Equal(KnownCommands.OpenSettings, Cmd(Press(mac, "cmd+,")));
        Assert.Equal("⌘,", mac.GetLabel(KnownCommands.OpenSettings));
        Assert.Equal("⌘S", mac.GetLabel(KnownCommands.Save));
        Assert.Equal("workbench.action.findInFiles", Cmd(Press(mac, "cmd+shift+f")));
        _f.Context.Set("editorTextFocus", true);
        Assert.Equal("editor.action.addCommentLine", Cmd(Press(mac, "cmd+k cmd+c")));

        // Cmd+H hides the app, Cmd+M minimizes, Cmd+Q quits, Cmd+` cycles windows, Cmd+Tab switches apps, Cmd+Space is Spotlight, Cmd+? is Help.
        string[] system = ["cmd+h", "cmd+m", "cmd+q", "cmd+`", "cmd+tab", "cmd+space", "cmd+shift+/"];
        var reserved = system.Select(KeyStrokeParser.ParseStroke).ToHashSet();
        Assert.DoesNotContain(mac.All, b => reserved.Contains(b.Chord[0]));
        // Keys that make another command unreachable (with the editor's contributed keys): EditorClipboardViewTests.On_macOS_no_preset_key_clashes_...
        Assert.DoesNotContain(_f.Log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void Visual_Studios_preset_binds_no_settings_key_on_Linux()
    {
        using var linux = _f.Create(HostOs.Linux);
        Assert.Empty(linux.GetBindingsFor(KnownCommands.OpenSettings));
        Assert.Equal("workbench.action.showAllSymbols", Cmd(Press(linux, "ctrl+,")));
        Assert.DoesNotContain(_f.Log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData(HostOs.Linux)]
    [InlineData(HostOs.MacOs)]
    public async Task The_VS_Code_preset_binds_VS_Codes_multi_cursor_keys(HostOs os)
    {
        using var svc = _f.Create(os);
        await svc.SetPresetAsync(KeybindingService.VsCodePreset, Ct);
        _f.Context.Set("editorTextFocus", true);
        var mac = os == HostOs.MacOs;
        Assert.Equal("editor.action.addSelectionToNextFindMatch", Cmd(Press(svc, mac ? "cmd+d" : "ctrl+d")));
        Assert.Equal("editor.action.moveSelectionToNextFindMatch", Cmd(Press(svc, mac ? "cmd+k cmd+d" : "ctrl+k ctrl+d")));
        Assert.Equal("editor.action.selectHighlights", Cmd(Press(svc, mac ? "cmd+shift+l" : "ctrl+shift+l")));
        Assert.Equal("editor.action.insertCursorAbove", Cmd(Press(svc, mac ? "cmd+alt+up" : "ctrl+alt+up")));
        Assert.Equal("editor.action.insertCursorBelow", Cmd(Press(svc, mac ? "cmd+alt+down" : "ctrl+alt+down")));
        Assert.Equal("cursorUndo", Cmd(Press(svc, mac ? "cmd+u" : "ctrl+u")));
        _f.Context.Set("vim.active", true); // Vim owns Ctrl+U (and multi-cursor is off with it)
        Assert.Equal(KeyResolution.None, Press(svc, mac ? "cmd+u" : "ctrl+u").Kind);
    }

    [Theory]
    [InlineData(HostOs.Linux)]
    [InlineData(HostOs.MacOs)]
    public async Task The_visual_studio_preset_keeps_its_own_keys_and_uses_Visual_Studios_multi_caret_keys(HostOs os)
    {
        using var svc = _f.Create(os);
        await svc.SetPresetAsync(VisualStudioPresetName, Ct);
        _f.Context.Set("editorTextFocus", true);
        var mac = os == HostOs.MacOs;
        // Ctrl+D, Ctrl+K Ctrl+D and Ctrl+Shift+L stay Duplicate, Format Document and Delete Line, as in Visual Studio
        Assert.Equal("editor.action.copyLinesDownAction", Cmd(Press(svc, mac ? "cmd+d" : "ctrl+d")));
        Assert.Equal("editor.action.formatDocument", Cmd(Press(svc, mac ? "cmd+k cmd+d" : "ctrl+k ctrl+d")));
        Assert.Equal("editor.action.deleteLines", Cmd(Press(svc, mac ? "cmd+shift+k" : "ctrl+shift+l")));
        Assert.Equal("editor.action.addSelectionToNextFindMatch", Cmd(Press(svc, "shift+alt+.")));
        Assert.Equal("editor.action.selectHighlights", Cmd(Press(svc, "shift+alt+;")));
        Assert.Equal("editor.action.insertCursorAbove", Cmd(Press(svc, mac ? "cmd+alt+up" : "ctrl+alt+up")));
        Assert.Equal("editor.action.insertCursorBelow", Cmd(Press(svc, mac ? "cmd+alt+down" : "ctrl+alt+down")));
        Assert.Equal("cursorUndo", Cmd(Press(svc, mac ? "cmd+u" : "ctrl+u")));
    }

    [Fact]
    public async Task The_VS_Code_preset_binds_no_toggle_fold_on_Linux()
    {
        using var linux = _f.Create(HostOs.Linux);
        await linux.SetPresetAsync(KeybindingService.VsCodePreset, Ct);
        _f.Context.Set("editorTextFocus", true);
        Assert.Empty(linux.GetBindingsFor("editor.toggleFold"));
        Assert.Equal(KeyResolution.None, Press(linux, "ctrl+k ctrl+l").Kind);
    }

    [Fact]
    public void Args_in_a_binding_are_preserved()
    {
        _f.WriteUser("""[ { "key": "ctrl+alt+x", "command": "run.thing", "args": { "mode": "fast", "n": [1, 2] } } ]""");
        using var svc = _f.Create();
        var b = Press(svc, "ctrl+alt+x").Binding!;
        Assert.Equal("fast", b.Args!.Value.GetProperty("mode").GetString());
        Assert.Equal(2, b.Args.Value.GetProperty("n")[1].GetInt32());
    }

    // ---- GetBindingsFor / GetLabel / All -----------------------------------------------------------------------------------------

    [Fact]
    public void GetBindingsFor_lists_highest_precedence_first_and_the_label_uses_it()
    {
        _f.WriteUser("""[ { "key": "ctrl+alt+s", "command": "workbench.action.files.save" } ]""");
        using var svc = _f.Create();
        var all = svc.GetBindingsFor("workbench.action.files.save");
        Assert.Equal(2, all.Count);
        Assert.Equal(KeyBindingSource.User, all[0].Source);
        Assert.Equal("Ctrl+Alt+S", svc.GetLabel("workbench.action.files.save"));
        Assert.Null(svc.GetLabel("no.such.command"));
        Assert.Empty(svc.GetBindingsFor("no.such.command"));
    }

    [Fact]
    public void Chord_labels_use_a_comma_separated_form()
    {
        using var svc = _f.Create();
        Assert.Equal("Ctrl+K, Ctrl+D", svc.GetLabel("editor.action.formatDocument"));
    }

    [Fact]
    public void All_contains_only_active_non_negative_bindings()
    {
        _f.WriteUser("""[ { "key": "ctrl+s", "command": "-workbench.action.files.save" }, { "key": "ctrl+9", "command": "mine" } ]""");
        using var svc = _f.Create();
        Assert.DoesNotContain(svc.All, b => b.IsNegative);
        Assert.DoesNotContain(svc.All, b => b.Command == "workbench.action.files.save");
        Assert.Contains(svc.All, b => b.Command == "mine");
    }

    // ---- presets -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Switching_presets_changes_the_table_fires_Changed_once_and_rejects_unknown_names()
    {
        using var svc = _f.Create();
        var changes = 0;
        using var sub = svc.Changed.Subscribe(_ => changes++);
        Assert.Equal("workbench.action.showCommands", Cmd(Press(svc, "ctrl+q")));

        await svc.SetPresetAsync("vscode", Ct);
        Assert.Equal("vscode", svc.ActivePreset);
        Assert.Equal(1, changes);
        Assert.Equal(KeyResolution.None, Press(svc, "ctrl+q").Kind);          // VS-only binding is gone
        _f.Context.Set("editorTextFocus", true);
        Assert.Equal("editor.action.formatDocument", Cmd(Press(svc, "ctrl+shift+i"))); // VS Code's Linux binding (the "linux" override replaces shift+alt+f)
        Assert.Equal(KeyResolution.None, Press(svc, "shift+alt+f").Kind);
        Assert.Equal("editor.action.deleteLines", Cmd(Press(svc, "ctrl+shift+k")));

        await svc.SetPresetAsync("VSCode", Ct);                                // same preset, any case: no-op
        Assert.Equal(1, changes);
        await Assert.ThrowsAsync<ArgumentException>(async () => await svc.SetPresetAsync("emacs", Ct));
        Assert.Equal("vscode", svc.ActivePreset);
    }

    [Fact]
    public async Task The_preset_choice_is_persisted_to_the_cscode_keymap_setting()
    {
        var cfg = new TestConfigurationService();
        using var svc = _f.Create(config: cfg);
        await svc.SetPresetAsync("vscode", Ct);
        Assert.Equal("vscode", cfg.Get<string>("cscode.keymap"));
    }

    [Fact]
    public async Task Without_a_configuration_service_the_preset_is_persisted_in_state_storage()
    {
        var state = new DictionaryStateStorage();
        using (var svc = _f.Create(state: state)) await svc.SetPresetAsync("vscode", Ct);
        using var again = _f.Create(state: state);
        Assert.Equal("vscode", again.ActivePreset);
    }

    [Fact]
    public async Task The_initial_preset_comes_from_the_setting_and_follows_live_changes()
    {
        var cfg = new TestConfigurationService();
        cfg.Set("cscode.keymap", "vscode");
        using var svc = _f.Create(config: cfg);
        Assert.Equal("vscode", svc.ActivePreset);

        var changes = 0;
        using var sub = svc.Changed.Subscribe(_ => changes++);
        cfg.Set("cscode.keymap", "visualstudio");        // the user edits settings.json
        Assert.Equal("visualstudio", svc.ActivePreset);
        Assert.Equal(1, changes);

        cfg.Set("cscode.keymap", "emacs");               // unknown value: keep what we have
        Assert.Equal("visualstudio", svc.ActivePreset);
        Assert.Equal(1, changes);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_unknown_keymap_in_the_settings_file_falls_back_to_visual_studio()
    {
        var cfg = new TestConfigurationService();
        cfg.Set("cscode.keymap", "emacs");
        using var svc = _f.Create(config: cfg);
        Assert.Equal("visualstudio", svc.ActivePreset);
        Assert.Contains(_f.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("emacs"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Preset_persistence_works_end_to_end_with_the_real_configuration_service()
    {
        using var lane = new IoLane("test.cfg", 2);
        using var cfg = new ConfigurationService(_f.Paths, lane, null, _f.Options);
        using (var svc = _f.Create(config: cfg)) await svc.SetPresetAsync("vscode", Ct);

        Assert.Contains("\"cscode.keymap\": \"vscode\"", File.ReadAllText(cfg.UserSettingsPath));
        using var cfg2 = new ConfigurationService(_f.Paths, lane, null, _f.Options);
        using var svc2 = _f.Create(config: cfg2);
        Assert.Equal("vscode", svc2.ActivePreset);
    }

    // ---- user file ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Edits_to_keybindings_json_are_picked_up_and_announced()
    {
        using var svc = _f.Create();
        var changes = 0;
        using var sub = svc.Changed.Subscribe(_ => changes++);
        Assert.Equal(KeyResolution.None, Press(svc, "ctrl+9").Kind);

        _f.WriteUser("""[ { "key": "ctrl+9", "command": "nine" } ]""");
        await svc.ReloadAsync(Ct);
        Assert.Equal("nine", Cmd(Press(svc, "ctrl+9")));
        Assert.Equal(1, changes);

        await svc.ReloadAsync(Ct);                       // unchanged file: nothing
        Assert.Equal(1, changes);

        File.Delete(_f.UserFile);
        await svc.ReloadAsync(Ct);
        Assert.Equal(KeyResolution.None, Press(svc, "ctrl+9").Kind);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task The_file_watcher_triggers_a_debounced_reload()
    {
        using var svc = _f.Create(watch: true);
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = svc.Changed.Subscribe(_ => fired.TrySetResult());
        _f.WriteUser("""[ { "key": "ctrl+9", "command": "nine" } ]""");
        for (var i = 0; i < 5; i++) _f.Watcher.Raise(_f.UserFile);
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("nine", Cmd(Press(svc, "ctrl+9")));
    }

    [Fact]
    public void The_user_file_may_contain_comments_and_trailing_commas()
    {
        _f.WriteUser("""
            // my keys
            [
              /* save elsewhere */
              { "key": "ctrl+9", "command": "nine", },
            ]
            """);
        using var svc = _f.Create();
        Assert.Equal("nine", Cmd(Press(svc, "ctrl+9")));
        Assert.Empty(svc.LoadWarnings);
    }

    [Fact]
    public void Bad_entries_are_skipped_with_a_warning_and_good_ones_still_load()
    {
        _f.WriteUser("""
            [
              { "key": "ctrl+9", "command": "nine" },
              { "key": "ctrl+8" },
              { "key": "ctrl+", "command": "bad.key" },
              { "key": "ctrl+7", "command": "bad.when", "when": "a &&" },
              "not an object",
              { "key": "ctrl+6", "command": "six" }
            ]
            """);
        using var svc = _f.Create();
        Assert.Equal("nine", Cmd(Press(svc, "ctrl+9")));
        Assert.Equal("six", Cmd(Press(svc, "ctrl+6")));
        Assert.Equal(KeyResolution.None, Press(svc, "ctrl+8").Kind);
        Assert.Equal(4, svc.LoadWarnings.Count);
        Assert.Contains(svc.LoadWarnings, w => w.Contains("no \"command\""));
        Assert.Contains(svc.LoadWarnings, w => w.Contains("bad.when") && w.Contains("when"));
        // the bad-when binding is kept but can never fire
        Assert.Equal(KeyResolution.None, Press(svc, "ctrl+7").Kind);
    }

    [Fact]
    public async Task A_file_that_is_not_valid_json_keeps_the_previous_bindings()
    {
        _f.WriteUser("""[ { "key": "ctrl+9", "command": "nine" } ]""");
        using var svc = _f.Create();
        _f.WriteUser("""[ { "key": "ctrl+9", "command": "nine" """);   // mid-edit typo
        await svc.ReloadAsync(Ct);
        Assert.Equal("nine", Cmd(Press(svc, "ctrl+9")));
        Assert.NotEmpty(svc.LoadWarnings);
    }

    [Fact]
    public void A_user_file_whose_root_is_not_an_array_is_reported_not_thrown()
    {
        _f.WriteUser("""{ "key": "ctrl+9" }""");
        using var svc = _f.Create();
        Assert.NotEmpty(svc.LoadWarnings);
        Assert.Equal("workbench.action.files.save", Cmd(Press(svc, "ctrl+s")));
    }

    // ---- contributions -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Contributed_bindings_appear_announce_and_disappear_with_their_registration()
    {
        using var svc = _f.Create();
        var changes = 0;
        using var sub = svc.Changed.Subscribe(_ => changes++);

        var reg = _f.Registry.Contribute(ContributionSources.Builtin, "", [new KeybindingContribution("my.contrib", "ctrl+9")]);
        Assert.Equal("my.contrib", Cmd(Press(svc, "ctrl+9")));
        Assert.Equal(1, changes);

        reg.Dispose();
        Assert.Equal(KeyResolution.None, Press(svc, "ctrl+9").Kind);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Contribution_precedence_is_preset_then_builtin_then_user_contribution_then_user_file()
    {
        _f.WriteUser("""[ { "key": "ctrl+s", "command": "from.file" } ]""");
        using var svc = _f.Create();

        _f.Registry.Contribute(ContributionSources.Builtin, "", [new KeybindingContribution("from.builtin", "ctrl+s")]);
        Assert.Equal("from.file", Cmd(Press(svc, "ctrl+s")));                    // user file beats everything

        File.Delete(_f.UserFile);
        await svc.ReloadAsync(Ct);
        Assert.Equal("from.builtin", Cmd(Press(svc, "ctrl+s")));                  // builtin contribution beats the preset

        _f.Registry.Contribute(ContributionSources.User, "", [new KeybindingContribution("from.usercontrib", "ctrl+s")]);
        Assert.Equal("from.usercontrib", Cmd(Press(svc, "ctrl+s")));              // user-sourced contribution beats builtin
    }

    [Fact]
    public void Contributions_honour_platform_keys_when_and_negation()
    {
        _f.Registry.Contribute(ContributionSources.Builtin, "",
        [
            new KeybindingContribution("c.cmd", "ctrl+9", Mac: "cmd+9", Linux: "alt+9", When: "ctxA"),
            new KeybindingContribution("-workbench.action.files.save", "ctrl+s"),
            new KeybindingContribution("c.bad", "ctrl+"),     // malformed: skipped, others unaffected
        ]);
        using var linux = _f.Create(HostOs.Linux);
        using var mac = _f.Create(HostOs.MacOs);
        _f.Context.Set("ctxA", true);
        Assert.Equal("c.cmd", Cmd(Press(linux, "alt+9")));
        Assert.Equal("c.cmd", Cmd(Press(mac, "cmd+9")));
        Assert.Equal(KeyResolution.None, Press(linux, "ctrl+s").Kind);
        _f.Context.Set("ctxA", false);
        Assert.Equal(KeyResolution.None, Press(linux, "alt+9").Kind);
    }

    // ---- performance, concurrency, lifetime --------------------------------------------------------------------------------------

    [Fact]
    public void Resolve_allocates_nothing_after_warm_up_over_one_million_strokes()
    {
        using var svc = _f.Create();
        _f.Context.Set("editorTextFocus", true);
        _f.Context.Set("inDebugMode", true);
        KeyStroke[] strokes =
        [
            new("s", Ctrl: true),                 // matched
            new("k", Ctrl: true),                 // chord start
            new("d", Ctrl: true),                 // chord completion
            new("a"),                             // unbound
            new("f5"),                            // when-filtered pair
            new("k", Ctrl: true), new("x"),       // chord that dead-ends
            new("f12"),
            new("tab", Ctrl: true),
        ];
        for (var i = 0; i < 100_000; i++) svc.Resolve(strokes[i % strokes.Length]);
        svc.ResetChord();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var matched = 0;
        for (var i = 0; i < 1_000_000; i++)
            if (svc.Resolve(strokes[i % strokes.Length]).Kind == KeyResolution.Matched) matched++;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(matched > 0);
        Assert.True(allocated <= 4096, $"Resolve allocated {allocated:N0} bytes over 1,000,000 strokes");
    }

    [Fact]
    public async Task Parallel_resolve_preset_switch_and_contributions_have_no_exceptions()
    {
        using var svc = _f.Create();
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var tasks = new List<Task>
        {
            Task.Run(() => { try { var i = 0; while (!cts.IsCancellationRequested) { svc.Resolve(new KeyStroke(i++ % 2 == 0 ? "s" : "k", Ctrl: true)); _ = svc.All; _ = svc.GetLabel("workbench.action.files.save"); } } catch (Exception ex) { errors.Add(ex); } }, Ct),
            Task.Run(async () => { try { var i = 0; while (!cts.IsCancellationRequested) await svc.SetPresetAsync(i++ % 2 == 0 ? "vscode" : "visualstudio", Ct); } catch (Exception ex) { errors.Add(ex); } }, Ct),
            Task.Run(() => { try { while (!cts.IsCancellationRequested) { using var _ = _f.Registry.Contribute(ContributionSources.Builtin, "", [new KeybindingContribution("c", "ctrl+9")]); } } catch (Exception ex) { errors.Add(ex); } }, Ct),
        };
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ReloadAsync_does_not_block_the_caller_when_the_io_lane_is_saturated()
    {
        using var lane = new IoLane("test.sat", 1);
        using var svc = new KeybindingService(_f.Context, _f.Paths, lane, _f.Registry, options: _f.Options);
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var blocker = lane.RunAsync(_ => { started.Set(); gate.Wait(); }, Ct);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), Ct));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var reload = svc.ReloadAsync(Ct);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(100));
        Assert.False(reload.IsCompleted);
        gate.Set();
        await reload.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await blocker;
    }

    [Fact]
    public void Disposing_the_service_releases_the_registry_config_and_watcher_subscriptions()
    {
        var cfg = new TestConfigurationService();
        var svc = _f.Create(config: cfg, watch: true);
        Assert.Equal(1, _f.Watcher.ActiveCount);
        svc.Dispose();
        Assert.Equal(0, _f.Watcher.ActiveCount);
        // after dispose a registry/config change must neither reach nor resurrect the service
        _f.Registry.Contribute(ContributionSources.Builtin, "", [new KeybindingContribution("x", "ctrl+9")]);
        cfg.Set("cscode.keymap", "vscode");
        Assert.Equal("visualstudio", svc.ActivePreset);
    }

    [Fact]
    public void A_disposed_service_and_its_subscriptions_are_not_retained_by_long_lived_collaborators()
    {
        var cfg = new TestConfigurationService();
        LeakAssert.NotRetained(() =>
        {
            var svc = _f.Create(config: cfg, watch: true);
            var subscriber = new List<object?>();
            var sub = svc.Changed.Subscribe(subscriber.Add);
            _f.Registry.Contribute(ContributionSources.Builtin, "", [new KeybindingContribution("x", "ctrl+9")]).Dispose();
            sub.Dispose();
            svc.Dispose();
            return [svc, subscriber];
        });
        GC.KeepAlive(cfg);
    }
}
