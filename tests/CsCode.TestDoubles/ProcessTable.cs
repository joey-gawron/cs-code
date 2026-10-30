using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CsCode.TestDoubles;

/// <summary>
/// Process-table helpers for "it runs there" and "nothing is left running" assertions: command lines, parents and descendants (Linux /proc;
/// macOS ps), environments (Linux /proc only; empty elsewhere).
/// </summary>
public static class ProcessTable
{
    public static bool IsAlive(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try { return !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z ", StringComparison.Ordinal); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        if (OperatingSystem.IsMacOS()) return Ps("-o", "pid=,stat=", "-p", pid.ToString(CultureInfo.InvariantCulture)) is [var r] && !r.Stat.StartsWith('Z');
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Live processes whose environment contains <c>variable=value</c> (the adapter server's marker reaches everything it spawned): Linux /proc;
    /// macOS <c>ps -E</c>, which shows the environment of this user's processes except Apple's own binaries.
    /// </summary>
    public static List<int> WithEnvironment(string variable, string value)
    {
        if (OperatingSystem.IsMacOS())
        {
            var entry = $" {variable}={value}";
            return Ps("-A", "-E", "-ww", "-o", "pid=,stat=,command=")
                .Where(r => r.Pid != Environment.ProcessId && !r.Stat.StartsWith('Z') && (r.Tail.EndsWith(entry, StringComparison.Ordinal) || r.Tail.Contains(entry + " ", StringComparison.Ordinal)))
                .Select(r => r.Pid).ToList();
        }
        var result = new List<int>();
        if (!OperatingSystem.IsLinux()) return result;
        var needle = Encoding.UTF8.GetBytes($"{variable}={value}\0");
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == Environment.ProcessId) continue;
            try
            {
                if (File.ReadAllBytes($"/proc/{pid}/environ").AsSpan().IndexOf(needle) >= 0 && IsAlive(pid)) result.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    /// <summary>PIDs of live processes whose command line contains <paramref name="marker"/> (Linux /proc; macOS ps).</summary>
    public static List<int> WithMarker(string marker)
    {
        if (OperatingSystem.IsMacOS())
            return Ps("-A", "-ww", "-o", "pid=,stat=,command=").Where(r => r.Tail.Contains(marker, StringComparison.Ordinal) && !r.Stat.StartsWith('Z'))
                .Select(r => r.Pid).ToList();
        var pids = new List<int>();
        if (!OperatingSystem.IsLinux()) return pids;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
            try
            {
                if (File.ReadAllText(Path.Combine(dir, "cmdline")).Contains(marker, StringComparison.Ordinal) && IsAlive(pid)) pids.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return pids;
    }

    /// <summary>All live descendants of <paramref name="pid"/> (Linux /proc parent links; macOS ps).</summary>
    public static HashSet<int> Descendants(int pid)
    {
        var result = new HashSet<int>();
        var parents = new Dictionary<int, int>();
        if (OperatingSystem.IsMacOS())
        {
            foreach (var r in Ps("-A", "-o", "pid=,stat=,ppid="))
                if (!r.Stat.StartsWith('Z') && int.TryParse(r.Tail, CultureInfo.InvariantCulture, out var parent)) parents[r.Pid] = parent;
        }
        else if (!OperatingSystem.IsLinux()) return result;
        else foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var p)) continue;
            try
            {
                var stat = File.ReadAllText($"/proc/{p}/stat");
                if (stat.Contains(") Z ", StringComparison.Ordinal)) continue;
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                parents[p] = int.Parse(fields[1], CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException) { }
        }
        var frontier = new Queue<int>([pid]);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var (child, parent) in parents)
                if (parent == current && result.Add(child)) frontier.Enqueue(child);
        }
        return result;
    }

    /// <summary>The parent of a process (Linux /proc; macOS ps), or 0 when it is gone (Linux: <see cref="IOException"/>).</summary>
    public static int ParentOf(int pid)
    {
        if (OperatingSystem.IsMacOS())
            return Ps("-o", "pid=,stat=,ppid=", "-p", pid.ToString(CultureInfo.InvariantCulture)) is [var r]
                && int.TryParse(r.Tail, CultureInfo.InvariantCulture, out var parent) ? parent : 0;
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        return int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1], CultureInfo.InvariantCulture);
    }

    /// <summary>The command line of a process, arguments separated by spaces (Linux /proc; macOS ps), or "?" when it is gone.</summary>
    public static string CommandLine(int pid)
    {
        if (OperatingSystem.IsMacOS())
            return Ps("-ww", "-o", "pid=,stat=,command=", "-p", pid.ToString(CultureInfo.InvariantCulture)) is [var r] ? r.Tail : "?";
        try { return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "?"; }
    }

    /// <summary>Rows of macOS's <c>ps</c> for <c>-o pid=,stat=,…</c>: the pid, the state and the rest of the line (ps itself left out).</summary>
    private static List<(int Pid, string Stat, string Tail)> Ps(params string[] args)
    {
        var psi = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var ps = Process.Start(psi)!;
        var text = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        var rows = new List<(int, string, string)>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var pid) && pid != ps.Id)
                rows.Add((pid, parts[1], parts.Length > 2 ? parts[2].Trim() : ""));
        }
        return rows;
    }
}
