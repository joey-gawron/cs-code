namespace CsCode.Core.Debugging;

/// <summary>A module (assembly / shared library) loaded by the debuggee, as the adapter reported it (DAP <c>Module</c>).</summary>
public sealed record DebugModule(string Id, string Name, string? Path = null, string? Version = null, bool? IsUserCode = null, string? SymbolStatus = null);

/// <summary>
/// Optional companion of <see cref="IDebugSession"/> (additive, like the interfaces in DebugExtras): the debuggee's modules. Adapters report
/// them with DAP <c>module</c> events, and only some also answer the <c>modules</c> request (<c>supportsModulesRequest</c>; netcoredbg does
/// not), so a UI should use this instead of sending that request itself.
/// </summary>
public interface IDebugSessionModules
{
    /// <summary>The modules reported so far (bounded).</summary>
    IReadOnlyList<DebugModule> Modules { get; }

    /// <summary>
    /// Refreshes the list with the <c>modules</c> request when the adapter advertises it (never otherwise), then returns <see cref="Modules"/>.
    /// </summary>
    ValueTask<IReadOnlyList<DebugModule>> GetModulesAsync(CancellationToken ct = default);
}
