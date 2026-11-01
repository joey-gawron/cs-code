using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests;

/// <summary>
/// Linked editing over a provider that answers like vscode-html-language-server: the name of the tag at the position and of its partner.
/// </summary>
public class LinkedEditingViewTests
{
    /// <summary>HTML tag pairs: the tag name under the position and its matching open / close tag's name (a stack over the text).</summary>
    private sealed class TagLinker(string? wordPattern = "[-_.:A-Za-z0-9]+") : ILinkedEditingRangeProvider
    {
        private static readonly Regex Tag = new(@"<(/?)([A-Za-z][-_.:A-Za-z0-9]*)?", RegexOptions.CultureInvariant);
        public int Calls;
        public Func<Task>? Gate { get; set; }

        public async ValueTask<LinkedEditingRanges?> ProvideLinkedEditingRangesAsync(ITextDocument document, TextPosition position, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Gate is { } g) await g().WaitAsync(ct).ConfigureAwait(false);
            var text = document.GetText();
            var offset = document.OffsetAt(position);
            var tags = Tag.Matches(text).Select(m => (Close: m.Groups[1].Length > 0, Start: m.Groups[2].Success ? m.Groups[2].Index : m.Index + m.Length, Name: m.Groups[2].Value)).ToList();
            var stack = new Stack<int>();
            var partner = new Dictionary<int, int>();
            for (var i = 0; i < tags.Count; i++)
            {
                if (!tags[i].Close) stack.Push(i);
                else if (stack.Count > 0) { var open = stack.Pop(); partner[open] = i; partner[i] = open; }
            }
            for (var i = 0; i < tags.Count; i++)
            {
                var t = tags[i];
                if (offset < t.Start || offset > t.Start + t.Name.Length || !partner.TryGetValue(i, out var j)) continue;
                var other = tags[j];
                return new LinkedEditingRanges(
                    [new TextRange(document.PositionAt(t.Start), document.PositionAt(t.Start + t.Name.Length)), new TextRange(document.PositionAt(other.Start), document.PositionAt(other.Start + other.Name.Length))],
                    wordPattern);
            }
            return null;
        }
    }

    private const string Html = "<body>\n  <div class=\"a\">text</div>\n</body>\n";

    private static LinkedEditingFeature Linked(CodeEditorControl v) => v.Feature<LinkedEditingFeature>()!;

    private static async Task<(ViewHost Host, CodeEditorControl View, TagLinker Linker)> OpenAsync(bool enabled = true, string text = Html, TagLinker? linker = null)
    {
        var host = ViewHost.Create();
        if (enabled) host.Config.Set(LinkedEditingFeature.Setting, true);
        linker ??= new TagLinker();
        host.Factory.Services.Features.Register<ILinkedEditingRangeProvider>(DocumentSelector.All, linker);
        var (_, view, _) = await host.OpenAsync(text, "index.html");
        return (host, view, linker);
    }

    private static async Task CaretAt(CodeEditorControl view, int line, int column, bool expectSession = true)
    {
        view.Controller.SetSelection(TextRange.At(line, column));
        if (expectSession) await ViewHost.WaitFor(() => Linked(view).IsActive, "linked ranges at the caret");
    }

    private static async Task Settle(CodeEditorControl view)
    {
        ViewHost.Pump();
        await Task.Delay(5);
        ViewHost.Pump();
    }

    [AvaloniaFact]
    public async Task Typing_in_a_tag_name_changes_the_matching_tag_and_a_typed_word_is_one_undo_step()
    {
        var (host, view, _) = await OpenAsync();
        using var _h = host;
        await CaretAt(view, 1, 6); // <div|
        Assert.Equal([TextRange.FromLines(1, 3, 1, 6), TextRange.FromLines(1, 23, 1, 26)], Linked(view).Ranges);

        foreach (var ch in "ider")
        {
            Type(view, ch.ToString());
            await Settle(view); // each character mirrored before the next, as when typing
        }
        Assert.Equal("  <divider class=\"a\">text</divider>", view.Document.GetLineText(1));
        Assert.True(Linked(view).IsActive);
        Assert.Equal(new TextPosition(1, 10), view.Controller.Caret);

        view.Controller.Undo();
        await Settle(view);
        Assert.Equal(Html, view.Document.GetText()); // both tags, the whole word, one step
        view.Controller.Redo();
        Assert.Equal("  <divider class=\"a\">text</divider>", view.Document.GetLineText(1));
    }

    [AvaloniaFact]
    public async Task Typing_in_the_closing_tag_changes_the_opening_one_and_the_caret_stays_where_it_types()
    {
        var (host, view, _) = await OpenAsync();
        using var _h = host;
        await CaretAt(view, 1, 26); // </div|
        Type(view, "x");
        await Settle(view);
        Type(view, "y");
        await Settle(view);
        Assert.Equal("  <divxy class=\"a\">text</divxy>", view.Document.GetLineText(1));
        Assert.Equal(new TextPosition(1, 30), view.Controller.Caret); // the mirror before it moved the caret along
        view.Controller.Undo();
        Assert.Equal(Html, view.Document.GetText());
    }

    [AvaloniaFact]
    public async Task Deleting_the_whole_name_and_typing_a_new_one_renames_both_tags()
    {
        var (host, view, window) = await OpenAsyncWithWindow();
        using var _h = host;
        await CaretAt(view, 1, 6);
        for (var i = 0; i < 3; i++)
        {
            Key(window, Avalonia.Input.Key.Back);
            await Settle(view);
        }
        Assert.Equal("  < class=\"a\">text</>", view.Document.GetLineText(1));
        Assert.True(Linked(view).IsActive); // empty ranges: typing at their edge grows them
        Type(view, "span");
        await Settle(view);
        Assert.Equal("  <span class=\"a\">text</span>", view.Document.GetLineText(1));
    }

    private static async Task<(ViewHost Host, CodeEditorControl View, Avalonia.Controls.Window Window)> OpenAsyncWithWindow()
    {
        var host = ViewHost.Create();
        host.Config.Set(LinkedEditingFeature.Setting, true);
        host.Factory.Services.Features.Register<ILinkedEditingRangeProvider>(DocumentSelector.All, new TagLinker());
        var (_, view, window) = await host.OpenAsync(Html, "index.html");
        return (host, view, window);
    }

    [AvaloniaFact]
    public async Task A_character_outside_the_word_pattern_ends_linked_editing_without_mirroring_it()
    {
        var (host, view, _) = await OpenAsync();
        using var _h = host;
        await CaretAt(view, 1, 5); // <di|v
        Type(view, " ");
        await Settle(view);
        Assert.Equal("  <di v class=\"a\">text</div>", view.Document.GetLineText(1));
        Assert.False(Linked(view).IsActive);
    }

    [AvaloniaFact]
    public async Task Without_the_servers_word_pattern_the_languages_is_used()
    {
        var (host, view, _) = await OpenAsync(linker: new TagLinker(wordPattern: null));
        using var _h = host;
        await CaretAt(view, 1, 5);
        Type(view, " ");
        await Settle(view);
        Assert.False(Linked(view).IsActive); // HTML's word pattern has no spaces either
        Assert.Equal("  <di v class=\"a\">text</div>", view.Document.GetLineText(1));
    }

    [AvaloniaFact]
    public async Task An_edit_outside_the_ranges_or_straddling_one_ends_linked_editing()
    {
        var (host, view, _) = await OpenAsync();
        using var _h = host;
        await CaretAt(view, 1, 6);
        view.Document.ApplyEdits([new TextEdit(TextRange.At(1, 18), "!")]); // in "text"
        await Settle(view);
        Assert.False(Linked(view).IsActive);
        Assert.Equal("  <div class=\"a\">t!ext</div>", view.Document.GetLineText(1));

        await CaretAt(view, 1, 4);
        view.Document.ApplyEdits([new TextEdit(TextRange.FromLines(1, 2, 1, 4), "<x")]); // over '<' and 'd'
        await Settle(view);
        Assert.False(Linked(view).IsActive);
        Assert.Equal("  <xiv class=\"a\">t!ext</div>", view.Document.GetLineText(1));
    }

    [AvaloniaFact]
    public async Task With_the_setting_off_nothing_links_until_start_linked_editing_which_toggles_the_session()
    {
        var (host, view, linker) = await OpenAsync(enabled: false);
        using var _h = host;
        view.Controller.SetSelection(TextRange.At(1, 6));
        await Task.Delay(250);
        ViewHost.Pump();
        Assert.False(Linked(view).IsActive);
        Assert.Equal(0, linker.Calls);

        await host.Commands.ExecuteAsync(LinkedEditingFeature.StartCommand);
        Assert.True(Linked(view).IsActive);
        Type(view, "x");
        await Settle(view);
        Assert.Equal("  <divx class=\"a\">text</divx>", view.Document.GetLineText(1));
        await host.Commands.ExecuteAsync(LinkedEditingFeature.StartCommand);
        Assert.False(Linked(view).IsActive);
        Type(view, "y");
        await Settle(view);
        Assert.Equal("  <divxy class=\"a\">text</divx>", view.Document.GetLineText(1));

        view.Controller.SetSelection(TextRange.At(1, 20)); // nothing to link in "text"
        await host.Commands.ExecuteAsync(LinkedEditingFeature.StartCommand);
        Assert.Contains("No linked ranges at the caret", host.Window.StatusMessages);
    }

    [AvaloniaFact]
    public async Task Moving_the_caret_out_of_the_ranges_ends_the_session_and_asks_again_where_it_rests()
    {
        var (host, view, linker) = await OpenAsync();
        using var _h = host;
        await CaretAt(view, 1, 6);
        var calls = linker.Calls;
        view.Controller.SetSelection(TextRange.At(1, 4)); // still in "div": nothing asked
        await Task.Delay(200);
        ViewHost.Pump();
        Assert.Equal(calls, linker.Calls);
        Assert.True(Linked(view).IsActive);

        view.Controller.SetSelection(TextRange.At(0, 3)); // in "body"
        Assert.False(Linked(view).IsActive);
        await ViewHost.WaitFor(() => Linked(view).IsActive, "body's ranges");
        Assert.Equal([TextRange.FromLines(0, 1, 0, 5), TextRange.FromLines(2, 2, 2, 6)], Linked(view).Ranges);
    }

    [AvaloniaFact]
    public async Task A_result_for_text_that_changed_meanwhile_is_dropped()
    {
        var gate = new TaskCompletionSource();
        var linker = new TagLinker { Gate = () => gate.Task };
        var (host, view, _) = await OpenAsync(linker: linker);
        using var _h = host;
        view.Controller.SetSelection(TextRange.At(1, 6));
        await ViewHost.WaitFor(() => linker.Calls == 1, "the request");
        view.Document.ApplyEdits([new TextEdit(TextRange.At(1, 6), "x")]);
        gate.SetResult();
        await Linked(view).Pending;
        await Settle(view);
        Assert.False(Linked(view).IsActive);
    }

    [AvaloniaFact]
    public async Task Listeners_see_the_edit_before_the_mirror_never_the_mirror_inside_the_edits_event()
    {
        var (host, view, _) = await OpenAsync();
        using var _h = host;
        await CaretAt(view, 1, 6);
        var seen = new List<(int Version, string Line, string Inserted)>();
        using var sub = view.Document.Changed.Subscribe(e => seen.Add((e.Version, view.Document.GetLineText(1), e.Changes[0].Text)));
        Type(view, "x");
        Assert.Single(seen); // the mirror waits for the event to finish
        Assert.Equal("  <divx class=\"a\">text</div>", seen[0].Line);
        await Settle(view);
        Assert.Equal(2, seen.Count);
        Assert.Equal(seen[0].Version + 1, seen[1].Version);
        Assert.Equal("  <divx class=\"a\">text</divx>", seen[1].Line);
    }

    [AvaloniaFact]
    public async Task Several_carets_typing_in_every_range_change_each_once_and_keep_the_session()
    {
        var (host, view, _) = await OpenAsync();
        using var _h = host;
        await CaretAt(view, 1, 6);
        view.Controller.SetSelections([TextRange.At(1, 6), TextRange.At(1, 26)]);
        Assert.Equal(2, view.Controller.Selections.Count);
        Type(view, "x");
        await Settle(view);
        Assert.Equal("  <divx class=\"a\">text</divx>", view.Document.GetLineText(1));
        Assert.True(Linked(view).IsActive);
        view.Controller.Undo();
        Assert.Equal(Html, view.Document.GetText());
    }

    [AvaloniaFact]
    public async Task Inside_a_modal_layers_undo_group_the_mirror_is_part_of_that_change()
    {
        var (host, view, _) = await OpenAsync();
        using var _h = host;
        await CaretAt(view, 1, 6);
        var doc = view.Document;
        doc.BeginUndoGroup(continueLast: false); // what the Vim layer holds open for one Insert session
        view.Modal.TypeText("i");
        await Settle(view);
        view.Modal.TypeText("v");
        await Settle(view);
        doc.EndUndoGroup();
        Assert.Equal("  <diviv class=\"a\">text</diviv>", doc.GetLineText(1));
        doc.Undo();
        Assert.Equal(Html, doc.GetText());
    }

    [AvaloniaFact]
    public async Task The_setting_is_registered_off_by_default_and_turning_it_off_ends_the_session()
    {
        var (host, view, _) = await OpenAsync();
        using var _h = host;
        Assert.Contains(LinkedEditingFeature.Properties, p => p.Key == LinkedEditingFeature.Setting && p.Default!.Value.GetBoolean() == false);
        await CaretAt(view, 1, 6);
        host.Config.Set(LinkedEditingFeature.Setting, false);
        await ViewHost.WaitFor(() => !Linked(view).IsActive, "session ended with the setting");
        Assert.True(host.Commands.Has(LinkedEditingFeature.StartCommand));
    }
}
