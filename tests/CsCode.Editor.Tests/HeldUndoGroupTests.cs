using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// Undo groups held open across dispatcher callbacks (<c>IEditorViewController.OpenUndoGroup</c>, <c>EditorDocument.OpenUndoGroup</c>): what macro
/// playback uses to make one Undo reverse a whole playback (docs/MACROS.md). Real headless views, real typing path, real AvaloniaEdit undo stack.
/// </summary>
public class HeldUndoGroupTests
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
    public async Task Typing_keys_and_edits_over_many_dispatcher_turns_become_one_undo_step()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("hello\nworld", "a.txt");
        var c = view.Controller;
        c.SetSelection(TextRange.At(0, 5));

        var group = c.OpenUndoGroup();
        Type(window, " there");
        Assert.True(c.ReplayKey(Key.Enter, KeyModifiers.None));
        ViewHost.Pump();
        Type(window, "x");
        Assert.True(c.ReplayKey(Key.Back, KeyModifiers.None));
        doc.ApplyEdits([new TextEdit(TextRange.At(2, 0), "// ")]); // a command's edit
        ViewHost.Pump();
        Type(window, "!");
        group.Dispose();
        var after = Text(doc);
        Assert.Equal("hello there\n!\n// world", after);
        Assert.False(doc.HasOpenUndoGroup);

        c.Undo();
        Assert.Equal("hello\nworld", Text(doc)); // ONE undo step
        c.Redo();
        Assert.Equal(after, Text(doc));
        c.Undo();
        Assert.False(doc.CanUndo); // nothing else was recorded
    }

    [AvaloniaFact]
    public async Task Undo_inside_the_group_reverses_its_own_steps_as_the_editor_groups_them_and_never_throws()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("", "a.txt");
        var c = view.Controller;
        Type(window, "x"); // history from before the group: one typed run

        var group = c.OpenUndoGroup();
        Type(window, "abc");                                   // one typed run (it does not continue "x": the group started in between)
        Assert.True(c.ReplayKey(Key.Back, KeyModifiers.None)); // one step: removes "c"
        Type(window, "de");                                    // a new typed run
        Assert.Equal("xabde", Text(doc));

        Assert.True(c.ReplayKey(Key.Z, KeyModifiers.Control)); // AvaloniaEdit's own Ctrl+Z on the text area
        Assert.Equal("xab", Text(doc));
        Assert.Equal(new TextPosition(0, 3), c.Caret);
        c.Undo();                                              // the controller (menu, toolbar, the "undo" command)
        Assert.Equal("xabc", Text(doc));
        Assert.Equal(new TextPosition(0, 4), c.Caret);
        doc.Undo();                                            // the document (what a modal layer calls)
        Assert.Equal("x", Text(doc));
        Assert.False(c.CanUndo);
        c.Undo();                                              // nothing of the group's left: the history before it is not touched
        Assert.True(c.ReplayKey(Key.Z, KeyModifiers.Control));
        Assert.Equal("x", Text(doc));

        Assert.True(c.ReplayKey(Key.Y, KeyModifiers.Control)); // redo inside the group
        Assert.Equal("xabc", Text(doc));
        c.Redo();
        Assert.Equal("xab", Text(doc));
        Type(window, "q");                                     // a new change drops what is left to redo
        Assert.False(c.CanRedo);
        Assert.Equal("xabq", Text(doc));

        group.Dispose();
        c.Undo();
        Assert.Equal("x", Text(doc));                          // the text as the group found it, in one step
        c.Undo();
        Assert.Equal("", Text(doc));                           // and the history before it is intact
    }

    [AvaloniaFact]
    public async Task Typing_right_before_the_group_does_not_continue_into_it()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("", "a.txt");
        Type(window, "ab");
        using (view.Controller.OpenUndoGroup()) Type(window, "cd"); // same caret, same version: would continue "ab" without the group
        Type(window, "ef");                                          // and typing after it does not join it either
        view.Controller.Undo();
        Assert.Equal("abcd", Text(doc));
        view.Controller.Undo();
        Assert.Equal("ab", Text(doc));
        view.Controller.Undo();
        Assert.Equal("", Text(doc));
    }

    [AvaloniaFact]
    public async Task StartNewUndoStep_ends_the_typed_run_without_a_group_and_inside_one()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("", "a.txt");
        var c = view.Controller;
        Type(window, "ab");
        c.StartNewUndoStep();
        Type(window, "cd");          // same caret, same version: one typed run with "ab" without the mark
        c.Undo();
        Assert.Equal("ab", Text(doc));

        using (c.OpenUndoGroup())
        {
            Type(window, "ef");
            c.StartNewUndoStep();
            Type(window, "gh");
            c.Undo();
            Assert.Equal("abef", Text(doc));
            c.Undo();
            Assert.Equal("ab", Text(doc));
        }
    }

    [AvaloniaFact]
    public async Task Handles_of_one_document_nest_and_the_group_closes_with_the_last()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("0", "a.txt");
        var second = host.Factory.CreateView(doc); // a split: two views, one document, one undo stack
        var c2 = host.Factory.GetController(second)!;
        view.Controller.SetSelection(TextRange.At(0, 1));

        var first = view.Controller.OpenUndoGroup();
        var nested = c2.OpenUndoGroup();
        Type(window, "1");
        first.Dispose();
        first.Dispose(); // twice: harmless
        Assert.True(doc.HasOpenUndoGroup);
        doc.ApplyEdits([new TextEdit(TextRange.At(0, 2), "2")]);
        nested.Dispose();
        Assert.False(doc.HasOpenUndoGroup);

        Assert.Equal("012", Text(doc));
        c2.Undo();
        Assert.Equal("0", Text(doc));
        host.Factory.DisposeView(second);
    }

    [AvaloniaFact]
    public async Task A_clean_document_turns_dirty_at_the_first_held_change_and_saves_inside_the_group_stay_honest()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("abc", "a.txt");
        var c = view.Controller;
        c.SetSelection(TextRange.At(0, 3));
        var dirtyEvents = 0;
        using var sub = doc.DirtyChanged.Subscribe(_ => dirtyEvents++);

        var group = c.OpenUndoGroup();
        Type(window, "d");
        Assert.True(doc.IsDirty);            // at once, not when the group closes (a close prompt or Save must see it)
        Assert.Equal(1, dirtyEvents);
        await doc.SaveAsync();
        ViewHost.Pump();
        Assert.False(doc.IsDirty);
        Assert.Equal("abcd", File.ReadAllText(host.Dir.Combine("a.txt")));
        Type(window, "e");
        Assert.True(doc.IsDirty);
        group.Dispose();
        Assert.True(doc.IsDirty);            // the saved text is "abcd", this is "abcde"
        c.Undo();
        Assert.Equal("abc", Text(doc));
        Assert.True(doc.IsDirty);            // the saved state was inside the undone step: no undo or redo reaches it
        c.Redo();
        Assert.True(doc.IsDirty);

        // a save as the last thing inside the group: clean when it closes
        group = c.OpenUndoGroup();
        Type(window, "f");
        await doc.SaveAsync();
        ViewHost.Pump();
        group.Dispose();
        Assert.False(doc.IsDirty);
        c.Undo();
        Assert.True(doc.IsDirty);
    }

    [AvaloniaFact]
    public async Task Closing_the_document_closes_its_held_group()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("abc", "a.txt");
        var group = view.Controller.OpenUndoGroup();
        Type(window, "x");
        window.Content = null;
        host.Factory.DisposeView(view);
        await host.Factory.CloseDocumentAsync(doc);
        Assert.False(doc.HasOpenUndoGroup);
        group.Dispose(); // after the document is gone: nothing happens
        Assert.False(doc.HasOpenUndoGroup);
    }

    [AvaloniaFact]
    public async Task A_reload_from_disk_keeps_the_group_open_from_the_reloaded_text()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("disk", "a.txt");
        var c = view.Controller;
        var group = c.OpenUndoGroup();
        File.WriteAllText(host.Dir.Combine("a.txt"), "changed");
        await doc.ReloadAsync(); // AvaloniaEdit's history is cleared: with the group open that would throw
        ViewHost.Pump();
        Assert.Equal("changed", Text(doc));
        Assert.True(doc.HasOpenUndoGroup);
        c.SetSelection(TextRange.At(0, 7));
        Type(window, "!");
        group.Dispose();
        c.Undo();
        Assert.Equal("changed", Text(doc));
        Assert.False(doc.CanUndo);
    }

    [AvaloniaFact]
    public async Task A_direct_undo_of_the_buffer_with_the_group_open_does_not_break_closing_it()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("", "a.txt");
        var group = view.Controller.OpenUndoGroup();
        Type(window, "ab");
        // code outside the editor's API: AvaloniaEdit throws and drops its open groups
        Assert.Throws<InvalidOperationException>(() => doc.Buffer.UndoStack.Undo());
        group.Dispose(); // must not throw
        Assert.False(doc.HasOpenUndoGroup);
        Type(window, "c");
        view.Controller.Undo();
        Assert.Equal("ab", Text(doc));
    }

    [AvaloniaFact]
    public async Task Opening_off_the_ui_thread_is_refused_and_closing_off_it_is_marshalled()
    {
        using var host = ViewHost.Create();
        var (doc, _, window) = await host.OpenAsync("", "a.txt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => doc.OpenUndoGroup()));
        var group = doc.OpenUndoGroup();
        Type(window, "a");
        await Task.Run(group.Dispose); // closes on the UI thread (posted)
        ViewHost.Pump();
        Assert.False(doc.HasOpenUndoGroup);
        Type(window, "b");
        doc.Undo();
        Assert.Equal("a", Text(doc));
    }

    [AvaloniaFact]
    public async Task A_view_destroyed_while_its_document_holds_a_group_is_released()
    {
        using var host = ViewHost.Create();
        var window = new Avalonia.Controls.Window { Width = 600, Height = 400 };
        window.Show();
        var path = host.Dir.WriteFile("leak.txt", "abc");
        var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(path));
        var weak = await HoldAndDestroyAsync(host, window, doc);
        for (var attempt = 0; attempt < 60 && weak.Any(w => w.IsAlive); attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            ViewHost.Pump();
            await Task.Delay(20);
        }
        var alive = weak.FirstOrDefault(w => w.IsAlive)?.Target;
        Assert.True(alive is null, alive is null ? "" : "still reachable: " + alive.GetType().Name + "\n" +
            RetentionPath.Find([host.Provider, host.Factory, host.Documents, doc, Avalonia.Threading.Dispatcher.UIThread, Avalonia.Application.Current!], alive));
        Assert.Equal("abcxy", Text(doc));
        doc.Undo();
        Assert.Equal("abc", Text(doc));
        window.Close();
        await host.Factory.CloseDocumentAsync(doc);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Task<List<WeakReference>> HoldAndDestroyAsync(ViewHost host, Avalonia.Controls.Window window, EditorDocument doc)
    {
        var weak = new List<WeakReference>();
        var view = host.Factory.CreateView(doc);
        window.Content = view;
        ViewHost.Pump();
        var controller = host.Factory.GetController(view)!;
        controller.SetSelection(TextRange.At(0, 3));
        var group = controller.OpenUndoGroup();
        Assert.True(controller.ReplayText("x"));
        Assert.True(controller.ReplayKey(Key.Z, KeyModifiers.Control)); // a step undone inside the group: the redo list holds it
        Assert.True(controller.ReplayKey(Key.Y, KeyModifiers.Control));
        window.Content = null;
        weak.Add(new WeakReference(view));
        host.Factory.DisposeView(view);
        doc.ApplyEdits([new TextEdit(TextRange.At(0, 4), "y")]); // the group outlives the view
        group.Dispose();
        Assert.False(doc.HasOpenUndoGroup);
        // AvaloniaEdit's static RoutedCommand keeps the last focused text area until focus moves (docs/EDITOR.md): move it
        var other = new Avalonia.Controls.TextBox();
        window.Content = other;
        ViewHost.Pump();
        other.Focus();
        ViewHost.Pump();
        window.Content = null;
        return Task.FromResult(weak);
    }
}
