using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;

namespace CsCode.Dap.Session;

// The Core view of the module list (IDebugSessionModules), for UIs that only know Core (the Modules window). The list itself is kept by
// OnModule (module events) and GetModulesAsync, which sends the `modules` request only to adapters that advertise it.
public sealed partial class DebugSession : IDebugSessionModules
{
    private readonly Emitter<IDebugSession> _modulesChanged = new();
    private int _modulesChangePosted;

    IEventSource<IDebugSession> IDebugSessionModules.ModulesChanged => _modulesChanged;

    IReadOnlyList<DebugModule> IDebugSessionModules.Modules => ToCoreModules(Modules);

    async ValueTask<IReadOnlyList<DebugModule>> IDebugSessionModules.GetModulesAsync(CancellationToken ct) => ToCoreModules(await GetModulesAsync(ct));

    private static List<DebugModule> ToCoreModules(IReadOnlyList<DebugModuleInfo> modules) =>
        [.. modules.Select(m => new DebugModule(m.Id, m.Name, m.Path, m.Version, m.IsUserCode, m.SymbolStatus))];

    /// <summary>
    /// Announces a change of <see cref="Modules"/> once per burst: the first <c>module</c> event posts the announcement behind the events
    /// already queued on the lane (an app start reports hundreds of modules in a row), later ones ride along until it ran.
    /// </summary>
    private void NotifyModulesChanged()
    {
        if (Interlocked.Exchange(ref _modulesChangePosted, 1) != 0) return;
        _lane.Post(() =>
        {
            Volatile.Write(ref _modulesChangePosted, 0);
            _modulesChanged.Fire(this);
        });
    }
}
