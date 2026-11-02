using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CsCode.Dap.Configuration;

/// <summary>
/// <c>envFile</c> in a launch configuration, for every debugger (language stage 1): the file's variables are merged into the configuration's
/// <c>env</c> before the debug type's provider sees it, as VS Code's language extensions do for their adapters (debugpy itself never reads the
/// file). A value already in <c>env</c> wins over the file's. The file is dotenv: <c>KEY=VALUE</c> lines, <c>export KEY=VALUE</c>, <c>#</c>
/// comments (whole lines, and after an unquoted value), values in double quotes (with <c>\n</c>, <c>\t</c>, <c>\"</c> and <c>\\</c> escapes) or
/// single quotes (literal), and <c>${env:NAME}</c> / <c>${NAME}</c> expanded from the variables above it in the file, then CsCode's environment
/// (an unknown name expands to nothing). Lines that are not assignments are skipped.
/// </summary>
public static partial class EnvFile
{
    /// <summary>The most a file may hold (1 MB): an envFile pointing at something huge must not stall the start.</summary>
    public const int MaxBytes = 1 << 20;

    [GeneratedRegex(@"^\s*(?:export\s+)?(?<key>[A-Za-z_][A-Za-z0-9_.\-]*)\s*=\s*(?<value>.*)$")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"\$\{(?:env:)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex Reference();

    /// <summary>The variables of <paramref name="text"/>, in file order (a later assignment of a name replaces the earlier one).</summary>
    public static IReadOnlyDictionary<string, string> Parse(string text, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimStart();
            if (line.Length == 0 || line[0] == '#') continue;
            var m = Assignment().Match(line);
            if (!m.Success) continue;
            var value = m.Groups["value"].Value.Trim();
            string parsed;
            var expand = true;
            if (value.Length >= 2 && value[0] == '"' && value.LastIndexOf('"') > 0)
                parsed = Unescape(value[1..value.LastIndexOf('"')]);
            else if (value.Length >= 2 && value[0] == '\'' && value.LastIndexOf('\'') > 0)
            {
                parsed = value[1..value.LastIndexOf('\'')];
                expand = false; // single quotes are literal, as in a shell
            }
            else
            {
                var hash = value.IndexOf(" #", StringComparison.Ordinal);
                parsed = (hash >= 0 ? value[..hash] : value).TrimEnd();
            }
            if (expand)
                parsed = Reference().Replace(parsed, r =>
                {
                    var name = r.Groups["name"].Value;
                    return result.TryGetValue(name, out var earlier) ? earlier : environment(name) ?? "";
                });
            result[m.Groups["key"].Value] = parsed;
        }
        return result;
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 == s.Length) { sb.Append(s[i]); continue; }
            var c = s[++i];
            sb.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => c });
        }
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="env"/> (the configuration's, may be absent) with <paramref name="fromFile"/> added under it: a name <paramref name="env"/>
    /// already has keeps its value (a null in <c>env</c> still removes the variable).
    /// </summary>
    public static JsonElement Merge(JsonElement? env, IReadOnlyDictionary<string, string> fromFile)
    {
        var merged = new System.Text.Json.Nodes.JsonObject();
        foreach (var (k, v) in fromFile) merged[k] = v;
        if (env is { ValueKind: JsonValueKind.Object } own)
            foreach (var p in own.EnumerateObject()) merged[p.Name] = System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText());
        return JsonSerializer.SerializeToElement(merged);
    }
}
