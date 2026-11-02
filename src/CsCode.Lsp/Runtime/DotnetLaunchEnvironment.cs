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
/// <item><b>Provide DOTNET_ROOT</b> when it is still unset, the server is a .NET program (<see cref="IsDotnetServer"/>) AND the host's own lookup
/// would not do (lsp-core-5): when <see cref="HostDefaultRoot"/> (install_location, then the default global directory) is an installation that has
/// the frameworks the server's runtimeconfig.json asks for — or nothing says what it asks for — DOTNET_ROOT stays unset, because setting it makes the
/// host look nowhere else. Otherwise: the first of the dotnet on the server's PATH, in <c>cscode.lsp.searchDirs</c>, <c>~/.dotnet</c>, the shared
/// runtime CsCode runs on (<see cref="FindDotnetRoot(string?, IReadOnlyList{string}?)"/>) that has those frameworks (or simply the first). The
/// <c>[client]</c> log line says which root was chosen and why.</item>
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
        "csharp-ls", "marksman", "fsautocomplete", "omnisharp", "Microsoft.CodeAnalysis.LanguageServer", "roslyn-language-server", "dotnet",
    };

    /// <summary>Adjusts a server's environment (a copy of CsCode's, with the definition's <c>env</c> applied) as described on the class.</summary>
    /// <param name="executable">The resolved executable the server is started from.</param>
    /// <param name="log">Receives one <c>[client]</c> line when DOTNET_ROOT is provided (or could not be), and why.</param>
    public static void Apply(IDictionary<string, string?> environment, LanguageServerDefinition definition, string executable,
        IReadOnlyList<string>? searchDirs, Action<string>? log = null) =>
        Apply(environment, definition, executable, searchDirs, DefaultFallbackRoots(), log, HostDefaultRoot(), prependRootToPath: OperatingSystem.IsMacOS());

    /// <param name="hostDefaultRoot">
    /// Where the .NET host looks by itself when DOTNET_ROOT is unset (<see cref="HostDefaultRoot"/>; null: nowhere usable). lsp-core-5: rule 2 used to
    /// set DOTNET_ROOT even then, which on Linux makes the host look ONLY there: a distro or global runtime that served the server before was
    /// replaced by whatever dotnet came first on PATH, which may lack the server's framework.
    /// </param>
    /// <param name="prependRootToPath">
    /// lsp-3 (macOS): a DOTNET_ROOT provided here is also put first on the server's PATH (unless the definition's <c>env</c> sets PATH), so the
    /// <c>dotnet</c> the server runs itself (csharp-ls loading MSBuild) is the installation it runs on, also when CsCode's PATH has none.
    /// </param>
    internal static void Apply(IDictionary<string, string?> environment, LanguageServerDefinition definition, string executable,
        IReadOnlyList<string>? searchDirs, IEnumerable<string> fallbackRoots, Action<string>? log, string? hostDefaultRoot = null, bool prependRootToPath = false)
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

        // The frameworks the server's runtimeconfig.json asks for (empty when there is none to read: a wrapper script, a self-contained build).
        var required = RequiredFrameworks(executable, Get(environment, "DOTNET_ROLL_FORWARD"));
        var need = required.Count == 0 ? "" : " (needs " + string.Join(", ", required.Select(static f => $"{f.Name} {f.Version}")) + ")";
        if (hostDefaultRoot is not null && IsDotnetRoot(hostDefaultRoot) && (required.Count == 0 || Satisfies(hostDefaultRoot, required)))
        {
            log?.Invoke($"[client] {definition.Command} is a .NET program{need}; DOTNET_ROOT is not set and the .NET host finds {hostDefaultRoot} by itself: left unset");
            return;
        }

        var candidates = FindDotnetRoots(Get(environment, "PATH"), searchDirs, fallbackRoots);
        var chosen = required.Count == 0 ? candidates.FirstOrDefault() : candidates.FirstOrDefault(r => Satisfies(r, required));
        var reason = required.Count == 0 ? "the first dotnet found" : "the first dotnet found that has it";
        if (chosen is null && candidates.Count > 0)
        {
            chosen = candidates[0];
            reason = "no dotnet found has that framework; the first one found";
        }
        if (chosen is not null)
        {
            environment[RootVariable] = chosen;
            if (prependRootToPath && definition.Env?.ContainsKey("PATH") != true)
            {
                var path = Get(environment, "PATH");
                if (!(path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Any(d => SameDirectory(d, chosen)))
                    environment["PATH"] = path is null ? chosen : chosen + Path.PathSeparator + path;
            }
            log?.Invoke($"[client] {definition.Command} is a .NET program{need} and DOTNET_ROOT is not set: using {chosen} ({reason})");
        }
        else log?.Invoke($"[client] {definition.Command} is a .NET program{need}, DOTNET_ROOT is not set and no dotnet was found (PATH, cscode.lsp.searchDirs, ~/.dotnet)");
    }

    /// <summary>A framework a .NET program's runtimeconfig.json requires, with the roll-forward policy that decides which installed versions fit.</summary>
    internal readonly record struct RequiredFramework(string Name, Version Version, string RollForward);

    /// <summary>
    /// Where the .NET host looks for the runtime when DOTNET_ROOT is unset: <c>/etc/dotnet/install_location_&lt;arch&gt;</c>, then
    /// <c>/etc/dotnet/install_location</c> (distribution packages register themselves there), then the platform's default global location
    /// (<c>/usr/share/dotnet</c> on Linux, <c>/usr/local/share/dotnet</c> on macOS, <c>%ProgramFiles%\dotnet</c> on Windows). Null when none of them is
    /// an installation.
    /// </summary>
    public static string? HostDefaultRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var dir = string.IsNullOrEmpty(programFiles) ? null : Path.Combine(programFiles, "dotnet");
            return IsDotnetRoot(dir) ? dir : null;
        }
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        foreach (var file in new[] { "/etc/dotnet/install_location_" + arch, "/etc/dotnet/install_location" })
        {
            string? line;
            try { line = File.Exists(file) ? File.ReadLines(file).FirstOrDefault()?.Trim() : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { line = null; }
            if (!string.IsNullOrEmpty(line)) return IsDotnetRoot(line) ? Path.TrimEndingDirectorySeparator(line) : null; // the host does not look further
        }
        var fallback = OperatingSystem.IsMacOS() ? "/usr/local/share/dotnet" : "/usr/share/dotnet";
        return IsDotnetRoot(fallback) ? fallback : null;
    }

    /// <summary>
    /// The frameworks named by the program's <c>.runtimeconfig.json</c>: beside the executable (or the file a symlink to it ends at), or, for a
    /// <c>dotnet tool</c> shim, the one of the program the shim runs (<see cref="ShimAppPath"/>: the path <c>dotnet tool install</c> wrote into it,
    /// <c>.store/&lt;tool&gt;/&lt;version&gt;/&lt;tool&gt;/&lt;version&gt;/tools/&lt;tfm&gt;/any/&lt;program&gt;.dll</c>). When that path cannot be read, the newest
    /// <c>.store</c> version is used, and if it was packed for several target frameworks (<c>tools/net8.0</c> and <c>tools/net9.0</c>) the
    /// requirements of ALL of them are returned: not knowing which one runs, a root is only good enough when it has each (lsp-core-5 follow-up:
    /// the first file found used to decide, by file-system order). Empty when there is none.
    /// </summary>
    internal static IReadOnlyList<RequiredFramework> RequiredFrameworks(string executable, string? rollForwardOverride = null)
    {
        foreach (var group in RuntimeConfigCandidates(executable))
        {
            List<RequiredFramework>? all = null;
            foreach (var config in group)
                if (ReadRuntimeConfig(config, rollForwardOverride) is { } frameworks)
                    foreach (var f in frameworks)
                        if (!(all ??= []).Contains(f)) all.Add(f);
            if (all is not null) return all;
        }
        return [];
    }

    private static List<RequiredFramework>? ReadRuntimeConfig(string config, string? rollForwardOverride)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config));
            if (!doc.RootElement.TryGetProperty("runtimeOptions", out var options)) return null;
            var rollForward = rollForwardOverride
                ?? (options.TryGetProperty("rollForward", out var rf) && rf.ValueKind == System.Text.Json.JsonValueKind.String ? rf.GetString() : null)
                ?? "Minor";
            var result = new List<RequiredFramework>();
            IEnumerable<System.Text.Json.JsonElement> frameworks = options.TryGetProperty("frameworks", out var many) && many.ValueKind == System.Text.Json.JsonValueKind.Array
                ? many.EnumerateArray()
                : options.TryGetProperty("framework", out var one) ? [one] : [];
            foreach (var f in frameworks)
                if (f.TryGetProperty("name", out var n) && f.TryGetProperty("version", out var v) && ParseVersion(v.GetString()) is { } version)
                    result.Add(new RequiredFramework(n.GetString() ?? "", version, rollForward));
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>Groups of runtimeconfig files, in order; within a group any one may be the program that runs (several target frameworks).</summary>
    private static IEnumerable<IReadOnlyList<string>> RuntimeConfigCandidates(string executable)
    {
        foreach (var file in new[] { executable, FinalTarget(executable) }.Distinct(StringComparer.Ordinal))
        {
            var dir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(dir)) continue;
            var beside = Path.Combine(dir, Stem(file) + ".runtimeconfig.json");
            if (File.Exists(beside)) yield return [beside];
            if (!Directory.Exists(Path.Combine(dir, ".store"))) continue;
            if (ShimAppPath(file) is { } app)
            {
                var exact = Path.ChangeExtension(app, ".runtimeconfig.json");
                if (File.Exists(exact)) { yield return [exact]; continue; } // the program the shim runs: nothing else matters
            }
            var store = Path.Combine(dir, ".store", Stem(file).ToLowerInvariant());
            if (!Directory.Exists(store)) continue;
            string[] versions;
            try { versions = Directory.EnumerateDirectories(store).OrderByDescending(static d => ParseVersion(Path.GetFileName(d)) ?? new Version()).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var version in versions)
                if (SafeFiles(version, "*.runtimeconfig.json") is { Length: > 0 } configs) yield return configs;
        }
    }

    private static string[] SafeFiles(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true })
                .Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>An apphost is ~100 KB; anything much larger beside a <c>.store</c> is not a tool shim and is not read.</summary>
    private const int MaxShimBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The program a <c>dotnet tool</c> shim runs. The shim is an apphost: <c>dotnet tool install</c> wrote the path of the tool's <c>.dll</c> for the
    /// target framework it chose into it (a NUL-terminated UTF-8 string, relative to the shim's directory, e.g.
    /// <c>.store/csharp-ls/0.28.0/csharp-ls/0.28.0/tools/net10.0/any/CSharpLanguageServer.dll</c>). That is the file the .NET host loads, so its
    /// runtimeconfig.json is the one that decides which runtime is needed. Null when <paramref name="shim"/> holds no such path.
    /// </summary>
    internal static string? ShimAppPath(string shim)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(shim);
            if (!info.Exists || info.Length > MaxShimBytes) return null;
            bytes = File.ReadAllBytes(shim);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }

        ReadOnlySpan<byte> marker = ".store"u8;
        var span = bytes.AsSpan();
        for (var from = 0; from < span.Length;)
        {
            var at = span[from..].IndexOf(marker);
            if (at < 0) return null;
            at += from;
            from = at + marker.Length;
            if (from >= span.Length || span[from] is not ((byte)'/' or (byte)'\\')) continue;
            var length = span[at..].IndexOf((byte)0);
            if (length is <= 0 or > 4096) continue;
            var text = System.Text.Encoding.UTF8.GetString(span.Slice(at, length));
            if (!text.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || text.Any(char.IsControl)) continue;
            try { return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(shim))!, text.Replace('\\', Path.DirectorySeparatorChar))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
        }
        return null;
    }

    /// <summary>Whether <paramref name="root"/> has a version of every required framework that its roll-forward policy accepts.</summary>
    internal static bool Satisfies(string root, IReadOnlyList<RequiredFramework> required)
    {
        foreach (var f in required)
        {
            string[] versions;
            try { versions = Directory.Exists(Path.Combine(root, "shared", f.Name)) ? Directory.GetDirectories(Path.Combine(root, "shared", f.Name)) : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { versions = []; }
            var ok = versions.Select(static d => ParseVersion(Path.GetFileName(d))).Any(v => v is not null && Fits(v, f));
            if (!ok) return false;
        }
        return true;
    }

    private static bool Fits(Version installed, RequiredFramework f)
    {
        if (installed < f.Version) return false;
        return f.RollForward.ToLowerInvariant() switch
        {
            "disable" => installed.Major == f.Version.Major && installed.Minor == f.Version.Minor && installed.Build == f.Version.Build,
            "latestpatch" => installed.Major == f.Version.Major && installed.Minor == f.Version.Minor,
            "major" or "latestmajor" => true,
            _ => installed.Major == f.Version.Major, // Minor (the default) and LatestMinor
        };
    }

    /// <summary>"8.0.11" or "9.0.0-preview.7.24405.7" → 8.0.11 / 9.0.0; null for anything else.</summary>
    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        return Version.TryParse(dash > 0 ? text[..dash] : text, out var v) ? v : null;
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

    internal static string? FindDotnetRoot(string? pathVariable, IReadOnlyList<string>? searchDirs, IEnumerable<string> fallbackRoots) =>
        FindDotnetRoots(pathVariable, searchDirs, fallbackRoots).FirstOrDefault();

    /// <summary>Every installation <see cref="FindDotnetRoot(string?, IReadOnlyList{string}?)"/> would consider, in its order, without duplicates.</summary>
    internal static List<string> FindDotnetRoots(string? pathVariable, IReadOnlyList<string>? searchDirs, IEnumerable<string> fallbackRoots)
    {
        var found = new List<string>();
        void Add(string root)
        {
            if (!found.Any(f => SameDirectory(f, root))) found.Add(root);
        }
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
                if (IsDotnetRoot(root)) { Add(root!); break; }
        }
        foreach (var root in fallbackRoots)
            if (IsDotnetRoot(root)) Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        return found;
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
