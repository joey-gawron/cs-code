using System.Globalization;
using System.Text;

namespace CsCode.Profiling;

/// <summary>Resident memory of one group of mappings (a library, the managed heap, the native heap…).</summary>
public sealed record MemoryRegion(string Name, long ResidentBytes, long PrivateBytes);

/// <summary>
/// Managed-vs-native attribution of the process's resident memory, for the About / diagnostics view (SHOULD). Linux: <c>/proc/self/smaps_rollup</c>
/// gives the totals (resident, proportional, private/shared, anonymous, swap) and <c>/proc/self/smaps</c> the per-mapping figures that are grouped
/// into anonymous memory, managed assemblies (*.dll), native libraries (*.so; Skia/HarfBuzz and ICU on their own) and other files (fonts…). The
/// anonymous part is split into managed (<see cref="ManagedBytes"/>: what the GC committed) and native (<see cref="NativeAnonymousBytes"/>: the rest
/// — malloc'd memory of Skia, HarfBuzz, ICU and the runtime, JIT code, stacks); the GC's committed regions are anonymous mappings that cannot be told
/// apart from the others in smaps, so this split is an estimate. An on-demand capture (a few milliseconds): never on the UI thread, never per
/// sample. Elsewhere only the totals the runtime knows are filled in.
/// </summary>
public sealed record MemoryBreakdown(
    DateTimeOffset Timestamp,
    long ResidentBytes,
    long ProportionalBytes,
    long AnonymousBytes,
    long FileBackedBytes,
    long PrivateBytes,
    long SharedBytes,
    long SwapBytes,
    long GcCommittedBytes,
    long GcHeapBytes,
    IReadOnlyList<MemoryRegion> Regions)
{
    /// <summary>
    /// Anonymous memory that is not the GC's: native allocations (Skia, HarfBuzz, ICU's working data, the runtime's own), JIT code, thread stacks.
    /// Approximation: anonymous resident memory minus what the GC has committed (committed GC memory is resident once touched).
    /// </summary>
    public long NativeAnonymousBytes => Math.Max(0, AnonymousBytes - ManagedBytes);

    /// <summary>The managed heap's share of the anonymous memory: the GC's committed bytes (bounded by the anonymous total).</summary>
    public long ManagedBytes => Math.Min(AnonymousBytes, GcCommittedBytes);

    internal const string Anonymous = "anonymous (managed heap + native heap, JIT code, stacks)";

    public static MemoryBreakdown Capture()
    {
        var info = GC.GetGCMemoryInfo();
        var heap = GC.GetTotalMemory(false);
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/smaps_rollup"))
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            return new MemoryBreakdown(DateTimeOffset.Now, self.WorkingSet64, 0, self.PrivateMemorySize64, 0, self.PrivateMemorySize64, 0, 0,
                info.TotalCommittedBytes, heap, []);
        }
        var rollup = ParseKb(File.ReadAllText("/proc/self/smaps_rollup"));
        long Get(string key) => rollup.TryGetValue(key, out var v) ? v : 0;
        var regions = GroupMappings(File.ReadLines("/proc/self/smaps"));
        return new MemoryBreakdown(DateTimeOffset.Now, Get("Rss"), Get("Pss"), Get("Anonymous"), Get("Rss") - Get("Anonymous"),
            Get("Private_Clean") + Get("Private_Dirty"), Get("Shared_Clean") + Get("Shared_Dirty"), Get("Swap"), info.TotalCommittedBytes, heap, regions);
    }

    /// <summary>Groups smaps mappings (header line + "Key: N kB" lines) into the named regions, largest first.</summary>
    internal static IReadOnlyList<MemoryRegion> GroupMappings(IEnumerable<string> smaps)
    {
        var mappings = new List<(string Path, long Size, long Rss, long Private, long Anonymous)>();
        string? path = null;
        long size = 0, rss = 0, priv = 0, anon = 0;
        void Flush()
        {
            if (path is not null) mappings.Add((path, size, rss, priv, anon));
        }
        foreach (var line in smaps)
        {
            if (line.Length > 0 && IsMappingHeader(line, out var name))
            {
                Flush();
                (path, size, rss, priv, anon) = (name, 0, 0, 0, 0);
                continue;
            }
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || path is null) continue;
            var key = line.AsSpan(0, colon);
            var value = ProcFile.ParseFirstNumber(Encoding.ASCII.GetBytes(line[(colon + 1)..])) * 1024;
            if (value < 0) continue;
            if (key.SequenceEqual("Size")) size = value;
            else if (key.SequenceEqual("Rss")) rss = value;
            else if (key.SequenceEqual("Private_Clean") || key.SequenceEqual("Private_Dirty")) priv += value;
            else if (key.SequenceEqual("Anonymous")) anon = value;
        }
        Flush();

        var groups = new Dictionary<string, (long Rss, long Private)>(StringComparer.Ordinal);
        void Add(string group, long r, long p)
        {
            groups.TryGetValue(group, out var g);
            groups[group] = (g.Rss + r, g.Private + p);
        }
        foreach (var m in mappings)
        {
            if (m.Path.Length == 0 || m.Path is "[heap]" or "[stack]" || m.Path.StartsWith("[anon", StringComparison.Ordinal)) { Add(Anonymous, m.Rss, m.Private); continue; }
            if (m.Path.StartsWith('[')) { Add("kernel ([vdso], [vvar]…)", m.Rss, m.Private); continue; }
            var file = Path.GetFileName(m.Path);
            if (file.Contains("SkiaSharp", StringComparison.OrdinalIgnoreCase) || file.Contains("HarfBuzz", StringComparison.OrdinalIgnoreCase)) Add("Skia / HarfBuzz (" + file + ")", m.Rss, m.Private);
            else if (file.StartsWith("libicu", StringComparison.Ordinal)) Add("ICU (" + file + ")", m.Rss, m.Private);
            else if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) Add("managed assemblies (*.dll)", m.Rss, m.Private);
            else if (file.Contains(".so", StringComparison.Ordinal)) Add("native libraries (*.so)", m.Rss, m.Private);
            else if (m.Anonymous > 0) Add(Anonymous, m.Anonymous, m.Private);
            else Add("other files (fonts, data)", m.Rss, m.Private);
        }
        return groups.Select(g => new MemoryRegion(g.Key, g.Value.Rss, g.Value.Private)).OrderByDescending(r => r.ResidentBytes).ToArray();
    }

    private static bool IsMappingHeader(string line, out string path)
    {
        // "7f12a000-7f12b000 r-xp 00000000 08:01 1234   /usr/lib/x.so" (path optional)
        path = "";
        var dash = line.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || !IsHex(line.AsSpan(0, dash))) return false;
        var parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return false;
        path = parts.Length == 6 ? parts[5].Trim() : "";
        return true;
    }

    private static bool IsHex(ReadOnlySpan<char> s)
    {
        foreach (var c in s) if (!char.IsAsciiHexDigit(c)) return false;
        return s.Length > 0;
    }

    private static Dictionary<string, long> ParseKb(string text)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var value = ProcFile.ParseFirstNumber(Encoding.ASCII.GetBytes(line[(colon + 1)..]));
            if (value >= 0) result[line[..colon]] = value * 1024;
        }
        return result;
    }

    /// <summary>A plain-text table for the diagnostics view or the Memory output channel.</summary>
    public string ToText()
    {
        static string Mb(long b) => (b / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Resident {Mb(ResidentBytes)} (proportional {Mb(ProportionalBytes)}; private {Mb(PrivateBytes)}, shared {Mb(SharedBytes)}; swap {Mb(SwapBytes)})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Anonymous {Mb(AnonymousBytes)} = managed (GC committed) {Mb(ManagedBytes)} + native {Mb(NativeAnonymousBytes)}; GC heap in use {Mb(GcHeapBytes)}; file-backed {Mb(FileBackedBytes)}");
        foreach (var r in Regions) sb.AppendLine(CultureInfo.InvariantCulture, $"  {Mb(r.ResidentBytes),10}  {r.Name}");
        return sb.ToString();
    }
}
