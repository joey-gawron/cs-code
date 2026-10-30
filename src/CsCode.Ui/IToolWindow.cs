using Avalonia.Controls;

namespace CsCode.Ui;

public enum ToolWindowLocation { Left, Right, Bottom, Document }

/// <summary>
/// A dockable tool window (Explorer, Output, Error List, Locals, Call Stack, Extensions …). Modules and panel libraries
/// register implementations in DI as <c>IToolWindow</c>; the shell discovers them, creates views lazily (only when first
/// shown) and disposes views when hidden/closed. The shell owner may ADD members but not rename/remove.
/// </summary>
public interface IToolWindow
{
    /// <summary>Stable id used for layout persistence, e.g. "explorer", "output", "debug.callstack".</summary>
    string Id { get; }
    string Title { get; }
    /// <summary>Icon catalog key (Codicon name, e.g. "files", "debug-alt").</summary>
    string? IconKey { get; }
    ToolWindowLocation DefaultLocation { get; }
    /// <summary>Command id that toggles/focuses this window (its keybinding is the shortcut View &gt; Tool Windows shows for the window).</summary>
    string? ToggleCommandId { get; }
    /// <summary>
    /// Command id that shows, activates and focuses this window the way its module needs (no argument): what View &gt; Tool Windows runs when it is
    /// registered, as Visual Studio's View menu puts the keyboard in the window it shows. The Terminal's creates a terminal when there is none and
    /// focuses it; Find in Files seeds its query from the selection and focuses the query box. Null (the default): the shell shows the window
    /// and focuses its view itself (<see cref="IToolWindowFocus"/>, else the view's first focusable element in tab order).
    /// </summary>
    string? ShowCommandId => null;
    /// <summary>Context-key `when` clause: window auto-shows while true (e.g. "inDebugMode" for debug panes). Null = manual.</summary>
    string? AutoShowWhen { get; }
    /// <summary>Creates the view. Called on the UI thread, once per show; the view (and its DataContext) is dropped on hide.</summary>
    Control CreateView(IServiceProvider services);
}

/// <summary>
/// Implemented by a tool window's view (the control <see cref="IToolWindow.CreateView"/> returns) that knows where the keyboard belongs when
/// View &gt; Tool Windows activates the window: its list or tree, not the first button of its toolbar, which is where tab order alone would put
/// it. A view without it gets the keyboard on its first focusable element in tab order (with none, the window itself takes it). Called on the
/// UI thread once the view is laid out.
/// </summary>
public interface IToolWindowFocus
{
    /// <summary>Focuses the view's main element; false when nothing of it can take the keyboard now (the shell then falls back to tab order).</summary>
    bool FocusPreferred();
}

/// <summary>Building blocks of <see cref="IToolWindowFocus.FocusPreferred"/>.</summary>
public static class ToolWindowFocus
{
    /// <summary>
    /// Puts the keyboard on a row of <paramref name="list"/>: the selected one when it is realized, else the first realized one (on screen, so the
    /// list does not jump to its top). An Avalonia ListBox never takes the keyboard itself (it is not focusable): its rows do, and the arrow keys
    /// move from a focused row. False for a hidden list or one with no row realized (empty).
    /// </summary>
    public static bool FocusRow(ListBox list)
    {
        if (!list.IsEffectivelyVisible) return false;
        var row = (list.SelectedItem is { } selected ? list.ContainerFromItem(selected) : null) ?? list.GetRealizedContainers().FirstOrDefault();
        return row?.Focus() == true;
    }
}
