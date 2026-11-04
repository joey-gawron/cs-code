using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using CsCode.Core.Contributions;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Bookmarks;
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

/// <summary>
/// Bookmarks in real views (docs/EDITOR.md §7g): the commands at the caret(s), the glyph (in the breakpoint margin when there is one, else a margin
/// of their own; on a wrapped line's first row, level with its line number), the overview ruler's mark, Next / Previous across files, the list,
/// edits, Save As, large-file mode, the right-click submenu, and that a closed view leaves nothing behind.
/// </summary>
public sealed class BookmarkViewTests
{
    private static BookmarkService Bookmarks(ViewHost host) => host.Provider.GetRequiredService<BookmarkService>();
    private static BookmarkFeature FeatureOf(CodeEditorControl view) => view.Feature<BookmarkFeature>()!;
    private static int[] LinesOf(CodeEditorControl view) => [.. FeatureOf(view).Bookmarks.Select(b => b.Line).Order()];
    private static Color Blue(CodeEditorControl view) => EditorPalette.ToColor(view.Factory.Themes.GetColor("bookmarks.iconForeground"));

    private static (FakeDebugService Debug, ViewHost Host) DebugHost(Action<IServiceCollection>? more = null)
    {
        var debug = new FakeDebugService();
        return (debug, ViewHost.Create(s => { s.AddSingleton<IDebugService>(debug); more?.Invoke(s); }));
    }

    /// <summary>The pixels of the margin between <paramref name="top"/> and <paramref name="bottom"/> (view coordinates) in the window's frame.</summary>
    private static PixelRect MarginBand(Control margin, Window window, double top, double bottom)
    {
        var origin = margin.TranslatePoint(new Point(0, 0), window)!.Value;
        return new PixelRect((int)origin.X, (int)Math.Floor(origin.Y + top), (int)margin.Bounds.Width, Math.Max(1, (int)Math.Ceiling(bottom - top)));
    }

    // ------------------------------------------------------------------ toggling and the glyph

    [AvaloniaFact]
    public async Task Toggle_Bookmark_marks_each_caret_line_in_a_margin_of_its_own_that_takes_no_width_without_bookmarks()
    {
        using var host = ViewHost.Create(); // no debugger: no breakpoint margin to share
        var (doc, view, window) = await host.OpenAsync("zero\none\ntwo\nthree\nfour\n", "a.cs");
        var feature = FeatureOf(view);
        ViewHost.Pump();
        Assert.Equal("Bookmark margin", Avalonia.Automation.AutomationProperties.GetName(feature.Margin));
        Assert.Equal(0, feature.Margin.Bounds.Width); // the gutter of a document without bookmarks is as it was

        view.Controller.SetSelection(TextRange.At(1, 2));
        await host.Commands.ExecuteAsync(BookmarkCommandIds.Toggle);
        ViewHost.Pump();
        Assert.Equal([1], LinesOf(view));
        Assert.Equal(BookmarkFeature.MarginWidth, feature.Margin.Bounds.Width);
        var glyph = feature.GlyphBounds(1)!.Value;
        var frame = window.CaptureRenderedFrame()!;
        Assert.True(CountPixels(frame, Blue(view), 30, MarginBand(feature.Margin, window, glyph.Top, glyph.Bottom)) > 20, "the glyph is drawn on line 1");
        Assert.Equal(0, CountPixels(frame, Blue(view), 30, MarginBand(feature.Margin, window, glyph.Bottom + 1, glyph.Bottom + 3 * glyph.Height)));

        // two carets (lines 1 and 3): each line is toggled, the one with a bookmark loses it
        view.Controller.SetSelections([TextRange.At(1, 0), TextRange.At(3, 1)]);
        await host.Commands.ExecuteAsync(BookmarkCommandIds.Toggle);
        Assert.Equal([3], LinesOf(view));

        // Enter at the start of the document: the glyph moves down with its line
        view.Controller.SetSelection(TextRange.At(0, 0));
        Key(window, Avalonia.Input.Key.Enter);
        ViewHost.Pump();
        Assert.Equal([4], LinesOf(view));
        Assert.Equal("three", doc.GetLineText(4));

        // Disabled: hollow (an outline), still in the margin
        await host.Commands.ExecuteAsync(BookmarkCommandIds.ToggleEnabled); // the caret is on line 0: nothing there
        Assert.True(FeatureOf(view).BookmarkAt(4)!.Enabled);
        view.Controller.SetSelection(TextRange.At(4, 0));
        await host.Commands.ExecuteAsync(BookmarkCommandIds.ToggleEnabled);
        Assert.False(FeatureOf(view).BookmarkAt(4)!.Enabled);

        await host.Commands.ExecuteAsync(BookmarkCommandIds.Clear);
        ViewHost.Pump();
        Assert.Empty(LinesOf(view));
        Assert.Equal(0, feature.Margin.Bounds.Width);
    }

    [AvaloniaFact]
    public async Task With_a_debugger_the_glyph_is_drawn_in_the_breakpoint_margin_under_a_breakpoint_and_a_click_toggles_only_the_breakpoint()
    {
        var (debug, host) = DebugHost();
        using var _ = host;
        var (doc, view, window) = await host.OpenAsync("line0\nline1\nline2\nline3\n", "a.cs");
        var breakpointMargin = view.Feature<DebugFeature>()!.Margin;
        Assert.Same(breakpointMargin, FeatureOf(view).Margin);
        Assert.Single(view.TextArea.LeftMargins, m => Avalonia.Automation.AutomationProperties.GetName(m) is "Breakpoint margin" or "Bookmark margin");

        Bookmarks(host).Toggle(doc.Uri, 2);
        ViewHost.Pump();
        var glyph = FeatureOf(view).GlyphBounds(2)!.Value;
        Assert.True(CountPixels(window.CaptureRenderedFrame()!, Blue(view), 30, MarginBand(breakpointMargin, window, glyph.Top, glyph.Bottom)) > 20, "drawn through the breakpoint margin's underlay");

        // a click in the margin is the breakpoint's, as before
        var at = breakpointMargin.TranslatePoint(new Point(9, glyph.Center.Y), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        ViewHost.Pump();
        Assert.Equal([2], debug.Manager.GetForUri(doc.Uri).Select(b => b.Line));
        Assert.Equal([2], LinesOf(view));
        var red = ((ISolidColorBrush)view.Palette.BreakpointFill).Color;
        Assert.True(CountPixels(window.CaptureRenderedFrame()!, red, 12, MarginBand(breakpointMargin, window, glyph.Top, glyph.Bottom)) > 20, "the breakpoint over the bookmark");
    }

    [AvaloniaFact]
    public async Task A_wrapped_line_s_glyph_sits_on_its_first_row_level_with_its_line_number()
    {
        var (_, host) = DebugHost();
        using var _h = host;
        host.Config.Set("editor.wordWrap", "on");
        var wrapped = string.Join(" ", Enumerable.Repeat("wrapping", 60));
        var (doc, view, window) = await host.OpenAsync($"first\n{wrapped}\nlast\n", "wrap.cs", width: 420);
        await ViewHost.WaitFor(() => view.TextView.VisualLinesValid && view.TextView.GetVisualLine(2) is { TextLines.Count: >= 3 }, "line 2 wraps into rows");
        Bookmarks(host).Toggle(doc.Uri, 1);
        ViewHost.Pump();

        var vl = view.TextView.GetVisualLine(2)!;
        var firstRowTop = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.TextTop) - view.TextView.VerticalOffset; // where the line number is drawn
        var glyph = FeatureOf(view).GlyphBounds(1)!.Value;
        Assert.Equal(firstRowTop, glyph.Top, 3);
        Assert.Equal(view.TextView.DefaultLineHeight, glyph.Height, 3);

        var margin = FeatureOf(view).Margin;
        var frame = window.CaptureRenderedFrame()!;
        var lineBottom = vl.VisualTop + vl.Height - view.TextView.VerticalOffset;
        Assert.True(CountPixels(frame, Blue(view), 30, MarginBand(margin, window, glyph.Top, glyph.Bottom)) > 20, "on the first row");
        Assert.Equal(0, CountPixels(frame, Blue(view), 30, MarginBand(margin, window, glyph.Bottom + 1, lineBottom))); // never by the other rows
    }

    [AvaloniaFact]
    public async Task Bookmarks_are_marked_on_the_overview_ruler_s_left_lane_below_breakpoints()
    {
        var (debug, host) = DebugHost();
        using var _ = host;
        host.Config.Set(ScrollDecorations.BreakpointsKey, true);
        var (doc, view, _) = await host.OpenAsync(string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"int v{i} = {i};")), "a.cs");
        Bookmarks(host).Toggle(doc.Uri, 300);
        Bookmarks(host).Toggle(doc.Uri, 700);
        debug.Manager.Add(doc.Uri, 700);
        ViewHost.Pump();
        view.ScrollDecorations!.FlushForTests();
        Assert.Equal([300, 700], view.ScrollDecorations.Bookmarks.Select(m => m.Line));
        int RulerY(int line)
        {
            var (top, height) = view.ScrollDecorations.Ruler.Track;
            return (int)Math.Floor(top + view.TextView.GetVisualTopByDocumentLine(line + 1) / ((IScrollable)view.TextArea).Extent.Height * height);
        }
        Assert.Equal(MarkerKind.Bookmark, view.ScrollDecorations.Ruler.Model.At(MarkerLane.Left, RulerY(300)));
        Assert.Equal(MarkerKind.Breakpoint, view.ScrollDecorations.Ruler.Model.At(MarkerLane.Left, RulerY(700))); // the breakpoint wins its row
        Assert.Equal(MarkerKind.None, view.ScrollDecorations.Ruler.Model.At(MarkerLane.Left, RulerY(500)));
    }

    // ------------------------------------------------------------------ navigation

    [AvaloniaFact]
    public async Task Next_and_previous_go_through_the_document_then_the_next_file_and_wrap_around()
    {
        using var host = ViewHost.Create();
        var (a, viewA, _) = await host.OpenAsync("a0\na1\na2\na3\na4\n", "a.cs");
        var (b, viewB, _) = await host.OpenAsync("b0\nb1\nb2\nb3\n", "b.cs"); // the view commands act on: it has the keyboard
        var service = Bookmarks(host);
        service.Toggle(a.Uri, 1);
        service.Toggle(b.Uri, 1);
        service.Toggle(b.Uri, 3);
        viewB.Controller.SetSelection(TextRange.At(0, 1));

        await host.Commands.ExecuteAsync(BookmarkCommandIds.JumpToNext);
        Assert.Equal(new TextPosition(1, 0), viewB.Controller.Caret); // in the same document: the caret moves there
        await host.Commands.ExecuteAsync(BookmarkCommandIds.JumpToNext);
        Assert.Equal(new TextPosition(3, 0), viewB.Controller.Caret);
        await host.Commands.ExecuteAsync(BookmarkCommandIds.JumpToNext); // past b.cs's last: wraps to a.cs (opened through navigation)
        Assert.Equal((a.Uri, (TextRange?)TextRange.At(1, 0)), host.Navigation.Opened[^1]);

        await host.Commands.ExecuteAsync(BookmarkCommandIds.JumpToPreviousInDocument); // from line 3 of b.cs, within b.cs only
        Assert.Equal(new TextPosition(1, 0), viewB.Controller.Caret);
        await host.Commands.ExecuteAsync(BookmarkCommandIds.JumpToPreviousInDocument);
        Assert.Equal(new TextPosition(3, 0), viewB.Controller.Caret); // wraps inside the document
        var opened = host.Navigation.Opened.Count;
        await host.Commands.ExecuteAsync(BookmarkCommandIds.JumpToNextInDocument);
        Assert.Equal(new TextPosition(1, 0), viewB.Controller.Caret);
        Assert.Equal(opened, host.Navigation.Opened.Count); // never left the document

        // a disabled bookmark is skipped
        service.Update(service.GetForUri(b.Uri)[1] with { Enabled = false });
        viewB.Controller.SetSelection(TextRange.At(2, 0));
        await host.Commands.ExecuteAsync(BookmarkCommandIds.JumpToNext); // from b.cs:2, b.cs:3 is disabled: on to a.cs:1 (wrapping)
        Assert.Equal(opened + 1, host.Navigation.Opened.Count);
        Assert.Equal((a.Uri, (TextRange?)TextRange.At(1, 0)), host.Navigation.Opened[^1]);
        Assert.Equal(new TextPosition(2, 0), viewB.Controller.Caret);
        Assert.Equal(new TextPosition(0, 0), viewA.Controller.Caret); // the recording navigation opens nothing itself
    }

    [AvaloniaFact]
    public async Task A_bookmark_whose_file_is_gone_is_dropped_on_the_way_and_the_next_one_is_taken()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("x0\nx1\n", "here.cs");
        var gone = new Uri(host.Dir.Combine("deleted-outside.cs"));
        var other = new Uri(host.Dir.WriteFile("other.cs", "o0\no1\no2\n"));
        var service = Bookmarks(host);
        service.Toggle(doc.Uri, 0);
        service.Toggle(gone, 4);
        service.Toggle(other, 2);
        view.Controller.SetSelection(TextRange.At(0, 0));
        // in path order: deleted-outside.cs, here.cs, other.cs
        await host.Commands.ExecuteAsync(BookmarkCommandIds.JumpToNext); // here.cs:0 -> other.cs:2
        Assert.Equal((other, (TextRange?)TextRange.At(2, 0)), host.Navigation.Opened[^1]);
        view.Controller.SetSelection(TextRange.At(1, 0));
        await host.Commands.ExecuteAsync(BookmarkCommandIds.JumpToPrevious); // here.cs:1 -> here.cs:0 (the caret moves)
        Assert.Equal(new TextPosition(0, 0), view.Controller.Caret);
        await host.Commands.ExecuteAsync(BookmarkCommandIds.JumpToPrevious); // before here.cs:0 is deleted-outside.cs: gone, so the one before it (wrapping) is other.cs
        Assert.DoesNotContain(service.All, b => b.Uri == gone);
        Assert.Contains(host.Window.StatusMessages, m => m.Contains("deleted-outside.cs") && m.Contains("no longer exists"));
        Assert.Equal((other, (TextRange?)TextRange.At(2, 0)), host.Navigation.Opened[^1]);
        Assert.DoesNotContain(host.Navigation.Opened, o => o.Uri == gone);
    }

    private sealed class PickWindow(RecordingWindowService inner) : IWindowService
    {
        public List<QuickPickItem> Shown { get; } = [];
        public int? Pick { get; set; }
        public ValueTask<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default)
        {
            Shown.Clear();
            Shown.AddRange(items);
            return ValueTask.FromResult(Pick is { } i ? items[i] : null);
        }
        public ValueTask<string?> ShowMessageAsync(MessageSeverity severity, string message, IReadOnlyList<MessageAction>? actions = null, CancellationToken ct = default) => inner.ShowMessageAsync(severity, message, actions, ct);
        public ValueTask<IReadOnlyList<QuickPickItem>?> ShowQuickPickManyAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) => inner.ShowQuickPickManyAsync(items, options, ct);
        public ValueTask<string?> ShowInputBoxAsync(InputBoxOptions? options = null, CancellationToken ct = default) => inner.ShowInputBoxAsync(options, ct);
        public ValueTask<IReadOnlyList<Uri>?> ShowOpenDialogAsync(OpenDialogOptions options, CancellationToken ct = default) => inner.ShowOpenDialogAsync(options, ct);
        public ValueTask<Uri?> ShowSaveDialogAsync(SaveDialogOptions options, CancellationToken ct = default) => inner.ShowSaveDialogAsync(options, ct);
        public Task<T> WithProgressAsync<T>(string title, bool cancellable, Func<IProgressReporter, CancellationToken, Task<T>> work) => inner.WithProgressAsync(title, cancellable, work);
        public IDisposable SetStatusBarMessage(string text, TimeSpan? timeout = null) => inner.SetStatusBarMessage(text, timeout);
        public ValueTask<bool> OpenExternalAsync(Uri uri) => inner.OpenExternalAsync(uri);
        public ValueTask CopyToClipboardAsync(string text) => inner.CopyToClipboardAsync(text);
    }

    [AvaloniaFact]
    public async Task List_Bookmarks_shows_every_bookmark_with_its_file_line_and_text_and_goes_to_the_one_picked()
    {
        var picks = new PickWindow(new RecordingWindowService());
        using var host = ViewHost.Create(s => s.AddSingleton<IWindowService>(picks));
        var (doc, view, _) = await host.OpenAsync("int a;\n    int b;\nint c;\n", "open.cs");
        var closed = new Uri(host.Dir.WriteFile("closed.cs", "﻿first\r\n  second line\r\n"));
        var service = Bookmarks(host);
        service.Toggle(doc.Uri, 1);
        service.Add(doc.Uri, 2, "the c field");
        service.Toggle(closed, 1);

        picks.Pick = 2;
        await host.Commands.ExecuteAsync(BookmarkCommandIds.List);
        Assert.Equal(["second line", "int b;", "the c field"], picks.Shown.Select(i => i.Label)); // closed.cs sorts first; text trimmed; a label wins
        Assert.Equal(["closed.cs:2", "open.cs:2", "open.cs:3"], picks.Shown.Select(i => i.Description));
        Assert.Equal("int c;", picks.Shown[2].Detail); // with a label, the line's text is the detail
        Assert.Equal(new TextPosition(2, 0), view.Controller.Caret);

        picks.Pick = 0;
        await host.Commands.ExecuteAsync(BookmarkCommandIds.List);
        Assert.Equal((closed, (TextRange?)TextRange.At(1, 0)), host.Navigation.Opened[^1]);
    }

    [AvaloniaFact]
    public async Task Clear_All_asks_first_and_clears_every_document()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("a\nb\n", "a.cs");
        var service = Bookmarks(host);
        service.Toggle(doc.Uri, 0);
        service.Toggle(new Uri(host.Dir.Combine("elsewhere.cs")), 3);
        host.Window.MessageActionResponses.Enqueue("Cancel");
        await host.Commands.ExecuteAsync(BookmarkCommandIds.ClearFromAllFiles);
        Assert.Equal(2, service.All.Count);
        Assert.Contains(host.Window.Messages, m => m.Message.Contains("delete all 2 bookmarks"));
        host.Window.MessageActionResponses.Enqueue("Delete");
        await host.Commands.ExecuteAsync(BookmarkCommandIds.ClearFromAllFiles);
        Assert.Empty(service.All);
        Assert.Empty(LinesOf(view));
    }

    // ------------------------------------------------------------------ documents

    [AvaloniaFact]
    public async Task An_untitled_buffer_s_bookmarks_follow_it_when_it_is_saved_as_a_file()
    {
        using var host = ViewHost.Create();
        var doc = (EditorDocument)host.Factory.CreateUntitled("plaintext");
        var view = (CodeEditorControl)host.Factory.CreateView(doc);
        var window = new Window { Width = 600, Height = 400, Content = view };
        window.Show();
        ViewHost.Pump();
        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "one\ntwo\nthree\n")]);
        var service = Bookmarks(host);
        service.Toggle(doc.Uri, 2);
        Assert.Equal([2], LinesOf(view));

        var target = new Uri(host.Dir.Combine("saved.txt"));
        await doc.SaveAsAsync(target);
        ViewHost.Pump();
        Assert.Equal([(target, 2)], service.All.Select(b => (b.Uri, b.Line)));
        Assert.Equal([2], LinesOf(view)); // the view's glyph stayed
        window.Close();
        host.Factory.DisposeView(view);
    }

    [AvaloniaFact]
    public async Task A_large_file_keeps_its_bookmarks_in_a_margin_of_their_own()
    {
        var (_, host) = DebugHost();
        using var _h = host;
        host.Config.Set(EditorSettingKeys.LargeFileThresholdBytes, 1000);
        var (doc, view, _) = await host.OpenAsync(string.Join("\n", Enumerable.Repeat("int x = 1;", 400)), "big.cs");
        Assert.True(doc.IsLargeFile);
        Assert.Null(view.Feature<DebugFeature>()); // large-file mode: no language feature, no breakpoint margin
        Bookmarks(host).Toggle(doc.Uri, 10);
        ViewHost.Pump();
        Assert.Equal([10], LinesOf(view));
        Assert.Equal(BookmarkFeature.MarginWidth, FeatureOf(view).Margin.Bounds.Width);
    }

    [AvaloniaFact]
    public async Task The_right_click_menu_offers_add_or_remove_and_enable_or_disable_for_the_caret_line()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("a\nb\nc\n", "a.cs");
        var keys = (TestContextKeyService)host.Provider.GetRequiredService<IContextKeyService>();
        var items = host.Factory.Services.Contributions.Of<MenuContribution>().Items.Select(c => c.Value).Where(m => m.MenuId == BookmarkCommands.Submenu).ToList();
        IEnumerable<string?> Visible() => items.Where(i => keys.Evaluate(i.When?.Split(" && ")[0]) && (i.When?.Split(" && ") is not [_, var second] || keys.Evaluate(second))).Select(i => i.Title);

        view.Controller.SetSelection(TextRange.At(1, 0));
        EditorMenus.PrepareContextKeys(view);
        Assert.Equal(true, keys.Get(BookmarkCommands.CanBookmarkKey));
        Assert.Contains("_Add Bookmark", Visible());
        Assert.DoesNotContain("_Remove Bookmark", Visible());

        Bookmarks(host).Toggle(doc.Uri, 1);
        EditorMenus.PrepareContextKeys(view);
        Assert.Contains("_Remove Bookmark", Visible());
        Assert.Contains("_Disable Bookmark", Visible());
        Assert.Contains("_Clear Bookmarks in Document", Visible());
        Assert.DoesNotContain("_Add Bookmark", Visible());
    }

    // ------------------------------------------------------------------ memory

    [AvaloniaFact]
    public async Task A_closed_view_with_bookmarks_is_not_retained_and_stops_listening()
    {
        using var host = ViewHost.Create();
        var service = Bookmarks(host);
        await service.Loaded;
        var baseline = service.ChangedSubscriberCount;
        WeakReference? document = null;
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var (doc, view, window) = await host.OpenAsync("a\nb\nc\nd\n", "leak.cs");
            var feature = FeatureOf(view);
            view.Controller.SetSelection(TextRange.At(2, 0));
            await host.Commands.ExecuteAsync(BookmarkCommandIds.Toggle);
            doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "\n")]);
            ViewHost.Pump();
            Assert.Equal(baseline + 1, service.ChangedSubscriberCount);
            host.Close(window);
            host.Factory.DisposeView(view);
            await host.Factory.CloseDocumentAsync(doc);
            ViewHost.Pump();
            document = new WeakReference(doc);
            return [view, feature];
        });
        Assert.Equal(baseline, service.ChangedSubscriberCount);
        Assert.Equal(3, service.All.Single().Line); // the bookmark itself stays, for when the file opens again

        // The closed document goes too. A document is held for a moment after it closes whether it has bookmarks or not (the editor's own
        // dispatcher and watcher work), so this pumps the UI between collections, as the editor's other view-release tests do.
        for (var i = 0; i < 40 && document!.IsAlive; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            ViewHost.Pump();
            await Task.Delay(25);
        }
        Assert.False(document!.IsAlive, document.Target is { } alive ? RetentionPath.Find([host.Provider, host.Factory, host.Documents, service, Avalonia.Threading.Dispatcher.UIThread], alive) : "");
    }
}
