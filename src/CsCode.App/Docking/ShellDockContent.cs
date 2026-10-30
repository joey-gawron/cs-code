using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dock.Avalonia.Controls;

namespace CsCode.App.Docking;

/// <summary>
/// How a <see cref="DockControl"/> (the main window's, or a floating window's) presents the shell's tabs.
/// <para>
/// Dock's content presenter builds a tab through its <c>ControlRecyclingDataTemplate</c>, which asks a dockable that is itself an
/// <see cref="IRecyclingDataTemplate"/> before anything else: <see cref="ShellDocument"/> and <see cref="ToolTab"/> are, and hand out their view host.
/// The shell used to register data templates for its tab types on the DockControl instead (T14). Those were also found by the other presenters
/// whose content is the dockable: the tab strip's close-button presenters (<c>PART_ClosePresenter</c>, <c>PART_CompactClosePresenter</c>) fall
/// back to the tree's data templates while Dock re-templates a moved tab (their <c>CloseTemplate</c> is not resolved yet), took an inactive tab's
/// shared host, and Split Editor Right threw "The control Border already has a visual parent".
/// </para>
/// <para>
/// So the DockControl gets one data template for the shell's tab types, first in its list, that builds an empty placeholder: a presenter that
/// falls back to the tree gets a throw-away control, never a tab's host, and Dock's own default templates (which would present the tab's content
/// again) are not reached for these types.
/// </para>
/// </summary>
public static class ShellDockContent
{
    /// <summary>Puts the placeholder template first in <paramref name="dock"/>'s data templates (idempotent).</summary>
    public static void Install(DockControl dock)
    {
        if (dock.DataTemplates.OfType<PlaceholderTemplate>().Any()) return;
        dock.DataTemplates.Insert(0, new PlaceholderTemplate());
    }

    /// <summary>Whether <paramref name="dock"/> presents the shell's tabs this way.</summary>
    public static bool IsInstalled(DockControl dock) => dock.DataTemplates.OfType<PlaceholderTemplate>().Any();

    private sealed class PlaceholderTemplate : IDataTemplate
    {
        public bool Match(object? data) => data is ShellDocument or ToolTab;
        public Control? Build(object? param) => new Panel();
    }
}
