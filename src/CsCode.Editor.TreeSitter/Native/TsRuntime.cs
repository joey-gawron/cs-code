using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CsCode.Editor.TreeSitter.Native;

/// <summary>
/// Finds, checks and loads <c>libtree-sitter</c> (the runtime is NOT shipped with CsCode; see docs/TREE-SITTER-EVALUATION.md §Distribution).
/// Search order: <see cref="LibraryPathOverride"/> / <c>CSCODE_TREESITTER_LIB</c>, the extra directories passed to <see cref="Load"/>
/// (the user's <c>{UserDataDir}/tree-sitter</c>), the system's default library search (<c>libtree-sitter.so.0</c>, <c>tree-sitter.dll</c>,
/// <c>libtree-sitter.dylib</c>), and on a Nix machine the newest <c>/nix/store/*-tree-sitter-0.2x*/lib</c>. A candidate is accepted only
/// when it exports the 0.25+ API the engine uses (progress callbacks, <c>ts_language_abi_version</c>); an older one is skipped with a reason.
///
/// Loading is idempotent and thread-safe; it is done once, off the UI thread (the editor's I/O lane or worker).
/// </summary>
public static unsafe class TsRuntime
{
    /// <summary>Grammar ABI range this runtime accepts (TREE_SITTER_MIN_COMPATIBLE_LANGUAGE_VERSION .. TREE_SITTER_LANGUAGE_VERSION of 0.25/0.26).</summary>
    public const uint MinAbi = 13;
    public const uint MaxAbi = 15;

    private static readonly object Gate = new();
    private static readonly List<string> Attempts = new();
    private static IntPtr _handle;
    private static bool _resolverInstalled;
    private static bool _loadAttempted;
    private static long _trackedBytes;
    private static long _trackedAllocations;
    private static IntPtr _currentFree;

    /// <summary>Explicit library path (tests, the bench script). Also read from the environment variable <c>CSCODE_TREESITTER_LIB</c>.</summary>
    public static string? LibraryPathOverride { get; set; }

    /// <summary>
    /// Count every byte tree-sitter allocates (a counting allocator installed through <c>ts_set_allocator</c> before the first allocation).
    /// Off by default: each allocation then costs a native→managed transition. Turned on by tests/benchmarks or <c>CSCODE_TREESITTER_TRACK_ALLOC=1</c>;
    /// it must be set before the first <see cref="Load"/> to take effect.
    /// </summary>
    public static bool TrackAllocations { get; set; } = Environment.GetEnvironmentVariable("CSCODE_TREESITTER_TRACK_ALLOC") == "1";

    public static bool IsAvailable => _handle != IntPtr.Zero;

    /// <summary>Path of the loaded library, or null.</summary>
    public static string? LoadedPath { get; private set; }

    /// <summary>Why the runtime is not available (every candidate tried and why it was rejected), or null when it is.</summary>
    public static string? FailureReason { get; private set; }

    /// <summary>True when the counting allocator is installed (then <see cref="AllocatedBytes"/> is exact).</summary>
    public static bool AllocationsTracked { get; private set; }

    /// <summary>Bytes currently allocated by tree-sitter (parsers, trees, queries, cursors) when <see cref="AllocationsTracked"/>; else -1.</summary>
    public static long AllocatedBytes => AllocationsTracked ? Interlocked.Read(ref _trackedBytes) : -1;

    public static long AllocationCount => Interlocked.Read(ref _trackedAllocations);

    /// <summary>Loads the runtime once. Returns <see cref="IsAvailable"/>. Never throws.</summary>
    public static bool Load(IEnumerable<string>? extraDirectories = null)
    {
        lock (Gate)
        {
            if (_loadAttempted) return IsAvailable;
            _loadAttempted = true;
            InstallResolver();
            foreach (var candidate in Candidates(extraDirectories))
            {
                if (TryAccept(candidate)) break;
            }
            if (_handle == IntPtr.Zero)
            {
                FailureReason = "libtree-sitter (0.25 or newer) was not found. Tried: " + (Attempts.Count == 0 ? "nothing" : string.Join("; ", Attempts));
                return false;
            }
            _currentFree = NativeLibrary.TryGetExport(_handle, "ts_current_free", out var slot) ? slot : IntPtr.Zero;
            if (TrackAllocations) InstallCountingAllocator();
            return true;
        }
    }

    private static void InstallResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;
        NativeLibrary.SetDllImportResolver(typeof(TsRuntime).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path) =>
        name == TsApi.Library ? _handle : IntPtr.Zero; // IntPtr.Zero = default probing (libc.so.6)

    private static IEnumerable<string> Candidates(IEnumerable<string>? extraDirectories)
    {
        if (!string.IsNullOrEmpty(LibraryPathOverride)) yield return LibraryPathOverride!;
        if (Environment.GetEnvironmentVariable("CSCODE_TREESITTER_LIB") is { Length: > 0 } env) yield return env;
        foreach (var dir in extraDirectories ?? [])
            foreach (var file in LibraryFileNames)
                yield return Path.Combine(dir, file);
        foreach (var file in LibraryFileNames) yield return file; // the platform's default search
        foreach (var nix in NixStoreRuntimes()) yield return nix;
    }

    private static string[] LibraryFileNames =>
        OperatingSystem.IsWindows() ? ["tree-sitter.dll", "libtree-sitter.dll"]
        : OperatingSystem.IsMacOS() ? ["libtree-sitter.0.dylib", "libtree-sitter.dylib"]
        : ["libtree-sitter.so.0", "libtree-sitter.so"];

    /// <summary>The Nix store's tree-sitter runtimes, newest version first (a developer-machine convenience: nothing is installed or changed).</summary>
    internal static IEnumerable<string> NixStoreRuntimes()
    {
        if (!OperatingSystem.IsLinux()) return [];
        try
        {
            var found = new List<(Version Version, string Path)>();
            foreach (var dir in NixStore.Entries())
            {
                // "<hash>-tree-sitter-0.26.11": the runtime package itself (grammar packages are "<hash>-tree-sitter-<lang>-...")
                var name = Path.GetFileName(dir);
                var dash = name.IndexOf('-', StringComparison.Ordinal);
                if (dash < 0) continue;
                var rest = name[(dash + 1)..];
                if (!rest.StartsWith("tree-sitter-0.", StringComparison.Ordinal)) continue;
                if (!Version.TryParse(rest["tree-sitter-".Length..], out var v)) continue;
                var lib = Path.Combine(dir, "lib", "libtree-sitter.so");
                if (File.Exists(lib)) found.Add((v, lib));
            }
            return found.OrderByDescending(f => f.Version).Select(f => f.Path).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryAccept(string candidate)
    {
        IntPtr handle;
        try
        {
            if (!NativeLibrary.TryLoad(candidate, out handle))
            {
                if (Path.IsPathRooted(candidate) && File.Exists(candidate)) Attempts.Add(candidate + " (could not be loaded)");
                return false;
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentException)
        {
            Attempts.Add($"{candidate} ({ex.GetType().Name})");
            return false;
        }
        // 0.25 introduced the progress-callback parse API (the old cancellation flag and timeout are gone in 0.26) and ts_language_abi_version.
        string[] required = ["ts_parser_parse_with_options", "ts_language_abi_version", "ts_query_cursor_next_capture", "ts_set_allocator"];
        var missing = required.Where(r => !NativeLibrary.TryGetExport(handle, r, out _)).ToArray();
        if (missing.Length > 0)
        {
            Attempts.Add($"{candidate} (too old: no {string.Join(", ", missing)})");
            NativeLibrary.Free(handle);
            return false;
        }
        _handle = handle;
        LoadedPath = candidate;
        FailureReason = null;
        return true;
    }

    // ---- allocation accounting ----

    private static void InstallCountingAllocator()
    {
        TsApi.ts_set_allocator(&CountingMalloc, &CountingCalloc, &CountingRealloc, &CountingFree);
        AllocationsTracked = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* CountingMalloc(nuint size)
    {
        var p = LibC.malloc(size);
        if (p is not null)
        {
            Interlocked.Add(ref _trackedBytes, (long)LibC.malloc_usable_size(p));
            Interlocked.Increment(ref _trackedAllocations);
        }
        return p;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* CountingCalloc(nuint count, nuint size)
    {
        var p = LibC.calloc(count, size);
        if (p is not null)
        {
            Interlocked.Add(ref _trackedBytes, (long)LibC.malloc_usable_size(p));
            Interlocked.Increment(ref _trackedAllocations);
        }
        return p;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* CountingRealloc(void* ptr, nuint size)
    {
        var before = ptr is null ? 0L : (long)LibC.malloc_usable_size(ptr);
        var p = LibC.realloc(ptr, size);
        if (p is not null) Interlocked.Add(ref _trackedBytes, (long)LibC.malloc_usable_size(p) - before);
        else if (size == 0) Interlocked.Add(ref _trackedBytes, -before);
        return p;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CountingFree(void* ptr)
    {
        if (ptr is null) return;
        Interlocked.Add(ref _trackedBytes, -(long)LibC.malloc_usable_size(ptr));
        LibC.free(ptr);
    }

    /// <summary>Frees memory tree-sitter handed to the caller (<c>ts_tree_get_changed_ranges</c>, <c>ts_node_string</c>) with tree-sitter's own allocator.</summary>
    internal static void FreeOwned(void* p)
    {
        if (p is null) return;
        if (_currentFree != IntPtr.Zero)
        {
            var fn = (delegate* unmanaged[Cdecl]<void*, void>)Marshal.ReadIntPtr(_currentFree);
            fn(p);
        }
        else LibC.free(p);
    }

    /// <summary>
    /// Bytes in use on the C heap of the whole process (glibc <c>mallinfo2</c>: all arenas + mmapped chunks), or -1 off glibc. Process-wide,
    /// so only meaningful as a before/after difference around a quiet measurement (benchmarks).
    /// </summary>
    public static long ProcessNativeHeapBytes()
    {
        if (!OperatingSystem.IsLinux()) return -1;
        try
        {
            var m = LibC.mallinfo2();
            return (long)m.Uordblks + (long)m.Hblkhd;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return -1;
        }
    }

    /// <summary>Returns free C-heap pages to the OS (benchmarks only, so before/after numbers are not hidden by the allocator's caches).</summary>
    public static void TrimNativeHeap()
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            LibC.malloc_trim(0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }
}

/// <summary>A cached listing of <c>/nix/store</c> (≈ 100k entries on this machine; listed once, on whatever lane first needs it).</summary>
internal static class NixStore
{
    private static string[]? _entries;
    private static readonly object Gate = new();

    public const string Root = "/nix/store";

    public static string[] Entries()
    {
        lock (Gate)
        {
            if (_entries is not null) return _entries;
            try
            {
                _entries = Directory.Exists(Root)
                    ? Directory.EnumerateDirectories(Root).Where(d => d.Contains("tree-sitter", StringComparison.Ordinal) || d.Contains("treesitter", StringComparison.Ordinal)).ToArray()
                    : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _entries = [];
            }
            return _entries;
        }
    }
}
