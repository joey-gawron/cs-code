using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit.Editing;
using CsCode.Core.Contributions;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using CsCode.Platform;
using CsCode.Platform.Context;
using CsCode.Platform.Io;
using CsCode.Platform.Keybindings;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// Clipboard brief A1/A3/A4/A5 and B1 in real (headless) views: the clipboard keys, Visual Studio's whole-line and block behaviours, one undo
/// step per paste, the clipboard ring, PRIMARY and middle-click, the right-click caret rule, the editor's menu contributions and context keys.
/// </summary>
public class EditorClipboardViewTests
{
    private static (ViewHost Host, InMemoryClipboardService Clipboard) Host(Action<IServiceCollection>? more = null)
    {
        var clipboard = new InMemoryClipboardService();
        return (ViewHost.Create(s => { s.AddSingleton<IClipboardService>(clipboard); more?.Invoke(s); }), clipboard);
    }

    /// <summary>The clipboard keys' modifier: Ctrl, ⌘ on macOS.</summary>
    private static RawInputModifiers Command => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static void Key(Avalonia.Controls.Window w, Key key, RawInputModifiers m = RawInputModifiers.None)
    {
        w.KeyPress(key, m, PhysicalKey.None, null);
        ViewHost.Pump();
    }

    [AvaloniaFact]
    public async Task Ctrl_C_on_a_caret_copies_the_line_and_Ctrl_V_pastes_it_above_the_caret_line_in_one_undo_step()
    {
        var (host, clipboard) = Host();
        using var _ = host;
        var (doc, view, window) = await host.OpenAsync("alpha\nbeta\ngamma\n");
        view.Controller.SetSelection(TextRange.At(0, 2));
        Key(window, Avalonia.Input.Key.C, Command);
        Assert.Equal("alpha\n", clipboard.Content?.Text);
        Assert.Equal(ClipboardTextShape.FullLines, clipboard.Content?.Shape);

        view.Controller.SetSelection(TextRange.At(2, 3));
        Key(window, Avalonia.Input.Key.V, Command);
        await ViewHost.WaitFor(() => doc.GetText() == "alpha\nbeta\nalpha\ngamma\n", "line pasted above");
        Assert.Equal(new TextPosition(3, 3), view.Controller.Caret); // still on "gamma"

        view.Controller.Undo();
        Assert.Equal("alpha\nbeta\ngamma\n", doc.GetText());
    }

    [AvaloniaFact]
    public async Task Ctrl_X_on_a_caret_cuts_the_line()
    {
        var (host, clipboard) = Host();
        using var _ = host;
        var (doc, view, window) = await host.OpenAsync("one\ntwo\nthree\n");
        view.Controller.SetSelection(TextRange.At(1, 1));
        Key(window, Avalonia.Input.Key.X, Command);
        Assert.Equal("one\nthree\n", doc.GetText());
        Assert.Equal("two\n", clipboard.Content?.Text);
        view.Controller.Undo();
        Assert.Equal("one\ntwo\nthree\n", doc.GetText());
    }

    [AvaloniaFact]
    public async Task Ctrl_Insert_Shift_Insert_and_Shift_Delete_copy_paste_and_cut()
    {
        var (host, clipboard) = Host();
        using var _ = host;
        var (doc, view, window) = await host.OpenAsync("hello world");
        view.Controller.SetSelection(TextRange.FromLines(0, 0, 0, 5));
        Key(window, Avalonia.Input.Key.Insert, RawInputModifiers.Control);
        Assert.Equal("hello", clipboard.Content?.Text);

        view.Controller.SetSelection(TextRange.At(0, 11));
        Key(window, Avalonia.Input.Key.Insert, RawInputModifiers.Shift);
        await ViewHost.WaitFor(() => doc.GetText() == "hello worldhello", "Shift+Insert pasted");

        view.Controller.SetSelection(TextRange.FromLines(0, 5, 0, 11));
        Key(window, Avalonia.Input.Key.Delete, RawInputModifiers.Shift);
        Assert.Equal("hellohello", doc.GetText());
        Assert.Equal(" world", clipboard.Content?.Text);
    }

    [AvaloniaFact]
    public async Task Paste_from_another_application_converts_its_line_endings_to_the_documents()
    {
        var (host, clipboard) = Host();
        using var _ = host;
        var (doc, view, _) = await host.OpenAsync("start\n", "notes.txt");
        clipboard.SetExternal("x\r\ny\r\n");
        view.Controller.SetSelection(TextRange.At(1, 0));
        await view.Clipboard.PasteAsync();
        Assert.Equal("start\nx\ny\n", doc.GetText());
        Assert.DoesNotContain('\r', doc.GetText());
    }

    [AvaloniaFact]
    public async Task Paste_never_blocks_the_UI_thread_while_the_clipboard_owner_takes_its_time()
    {
        var (host, clipboard) = Host();
        using var _ = host;
        var (doc, view, _) = await host.OpenAsync("abc");
        clipboard.SetExternal("XYZ");
        var release = new TaskCompletionSource();
        clipboard.ReadGate = release.Task;
        view.Controller.SetSelection(TextRange.At(0, 3));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var paste = view.Clipboard.PasteAsync();
        Assert.True(started.ElapsedMilliseconds < 50, "starting a paste returned quickly");
        var ran = false;
        Dispatcher.UIThread.Post(() => ran = true);
        await ViewHost.WaitFor(() => ran, "a UI job while the read is pending");
        Assert.False(paste.IsCompleted);
        Assert.Equal("abc", doc.GetText());
        release.SetResult();
        await paste;
        Assert.Equal("abcXYZ", doc.GetText());
    }

    [AvaloniaFact]
    public async Task A_10_MB_paste_goes_in_whole_and_one_undo_removes_it()
    {
        var (host, clipboard) = Host();
        using var _ = host;
        var (doc, view, _) = await host.OpenAsync("head\n", "big.txt");
        var line = new string('x', 99) + "\n";
        var big = string.Concat(Enumerable.Repeat(line, 100_000)); // 10,000,000 characters
        clipboard.SetExternal(big);
        view.Controller.SetSelection(TextRange.At(1, 0));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await view.Clipboard.PasteAsync();
        sw.Stop();
        Assert.Equal(5 + big.Length, doc.Length);
        Assert.True(sw.ElapsedMilliseconds < 10_000, $"10 MB paste took {sw.ElapsedMilliseconds} ms");
        view.Controller.Undo();
        Assert.Equal("head\n", doc.GetText());
    }

    [AvaloniaFact]
    public async Task Ctrl_Shift_V_cycles_the_clipboard_ring_in_place_and_one_undo_removes_the_whole_cycle()
    {
        var (host, clipboard) = Host();
        using var _ = host;
        var (doc, view, window) = await host.OpenAsync("one two three\n\n");
        foreach (var (s, e) in new[] { (0, 3), (4, 7), (8, 13) })
        {
            view.Controller.SetSelection(TextRange.FromLines(0, s, 0, e));
            await view.Clipboard.CopyAsync();
        }
        Assert.Equal(["three", "two", "one"], clipboard.Ring.Select(r => r.Text));

        view.Controller.SetSelection(TextRange.At(1, 0));
        Key(window, Avalonia.Input.Key.V, Command | RawInputModifiers.Shift);
        Assert.Equal("one two three\nthree\n", doc.GetText());
        Key(window, Avalonia.Input.Key.V, Command | RawInputModifiers.Shift);
        Assert.Equal("one two three\ntwo\n", doc.GetText());
        Key(window, Avalonia.Input.Key.V, Command | RawInputModifiers.Shift);
        Assert.Equal("one two three\none\n", doc.GetText());
        Key(window, Avalonia.Input.Key.V, Command | RawInputModifiers.Shift);
        Assert.Equal("one two three\nthree\n", doc.GetText()); // wraps around

        view.Controller.Undo();
        Assert.Equal("one two three\n\n", doc.GetText());

        // Typing in between ends the cycle: the next Ctrl+Shift+V pastes the newest entry again.
        view.Controller.SetSelection(TextRange.At(1, 0));
        Key(window, Avalonia.Input.Key.V, Command | RawInputModifiers.Shift);
        ViewTestHelpers.Type(view, "!");
        Key(window, Avalonia.Input.Key.V, Command | RawInputModifiers.Shift);
        Assert.Equal("one two three\nthree!three\n", doc.GetText());
    }

    [AvaloniaFact]
    public async Task A_box_selection_copies_as_a_block_and_pastes_as_a_block()
    {
        var (host, clipboard) = Host();
        using var _ = host;
        var (doc, view, _) = await host.OpenAsync("abcd\nefgh\nijkl\n\n", "box.txt");
        var area = view.TextArea;
        area.Selection = new RectangleSelection(area, new AvaloniaEdit.TextViewPosition(1, 2), new AvaloniaEdit.TextViewPosition(3, 4));
        await view.Clipboard.CopyAsync();
        Assert.Equal(ClipboardTextShape.Block, clipboard.Content?.Shape);
        Assert.Equal("bc\nfg\njk", clipboard.Content?.Text);

        view.Controller.SetSelection(TextRange.At(0, 4));
        await view.Clipboard.PasteAsync();
        Assert.Equal("abcdbc\nefghfg\nijkljk\n\n", doc.GetText());
        view.Controller.Undo();
        Assert.Equal("abcd\nefgh\nijkl\n\n", doc.GetText());
    }

    [AvaloniaFact]
    public async Task Right_click_moves_the_caret_unless_it_lands_inside_the_selection()
    {
        var (host, _) = Host();
        using var h = host;
        var (_, view, window) = await host.OpenAsync("first line here\nsecond line here\n");
        view.Controller.SetSelection(TextRange.FromLines(0, 0, 0, 5));
        ViewHost.Pump();

        // Inside the selection: the selection stays (the menu acts on it).
        window.MouseDown(ViewTestHelpers.PointOf(window, view, 2), MouseButton.Right);
        window.MouseUp(ViewTestHelpers.PointOf(window, view, 2), MouseButton.Right);
        ViewHost.Pump();
        Assert.Equal(TextRange.FromLines(0, 0, 0, 5), view.Controller.Selections[0]);

        // Outside: the caret moves to the click and the selection goes.
        var target = view.Document.OffsetAt(new TextPosition(1, 7));
        window.MouseDown(ViewTestHelpers.PointOf(window, view, target), MouseButton.Right);
        window.MouseUp(ViewTestHelpers.PointOf(window, view, target), MouseButton.Right);
        ViewHost.Pump();
        Assert.True(view.Controller.Selections[0].IsEmpty);
        Assert.Equal(1, view.Controller.Caret.Line);
        Assert.InRange(view.Controller.Caret.Character, 6, 8);
    }

    [AvaloniaFact]
    public async Task Selecting_offers_the_text_as_PRIMARY_and_a_middle_click_pastes_PRIMARY_at_the_click()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "editor.selectionClipboard (PRIMARY, middle-click paste) is Linux-only, as in VS Code (EditorSettings)");
        var (host, clipboard) = Host();
        using var h = host;
        host.Config.Set("editor.selectionClipboard", true);
        var (doc, view, window) = await host.OpenAsync("pick me\nand here: \n");
        view.Controller.SetSelection(TextRange.FromLines(0, 0, 0, 7));
        ViewHost.Pump();
        Assert.Equal("pick me", clipboard.OfferedPrimary);
        Assert.Equal(0, clipboard.Writes); // PRIMARY is not the clipboard

        var at = view.Document.OffsetAt(new TextPosition(1, 10));
        window.MouseDown(ViewTestHelpers.PointOf(window, view, at), MouseButton.Middle);
        window.MouseUp(ViewTestHelpers.PointOf(window, view, at), MouseButton.Middle);
        await ViewHost.WaitFor(() => doc.GetText().Contains("here: pick me"), "middle-click paste");
        Assert.Equal("pick me\nand here: pick me\n", doc.GetText());

        // Another application's PRIMARY, with Windows line endings, lands with the document's.
        clipboard.SetExternalPrimary("x\r\ny");
        await view.Clipboard.PastePrimaryAsync(0);
        Assert.StartsWith("x\ny", doc.GetText());
        view.Controller.Undo();
        Assert.Equal("pick me\nand here: pick me\n", doc.GetText());
    }

    [AvaloniaFact]
    public async Task Middle_click_does_nothing_when_editor_selectionClipboard_is_off()
    {
        var (host, clipboard) = Host();
        using var h = host;
        host.Config.Set("editor.selectionClipboard", false);
        var (doc, view, window) = await host.OpenAsync("text\n");
        clipboard.SetExternalPrimary("PRIMARY");
        view.Controller.SetSelection(TextRange.FromLines(0, 0, 0, 4));
        ViewHost.Pump();
        Assert.Null(clipboard.OfferedPrimary);
        window.MouseDown(ViewTestHelpers.PointOf(window, view, 2), MouseButton.Middle);
        window.MouseUp(ViewTestHelpers.PointOf(window, view, 2), MouseButton.Middle);
        ViewHost.Pump();
        Assert.Equal("text\n", doc.GetText());
    }

    [AvaloniaFact]
    public async Task Format_on_paste_formats_the_pasted_range_as_its_own_undo_step()
    {
        var (host, clipboard) = Host();
        using var h = host;
        host.Config.Set("editor.formatOnPaste", true);
        TextRange? formatted = null;
        var provider = new FakeFormattingProvider((_, _) => Task.FromResult<IReadOnlyList<TextEdit>?>(null),
            range: (d, r) => { formatted = r; return Task.FromResult<IReadOnlyList<TextEdit>?>([new TextEdit(r, "formatted()")]); });
        using var reg = host.Factory.Services.Features.Register<IDocumentFormattingProvider>(DocumentSelector.All, provider);
        var (doc, view, _) = await host.OpenAsync("x;\n");
        clipboard.SetExternal("f( )");
        view.Controller.SetSelection(TextRange.At(1, 0));
        await view.Clipboard.PasteAsync();
        await ViewHost.WaitFor(() => doc.GetText() == "x;\nformatted()", "formatted paste");
        Assert.Equal(TextRange.FromLines(1, 0, 1, 4), formatted);
        view.Controller.Undo();
        Assert.Equal("x;\nf( )", doc.GetText());
        view.Controller.Undo();
        Assert.Equal("x;\n", doc.GetText());
    }

    [AvaloniaFact]
    public async Task A_read_only_document_copies_but_neither_cuts_nor_pastes()
    {
        var (host, clipboard) = Host();
        using var h = host;
        var (doc, view, window) = await host.OpenAsync("keep\n");
        doc.IsReadOnly = true;
        view.Controller.SetSelection(TextRange.FromLines(0, 0, 0, 4));
        Key(window, Avalonia.Input.Key.X, Command);
        Assert.Equal("keep\n", doc.GetText());
        Key(window, Avalonia.Input.Key.C, Command);
        Assert.Equal("keep", clipboard.Content?.Text);
        Key(window, Avalonia.Input.Key.V, Command);
        ViewHost.Pump();
        Assert.Equal("keep\n", doc.GetText());
        Assert.False(view.Controller.CanCut);
        Assert.False(view.Controller.CanPaste);
        Assert.True(view.Controller.CanCopy);
    }

    [AvaloniaFact]
    public async Task The_clipboard_commands_act_on_the_focused_view()
    {
        var (host, clipboard) = Host();
        using var h = host;
        var (doc, view, _) = await host.OpenAsync("abc\n");
        view.Controller.SetSelection(TextRange.FromLines(0, 0, 0, 2));
        await host.Commands.ExecuteAsync("editor.action.clipboardCopyAction");
        Assert.Equal("ab", clipboard.Content?.Text);
        view.Controller.SetSelection(TextRange.At(0, 3));
        await host.Commands.ExecuteAsync("editor.action.clipboardPasteAction");
        Assert.Equal("abcab\n", doc.GetText());
        view.Controller.SetSelection(TextRange.FromLines(0, 0, 0, 1));
        await host.Commands.ExecuteAsync("editor.action.clipboardCutAction");
        Assert.Equal("bcab\n", doc.GetText());
        Assert.Equal("a", clipboard.Content?.Text);
    }

    [AvaloniaFact]
    public async Task Without_a_clipboard_service_the_view_uses_its_windows_clipboard()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("one\ntwo\n");
        view.Controller.SetSelection(TextRange.At(0, 1));
        await view.Clipboard.CopyAsync(); // whole line, through Avalonia's (headless) clipboard
        view.Controller.SetSelection(TextRange.At(1, 0));
        await view.Clipboard.PasteAsync();
        Assert.Equal("one\none\ntwo\n", doc.GetText());
    }

    // ---------------------------------------------------------------- B1/B3: what the editor contributes to the shell's menus

    [AvaloniaFact]
    public void The_editor_contributes_its_context_menu_in_Visual_Studio_order_and_the_clipboard_keys()
    {
        var (host, _) = Host();
        using var h = host;
        var registry = host.Factory.Services.Contributions;
        var items = registry.Of<MenuContribution>().Items.Select(i => i.Value).Where(i => i.MenuId == MenuIds.EditorContext)
            .OrderBy(i => i.Group, StringComparer.Ordinal).Select(i => i.Title ?? "(submenu)").ToArray();
        Assert.Equal(
        [
            // inline merge conflicts (shown only while the caret is in a conflict block)
            "Accept _Current Change", "Accept _Incoming Change", "Accept _Both Changes", "Compare C_hanges",
            "Quick Actions and Refactorings...", "Re_name...",
            "Pee_k Definition", "Go To _Definition", "Go To D_eclaration", "Go To _Implementation", "Find All Re_ferences",
            "(submenu)", "(submenu)", "Run To C_ursor", "Add _Watch", "_QuickWatch...", // Breakpoint, then Bookmarks
            "(submenu)",
            "Cu_t", "_Copy", "Copy wit_h Formatting", "_Paste",
            "_Format Document", "Format Document _With...", "Format _Selection", "Co_mment Selection", "_Uncomment Selection",
            "Command _Palette...",
        ], items);
        var breakpoint = registry.Of<MenuContribution>().Items.Select(i => i.Value).Where(i => i.MenuId == EditorMenus.BreakpointSubmenu).Select(i => i.Title);
        Assert.Equal(["Insert _Breakpoint", "_Delete Breakpoint", "Disa_ble Breakpoint", "E_nable Breakpoint", "_Conditions..."], breakpoint);

        var keys = registry.Of<KeybindingContribution>().Items.Select(i => (i.Value.Command, i.Value.Key)).ToArray();
        foreach (var expected in new[]
        {
            ("editor.action.clipboardCopyAction", "ctrl+c"), ("editor.action.clipboardCopyAction", "ctrl+insert"),
            ("editor.action.clipboardCutAction", "ctrl+x"), ("editor.action.clipboardCutAction", "shift+delete"),
            ("editor.action.clipboardPasteAction", "ctrl+v"), ("editor.action.clipboardPasteAction", "shift+insert"),
            ("cscode.editor.cycleClipboardRing", "ctrl+shift+v"),
        })
            Assert.Contains(expected, keys);
    }

    [AvaloniaFact]
    public async Task Requesting_the_menu_brings_the_caret_dependent_context_keys_up_to_date()
    {
        var debug = new FakeDebugService();
        var (host, clipboard) = Host(s => s.AddSingleton<IDebugService>(debug));
        using var h = host;
        var keys = host.Provider.GetRequiredService<IContextKeyService>();
        var (doc, view, _) = await host.OpenAsync("a\nb\nc\n");
        debug.Manager.Add(doc.Uri, 1);
        view.Controller.SetSelection(TextRange.At(1, 0));
        view.TextArea.RaiseEvent(new ContextRequestedEventArgs());
        Assert.Equal(true, keys.Get(EditorMenus.BreakpointsAvailableKey));
        Assert.Equal(true, keys.Get(EditorMenus.LineHasBreakpointKey));
        Assert.Equal(true, keys.Get(EditorMenus.LineBreakpointEnabledKey));
        Assert.Equal(true, keys.Get(EditorMenus.CanCutCopyKey));

        view.Controller.SetSelection(TextRange.At(2, 0));
        view.TextArea.RaiseEvent(new ContextRequestedEventArgs());
        Assert.Equal(false, keys.Get(EditorMenus.LineHasBreakpointKey));

        host.Config.Set("editor.emptySelectionClipboard", false);
        await ViewHost.WaitFor(() => !view.Settings.EmptySelectionClipboard, "setting applied");
        view.TextArea.RaiseEvent(new ContextRequestedEventArgs());
        Assert.Equal(false, keys.Get(EditorMenus.CanCutCopyKey)); // no selection, no whole-line mode: Cut and Copy are hidden
    }

    [AvaloniaFact]
    public async Task Capability_keys_follow_the_language_servers_of_the_focused_document()
    {
        var (host, _) = Host();
        using var h = host;
        var keys = host.Provider.GetRequiredService<IContextKeyService>();
        var (_, view, _) = await host.OpenAsync("class A {}\n");
        Assert.Equal(false, keys.Get("editorHasRenameProvider"));
        using (host.Factory.Services.Features.Register<IRenameProvider>(DocumentSelector.All, new FakeRenameProvider((_, _, _) => Task.FromResult<WorkspaceEdit?>(null))))
        {
            await ViewHost.WaitFor(() => keys.Get("editorHasRenameProvider") is true, "rename provider key");
        }
        await ViewHost.WaitFor(() => keys.Get("editorHasRenameProvider") is false, "rename provider gone");
    }

    [AvaloniaFact]
    public async Task Run_To_Cursor_sets_a_temporary_breakpoint_continues_and_removes_it_at_the_next_stop()
    {
        var debug = new FakeDebugService();
        var (host, _) = Host(s => s.AddSingleton<IDebugService>(debug));
        using var h = host;
        var continued = 0;
        using var reg = host.Commands.Register(KnownCommands.DebugContinue, _ => { continued++; return ValueTask.FromResult<object?>(null); });
        var (doc, view, _) = await host.OpenAsync("a\nb\nc\n");
        debug.Stop(doc.Uri, 0);
        view.Controller.SetSelection(TextRange.At(2, 0));
        await EditorMenus.RunToCursorAsync(view);
        Assert.Equal(1, continued);
        Assert.Contains(debug.Manager.SourceBreakpoints, b => b.Line == 2);
        debug.Resume();
        debug.Stop(doc.Uri, 2);
        Assert.DoesNotContain(debug.Manager.SourceBreakpoints, b => b.Line == 2);
    }

    [AvaloniaFact]
    public async Task Views_with_clipboard_and_menu_activity_are_released_when_disposed()
    {
        var (host, clipboard) = Host();
        using var h = host;
        host.Config.Set("editor.selectionClipboard", true);
        var window = new Avalonia.Controls.Window { Width = 800, Height = 500 };
        window.Show();
        var weak = new List<WeakReference>();
        await CycleAsync(host, clipboard, window, weak);
        for (var attempt = 0; attempt < 60 && weak.Any(w => w.IsAlive); attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            ViewHost.Pump();
            await Task.Delay(50);
        }
        var alive = weak.FirstOrDefault(w => w.IsAlive)?.Target;
        Assert.True(alive is null, alive is null ? "" : "still reachable: " + alive.GetType().Name + "\n" +
            RetentionPath.Find([host.Provider, host.Factory, host.Documents, Dispatcher.UIThread, Avalonia.Application.Current!, clipboard], alive));
        window.Close();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task CycleAsync(ViewHost host, InMemoryClipboardService clipboard, Avalonia.Controls.Window window, List<WeakReference> weak)
    {
        for (var i = 0; i < 20; i++)
        {
            var path = host.Dir.WriteFile($"clip{i}.cs", "abc\ndef\n");
            var doc = (CsCode.Editor.Documents.EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(path));
            var view = (CodeEditorControl)host.Factory.CreateView(doc);
            window.Content = view;
            ViewHost.Pump();
            view.Controller.SetSelection(TextRange.FromLines(0, 0, 0, 3));
            ViewHost.Pump();
            await view.Clipboard.CopyAsync();
            view.Controller.SetSelection(TextRange.At(1, 0));
            await view.Clipboard.PasteAsync();
            await view.Clipboard.CycleRingAsync();
            await view.Clipboard.CycleRingAsync();
            await view.Clipboard.PastePrimaryAsync(0);
            view.TextArea.RaiseEvent(new ContextRequestedEventArgs());
            window.Content = null;
            weak.Add(new WeakReference(view));
            weak.Add(new WeakReference(view.Editor));
            weak.Add(new WeakReference(doc));
            host.Factory.DisposeView(view);
            await host.Factory.CloseDocumentAsync(doc);
        }
        clipboard.SetPrimarySelection(null); // the last PRIMARY offer holds a snapshot of a closed document's text, not the document
        // A middle-click focuses the editor; AvaloniaEdit's static RoutedCommand keeps the last focused element until focus moves (a known,
        // bounded AvaloniaEdit retention, docs/EDITOR.md): move it, as the shell does when a tab closes.
        var other = new Avalonia.Controls.TextBox();
        window.Content = other;
        ViewHost.Pump();
        other.Focus();
        ViewHost.Pump();
        window.Content = null;
    }

    [Fact]
    public void Every_Ctrl_key_the_editor_contributes_has_a_Mac_key()
    {
        var missing = EditorMenus.Keybindings.Where(k => KeyStrokeParser.ParseChord(k.Key).Any(s => s.Ctrl) && k.Mac is null).Select(k => $"{k.Key} → {k.Command}");
        Assert.Empty(missing);
        Assert.Contains(EditorMenus.Keybindings, k => k.Command == EditorMenus.CycleClipboardRing && k.Mac == "cmd+shift+v");
    }

    public static TheoryData<string> PresetNames => [.. KeybindingService.Presets];

    /// <summary>
    /// Ctrl+Shift+V (Cmd+Shift+V) in a Markdown editor: Visual Studio's Cycle Clipboard Ring in the Visual Studio keymap (the key used to open the
    /// Markdown preview there, so a Visual Studio user lost the ring in every .md file), VS Code's Open Preview in the VS Code keymap; elsewhere
    /// the ring in both, and Show Source from a preview in both.
    /// </summary>
    [Theory]
    [InlineData("visualstudio", HostOs.Linux, "ctrl+shift+v", "cscode.editor.cycleClipboardRing")]
    [InlineData("visualstudio", HostOs.MacOs, "cmd+shift+v", "cscode.editor.cycleClipboardRing")]
    [InlineData("vscode", HostOs.Linux, "ctrl+shift+v", "markdown.showPreview")]
    [InlineData("vscode", HostOs.MacOs, "cmd+shift+v", "markdown.showPreview")]
    public async Task Ctrl_Shift_V_in_a_Markdown_editor_follows_the_keymap(string preset, HostOs os, string key, string inMarkdown)
    {
        using var temp = new TempDirectory("cscode-kb-");
        using var lane = new IoLane("test.kb", 1);
        var registry = new ContributionRegistry();
        using var _ = registry.Contribute(ContributionSources.Builtin, "", EditorMenus.Keybindings);
        var paths = new AppPaths(k => k == "CSCODE_HOME" ? temp.Combine("home") : null, os, "/home/test");
        var context = new ContextKeyService();
        using var keys = new KeybindingService(context, paths, lane, registry, os: os);
        await keys.SetPresetAsync(preset, TestContext.Current.CancellationToken);
        string? Resolve()
        {
            keys.ResetChord();
            var r = keys.Resolve(KeyStrokeParser.ParseStroke(key));
            return r.Kind == KeyResolution.Matched ? r.Binding!.Command : null;
        }

        context.Set("editorTextFocus", true);
        context.Set("editorLangId", "markdown");
        Assert.Equal(inMarkdown, Resolve());
        context.Set("editorLangId", "csharp");
        Assert.Equal("cscode.editor.cycleClipboardRing", Resolve());
        context.Set("editorTextFocus", false);
        context.Set("markdownPreviewFocus", true);
        Assert.Equal("markdown.showSource", Resolve());
    }

    /// <summary>
    /// On macOS the presets' Cmd keys and the editor's contributed Cmd keys share one keyboard: two commands on the same keys whose `when`
    /// clauses can hold together make one unreachable, and a key that is also the first stroke of a chord makes the chord unreachable (or
    /// the key, depending on precedence). Two `when` clauses exclude each other only when one negates a term of the other.
    /// </summary>
    [Theory]
    [MemberData(nameof(PresetNames))]
    public async Task On_macOS_no_preset_key_clashes_with_a_key_the_editor_contributes(string preset)
    {
        using var temp = new TempDirectory("cscode-kb-");
        using var lane = new IoLane("test.kb", 1);
        var registry = new ContributionRegistry();
        using var _ = registry.Contribute(ContributionSources.Builtin, "", EditorMenus.Keybindings);
        var paths = new AppPaths(k => k == "CSCODE_HOME" ? temp.Combine("home") : null, HostOs.MacOs, "/Users/test");
        using var mac = new KeybindingService(new ContextKeyService(), paths, lane, registry, os: HostOs.MacOs);
        await mac.SetPresetAsync(preset, TestContext.Current.CancellationToken);
        Assert.Contains(mac.All, b => b.Command == EditorMenus.SurroundWithSnippet && b.Chord.SequenceEqual(KeyStrokeParser.ParseChord("cmd+k cmd+s")));

        static string[] Terms(string? when) => when is null ? [] : [.. when.Split("&&", StringSplitOptions.TrimEntries)];
        // "!k" excludes "k", and "k != 'v'" excludes "k == 'v'" (the Markdown preview's keys share Cmd+Shift+V with the clipboard ring that way)
        static string Negated(string t) => t.Contains("!=", StringComparison.Ordinal) ? t.Replace("!=", "==", StringComparison.Ordinal)
            : t.Contains("==", StringComparison.Ordinal) ? t.Replace("==", "!=", StringComparison.Ordinal) : t.StartsWith('!') ? t[1..] : "!" + t;
        static bool Exclusive(string? a, string? b) => Terms(a).Any(t => Terms(b).Contains(Negated(t)));
        var all = mac.All.Where(b => !b.IsNegative).ToArray();
        var clashes = (from x in all
                       from y in all
                       where x.Command != y.Command && x.Chord.Count <= y.Chord.Count && !(x.Chord.Count == y.Chord.Count && string.CompareOrdinal(x.Command, y.Command) > 0)
                             && x.Chord.SequenceEqual(y.Chord.Take(x.Chord.Count)) && !Exclusive(x.When, y.When)
                       select $"{string.Join(' ', x.Chord)} → {x.Command} [{x.When}] / {string.Join(' ', y.Chord)} → {y.Command} [{y.When}]").Distinct().ToArray();
        Assert.True(clashes.Length == 0, $"{preset}: {string.Join("; ", clashes)}");
    }
}
