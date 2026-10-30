namespace CsCode.Profiling.Testing;

/// <summary>A process of the tree under this one, with its resident set.</summary>
public sealed record ProcessTreeEntry(int Pid, int ParentPid, string Name, long ResidentBytes)
{
    /// <summary>
    /// A readable label: the program and, for interpreters (node, python, dotnet), the script it runs — node renames its main thread, so
    /// <see cref="Name"/> (the kernel's comm) says "MainThread" for every node process.
    /// </summary>
    public string Label => ProcessTree.Label(Pid) ?? Name;
}

/// <summary>
/// The processes below a pid (Linux <c>/proc</c>: every process's <c>stat</c> parent field; macOS: libproc), with their resident sets — so a budget can count the
/// language servers, debug adapters, debuggees, shells and remote servers the app started, including their own children (tsserver under
/// typescript-language-server). Read-only: never signals anything.
/// </summary>
public static class ProcessTree
{
    public static IReadOnlyList<ProcessTreeEntry> Descendants(int rootPid)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return [];
        var parents = new Dictionary<int, (int Parent, string Name)>();
        if (OperatingSystem.IsMacOS())
        {
            foreach (var pid in MacProcesses.AllPids())
                if (MacProcesses.Info(pid) is { } info) parents[pid] = info;
        }
        else foreach (var dir in Directory.EnumerateDirectories("/proc"))
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

    internal static string? Label(int pid)
    {
        try
        {
            var args = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (args.Length == 0) return null;
            var program = Path.GetFileName(args[0]);
            var script = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
            return program is "node" or "python3" or "python" or "dotnet" && script is not null ? program + " " + Path.GetFileName(script) : program;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>VmRSS of a process (bytes; macOS: libproc's resident size), 0 when it is gone or a zombie.</summary>
    public static long Resident(int pid)
    {
        if (OperatingSystem.IsMacOS()) return MacProcesses.Resident(pid);
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
