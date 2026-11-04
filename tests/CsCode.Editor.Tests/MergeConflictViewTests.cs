using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using CsCode.Editor.View.Decorations;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests;

/// <summary>Inline merge conflicts: the scan, the tint and ruler marks, the code lens, the accept / navigate / compare commands, settings and keys.</summary>
public class MergeConflictViewTests
{
    private sealed class RecordingDiffs : IDiffEditorService
    {
        public List<DiffEditorRequest> Requests { get; } = new();
        public ValueTask OpenDiffAsync(DiffEditorRequest request, CancellationToken ct = default) { Requests.Add(request); return ValueTask.CompletedTask; }
    }

    private const string Text = """
        first
        <<<<<<< HEAD
        ours
        =======
        theirs
        >>>>>>> topic
        middle
        <<<<<<< HEAD
        mine 1
        mine 2
        ||||||| base
        original
        =======
        yours
        >>>>>>> topic
        last

        """;

    private static async Task<MergeConflictFeature> ReadyAsync(CodeEditorControl view, int count)
    {
        var feature = view.Feature<MergeConflictFeature>()!;
        Assert.NotNull(feature);
        await ViewHost.WaitFor(() => feature.Set is { } s && s.Version == view.Document.Version && s.Conflicts.Count == count, $"{count} conflicts scanned");
        return feature;
    }

    private static void Caret(CodeEditorControl view, int line, int column = 0) => view.Controller.SetSelection(TextRange.At(line, column), reveal: false);

    private static Task Run(ViewHost host, string command, params object?[] args) => host.Commands.ExecuteAsync(command, args).AsTask();

    [AvaloniaFact]
    public async Task Blocks_are_scanned_per_version_and_their_lines_follow_typing_until_the_next_scan()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync(Text, "m.txt");
        var feature = await ReadyAsync(view, 2);
        Assert.Equal((1, 3, 5), (feature.Blocks[0].Start, feature.Blocks[0].Splitter, feature.Blocks[0].End));
        Assert.Equal((7, 12, 14), (feature.Blocks[1].Start, feature.Blocks[1].Splitter, feature.Blocks[1].End));
        Assert.Equal([10], feature.Blocks[1].Ancestors);
        Assert.Equal(MergeConflictPart.CommonAncestors, feature.Blocks[1].PartAt(11));

        // a line inserted above the first block: both move at once, before the rescan
        MergeConflictFeature.Delay = TimeSpan.FromSeconds(30);
        try
        {
            doc.ApplyEdits([new TextEdit(TextRange.At(1, 0), "new line\n")]);
            Assert.Equal((2, 4, 6), (feature.Blocks[0].Start, feature.Blocks[0].Splitter, feature.Blocks[0].End));
            Assert.Equal(8, feature.Blocks[1].Start);
            Assert.Equal([11], feature.Blocks[1].Ancestors);
        }
        finally { MergeConflictFeature.Delay = TimeSpan.FromMilliseconds(250); }

        // the next scan replaces them; a resolved file has none
        doc.ApplyEdits([new TextEdit(new TextRange(new TextPosition(0, 0), new TextPosition(doc.LineCount - 1, 0)), "clean\n")]);
        await ReadyAsync(view, 0);
        Assert.Empty(feature.Blocks);
    }

    [Fact]
    public void A_marker_across_two_chunks_is_found_and_a_text_without_one_is_not_materialised()
    {
        var ui = new ThreadAffinityUiThread();
        var before = new string('x', 64 * 1024 - 4) + "\n"; // the marker starts 3 characters before the first chunk ends
        var text = before + "<<<<<<< HEAD\na\n=======\nb\n>>>>>>> t\n";
        var doc = new EditorDocument(new Uri("file:///tmp/chunk.txt"), "plaintext", EditorDocument.CreateBuffer(text, ui), ui);
        Assert.True(MergeConflictTracker.MayContainConflicts(doc.Snapshot()));
        var set = MergeConflictTracker.Compute(doc.Snapshot(), CancellationToken.None);
        Assert.Single(set.Conflicts);
        var plain = new EditorDocument(new Uri("file:///tmp/plain.txt"), "plaintext", EditorDocument.CreateBuffer(new string('y', 300_000), ui), ui);
        var none = MergeConflictTracker.Compute(plain.Snapshot(), CancellationToken.None);
        Assert.Empty(none.Conflicts);
        Assert.Equal("", none.Text); // nothing kept for a document without conflicts
    }

    [AvaloniaFact]
    public async Task Sides_are_tinted_with_the_merge_colours_and_marked_on_the_ruler()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync(Text, "m.txt");
        var feature = await ReadyAsync(view, 2);
        Caret(view, 0);
        var decorations = view.ScrollDecorations!;
        decorations.FlushForTests();
        Assert.Equal(
            [new LineMarker(7, 9, MarkerKind.MergeCurrent), new LineMarker(10, 11, MarkerKind.MergeCommon), new LineMarker(13, 14, MarkerKind.MergeIncoming)],
            decorations.Merge.Skip(2).ToArray());
        Assert.Equal(new LineMarker(1, 2, MarkerKind.MergeCurrent), decorations.Merge[0]);
        Assert.Equal(new LineMarker(4, 5, MarkerKind.MergeIncoming), decorations.Merge[1]);

        ViewHost.Pump();
        var themes = view.Factory.Themes;
        var bg = EditorPalette.ToColor(themes.GetColor("editor.background"));
        Color Over(string key) => Blend(bg, EditorPalette.ToColor(themes.GetColor(key)));
        var frame = window.CaptureRenderedFrame()!;
        int At(int line, string key)
        {
            var vl = view.TextView.GetOrConstructVisualLine(view.Editor.Document.GetLineByNumber(line + 1));
            var top = vl.VisualTop - view.TextView.ScrollOffset.Y + LineZones.HeightOf(vl);
            var origin = view.TextView.TranslatePoint(new Point(0, top), window)!.Value;
            var rect = new PixelRect((int)origin.X + 200, (int)origin.Y + 2, 100, (int)view.TextView.DefaultLineHeight - 4); // right of the text
            return CountPixels(frame, Over(key), 3, rect);
        }
        Assert.True(At(2, "merge.currentContentBackground") > 100, "current content tinted");
        Assert.True(At(4, "merge.incomingContentBackground") > 100, "incoming content tinted");
        Assert.True(At(5, "merge.incomingHeaderBackground") > 100, "incoming footer tinted");
        Assert.True(At(11, "merge.commonContentBackground") > 100, "common ancestor content tinted");
        Assert.Equal(0, At(6, "merge.currentContentBackground")); // between the blocks: no tint
        Assert.Equal(0, At(6, "merge.incomingContentBackground"));

        // decorators off: no tint, no ruler marks
        host.Config.Set(MergeConflictFeature.DecoratorsSetting, false);
        await ViewHost.WaitFor(() => !feature.DecorationsEnabled, "setting read");
        decorations.FlushForTests();
        Assert.Empty(decorations.Merge);
        ViewHost.Pump();
        frame = window.CaptureRenderedFrame()!;
        Assert.Equal(0, At(2, "merge.currentContentBackground"));
    }

    private static Color Blend(Color under, Color over)
    {
        var a = over.A / 255.0;
        byte M(byte u, byte o) => (byte)Math.Round(o * a + u * (1 - a));
        return Color.FromRgb(M(under.R, over.R), M(under.G, over.G), M(under.B, over.B));
    }

    [Fact]
    public void Theme_defaults_are_vs_codes()
    {
        static Core.Theming.Rgba? R(string key, Core.Theming.ThemeKind kind = Core.Theming.ThemeKind.Dark) =>
            Theming.DefaultColors.Resolve(key, new Dictionary<string, Core.Theming.Rgba>(), kind);
        static Core.Theming.Rgba P(string hex) => Core.Theming.Rgba.Parse(hex);
        Assert.Equal(P("#40C8AE80"), R("merge.currentHeaderBackground"));
        Assert.Equal(P("#40C8AE33"), R("merge.currentContentBackground"));
        Assert.Equal(P("#40A6FF80"), R("merge.incomingHeaderBackground"));
        Assert.Equal(P("#40A6FF33"), R("merge.incomingContentBackground"));
        Assert.Equal(P("#60606066"), R("merge.commonHeaderBackground"));
        Assert.Equal(P("#60606029"), R("merge.commonContentBackground"));
        Assert.Equal(P("#40C8AE80"), R("editorOverviewRuler.currentContentForeground"));
        Assert.Equal(P("#40A6FF80"), R("editorOverviewRuler.incomingContentForeground"));
        Assert.Equal(P("#60606066"), R("editorOverviewRuler.commonContentForeground"));
        Assert.Equal(P("#C3DF6F"), R("editorOverviewRuler.currentContentForeground", Core.Theming.ThemeKind.HighContrastDark));
        // a theme's own header colour carries to the ruler
        var own = new Dictionary<string, Core.Theming.Rgba> { ["merge.currentHeaderBackground"] = P("#112233") };
        Assert.Equal(P("#112233"), Theming.DefaultColors.Resolve("editorOverviewRuler.currentContentForeground", own, Core.Theming.ThemeKind.Dark));
    }

    // ------------------------------------------------------------------------------------- code lens

    [AvaloniaFact]
    public async Task The_code_lens_offers_vs_codes_four_actions_and_a_click_resolves_that_block_in_one_undo_step()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync(Text, "m.txt");
        await ReadyAsync(view, 2);
        var lenses = view.Feature<CodeLensFeature>()!;
        await ViewHost.WaitFor(() => lenses.Placements.Count == 8, "lenses placed");
        Assert.Equal([(1, "Accept Current Change"), (1, "Accept Incoming Change"), (1, "Accept Both Changes"), (1, "Compare Changes")], lenses.Placements.Take(4).ToArray());
        Assert.Equal(7, lenses.Placements[4].Line);

        // the second block's "Accept Incoming Change", with the caret elsewhere
        Caret(view, 0);
        await lenses.ClickAsync(7, 1);
        Assert.Equal("first\n<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> topic\nmiddle\nyours\nlast\n", doc.GetText());
        await ViewHost.WaitFor(() => lenses.Placements.Count == 4, "the resolved block lost its lenses");
        view.Controller.Undo();
        Assert.Equal(Text, doc.GetText());

        // turned off: none
        host.Config.Set(MergeConflictCommands.CodeLensSetting, false);
        await ViewHost.WaitFor(() => lenses.Placements.Count == 0, "lenses removed");
        host.Config.Set(MergeConflictCommands.CodeLensSetting, true);
        await ViewHost.WaitFor(() => lenses.Placements.Count == 8, "lenses back");
    }

    // ------------------------------------------------------------------------------------- commands

    [AvaloniaFact]
    public async Task Accept_commands_act_on_the_block_at_the_caret_each_in_one_undo_step()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync(Text, "m.txt");
        await ReadyAsync(view, 2);

        Caret(view, 2);
        await Run(host, MergeConflictCommands.AcceptCurrent);
        var afterFirst = "first\nours\nmiddle\n" + Text[Text.IndexOf("<<<<<<< HEAD\nmine", StringComparison.Ordinal)..];
        Assert.Equal(afterFirst, doc.GetText());
        await ReadyAsync(view, 1);

        Caret(view, 4); // "mine 1" of the remaining block
        await Run(host, MergeConflictCommands.AcceptBoth);
        Assert.Equal("first\nours\nmiddle\nmine 1\nmine 2\nyours\nlast\n", doc.GetText());
        view.Controller.Undo();
        Assert.Equal(afterFirst, doc.GetText());
        view.Controller.Undo();
        Assert.Equal(Text, doc.GetText());

        // outside a block: a warning, nothing changes
        await ReadyAsync(view, 2);
        Caret(view, 6);
        await Run(host, MergeConflictCommands.AcceptIncoming);
        Assert.Equal(Text, doc.GetText());
        Assert.Contains(host.Window.Messages, m => m.Message == MergeConflictCommands.NotInConflict);
    }

    [AvaloniaFact]
    public async Task Accept_selection_takes_the_side_the_caret_is_in_and_refuses_the_ancestors_and_the_splitter()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync(Text, "m.txt");
        await ReadyAsync(view, 2);
        Caret(view, 11); // common ancestors
        await Run(host, MergeConflictCommands.AcceptSelection);
        Assert.Equal(Text, doc.GetText());
        Assert.Contains(host.Window.Messages, m => m.Message.Contains("common ancestors block", StringComparison.Ordinal));
        Caret(view, 3); // splitter
        await Run(host, MergeConflictCommands.AcceptSelection);
        Assert.Equal(Text, doc.GetText());
        Assert.Contains(host.Window.Messages, m => m.Message.Contains("splitter", StringComparison.Ordinal));
        Caret(view, 5, 3); // the footer counts as incoming
        await Run(host, MergeConflictCommands.AcceptSelection);
        Assert.StartsWith("first\ntheirs\nmiddle\n", doc.GetText(), StringComparison.Ordinal);
        await ReadyAsync(view, 1);
        Caret(view, 4); // the header counts as current
        await Run(host, MergeConflictCommands.AcceptSelection);
        Assert.Equal("first\ntheirs\nmiddle\nmine 1\nmine 2\nlast\n", doc.GetText());
    }

    [AvaloniaFact]
    public async Task Accept_all_resolves_every_block_in_one_undo_step_and_says_so_when_there_are_none()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync(Text, "m.txt");
        await ReadyAsync(view, 2);
        await Run(host, MergeConflictCommands.AcceptAllIncoming);
        Assert.Equal("first\ntheirs\nmiddle\nyours\nlast\n", doc.GetText());
        view.Controller.Undo();
        Assert.Equal(Text, doc.GetText());
        await ReadyAsync(view, 2);
        await Run(host, MergeConflictCommands.AcceptAllBoth);
        Assert.Equal("first\nours\ntheirs\nmiddle\nmine 1\nmine 2\nyours\nlast\n", doc.GetText());
        await ReadyAsync(view, 0);
        await Run(host, MergeConflictCommands.AcceptAllCurrent);
        Assert.Contains(host.Window.Messages, m => m.Message == MergeConflictCommands.NoConflicts);
    }

    [AvaloniaFact]
    public async Task Accept_all_with_file_uris_edits_open_documents_and_files_on_disk()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync(Text, "open.txt");
        await ReadyAsync(view, 2);
        var closed = host.Dir.WriteFile("closed.txt", "﻿a\r\n<<<<<<< HEAD\r\nx\r\n=======\r\ny\r\n>>>>>>> b\r\nz\r\n");
        await Run(host, MergeConflictCommands.AcceptAllCurrent, doc.Uri, new Uri(closed).ToString());
        // the navigation fake applies workspace edits to open documents; a closed file goes through it too
        var applied = host.Navigation.AppliedEdits.Last();
        Assert.True(applied.Changes.ContainsKey(doc.Uri));
        Assert.True(applied.Changes.ContainsKey(new Uri(closed)));
        var edit = Assert.Single(applied.Changes[new Uri(closed)]);
        Assert.Equal("x\r\n", edit.NewText);
        Assert.Equal(new TextRange(new TextPosition(1, 0), new TextPosition(6, 0)), edit.Range);
    }

    [AvaloniaFact]
    public async Task Next_and_previous_conflict_wrap_around()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Text, "m.txt");
        await ReadyAsync(view, 2);
        Caret(view, 0);
        await Run(host, MergeConflictCommands.Next);
        Assert.Equal(new TextPosition(1, 0), view.Controller.Caret);
        await Run(host, MergeConflictCommands.Next);
        Assert.Equal(new TextPosition(7, 0), view.Controller.Caret);
        await Run(host, MergeConflictCommands.Next);
        Assert.Equal(new TextPosition(1, 0), view.Controller.Caret); // wrapped
        await Run(host, MergeConflictCommands.Previous);
        Assert.Equal(new TextPosition(7, 0), view.Controller.Caret); // wrapped back
        Caret(view, 7, 2);
        await Run(host, MergeConflictCommands.Previous);
        Assert.Equal(new TextPosition(7, 0), view.Controller.Caret); // inside the header line: its own start is before the caret
    }

    [AvaloniaFact]
    public async Task Compare_opens_the_file_with_every_current_side_against_every_incoming_side_at_the_block()
    {
        var diffs = new RecordingDiffs();
        using var host = ViewHost.Create(s => s.AddSingleton<IDiffEditorService>(diffs));
        var (_, view, _) = await host.OpenAsync(Text, "m.txt");
        await ReadyAsync(view, 2);
        Caret(view, 13);
        await Run(host, MergeConflictCommands.Compare);
        var r = Assert.Single(diffs.Requests);
        Assert.Equal("first\nours\nmiddle\nmine 1\nmine 2\nlast\n", r.Original.Text);
        Assert.Equal("first\ntheirs\nmiddle\nyours\nlast\n", r.Modified.Text);
        Assert.Equal("m.txt: Current Changes ↔ Incoming Changes", r.Title);
        Assert.Equal(3, r.RevealLine); // "yours": the second block starts at line 3 of the incoming text
    }

    [AvaloniaFact]
    public async Task The_context_keys_follow_the_caret_and_the_right_click_items_need_them()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Text, "m.txt");
        await ReadyAsync(view, 2);
        var keys = (TestContextKeyService)host.Provider.GetRequiredService<IContextKeyService>();
        Caret(view, 2);
        Assert.Equal(true, keys.Get(MergeConflictFeature.AtCursorKey));
        Assert.Equal(2, keys.Get(MergeConflictFeature.CountKey));
        Caret(view, 6);
        Assert.Equal(false, keys.Get(MergeConflictFeature.AtCursorKey));
        Assert.All(MergeConflictCommands.Menus, m => Assert.Contains(MergeConflictFeature.AtCursorKey, m.When));
        Assert.Contains(host.Factory.Services.Contributions.Of<Core.Contributions.MenuContribution>().Items,
            m => m.Value.Command == MergeConflictCommands.AcceptCurrent && m.Value.MenuId == Core.Contributions.MenuIds.EditorContext);
        Assert.All(new[] { MergeConflictCommands.AcceptCurrent, MergeConflictCommands.AcceptIncoming, MergeConflictCommands.AcceptBoth, MergeConflictCommands.AcceptSelection,
            MergeConflictCommands.AcceptAllCurrent, MergeConflictCommands.AcceptAllIncoming, MergeConflictCommands.AcceptAllBoth, MergeConflictCommands.Next,
            MergeConflictCommands.Previous, MergeConflictCommands.Compare }, id => Assert.True(host.Commands.Has(id), id));
        Assert.Contains(host.Config.Properties, p => p.Key == MergeConflictCommands.CodeLensSetting);
        Assert.Contains(host.Config.Properties, p => p.Key == MergeConflictFeature.DecoratorsSetting);
    }

    [AvaloniaFact]
    public async Task Diff_panes_get_no_conflict_support()
    {
        using var host = ViewHost.Create();
        var diff = (Diff.DiffEditorControl)host.Factory.CreateDiffView(new DiffEditorRequest(new DiffSide("a", Text: Text), new DiffSide("b", Text: Text + "x\n"), "t"))!;
        try
        {
            await ViewHost.WaitFor(() => diff.Model is not null, "diff computed");
            Assert.Null(diff.OriginalView!.Feature<MergeConflictFeature>());
            Assert.Null(diff.ModifiedView!.Feature<MergeConflictFeature>());
        }
        finally { diff.Dispose(); }
    }

    [AvaloniaFact]
    public async Task A_closed_view_with_conflicts_is_not_retained()
    {
        using var host = ViewHost.Create();
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var (doc, view, window) = await host.OpenAsync(Text, "leak.txt");
            await ReadyAsync(view, 2);
            await ViewHost.WaitFor(() => view.Feature<CodeLensFeature>()!.Placements.Count == 8, "lenses");
            host.Close(window);
            host.Factory.DisposeView(view);
            await host.Factory.CloseDocumentAsync(doc);
            ViewHost.Pump();
            return [view, doc];
        });
    }
}
