using System.Collections.Concurrent;
using System.Text;
using CsCode.Vim.Tests.Oracle;

namespace CsCode.Vim.Tests.Harness;

/// <summary>Runs cases through real Neovim and the engine (a bounded number of nvim processes at a time) and reports divergences.</summary>
public static class Differential
{
    public sealed record Result(VimTestCase Case, string Diff, string? Error);

    public static IReadOnlyDictionary<string, string> KnownDivergences { get; } = LoadKnown();

    private static Dictionary<string, string> LoadKnown()
    {
        var d = new Dictionary<string, string>();
        var path = Path.Combine(CaseLoader.CasesDirectory, "known-divergences.txt");
        if (!File.Exists(path)) return d;
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            var bar = line.IndexOf('|');
            d[(bar < 0 ? line : line[..bar]).Trim()] = bar < 0 ? "" : line[(bar + 1)..].Trim();
        }
        return d;
    }

    public static Result RunOne(VimTestCase c)
    {
        var input = c.ToInput();
        try
        {
            using var client = NvimOracle.Start(input, out var initial);
            NvimOracle.Type(client, input.Keys);
            var nv = NvimOracle.Snapshot(client);
            VimSnapshot mine;
            try { mine = EngineRunner.Run(input); }
            catch (Exception ex) { return new Result(c, "engine threw: " + ex.Message + "\n" + ex.StackTrace, null); }
            return new Result(c, EngineRunner.Diff(nv, mine, initial), null);
        }
        catch (Exception ex)
        {
            return new Result(c, "", ex.Message);
        }
    }

    public static List<Result> RunAll(IEnumerable<VimTestCase> cases, int degree = 4)
    {
        var results = new ConcurrentBag<Result>();
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = degree }, c => results.Add(RunOne(c)));
        return [.. results.OrderBy(r => r.Case.Id, StringComparer.Ordinal)];
    }

    public static string Report(IReadOnlyList<Result> results, int total)
    {
        var sb = new StringBuilder();
        var bad = results.Where(r => r.Diff.Length > 0 || r.Error is not null).ToList();
        var explained = bad.Where(r => KnownDivergences.ContainsKey(r.Case.Id)).ToList();
        var unexplained = bad.Except(explained).ToList();
        sb.AppendLine($"differential: {results.Count} cases run through nvim, {bad.Count} diverged, {explained.Count} explained, {unexplained.Count} unexplained");
        foreach (var r in unexplained)
        {
            sb.AppendLine($"--- {r.Case.Id}  keys: {r.Case.Keys}");
            sb.AppendLine("    buffer: " + string.Join("\\n", r.Case.Lines.Select((l, i) => i == r.Case.Line ? l.Insert(Math.Min(r.Case.Col, l.Length), "‸") : l)));
            if (r.Case.Ex.Count > 0) sb.AppendLine("    ex: " + string.Join(" | ", r.Case.Ex));
            if (r.Error is not null) sb.AppendLine("    nvim error: " + r.Error);
            foreach (var l in r.Diff.TrimEnd().Split('\n')) if (l.Length > 0) sb.AppendLine("    " + l);
        }
        _ = total;
        return sb.ToString();
    }
}
