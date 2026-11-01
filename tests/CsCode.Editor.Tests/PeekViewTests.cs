using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using CsCode.Core.Contributions;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using CsCode.TestDoubles;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;
using Location = CsCode.Core.Languages.Location;

namespace CsCode.Editor.Tests;

/// <summary>Peek Definition / References: the embedded editor under the line, the zone it reserves, the list, the keys, the documents it uses.</summary>
public class PeekViewTests
{
    private static readonly string Source = string.Join("\n", Enumerable.Range(0, 60).Select(i => i == 5 ? "    var total = Compute(1, 2);" : $"    // line {i}"));
    private static readonly string Target = string.Join("\n", Enumerable.Range(0, 40).Select(i => i == 20 ? "static int Compute(int a, int b) => a + b;" : $"// target {i}"));

    private static PeekFeature Peek(CodeEditorControl v) => v.Feature<PeekFeature>()!;

    private static LocationLink Link(Uri uri, int line, int from, int to) => new(uri, TextRange.FromLines(line, 0, line + 1, 0), TextRange.FromLines(line, from, line, to));

    private static async Task<(ViewHost Host, CodeEditorControl View, Window Window, Uri TargetUri)> OpenWithDefinitionAsync(bool targetOpen = false)
    {
        var host = ViewHost.Create();
        var targetPath = host.Dir.WriteFile("Target.cs", Target);
        var targetUri = new Uri(targetPath);
        if (targetOpen) await host.Factory.OpenDocumentAsync(targetUri);
        host.Factory.Services.Features.Register<IDefinitionProvider>(DocumentSelector.All,
            new FakeDefinitionProvider((_, _, _) => Task.FromResult<IReadOnlyList<LocationLink>?>([Link(targetUri, 20, 11, 18)])));
        var (_, view, window) = await host.OpenAsync(Source, "Program.cs", height: 700);
        view.Controller.SetSelection(TextRange.At(5, 18)); // on Compute
        ViewHost.Pump();
        return (host, view, window, targetUri);
    }

    private static async Task OpenPeekAsync(ViewHost host, CodeEditorControl view, string command = PeekFeature.PeekDefinitionCommand)
    {
        await host.Commands.ExecuteAsync(command);
        await ViewHost.WaitFor(() => Peek(view).Embedded is not null, "the peek's editor");
        await Peek(view).Shown;
        ViewHost.Pump();
    }

    [AvaloniaFact]
    public async Task Peek_definition_embeds_the_target_under_the_line_and_moves_the_lines_below_down()
    {
        var (host, view, _, targetUri) = await OpenWithDefinitionAsync();
        using var _h = host;
        var tv = view.TextView;
        var lineHeight = tv.DefaultLineHeight;
        var belowBefore = tv.GetVisualTopByDocumentLine(7);

        await OpenPeekAsync(host, view);

        var peek = Peek(view);
        Assert.True(peek.IsOpen);
        Assert.Equal(5, peek.AnchorLine);
        // the zone: line 7 (1-based, the line after the anchor) is pushed down by the widget's height; the anchor line's text stays where it was
        Assert.InRange(tv.GetVisualTopByDocumentLine(7) - (belowBefore + peek.ZoneHeight), -0.01, 1); // the spacer is laid out in whole pixels
        Assert.Equal(5 * lineHeight, tv.GetVisualTopByDocumentLine(6), 1);
        // the widget sits right under the anchor line's text, across the editor, in the reserved zone
        var widget = peek.Widget!;
        var widgetTop = widget.TranslatePoint(default, tv)!.Value.Y;
        Assert.InRange(widgetTop - (tv.GetVisualTopByDocumentLine(6) + lineHeight - tv.ScrollOffset.Y), -1, 1); // layout rounds to whole pixels
        Assert.Equal(peek.ZoneHeight, widget.Bounds.Height, 1);
        Assert.True(widget.Bounds.Width > tv.Bounds.Width * 0.9);

        // a read-only editor over the file (it is not open in the shell), at the definition
        var embedded = peek.Embedded!;
        Assert.Equal(targetUri, embedded.Document.Uri);
        Assert.True(embedded.Document.IsReadOnly);
        Assert.Equal(Target, embedded.Document.GetText());
        Assert.Equal(new TextPosition(20, 11), embedded.Controller.Caret);
        await ViewHost.WaitFor(() => Math.Abs(embedded.Controller.TopLine - 17) < 0.5, "the definition near the top of the embedded editor");
        Assert.Contains("Target.cs", TextOf(widget));
        Assert.False(peek.List!.IsVisible); // one location: no list
        Assert.Null(host.Documents.Get(targetUri)); // nothing registered: no language server sees the peeked file
    }

    [AvaloniaFact]
    public async Task Escape_closes_the_peek_from_the_view_or_from_the_embedded_editor_and_the_zone_goes()
    {
        var (host, view, window, _) = await OpenWithDefinitionAsync();
        using var _h = host;
        var tv = view.TextView;
        var below = tv.GetVisualTopByDocumentLine(7);

        await OpenPeekAsync(host, view);
        Key(window, Avalonia.Input.Key.Escape);
        ViewHost.Pump();
        Assert.False(Peek(view).IsOpen);
        Assert.Equal(below, tv.GetVisualTopByDocumentLine(7), 1);
        Assert.Empty(view.WidgetLayer.Children);

        await OpenPeekAsync(host, view);
        var embedded = Peek(view).Embedded!;
        embedded.TextArea.Focus();
        ViewHost.Pump();
        Key(window, Avalonia.Input.Key.Escape);
        ViewHost.Pump();
        Assert.False(Peek(view).IsOpen);
        Assert.True(view.TextArea.IsKeyboardFocusWithin); // back in the view
        Assert.True(embedded.IsDisposed);
        Assert.DoesNotContain(embedded, host.Factory.Views);
    }

    [AvaloniaFact]
    public async Task A_modal_layer_closes_the_peek_with_ClosePeek_and_its_popup_closing_escape_leaves_it_open()
    {
        var (host, view, _, _) = await OpenWithDefinitionAsync();
        using var _h = host;
        await OpenPeekAsync(host, view);
        // what the Vim adapter does on every Esc (leaving Insert or Visual mode too): hover and parameter hints close, the peek stays
        Peek(view).Embedded!.Modal.CloseInfoPopups();
        view.Modal.CloseInfoPopups();
        Assert.True(Peek(view).IsOpen);
        Assert.True(Peek(view).Embedded!.Modal.ClosePeek()); // Normal mode's Esc, in the embedded editor
        Assert.False(Peek(view).IsOpen);
        Assert.True(view.TextArea.IsKeyboardFocusWithin);
        await OpenPeekAsync(host, view);
        Assert.True(view.Modal.ClosePeek()); // and in the view itself
        Assert.False(Peek(view).IsOpen);
        Assert.False(view.Modal.ClosePeek()); // nothing open
    }

    [AvaloniaFact]
    public async Task A_document_open_in_the_shell_is_edited_in_place_in_the_peek_with_its_undo()
    {
        var (host, view, _, targetUri) = await OpenWithDefinitionAsync(targetOpen: true);
        using var _h = host;
        await OpenPeekAsync(host, view);
        var embedded = Peek(view).Embedded!;
        var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(targetUri);
        Assert.Same(doc, embedded.Document);
        Assert.False(embedded.Document.IsReadOnly);

        embedded.TextArea.Focus();
        embedded.Controller.SetSelection(TextRange.At(20, 0));
        Type(embedded, "public ");
        Assert.StartsWith("public static int Compute", doc.GetLineText(20));
        embedded.Controller.Undo();
        Assert.Equal("static int Compute(int a, int b) => a + b;", doc.GetLineText(20));

        Peek(view).Close();
        Assert.False(doc.IsReadOnly);
        Assert.Same(doc, host.Factory.TryGetOpenDocument(targetUri)); // the shell's document stays open
    }

    [AvaloniaFact]
    public async Task Several_locations_show_a_list_that_switches_the_embedded_editor_and_enter_opens_the_selected_one()
    {
        using var host = ViewHost.Create();
        var a = new Uri(host.Dir.WriteFile("A.cs", "class A\n{\n    void Run() { }\n}\n"));
        var b = new Uri(host.Dir.WriteFile("B.cs", "class B\n{\n    void Other() { }\n    void Run() { }\n}\n"));
        IReadOnlyList<Location> refs = [new Location(a, TextRange.FromLines(2, 9, 2, 12)), new Location(b, TextRange.FromLines(3, 9, 3, 12))];
        host.Factory.Services.Features.Register<IReferenceProvider>(DocumentSelector.All, new FakeReferenceProvider((_, _) => Task.FromResult<IReadOnlyList<Location>?>(refs)));
        var (_, view, window) = await host.OpenAsync(Source, "Program.cs", height: 700);
        view.Controller.SetSelection(TextRange.At(5, 18));

        await OpenPeekAsync(host, view, PeekFeature.PeekReferencesCommand);
        var peek = Peek(view);
        Assert.True(peek.List!.IsVisible);
        Assert.Equal(2, peek.List.ItemCount);
        Assert.Equal(a, peek.Embedded!.Document.Uri);
        Assert.Contains("1 of 2 references", TextOf(peek.Widget!));
        await ViewHost.WaitFor(() => TextOf(peek.List).Contains("void Run() { }"), "the list's previews");

        peek.List.SelectedIndex = 1;
        await ViewHost.WaitFor(() => peek.Embedded?.Document.Uri == b, "B in the embedded editor");
        await peek.Shown;
        Assert.Equal(new TextPosition(3, 9), peek.Embedded!.Controller.Caret);
        Assert.Contains("2 of 2 references", TextOf(peek.Widget!));

        var item = peek.List.ContainerFromIndex(1)!;
        item.Focus();
        ViewHost.Pump();
        Key(window, Avalonia.Input.Key.Enter);
        await ViewHost.WaitFor(() => host.Navigation.Opened.Count > 0, "navigation");
        Assert.Equal((b, (TextRange?)TextRange.FromLines(3, 9, 3, 12)), host.Navigation.Opened.Single());
        Assert.False(peek.IsOpen);
    }

    [AvaloniaFact]
    public async Task A_peeked_location_in_the_same_file_opens_in_the_view_itself()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync(Source, "Program.cs", height: 700);
        host.Factory.Services.Features.Register<IDefinitionProvider>(DocumentSelector.All,
            new FakeDefinitionProvider((_, _, _) => Task.FromResult<IReadOnlyList<LocationLink>?>([Link(doc.Uri, 40, 4, 9)])));
        view.Controller.SetSelection(TextRange.At(5, 18));
        await OpenPeekAsync(host, view);
        var embedded = Peek(view).Embedded!;
        Assert.Same(doc, embedded.Document); // the same document, a second editor over it
        embedded.TextArea.Focus();
        ViewHost.Pump();
        await Peek(view).OpenSelectedAsync();
        Assert.False(Peek(view).IsOpen);
        Assert.Equal(new TextPosition(40, 4), view.Controller.Selections[0].Start);
        Assert.Empty(host.Navigation.Opened);
    }

    [AvaloniaFact]
    public async Task Enter_or_a_double_click_in_a_read_only_embedded_editor_opens_the_location()
    {
        var (host, view, window, targetUri) = await OpenWithDefinitionAsync();
        using var _h = host;
        await OpenPeekAsync(host, view);
        var embedded = Peek(view).Embedded!;
        embedded.TextArea.Focus();
        ViewHost.Pump();
        Key(window, Avalonia.Input.Key.Enter);
        await ViewHost.WaitFor(() => host.Navigation.Opened.Count == 1, "navigation");
        Assert.Equal(targetUri, host.Navigation.Opened[0].Uri);
        Assert.False(Peek(view).IsOpen);

        await OpenPeekAsync(host, view);
        Peek(view).Embedded!.RaiseEvent(new TappedEventArgs(Avalonia.Input.InputElement.DoubleTappedEvent, null!));
        await ViewHost.WaitFor(() => host.Navigation.Opened.Count == 2, "navigation on double-click");
        Assert.False(Peek(view).IsOpen);
    }

    [AvaloniaFact]
    public async Task Without_a_definition_the_status_bar_says_so_and_no_peek_opens()
    {
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<IDefinitionProvider>(DocumentSelector.All, new FakeDefinitionProvider((_, _, _) => Task.FromResult<IReadOnlyList<LocationLink>?>(null)));
        var (_, view, _) = await host.OpenAsync(Source, "Program.cs");
        view.Controller.SetSelection(TextRange.At(5, 18));
        await host.Commands.ExecuteAsync(PeekFeature.PeekDefinitionCommand);
        Assert.False(Peek(view).IsOpen);
        Assert.Contains("No definition found for 'Compute'", host.Window.StatusMessages);
    }

    [AvaloniaFact]
    public async Task Deleting_the_anchor_line_closes_the_peek_and_edits_above_move_it_with_its_line()
    {
        var (host, view, _, _) = await OpenWithDefinitionAsync();
        using var _h = host;
        await OpenPeekAsync(host, view);
        var doc = view.Document;
        doc.ApplyEdits([new TextEdit(TextRange.At(0, 0), "// new line\n")]);
        ViewHost.Pump();
        Assert.Equal(6, Peek(view).AnchorLine);
        var tv = view.TextView;
        var widgetTop = Peek(view).Widget!.TranslatePoint(default, tv)!.Value.Y;
        Assert.InRange(widgetTop - (tv.GetVisualTopByDocumentLine(7) + tv.DefaultLineHeight - tv.ScrollOffset.Y), -1, 1);

        doc.ApplyEdits([new TextEdit(TextRange.FromLines(5, 0, 7, 0), "")]); // the anchor line goes
        ViewHost.Pump();
        Assert.False(Peek(view).IsOpen);
    }

    [AvaloniaFact]
    public async Task The_peek_definition_item_of_the_right_click_menu_shows_now_that_the_command_exists()
    {
        using var host = ViewHost.Create();
        var item = host.Factory.Services.Contributions.Of<MenuContribution>().Items.Select(i => i.Value).Single(i => i.Command == PeekFeature.PeekDefinitionCommand);
        Assert.Equal(MenuIds.EditorContext, item.MenuId);
        Assert.Equal("editorHasDefinitionProvider", item.When);
        Assert.True(host.Commands.Has(PeekFeature.PeekDefinitionCommand)); // an unregistered command hid it
        foreach (var id in new[] { PeekFeature.PeekReferencesCommand, PeekFeature.PeekDeclarationCommand, PeekFeature.PeekTypeDefinitionCommand, PeekFeature.PeekImplementationCommand, PeekFeature.CloseCommand })
            Assert.True(host.Commands.Has(id), id);
    }

    [AvaloniaFact]
    public async Task Closing_the_view_with_a_peek_open_releases_the_embedded_editor_and_its_document()
    {
        var (host, view, window, _) = await OpenWithDefinitionAsync();
        using var _h = host;
        var weak = await OpenAndCloseAsync(host, view, window, closeView: false); // the view lives on: nothing of the peek may stay with it
        for (var i = 0; i < 3; i++) weak.AddRange(await OpenAndCloseAsync(host, view, window, closeView: false));
        // the view goes with its peek open: the embedded editor is disposed with it (what keeps it then is the destroyed view this test holds)
        await OpenAndCloseAsync(host, view, window, closeView: true);
        for (var attempt = 0; attempt < 40 && weak.Any(w => w.IsAlive); attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            ViewHost.Pump();
            await Task.Delay(20);
        }
        var alive = weak.FirstOrDefault(w => w.IsAlive)?.Target;
        Assert.True(alive is null, alive is null ? "" : "still reachable: " + alive.GetType().Name + "\n" +
            RetentionPath.Find([host.Provider, host.Factory, host.Documents, view, window, Avalonia.Threading.Dispatcher.UIThread, Avalonia.Application.Current!], alive));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference>> OpenAndCloseAsync(ViewHost host, CodeEditorControl view, Window window, bool closeView)
    {
        await OpenPeekAsync(host, view);
        var embedded = Peek(view).Embedded!;
        var weak = new List<WeakReference> { new(embedded), new(embedded.Document) };
        if (closeView)
        {
            host.Factory.DisposeView(view);
            ViewHost.Pump();
            window.Content = null;
        }
        else Peek(view).Close();
        ViewHost.Pump(); // a frame: the composition tree lets go of the removed widget
        Assert.True(embedded.IsDisposed);
        Assert.DoesNotContain(embedded, host.Factory.Views);
        return weak;
    }

    [AvaloniaFact]
    public async Task The_peek_is_drawn_in_the_view_screenshot()
    {
        var (host, view, window, _) = await OpenWithDefinitionAsync();
        using var _h = host;
        await OpenPeekAsync(host, view);
        ViewHost.Pump();
        var frame = window.CaptureRenderedFrame()!;
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.scratch/screenshots"));
        Directory.CreateDirectory(dir);
        using (var fs = File.Create(Path.Combine(dir, "editor-peek-definition.png"))) frame.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        // the peek's border colour (peekView.border, #3794FF in the dark themes) is on screen under the anchor line
        var widget = Peek(view).Widget!;
        var top = widget.TranslatePoint(default, window)!.Value;
        var region = new PixelRect((int)top.X + 20, (int)top.Y, 200, 2);
        Assert.True(CountPixels(frame, Avalonia.Media.Color.Parse("#3794FF"), 40, region) > 50, "the peek border is drawn");
    }
}
