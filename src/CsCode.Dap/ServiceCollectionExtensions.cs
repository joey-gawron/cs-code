using CsCode.Core.Debugging;
using CsCode.Core.Tasks;
using CsCode.Dap.Adapters;
using CsCode.Dap.Breakpoints;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Service;
using CsCode.Dap.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Dap;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the DAP module: <see cref="IDebugService"/> (+ <see cref="IDebugFrameFocus"/>, <see cref="IDebugOutputHistory"/>,
    /// <see cref="IDebugSourceContentProvider"/> resolving to the same instance), <see cref="IBreakpointManager"/>,
    /// <see cref="IDebugConfigurationService"/>, <see cref="ITaskService"/> (with cargo's, the .NET SDK's and zig's <see cref="ITaskProvider"/>s) and the built-in adapter factories (netcoredbg for
    /// <c>coreclr</c>/<c>netcoredbg</c>; js-debug for <c>pwa-node</c>/<c>node</c>; GDB's native DAP for <c>gdb</c>/<c>rust-gdb</c>; CodeLLDB for
    /// <c>lldb</c>; LLVM's lldb-dap for <c>lldb-dap</c>; debugpy for <c>debugpy</c>/<c>python</c>). Every Core platform service it can use (settings, state storage, window, commands, output…)
    /// is optional. Resolve <see cref="IDebugService"/> once at startup: its constructor registers the debug commands,
    /// <c>${command:pickProcess}</c> and the context keys.
    /// </summary>
    public static IServiceCollection AddCsCodeDap(this IServiceCollection services)
    {
        services.AddSingleton<DebugLane>(_ => new DebugLane());
        services.AddSingleton(DapPlatform.From);

        services.AddSingleton<BreakpointManager>(sp => new BreakpointManager(
            sp.GetService<CsCode.Core.Platform.IStateStorage>(),
            sp.GetService<CsCode.Core.Platform.IWorkspaceService>(),
            sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger("CsCode.Dap.Breakpoints")));
        services.AddSingleton<IBreakpointManager>(sp => sp.GetRequiredService<BreakpointManager>());
        services.AddSingleton<INativeBreakpointManager>(sp => sp.GetRequiredService<BreakpointManager>());

        services.AddSingleton<TaskService>(sp => new TaskService(sp.GetRequiredService<DebugLane>(), sp.GetRequiredService<DapPlatform>()));
        services.AddSingleton<ITaskService>(sp => sp.GetRequiredService<TaskService>());
        services.AddSingleton<ITaskProvider>(sp => new CargoTaskProvider(sp.GetRequiredService<DapPlatform>()));
        services.AddSingleton<ITaskProvider>(sp => new DotnetTaskProvider(sp.GetRequiredService<DapPlatform>()));
        services.AddSingleton<ITaskProvider>(sp => new ZigTaskProvider(sp.GetRequiredService<DapPlatform>()));

        services.AddSingleton<DebugConfigurationService>(sp => new DebugConfigurationService(
            sp.GetRequiredService<DebugLane>(), sp.GetRequiredService<DapPlatform>(), () => sp.GetService<ITaskService>()));
        services.AddSingleton<IDebugConfigurationService>(sp => sp.GetRequiredService<DebugConfigurationService>());

        services.AddSingleton<NetCoreDbgAdapterFactory>(sp => new NetCoreDbgAdapterFactory(sp.GetRequiredService<DapPlatform>()));
        foreach (var index in new[] { 0, 1 }) // coreclr and its alias netcoredbg
            services.AddSingleton<BuiltinDebugAdapter>(sp => NetCoreDbgAdapterFactory.Builtins(sp.GetRequiredService<NetCoreDbgAdapterFactory>()).ElementAt(index));
        services.AddSingleton<JsDebugAdapterFactory>(sp => new JsDebugAdapterFactory(sp.GetRequiredService<DapPlatform>()));
        foreach (var adapter in JsDebugAdapters())
            services.AddSingleton<BuiltinDebugAdapter>(sp => adapter(sp.GetRequiredService<JsDebugAdapterFactory>()));
        services.AddSingleton<GdbAdapterFactory>(sp => new GdbAdapterFactory(sp.GetRequiredService<DapPlatform>()));
        foreach (var index in new[] { 0, 1 })
            services.AddSingleton<BuiltinDebugAdapter>(sp => GdbAdapterFactory.Builtins(sp.GetRequiredService<GdbAdapterFactory>()).ElementAt(index));
        services.AddSingleton<CodeLldbAdapterFactory>(sp => new CodeLldbAdapterFactory(sp.GetRequiredService<DapPlatform>()));
        services.AddSingleton<BuiltinDebugAdapter>(sp => CodeLldbAdapterFactory.Builtins(sp.GetRequiredService<CodeLldbAdapterFactory>()).Single());
        services.AddSingleton<LldbDapAdapterFactory>(sp => new LldbDapAdapterFactory(sp.GetRequiredService<DapPlatform>()));
        services.AddSingleton<BuiltinDebugAdapter>(sp => LldbDapAdapterFactory.Builtins(sp.GetRequiredService<LldbDapAdapterFactory>()).Single());
        services.AddSingleton<DebugpyAdapterFactory>(sp => new DebugpyAdapterFactory(sp.GetRequiredService<DapPlatform>()));
        foreach (var index in new[] { 0, 1 }) // debugpy and its legacy alias python
            services.AddSingleton<BuiltinDebugAdapter>(sp => DebugpyAdapterFactory.Builtins(sp.GetRequiredService<DebugpyAdapterFactory>()).ElementAt(index));

        services.AddSingleton<DebugService>(sp => new DebugService(
            sp.GetRequiredService<DebugLane>(), sp.GetRequiredService<DapPlatform>(), sp.GetRequiredService<BreakpointManager>(),
            sp.GetRequiredService<DebugConfigurationService>(), sp.GetRequiredService<ITaskService>(), sp.GetServices<BuiltinDebugAdapter>()));
        services.AddSingleton<IDebugService>(sp => sp.GetRequiredService<DebugService>());
        services.AddSingleton<IDebugFrameFocus>(sp => sp.GetRequiredService<DebugService>());
        services.AddSingleton<IDebugOutputHistory>(sp => sp.GetRequiredService<DebugService>());
        services.AddSingleton<IDebugSourceContentProvider>(sp => sp.GetRequiredService<DebugService>());
        services.AddSingleton<IDebugKnownCapabilities>(sp => sp.GetRequiredService<DebugService>());
        return services;
    }

    /// <summary>
    /// js-debug's two debug types: <c>pwa-node</c> (what VS Code writes today) and its legacy alias <c>node</c>, which the provider rewrites to
    /// <c>pwa-node</c> (the standalone server only knows the pwa-* types). Both share one factory and one provider.
    /// </summary>
    public static IEnumerable<Func<JsDebugAdapterFactory, BuiltinDebugAdapter>> JsDebugAdapters() =>
    [
        f => new BuiltinDebugAdapter(JsDebugAdapterFactory.PrimaryType, "Node.js / TypeScript (js-debug)", JsDebugAdapterFactory.Languages, f, f),
        f => new BuiltinDebugAdapter("node", "Node.js (legacy 'node' type, js-debug)", JsDebugAdapterFactory.Languages, f, f) { IsAlias = true },
    ];
}
