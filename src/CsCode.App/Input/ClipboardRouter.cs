using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

    private readonly IContextKeyService _keys;
    private readonly DocumentWell _well;
    private readonly Func<TopLevel?> _window;
    private readonly DisposableStore _subs = new();
    private WeakReference<Visual>? _focused;

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

    /// <summary>The window reports every focus change; focus moving into a menu or popup is ignored (the menu acts on what had focus before).</summary>
    public void NoteFocus(Visual? focused)
    {
        if (focused is null || IsMenuOrPopup(focused)) return;
        _focused = new WeakReference<Visual>(focused);
    }

    private static bool IsMenuOrPopup(Visual v)
    {
        for (var c = v; c is not null; c = c.GetVisualParent())
            if (c is MenuItem or MenuBase or PopupRoot or OverlayPopupHost) return true;
        return false;
    }

    /// <summary>What Cut/Copy/Paste act on now (null: nothing that can use the clipboard has focus).</summary>
    public IClipboardTarget? Resolve()
    {
        Visual? focused = null;
        if (_focused?.TryGetTarget(out var remembered) == true && remembered.IsAttachedToVisualTree()) focused = remembered;
        // Nothing remembered yet (focus set before the window reported it): what the window's focus manager says, unless that is a menu.
        if (focused is null && _window()?.FocusManager?.GetFocusedElement() is Visual current && !IsMenuOrPopup(current)) focused = current;
        for (var v = focused; v is not null; v = v.GetVisualParent())
        {
            if (v is TextBox box) return new TextBoxClipboardTarget(box);
            if (v is IClipboardTarget target) return target;
            if (v is Control c && EditorOf(c) is { } editor) return new EditorTarget(editor);
        }
        return null;
    }

    private IEditorViewController? EditorOf(Control view)
    {
        foreach (var tab in _well.EditorTabs)
            if (ReferenceEquals(tab.LiveView, view)) return tab.Controller;
        return null;
    }

    /// <summary>Sets the Edit menu's <c>cscode.edit.can*</c> keys from the focused target (called before the menus refresh).</summary>
    public void UpdateKeys()
    {
        var target = Resolve();
        _keys.Set(CanCutKey, target?.CanCut ?? false);
        _keys.Set(CanCopyKey, target?.CanCopy ?? false);
        _keys.Set(CanPasteKey, target?.CanPaste ?? false);
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
