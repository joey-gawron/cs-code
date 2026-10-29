using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace CsCode.Ui.Menus;

/// <summary>
/// A view that can copy (and possibly cut and paste) its own selection: the Call Stack's frames, the Locals' values, the Error List's rows, the
/// Output's lines... The shell's Edit menu and Cut/Copy/Paste commands act on the focused view through this, so Edit > Copy and Ctrl+C do the
/// same thing everywhere (Visual Studio routes Edit.Copy to the focused window the same way). Implemented by the view's root control.
/// </summary>
public interface IClipboardTarget
{
    bool CanCopy { get; }
    bool CanCut => false;
    bool CanPaste => false;
    Task CopyAsync();
    Task CutAsync() => Task.CompletedTask;
    Task PasteAsync() => Task.CompletedTask;
}

public enum ClipboardAction { None, Cut, Copy, Paste }

/// <summary>The standard clipboard keys: Ctrl+C / Ctrl+Insert, Ctrl+X / Shift+Delete, Ctrl+V / Shift+Insert (Cmd on macOS).</summary>
public static class ClipboardKeys
{
    public static ClipboardAction Classify(Key key, KeyModifiers modifiers)
    {
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        return (key, modifiers) switch
        {
            (Key.C, var m) when m == command => ClipboardAction.Copy,
            (Key.Insert, KeyModifiers.Control) => ClipboardAction.Copy,
            (Key.X, var m) when m == command => ClipboardAction.Cut,
            (Key.Delete, KeyModifiers.Shift) => ClipboardAction.Cut,
            (Key.V, var m) when m == command => ClipboardAction.Paste,
            (Key.Insert, KeyModifiers.Shift) => ClipboardAction.Paste,
            _ => ClipboardAction.None,
        };
    }

    public static ClipboardAction Classify(KeyEventArgs e) => Classify(e.Key, e.KeyModifiers);

    /// <summary>
    /// Runs the clipboard action <paramref name="e"/> asks for on <paramref name="target"/> (when it can) and marks the key handled. For views whose
    /// own key handler should treat Ctrl+C and Ctrl+Insert alike.
    /// </summary>
    public static bool TryHandle(KeyEventArgs e, IClipboardTarget target)
    {
        var action = Classify(e);
        Func<Task>? run = action switch
        {
            ClipboardAction.Copy when target.CanCopy => target.CopyAsync,
            ClipboardAction.Cut when target.CanCut => target.CutAsync,
            ClipboardAction.Paste when target.CanPaste => target.PasteAsync,
            _ => null,
        };
        if (run is null) return false;
        e.Handled = true;
        _ = Run(run);
        return true;
    }

    private static async Task Run(Func<Task> run)
    {
        try { await run().ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ContextMenus.ErrorSink?.Invoke(ex); }
    }

    /// <summary>The nearest <see cref="IClipboardTarget"/> at or above <paramref name="element"/> in the visual tree.</summary>
    public static IClipboardTarget? FindTarget(Visual? element)
    {
        for (var v = element; v is not null; v = v.GetVisualParent())
            if (v is IClipboardTarget t) return t;
        return null;
    }

    /// <summary>Standard Cut/Copy/Paste entries for a target, with the clipboard keys' text (hidden entries are left out).</summary>
    public static IReadOnlyList<MenuEntry> Entries(IClipboardTarget target, bool withCut = false, bool withPaste = false)
    {
        var list = new List<MenuEntry>();
        if (withCut) list.Add(new MenuEntry("Cu_t", target.CutAsync) { Gesture = Gesture("X"), IsEnabled = target.CanCut });
        list.Add(new MenuEntry("_Copy", target.CopyAsync) { Gesture = Gesture("C"), Icon = "copy", IsEnabled = target.CanCopy });
        if (withPaste) list.Add(new MenuEntry("_Paste", target.PasteAsync) { Gesture = Gesture("V"), Icon = "clippy", IsEnabled = target.CanPaste });
        return list;
    }

    /// <summary>"Ctrl+C" (or "⌘C" on macOS): the gesture text panels show next to their clipboard items.</summary>
    public static string Gesture(string letter) => OperatingSystem.IsMacOS() ? "⌘" + letter : "Ctrl+" + letter;
}

/// <summary>Adapts a <see cref="TextBox"/> (settings fields, the quick input, the Debug Console input) to <see cref="IClipboardTarget"/>.</summary>
public sealed class TextBoxClipboardTarget(TextBox box) : IClipboardTarget
{
    public bool CanCopy => box.CanCopy;
    public bool CanCut => box.CanCut;
    public bool CanPaste => box.CanPaste;
    public Task CopyAsync() { box.Copy(); return Task.CompletedTask; }
    public Task CutAsync() { box.Cut(); return Task.CompletedTask; }
    public Task PasteAsync() { box.Paste(); return Task.CompletedTask; }
}
