using System.Text;

namespace CsCode.Vim;

/// <summary>A register: its text as lines (never null) plus how it was yanked. Block registers remember their width.</summary>
public sealed class Register
{
    public string[] Lines = [];
    public VimRegisterType Type;
    public int Width;

    public bool IsEmpty => Lines.Length == 0;
    public int TotalChars { get { var n = 0; foreach (var l in Lines) n += l.Length + 1; return n; } }

    public void Set(string[] lines, VimRegisterType type, int width = 0)
    {
        Lines = lines;
        Type = type;
        Width = width;
    }

    public void CopyFrom(Register r) => Set(r.Lines, r.Type, r.Width);

    /// <summary>Text as Vim shows it: lines joined by '\n'; linewise registers end with a newline.</summary>
    public string ToText()
    {
        if (Lines.Length == 0) return "";
        if (Lines.Length == 1) return Type == VimRegisterType.Line ? Lines[0] + "\n" : Lines[0];
        var sb = new StringBuilder();
        for (var i = 0; i < Lines.Length; i++)
        {
            sb.Append(Lines[i]);
            if (i < Lines.Length - 1 || Type == VimRegisterType.Line) sb.Append('\n');
        }
        return sb.ToString();
    }

    public static Register FromText(string text, VimRegisterType? forced = null)
    {
        var r = new Register();
        var type = forced ?? (text.EndsWith('\n') ? VimRegisterType.Line : VimRegisterType.Character);
        var body = type == VimRegisterType.Line && text.EndsWith('\n') ? text[..^1] : text;
        r.Set(body.Split('\n'), type);
        if (type == VimRegisterType.Block) r.Width = r.Lines.Max(l => VimText.DisplayWidth(l, 8));
        return r;
    }
}

/// <summary>
/// All registers of one session: "" (unnamed pointer), 0, 1-9 (delete ring), a-z (A-Z append), - (small delete), _ black hole,
/// read-only . : / % #, and + * (system clipboard through the host). Follows ops.c semantics for numbered-register shifting.
/// </summary>
public sealed class RegisterFile
{
    private const int Small = 36;
    private readonly Register[] _regs = Enumerable.Range(0, 37).Select(_ => new Register()).ToArray();
    private Register? _previous;

    /// <summary>The last text typed in Insert mode (register '.'), stored in key-encoded form.</summary>
    public string? LastInserted { get; set; }
    public string LastCommandLine { get; set; } = "";
    public string LastSearch { get; set; } = "";
    public Func<string?>? CurrentFileName { get; set; }
    public Func<string?>? AlternateFileName { get; set; }

    /// <summary>True for every register name the engine accepts after '"'.</summary>
    public static bool IsValidName(int c) =>
        c is '"' or '-' or '_' or '.' or ':' or '/' or '%' or '#' or '+' or '*' or '=' || (c is >= '0' and <= '9') ||
        (c is >= 'a' and <= 'z') || (c is >= 'A' and <= 'Z');

    private static int Index(int name) => name switch
    {
        >= '0' and <= '9' => name - '0',
        >= 'a' and <= 'z' => name - 'a' + 10,
        >= 'A' and <= 'Z' => name - 'A' + 10,
        '-' => Small,
        _ => 0,
    };

    /// <summary>Register to READ for <paramref name="name"/> (0 or '"' = unnamed). Null when the register does not exist/is empty.</summary>
    public Register? ForRead(int name, Func<string?>? clipboard = null)
    {
        switch (name)
        {
            case 0 or '"': return _previous ?? _regs[0];
            case '.': return LastInserted is null ? null : Register.FromText(LastInserted, VimRegisterType.Character);
            case ':': return LastCommandLine.Length == 0 ? null : Register.FromText(LastCommandLine, VimRegisterType.Character);
            case '/': return LastSearch.Length == 0 ? null : Register.FromText(LastSearch, VimRegisterType.Character);
            case '%': return CurrentFileName?.Invoke() is { } f ? Register.FromText(f, VimRegisterType.Character) : null;
            case '#': return AlternateFileName?.Invoke() is { } a ? Register.FromText(a, VimRegisterType.Character) : null;
            case '+' or '*':
            {
                var t = clipboard?.Invoke();
                return string.IsNullOrEmpty(t) ? _previous ?? _regs[0] : Register.FromText(t);
            }
            case '_': return null;
            default:
                return IsValidName(name) ? _regs[Index(name)] : null;
        }
    }

    /// <summary>
    /// As <see cref="ForRead(int, Func{string?}?)"/>, with the clipboard registers read by name: <paramref name="clipboard"/> gets <c>'+'</c> or
    /// <c>'*'</c> and returns that register as the host has it (typed when the host knows how it was copied), or null when it is empty (the
    /// unnamed register is read instead).
    /// </summary>
    public Register? ForRead(int name, Func<char, Register?> clipboard) =>
        name is '+' or '*' ? clipboard((char)name) is { IsEmpty: false } r ? r : _previous ?? _regs[0] : ForRead(name);

    /// <summary>
    /// Register to WRITE for <paramref name="name"/>; remembers it as the target of the unnamed register.
    /// <paramref name="append"/> is true for A-Z. Returns null for the black hole.
    /// </summary>
    public Register? ForWrite(int name, out bool append, bool updatePrevious = true)
    {
        append = name is >= 'A' and <= 'Z';
        if (name == '_') return null;
        var r = name is 0 or '"' or '+' or '*' ? _regs[0] : _regs[Index(name)];
        if (updatePrevious) _previous = r;
        return r;
    }

    /// <summary>Stores into <paramref name="target"/>, appending with Vim's rules when <paramref name="append"/>.</summary>
    public static void Store(Register target, string[] lines, VimRegisterType type, int width, bool append)
    {
        if (!append || target.IsEmpty)
        {
            target.Set(lines, type, width);
            return;
        }
        // Appending: a linewise yank (or register) makes the result linewise; charwise joins last+first line.
        var newType = type == VimRegisterType.Line ? VimRegisterType.Line : target.Type;
        var list = new List<string>(target.Lines);
        if (target.Type == VimRegisterType.Character && type != VimRegisterType.Line)
        {
            list[^1] += lines[0];
            list.AddRange(lines.Skip(1));
        }
        else list.AddRange(lines);
        target.Set([.. list], newType, Math.Max(target.Width, width));
    }

    /// <summary>1 -> 2 -> ... -> 9 ring shift done before a multi-line delete lands in register 1.</summary>
    public Register ShiftDeleteRegisters(bool keepPrevious = false)
    {
        var nine = _regs[9];
        for (var n = 9; n > 1; n--) _regs[n] = _regs[n - 1];
        _regs[1] = nine;
        nine.Set([], VimRegisterType.Character);
        if (!keepPrevious) _previous = _regs[1];
        return _regs[1];
    }

    /// <summary>The small-delete register '-'.</summary>
    public Register SmallDelete()
    {
        _previous = _regs[Small];
        return _regs[Small];
    }

    /// <summary>Register 0 (last yank) — written by yanks without a register name.</summary>
    public Register Yank0()
    {
        _previous = _regs[0];
        return _regs[0];
    }

    public Register Named(char c) => _regs[Index(c)];

    public void Clear()
    {
        foreach (var r in _regs) r.Set([], VimRegisterType.Character);
        _previous = null;
    }

    // ---- persistence (vim.persistRegisters): a-z only, capped ----

    public IReadOnlyDictionary<string, (char Type, string Text, int Width)> Export(int maxCharsPerRegister = 65536, int maxTotalChars = 1 << 20)
    {
        var d = new Dictionary<string, (char, string, int)>();
        var total = 0;
        for (var c = 'a'; c <= 'z'; c++)
        {
            var r = _regs[Index(c)];
            if (r.IsEmpty) continue;
            var text = string.Join('\n', r.Lines);
            if (text.Length > maxCharsPerRegister || total + text.Length > maxTotalChars) continue;
            total += text.Length;
            d[c.ToString()] = (r.Type switch { VimRegisterType.Line => 'l', VimRegisterType.Block => 'b', _ => 'c' }, text, r.Width);
        }
        return d;
    }

    public void Import(IReadOnlyDictionary<string, (char Type, string Text, int Width)> data)
    {
        foreach (var (k, v) in data)
        {
            if (k.Length != 1 || k[0] is < 'a' or > 'z') continue;
            var type = v.Type switch { 'l' => VimRegisterType.Line, 'b' => VimRegisterType.Block, _ => VimRegisterType.Character };
            _regs[Index(k[0])].Set(v.Text.Split('\n'), type, v.Width);
        }
    }

    /// <summary>Formatted like :registers.</summary>
    public string Describe(string? only = null)
    {
        var sb = new StringBuilder("Type Name Content");
        void Row(string name, Register? r)
        {
            if (r is null || r.IsEmpty) return;
            if (only is { Length: > 0 } && !only.Contains(name[0])) return;
            var t = r.Type switch { VimRegisterType.Line => "l", VimRegisterType.Block => "b", _ => "c" };
            var text = Display(r.ToText());
            sb.Append("\n  ").Append(t).Append("  \"").Append(name).Append("   ").Append(text.Length > 150 ? text[..150] : text);
        }
        Row("\"", _previous ?? _regs[0]);
        for (var i = 0; i <= 9; i++) Row(i.ToString(), _regs[i]);
        for (var c = 'a'; c <= 'z'; c++) Row(c.ToString(), _regs[Index(c)]);
        Row("-", _regs[Small]);
        if (!string.IsNullOrEmpty(LastInserted)) Row(".", Register.FromText(LastInserted, VimRegisterType.Character));
        if (LastCommandLine.Length > 0) Row(":", Register.FromText(LastCommandLine, VimRegisterType.Character));
        if (LastSearch.Length > 0) Row("/", Register.FromText(LastSearch, VimRegisterType.Character));
        return sb.ToString();
    }

    private static string Display(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (c == '\n') sb.Append("^J");
            else if (c < 32) sb.Append('^').Append((char)(c + 64));
            else if (c == Keys.KSpecial) sb.Append("<80>");
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
