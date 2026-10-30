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
    /// The host's start call: starts the monitor's sampling thread and, when the host has an <see cref="IOutputService"/>, the "Memory" output channel
    /// (<see cref="MemoryLog"/>). Cheap and non-blocking (one thread is created; settings are read on it). Disposing the result stops the channel
    /// writer; the monitor itself stops when the container disposes it. Returns <see cref="Disposable.Empty"/> when profiling is not registered.
    /// </summary>
    public static IDisposable StartCsCodeProfiling(this IServiceProvider services)
    {
        if (services.GetService<MemoryMonitor>() is not { } monitor) return Disposable.Empty;
        monitor.Start();
        if (services.GetService<IOutputService>() is not { } output) return Disposable.Empty;
        return new MemoryLog(monitor, output, services.GetService<IConfigurationService>());
    }
}
