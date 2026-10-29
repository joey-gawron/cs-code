namespace CsCode.Core.Debugging;

/// <summary>
/// Optional companion of <see cref="IDebugSession"/> (additive, like the interfaces in DebugExtras): DAP's filtered variable paging. For a
/// value with <see cref="DebugVariable.IndexedVariables"/>, <paramref name="filter"/> <c>"indexed"</c> pages through the elements with
/// <c>start</c>/<c>count</c> and <c>"named"</c> returns the other children (an array's <c>length</c>, its prototype). DAP only promises that
/// <c>start</c>/<c>count</c> apply to a filtered set: js-debug v1.140.0 ignores <c>start</c> without a filter (it answers every page with
/// elements 0…), so a UI paging a large array must use this.
/// </summary>
public interface IDebugSessionVariablePaging
{
    ValueTask<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, string? filter, int? start, int? count, CancellationToken ct = default);
}
