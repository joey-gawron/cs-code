using System.Globalization;

namespace CsCode.Profiling;

/// <summary>
/// The descendants of a process (Linux), for <see cref="MemoryMonitor"/>'s tree accounting (lsp-core-4). Reads each thread's
/// <c>/proc/&lt;pid&gt;/task/&lt;tid&gt;/children</c> (a few small files per process in the tree); on a kernel without that file
/// (CONFIG_PROC_CHILDREN) it falls back to the parent field of every <c>/proc/&lt;pid&gt;/stat</c>. Read-only, never signals anything.
/// </summary>
internal static class ProcessTreeScanner
{
    private static readonly bool HasChildrenFiles =
        OperatingSystem.IsLinux() && File.Exists($"/proc/{Environment.ProcessId}/task/{Environment.ProcessId}/children");

    /// <summary>Breadth-first descendants of <paramref name="root"/> (not the root), at most <paramref name="max"/>; a pid in <paramref name="stop"/> is
    /// neither listed nor descended into.</summary>
    public static List<int> Descendants(int root, IReadOnlySet<int> stop, int max)
    {
        var result = new List<int>();
        if (!OperatingSystem.IsLinux()) return result;
        var parents = HasChildrenFiles ? null : ParentMap();
        var seen = new HashSet<int> { root };
        var queue = new Queue<int>();
        queue.Enqueue(root);
        while (queue.Count > 0 && result.Count < max)
        {
            var pid = queue.Dequeue();
            foreach (var child in parents is null ? ChildrenOf(pid) : parents.Where(kv => kv.Value == pid).Select(kv => kv.Key))
            {
                if (!seen.Add(child) || stop.Contains(child)) continue;
                result.Add(child);
                queue.Enqueue(child);
                if (result.Count >= max) break;
            }
        }
        return result;
    }

    private static IEnumerable<int> ChildrenOf(int pid)
    {
        string[] tasks;
        try { tasks = Directory.GetDirectories($"/proc/{pid}/task"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var task in tasks)
        {
            string text;
            try { text = File.ReadAllText(Path.Combine(task, "children")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var child)) yield return child;
        }
    }

    private static Dictionary<int, int> ParentMap()
    {
        var parents = new Dictionary<int, int>();
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories("/proc"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return parents; }
        foreach (var dir in dirs)
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var close = stat.LastIndexOf(')');
                var fields = stat[(close + 2)..].Split(' ');
                parents[pid] = int.Parse(fields[1], CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException) { }
        }
        return parents;
    }
}
