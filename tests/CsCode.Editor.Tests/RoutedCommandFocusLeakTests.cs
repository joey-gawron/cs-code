using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CsCode.Editor.Documents;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// AvaloniaEdit's static <c>RoutedCommand._inputElement</c> keeps the last element that took focus: when the last document closed nothing moved
/// focus, so the last focused view, its text area and the document's text stayed reachable until focus next moved anywhere. A disposed view clears
/// it when it points into its own text area.
/// </summary>
public class RoutedCommandFocusLeakTests
{
    [AvaloniaFact]
    public void AvaloniaEdits_private_layout_is_the_one_the_release_was_written_against()
    {
        Assert.True(RoutedCommandFocusRelease.Supported, "Avalonia.AvaloniaEdit no longer has RoutedCommand._inputElement: update RoutedCommandFocusRelease");
    }

    [AvaloniaFact]
    public async Task The_last_focused_view_is_released_when_it_is_disposed_with_nothing_else_to_focus()
    {
        using var host = ViewHost.Create();
        var window = new Window { Width = 600, Height = 400 };
        window.Show();
        var path = host.Dir.WriteFile("last.txt", "the text of the last document");
        var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(path));
        var weak = FocusAndDispose(host, window, doc);
        await host.Factory.CloseDocumentAsync(doc);
        doc = null;
        for (var attempt = 0; attempt < 60 && weak.Any(w => w.IsAlive); attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            ViewHost.Pump();
        }
        Assert.False(weak[0].IsAlive, "the view is still reachable");
        Assert.False(weak[1].IsAlive, "its text area is still reachable");
        Assert.Null(RoutedCommandFocusRelease.Held);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Another_views_focus_is_left_alone()
    {
        using var host = ViewHost.Create();
        var (_, focused, window) = await host.OpenAsync("one", "a.txt");
        var path = host.Dir.WriteFile("b.txt", "two");
        var other = host.Factory.CreateView(await host.Factory.OpenDocumentAsync(new Uri(path)));
        focused.TextArea.Focus();
        ViewHost.Pump();
        var held = RoutedCommandFocusRelease.Held;
        Assert.True(held == focused.TextArea || (held is Avalonia.Visual v && focused.TextArea.IsVisualAncestorOf(v)), $"held: {held}");
        host.Factory.DisposeView(other); // never shown, never focused: the static keeps pointing at the focused view
        Assert.Same(held, RoutedCommandFocusRelease.Held);
        window.Close();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> FocusAndDispose(ViewHost host, Window window, EditorDocument doc)
    {
        var view = (CodeEditorControl)host.Factory.CreateView(doc);
        window.Content = view;
        ViewHost.Pump();
        view.TextArea.Focus();
        ViewHost.Pump();
        var held = RoutedCommandFocusRelease.Held;
        Assert.True(held == view.TextArea || (held is Avalonia.Visual v && view.TextArea.IsVisualAncestorOf(v)), $"focusing the view did not reach RoutedCommand: {held}");
        window.Content = null; // the tab goes; nothing else takes focus (the last document closed)
        var weak = new List<WeakReference> { new(view), new(view.TextArea) };
        host.Factory.DisposeView(view);
        return weak;
    }
}
