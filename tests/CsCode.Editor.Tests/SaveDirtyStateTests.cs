using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Tests.Support;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// T50 (data loss): a save in the middle of a typed run. The typing path continues the run's undo step while the caret has not moved, and a save
/// does not change the version, so typing on after a save joined the step the file was saved after; AvaloniaEdit's saved-marker arithmetic for
/// such a continued group lands on "saved", the document reported clean while the text differed from the file, and closing it did not prompt.
/// A step never continues across a save now (VS Code also ends the undo stop at a save). Real headless views, real typing path, real file.
/// </summary>
public class SaveDirtyStateTests
{
    private static void Type(Avalonia.Controls.Window window, string text)
    {
        foreach (var c in text)
        {
            window.KeyTextInput(c.ToString());
            ViewHost.Pump(); // every character in a dispatcher turn of its own, as typed
        }
    }

    private static string Text(EditorDocument doc) => doc.GetText().Replace("\r\n", "\n");

    [AvaloniaFact]
    public async Task Typing_on_at_the_same_caret_after_a_save_leaves_the_document_dirty()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("", "a.txt");
        var path = host.Dir.Combine("a.txt");

        Type(window, "a");
        Assert.True(doc.IsDirty);
        await doc.SaveAsync();
        ViewHost.Pump();
        Assert.False(doc.IsDirty);
        Assert.Equal("a", File.ReadAllText(path));

        Type(window, "b"); // same caret, same version as the save: this used to continue the saved step
        Assert.Equal("ab", Text(doc));
        Assert.True(doc.IsDirty, "the text is 'ab' and the file holds 'a': closing must prompt");

        // The save ended the step: one Undo goes back to the saved text (clean), the next one to the empty start (dirty).
        view.Controller.Undo();
        Assert.Equal("a", Text(doc));
        Assert.False(doc.IsDirty);
        view.Controller.Undo();
        Assert.Equal("", Text(doc));
        Assert.True(doc.IsDirty);
        view.Controller.Redo();
        Assert.False(doc.IsDirty);
        view.Controller.Redo();
        Assert.Equal("ab", Text(doc));
        Assert.True(doc.IsDirty);
    }

    [AvaloniaFact]
    public async Task A_longer_run_split_by_saves_stays_honest_at_every_point()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("x\n", "a.txt");
        view.Controller.SetSelection(TextRange.At(0, 1));
        var path = host.Dir.Combine("a.txt");

        for (var i = 0; i < 3; i++)
        {
            Type(window, "yz");
            Assert.True(doc.IsDirty);
            await doc.SaveAsync();
            ViewHost.Pump();
            Assert.False(doc.IsDirty);
            Assert.Equal(Text(doc), File.ReadAllText(path));
        }
        Type(window, "!");
        Assert.True(doc.IsDirty);
        Assert.Equal("xyzyzyz!\n", Text(doc));

        // Typing without a save in between still continues the run: one step per saved run.
        Type(window, "?");
        view.Controller.Undo();
        Assert.Equal("xyzyzyz\n", Text(doc));
        Assert.False(doc.IsDirty);
        view.Controller.Undo();
        Assert.Equal("xyzyz\n", Text(doc));
        Assert.True(doc.IsDirty);
    }

    [AvaloniaFact]
    public async Task Typing_on_after_a_save_still_continues_the_run_when_no_save_came_between()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("", "a.txt");
        Type(window, "abc");
        Assert.True(doc.IsDirty);
        view.Controller.Undo();
        Assert.Equal("", Text(doc)); // one typed run, one step (the rule above did not split ordinary typing)
        Assert.False(doc.IsDirty);
    }

    [AvaloniaFact]
    public async Task A_continued_group_reports_whether_it_joined_the_last_step()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("", "a.txt");

        Assert.False(doc.BeginUndoGroup(continueLast: false));
        doc.ApplyEdits([new TextEdit(TextRange.At(0, 0), "a")]);
        doc.EndUndoGroup();
        Assert.True(doc.BeginUndoGroup(continueLast: true)); // nothing between: joins
        doc.ApplyEdits([new TextEdit(TextRange.At(0, 1), "b")]);
        doc.EndUndoGroup();

        await doc.SaveAsync();
        ViewHost.Pump();
        Assert.False(doc.IsDirty);
        Assert.False(doc.BeginUndoGroup(continueLast: true)); // the save ends the step: a step of its own
        doc.ApplyEdits([new TextEdit(TextRange.At(0, 2), "c")]);
        doc.EndUndoGroup();
        Assert.True(doc.IsDirty);
        Assert.True(doc.BeginUndoGroup(continueLast: true)); // and later changes join that one again
        doc.ApplyEdits([new TextEdit(TextRange.At(0, 3), "d")]);
        doc.EndUndoGroup();
        Assert.True(doc.IsDirty);

        doc.Undo();
        Assert.Equal("ab", Text(doc));
        Assert.False(doc.IsDirty);
        doc.Undo();
        Assert.Equal("", Text(doc));
        Assert.True(doc.IsDirty);
        _ = view;
        _ = window;
    }

    [AvaloniaFact]
    public async Task Inside_a_held_undo_group_a_save_also_ends_the_typed_run()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("", "a.txt");
        var c = view.Controller;

        var group = c.OpenUndoGroup(); // macro playback
        Type(window, "ab");
        await doc.SaveAsync();
        ViewHost.Pump();
        Assert.False(doc.IsDirty);
        Type(window, "cd");
        Assert.True(doc.IsDirty);
        c.Undo();                      // inside the group: only the run typed after the save
        Assert.Equal("ab", Text(doc));
        c.Undo();
        Assert.Equal("", Text(doc));
        c.Redo();
        c.Redo();
        Assert.Equal("abcd", Text(doc));
        group.Dispose();
        Assert.True(doc.IsDirty);      // the file holds "ab"
        c.Undo();                      // one step for the whole group
        Assert.Equal("", Text(doc));
        Assert.True(doc.IsDirty);
    }

    [AvaloniaFact]
    public async Task Cycling_the_clipboard_ring_after_a_save_leaves_the_document_dirty()
    {
        var clipboard = new InMemoryClipboardService();
        using var host = ViewHost.Create(s => s.AddSingleton<IClipboardService>(clipboard));
        var (doc, view, _) = await host.OpenAsync("one two\n\n", "a.txt");
        foreach (var (s, e) in new[] { (0, 3), (4, 7) })
        {
            view.Controller.SetSelection(TextRange.FromLines(0, s, 0, e));
            await view.Clipboard.CopyAsync();
        }
        view.Controller.SetSelection(TextRange.At(1, 0));

        await view.Clipboard.CycleRingAsync(); // pastes the newest entry
        ViewHost.Pump();
        Assert.Equal("one two\ntwo\n", Text(doc));
        Assert.True(doc.IsDirty);
        await doc.SaveAsync();
        ViewHost.Pump();
        Assert.False(doc.IsDirty);

        await view.Clipboard.CycleRingAsync(); // the next entry replaces the pasted one in place (the same undo step unless a save came between)
        ViewHost.Pump();
        Assert.Equal("one two\none\n", Text(doc));
        Assert.True(doc.IsDirty, "the file holds 'two'");
        view.Controller.Undo();
        Assert.Equal("one two\ntwo\n", Text(doc));
        Assert.False(doc.IsDirty);
    }
}
