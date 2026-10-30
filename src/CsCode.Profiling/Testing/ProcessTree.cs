namespace CsCode.Profiling.Testing;

/// <summary>A process of the tree under this one, with its resident set.</summary>
public sealed record ProcessTreeEntry(int Pid, int ParentPid, string Name, long ResidentBytes);

/// <summary>
/// The processes below a pid (Linux <c>/proc</c>: every process's <c>stat</c> parent field), with their resident sets — so a budget can count the
/// language servers, debug adapters, debuggees, shells and remote servers the app started, including their own children (tsserver under
/// typescript-language-server). Read-only: never signals anything.
/// </summary>
public static class ProcessTree
{
    public static IReadOnlyList<ProcessTreeEntry> Descendants(int rootPid)
    {
        if (!OperatingSystem.IsLinux()) return [];
        var parents = new Dictionary<int, (int Parent, string Name)>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var open = stat.IndexOf('(', StringComparison.Ordinal);
                var close = stat.LastIndexOf(')');
                var fields = stat[(close + 2)..].Split(' ');
                parents[pid] = (int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture), stat[(open + 1)..close]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException) { }
        }
        var result = new List<ProcessTreeEntry>();
        var frontier = new Queue<int>([rootPid]);
        while (frontier.Count > 0)
        {
            var parent = frontier.Dequeue();
            foreach (var (pid, info) in parents)
            {
                if (info.Parent != parent) continue;
                result.Add(new ProcessTreeEntry(pid, parent, info.Name, Resident(pid)));
                frontier.Enqueue(pid);
            }
        }
        return result;
    }

    /// <summary>VmRSS of a process (bytes), 0 when it is gone or a zombie.</summary>
    public static long Resident(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                    return ProcFile.ParseFirstNumber(System.Text.Encoding.ASCII.GetBytes(line[6..])) * 1024;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return 0;
    }
}
