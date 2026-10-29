using System.Text;
using CsCode.Vim.Tests.Oracle;

namespace CsCode.Vim.Tests.Harness;

/// <summary>
/// One table-driven case from a <c>.vimtest</c> file:
/// <code>
/// === name [nodiff]            # header; tags: nodiff (engine-specific, skip nvim), slow
/// set: sw=4 et                 # Ex setup lines (set: prefixes "set "), run on both sides before typing
/// ex: nnoremap Q dd
/// > foo ‸bar                  # initial buffer, ‸ marks the cursor (default 0:0); \t = tab, trailing ⏎ protects spaces
/// keys: dw                     # Vim key notation, typed one key at a time
/// &lt; foo ‸                    # expected buffer (‸ = expected cursor, optional)
/// mode: n                      # expected mode (default n)
/// reg a c: text\nmore          # expected register: name type(c|l|bN): text
/// mark a: 1:5                  # expected mark (1-based line:col)
/// err: E486                    # expected error code prefix
/// </code>
/// </summary>
public sealed class VimTestCase
{
    public string File { get; init; } = "";
    public string Name { get; init; } = "";
    public int LineNumber { get; init; }
    public HashSet<string> Tags { get; } = [];
    public List<string> Ex { get; } = [];
    public string[] Lines { get; set; } = [""];
    public int Line { get; set; }
    public int Col { get; set; }
    public string Keys { get; set; } = "";
    public string[]? ExpectedLines { get; set; }
    public int? ExpectedLine { get; set; }
    public int? ExpectedCol { get; set; }
    public string ExpectedMode { get; set; } = "n";
    public Dictionary<string, RegSnap> ExpectedRegs { get; } = new();
    public Dictionary<string, (int Line, int Col)> ExpectedMarks { get; } = new();
    public string? ExpectedError { get; set; }

    public string Id => $"{File}:{Name}";
    public bool NoDiff => Tags.Contains("nodiff");
    public OracleInput ToInput() => new(Lines, Line, Col, Keys, Ex);

    public override string ToString() => Id;
}

public static class CaseLoader
{
    public const string CursorMarker = "‸";

    public static string CasesDirectory
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Cases");
            if (Directory.Exists(dir)) return dir;
            // running from the source tree
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tests", "CsCode.Vim.Tests", "Cases"))) d = d.Parent;
            return d is null ? dir : Path.Combine(d.FullName, "tests", "CsCode.Vim.Tests", "Cases");
        }
    }

    /// <summary>The Cases directory inside the SOURCE tree (what blessing rewrites).</summary>
    public static string SourceCasesDirectory
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tests", "CsCode.Vim.Tests", "Cases"))) d = d.Parent;
            return d is null ? CasesDirectory : Path.Combine(d.FullName, "tests", "CsCode.Vim.Tests", "Cases");
        }
    }

    private static readonly Lazy<List<VimTestCase>> AllLazy = new(() => LoadAll(CasesDirectory));
    public static IReadOnlyList<VimTestCase> All => AllLazy.Value;

    public static List<VimTestCase> LoadAll(string dir)
    {
        var list = new List<VimTestCase>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir, "*.vimtest").OrderBy(x => x, StringComparer.Ordinal))
            list.AddRange(Parse(Path.GetFileNameWithoutExtension(f), File.ReadAllLines(f)));
        return list;
    }

    public static string Unescape(string s)
    {
        if (s.EndsWith('⏎')) s = s[..^1];
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var n = s[i + 1];
                if (n == 't') { sb.Append('\t'); i++; continue; }
                if (n == '\\') { sb.Append('\\'); i++; continue; }
                if (n == 'n') { sb.Append('\n'); i++; continue; }
                if (n == 'e') { sb.Append('\u001b'); i++; continue; }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public static string Escape(string s)
    {
        var e = s.Replace("\\", "\\\\").Replace("\t", "\\t");
        return e.EndsWith(' ') || e.EndsWith('\t') ? e + "⏎" : e;
    }

    public static List<VimTestCase> Parse(string file, string[] lines)
    {
        var cases = new List<VimTestCase>();
        VimTestCase? cur = null;
        List<string>? input = null, expected = null;
        var finish = () =>
        {
            if (cur is null) return;
            if (input is { Count: > 0 })
            {
                var all = input.Select(Unescape).ToList();
                var joined = string.Join("\n", all);
                var idx = joined.IndexOf(CursorMarker, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var before = joined[..idx].Split('\n');
                    cur.Line = before.Length - 1;
                    cur.Col = before[^1].Length;
                    joined = joined.Remove(idx, CursorMarker.Length);
                }
                cur.Lines = joined.Split('\n');
            }
            if (expected is { Count: > 0 })
            {
                var all = expected.Select(Unescape).ToList();
                var joined = string.Join("\n", all);
                var idx = joined.IndexOf(CursorMarker, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var before = joined[..idx].Split('\n');
                    cur.ExpectedLine = before.Length - 1;
                    cur.ExpectedCol = before[^1].Length;
                    joined = joined.Remove(idx, CursorMarker.Length);
                }
                cur.ExpectedLines = joined.Split('\n');
            }
            cases.Add(cur);
        };
        for (var n = 0; n < lines.Length; n++)
        {
            var raw = lines[n];
            if (raw.StartsWith("===", StringComparison.Ordinal))
            {
                finish();
                var header = raw[3..].Trim();
                var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                cur = new VimTestCase { File = file, Name = parts.Length > 0 ? parts[0] : $"case{cases.Count}", LineNumber = n + 1 };
                foreach (var t in parts.Skip(1)) cur.Tags.Add(t);
                input = [];
                expected = [];
                continue;
            }
            if (cur is null || raw.StartsWith('#') || raw.Trim().Length == 0 && !raw.StartsWith('>') && !raw.StartsWith('<')) continue;
            if (raw.StartsWith('>')) { input!.Add(raw.Length > 1 && raw[1] == ' ' ? raw[2..] : raw[1..]); continue; }
            if (raw.StartsWith('<')) { expected!.Add(raw.Length > 1 && raw[1] == ' ' ? raw[2..] : raw[1..]); continue; }
            var colon = raw.IndexOf(':');
            if (colon <= 0) continue;
            var key = raw[..colon].Trim();
            var val = raw[(colon + 1)..].TrimStart();
            switch (key)
            {
                case "set": cur.Ex.Add("set " + val); break;
                case "ex": cur.Ex.Add(val); break;
                case "keys": cur.Keys = val; break;
                case "mode": cur.ExpectedMode = val.Trim(); break;
                case "err": cur.ExpectedError = val.Trim(); break;
                default:
                    if (key.StartsWith("reg ", StringComparison.Ordinal))
                    {
                        // reg a c: text   |   reg a b5: text
                        var head = key[4..].Trim().Split(' ', 2);
                        var name = head[0];
                        var type = head.Length > 1 ? head[1] : "c";
                        var text = Unescape(val);
                        var rl = text.Split('\n');
                        cur.ExpectedRegs[name] = type[0] switch
                        {
                            'l' => new RegSnap('l', rl),
                            'b' => new RegSnap('b', rl, int.TryParse(type.AsSpan(1), out var w) ? w : 0),
                            _ => new RegSnap('c', rl),
                        };
                    }
                    else if (key.StartsWith("mark ", StringComparison.Ordinal))
                    {
                        var name = key[5..].Trim();
                        var lc = val.Split(':');
                        cur.ExpectedMarks[name] = (int.Parse(lc[0]) - 1, int.Parse(lc[1]) - 1);
                    }
                    break;
            }
        }
        finish();
        return cases;
    }

    /// <summary>Writes expectations produced by <paramref name="nvim"/> into every case that has none (blessing). Returns how many were written.</summary>
    public static int Bless(string file, Func<VimTestCase, VimSnapshot?> nvim)
    {
        var path = Path.Combine(SourceCasesDirectory, file + ".vimtest");
        var lines = File.ReadAllLines(path).ToList();
        var cases = Parse(file, [.. lines]);
        var written = 0;
        // process bottom-to-top so earlier line numbers stay valid
        foreach (var c in Enumerable.Reverse(cases))
        {
            if (c.ExpectedLines is not null) continue;
            var snap = nvim(c);
            if (snap is null) continue;
            // find the "keys:" line of this case
            var start = c.LineNumber - 1;
            var keysIdx = -1;
            for (var i = start + 1; i < lines.Count && !lines[i].StartsWith("===", StringComparison.Ordinal); i++)
                if (lines[i].StartsWith("keys:", StringComparison.Ordinal)) keysIdx = i;
            if (keysIdx < 0) continue;
            var outLines = new List<string>();
            for (var i = 0; i < snap.Lines.Length; i++)
            {
                var t = snap.Lines[i];
                if (i == snap.Line) t = t.Insert(Math.Min(snap.Col, t.Length), CursorMarker);
                outLines.Add("< " + Escape(t).Replace($"{CursorMarker}⏎", CursorMarker + "⏎"));
            }
            if (snap.Mode != "n") outLines.Add("mode: " + snap.Mode);
            lines.InsertRange(keysIdx + 1, outLines);
            written++;
        }
        if (written > 0) File.WriteAllLines(path, lines);
        return written;
    }
}
