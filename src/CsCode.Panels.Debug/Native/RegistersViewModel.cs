using System.Text.RegularExpressions;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Infrastructure;
using CsCode.Panels.Debug.Variables;

namespace CsCode.Panels.Debug.Native;

/// <summary>
/// Registers window: the focused frame's register scope, which the native adapters expose as a scope of their own (CodeLLDB "Registers", with
/// "General Purpose Registers", "Floating Point Registers"… under it; lldb-dap "Registers"; GDB "Registers"). Adapters mark it expensive, so
/// Locals never loads it; this window does, at each stop and frame change, as the Locals window loads its scopes (same tree: values that changed
/// since the last stop are highlighted, Edit Value sets a register where the adapter allows it).
/// </summary>
public sealed partial class RegistersViewModel : FrameVariablesViewModel
{
    public RegistersViewModel(DebugPanelServices services) : base(services) => StartTracking();

    [GeneratedRegex("regist", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegistersName();

    /// <summary>The register scopes of a frame: hinted <c>registers</c>, or named like it.</summary>
    public static IReadOnlyList<DebugScope> ForRegisters(IReadOnlyList<DebugScope> scopes) =>
        [.. scopes.Where(s => s.VariablesReference > 0 && (s.PresentationHint == "registers" || RegistersName().IsMatch(s.Name)))];

    protected override async Task LoadFrameAsync(IDebugSession session, DebugFocus focus, LoadToken token)
    {
        var scopes = await Services.Ui.OnUi(session.GetScopesAsync(focus.FrameId!.Value, token.Cancellation), token.Cancellation);
        if (token.Generation != Tree.Generation) return;
        var registers = ForRegisters(scopes);
        if (registers.Count == 0)
        {
            Tree.SetStatus("The debug adapter shows no registers for this frame.");
            return;
        }
        await Tree.ShowScopesAsync(token, registers);
    }
}
