using System.Text;
using CsCode.Vim.Tests.Harness;
using CsCode.Vim.Tests.Oracle;
using Xunit;

namespace CsCode.Vim.Tests;

/// <summary>The table-driven suite: every .vimtest case is typed into the engine and compared with its stored expectation.</summary>
public class CaseTests
{
    public static IEnumerable<object[]> Cases() => CaseLoader.All.Select(c => new object[] { c.Id });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Engine_produces_expected_result(string id)
    {
        var c = CaseLoader.All.First(x => x.Id == id);
        Assert.True(c.ExpectedLines is not null, $"{id}: case has no expectation ('<' lines) — bless it with VIMTEST_BLESS=1");
        var snap = EngineRunner.Run(c.ToInput());
        var problems = Check(c, snap);
        Assert.True(problems.Length == 0, $"{id} (keys: {c.Keys})\n{problems}\nactual:\n{snap}");
    }

    public static string Check(VimTestCase c, VimSnapshot snap)
    {
        var sb = new StringBuilder();
        if (c.ExpectedLines is { } el && !el.AsSpan().SequenceEqual(snap.Lines))
            sb.AppendLine($"text: expected [{string.Join("\\n", el)}] actual [{string.Join("\\n", snap.Lines)}]");
        if (c.ExpectedLine is { } l && c.ExpectedCol is { } col && (snap.Line != l || snap.Col != col)) sb.AppendLine($"cursor: expected {l}:{col} actual {snap.Line}:{snap.Col}");
        if (snap.Mode != c.ExpectedMode) sb.AppendLine($"mode: expected {c.ExpectedMode} actual {snap.Mode}");
        foreach (var (k, v) in c.ExpectedRegs)
            if (!snap.Regs.TryGetValue(k, out var a) || !a.Same(v)) sb.AppendLine($"reg {k}: expected {v} actual {(snap.Regs.TryGetValue(k, out var x) ? x.ToString() : "-")}");
        foreach (var (k, v) in c.ExpectedMarks)
            if (!snap.Marks.TryGetValue(k, out var a) || a != v) sb.AppendLine($"mark {k}: expected {v.Line + 1}:{v.Col + 1} actual {(snap.Marks.TryGetValue(k, out var x) ? $"{x.Line + 1}:{x.Col + 1}" : "-")}");
        if (c.ExpectedError is { } err && !snap.Error.StartsWith(err, StringComparison.Ordinal)) sb.AppendLine($"error: expected {err} actual '{snap.Error}'");
        return sb.ToString();
    }

    [Fact]
    public void Case_names_are_unique()
    {
        var dup = CaseLoader.All.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dup.Count == 0, "duplicate case ids: " + string.Join(", ", dup));
    }
}

/// <summary>Differential suite against REAL Neovim. Skips (with a reason) when nvim is not installed.</summary>
public class DifferentialTests
{
    private static void RequireNvim()
    {
        if (!NvimOracle.Available) Assert.Skip("nvim not found (set CSCODE_NVIM or install neovim; tested with v0.12.x)");
    }

    [Fact]
    public void Table_cases_behave_like_neovim()
    {
        RequireNvim();
        var cases = CaseLoader.All.Where(c => !c.NoDiff).ToList();
        var results = Differential.RunAll(cases);
        var report = Differential.Report(results, cases.Count);
        var dir = Path.Combine(AppContext.BaseDirectory, "reports");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "vim-differential.txt"), report);
        var unexplained = results.Count(r => (r.Diff.Length > 0 || r.Error is not null) && !Differential.KnownDivergences.ContainsKey(r.Case.Id));
        Assert.True(unexplained == 0, report);
    }

    [Fact]
    public void Stored_expectations_match_neovim()
    {
        RequireNvim();
        var cases = CaseLoader.All.Where(c => !c.NoDiff && c.ExpectedLines is not null).ToList();
        var bad = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = 4 }, c =>
        {
            if (Differential.KnownDivergences.ContainsKey(c.Id)) return;
            try
            {
                var nv = NvimOracle.Run(c.ToInput());
                var p = CaseTests.Check(c, nv);
                if (p.Length > 0) bad.Add($"{c.Id} (keys: {c.Keys})\n{p}");
            }
            catch (Exception ex) { bad.Add($"{c.Id}: nvim failed: {ex.Message}"); }
        });
        Assert.True(bad.IsEmpty, $"{bad.Count} stored expectations disagree with nvim:\n" + string.Join("\n", bad.OrderBy(x => x)));
    }

    /// <summary>Developer tool: VIMTEST_BLESS=1 dotnet test --filter Bless writes nvim's result into every case that has no '&lt;' lines.</summary>
    [Fact]
    public void Bless()
    {
        if (Environment.GetEnvironmentVariable("VIMTEST_BLESS") != "1") Assert.Skip("set VIMTEST_BLESS=1 to write missing expectations from nvim");
        RequireNvim();
        var total = 0;
        foreach (var file in CaseLoader.All.Select(c => c.File).Distinct())
        {
            total += CaseLoader.Bless(file, c =>
            {
                try { return NvimOracle.Run(c.ToInput()); }
                catch (Exception) { return null; }
            });
        }
        Assert.True(total >= 0);
    }
}
