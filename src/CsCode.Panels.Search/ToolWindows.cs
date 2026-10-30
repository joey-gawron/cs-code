using Avalonia.Controls;
using CsCode.Panels.Search.Settings;
using CsCode.Panels.Search.Views;
using CsCode.Ui;

namespace CsCode.Panels.Search;

/// <summary>
/// "Find in Files" (<c>search</c>). Docks on the left by default and lays out fine in a bottom well too. The shell creates the view when
/// first shown and disposes it when hidden (which cancels a running search and releases every subscription).
/// </summary>
public sealed class FindInFilesToolWindow(SearchPanelsHost host) : IToolWindow
{
    public string Id => SearchPanelsHost.FindViewId;
    public string Title => "Find in Files";
    public string? IconKey => "search";
    public ToolWindowLocation DefaultLocation => ToolWindowLocation.Left;
    public string? ToggleCommandId => SearchPanelsHost.FindInFiles;
    /// <summary>View &gt; Tool Windows &gt; Find in Files does what the command does: the query seeded from the selection, the query box focused.</summary>
    public string? ShowCommandId => SearchPanelsHost.FindInFiles;
    public string? AutoShowWhen => null;

    public Control CreateView(IServiceProvider services) => new FindInFilesView(host.CreateFindViewModel(), host.ReleaseFindViewModel);
}

/// <summary>The Settings editor, a document view (<see cref="ToolWindowLocation.Document"/>) opened by <c>workbench.action.openSettings</c>.</summary>
public sealed class SettingsToolWindow(SearchPanelsHost host) : IToolWindow
{
    public string Id => SearchPanelsHost.SettingsViewId;
    public string Title => "Settings";
    public string? IconKey => "settings-gear";
    public ToolWindowLocation DefaultLocation => ToolWindowLocation.Document;
    public string? ToggleCommandId => SearchPanelsHost.OpenSettings;
    public string? AutoShowWhen => null;

    public Control CreateView(IServiceProvider services) => new SettingsView(host.CreateSettingsViewModel(), host.ReleaseSettingsViewModel);
}

/// <summary>The Keyboard Shortcuts editor, a document view opened by <c>workbench.action.openGlobalKeybindings</c>.</summary>
public sealed class KeyboardShortcutsToolWindow(SearchPanelsHost host) : IToolWindow
{
    public string Id => SearchPanelsHost.KeybindingsViewId;
    public string Title => "Keyboard Shortcuts";
    public string? IconKey => "key";
    public ToolWindowLocation DefaultLocation => ToolWindowLocation.Document;
    public string? ToggleCommandId => SearchPanelsHost.OpenKeybindings;
    public string? AutoShowWhen => null;

    public Control CreateView(IServiceProvider services) => new KeyboardShortcutsView(new KeyboardShortcutsViewModel(host.Services));
}
