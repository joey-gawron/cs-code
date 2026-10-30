using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using CsCode.Core.Text;
using CsCode.Editor.Tests.Support;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// The controller members macros record and replay through (docs/MACROS.md): the text-input tap, <c>ReplayText</c> (the typing path) and
/// <c>ReplayKey</c> (a key press on the text area), in a real headless view.
/// </summary>
public class MacroHooksViewTests
{
    [AvaloniaFact]
    public async Task The_text_input_tap_reports_typed_text_and_ignores_control_characters_and_code_insertions()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync("", "a.txt");
        var seen = new List<string>();
        using var sub = view.Controller.TextInput.Subscribe(seen.Add);

        window.KeyTextInput("a");
        window.KeyTextInput("bc");       // an IME commit arrives as one piece
        window.KeyTextInput("\b");       // a control character some platforms send after a key press (the key itself is what counts)
        view.Controller.ReplayText("x"); // code, not the user
        view.Document.ApplyEdits([new TextEdit(TextRange.At(0, 0), "y")]);
        ViewHost.Pump();

        Assert.Equal(["a", "bc"], seen);
        Assert.Equal("yabcx", view.Document.GetText());
    }

    [AvaloniaFact]
    public async Task Input_an_earlier_handler_took_is_not_reported()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync("", "a.txt");
        var seen = new List<string>();
        using var sub = view.Controller.TextInput.Subscribe(seen.Add);
        // a modal layer (Vim) takes text in the window's tunnelling handler, before the text area
        void Take(object? s, TextInputEventArgs e) { if (e.Text == "q") e.Handled = true; }
        window.AddHandler(InputElement.TextInputEvent, Take, RoutingStrategies.Tunnel);

        window.KeyTextInput("q");
        window.KeyTextInput("w");
        ViewHost.Pump();

        Assert.Equal(["w"], seen);
        Assert.Equal("w", view.Document.GetText());
    }

    [AvaloniaFact]
    public async Task ReplayText_types_through_the_typing_path_with_auto_closing()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("", "a.cs");

        foreach (var c in "f(") Assert.True(view.Controller.ReplayText(c.ToString()));
        Assert.Equal("f()", view.Document.GetText()); // the closer was inserted as when typed
        Assert.True(view.Controller.ReplayText(")"));
        Assert.Equal("f()", view.Document.GetText()); // and over-typed
        Assert.Equal(new TextPosition(0, 3), view.Controller.Caret);
    }

    [AvaloniaFact]
    public async Task ReplayText_refuses_a_read_only_document()
    {
        using var host = ViewHost.Create();
        var path = host.Dir.WriteFile("ro.txt", "x");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var doc = await host.Factory.OpenDocumentAsync(new Uri(path));
            if (!doc.IsReadOnly) Assert.Skip("the file system does not report the file read-only (running as root?)");
            var view = host.Factory.CreateView(doc);
            Assert.False(host.Factory.GetController(view)!.ReplayText("a"));
            Assert.Equal("x", doc.GetText());
            host.Factory.DisposeView(view);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [AvaloniaFact]
    public async Task ReplayKey_moves_the_caret_selects_deletes_and_breaks_lines_like_the_keyboard()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("abc\ndef", "a.txt");
        var c = view.Controller;
        c.SetSelection(TextRange.At(0, 0));

        Assert.True(c.ReplayKey(Key.End, KeyModifiers.None));
        Assert.Equal(new TextPosition(0, 3), c.Caret);
        Assert.True(c.ReplayKey(Key.Left, KeyModifiers.Shift));
        Assert.Equal(new TextRange(new TextPosition(0, 2), new TextPosition(0, 3)), c.Selections[0]);
        Assert.True(c.ReplayKey(Key.Back, KeyModifiers.None));
        Assert.Equal("ab\ndef", view.Document.GetText());
        Assert.True(c.ReplayKey(Key.Enter, KeyModifiers.None));
        Assert.Equal("ab\n\ndef", view.Document.GetText().Replace("\r\n", "\n"));
        Assert.True(c.ReplayKey(Key.Down, KeyModifiers.None));
        Assert.True(c.ReplayKey(Key.Delete, KeyModifiers.None));
        Assert.Equal("ab\n\nef", view.Document.GetText().Replace("\r\n", "\n"));
    }

    [AvaloniaFact]
    public async Task A_destroyed_view_replays_nothing_and_its_tap_is_released()
    {
        using var host = ViewHost.Create();
        var window = new Avalonia.Controls.Window { Width = 600, Height = 400 };
        window.Show();
        var weak = new List<WeakReference>();
        await CycleAsync(host, window, weak);
        for (var attempt = 0; attempt < 60 && weak.Any(w => w.IsAlive); attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            ViewHost.Pump();
            await Task.Delay(20);
        }
        var alive = weak.FirstOrDefault(w => w.IsAlive)?.Target;
        Assert.True(alive is null, alive is null ? "" : "still reachable: " + alive.GetType().Name + "\n" +
            RetentionPath.Find([host.Provider, host.Factory, host.Documents, Avalonia.Threading.Dispatcher.UIThread, Avalonia.Application.Current!], alive));
        window.Close();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task CycleAsync(ViewHost host, Avalonia.Controls.Window window, List<WeakReference> weak)
    {
        for (var i = 0; i < 5; i++)
        {
            var path = host.Dir.WriteFile($"d{i}.txt", "abc");
            var doc = await host.Factory.OpenDocumentAsync(new Uri(path));
            var view = host.Factory.CreateView(doc);
            window.Content = view;
            ViewHost.Pump();
            var controller = host.Factory.GetController(view)!;
            var typed = new List<string>();
            var sub = controller.TextInput.Subscribe(typed.Add);
            Assert.True(controller.ReplayKey(Key.End, KeyModifiers.None));
            Assert.True(controller.ReplayText("d"));
            window.Content = null;
            weak.Add(new WeakReference(view));
            weak.Add(new WeakReference(typed));
            host.Factory.DisposeView(view);
            Assert.False(controller.ReplayText("x"));
            Assert.False(controller.ReplayKey(Key.Left, KeyModifiers.None));
            Assert.Equal("abcd", doc.GetText());
            sub.Dispose();
            await host.Factory.CloseDocumentAsync(doc);
        }
        // ReplayKey focuses the view; AvaloniaEdit's static RoutedCommand keeps the last focused text area until focus moves (a known, bounded
        // AvaloniaEdit retention, docs/EDITOR.md): move it, as the shell does when a tab closes.
        var other = new Avalonia.Controls.TextBox();
        window.Content = other;
        ViewHost.Pump();
        other.Focus();
        ViewHost.Pump();
        window.Content = null;
    }

    [AvaloniaFact]
    public async Task ReplayKey_acts_on_its_own_view_when_another_editor_has_the_focus()
    {
        using var host = ViewHost.Create();
        var (_, focused, window) = await host.OpenAsync("one", "a.txt");
        var path = host.Dir.WriteFile("b.txt", "two");
        var docB = await host.Factory.OpenDocumentAsync(new Uri(path));
        var viewB = host.Factory.CreateView(docB);
        var grid = new Avalonia.Controls.Grid { ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("*,*") };
        window.Content = null;
        grid.Children.Add(focused);
        Avalonia.Controls.Grid.SetColumn(viewB, 1);
        grid.Children.Add(viewB);
        window.Content = grid;
        ViewHost.Pump();
        focused.Controller.Focus();
        ViewHost.Pump();
        var b = host.Factory.GetController(viewB)!;

        Assert.True(b.ReplayKey(Key.End, KeyModifiers.None));
        Assert.True(b.ReplayText("!"));

        Assert.Equal("two!", docB.GetText());
        Assert.Equal("one", focused.Document.GetText());
        Assert.Equal(TextPosition.Zero, focused.Controller.Caret);
        host.Factory.DisposeView(viewB);
    }
}
