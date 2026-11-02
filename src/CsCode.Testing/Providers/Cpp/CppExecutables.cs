using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using CsCode.Core.Build;
using CsCode.Core.Text;

namespace CsCode.Testing.Providers.Cpp;

/// <summary>The C++ test frameworks the provider runs.</summary>
public enum CppFramework { None, GoogleTest, Catch2, Doctest }

/// <summary>
/// Finds the test executables of a folder: the files matching the <c>cscode.testing.cpp.executables</c> globs (relative to the folder, as VS Code's
/// C++ TestMate reads <c>testMate.cpp.test.executables</c>) that are native executables (Mach-O, ELF, PE; shared libraries and objects are not)
/// and contain one framework's signature (<see cref="Detect"/>). Executables are never run to be classified: only those a signature names are
/// listed. Classifications are cached by path, size and modification time (bounded), so a rescan reads only what a build changed.
/// </summary>
public sealed class CppExecutables
{
    /// <summary>C++ TestMate's default: test-named files under the usual build directories.</summary>
    public static readonly IReadOnlyList<string> DefaultPatterns = ["{build,Build,BUILD,out,Out,OUT}/**/*{test,Test,TEST}*"];

    /// <summary>Directories never descended into: VCS and dependency trees, CMake's own scratch (compiler checks), fetched dependencies.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "CMakeFiles", "_deps", "vcpkg_installed", ".cache", ".scratch", "Testing",
    };

    private static readonly HashSet<string> NotExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        ".so", ".dylib", ".dll", ".a", ".lib", ".o", ".obj", ".pdb", ".ilk", ".exp", ".dSYM", ".json", ".txt", ".cmake", ".ninja", ".log", ".xml",
        ".h", ".hpp", ".c", ".cc", ".cpp", ".cxx", ".py", ".sh", ".ps1", ".bat", ".cmd",
    };

    /// <summary>Strings only a binary built with the framework contains (its help text, its reporter, an imported symbol when it is linked as a library).</summary>
    private static readonly (CppFramework Framework, byte[] Signature)[] Signatures =
    [
        (CppFramework.GoogleTest, "gtest_list_tests"u8.ToArray()),
        (CppFramework.GoogleTest, "N7testing14InitGoogleTest"u8.ToArray()),
        (CppFramework.Catch2, "Catch2TestRun"u8.ToArray()),          // v3's XML reporter
        (CppFramework.Catch2, "list-test-names-only"u8.ToArray()),   // v2's options
        (CppFramework.Doctest, "list-test-cases"u8.ToArray()),
    ];

    private const int MaxEntriesVisited = 200_000;
    private const int MaxDepth = 16;
    private const long MaxScannedBytes = 1L << 30;
    private const int MaxCached = 4096;

    private readonly ConcurrentDictionary<string, (long Size, DateTime Modified, CppFramework Framework)> _cache = new(StringComparer.Ordinal);

    /// <summary>An executable with its framework.</summary>
    public sealed record Found(string Path, CppFramework Framework);

    /// <summary>The test executables under <paramref name="folder"/> matching <paramref name="patterns"/>, in path order.</summary>
    public IReadOnlyList<Found> Find(string folder, IReadOnlyList<string> patterns, CancellationToken ct)
    {
        var result = new List<Found>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in patterns)
        {
            var (roots, pattern, relativeTo) = Prepare(folder, raw);
            foreach (var file in roots.Where(Directory.Exists).SelectMany(r => Walk(r, ct)))
            {
                var rel = "/" + Path.GetRelativePath(relativeTo, file).Replace('\\', '/');
                if (!Glob.IsMatch(pattern, rel) || !seen.Add(file)) continue;
                var framework = Classify(file);
                if (framework != CppFramework.None) result.Add(new Found(file, framework));
            }
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return result;
    }

    /// <summary>
    /// A pattern made anchored (<c>/build/**/x</c> matched against <c>/</c> + the path relative to the folder) and the directories to walk: its
    /// leading literal segments, where a brace of literals (<c>{build,out}</c>) names the directories that exist with one of those names exactly
    /// (so a case-insensitive file system does not walk <c>build</c> and <c>Build</c> twice). <c>${workspaceFolder}</c> is the folder; an absolute
    /// pattern is matched against absolute paths.
    /// </summary>
    internal static (IReadOnlyList<string> Roots, string Pattern, string RelativeTo) Prepare(string folder, string raw)
    {
        var p = raw.Trim().Replace('\\', '/');
        foreach (var variable in new[] { "${workspaceFolder}", "${workspaceRoot}" })
            if (p.StartsWith(variable, StringComparison.Ordinal)) p = p[variable.Length..].TrimStart('/');
        if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        var absolute = Path.IsPathRooted(p);
        var relativeTo = absolute ? Path.GetPathRoot(p)! : folder;
        var segments = (absolute ? p[relativeTo.Replace('\\', '/').Length..] : p).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var anchored = "/" + string.Join('/', segments);
        List<string> roots = [relativeTo];
        foreach (var s in segments.Length > 0 ? segments[..^1] : [])
        {
            if (s.IndexOfAny(['*', '?', '[']) < 0 && !s.Contains('{'))
            {
                roots = [.. roots.Select(r => Path.Combine(r, s))];
                continue;
            }
            if (s.StartsWith('{') && s.EndsWith('}') && s.IndexOfAny(['*', '?', '[', '/'], 1) < 0 && s.IndexOf('{', 1) < 0)
            {
                var names = s[1..^1].Split(',').ToHashSet(StringComparer.Ordinal);
                roots = [.. roots.SelectMany(r => Subdirectories(r).Where(names.Contains).Select(n => Path.Combine(r, n)))];
            }
            break;
        }
        return (roots, anchored, relativeTo);
    }

    private static IEnumerable<string> Subdirectories(string dir)
    {
        try { return new DirectoryInfo(dir).GetDirectories().Select(d => d.Name).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
    }

    /// <summary>Files under <paramref name="root"/> (depth-first, bounded), skipping dependency and VCS directories and never following links.</summary>
    private static IEnumerable<string> Walk(string root, CancellationToken ct)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        var visited = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(dir).GetFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
            foreach (var e in entries)
            {
                if (++visited > MaxEntriesVisited) yield break;
                if (e is DirectoryInfo d)
                {
                    if (depth < MaxDepth && d.LinkTarget is null && !Skipped.Contains(d.Name) && !d.Name.EndsWith(".dSYM", StringComparison.OrdinalIgnoreCase)
                        && !d.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                        stack.Push((d.FullName, depth + 1));
                }
                else if (!NotExecutables.Contains(Path.GetExtension(e.Name))) yield return e.FullName;
            }
        }
    }

    /// <summary>The framework of the executable at <paramref name="path"/> (None: not an executable, or no framework's signature), cached.</summary>
    public CppFramework Classify(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return CppFramework.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return CppFramework.None; }
        if (_cache.TryGetValue(path, out var c) && c.Size == info.Length && c.Modified == info.LastWriteTimeUtc) return c.Framework;
        var framework = IsExecutable(info) ? Detect(path) : CppFramework.None;
        if (_cache.Count >= MaxCached) _cache.Clear();
        _cache[path] = (info.Length, info.LastWriteTimeUtc, framework);
        return framework;
    }

    /// <summary>A native executable: executable by its owner (not on Windows, where it is an .exe) and a Mach-O, ELF or PE image by its first bytes.</summary>
    internal static bool IsExecutable(FileInfo info)
    {
        if (info.Length < 64) return false;
        if (OperatingSystem.IsWindows()) { if (!info.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return false; }
        else if ((info.UnixFileMode & UnixFileMode.UserExecute) == 0) return false;
        Span<byte> head = stackalloc byte[20];
        try
        {
            using var s = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            if (s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
        if (magic is 0xFEEDFACF or 0xFEEDFACE) return BinaryPrimitives.ReadUInt32LittleEndian(head[12..]) == 2; // Mach-O, MH_EXECUTE
        if (magic is 0xBEBAFECA) return true;                                  // a universal (fat) binary
        if (head[0] == 0x7F && head[1] == 'E' && head[2] == 'L' && head[3] == 'F') return (head[16] | head[17] << 8) is 2 or 3; // ET_EXEC, or ET_DYN (PIE)
        return head[0] == 'M' && head[1] == 'Z';
    }

    /// <summary>The framework whose signature the file contains (read in 4 MB blocks, at most 1 GB), else None.</summary>
    public static CppFramework Detect(string path)
    {
        var overlap = Signatures.Max(s => s.Signature.Length) - 1;
        var buffer = ArrayPool<byte>.Shared.Rent(4 << 20);
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            var kept = 0;
            long total = 0;
            var found = CppFramework.None;
            while (total < MaxScannedBytes)
            {
                var read = s.Read(buffer, kept, buffer.Length - kept);
                if (read <= 0) break;
                total += read;
                var span = buffer.AsSpan(0, kept + read);
                foreach (var (framework, signature) in Signatures)
                    if ((found == CppFramework.None || framework < found) && span.IndexOf(signature) >= 0) found = framework;
                if (found == CppFramework.GoogleTest) return found;
                kept = Math.Min(overlap, span.Length);
                span[^kept..].CopyTo(buffer);
            }
            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return CppFramework.None; }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>
    /// The framework a CMake target links, from the File API's view of it (<see cref="CMakeTarget"/>): one of the project's own framework
    /// targets among its dependencies (<c>gtest_main</c>, <c>Catch2WithMain</c> when the framework comes from <c>FetchContent</c> or
    /// <c>add_subdirectory</c>), else a framework library on its link line (an imported <c>GTest::gtest_main</c> or <c>Catch2::Catch2WithMain</c>
    /// shows there as <c>libgtest_main.a</c>, <c>libCatch2Maind.a</c>, <c>-lgtest</c> or <c>gtest.lib</c>). doctest is header-only, so nothing
    /// there names it unless the target links <c>doctest_with_main</c>: None.
    /// </summary>
    public static CppFramework FromTarget(CMakeTarget target)
    {
        foreach (var name in target.Dependencies.Concat(target.LinkLibraries.Select(LibraryName)))
            if (FrameworkLibrary(name) is var f and not CppFramework.None) return f;
        return CppFramework.None;
    }

    /// <summary>A framework's library by name, with or without the <c>d</c> debug postfix (GoogleTest's and Catch2's debug builds add it).</summary>
    private static CppFramework FrameworkLibrary(string name) => Library(name) is var f and not CppFramework.None
        ? f
        : name.Length > 1 && name.EndsWith('d') ? Library(name[..^1]) : CppFramework.None;

    private static CppFramework Library(string name) => name.ToLowerInvariant() switch
    {
        "gtest" or "gtest_main" or "gmock" or "gmock_main" => CppFramework.GoogleTest,
        "catch2" or "catch2main" or "catch2withmain" => CppFramework.Catch2,
        "doctest_with_main" => CppFramework.Doctest,
        _ => CppFramework.None,
    };

    /// <summary>
    /// A link-line fragment's library name: <c>-lgtest</c> → <c>gtest</c>; a path's file name up to its first dot, without <c>lib</c>
    /// (<c>/x/lib/libgtest_main.a</c> → <c>gtest_main</c>, <c>libgtest.so.1.17.0</c> → <c>gtest</c>, <c>gtest.lib</c> → <c>gtest</c>).
    /// </summary>
    internal static string LibraryName(string fragment)
    {
        var f = fragment.Trim().Trim('"');
        if (f.StartsWith("-l", StringComparison.Ordinal)) return f[2..];
        if (f.StartsWith('-')) return "";
        var name = f[(f.LastIndexOfAny(['/', '\\']) + 1)..];
        if (name.IndexOf('.') is var dot and > 0) name = name[..dot];
        return name.StartsWith("lib", StringComparison.Ordinal) && name.Length > 3 ? name[3..] : name;
    }

    /// <summary>A short name for the framework (the explorer shows it on the project).</summary>
    public static string Label(CppFramework f) => f switch
    {
        CppFramework.GoogleTest => "GoogleTest",
        CppFramework.Catch2 => "Catch2",
        CppFramework.Doctest => "doctest",
        _ => "",
    };

    /// <summary>Patterns read from a setting value: a string (one glob, as TestMate's) or a list of strings.</summary>
    public static IReadOnlyList<string>? Patterns(System.Text.Json.JsonElement? value)
    {
        if (value is not { } v) return null;
        if (v.ValueKind == System.Text.Json.JsonValueKind.String) return v.GetString() is { Length: > 0 } s ? [s] : null;
        if (v.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
        var list = v.EnumerateArray().Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String).Select(e => e.GetString()!).Where(s => s.Length > 0).ToList();
        return list;
    }

    internal static string Describe(IReadOnlyList<string> patterns)
    {
        var sb = new StringBuilder();
        foreach (var p in patterns) sb.Append(sb.Length > 0 ? ", " : "").Append(p);
        return sb.ToString();
    }
}
