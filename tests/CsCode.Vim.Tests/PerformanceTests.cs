using System.Diagnostics;
using System.Text;
using Xunit;

namespace CsCode.Vim.Tests;

/// <summary>Per-key time and allocation budgets on a 1 MB buffer, large-operation slicing and bounded memory. Budgets are generous (CI noise, unoptimised test build); the measured numbers are in docs/VIM.md.</summary>
public class PerformanceTests
{
    private static string BigText(int lines, int width = 49)
    {
        var sb = new StringBuilder(lines * (width + 1));
        var r = new Random(1);
        for (var i = 0; i < lines; i++)
        {
            for (var c = 0; c < width; c++) sb.Append(r.Next(8) == 0 ? ' ' : (char)('a' + r.Next(26)));
            if (i < lines - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string ManyLines(int lines) => BigText(lines, 9);

    private sealed class Rig : IDisposable
    {
        public LineVimBuffer Buffer { get; }
        public InMemoryVimHost Host { get; }
        public VimEngine Engine { get; }
        public Rig(string text)
        {
            Buffer = new LineVimBuffer(text);
            Host = new InMemoryVimHost(Buffer);
            Engine = new VimEngine(Host, new VimSession());
        }
        public void Dispose() => Engine.Dispose();
    }

    private static (double Micros, double Bytes) Measure(VimEngine engine, string setup, string keys, int iterations)
    {
        var parsed = Keys.ParseNotation(keys);
        var setupKeys = Keys.ParseNotation(setup);
        void Run(int n)
        {
            for (var i = 0; i < n; i++)
            {
                foreach (var k in setupKeys) engine.HandleKey(k);
                foreach (var k in parsed) engine.HandleKey(k);
            }
        }
        Run(Math.Max(20, iterations / 10));          // warm-up (JIT, caches)
        GC.Collect();
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        Run(iterations);
        sw.Stop();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        var keysPerIteration = parsed.Count + setupKeys.Count;
        return (sw.Elapsed.TotalMilliseconds * 1000.0 / iterations / keysPerIteration, (double)bytes / iterations / keysPerIteration);
    }

    // The 30 most common commands: (name, setup keys that reposition, keys under test)
    private static readonly (string Name, string Setup, string Keys)[] Common =
    [
        ("h", "$", "h"), ("l", "0", "l"), ("j", "", "j"), ("k", "G", "k"), ("w", "0", "w"), ("b", "$", "b"), ("e", "0", "e"), ("0", "", "0"), ("^", "", "^"), ("$", "", "$"),
        ("f", "0", "fa"), ("t", "0", "ta"), ("%", "", "%"), ("}", "gg", "}"), ("gg", "G", "gg"), ("G", "gg", "G"), ("H", "", "H"), ("zz", "", "zz"), ("<C-d>", "gg", "<C-d>"),
        ("x", "0", "x"), ("dd", "", "dd"), ("dw", "0", "dw"), ("cwa<Esc>", "0", "cwa<Esc>"), ("ix<Esc>", "0", "ix<Esc>"), ("Ax<Esc>", "", "Ax<Esc>"), ("yy", "", "yy"), ("yyp", "", "yyp"),
        ("u", "ix<Esc>", "u"), ("ddp", "", "ddp"), ("J", "", "J"),
    ];

    [Fact]
    public void Common_commands_are_fast_on_a_one_megabyte_buffer()
    {
        using var rig = new Rig(BigText(20_000));
        var report = new StringBuilder();
        var slow = new List<string>();
        foreach (var (name, setup, keys) in Common)
        {
            var (micros, bytes) = Measure(rig.Engine, setup, keys, 400);
            report.AppendLine($"{name,-10} {micros,8:F1} us/key {bytes,9:F0} B/key");
            // typical keys take about a microsecond; ^} scans the whole buffer when there is no blank line; the budget leaves room for a loaded CI machine
            var budget = name == "}" ? 5_000 : 500;
            if (micros > budget) slow.Add($"{name}: {micros:F0} us/key (budget {budget})");
        }
        TestContext.Current.SendDiagnosticMessage(report.ToString());
        Assert.True(slow.Count == 0, "slow commands:\n" + string.Join("\n", slow) + "\n\nall:\n" + report);
    }

    [Fact]
    public void Motions_and_single_character_edits_allocate_next_to_nothing_after_warm_up()
    {
        using var rig = new Rig(BigText(20_000));
        var offenders = new List<string>();
        foreach (var (name, setup, keys) in Common)
        {
            var (_, bytes) = Measure(rig.Engine, setup, keys, 400);
            var budget = name switch
            {
                "h" or "l" or "j" or "k" or "w" or "b" or "e" or "0" or "^" or "$" or "H" => 16,       // pure motions: nothing
                "f" or "t" or "gg" or "G" or "zz" or "<C-d>" or "yy" or "%" or "}" => 400,                // a jump-list or register entry
                "J" => 200_000,                                                                         // repeated J grows one line by 50 characters each time
                _ => 6_000,                                                                             // edits rebuild one line and keep the old one for undo
            };
            if (bytes > budget) offenders.Add($"{name}: {bytes:F0} B/key (budget {budget})");
        }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    // ------------------------------------------------------------------------------------------------------------
    // big operations: cooperative slices / one batched edit
    // ------------------------------------------------------------------------------------------------------------

    private sealed record SliceReport(double MaxCallMs, int Calls, double TotalMs);

    private static SliceReport RunToCompletion(VimEngine engine, string keys)
    {
        var max = 0.0;
        var calls = 0;
        var total = Stopwatch.StartNew();
        foreach (var key in Keys.ParseNotation(keys))
        {
            var sw = Stopwatch.StartNew();
            engine.HandleKey(key);
            max = Math.Max(max, sw.Elapsed.TotalMilliseconds);
            calls++;
        }
        while (engine.HasPendingWork)
        {
            var sw = Stopwatch.StartNew();
            engine.RunPendingWork(TimeSpan.FromMilliseconds(3));
            max = Math.Max(max, sw.Elapsed.TotalMilliseconds);
            calls++;
        }
        return new SliceReport(max, calls, total.Elapsed.TotalMilliseconds);
    }

    /// <summary>The longest time one call into the engine may take on a 100k-line buffer. The UI budget is 50 ms in a release build; the tests run unoptimised on shared machines.</summary>
    private const double MaxCallMs = 400;

    [Fact]
    public void Substitute_over_100k_lines_is_sliced_and_correct()
    {
        using var rig = new Rig(ManyLines(100_000));
        var before = rig.Buffer.GetText();
        var report = RunToCompletion(rig.Engine, ":%s/a/b/g<CR>");
        TestContext.Current.SendDiagnosticMessage($":%s over 100k lines: max call {report.MaxCallMs:F1} ms, {report.Calls} calls, {report.TotalMs:F0} ms");
        Assert.DoesNotContain('a', rig.Buffer.GetText());
        Assert.Equal(before.Replace('a', 'b'), rig.Buffer.GetText());
        Assert.Equal(100_000, rig.Buffer.LineCount);
        Assert.True(report.MaxCallMs < MaxCallMs, $"max call {report.MaxCallMs:F1} ms");
        Assert.True(report.Calls > 3, "the substitution should have been split into several slices");
        rig.Engine.TypeKeys("u");
        Assert.Equal(before, rig.Buffer.GetText());
    }

    [Fact]
    public void Delete_to_the_end_of_a_100k_line_buffer_is_one_batched_edit()
    {
        using var rig = new Rig(ManyLines(100_000));
        var report = RunToCompletion(rig.Engine, "dG");
        TestContext.Current.SendDiagnosticMessage($"dG over 100k lines: max call {report.MaxCallMs:F1} ms");
        Assert.Equal(1, rig.Buffer.LineCount);
        Assert.Equal("", rig.Buffer.GetLine(0));
        Assert.True(report.MaxCallMs < MaxCallMs, $"max call {report.MaxCallMs:F1} ms");
        rig.Engine.TypeKeys("u");
        Assert.Equal(100_000, rig.Buffer.LineCount);
    }

    [Fact]
    public void Reindent_of_a_100k_line_buffer_completes()
    {
        using var rig = new Rig(ManyLines(100_000));
        var report = RunToCompletion(rig.Engine, "gg=G");
        TestContext.Current.SendDiagnosticMessage($"gg=G over 100k lines: max call {report.MaxCallMs:F1} ms");
        Assert.Equal(100_000, rig.Buffer.LineCount);
        Assert.True(report.MaxCallMs < MaxCallMs, $"max call {report.MaxCallMs:F1} ms");
    }

    [Fact]
    public void Global_and_normal_commands_over_100k_lines_stay_responsive()
    {
        var cases = new (string Keys, Func<LineVimBuffer, bool> Check)[]
        {
            (":g/a/d<CR>", b => Enumerable.Range(0, b.LineCount).All(i => !b.GetLine(i).Contains('a'))),
            (":v/a/s/b/c/<CR>", b => true),
            (":g/a/normal Ax<CR>", b => true),
            (":%norm Ax<CR>", b => Enumerable.Range(0, b.LineCount).All(i => b.GetLine(i).EndsWith('x'))),
            (":sort<CR>", b => Enumerable.Range(1, b.LineCount - 1).All(i => string.CompareOrdinal(b.GetLine(i - 1), b.GetLine(i)) <= 0)),
        };
        foreach (var (keys, check) in cases)
        {
            using var rig = new Rig(ManyLines(100_000));
            var report = RunToCompletion(rig.Engine, keys);
            TestContext.Current.SendDiagnosticMessage($"{keys} over 100k lines: max call {report.MaxCallMs:F1} ms, {report.TotalMs:F0} ms total");
            Assert.True(check(rig.Buffer), keys);
            Assert.True(report.MaxCallMs < MaxCallMs, $"{keys}: max call {report.MaxCallMs:F1} ms");
        }
    }

    [Fact]
    public void Typed_keys_wait_behind_running_work_and_run_in_order()
    {
        using var rig = new Rig(ManyLines(50_000));
        rig.Engine.HandleKey(':');
        foreach (var k in "%s/a/b/g") rig.Engine.HandleKey(k);
        rig.Engine.HandleKey(Keys.CR);
        // typed while the substitution is still running: queued, executed afterwards, nothing lost
        foreach (var k in "Gx") rig.Engine.HandleKey(k);
        while (rig.Engine.HasPendingWork) rig.Engine.RunPendingWork(TimeSpan.FromMilliseconds(3));
        Assert.DoesNotContain('a', rig.Buffer.GetText());
        Assert.Equal(49_999, rig.Engine.Cursor.Line);
    }

    // ------------------------------------------------------------------------------------------------------------
    // bounded memory
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Jump_list_and_change_list_are_bounded()
    {
        using var rig = new Rig(ManyLines(5_000));
        for (var i = 0; i < 1_000; i++) rig.Engine.TypeKeys($"{(i * 7) % 4_000 + 1}Gx");
        Assert.True(rig.Engine.ChangeListCount <= 100, $"change list holds {rig.Engine.ChangeListCount}");
        Assert.True(rig.Engine.JumpListCount <= 100, $"jump list holds {rig.Engine.JumpListCount}");
    }

    [Fact]
    public void Undo_history_is_bounded_by_undolevels()
    {
        using var rig = new Rig("abc def ghi");
        rig.Engine.ExecuteEx("set undolevels=50");
        for (var i = 0; i < 2_000; i++) rig.Engine.TypeKeys(i % 2 == 0 ? "ix<Esc>" : "x");
        var undone = 0;
        for (var i = 0; i < 500; i++)
        {
            var before = rig.Buffer.GetText();
            rig.Engine.TypeKeys("u");
            if (rig.Buffer.GetText() != before) undone++;
        }
        Assert.True(undone <= 51, $"{undone} undo steps were available with undolevels=50");
        Assert.True(undone >= 40, $"only {undone} undo steps were available");
    }

    [Fact]
    public void Undo_memory_is_capped_for_huge_changes()
    {
        using var rig = new Rig(ManyLines(200_000));
        for (var i = 0; i < 15; i++) rig.Engine.TypeKeys(":%s/a/b/<CR>:%s/b/a/<CR>");
        Assert.True(rig.Engine.UndoSavedChars <= 48_000_000 + 20_000_000, $"undo keeps {rig.Engine.UndoSavedChars} characters");
    }

    [Fact]
    public void Registers_respect_the_configured_cap()
    {
        using var rig = new Rig(ManyLines(50_000));
        rig.Engine.Options.MaxRegisterChars = 100_000;
        rig.Engine.TypeKeys("ggyG");
        Assert.Contains("register", rig.Engine.LastError ?? "", StringComparison.OrdinalIgnoreCase);
        var reg = rig.Engine.Session.Registers.ForRead('0');
        Assert.True(reg is null || reg.TotalChars <= 100_000);
        rig.Engine.TypeKeys("yy");
        Assert.NotNull(rig.Engine.Session.Registers.ForRead('0'));
    }

    [Fact]
    public void Runaway_macros_are_stopped_by_the_budget_and_recording_is_bounded()
    {
        using var rig = new Rig(ManyLines(100));
        rig.Engine.Session.Registers.Named('a').Set(["@a"], VimRegisterType.Character);
        rig.Engine.MacroKeyBudget = 100_000;
        rig.Engine.TypeKeys("@a");
        Assert.NotNull(rig.Engine.LastError);
        rig.Engine.TypeKeys("x");
        Assert.Equal(VimMode.Normal, rig.Engine.Mode);
        // recording is capped so a forgotten q cannot grow without bound
        rig.Engine.TypeKeys("qb");
        for (var i = 0; i < 1_200_000; i++) rig.Engine.HandleKey('l');
        Assert.False(rig.Engine.IsRecording);
        Assert.True((rig.Engine.Session.Registers.ForRead('b')?.TotalChars ?? 0) <= 1_100_000);
    }
}
