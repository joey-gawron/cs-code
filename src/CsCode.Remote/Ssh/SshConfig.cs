namespace CsCode.Remote.Ssh;

/// <summary>Effective ssh settings for one destination, as printed by <c>ssh -G</c> (keys lower case; repeatable keys keep every value).</summary>
public sealed class SshEffectiveConfig
{
    private readonly Dictionary<string, List<string>> _values;

    private SshEffectiveConfig(Dictionary<string, List<string>> values) => _values = values;

    public string? this[string key] => _values.TryGetValue(key.ToLowerInvariant(), out var v) && v.Count > 0 ? v[0] : null;
    public IReadOnlyList<string> All(string key) => _values.TryGetValue(key.ToLowerInvariant(), out var v) ? v : [];
    public IReadOnlyCollection<string> Keys => _values.Keys;

    public string? HostName => this["hostname"];
    public string? User => this["user"];
    public int? Port => int.TryParse(this["port"], out var p) ? p : null;
    public IReadOnlyList<string> ProxyJump => this["proxyjump"] is { } j && j != "none" ? j.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];

    /// <summary>Parses <c>ssh -G</c> output: one <c>key value…</c> per line; blank lines and anything without a value are ignored.</summary>
    public static SshEffectiveConfig Parse(string output)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var sp = line.IndexOfAny([' ', '\t']);
            if (sp <= 0) continue;
            var key = line[..sp].ToLowerInvariant();
            var value = line[(sp + 1)..].Trim();
            if (!values.TryGetValue(key, out var list)) values[key] = list = new List<string>(1);
            list.Add(value);
        }
        return new SshEffectiveConfig(values);
    }
}

/// <summary>
/// Lists the concrete hosts of an ssh_config file for "Remote: Connect to Host…": every <c>Host</c> pattern without wildcards
/// (<c>*</c>, <c>?</c>) or negation (<c>!</c>), in file order, de-duplicated; <c>Include</c> directives are followed (relative paths
/// resolve against the including file's directory, <c>~</c> against the home directory, simple <c>*</c>/<c>?</c> file-name wildcards), at most 16 levels deep.
/// The caller passes the file (the user's <c>~/.ssh/config</c>, or <c>remote.ssh.configFile</c>); this class never decides to read anything itself.
/// </summary>
public static class SshConfigHosts
{
    public static IReadOnlyList<string> List(string configFile, string? home = null)
    {
        var hosts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Walk(configFile, home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), hosts, seen, visited, 0);
        return hosts;
    }

    /// <summary>Same, over text already read (tests, and callers that read through their own file system).</summary>
    public static IReadOnlyList<string> Parse(string text)
    {
        var hosts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (keyword, args) in Lines(text))
            if (keyword == "host") AddHosts(args, hosts, seen);
        return hosts;
    }

    private static void Walk(string file, string home, List<string> hosts, HashSet<string> seen, HashSet<string> visited, int depth)
    {
        if (depth > 16 || !visited.Add(Path.GetFullPath(file)) || !File.Exists(file)) return;
        string text;
        try
        {
            if (new FileInfo(file).Length > 1024 * 1024) return; // not a config file anyone writes by hand
            text = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        var dir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".";
        foreach (var (keyword, args) in Lines(text))
        {
            if (keyword == "host") AddHosts(args, hosts, seen);
            else if (keyword == "include")
                foreach (var pattern in args)
                    foreach (var included in Expand(pattern, dir, home))
                        Walk(included, home, hosts, seen, visited, depth + 1);
        }
    }

    private static void AddHosts(IReadOnlyList<string> patterns, List<string> hosts, HashSet<string> seen)
    {
        foreach (var p in patterns)
            if (p.Length > 0 && p.IndexOfAny(['*', '?', '!']) < 0 && seen.Add(p)) hosts.Add(p);
    }

    private static IEnumerable<string> Expand(string pattern, string dir, string home)
    {
        var p = pattern.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, pattern[2..]) : pattern;
        if (!Path.IsPathRooted(p)) p = Path.Combine(dir, p);
        if (p.IndexOfAny(['*', '?']) < 0) return [p];
        var parent = Path.GetDirectoryName(p);
        var name = Path.GetFileName(p);
        if (parent is null || !Directory.Exists(parent) || parent.IndexOfAny(['*', '?']) >= 0) return [];
        try { return Directory.GetFiles(parent, name).Order(StringComparer.Ordinal).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>ssh_config lines: <c>Keyword args</c> or <c>Keyword=args</c>, comments, double-quoted arguments.</summary>
    private static IEnumerable<(string Keyword, IReadOnlyList<string> Args)> Lines(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var tokens = Tokenize(line);
            if (tokens.Count == 0) continue;
            var keyword = tokens[0];
            var rest = tokens.Skip(1).ToList();
            var eq = keyword.IndexOf('=');
            if (eq > 0)
            {
                if (eq < keyword.Length - 1) rest.Insert(0, keyword[(eq + 1)..]);
                keyword = keyword[..eq];
            }
            else if (rest.Count > 0 && rest[0] == "=") rest.RemoveAt(0);
            else if (rest.Count > 0 && rest[0].StartsWith('=')) rest[0] = rest[0][1..];
            yield return (keyword.ToLowerInvariant(), rest.Where(r => r.Length > 0).ToArray());
        }
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (!quoted && ch == '#') break;
            if (!quoted && char.IsWhiteSpace(ch)) { if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); } continue; }
            cur.Append(ch);
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        return tokens;
    }
}
