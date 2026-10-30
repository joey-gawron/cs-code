using System.Runtime.InteropServices;
using CsCode.Core.Languages;

namespace CsCode.Lsp.Runtime;

/// <summary>
/// The .NET part of a language server's launch environment. A framework-dependent .NET program (an "apphost": csharp-ls from
/// <c>dotnet tool install -g</c>, fsautocomplete, a Marksman that is not self-contained) finds its runtime through <c>DOTNET_ROOT_&lt;ARCH&gt;</c>,
/// then <c>DOTNET_ROOT</c>, then the default install locations. A dotnet from Nix, <c>~/.dotnet</c> or a tarball is in none of those, so a server
/// started by an editor whose environment lacks DOTNET_ROOT (the installed app started from the desktop menu) dies with "You must install or
/// update .NET to run this application". Two rules, applied to every process server after the definition's own <c>env</c>:
/// <list type="number">
/// <item><b>Fold the arch-specific variable into DOTNET_ROOT.</b> <c>dotnet run</c> and <c>dotnet test</c> set <c>DOTNET_ROOT_X64</c> for CsCode's
/// OWN apphost. It outranks DOTNET_ROOT, so a server's wrapper that exports the DOTNET_ROOT it needs loses to it: nixpkgs' marksman wrapper points
/// DOTNET_ROOT at the .NET 9 runtime marksman targets, and the inherited DOTNET_ROOT_X64 (the SDK's .NET 10) made it fail. A direct apphost child
/// resolves the same root either way. Left alone when DOTNET_ROOT names a different directory (a deliberate split set-up).</item>
/// <item><b>Provide DOTNET_ROOT</b> when it is still unset and the server is a .NET program (<see cref="IsDotnetServer"/>): the root of the dotnet on
/// the server's PATH, then in <c>cscode.lsp.searchDirs</c>, then <c>~/.dotnet</c>, then the shared runtime CsCode itself runs on
/// (<see cref="FindDotnetRoot(string?, IReadOnlyList{string}?)"/>).</item>
/// </list>
/// A definition whose <c>env</c> sets either variable is the user's explicit choice and is never changed. Nothing here starts a process.
/// </summary>
public static class DotnetLaunchEnvironment
{
    public const string RootVariable = "DOTNET_ROOT";

    /// <summary><c>DOTNET_ROOT_X64</c>, <c>DOTNET_ROOT_ARM64</c>, …: the variable the .NET host reads first, for this process's architecture.</summary>
    public static string ArchRootVariable { get; } = RootVariable + "_" + RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();

    /// <summary>
    /// Server commands known to be framework-dependent .NET programs even when the executable does not show it (a wrapper script, as nixpkgs
    /// installs them). For a self-contained build DOTNET_ROOT is simply ignored.
    /// </summary>
    public static IReadOnlySet<string> KnownDotnetServers { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "csharp-ls", "marksman", "fsautocomplete", "omnisharp", "Microsoft.CodeAnalysis.LanguageServer", "dotnet",
    };

    /// <summary>Adjusts a server's environment (a copy of CsCode's, with the definition's <c>env</c> applied) as described on the class.</summary>
    /// <param name="executable">The resolved executable the server is started from.</param>
    /// <param name="log">Receives one <c>[client]</c> line when DOTNET_ROOT is provided (or could not be).</param>
    public static void Apply(IDictionary<string, string?> environment, LanguageServerDefinition definition, string executable,
        IReadOnlyList<string>? searchDirs, Action<string>? log = null) =>
        Apply(environment, definition, executable, searchDirs, DefaultFallbackRoots(), log);

    internal static void Apply(IDictionary<string, string?> environment, LanguageServerDefinition definition, string executable,
        IReadOnlyList<string>? searchDirs, IEnumerable<string> fallbackRoots, Action<string>? log)
    {
        if (definition.Env is { } own && (own.ContainsKey(RootVariable) || own.ContainsKey(ArchRootVariable))) return;

        var root = Get(environment, RootVariable);
        if (Get(environment, ArchRootVariable) is { } arch)
        {
            if (root is null)
            {
                environment[RootVariable] = root = arch;
                environment.Remove(ArchRootVariable);
            }
            else if (SameDirectory(root, arch)) environment.Remove(ArchRootVariable);
        }
        if (root is not null || !IsDotnetServer(definition.Command, executable)) return;

        if (FindDotnetRoot(Get(environment, "PATH"), searchDirs, fallbackRoots) is { } found)
        {
            environment[RootVariable] = found;
            log?.Invoke($"[client] {definition.Command} is a .NET program and DOTNET_ROOT is not set: using {found}");
        }
        else log?.Invoke($"[client] {definition.Command} is a .NET program, DOTNET_ROOT is not set and no dotnet was found (PATH, cscode.lsp.searchDirs, ~/.dotnet)");
    }

    /// <summary>
    /// Whether <paramref name="executable"/> (started for the definition command <paramref name="command"/>) is a .NET program: a known one, an
    /// apphost with its <c>.dll</c> or <c>.runtimeconfig.json</c> beside it (also through a symlink), or a <c>dotnet tool</c> shim (a <c>.store</c>
    /// directory beside it: global tools and <c>--tool-path</c> installs).
    /// </summary>
    public static bool IsDotnetServer(string command, string executable)
    {
        if (KnownDotnetServers.Contains(Stem(command)) || KnownDotnetServers.Contains(Stem(executable))) return true;
        foreach (var file in new[] { executable, FinalTarget(executable) }.Distinct(StringComparer.Ordinal))
        {
            var dir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(dir)) continue;
            var stem = Stem(file);
            if (File.Exists(Path.Combine(dir, stem + ".dll")) || File.Exists(Path.Combine(dir, stem + ".runtimeconfig.json"))
                || Directory.Exists(Path.Combine(dir, ".store")))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The installation directory (the one holding <c>host/fxr</c>) of the first dotnet found on <paramref name="pathVariable"/> (null: CsCode's
    /// PATH), then in <paramref name="searchDirs"/>, then <c>~/.dotnet</c>, then the shared runtime CsCode runs on; symlinks are followed
    /// (<c>~/.nix-profile/bin/dotnet</c> → the store's <c>share/dotnet</c>). Null when there is none.
    /// </summary>
    public static string? FindDotnetRoot(string? pathVariable, IReadOnlyList<string>? searchDirs) =>
        FindDotnetRoot(pathVariable, searchDirs, DefaultFallbackRoots());

    internal static string? FindDotnetRoot(string? pathVariable, IReadOnlyList<string>? searchDirs, IEnumerable<string> fallbackRoots)
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var dirs = (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(searchDirs ?? []);
        foreach (var dir in dirs)
        {
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(dir, exe)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!ToolResolver.IsExecutableFile(candidate)) continue;
            // the real file first (a symlinked dotnet's own directory is a bin folder), then the directory it was found in
            foreach (var root in new[] { Path.GetDirectoryName(FinalTarget(candidate)), Path.GetDirectoryName(candidate) })
                if (IsDotnetRoot(root)) return root;
        }
        foreach (var root in fallbackRoots)
            if (IsDotnetRoot(root)) return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return null;
    }

    /// <summary>A dotnet installation directory: the host resolver lives in <c>host/fxr/&lt;version&gt;</c>.</summary>
    public static bool IsDotnetRoot(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        try { return Directory.Exists(Path.Combine(dir, "host", "fxr")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary><c>~/.dotnet</c> (dotnet-install.sh's default), then the root of the shared runtime this process runs on (none when self-contained).</summary>
    private static IEnumerable<string> DefaultFallbackRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, ".dotnet");
        // shared/Microsoft.NETCore.App/<version>/ → the root three levels up; a self-contained app's directory has no host/fxr above it
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        if (!string.IsNullOrEmpty(runtime)) yield return Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
    }

    private static string? Get(IDictionary<string, string?> environment, string name) =>
        environment.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null; // the host ignores an empty value too

    private static string Stem(string path)
    {
        var name = Path.GetFileName(path);
        return OperatingSystem.IsWindows() && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>The file a chain of symlinks ends at (full path); the path itself when it is not a link or cannot be read.</summary>
    private static string FinalTarget(string path)
    {
        try { return File.ResolveLinkTarget(path, returnFinalTarget: true) is { } target ? Path.GetFullPath(target.FullName) : path; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return path; }
    }

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
