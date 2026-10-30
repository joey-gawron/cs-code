namespace CsCode.Terminal.Pty.Unix;

/// <summary>Reads Linux's <c>/proc</c> for the terminal: names of foreground processes and the members of a terminal's session.</summary>
internal static class ProcessTable
{
    /// <summary>The short name of a process (<c>/proc/&lt;pid&gt;/comm</c>), or null.</summary>
    public static string? Name(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            var comm = File.ReadAllText($"/proc/{pid}/comm").TrimEnd('\n');
            return comm.Length > 0 ? comm : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Every process whose session id is <paramref name="sid"/> (empty off Linux).</summary>
    public static IReadOnlyList<int> SessionMembers(int sid)
    {
        var result = new List<int>();
        if (!OperatingSystem.IsLinux() || sid <= 0) return result;
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories("/proc"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return result; }
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (name.Length == 0 || !char.IsAsciiDigit(name[0]) || !int.TryParse(name, out var pid)) continue;
            if (Stat(pid) is { } stat && stat.Session == sid) result.Add(pid);
        }
        return result;
    }

    /// <summary>Fields of <c>/proc/&lt;pid&gt;/stat</c> the terminal needs: state, parent, process group and session.</summary>
    public static (char State, int Parent, int Group, int Session)? Stat(int pid)
    {
        string text;
        try { text = File.ReadAllText($"/proc/{pid}/stat"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        // "pid (comm) S ppid pgrp session ...": comm may contain spaces and parentheses, so parse after the LAST ')'.
        var close = text.LastIndexOf(')');
        if (close < 0 || close + 2 >= text.Length) return null;
        var fields = text[(close + 2)..].Split(' ');
        if (fields.Length < 4 || fields[0].Length == 0) return null;
        return int.TryParse(fields[1], out var ppid) && int.TryParse(fields[2], out var pgrp) && int.TryParse(fields[3], out var session)
            ? (fields[0][0], ppid, pgrp, session)
            : null;
    }
}
