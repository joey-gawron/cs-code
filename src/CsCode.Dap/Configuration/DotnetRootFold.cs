using System.Runtime.InteropServices;

namespace CsCode.Dap.Configuration;

/// <summary>
/// The .NET host part of the programs the debugger starts itself, from CsCode's own environment — debug adapters, as a process or as a server
/// (and so the programs they launch), and debuggees run without a terminal. <c>dotnet run</c> and <c>dotnet test</c> give CsCode's OWN apphost
/// <c>DOTNET_ROOT_&lt;ARCH&gt;</c> (<c>DOTNET_ROOT_X64</c>), which the .NET host reads before <c>DOTNET_ROOT</c>: inherited by a debuggee or a
/// tool, it overrides the DOTNET_ROOT that program's wrapper exports (a Nix-wrapped .NET tool targeting another runtime then fails with "You must
/// install or update .NET"). It is folded into DOTNET_ROOT, as the language servers' launch environment does (CsCode.Lsp's
/// DotnetLaunchEnvironment, rule 1): moved there when DOTNET_ROOT is unset, dropped when DOTNET_ROOT names the same directory, left alone when
/// DOTNET_ROOT names another one (a deliberate split set-up). An explicit environment (a launch configuration's <c>env</c>) that sets either
/// variable is the user's choice and is never changed. Not for programs in a terminal (tasks, <c>runInTerminal</c> debuggees): the terminal's
/// launch resolver folds what a local terminal inherits before the user's <c>terminal.integrated.env</c> applies (cross-branch review C3).
/// </summary>
internal static class DotnetRootFold
{
    public const string RootVariable = "DOTNET_ROOT";
    public static string ArchRootVariable { get; } = RootVariable + "_" + RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();

    /// <summary>Folds the variables in a child's environment (a copy of CsCode's with <paramref name="explicitEnv"/> applied).</summary>
    public static void Apply(IDictionary<string, string?> environment, IEnumerable<string>? explicitEnv = null)
    {
        if (explicitEnv is not null && explicitEnv.Any(k => k is RootVariable || k == ArchRootVariable)) return;
        foreach (var (name, value) in Changes(n => environment.TryGetValue(n, out var v) ? v : null))
        {
            if (value is null) environment.Remove(name);
            else environment[name] = value;
        }
    }

    private static Dictionary<string, string?> Changes(Func<string, string?> get)
    {
        var changes = new Dictionary<string, string?>(StringComparer.Ordinal);
        var arch = NonEmpty(get(ArchRootVariable));
        if (arch is null) return changes;
        var root = NonEmpty(get(RootVariable));
        if (root is null)
        {
            changes[RootVariable] = arch;
            changes[ArchRootVariable] = null;
        }
        else if (SameDirectory(root, arch)) changes[ArchRootVariable] = null;
        return changes;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value; // the host ignores an empty value too

    private static bool SameDirectory(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Normalize(a), Normalize(b), comparison);

        static string Normalize(string dir)
        {
            string full;
            try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return dir; }
            try { return Directory.ResolveLinkTarget(full, returnFinalTarget: true) is { } target ? Path.TrimEndingDirectorySeparator(target.FullName) : full; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return full; }
        }
    }
}
