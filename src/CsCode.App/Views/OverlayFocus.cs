using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CsCode.App.Views;

/// <summary>
/// Gives the keyboard back when an overlay that took it (the quick input, a modal prompt) closes, as VS Code and Visual Studio do. Left alone, the
/// keyboard went nowhere: typing reached no editor and the focus context keys kept the overlay's <c>inputFocus</c>. On close (a dispatcher turn
/// later, so whatever the accepted entry focuses itself wins): the editor that became active meanwhile; else what had the keyboard when the overlay
/// opened, if it is still on screen; else the active editor. Nothing moves when something outside the overlay already has the keyboard. An overlay
/// that opens again before that turn (a quick pick followed by an input box) counts as the same one: what had the keyboard before the first gets it
/// after the last. UI thread.
/// </summary>
public sealed class OverlayFocus
{
    private readonly Control _overlay;
    private readonly Func<object?>? _activeEditor;
    private readonly Action? _focusActiveEditor;
    private WeakReference<IInputElement>? _before;
    private WeakReference<object>? _editorBefore;
    private bool _open;
    private int _generation;

    /// <param name="overlay">The overlay's root (focus inside it is the overlay's own).</param>
    /// <param name="activeEditor">The active document tab (identity only), or null when there is no document well.</param>
    /// <param name="focusActiveEditor">Puts the keyboard into the active editor (once it is shown).</param>
    public OverlayFocus(Control overlay, Func<object?>? activeEditor = null, Action? focusActiveEditor = null)
    {
        _overlay = overlay;
        _activeEditor = activeEditor;
        _focusActiveEditor = focusActiveEditor;
    }

    /// <summary>The overlay is shown and is about to take the keyboard: remember what has it.</summary>
    public void Opened()
    {
        _generation++;
        if (_open) return; // opened again before the close was handled: the first opening's focus is the one to give back
        _open = true;
        var focused = FocusedElement();
        _before = focused is Visual v && focused is not TopLevel && !IsInOverlay(v) ? new WeakReference<IInputElement>(focused) : null;
        _editorBefore = _activeEditor?.Invoke() is { } editor ? new WeakReference<object>(editor) : null;
    }

    /// <summary>The overlay was hidden: give the keyboard back a dispatcher turn later, unless it is open again by then.</summary>
    public void Closed()
    {
        if (!_open) return;
        var generation = ++_generation;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _generation) return; // opened (or closed) again meanwhile
            _open = false;
            Restore();
        }, DispatcherPriority.Input);
    }

    private IInputElement? FocusedElement() => TopLevel.GetTopLevel(_overlay)?.FocusManager?.GetFocusedElement();

    private bool IsInOverlay(Visual v) => ReferenceEquals(v, _overlay) || _overlay.IsVisualAncestorOf(v);

    private void Restore()
    {
        var before = _before?.TryGetTarget(out var b) == true ? b : null;
        var editorBefore = _editorBefore?.TryGetTarget(out var e) == true ? e : null;
        _before = null;
        _editorBefore = null;
        if (!_overlay.IsAttachedToVisualTree()) return; // its window closed meanwhile
        var now = FocusedElement();
        if (now is Visual current && now is not TopLevel && !IsInOverlay(current)) return; // the accepted entry focused its own target
        var editorNow = _activeEditor?.Invoke();
        if (editorNow is not null && !ReferenceEquals(editorNow, editorBefore))
        {
            _focusActiveEditor?.Invoke(); // the entry opened (or went to) another editor
            return;
        }
        if (before is InputElement { Focusable: true, IsEffectivelyEnabled: true, IsEffectivelyVisible: true } element && element.IsAttachedToVisualTree()
            && element.Focus())
            return;
        _focusActiveEditor?.Invoke();
    }
}
