namespace CsCode.Core.Terminal;

// Additive Core contract (language stage 1): what other modules add to a new terminal's environment. Consumed by CsCode.Terminal's launch resolver
// for local terminals; the selected Python environment is the first contributor.

/// <summary>
/// Variables to set (null removes one) and directories to put first on PATH. The terminal also passes the directories to its shell integration
/// as <c>CSCODE_PATH_PREPEND</c>, which puts them first again after the user's startup files ran (a macOS login shell's <c>path_helper</c> moves
/// them behind the system directories otherwise).
/// </summary>
public sealed record TerminalEnvironmentContribution(IReadOnlyDictionary<string, string?> Variables, IReadOnlyList<string> PathPrepend);

/// <summary>Adds to the environment of new terminals. Resolve all with <c>IEnumerable&lt;ITerminalEnvironmentContributor&gt;</c>.</summary>
public interface ITerminalEnvironmentContributor
{
    /// <summary>What to add for a terminal created with <paramref name="options"/>, or null. Cheap and non-blocking (no I/O, no waiting).</summary>
    TerminalEnvironmentContribution? Contribute(TerminalCreateOptions options);
}
