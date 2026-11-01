using Avalonia.Controls;
using CsCode.Ui;

namespace CsCode.Panels.Testing;

/// <summary>
/// "Test Explorer" (<c>testing</c>): docks on the left by default (with Find in Files; Visual Studio puts it in the left tool well too). The shell
/// creates the view when it is shown and disposes it when hidden; the tests and their results stay in the host's controller.
/// </summary>
public sealed class TestExplorerToolWindow(TestingHost host) : IToolWindow
{
    public string Id => TestingHost.ViewId;
    public string Title => "Test Explorer";
    public string? IconKey => "checklist";
    public ToolWindowLocation DefaultLocation => ToolWindowLocation.Left;
    public string? ToggleCommandId => TestingHost.FocusView;
    public string? AutoShowWhen => null;

    public Control CreateView(IServiceProvider services) => new TestExplorerView(host.CreateExplorer(), host.ReleaseExplorer) { HostServices = services };
}
