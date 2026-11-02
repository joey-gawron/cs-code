using System.Text.Json;
using Avalonia.Controls;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Panels.NewProject.ViewModels;
using CsCode.Panels.NewProject.Views;
using CsCode.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.NewProject;

/// <summary>
/// The module's composition point: registers the <c>cscode.newProject.defaultLocation</c> setting and the File ▸ New ▸ Project… command
/// (<see cref="OpenCommand"/>, "File: New Project..." in the palette) once, and makes the page's view models. Resolved when the shell enumerates
/// the tool windows (the document view depends on it), so the command exists before the menus are built.
/// </summary>
public sealed class NewProjectHost : IDisposable
{
    public const string ViewId = "newProject";
    public const string OpenCommand = "cscode.file.newProject";
    public const string DefaultLocationSetting = "cscode.newProject.defaultLocation";

    private readonly IServiceProvider _sp;
    private readonly DisposableStore _store = new();

    public NewProjectHost(IServiceProvider services)
    {
        _sp = services;
        if (_sp.GetService<IConfigurationService>() is { } config)
            _store.Add(config.RegisterProperties(
            [
                new ConfigurationProperty(DefaultLocationSetting, JsonSerializer.SerializeToElement(""),
                    "The folder File > New > Project... proposes for a new project (~ is your home folder). Empty: the parent of the most recently opened folder, else your home folder."),
            ]));
        if (_sp.GetService<ICommandService>() is { } commands && !commands.Has(OpenCommand))
            _store.Add(commands.Register(OpenCommand, async _ =>
            {
                if (_sp.GetService<IViewActivator>() is { } views) await views.ShowViewAsync(ViewId).ConfigureAwait(false);
                return null;
            }, new CommandMetadata(OpenCommand, "New Project...", "File", "new-folder")));
    }

    public NewProjectViewModel CreateViewModel() => new(NewProjectServices.From(_sp));

    public void Dispose() => _store.Dispose();
}

/// <summary>The New Project page, a document view (<see cref="ToolWindowLocation.Document"/>) opened by <see cref="NewProjectHost.OpenCommand"/>.</summary>
public sealed class NewProjectToolWindow(NewProjectHost host) : IToolWindow
{
    public string Id => NewProjectHost.ViewId;
    public string Title => "New Project";
    public string? IconKey => "new-folder";
    public ToolWindowLocation DefaultLocation => ToolWindowLocation.Document;
    public string? ToggleCommandId => NewProjectHost.OpenCommand;
    public string? AutoShowWhen => null;

    public Control CreateView(IServiceProvider services) => new NewProjectView(host.CreateViewModel());
}
