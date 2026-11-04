using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Editor.Diff;
using CsCode.Editor.Documents;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>The 3-way merge editor: panes, the result's reset and anchors, checkboxes (lenses), buttons, navigation, completion, disposal.</summary>
public class MergeEditorViewTests
{
    private const string Base = "head\nshared 1\nbase A\nshared 2\nshared 3\nbase B\ntail\n";
    private const string Ours = "head\nshared 1\nours A\nshared 2\nshared 3\nours B\ntail\n";
    private const string Theirs = "head changed by them\nshared 1\ntheirs A\nshared 2\nshared 3\ntheirs B\ntail\n";
    // what git writes into the working file
    private const string Markers = "head changed by them\nshared 1\n<<<<<<< HEAD\nours A\n=======\ntheirs A\n>>>>>>> topic\nshared 2\nshared 3\n<<<<<<< HEAD\nours B\n=======\ntheirs B\n>>>>>>> topic\ntail\n";

    private sealed record Open(ViewHost Host, EditorDocument Doc, MergeEditorControl Editor, Window Window, List<string> Completions) : IDisposable
    {
        public void Dispose()
        {
            Window.Close();
            Editor.Dispose();
            Host.Dispose();
        }
    }

    private static async Task<Open> OpenAsync(string fileText = Markers)
    {
        var host = ViewHost.Create();
        var path = host.Dir.WriteFile("m.txt", fileText);
        var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(path));
        var completions = new List<string>();
        var request = new MergeEditorRequest(doc.Uri, Base, Ours, Theirs, "Merging: m.txt")
        {
            CurrentLabel = "HEAD",
            IncomingLabel = "topic",
            Complete = _ => { completions.Add(File.ReadAllText(path)); return Task.CompletedTask; },
        };
        var editor = (MergeEditorControl)host.Factory.CreateMergeView(request, doc)!;
        var window = new Window { Width = 1000, Height = 800, Content = editor };
        window.Show();
        await editor.Ready;
        ViewHost.Pump();
        return new Open(host, doc, editor, window, completions);
    }

    private const string Reset = "head changed by them\nshared 1\nbase A\nshared 2\nshared 3\nbase B\ntail\n";

    [AvaloniaFact]
    public async Task A_file_with_markers_starts_as_the_base_with_the_merged_changes_and_the_inputs_show_both_sides()
    {
        using var o = await OpenAsync();
        Assert.Equal(Reset, o.Doc.GetText()); // their head change merged by itself; the conflicts hold the base
        Assert.Equal(2, o.Editor.Merge!.Conflicts.Count);
        Assert.Equal("2 Conflicts Remaining", o.Editor.CountText);
        Assert.Equal(Theirs, o.Editor.IncomingPane!.Document.GetText());
        Assert.Equal(Ours, o.Editor.CurrentPane!.Document.GetText());
        Assert.Same(o.Doc, o.Editor.ResultPane!.Document);
        Assert.Equal(0, o.Editor.FocusedConflict); // the first unhandled conflict is shown
        Assert.Equal((2, 3), o.Editor.ResultLines(0));
        Assert.Equal((5, 6), o.Editor.ResultLines(1));
        // the reset is one undo step
        o.Doc.Undo();
        Assert.Equal(Markers, o.Doc.GetText());
    }

    [AvaloniaFact]
    public async Task Checkboxes_take_sides_in_the_order_taken_each_as_one_undo_step_and_undo_is_read_back()
    {
        using var o = await OpenAsync();
        o.Editor.Toggle(0, MergeSides.Incoming);
        Assert.Equal("head changed by them\nshared 1\ntheirs A\nshared 2\nshared 3\nbase B\ntail\n", o.Doc.GetText());
        Assert.Equal("1 Conflict Remaining", o.Editor.CountText);
        o.Editor.Toggle(0, MergeSides.Current);
        Assert.Equal("head changed by them\nshared 1\ntheirs A\nours A\nshared 2\nshared 3\nbase B\ntail\n", o.Doc.GetText()); // incoming first: taken first
        Assert.Equal((2, 4), o.Editor.ResultLines(0)); // the anchors grew with it
        Assert.Equal((6, 7), o.Editor.ResultLines(1));
        o.Doc.Undo();
        Assert.Equal(MergeSides.Incoming, o.Editor.Resolution!.Taken(0)); // read back from the text
        o.Doc.Undo();
        Assert.Equal(Reset, o.Doc.GetText());
        Assert.False(o.Editor.Resolution.IsHandled(0));
        Assert.Equal("2 Conflicts Remaining", o.Editor.CountText);
    }

    [AvaloniaFact]
    public async Task The_input_panes_have_a_checkbox_lens_per_conflict_that_toggles_its_side()
    {
        using var o = await OpenAsync();
        var lenses = o.Editor.IncomingPane!.Feature<CodeLensFeature>()!;
        await ViewHost.WaitFor(() => lenses.Placements.Count == 2, "incoming lenses");
        Assert.Equal([(2, "☐ Accept Incoming"), (5, "☐ Accept Incoming")], lenses.Placements.ToArray());
        await lenses.ClickAsync(5, 0);
        Assert.Equal("head changed by them\nshared 1\nbase A\nshared 2\nshared 3\ntheirs B\ntail\n", o.Doc.GetText());
        await ViewHost.WaitFor(() => lenses.Placements.Contains((5, "☑ Accept Incoming")), "checked");
        var current = o.Editor.CurrentPane!.Feature<CodeLensFeature>()!;
        await ViewHost.WaitFor(() => current.Placements.Count == 2, "current lenses");
        Assert.Equal((2, "☐ Accept Current"), current.Placements[0]);
        // the result's own document gets no merge-conflict lenses (no markers after the reset)
        Assert.Empty(o.Editor.ResultPane!.Feature<CodeLensFeature>()!.Placements);
    }

    [AvaloniaFact]
    public async Task Typing_inside_a_conflict_handles_it_by_hand_and_typing_outside_moves_it()
    {
        using var o = await OpenAsync();
        o.Doc.ApplyEdits([new TextEdit(TextRange.At(0, 0), "new first line\n")]);
        Assert.Equal((3, 4), o.Editor.ResultLines(0));
        o.Doc.ApplyEdits([new TextEdit(new TextRange(new TextPosition(3, 0), new TextPosition(3, 6)), "both A")]);
        Assert.True(o.Editor.Resolution!.IsHandled(0));
        Assert.Equal(MergeSides.None, o.Editor.Resolution.Taken(0));
        Assert.Equal("1 Conflict Remaining", o.Editor.CountText);
    }

    [AvaloniaFact]
    public async Task Navigation_and_the_header_buttons_act_on_the_conflict_at_the_result_caret()
    {
        using var o = await OpenAsync();
        Assert.Equal(1, o.Editor.GoToConflict(forward: true));
        Assert.Equal(5, o.Editor.ResultPane!.Controller.Caret.Line);
        Assert.Equal(0, o.Editor.GoToConflict(forward: true)); // wraps
        Assert.Equal(1, o.Editor.GoToConflict(forward: false)); // wraps back
        o.Editor.Set(o.Editor.FocusedConflict, MergeSides.Both);
        Assert.Equal("head changed by them\nshared 1\nbase A\nshared 2\nshared 3\nours B\ntheirs B\ntail\n", o.Doc.GetText());
        // the panes line up on the conflicts: the second conflict's start maps between all three
        Assert.Equal(5, o.Editor.MapLine(5, 2, 0));
        Assert.Equal(5, o.Editor.MapLine(5, 0, 1));
        Assert.Equal(7, o.Editor.MapLine(6, 1, 2)); // the line after "ours B" is after both taken lines in the result
    }

    [AvaloniaFact]
    public async Task Complete_merge_asks_while_conflicts_are_left_then_saves_and_runs_the_completion()
    {
        using var o = await OpenAsync();
        var completed = 0;
        using var sub = o.Editor.Completed.Subscribe(_ => completed++);
        o.Host.Window.MessageActionResponses.Enqueue("Cancel");
        Assert.False(await o.Editor.CompleteAsync());
        Assert.Contains(o.Host.Window.Messages, m => m.Message.Contains("unhandled conflicts", StringComparison.Ordinal));
        Assert.Empty(o.Completions);
        o.Editor.Set(0, MergeSides.Current);
        o.Editor.Set(1, MergeSides.Incoming);
        Assert.True(await o.Editor.CompleteAsync());
        var merged = "head changed by them\nshared 1\nours A\nshared 2\nshared 3\ntheirs B\ntail\n";
        Assert.Equal([merged], o.Completions); // saved before the completion ran
        Assert.False(o.Doc.IsDirty);
        Assert.Equal(1, completed);
    }

    [AvaloniaFact]
    public async Task Closing_an_untouched_merge_editor_gives_the_file_its_markers_back()
    {
        using var o = await OpenAsync();
        Assert.True(o.Doc.IsDirty);
        o.Editor.Dispose();
        Assert.Equal(Markers, o.Doc.GetText());
        Assert.False(o.Doc.IsDirty);
    }

    [AvaloniaFact]
    public async Task A_result_without_markers_is_kept_and_each_conflicts_state_is_read_from_its_text()
    {
        // resolved by hand already: the first conflict as ours, the second as both (theirs first), plus a line added elsewhere
        var text = "head changed by them\nshared 1\nours A\nshared 2\nadded by hand\nshared 3\ntheirs B\nours B\ntail\n";
        using var o = await OpenAsync(text);
        Assert.Equal(text, o.Doc.GetText());
        Assert.False(o.Doc.IsDirty);
        Assert.Equal(MergeSides.Current, o.Editor.Resolution!.Taken(0));
        Assert.Equal((MergeSides.Both, true), (o.Editor.Resolution.Taken(1), o.Editor.Resolution.IncomingFirst(1)));
        Assert.Equal((6, 8), o.Editor.ResultLines(1));
        Assert.Equal("No Remaining Conflicts", o.Editor.CountText);
    }

    [AvaloniaFact]
    public async Task A_closed_merge_editor_releases_its_panes_and_documents()
    {
        var host = ViewHost.Create();
        try
        {
            var path = host.Dir.WriteFile("leak.txt", Markers);
            var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(path));
            var before = host.Factory.Views.Count;
            await LeakAssert.NotRetainedAsync(async () =>
            {
                var editor = (MergeEditorControl)host.Factory.CreateMergeView(new MergeEditorRequest(doc.Uri, Base, Ours, Theirs, "m"), doc)!;
                var window = new Window { Width = 800, Height = 600, Content = editor };
                window.Show();
                await editor.Ready;
                ViewHost.Pump();
                Assert.Equal(before + 3, host.Factory.Views.Count);
                var panes = new object[] { editor, editor.IncomingPane!, editor.CurrentPane!, editor.ResultPane!, editor.IncomingPane!.Document };
                window.Close();
                editor.Dispose();
                ViewHost.Pump();
                return panes;
            });
            Assert.Equal(before, host.Factory.Views.Count);
            Assert.Empty(host.Factory.MergeEditors);
        }
        finally { host.Dispose(); }
    }
}
