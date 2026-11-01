using CsCode.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CsCode.Panels.Testing;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Test Explorer tool window and the <see cref="TestingHost"/> behind it (the test controller with the built-in .NET, Rust
    /// and JavaScript providers, the <c>testing.*</c> commands and settings, the editor's test glyph provider, Error List and Output glue).
    /// The tool window resolves the host, so the shell enumerating <c>IEnumerable&lt;IToolWindow&gt;</c> at startup is what starts it. Every
    /// service it uses is optional (workspace, configuration, commands, context keys, diagnostics, output, navigation, debug, view activator).
    /// </summary>
    public static IServiceCollection AddCsCodeTestExplorer(this IServiceCollection services)
    {
        services.TryAddSingleton<TestingHost>(sp => new TestingHost(sp));
        services.AddSingleton<IToolWindow>(sp => new TestExplorerToolWindow(sp.GetRequiredService<TestingHost>()));
        return services;
    }
}
