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
    /// <summary>Command id that toggles/focuses this window (shown in View menu with its keybinding).</summary>
    string? ToggleCommandId { get; }
    /// <summary>Context-key `when` clause: window auto-shows while true (e.g. "inDebugMode" for debug panes). Null = manual.</summary>
    string? AutoShowWhen { get; }
    /// <summary>Creates the view. Called on the UI thread, once per show; the view (and its DataContext) is dropped on hide.</summary>
    Control CreateView(IServiceProvider services);
}
