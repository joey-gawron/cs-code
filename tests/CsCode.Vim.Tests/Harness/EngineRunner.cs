using System.Text;
using CsCode.Core.Text;
using CsCode.Vim.Tests.Oracle;

namespace CsCode.Vim.Tests.Harness;

/// <summary>Runs a scenario through CsCode.Vim (typing the keys one by one, exactly like a user) and snapshots the result in the oracle's shape.</summary>
public static class EngineRunner
{
    private const string RegisterNames = "\"0123456789-abcdefghijklmnopqrstuvwxyz.:/";
    private const string MarkNames = "abcdefghijklmnopqrstuvwxyz[]<>.^'";

    public sealed class Session : IDisposable
    {
        public InMemoryVimHost Host { get; }
        public VimEngine Engine { get; }
        public VimSession VimSession { get; }

        public Session(OracleInput input, VimSession? shared = null)
        {
            Host = new InMemoryVimHost(string.Join("\n", input.Lines));
            Host.Caret = new TextPosition(input.Line, input.Col);
            VimSession = shared ?? new VimSession();
            Engine = new VimEngine(Host, VimSession);
            foreach (var ex in input.Ex) Engine.ExecuteEx(ex);
        }

        public void Dispose() => Engine.Dispose();
    }

    public static VimSnapshot Run(OracleInput input)
    {
        using var s = new Session(input);
        s.Engine.TypeKeys(input.Keys);
        return Snapshot(s.Engine, s.Host);
    }

    public static VimSnapshot Snapshot(VimEngine engine, InMemoryVimHost host)
    {
        var buf = host.Buffer;
        var lines = new string[buf.LineCount];
        for (var i = 0; i < lines.Length; i++) lines[i] = buf.GetLine(i);
        var snap = new VimSnapshot
        {
            Lines = lines,
            Line = engine.Cursor.Line,
            Col = engine.Cursor.Character,
            Mode = engine.ModeCode,
            Error = engine.LastError ?? "",
        };
        var regs = engine.Session.Registers;
        foreach (var name in RegisterNames)
        {
            var r = regs.ForRead(name);
            if (r is null || r.IsEmpty) continue;
            snap.Regs[name.ToString()] = new RegSnap(r.Type switch { VimRegisterType.Line => 'l', VimRegisterType.Block => 'b', _ => 'c' }, r.Lines, r.Type == VimRegisterType.Block ? r.Width : 0);
        }
        foreach (var m in MarkNames)
            if (engine.GetMarkPosition(m) is { } p) snap.Marks[m.ToString()] = (p.Line, p.Character);
        return snap;
    }

    /// <summary>Differences between what Neovim did and what the engine did. Marks are compared relative to the initial state, errors by their E-code.</summary>
    public static string Diff(VimSnapshot nvim, VimSnapshot mine, VimSnapshot? nvimInitial = null, bool compareRegs = true, bool compareMarks = true)
    {
        var d = new StringBuilder();
        if (!nvim.Lines.AsSpan().SequenceEqual(mine.Lines))
            d.AppendLine($"text: nvim=[{string.Join("\\n", nvim.Lines)}] mine=[{string.Join("\\n", mine.Lines)}]");
        if (nvim.Line != mine.Line || nvim.Col != mine.Col) d.AppendLine($"cursor: nvim={nvim.Line}:{nvim.Col} mine={mine.Line}:{mine.Col}");
        if (nvim.Mode != mine.Mode) d.AppendLine($"mode: nvim={nvim.Mode} mine={mine.Mode}");
        if (compareRegs)
        {
            foreach (var k in nvim.Regs.Keys.Union(mine.Regs.Keys).OrderBy(x => x))
            {
                nvim.Regs.TryGetValue(k, out var a);
                mine.Regs.TryGetValue(k, out var b);
                if (a is null && b is null) continue;
                if (k is "/" or ":" or ".")
                {
                    if (a is null || b is null || !a.Lines.AsSpan().SequenceEqual(b.Lines)) d.AppendLine($"reg {k}: nvim={a?.ToString() ?? "-"} mine={b?.ToString() ?? "-"}");
                    continue;
                }
                if (a is null || !a.Same(b)) d.AppendLine($"reg {k}: nvim={a?.ToString() ?? "-"} mine={b?.ToString() ?? "-"}");
            }
        }
        if (compareMarks)
        {
            Dictionary<string, (int, int)> Norm(VimSnapshot s)
            {
                var r = new Dictionary<string, (int, int)>();
                foreach (var (k, v) in s.Marks)
                {
                    if (nvimInitial is not null && nvimInitial.Marks.TryGetValue(k, out var iv) && iv == v) continue;
                    r[k] = v;
                }
                return r;
            }
            var na = Norm(nvim);
            var nb = Norm(mine);
            foreach (var k in na.Keys.Union(nb.Keys).OrderBy(x => x))
            {
                var has1 = na.TryGetValue(k, out var v1);
                var has2 = nb.TryGetValue(k, out var v2);
                if (has1 != has2 || v1 != v2) d.AppendLine($"mark {k}: nvim={(has1 ? $"{v1.Item1}:{v1.Item2}" : "-")} mine={(has2 ? $"{v2.Item1}:{v2.Item2}" : "-")}");
            }
        }
        var e1 = ErrCode(nvim.Error);
        var e2 = ErrCode(mine.Error);
        if (e1 != e2 && e1.Length > 0) d.AppendLine($"error: nvim={nvim.Error} mine={mine.Error}");
        return d.ToString();
    }

    private static string ErrCode(string e)
    {
        var i = e.IndexOf(':');
        return e.StartsWith('E') && i > 0 ? e[..i] : "";
    }
}
