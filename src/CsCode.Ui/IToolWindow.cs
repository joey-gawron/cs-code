using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
    /// and focuses its view itself (<see cref="IToolWindowFocus"/>, else the view's first focusable element in tab order that is not a button, a
    /// splitter or a drop-down).
    /// </summary>
    string? ShowCommandId => null;
    /// <summary>Context-key `when` clause: window auto-shows while true (e.g. "inDebugMode" for debug panes). Null = manual.</summary>
    string? AutoShowWhen { get; }
    /// <summary>Creates the view. Called on the UI thread, once per show; the view (and its DataContext) is dropped on hide.</summary>
    Control CreateView(IServiceProvider services);
}

/// <summary>
/// Implemented by a tool window's view (the control <see cref="IToolWindow.CreateView"/> returns) that knows where the keyboard belongs when
/// View &gt; Tool Windows activates the window: its list, tree or input, not the first button of its toolbar or the splitter of its column header,
/// which is where tab order alone would put it. A view without it, or whose <see cref="FocusPreferred"/> finds nothing, gets the keyboard on its
/// first focusable element in tab order that is not a button, a GridSplitter or a ComboBox (with none, the window itself takes it). Called on the
/// UI thread once the view is laid out and its content has come (<see cref="ContentPending"/>).
/// </summary>
public interface IToolWindowFocus
{
    /// <summary>Focuses the view's main element; false when nothing of it can take the keyboard now (the shell then falls back to tab order).</summary>
    bool FocusPreferred();

    /// <summary>
    /// True while what <see cref="FocusPreferred"/> would focus is still on its way: an Explorer just created has no row until its folder loads,
    /// after its first frame. The shell then waits, as (and as long as) it waits for Dock to lay the view out, before it asks; asked at once, the
    /// view found nothing and tab order put the keyboard in the Explorer's filter box. False by default.
    /// </summary>
    bool ContentPending => false;
}

/// <summary>Building blocks of <see cref="IToolWindowFocus.FocusPreferred"/>, and of keeping the keyboard where it put it.</summary>
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

    /// <summary>
    /// Keeps the keyboard on <paramref name="list"/>'s rows when a change of its items takes it away. A bulk change (one Reset, which
    /// <see cref="Collections.BulkObservableCollection{T}"/> raises for every range it inserts or removes) recycles every realized row, the
    /// focused one too, and Avalonia then leaves the keyboard on nothing, where the arrows, Enter and typing go nowhere: in the Explorer, each
    /// folder expanded or collapsed (clicked, or loading its children after View &gt; Tool Windows put the keyboard on the root) did that. Once
    /// the rows are laid out again, <see cref="FocusRow"/> gives it back: the selected row, else the first on screen. Only for an element that
    /// lost it to nothing by leaving the screen (a recycled row, the rename box once hidden): not when something else has taken the keyboard
    /// meanwhile, nor when the list left the screen (its window hidden), nor when the element is still on screen, which is how Avalonia leaves
    /// it when another of the app's windows activates with nothing focused in it (a tool window floated) — taking it back then sent the keys
    /// typed in that window to this list (Down moved the Explorer's selection, Delete asked to delete the file).
    /// </summary>
    public static void KeepOnRows(ListBox list) => list.LostFocus += (_, e) =>
    {
        if (e.NewFocusedElement is not (null or TopLevel)) return; // moved on: the user's or a command's doing
        if (e.Source is Avalonia.Visual lost && lost.IsAttachedToVisualTree() && lost.IsEffectivelyVisible) return; // still on screen: another window activated
        Dispatcher.UIThread.Post(() =>
        {
            if (!list.IsAttachedToVisualTree() || TopLevel.GetTopLevel(list)?.FocusManager is not { } focus || focus.GetFocusedElement() is not (null or TopLevel)) return;
            list.UpdateLayout(); // the rows of the new items
            FocusRow(list);
        }, DispatcherPriority.Loaded);
    };
}
