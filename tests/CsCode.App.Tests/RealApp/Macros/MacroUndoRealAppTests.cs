using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.View;
using CsCode.Macros;
using Xunit;

namespace CsCode.App.Tests.RealApp.Macros;

/// <summary>
/// One Undo reverses a whole macro playback (docs/MACROS.md, "Undo"), in the REAL composition: real key events (Ctrl+Z / Ctrl+Y go to
/// AvaloniaEdit's own bindings, as for a user), the real player, editor views and undo stacks; <c>CSCODE_HOME</c> in a temp directory.
/// </summary>
public class MacroUndoRealAppTests
{
    private static KeyStep K(string key) => new(KeyStrokeParser.ParseStroke(key));

    private static void Undo(MacroApp app) => app.Press(Key.Z, MacroApp.Command);
    private static void Redo(MacroApp app) => app.Press(Key.Y, MacroApp.Command);

    [AvaloniaFact(Timeout = 120_000)]
    public async Task One_ctrl_z_reverses_a_playback_of_many_runs_and_ctrl_y_redoes_it()
    {
        await using var app = await MacroApp.StartAsync("one\ntwo\nthree\nfour\nfive");
        app.RecordKey();
        app.Press(Key.Home);
        app.Type("> ");
        app.Press(Key.Down);
        app.RecordKey();
        Assert.Equal([K("home"), new TextStep("> "), K("down")], app.Service.Last!.Steps);

        await app.Host.RunAsync(MacroCommands.PlayLast, 3);
        await app.FocusAsync();
        const string played = "> one\n> two\n> three\n> four\nfive";
        Assert.Equal(played, app.Text);
        Assert.False(app.View.Document.HasOpenUndoGroup);
        Assert.Equal(0, app.Integration.OpenUndoGroups);

        Undo(app);
        Assert.Equal("> one\ntwo\nthree\nfour\nfive", app.Text); // the three runs at once; the recording's own typing stays
        Assert.Equal(new TextPosition(1, 0), app.Caret);           // where the playback started
        Redo(app);
        Assert.Equal(played, app.Text);
        Undo(app);
        Undo(app);
        Assert.Equal("one\ntwo\nthree\nfour\nfive", app.Text);    // the recording's typed run was a step of its own
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Undo_recorded_inside_a_macro_replays_without_throwing_and_one_undo_still_reverses_the_playback()
    {
        await using var app = await MacroApp.StartAsync("base", seed: h => h.Home.WriteFile("data/macros.json", """
            { "macros": [ { "name": "menu-undo", "steps": [ { "text": "12" }, { "command": "undo" }, { "text": "3" }, { "command": "undo" }, { "command": "redo" } ] } ] }
            """));
        Assert.True(await RealAppHost.WaitFor(() => app.Host.Get<ICommandService>().Has("macro.run.menu-undo")));
        app.SetCaret(0, 4);
        app.RecordKey();
        app.Type("abc");
        Undo(app);                                   // AvaloniaEdit's own Ctrl+Z (Cmd+Z): not a key binding, so it is recorded as a key step
        app.Type("xy");
        app.RecordKey();
        Assert.Equal([new TextStep("abc"), K(OperatingSystem.IsMacOS() ? "meta+z" : "ctrl+z"), new TextStep("xy")], app.Service.Last!.Steps);
        Assert.Equal("basexy", app.Text);

        var result = await app.Service.PlayAsync(app.Service.Last!, new MacroPlayOptions(MacroRepeat.Times, 2));
        Assert.Equal(MacroPlayStatus.Completed, result.Status);
        Assert.Equal("basexyxyxy", app.Text);      // each run's Ctrl+Z took back its own "abc", as when it was recorded
        await app.FocusAsync();
        Undo(app);
        Assert.Equal("basexy", app.Text);

        // the editor's "undo" / "redo" commands (Edit menu, toolbar) inside a macro: the same
        await app.Host.RunAsync("macro.run.menu-undo");
        Assert.Equal("basexy3", app.Text);           // "12" undone, "3" undone and redone
        Assert.DoesNotContain(app.Notifications, n => n.Contains("menu-undo", StringComparison.Ordinal));
        await app.FocusAsync();
        Undo(app);
        Assert.Equal("basexy", app.Text);
    }

    private const string TwoEditorMacros = """
        { "macros": [ { "name": "both", "steps": [
            { "text": "1" }, { "command": "workbench.action.nextEditor" }, { "text": "2" },
            { "command": "workbench.action.previousEditor" }, { "text": "3" }, { "command": "workbench.action.nextEditor" }, { "text": "4" } ] } ] }
        """;

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_playback_across_two_editors_is_one_undo_step_in_each()
    {
        await using var app = await MacroApp.StartAsync("A", "a.txt", seed: h =>
        {
            h.Files.WriteFile("b.txt", "B");
            h.Home.WriteFile("data/macros.json", TwoEditorMacros);
        });
        var a = app.Tab;
        var b = await app.Well.OpenAsync(new Uri(app.Host.Files.Combine("b.txt")));
        app.Well.Activate(a);
        await app.FocusAsync();
        Assert.True(await RealAppHost.WaitFor(() => app.Host.Get<ICommandService>().Has("macro.run.both")));
        var docA = app.View.Document;

        await app.Host.RunAsync("macro.run.both");
        Assert.Same(b, app.Tab);
        var docB = app.View.Document;
        Assert.Equal("13A", docA.GetText());
        Assert.Equal("24B", docB.GetText());
        Assert.False(docA.HasOpenUndoGroup || docB.HasOpenUndoGroup);

        docB.Undo();
        docA.Undo();
        Assert.Equal(("A", "B"), (docA.GetText(), docB.GetText()));
        Assert.False(docA.CanUndo || docB.CanUndo); // one step each, nothing else
    }

    private const string StopMacros = """
        { "macros": [
            { "name": "fails", "steps": [ { "text": "a" }, { "command": "no.such.command" }, { "text": "b" } ] },
            { "name": "stops", "steps": [ { "text": "c" }, { "command": "macro.stop" }, { "text": "d" } ] },
            { "name": "vim", "steps": [ { "text": "e" }, { "command": "vim.toggle" }, { "text": "f" } ] },
            { "name": "close", "steps": [ { "text": "q" }, { "command": "workbench.action.files.save" }, { "command": "workbench.action.closeActiveEditor" }, { "text": "z" } ] }
        ] }
        """;

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_failing_stopped_or_vim_interrupted_playback_closes_its_undo_group_and_one_undo_reverses_what_it_did()
    {
        await using var app = await MacroApp.StartAsync("x", seed: h => h.Home.WriteFile("data/macros.json", StopMacros));
        Assert.True(await RealAppHost.WaitFor(() => app.Host.Get<ICommandService>().Has("macro.run.vim")));
        var doc = app.View.Document;

        foreach (var (name, typed) in new[] { ("fails", "a"), ("stops", "c") })
        {
            app.SetCaret(0, 0);
            await app.Host.RunAsync("macro.run." + name);
            Assert.Equal(typed + "x", app.Text);
            Assert.False(doc.HasOpenUndoGroup, name);
            Assert.Equal(0, app.Integration.OpenUndoGroups);
            await app.FocusAsync();
            app.Type("!");                           // after the playback: a step of its own
            Undo(app);
            Assert.Equal(typed + "x", app.Text);
            Undo(app);
            Assert.Equal("x", app.Text);
        }

        // Vim switched on by a step: the group closes there (Vim groups its own changes; the editor's Undo then runs Vim's u)
        app.SetCaret(0, 0);
        await app.Host.RunAsync("macro.run.vim");
        Assert.True(app.Host.Shell.Vim!.Enabled);
        Assert.Equal("ex", app.Text);
        Assert.False(doc.HasOpenUndoGroup);
        await app.Host.RunAsync("undo");              // "e" was typed before Vim: the editor undoes it itself
        Assert.Equal("x", app.Text);
        await app.Host.RunAsync("vim.toggle");
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Closing_the_editor_during_playback_closes_its_undo_group()
    {
        await using var app = await MacroApp.StartAsync("x", seed: h => h.Home.WriteFile("data/macros.json", StopMacros));
        Assert.True(await RealAppHost.WaitFor(() => app.Host.Get<ICommandService>().Has("macro.run.close")));
        var doc = app.View.Document;
        var groupOpenAtSave = false;
        using var saved = doc.Saved.Subscribe(_ => groupOpenAtSave = doc.HasOpenUndoGroup);

        await app.Host.RunAsync("macro.run.close");
        Assert.True(await RealAppHost.WaitFor(() => app.Well.ActiveEditor is null));
        Assert.True(groupOpenAtSave);                 // the save ran inside the playback's group
        Assert.False(doc.HasOpenUndoGroup);           // and the document's close ended it
        Assert.Equal(0, app.Integration.OpenUndoGroups);
        Assert.Equal("qx", await File.ReadAllTextAsync(app.Host.Files.Combine("test.txt")));
        Assert.Contains(app.Notifications, n => n.StartsWith("Macro 'close' stopped at step 4 of 4") && n.Contains("No editor is open"));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task The_document_is_dirty_from_the_playbacks_first_change_not_only_when_it_ends()
    {
        await using var app = await MacroApp.StartAsync("x", seed: h => h.Home.WriteFile("data/macros.json", """
            { "macros": [ { "name": "probe", "steps": [ { "text": "a" }, { "command": "test.probe" }, { "text": "b" } ] } ] }
            """));
        var dirtyDuringPlayback = (bool?)null;
        var tabDirtyDuringPlayback = (bool?)null;
        using var probe = app.Host.Get<ICommandService>().Register("test.probe", _ =>
        {
            dirtyDuringPlayback = app.View.Document.IsDirty;
            tabDirtyDuringPlayback = app.Tab.IsDirty;
            return ValueTask.FromResult<object?>(null);
        });
        Assert.True(await RealAppHost.WaitFor(() => app.Host.Get<ICommandService>().Has("macro.run.probe")));
        Assert.False(app.View.Document.IsDirty);

        await app.Host.RunAsync("macro.run.probe");
        Assert.Equal("abx", app.Text);
        Assert.True(dirtyDuringPlayback);             // a close prompt or a "save if dirty" step sees the change
        Assert.True(tabDirtyDuringPlayback);
        Undo(app);
        Assert.Equal("x", app.Text);
        Assert.False(app.View.Document.IsDirty);
    }
}
