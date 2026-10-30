using Avalonia.Input;

namespace CsCode.App.Input;

/// <summary>
/// A layer that sees keyboard input before the shell's key bindings: the Vim adapter. The main window calls it from its tunnelling key and
/// text-input handlers; returning true consumes the event (no key binding runs and the focused control does not see it).
/// </summary>
public interface IShellKeyInterceptor
{
    bool OnKeyDown(KeyEventArgs e);
    bool OnTextInput(TextInputEventArgs e);
}
