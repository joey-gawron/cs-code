using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsCode.Core.Platform;
using CsCode.Core.Text;

namespace CsCode.Editor.Documents;

/// <summary>The subset of .editorconfig properties the editor honours (null = not specified / unset).</summary>
public sealed record EditorConfigProperties(
    string? IndentStyle = null,
    int? IndentSize = null,
    int? TabWidth = null,
    string? EndOfLine = null,
    string? Charset = null,
    bool? TrimTrailingWhitespace = null,
    bool? InsertFinalNewline = null)
{
    public static readonly EditorConfigProperties None = new();

    /// <summary>Effective indent width: <c>indent_size</c> (or <c>tab_width</c> when it is "tab"), else <c>tab_width</c>.</summary>
    public int? EffectiveIndentSize => IndentSize ?? TabWidth;

    /// <summary>"\n", "\r\n" or "\r" for end_of_line = lf|crlf|cr, else null.</summary>
    public string? EolString => EndOfLine switch { "lf" => "\n", "crlf" => "\r\n", "cr" => "\r", _ => null };
}

/// <summary>Resolves editorconfig properties for a file. Optional seam: the Platform module may ship its own implementation.</summary>
public interface IEditorConfigSource
{
    ValueTask<EditorConfigProperties> ResolveAsync(Uri file, CancellationToken ct = default);
}

/// <summary>
/// Reads .editorconfig files through <see cref="IFileSystem"/> (walks up to the nearest <c>root = true</c>), applies the spec's
/// glob rules (<c>*</c> <c>**</c> <c>?</c> <c>[seq]</c> <c>{a,b}</c> <c>{1..3}</c>; patterns without '/' match at any depth) and merges
/// nearer files / later sections over earlier ones. Parsed files are cached per directory (bounded) and re-validated by stat.
/// </summary>
public sealed class EditorConfigResolver(IFileSystem fs) : IEditorConfigSource
{
    private const int MaxCachedFiles = 64;

    internal sealed record Section(GlobRegex Pattern, IReadOnlyDictionary<string, string> Values);
    internal sealed record Parsed(DateTimeOffset Modified, long Size, bool IsRoot, IReadOnlyList<Section> Sections);

    private readonly object _gate = new();
    private readonly Dictionary<string, Parsed?> _cache = new(StringComparer.Ordinal);

    public async ValueTask<EditorConfigProperties> ResolveAsync(Uri file, CancellationToken ct = default)
    {
        if (!file.IsFile) return EditorConfigProperties.None;
        var path = file.LocalPath;
        var dir = Path.GetDirectoryName(path);
        var chain = new List<(string Dir, Parsed Config)>();
        while (!string.IsNullOrEmpty(dir))
        {
            ct.ThrowIfCancellationRequested();
            var cfg = await LoadAsync(dir, ct).ConfigureAwait(false);
            if (cfg is not null)
            {
                chain.Add((dir, cfg));
                if (cfg.IsRoot) break;
            }
            dir = Path.GetDirectoryName(dir);
        }

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = chain.Count - 1; i >= 0; i--) // farthest first, nearest wins
        {
            var (cfgDir, cfg) = chain[i];
            var relative = Path.GetRelativePath(cfgDir, path).Replace('\\', '/');
            foreach (var section in cfg.Sections)
                if (section.Pattern.IsMatch(relative))
                    foreach (var (k, v) in section.Values) merged[k] = v;
        }
        return ToProperties(merged);
    }

    private async ValueTask<Parsed?> LoadAsync(string dir, CancellationToken ct)
    {
        var configPath = Path.Combine(dir, ".editorconfig");
        var uri = new Uri(configPath);
        var stat = await fs.StatAsync(uri, ct).ConfigureAwait(false);
        if (stat is null || stat.Type == FileType.Directory)
        {
            lock (_gate) _cache.Remove(configPath);
            return null;
        }
        lock (_gate)
            if (_cache.TryGetValue(configPath, out var hit) && hit is not null && hit.Modified == stat.ModifiedUtc && hit.Size == stat.Size)
                return hit;

        string text;
        try
        {
            text = Encoding.UTF8.GetString(await fs.ReadFileAsync(uri, ct).ConfigureAwait(false));
        }
        catch (IOException)
        {
            return null;
        }
        var parsed = Parse(text, stat);
        lock (_gate)
        {
            if (_cache.Count >= MaxCachedFiles) _cache.Clear(); // bounded: a workspace has few distinct .editorconfig files
            _cache[configPath] = parsed;
        }
        return parsed;
    }

    internal static Parsed Parse(string text, FileStat stat)
    {
        var sections = new List<Section>();
        var isRoot = false;
        GlobRegex? currentPattern = null;
        Dictionary<string, string>? currentValues = null;

        void Close()
        {
            if (currentPattern is not null && currentValues is not null) sections.Add(new Section(currentPattern, currentValues));
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                Close();
                currentValues = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    currentPattern = GlobRegex.Compile(GlobToRegex(line[1..^1])); // T38: linear, no ~150 KB NonBacktracking cache (see Core Glob)
                }
                catch (ArgumentException)
                {
                    currentPattern = GlobRegex.Never; // an invalid glob matches nothing
                }
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..].Trim();
            var hash = value.IndexOfAny([' ', '\t']);
            if (hash >= 0 && value[(hash + 1)..].TrimStart() is ['#' or ';', ..]) value = value[..hash].Trim(); // inline comment
            if (currentValues is null)
            {
                if (key == "root") isRoot = value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            else currentValues[key] = value.ToLowerInvariant();
        }
        Close();
        return new Parsed(stat.ModifiedUtc, stat.Size, isRoot, sections);
    }

    private static EditorConfigProperties ToProperties(Dictionary<string, string> v)
    {
        string? Get(string k) => v.TryGetValue(k, out var s) && s != "unset" ? s : null;
        int? Int(string k) => Get(k) is { } s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
        bool? Bool(string k) => Get(k) switch { "true" => true, "false" => false, _ => null };

        var tabWidth = Int("tab_width");
        var indentSizeRaw = Get("indent_size");
        int? indentSize = indentSizeRaw == "tab" ? tabWidth : Int("indent_size");
        return new EditorConfigProperties(
            IndentStyle: Get("indent_style"),
            IndentSize: indentSize,
            TabWidth: tabWidth,
            EndOfLine: Get("end_of_line"),
            Charset: Get("charset"),
            TrimTrailingWhitespace: Bool("trim_trailing_whitespace"),
            InsertFinalNewline: Bool("insert_final_newline"));
    }

    /// <summary>EditorConfig glob → anchored regex over a forward-slash path relative to the config directory.</summary>
    internal static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        var hasSlash = glob.Contains('/');
        // Patterns containing '/' (a leading '/' is just stripped) are anchored at the .editorconfig directory;
        // basename-only patterns match at any depth below it.
        if (glob.StartsWith('/')) glob = glob[1..];
        else if (!hasSlash) sb.Append("(?:.*/)?");

        var braces = new Stack<int>();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        sb.Append(".*");
                        i++;
                    }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '[':
                {
                    var end = glob.IndexOf(']', i + 1);
                    if (end < 0)
                    {
                        sb.Append(@"\[");
                        break;
                    }
                    var body = glob[(i + 1)..end];
                    if (body.StartsWith('!')) body = "^" + body[1..];
                    sb.Append('[').Append(body.Replace(@"\", @"\\")).Append(']');
                    i = end;
                    break;
                }
                case '{':
                {
                    var end = glob.IndexOf('}', i + 1);
                    var body = end < 0 ? null : glob[(i + 1)..end];
                    if (body is not null && Regex.Match(body, @"^(-?\d+)\.\.(-?\d+)$") is { Success: true } range
                        && int.TryParse(range.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var lo)
                        && int.TryParse(range.Groups[2].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var hi))
                    {
                        // numeric range {1..3}: the first 1000 numbers, as digit classes (one alternative per number was ~9,000 characters for
                        // {10000000..10000999}, above GlobRegex's limit, so the section never matched: T38 follow-up). A number beyond int is no range.
                        if (lo > hi) (lo, hi) = (hi, lo);
                        sb.Append(GlobRegex.IntegerRange(lo, (int)Math.Min(hi, lo + 999L)));
                        i = end;
                    }
                    else if (body is not null && body.Contains(','))
                    {
                        braces.Push(sb.Length);
                        sb.Append("(?:");
                    }
                    else sb.Append(@"\{");
                    break;
                }
                case '}':
                    if (braces.Count > 0)
                    {
                        braces.Pop();
                        sb.Append(')');
                    }
                    else sb.Append(@"\}");
                    break;
                case ',':
                    sb.Append(braces.Count > 0 ? "|" : ",");
                    break;
                case '\\' when i + 1 < glob.Length:
                    sb.Append(Regex.Escape(glob[++i].ToString()));
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        while (braces.Count > 0)
        {
            braces.Pop();
            sb.Append(')');
        }
        return sb.Append('$').ToString();
    }
}
