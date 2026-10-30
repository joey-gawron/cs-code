using System.Runtime.InteropServices;

namespace CsCode.Core.Platform;

/// <summary>
/// The rule CsCode.Lsp's <c>DotnetLaunchEnvironment</c> applies to language servers, for every other program CsCode starts with its own
/// environment (terminal shells, task processes; end pass T27): <c>dotnet run</c> and <c>dotnet test</c> give CsCode's OWN apphost
/// <c>DOTNET_ROOT_&lt;ARCH&gt;</c>, which outranks <c>DOTNET_ROOT</c>, so a .NET program started from CsCode whose wrapper exports the DOTNET_ROOT it
/// needs (nixpkgs' marksman, any Nix-wrapped .NET tool run in the terminal) would run on CsCode's runtime instead. The arch-specific variable is
/// folded into DOTNET_ROOT when that is unset (a direct apphost child resolves the same root either way) and dropped when DOTNET_ROOT names the
/// same directory; left alone when DOTNET_ROOT names a different one (a deliberate split set-up). Additive Core helper (end pass).
/// </summary>
public static class InheritedDotnetRoot
{
    public const string RootVariable = "DOTNET_ROOT";

    /// <summary><c>DOTNET_ROOT_X64</c>, <c>DOTNET_ROOT_ARM64</c>, …: the variable the .NET host reads first, for this process's architecture.</summary>
    public static string ArchRootVariable { get; } = RootVariable + "_" + RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();

    /// <summary>Applies the rule to an inherited environment (before the program's own variables are applied on top).</summary>
    public static void Fold(IDictionary<string, string?> environment)
    {
        if (Get(environment, ArchRootVariable) is not { } arch) return;
        var root = Get(environment, RootVariable);
        if (root is null)
        {
            environment[RootVariable] = arch;
            environment.Remove(ArchRootVariable);
        }
        else if (SameDirectory(root, arch)) environment.Remove(ArchRootVariable);
    }

    /// <summary><see cref="Fold"/> for an environment without null values.</summary>
    public static void FoldValues(IDictionary<string, string> environment)
    {
        if (!environment.TryGetValue(ArchRootVariable, out var arch) || string.IsNullOrEmpty(arch)) return;
        if (!environment.TryGetValue(RootVariable, out var root) || string.IsNullOrEmpty(root))
        {
            environment[RootVariable] = arch;
            environment.Remove(ArchRootVariable);
        }
        else if (SameDirectory(root, arch)) environment.Remove(ArchRootVariable);
    }

    private static string? Get(IDictionary<string, string?> environment, string name) =>
        environment.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null; // the host ignores an empty value too

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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return full; } // missing: compare as written
        }
    }
}
