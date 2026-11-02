using CsCode.Dap.Session;

namespace CsCode.Dap.Adapters;

/// <summary>
/// What Samsung netcoredbg does differently from what the client expects:
/// <list type="bullet">
/// <item><b>Breakpoint paths</b> are matched as strings against the source paths in the portable PDB, which the compiler writes with every
/// symbolic link resolved. On macOS the temporary directory (<c>/var/folders/…</c>) is a link into <c>/private/var</c>, so a project there (and
/// any project opened through a linked folder, on every platform) never stopped at its breakpoints: the breakpoint stayed pending and the
/// program ran to its end. Breakpoints are sent by their physical paths and frames mapped back (<see cref="DebugAdapterQuirks.BindsPhysicalSourcePaths"/>).
/// Found 2026-11-01 on macOS arm64 with netcoredbg 3.2.0-1092 (docs/CSHARP.md, "netcoredbg on macOS arm64").</item>
/// </list>
/// </summary>
public sealed class NetCoreDbgAdapterQuirks : DebugAdapterQuirks
{
    public static NetCoreDbgAdapterQuirks Instance { get; } = new();

    public override bool BindsPhysicalSourcePaths => true;
}
