using System.Text;

namespace CsCode.Vim.Tests.Oracle;

/// <summary>A register as seen by a test: type c|l|b, its lines and (block only) width.</summary>
public sealed record RegSnap(char Type, string[] Lines, int Width = 0)
{
    public bool Same(RegSnap? o) => o is not null && o.Type == Type && o.Width == Width && o.Lines.AsSpan().SequenceEqual(Lines);
    public override string ToString() => $"{Type}{(Type == 'b' ? Width : "")}:[{string.Join("\\n", Lines.Select(Escape))}]";
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t");
}

/// <summary>Everything the differential harness compares between the engine and real Neovim.</summary>
public sealed class VimSnapshot
{
    public string[] Lines { get; init; } = [];
    public int Line { get; init; }
    /// <summary>UTF-16 column (Neovim reports bytes; the oracle converts).</summary>
    public int Col { get; init; }
    /// <summary>Normalised: n i R v V ^V no ni c.</summary>
    public string Mode { get; init; } = "n";
    public Dictionary<string, RegSnap> Regs { get; init; } = new();
    public Dictionary<string, (int Line, int Col)> Marks { get; init; } = new();
    public string Error { get; init; } = "";

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"mode={Mode} cursor={Line}:{Col}\n");
        for (var i = 0; i < Lines.Length; i++) sb.Append($"  {i}: {Lines[i].Replace("\t", "\\t")}\n");
        foreach (var (k, v) in Regs.OrderBy(x => x.Key)) sb.Append($"  reg {k} {v}\n");
        foreach (var (k, v) in Marks.OrderBy(x => x.Key)) sb.Append($"  mark {k} {v.Line}:{v.Col}\n");
        if (Error.Length > 0) sb.Append($"  err {Error}\n");
        return sb.ToString();
    }
}

/// <summary>One scenario given to the oracle.</summary>
public sealed record OracleInput(string[] Lines, int Line, int Col, string Keys, IReadOnlyList<string> Ex);

/// <summary>Runs a scenario in a fresh real Neovim (pinned to Vim-like defaults) and reports the resulting state.</summary>
public static class NvimOracle
{
    public static readonly string? NvimPath = NvimClient.FindNvim();
    public static bool Available => NvimPath is not null;

    // Neovim's defaults differ from Vim's in ways that would make every comparison noisy; pin them to Vim's.
    private static readonly string[] Preamble =
    [
        "set nomore noswapfile nobackup noautoindent nosmartindent nocindent startofline",
        "set shiftwidth=8 tabstop=8 softtabstop=0 noexpandtab nosmarttab",
        "set cpo=aABceFs formatoptions=tcq jumpoptions= whichwrap=b,s backspace=indent,eol,start nrformats=bin,hex",
        "set undolevels=1000 matchpairs=(:),{:},[:] comments= commentstring= textwidth=0 nowrap scrolloff=0 sidescrolloff=0 nojoinspaces",
        "set noignorecase nosmartcase wrapscan nohlsearch noincsearch magic selection=inclusive virtualedit= timeout timeoutlen=60000 ttimeoutlen=0",
        "silent! nunmap Y",
        "silent! nunmap &",
        "silent! nunmap <C-L>",
        "silent! nunmap gc",
        "silent! nunmap gcc",
        "silent! ounmap gc",
        "silent! xunmap gc",
        "silent! nunmap <C-W>d",
    ];

    private const string SnapLua = """
        function _G.__snap()
          local m = vim.api.nvim_get_mode()
          local cur = vim.api.nvim_win_get_cursor(0)
          local lines = vim.api.nvim_buf_get_lines(0, 0, -1, false)
          local regs = {}
          for r in ("\"0123456789-abcdefghijklmnopqrstuvwxyz.:/"):gmatch(".") do
            local ok, i = pcall(vim.fn.getreginfo, r)
            if ok and type(i) == "table" and i.regcontents and #i.regcontents > 0 then
              regs[r] = { i.regtype, i.regcontents }
            end
          end
          local marks = {}
          for mk in ("abcdefghijklmnopqrstuvwxyz[]<>.^'"):gmatch(".") do
            local ok, p = pcall(vim.api.nvim_buf_get_mark, 0, mk)
            if ok and type(p) == "table" and p[1] > 0 then marks[mk] = p end
          end
          return { m.mode, cur, lines, regs, marks, vim.v.errmsg }
        end
        function _G.__setup(lines, row, col)
          vim.bo.undolevels = -1
          vim.api.nvim_buf_set_lines(0, 0, -1, false, lines)
          vim.bo.undolevels = 1000
          vim.api.nvim_win_set_cursor(0, { row, col })
          vim.bo.modified = false
          vim.v.errmsg = ""
        end
        """;

    public static string NormalizeMode(string m)
    {
        if (m.StartsWith("no")) return "no";
        if (m.StartsWith("ni")) return "ni";
        if (m.StartsWith('n')) return "n";
        if (m.StartsWith('i')) return "i";
        if (m.StartsWith('R')) return "R";
        if (m == "v") return "v";
        if (m == "V") return "V";
        if (m == "\u0016") return "^V";
        if (m.StartsWith('c')) return "c";
        return m;
    }

    /// <summary>Starts a fresh nvim, sets the buffer up and returns the client (caller disposes) plus the initial snapshot.</summary>
    public static NvimClient Start(OracleInput input, out VimSnapshot initial)
    {
        var client = new NvimClient(NvimPath ?? throw new InvalidOperationException("nvim not available"));
        try
        {
            client.Lua(SnapLua);
            foreach (var cmd in Preamble) client.Command(cmd);
            foreach (var cmd in input.Ex) client.Command(cmd);
            var line = input.Lines[input.Line];
            var bytePrefix = Encoding.UTF8.GetByteCount(line.AsSpan(0, Math.Min(input.Col, line.Length)));
            client.Lua("__setup(...)", input.Lines.Cast<object?>().ToList(), (long)(input.Line + 1), (long)bytePrefix);
            initial = Snapshot(client);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static void Type(NvimClient client, string keys)
    {
        if (keys.Length == 0) return;
        client.Call("nvim_input", keys);
    }

    public static VimSnapshot Run(OracleInput input)
    {
        using var client = Start(input, out _);
        Type(client, input.Keys);
        return Snapshot(client);
    }

    public static VimSnapshot Snapshot(NvimClient client)
    {
        var r = (List<object?>)client.Lua("return __snap()")!;
        var mode = NormalizeMode((string)r[0]!);
        var cur = (List<object?>)r[1]!;
        var lines = ((List<object?>)r[2]!).Select(o => (string)o!).ToArray();
        var row = (int)(long)cur[0]! - 1;
        var byteCol = (int)(long)cur[1]!;
        var snap = new VimSnapshot
        {
            Lines = lines,
            Line = row,
            Col = ByteToChar(lines[row], byteCol),
            Mode = mode,
            Error = (string)(r[5] ?? ""),
        };
        if (r[3] is Dictionary<string, object?> regs)
            foreach (var (name, v) in regs)
            {
                var pair = (List<object?>)v!;
                var type = (string)pair[0]!;
                var content = ((List<object?>)pair[1]!).Select(o => (string)o!).ToArray();
                snap.Regs[name] = type switch
                {
                    "v" => new RegSnap('c', content),
                    "V" => new RegSnap('l', content),
                    _ => new RegSnap('b', content, int.TryParse(type.AsSpan(1), out var w) ? w : 0),
                };
            }
        if (r[4] is Dictionary<string, object?> marks)
            foreach (var (name, v) in marks)
            {
                var p = (List<object?>)v!;
                var ml = (int)(long)p[0]! - 1;
                if (ml >= 0 && ml < lines.Length) snap.Marks[name] = (ml, ByteToChar(lines[ml], (int)(long)p[1]!));
            }
        return snap;
    }

    private static int ByteToChar(string line, int byteCol)
    {
        if (byteCol <= 0) return 0;
        var bytes = Encoding.UTF8.GetBytes(line);
        if (byteCol >= bytes.Length) return line.Length + (byteCol - bytes.Length);
        return Encoding.UTF8.GetString(bytes, 0, byteCol).Length;
    }
}
