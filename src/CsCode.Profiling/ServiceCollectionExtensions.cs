using CsCode.Core.Diagnostics;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CsCode.Profiling;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the memory monitor (MUST 8): <see cref="MemoryMonitor"/> as a singleton, exposed as <see cref="IMemoryMonitor"/> and
    /// <see cref="IUserActivityMonitor"/>. Creating it does no I/O and starts nothing; the host calls <see cref="StartCsCodeProfiling"/> once it is up.
    /// Configuration, logging and the output service are optional and resolved lazily (a host without them gets an unlimited budget, idle trim on and
    /// no Memory channel). Override the timings by registering a <see cref="MemoryMonitorOptions"/> instance first.
    /// </summary>
    public static IServiceCollection AddCsCodeProfiling(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new MemoryMonitor(sp.GetService<MemoryMonitorOptions>(), () => sp.GetService<IConfigurationService>(),
            sp.GetService<ILoggerFactory>()));
        services.TryAddSingleton<IMemoryMonitor>(sp => sp.GetRequiredService<MemoryMonitor>());
        services.TryAddSingleton<IUserActivityMonitor>(sp => sp.GetRequiredService<MemoryMonitor>());
        return services;
    }

    /// <summary>
    /// The host's start call: starts the monitor's sampling thread, the "Memory" output channel (<see cref="MemoryLog"/>) when the host has an
    /// <see cref="IOutputService"/>, and, when it has an <see cref="IWorkspaceService"/>, starts the steady-growth grace over whenever the workspace's
    /// folders change (<see cref="MemoryMonitor.RestartGrowthGrace"/>; a folder opened long after start ramps like a start — file index, grammars,
    /// language servers). Cheap and non-blocking (one thread is created; settings are read on it). Disposing the result stops the channel writer and
    /// the folder subscription; the monitor itself stops when the container disposes it. Returns <see cref="Disposable.Empty"/> when profiling is
    /// not registered.
    /// </summary>
    public static IDisposable StartCsCodeProfiling(this IServiceProvider services)
    {
        if (services.GetService<MemoryMonitor>() is not { } monitor) return Disposable.Empty;
        monitor.Start();
        var started = new DisposableStore();
        if (services.GetService<IWorkspaceService>() is { } workspace) started.Add(workspace.FoldersChanged.Subscribe(_ => monitor.RestartGrowthGrace()));
        if (services.GetService<IOutputService>() is { } output) started.Add(new MemoryLog(monitor, output, services.GetService<IConfigurationService>()));
        return started;
    }
}
