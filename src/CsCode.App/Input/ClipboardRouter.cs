using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using CsCode.App.Docking;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Editor;
using CsCode.Ui.Menus;

namespace CsCode.App.Input;

/// <summary>
/// Edit > Cut / Copy / Paste act on whatever has keyboard focus, as in Visual Studio: the editor (its Visual Studio clipboard behaviours), a
/// text box (settings fields, the quick input, the Debug Console input: the text box's own Cut/Copy/Paste), or a view that copies its own
/// selection (<see cref="IClipboardTarget"/>: Call Stack, Locals/Autos/Watch, Error List, Output, Find Results, Find in Files...).
/// <para>
/// It owns the command ids the menus and the keymap use (VS Code's <c>editor.action.clipboard*Action</c>, also registered by the editor for a
/// standalone editor; the shell's registration is newer and therefore runs, see docs/SHELL.md "Command ownership"). Focus is remembered as it
/// moves (opening the Edit menu must not count as focus), and the <c>cscode.edit.can*</c> context keys the Edit menu's items are gated on are
/// computed right before a menu refresh.
/// </para>
/// </summary>
public sealed class ClipboardRouter : IDisposable
{
    public const string CanCutKey = "cscode.edit.canCut";
    public const string CanCopyKey = "cscode.edit.canCopy";
    public const string CanPasteKey = "cscode.edit.canPaste";
    /// <summary>True when the focused clipboard target is a writable editor (Edit > Cycle Clipboard Ring).</summary>
    public const string EditorPasteKey = "cscode.edit.editorCanPaste";
    /// <summary>True when the focused clipboard target is an editor that can copy (Edit > Copy with Formatting).</summary>
    public const string EditorCopyKey = "cscode.edit.editorCanCopy";

    private readonly IContextKeyService _keys;
    private readonly DocumentWell _well;
    private readonly Func<TopLevel?> _window;
    private readonly DisposableStore _subs = new();
    private WeakReference<Visual>? _focused;
    // The window that last took focus (the main window, or a floating one: each has its own focus manager).
    private WeakReference<TopLevel>? _focusedWindow;

    public ClipboardRouter(ICommandService commands, IContextKeyService keys, DocumentWell well, Func<TopLevel?>? window = null)
    {
        _keys = keys;
        _well = well;
        _window = window ?? (() => null);
        void Reg(string id, string title, Func<IClipboardTarget, Task> run) => _subs.Add(commands.Register(id, async _ =>
        {
            if (Resolve() is { } target) await run(target).ConfigureAwait(true);
            return null;
        }, new CommandMetadata(id, title, "Edit")));
        Reg("editor.action.clipboardCutAction", "Cut", t => t.CanCut ? t.CutAsync() : Task.CompletedTask);
        Reg("editor.action.clipboardCopyAction", "Copy", t => t.CanCopy ? t.CopyAsync() : Task.CompletedTask);
        Reg("editor.action.clipboardPasteAction", "Paste", t => t.CanPaste ? t.PasteAsync() : Task.CompletedTask);
        UpdateKeys();
    }

    /// <summary>
    /// Follows keyboard focus in <paramref name="root"/> (the main window, and each floating window). Handled focus events count too: AvaloniaEdit's
    /// text area marks its GotFocus handled, so a plain handler never sees the editor take focus.
    /// </summary>
    public IDisposable Attach(Control root)
    {
        root.AddHandler(InputElement.GotFocusEvent, OnGotFocus, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        return Disposable.Create(() => root.RemoveHandler(InputElement.GotFocusEvent, OnGotFocus));
    }

    private void OnGotFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => NoteFocus(e.Source as Visual);

    /// <summary>Records a focus change; focus moving into a menu or popup is ignored (the menu acts on what had focus before).</summary>
    public void NoteFocus(Visual? focused)
    {
        if (focused is null || IsMenuOrPopup(focused)) return;
        _focused = new WeakReference<Visual>(focused);
        if (TopLevel.GetTopLevel(focused) is { } window) _focusedWindow = new WeakReference<TopLevel>(window);
    }

    private static bool IsMenuOrPopup(Visual v)
    {
        // A context menu is not a MenuBase in Avalonia 12, and an overlay popup lives inside the window's own tree: test for both explicitly.
        for (var c = v; c is not null; c = c.GetVisualParent())
            if (c is MenuItem or MenuBase or ContextMenu or Popup or PopupRoot or OverlayPopupHost) return true;
        return false;
    }

    /// <summary>What Cut/Copy/Paste act on now (null: nothing that can use the clipboard has focus).</summary>
    public IClipboardTarget? Resolve()
    {
        // What has focus now, unless that is a menu or popup (the Edit menu or a context menu is open): then what had focus before it. "Now" is in
        // the window that last took focus: a floating window's focus is not the main window's.
        var window = _focusedWindow?.TryGetTarget(out var w) == true && w is not Window { IsVisible: false } ? w : _window();
        Visual? focused = window?.FocusManager?.GetFocusedElement() is Visual current && !IsMenuOrPopup(current) ? current : null;
        if (focused is null && _focused?.TryGetTarget(out var remembered) == true && remembered.IsAttachedToVisualTree()) focused = remembered;
        if (focused is null) return null;
        var tabs = _well.EditorTabs; // once: this runs on every menu refresh
        for (var v = focused; v is not null; v = v.GetVisualParent())
        {
            if (v is TextBox box) return new TextBoxClipboardTarget(box);
            if (v is IClipboardTarget target) return target;
            foreach (var tab in tabs)
                if (ReferenceEquals(tab.LiveView, v) && tab.Controller is { } editor) return new EditorTarget(editor);
        }
        return null;
    }

    /// <summary>Sets the Edit menu's <c>cscode.edit.can*</c> keys from the focused target (called before the menus refresh).</summary>
    public void UpdateKeys()
    {
        var target = Resolve();
        _keys.Set(CanCutKey, target?.CanCut ?? false);
        _keys.Set(CanCopyKey, target?.CanCopy ?? false);
        _keys.Set(CanPasteKey, target?.CanPaste ?? false);
        _keys.Set(EditorPasteKey, target is EditorTarget { CanPaste: true });
        _keys.Set(EditorCopyKey, target is EditorTarget { CanCopy: true });
    }

    public void Dispose() => _subs.Dispose();

    private sealed class EditorTarget(IEditorViewController editor) : IClipboardTarget
    {
        public bool CanCopy => editor.CanCopy;
        public bool CanCut => editor.CanCut;
        public bool CanPaste => editor.CanPaste;
        public Task CopyAsync() => editor.CopyAsync();
        public Task CutAsync() => editor.CutAsync();
        public Task PasteAsync() => editor.PasteAsync();
    }
}
