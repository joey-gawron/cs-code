using CsCode.Terminal.Emulator;
using Xunit;

namespace CsCode.Terminal.Tests.Differential;

/// <summary>
/// Real beats mock: identical byte streams go to CsCode's emulator and to reference terminals — tmux (a private server per run) and libvterm
/// (in-process) — and the screens are compared cell by cell (characters, the attributes each reference reports, cursor, alternate screen).
/// Tests skip with the reason when a reference is not installed. Set <c>CSCODE_DIFF_REPORT=&lt;file&gt;</c> to write every disagreement.
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class DifferentialTests
{
    public static TheoryData<string> CaseNames() => new(Corpus.HandWritten().Select(c => c.Name));

    private static CorpusCase Case(string name) => Corpus.HandWritten().Single(c => c.Name == name);

    internal static ScreenState Ours(CorpusCase c) => Ours(c.Bytes, c.Columns, c.Rows);

    internal static ScreenState Ours(byte[] bytes, int columns, int rows)
    {
        var t = new VtEmulator(columns, rows);
        t.Feed(bytes);
        return ScreenState.From(t);
    }

    internal static void AssertSame(IOracle oracle, string caseName, byte[] bytes, int columns, int rows)
    {
        var ours = Ours(bytes, columns, rows);
        var reference = oracle.Run(bytes, columns, rows);
        var diffs = ScreenState.Diff(ours, reference, oracle.Capabilities);
        if (diffs.Count == 0) return;
        Report(oracle.Name, caseName, diffs, ours, reference);
        if (Corpus.KnownDivergences.TryGetValue((oracle.Name, caseName), out var reason))
        {
            Assert.Skip($"known divergence from {oracle.Name}: {reason}");
            return;
        }
        Assert.Fail($"{caseName}: differs from {oracle.Name}:\n{string.Join('\n', diffs)}\n--- ours ---\n{ours.Text()}--- {oracle.Name} ---\n{reference.Text()}");
    }

    private static void Report(string oracle, string name, List<string> diffs, ScreenState ours, ScreenState reference)
    {
        if (Environment.GetEnvironmentVariable("CSCODE_DIFF_REPORT") is not { Length: > 0 } path) return;
        lock (typeof(DifferentialTests))
            File.AppendAllText(path, $"== {oracle} / {name}\n{string.Join('\n', diffs)}\n--- ours ---\n{ours.Text()}--- {oracle} ---\n{reference.Text()}\n");
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Matches_tmux(string name)
    {
        Assert.SkipUnless(TmuxOracle.Executable is not null, "tmux is not installed (/usr/bin/tmux or CSCODE_TMUX)");
        using var tmux = new TmuxOracle();
        var c = Case(name);
        try { AssertSame(tmux, name, c.Bytes, c.Columns, c.Rows); }
        finally { foreach (var pid in tmux.StartedPids) Assert.False(Support.ProcessChecks.IsAlive(pid), $"tmux process {pid} survived"); }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Matches_libvterm(string name)
    {
        Assert.SkipUnless(LibVtermOracle.IsAvailable, "libvterm (Neovim's, *-libvterm-neovim-* in the Nix store, or CSCODE_LIBVTERM) is not available");
        if (Corpus.Unsafe.TryGetValue(("libvterm", name), out var why)) Assert.Skip(why);
        using var vterm = new LibVtermOracle();
        var c = Case(name);
        AssertSame(vterm, name, c.Bytes, c.Columns, c.Rows);
    }
}
