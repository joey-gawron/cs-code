using CsCode.Core.Debugging;

namespace CsCode.Dap.Session;

// The Core view of the module list (IDebugSessionModules), for UIs that only know Core (the Modules window). The list itself is kept by
// OnModule (module events) and GetModulesAsync, which sends the `modules` request only to adapters that advertise it.
public sealed partial class DebugSession : IDebugSessionModules
{
    IReadOnlyList<DebugModule> IDebugSessionModules.Modules => ToCoreModules(Modules);

    async ValueTask<IReadOnlyList<DebugModule>> IDebugSessionModules.GetModulesAsync(CancellationToken ct) => ToCoreModules(await GetModulesAsync(ct));

    private static List<DebugModule> ToCoreModules(IReadOnlyList<DebugModuleInfo> modules) =>
        [.. modules.Select(m => new DebugModule(m.Id, m.Name, m.Path, m.Version, m.IsUserCode, m.SymbolStatus))];
}
