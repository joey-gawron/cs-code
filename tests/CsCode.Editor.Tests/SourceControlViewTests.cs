using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using CsCode.Core.Lifecycle;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Editor.Diff;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>The diff editor, the gutter change markers (quick diff) and inline blame, in real views (docs/SOURCE-CONTROL.md).</summary>
public class SourceControlViewTests
{
    private sealed class FakeQuickDiff : IQuickDiffProvider
    {
        public readonly Emitter<Uri?> Changed = new();
        public string? Original { get; set; }
        public List<(Uri Uri, int Start, int End)> Staged { get; } = new();
        public ValueTask<QuickDiffOriginal?> GetOriginalAsync(Uri uri, CancellationToken ct = default) =>
            ValueTask.FromResult(Original is null ? null : new QuickDiffOriginal(Original, "Index"));
        public IEventSource<Uri?> OriginalChanged => Changed;
        public ValueTask<bool> StageRangeAsync(Uri uri, int startLine, int endLine, CancellationToken ct = default)
        {
            Staged.Add((uri, startLine, endLine));
            return ValueTask.FromResult(true);
        }
    }

    private sealed class FakeBlame : ILineBlameProvider
    {
        public readonly Emitter<object?> ChangedEmitter = new();
        public bool IsEnabled { get; set; } = true;
        public List<(int Line, string? Text)> Requests { get; } = new();
        public ValueTask<LineBlame?> GetLineBlameAsync(Uri uri, int line, string? documentText, CancellationToken ct = default)
        {
            Requests.Add((line, documentText));
            return ValueTask.FromResult<LineBlame?>(new LineBlame($"Jane Doe, 2 days ago • line {line}"));
        }
        public IEventSource<object?> Changed => ChangedEmitter;
    }

    // ------------------------------------------------------------------------------------- diff model

    [Fact]
    public void Diff_model_marks_lines_and_changed_characters_and_maps_lines_between_sides()
    {
        var m = DiffModel.Compute("a\nint count = 1;\nc\nd\n", "a\nint total = 1;\nc\nnew\nd\n", ignoreTrimWhitespace: false);
        Assert.Equal(2, m.Changes.Count);
        Assert.Equal(DiffLineKind.Removed, m.OriginalKinds[1]);
        Assert.Equal(DiffLineKind.Inserted, m.ModifiedKinds[1]);
        Assert.Equal(DiffLineKind.Inserted, m.ModifiedKinds[3]);
        Assert.Equal(DiffLineKind.None, m.OriginalKinds[3]);
        var o = Assert.Single(m.OriginalSpans[1]);
        Assert.Equal("count", "int count = 1;".Substring(o.Start, o.Length));
        var n = Assert.Single(m.ModifiedSpans[1]);
        Assert.Equal("total", "int total = 1;".Substring(n.Start, n.Length));
        // original line 3 ("d") is modified line 4; lines after an insertion shift
        Assert.Equal(4, m.MapLine(3, fromOriginal: true));
        Assert.Equal(3, m.MapLine(4, fromOriginal: false));
        Assert.Equal(0, m.MapLine(0, fromOriginal: true));
        Assert.Equal(0, m.NextChange(0, forward: true));
        Assert.Equal(1, m.NextChange(1, forward: true));
        Assert.Equal(0, m.NextChange(3, forward: true)); // wraps
        Assert.Equal(0, m.NextChange(3, forward: false));
    }

    [Fact]
    public void Inline_diff_puts_removed_lines_above_their_replacement_with_both_line_numbers()
    {
        var m = DiffModel.Compute("a\nb\nc", "a\nB\nc\nd", ignoreTrimWhitespace: false);
        var u = UnifiedDiff.From(m);
        Assert.Equal("a\nb\nB\nc\nd", u.Text);
        Assert.Equal([DiffLineKind.None, DiffLineKind.Removed, DiffLineKind.Inserted, DiffLineKind.None, DiffLineKind.Inserted], u.Kinds);
        Assert.Equal([1, 2, null, 3, null], u.OriginalNumbers);
        Assert.Equal([1, null, 2, 3, 4], u.ModifiedNumbers);
        Assert.Equal([1, 4], u.ChangeStarts);
    }

    [Fact]
    public void Whitespace_only_changes_are_ignored_when_asked()
    {
        Assert.Single(DiffModel.Compute("  a\n", "a\n", ignoreTrimWhitespace: false).Changes);
        Assert.True(DiffModel.Compute("  a\n", "a\n", ignoreTrimWhitespace: true).IsIdentical);
    }

    // ------------------------------------------------------------------------------------- diff editor

    [AvaloniaFact]
    public async Task Diff_editor_shows_two_read_only_coloured_panes_and_steps_through_changes()
    {
        using var host = ViewHost.Create();
        var original = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"int x{i} = {i};"));
        var lines = Enumerable.Range(1, 200).Select(i => $"int x{i} = {i};").ToList();
        lines[10] = "int x11 = 1100;";
        lines.Insert(150, "// new line");
        var request = new DiffEditorRequest(new DiffSide("a.cs (Index)", Text: original), new DiffSide("a.cs (Working Tree)", Text: string.Join('\n', lines)), "a.cs", "a.cs");
        var diff = (DiffEditorControl)host.Factory.CreateDiffView(request)!;
        var window = new Window { Width = 1000, Height = 500, Content = diff };
        window.Show();
        await ViewHost.WaitFor(() => diff.Model is not null && diff.OriginalView is not null, "diff computed");
        ViewHost.Pump();

        Assert.True(diff.IsSideBySide);
        Assert.Equal(2, diff.Model!.Changes.Count);
        Assert.Equal("2 changes", diff.Summary);
        Assert.True(diff.OriginalView!.Document.IsReadOnly);
        Assert.True(diff.ModifiedView!.Editor.IsReadOnly);
        Assert.Equal("csharp", diff.ModifiedView.Document.LanguageId); // coloured as the hinted file
        Assert.Equal(original, diff.OriginalView.Document.GetText());

        Assert.Equal(0, diff.GoToChange(forward: true));
        Assert.Equal(11, diff.ModifiedView.TextArea.Caret.Line);
        Assert.Equal(1, diff.GoToChange(forward: true));
        Assert.Equal(151, diff.ModifiedView.TextArea.Caret.Line);
        Assert.Equal(151, diff.OriginalView.TextArea.Caret.Line); // the original side's caret follows to the same change
        Assert.Equal(0, diff.GoToChange(forward: true)); // wraps
        Assert.Equal(1, diff.GoToChange(forward: false));

        // scrolling one pane scrolls the other to the corresponding line
        ((Avalonia.Controls.Primitives.IScrollable)diff.ModifiedView.TextArea).Offset = new Avalonia.Vector(0, diff.ModifiedView.TextView.DefaultLineHeight * 170);
        int LeftTop() => diff.OriginalView.TextView.GetDocumentLineByVisualTop(diff.OriginalView.TextView.ScrollOffset.Y).LineNumber;
        await ViewHost.WaitFor(() => LeftTop() > 100, "the original pane follows");
        Assert.InRange(LeftTop(), 168, 171); // one line was inserted above line 170 on the right
        window.Close();
        diff.Dispose();
    }

    [AvaloniaFact]
    public async Task Diff_editor_inline_mode_and_a_file_side_that_follows_edits()
    {
        using var host = ViewHost.Create();
        var (doc, _, _) = await host.OpenAsync("one\ntwo\nthree\n", "f.txt");
        var request = new DiffEditorRequest(new DiffSide("f.txt (saved)", Text: "one\ntwo\nthree\n"), new DiffSide("f.txt", Uri: doc.Uri), "f.txt", doc.Uri.ToString());
        var diff = (DiffEditorControl)host.Factory.CreateDiffView(request)!;
        var window = new Window { Width = 900, Height = 400, Content = diff };
        window.Show();
        await ViewHost.WaitFor(() => diff.Model is not null, "diff computed");
        Assert.Equal("Files are identical", diff.Summary);

        doc.ApplyEdits([new TextEdit(new TextRange(new TextPosition(1, 0), new TextPosition(1, 3)), "TWO")]);
        await ViewHost.WaitFor(() => diff.Model?.Changes.Count == 1, "diff follows the document");

        host.Config.Set(DiffEditorControl.SideBySideSetting, false);
        var inline = (DiffEditorControl)host.Factory.CreateDiffView(request)!;
        window.Content = inline;
        await ViewHost.WaitFor(() => inline.Model is not null && inline.InlineView?.Document.GetText().Contains("TWO") == true, "inline computed");
        Assert.False(inline.IsSideBySide);
        Assert.Equal("one\ntwo\nTWO\nthree\n", inline.InlineView!.Document.GetText());
        Assert.False(inline.InlineView.Editor.ShowLineNumbers); // its own two-column numbers instead
        Assert.Equal(0, inline.GoToChange(forward: true));
        Assert.Equal(2, inline.InlineView.TextArea.Caret.Line);
        window.Close();
        diff.Dispose();
        inline.Dispose();
    }

    [AvaloniaFact]
    public async Task A_diff_without_an_original_is_one_read_only_document()
    {
        using var host = ViewHost.Create();
        var diff = (DiffEditorControl)host.Factory.CreateDiffView(new DiffEditorRequest(new DiffSide(""), new DiffSide("Blame: a.cs", Text: "abc 1\nabc 2"), "Blame", "blame.txt"))!;
        var window = new Window { Width = 600, Height = 300, Content = diff };
        window.Show();
        await ViewHost.WaitFor(() => diff.Model is not null && diff.InlineView?.Document.GetText() == "abc 1\nabc 2", "shown");
        Assert.True(diff.IsSingle);
        Assert.Equal("", diff.Summary);
        Assert.True(diff.InlineView!.Editor.IsReadOnly);
        window.Close();
        diff.Dispose();
    }

    [AvaloniaFact]
    public async Task Closing_a_diff_releases_its_views_and_documents()
    {
        using var host = ViewHost.Create();
        var before = host.Factory.Views.Count;
        var diff = (DiffEditorControl)host.Factory.CreateDiffView(new DiffEditorRequest(new DiffSide("a", Text: "x"), new DiffSide("b", Text: "y"), "t"))!;
        Assert.Equal(before + 2, host.Factory.Views.Count);
        diff.Dispose();
        Assert.Equal(before, host.Factory.Views.Count);
    }

    // ------------------------------------------------------------------------------------- gutter markers

    [AvaloniaFact]
    public async Task Gutter_markers_follow_the_original_and_the_edits()
    {
        var quick = new FakeQuickDiff { Original = "a\nb\nc\nd\n" };
        using var host = ViewHost.Create(s => s.AddSingleton<IQuickDiffProvider>(quick));
        var (doc, view, _) = await host.OpenAsync("a\nB\nc\nnew\nd\n", "g.txt");
        var feature = view.Feature<QuickDiffFeature>()!;
        Assert.NotNull(feature);
        await ViewHost.WaitFor(() => feature.Changes.Count == 2, "markers computed");
        Assert.Equal("Modified@1+1,Added@3+1", QuickDiffText.Describe(feature.Changes));
        Assert.Equal(0, feature.ChangeAt(1));
        Assert.Equal(1, feature.ChangeAt(3));
        Assert.Equal(-1, feature.ChangeAt(0));

        // deleting a line shows a deleted marker
        doc.ApplyEdits([new TextEdit(new TextRange(new TextPosition(2, 0), new TextPosition(3, 0)), "")]);
        await ViewHost.WaitFor(() => QuickDiffText.Describe(feature.Changes) == "Modified@1+2", "markers follow the edit");

        // the original moves (staged): markers recomputed
        quick.Original = "a\nB\nnew\nd\n";
        quick.Changed.Fire(null);
        await ViewHost.WaitFor(() => feature.Changes.Count == 0, "no changes against the new original");

        // no original (untracked): no markers; turning them off removes them
        quick.Original = "x\n";
        quick.Changed.Fire(doc.Uri);
        await ViewHost.WaitFor(() => feature.Changes.Count > 0, "against x");
        host.Config.Set(QuickDiffFeature.Setting, "none");
        await ViewHost.WaitFor(() => feature.Changes.Count == 0, "markers off");
    }

    [AvaloniaFact]
    public async Task Clicking_a_marker_peeks_the_original_and_revert_and_stage_act_on_that_change_only()
    {
        var quick = new FakeQuickDiff { Original = "a\nb\nc\nd\ne\n" };
        using var host = ViewHost.Create(s => s.AddSingleton<IQuickDiffProvider>(quick));
        var (doc, view, _) = await host.OpenAsync("a\nB\nc\nd\nE\n", "r.txt");
        var feature = view.Feature<QuickDiffFeature>()!;
        await ViewHost.WaitFor(() => feature.Changes.Count == 2, "markers computed");

        feature.ShowPeek(0);
        ViewHost.Pump();
        Assert.True(feature.IsPeekOpen, "peek open");
        var texts = feature.PeekRoot.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("- b", texts);
        Assert.Contains(texts, t => t?.Contains("change 1 of 2", StringComparison.Ordinal) == true);

        Assert.True(feature.Revert(0), "first revert");
        Assert.False(feature.IsPeekOpen);
        Assert.Equal("a\nb\nc\nd\nE\n", doc.GetText());
        await ViewHost.WaitFor(() => feature.Changes.Count == 1, "one change left");

        // stage the remaining change through the provider, with its document lines
        feature.ShowPeek(0);
        ViewHost.Pump();
        var stage = feature.PeekRoot.GetLogicalDescendants().OfType<Button>().Single(b => (b.Content as string) == "Stage");
        stage.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await ViewHost.WaitFor(() => quick.Staged.Count == 1, "staged");
        Assert.Equal((doc.Uri, 4, 4), quick.Staged[0]);

        // reverting added lines and deleted lines restores the original exactly
        quick.Original = "1\n2\n3\n";
        quick.Changed.Fire(null);
        doc.ApplyEdits([new TextEdit(new TextRange(TextPosition.Zero, new TextPosition(doc.LineCount - 1, 0)), "1\n2\nadded\n3\n")]);
        await ViewHost.WaitFor(() => QuickDiffText.Describe(feature.Changes) == "Added@2+1", "added line");
        Assert.True(feature.Revert(0), "revert added");
        Assert.Equal("1\n2\n3\n", doc.GetText());
        doc.ApplyEdits([new TextEdit(new TextRange(new TextPosition(1, 0), new TextPosition(2, 0)), "")]);
        await ViewHost.WaitFor(() => QuickDiffText.Describe(feature.Changes) == "Deleted@1+0", "deleted line");
        Assert.True(feature.Revert(0), "revert deleted");
        Assert.Equal("1\n2\n3\n", doc.GetText());
        await ViewHost.WaitFor(() => feature.Changes.Count == 0, "nothing left");
        Assert.Equal(-1, feature.GoToChange(forward: true));
    }

    [AvaloniaFact]
    public async Task Gutter_markers_exist_only_for_files_and_with_a_provider()
    {
        using var plain = ViewHost.Create();
        var (_, view, _) = await plain.OpenAsync("a\n", "p.txt");
        Assert.Null(view.Feature<QuickDiffFeature>());
        Assert.Null(view.Feature<InlineBlameFeature>());

        var quick = new FakeQuickDiff { Original = "a\n" };
        using var host = ViewHost.Create(s => s.AddSingleton<IQuickDiffProvider>(quick));
        var untitled = (Documents.EditorDocument)host.Factory.CreateUntitled();
        var untitledView = (View.CodeEditorControl)host.Factory.CreateView(untitled);
        Assert.Null(untitledView.Feature<QuickDiffFeature>());
        untitledView.Dispose();
    }

    // ------------------------------------------------------------------------------------- inline blame

    [AvaloniaFact]
    public async Task Inline_blame_annotates_the_caret_line_after_it_rests_and_follows_the_setting()
    {
        var blame = new FakeBlame();
        InlineBlameFeature.Delay = TimeSpan.FromMilliseconds(30);
        using var host = ViewHost.Create(s => s.AddSingleton<ILineBlameProvider>(blame));
        var (doc, view, _) = await host.OpenAsync("one\ntwo\nthree\n", "b.txt");
        var feature = view.Feature<InlineBlameFeature>()!;
        await ViewHost.WaitFor(() => feature.Current is not null, "first annotation");
        Assert.Equal(0, feature.Current!.Value.Line);
        Assert.Null(blame.Requests[0].Text); // saved document: blame runs on the file

        view.TextArea.Caret.Line = 3;
        await ViewHost.WaitFor(() => feature.Current?.Line == 2, "annotation follows the caret");
        Assert.Equal("Jane Doe, 2 days ago • line 2", feature.Current!.Value.Blame.Text);

        doc.ApplyEdits([new TextEdit(new TextRange(new TextPosition(2, 0), new TextPosition(2, 0)), "x")]);
        view.TextArea.Caret.Line = 2;
        await ViewHost.WaitFor(() => feature.Current?.Line == 1, "after an edit");
        Assert.Equal("one\ntwo\nxthree\n", blame.Requests[^1].Text); // unsaved: the editor's text goes along

        blame.IsEnabled = false;
        blame.ChangedEmitter.Fire(null);
        await ViewHost.WaitFor(() => feature.Current is null, "turned off");
        var count = blame.Requests.Count;
        view.TextArea.Caret.Line = 1;
        await Task.Delay(100);
        ViewHost.Pump();
        Assert.Equal(count, blame.Requests.Count);
    }
}
