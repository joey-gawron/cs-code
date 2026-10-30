using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Notifications;
using CsCode.Core.Platform;
using Xunit;

namespace CsCode.App.Tests.RealApp.Macros;

/// <summary>
/// T50 (data loss) during macro playback, in the REAL composition: a playback that saves mid-way and types on at the same caret (the save does
/// not change the version, so the typed runs on both sides of it used to be one continued step). The document must be dirty after the playback
/// (the file holds the text of the save), closing must prompt, and one Undo still reverses the whole playback.
/// </summary>
public class MacroSaveDirtyTests
{
    private const string SaveMidway = """
        { "macros": [
            { "name": "midway", "steps": [ { "text": "a" }, { "command": "workbench.action.files.save" }, { "text": "b" } ] },
            { "name": "last", "steps": [ { "text": "c" }, { "command": "workbench.action.files.save" } ] } ] }
        """;

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_playback_that_saves_midway_and_types_on_leaves_the_document_dirty_and_closing_prompts()
    {
        await using var app = await MacroApp.StartAsync("x", seed: h => h.Home.WriteFile("data/macros.json", SaveMidway));
        Assert.True(await RealAppHost.WaitFor(() => app.Host.Get<ICommandService>().Has("macro.run.midway")));
        var path = app.Host.Files.Combine("test.txt");
        var doc = app.View.Document;

        await app.Host.RunAsync("macro.run.midway");
        Assert.Equal("abx", app.Text);
        Assert.Equal("ax", await File.ReadAllTextAsync(path));
        Assert.False(doc.HasOpenUndoGroup);
        Assert.True(doc.IsDirty, "the text is 'abx' and the file holds 'ax'");
        Assert.True(app.Tab.IsDirty);

        await app.FocusAsync();
        app.Press(Key.Z, MacroApp.Ctrl);              // one Undo reverses the playback
        Assert.Equal("x", app.Text);
        Assert.True(doc.IsDirty);                     // the file holds "ax"
        app.Press(Key.Y, MacroApp.Ctrl);
        Assert.Equal("abx", app.Text);
        Assert.True(doc.IsDirty);

        // Closing asks (the change would be lost otherwise).
        var closing = app.Host.Get<ICommandService>().ExecuteAsync(KnownCommands.CloseActiveEditor).AsTask();
        var dialogs = app.Host.Get<DialogHostViewModel>();
        Assert.True(await RealAppHost.WaitFor(() => dialogs.IsVisible));
        Assert.Contains("Don't Save", dialogs.Buttons);
        dialogs.Choose("Cancel");
        await closing;
        Assert.NotNull(app.Well.ActiveEditor);
        Assert.Equal("ax", await File.ReadAllTextAsync(path));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Typing_at_the_same_caret_after_a_playback_that_ended_with_a_save_leaves_the_document_dirty()
    {
        await using var app = await MacroApp.StartAsync("x", seed: h => h.Home.WriteFile("data/macros.json", SaveMidway));
        Assert.True(await RealAppHost.WaitFor(() => app.Host.Get<ICommandService>().Has("macro.run.last")));
        var doc = app.View.Document;

        await app.Host.RunAsync("macro.run.last");
        Assert.Equal("cx", app.Text);
        Assert.False(doc.IsDirty);                    // saved as its last step
        await app.FocusAsync();
        app.Type("d");                                // the caret the playback left, the version of the save
        Assert.Equal("cdx", app.Text);
        Assert.True(doc.IsDirty);
        app.Press(Key.Z, MacroApp.Ctrl);              // the typed run is a step of its own: back to the saved text
        Assert.Equal("cx", app.Text);
        Assert.False(doc.IsDirty);
        app.Press(Key.Z, MacroApp.Ctrl);              // and the playback
        Assert.Equal("x", app.Text);
        Assert.True(doc.IsDirty);
    }
}
