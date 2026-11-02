using CsCode.Core.Diagnostics;
using CsCode.Core.Projects;
using CsCode.Core.Python;
using CsCode.ProjectTemplates.Engine;
using CsCode.ProjectTemplates.Generators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CsCode.ProjectTemplates;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the built-in New Project generators as <see cref="IProjectGenerator"/>s (Empty, Python, Rust, C, C++, C#, Zig; TypeScript and JavaScript on Node.js, a static website), the
    /// <see cref="IToolRunner"/> they start their tools with (tracked by the memory monitor when there is one) and the <see cref="ProjectCreator"/>
    /// the page runs them through, and the <see cref="ProjectFollowUps"/> it runs once a new project has opened. Another module adds a generator
    /// by registering one more <see cref="IProjectGenerator"/>.
    /// </summary>
    public static IServiceCollection AddCsCodeProjectTemplates(this IServiceCollection services)
    {
        services.TryAddSingleton<IToolRunner>(sp => new ToolRunner(sp.GetService<IMemoryMonitor>()));
        services.TryAddSingleton(sp => new ProjectCreator(sp.GetRequiredService<IToolRunner>()));
        services.TryAddSingleton(sp => new ProjectFollowUps(sp.GetService<IPythonEnvironmentService>()));
        services.AddSingleton<IProjectGenerator>(sp => new EmptyProjectGenerator(sp.GetRequiredService<IToolRunner>()));
        services.AddSingleton<IProjectGenerator>(sp => new PythonProjectGenerator(sp.GetRequiredService<IToolRunner>()));
        services.AddSingleton<IProjectGenerator>(sp => new RustProjectGenerator(sp.GetRequiredService<IToolRunner>()));
        services.AddSingleton<IProjectGenerator>(sp => new CMakeProjectGenerator(sp.GetRequiredService<IToolRunner>(), cpp: false));
        services.AddSingleton<IProjectGenerator>(sp => new CMakeProjectGenerator(sp.GetRequiredService<IToolRunner>(), cpp: true));
        services.AddSingleton<IProjectGenerator>(sp => new DotnetProjectGenerator(sp.GetRequiredService<IToolRunner>(), sp.GetService<TimeProvider>()));
        services.AddSingleton<IProjectGenerator>(sp => new ZigProjectGenerator(sp.GetRequiredService<IToolRunner>()));
        services.AddSingleton<IProjectGenerator>(sp => new NodeProjectGenerator(sp.GetRequiredService<IToolRunner>(), typeScript: true));
        services.AddSingleton<IProjectGenerator>(sp => new NodeProjectGenerator(sp.GetRequiredService<IToolRunner>(), typeScript: false));
        services.AddSingleton<IProjectGenerator>(sp => new StaticSiteGenerator(sp.GetRequiredService<IToolRunner>()));
        return services;
    }
}
