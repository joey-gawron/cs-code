using System.Text.Json.Nodes;
using CsCode.Core.Languages;
using CsCode.Dap.Tasks;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// Problem matchers (tasks.json <c>problemMatcher</c>): the built-in ones on real compiler output shapes, custom ones (single, multi-line, loop,
/// fileLocation), output cleaning (escape sequences, carriage returns, any chunking), clearing per run and the cap.
/// </summary>
public sealed class ProblemMatcherTests
{
    private const string Folder = "/work/proj";
    private readonly DiagnosticService _diagnostics = new();

    private ProblemCollector Collector(string matcherJson, string? cwd = null)
    {
        var collector = new ProblemCollector(ProblemMatchers.Parse(JsonNode.Parse(matcherJson)), _diagnostics, Folder, cwd ?? Folder);
        collector.Start();
        return collector;
    }

    private (Uri Uri, Diagnostic D) Single() => Assert.Single(_diagnostics.GetAll());

    private static void Feed(ProblemCollector c, string output)
    {
        c.Append(output);
        c.Complete();
    }

    [Fact]
    public void MsCompile_reads_dotnet_build_errors_with_location_code_and_the_project_suffix_removed()
    {
        var c = Collector("\"$msCompile\"");
        Feed(c, "  Restored /work/proj/app.csproj\n/work/proj/Program.cs(12,17): error CS0103: The name 'x' does not exist in the current context [/work/proj/app.csproj]\n" +
            "Build FAILED.\n");
        var (uri, d) = Single();
        Assert.Equal("/work/proj/Program.cs", uri.LocalPath);
        Assert.Equal((11, 16), (d.Range.Start.Line, d.Range.Start.Character));
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal("CS0103", d.Code);
        Assert.Equal("The name 'x' does not exist in the current context", d.Message);
        Assert.Equal((1, 0), (c.Errors, c.Warnings));
    }

    [Fact]
    public void Tsc_reads_both_output_styles_relative_to_the_working_directory()
    {
        var c = Collector("\"$tsc\"", cwd: "/work/proj/web");
        Feed(c, "src/a.ts(3,5): error TS2322: Type 'string' is not assignable to type 'number'.\n" +
            "src/b.ts:7:1 - warning TS6133: 'y' is declared but its value is never read.\n");
        var all = _diagnostics.GetAll().OrderBy(x => x.Uri.LocalPath).ToList();
        Assert.Equal(2, all.Count);
        Assert.Equal("/work/proj/web/src/a.ts", all[0].Uri.LocalPath);
        Assert.Equal(("2322", 2, 4), (all[0].Diagnostic.Code, all[0].Diagnostic.Range.Start.Line, all[0].Diagnostic.Range.Start.Character));
        Assert.Equal(DiagnosticSeverity.Warning, all[1].Diagnostic.Severity);
    }

    [Fact]
    public void Rustc_reads_the_two_line_form_cargo_prints()
    {
        var c = Collector("\"$rustc\"");
        Feed(c, "   Compiling demo v0.1.0 (/work/proj)\n" +
            "error[E0425]: cannot find value `x` in this scope\n" +
            "  --> src/main.rs:3:13\n" +
            "   |\n" +
            "3  |     let y = x + 1;\n" +
            "warning: unused variable: `z`\n" +
            " --> src/lib.rs:10:9\n");
        var all = _diagnostics.GetAll().ToDictionary(x => Path.GetFileName(x.Uri.LocalPath), x => x.Diagnostic);
        Assert.Equal(("E0425", DiagnosticSeverity.Error, "cannot find value `x` in this scope", 2, 12),
            (all["main.rs"].Code, all["main.rs"].Severity, all["main.rs"].Message, all["main.rs"].Range.Start.Line, all["main.rs"].Range.Start.Character));
        Assert.Equal(DiagnosticSeverity.Warning, all["lib.rs"].Severity);
        Assert.Equal("/work/proj/src/lib.rs", _diagnostics.GetAll().Single(x => x.Uri.LocalPath.EndsWith("lib.rs", StringComparison.Ordinal)).Uri.LocalPath);
    }

    [Fact]
    public void Gcc_and_eslint_stylish_with_its_looping_pattern()
    {
        var c = Collector("[\"$gcc\", \"$eslint-stylish\"]");
        Feed(c, "/work/proj/main.c:4:10: fatal error: missing.h: No such file or directory\n" +
            "/work/proj/web/app.js\n" +
            "  1:10  error    'a' is defined but never used  no-unused-vars\n" +
            "  2:3   warning  Unexpected console statement   no-console\n" +
            "\n" +
            "✖ 2 problems\n");
        var all = _diagnostics.GetAll().ToList();
        Assert.Equal(3, all.Count);
        Assert.Contains(all, x => x.Uri.LocalPath == "/work/proj/main.c" && x.Diagnostic.Message == "missing.h: No such file or directory");
        var js = all.Where(x => x.Uri.LocalPath == "/work/proj/web/app.js").Select(x => x.Diagnostic).OrderBy(d => d.Range.Start.Line).ToList();
        Assert.Equal(["no-unused-vars", "no-console"], js.Select(d => d.Code));
        Assert.Equal([DiagnosticSeverity.Error, DiagnosticSeverity.Warning], js.Select(d => d.Severity));
    }

    [Fact]
    public void A_custom_matcher_with_location_severity_default_and_absolute_files()
    {
        var c = Collector("""
            {"owner":"mytool","source":"mt","severity":"warning","fileLocation":"absolute",
             "pattern":{"regexp":"^MT (\\S+) @\\((\\d+,\\d+,\\d+,\\d+)\\): (.*)$","file":1,"location":2,"message":3}}
            """);
        Feed(c, "MT /abs/file.txt @(2,3,4,5): something odd\nMT relative.txt @(1,1,1,1): ignored, not absolute\n");
        var (uri, d) = Single();
        Assert.Equal("/abs/file.txt", uri.LocalPath);
        Assert.Equal(((1, 2), (3, 4)), ((d.Range.Start.Line, d.Range.Start.Character), (d.Range.End.Line, d.Range.End.Character)));
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("mt", d.Source);
        Assert.NotEmpty(_diagnostics.Get(new Uri("/abs/file.txt")));
    }

    [Fact]
    public void Output_is_matched_as_a_terminal_shows_it_whatever_the_chunking()
    {
        var c = Collector("\"$gcc\"");
        // colours from the compiler, a progress line overwritten with \r, CRLF line ends, the line split across chunks
        const string output = "[1/2] Building\r[2/2] Linking\r\n\e[01m\e[K/work/proj/x.c:1:2:\e[m\e[K \e[01;31m\e[Kerror: \e[m\e[Kbad\r\n\e]0;title\a";
        for (var i = 0; i < output.Length; i += 7) c.Append(output.Substring(i, Math.Min(7, output.Length - i)));
        c.Complete();
        var (uri, d) = Single();
        Assert.Equal("/work/proj/x.c", uri.LocalPath);
        Assert.Equal("bad", d.Message);
    }

    [Fact]
    public void A_new_run_clears_the_matchers_earlier_problems_and_problems_are_capped()
    {
        var first = Collector("\"$gcc\"");
        Feed(first, "/work/proj/a.c:1:1: error: old\n");
        Assert.Single(_diagnostics.GetAll());
        var second = Collector("\"$gcc\"");
        Assert.Empty(_diagnostics.GetAll()); // cleared when the task starts again
        Feed(second, string.Concat(Enumerable.Range(1, ProblemCollector.MaxProblems + 500).Select(i => $"/work/proj/b.c:{i}:1: warning: w{i}\n")));
        Assert.Equal(ProblemCollector.MaxProblems, _diagnostics.GetAll().Count);
    }

    [Theory]
    [InlineData("\"$unknown\"")]
    [InlineData("{\"owner\":\"x\"}")]
    [InlineData("{\"pattern\":{\"regexp\":\"(unclosed\",\"file\":1,\"message\":1}}")]
    [InlineData("42")]
    public void Unknown_or_invalid_matchers_are_ignored(string json) => Assert.Empty(ProblemMatchers.Parse(JsonNode.Parse(json)));

    /// <summary>
    /// terminal-5: in a terminal (tasks run in a PTY) the .NET SDK's terminal logger prints each diagnostic indented under its project, in
    /// colour. The bytes are a real `dotnet build` (SDK 10.0.401) captured in a PTY, with the probe's temp path replaced.
    /// </summary>
    [Fact]
    public void MsCompile_reads_the_terminal_loggers_indented_coloured_diagnostics()
    {
        var c = Collector("\"$msCompile\"");
        Feed(c, "\e[?1h\e=\e]9;4;3;\e\\Restore complete (0.3s)\r\r\n" +
            "\e[?1h\e=  app \e[36;1mnet10.0\e[m \e[31;1mfailed with 2 error(s)\e[m (2.2s)\r\r\n" +
            "    /work/proj/\e[1mProgram.cs\e[m(5,17): \e[31;1merror\e[m \e[31;1mCS0103\e[m: The name 'y' does not exist in the current context\r\r\n" +
            "    /work/proj/\e[1mProgram.cs\e[m(6,20): \e[31;1merror\e[m \e[31;1mCS0029\e[m: Cannot implicitly convert type 'int' to 'string'\r\r\n" +
            "\r\r\nBuild \e[31;1mfailed with 2 error(s)\e[m in 2.8s\r\r\n\e]9;4;0;\e\\");
        var all = _diagnostics.GetAll().OrderBy(x => x.Diagnostic.Range.Start.Line).ToList();
        Assert.Equal(2, all.Count);
        Assert.All(all, x => Assert.Equal("/work/proj/Program.cs", x.Uri.LocalPath));
        Assert.Equal(("CS0103", 4, 16, "The name 'y' does not exist in the current context"),
            (all[0].Diagnostic.Code, all[0].Diagnostic.Range.Start.Line, all[0].Diagnostic.Range.Start.Character, all[0].Diagnostic.Message));
        Assert.Equal(("CS0029", 5, 19), (all[1].Diagnostic.Code, all[1].Diagnostic.Range.Start.Line, all[1].Diagnostic.Range.Start.Character));
        Assert.Equal((2, 0), (c.Errors, c.Warnings));
    }

    /// <summary>terminal-12: a program's CRLF reaches a task's matchers through the PTY as "\r\r\n" (ONLCR): the line is still matched.</summary>
    [Fact]
    public void A_crlf_line_through_the_pty_is_matched_and_a_trailing_cr_overwrites_nothing()
    {
        Assert.Equal("a", ProblemCollector.Clean("a\r\r"));
        Assert.Equal("a", ProblemCollector.Clean("a\r"));
        Assert.Equal("new", ProblemCollector.Clean("old text\rnew\r\r"));
        var c = Collector("\"$gcc\"");
        Feed(c, "src/a.c:3:5: error: boom\r\r\n");
        var (uri, d) = Single();
        Assert.Equal("/work/proj/src/a.c", uri.LocalPath);
        Assert.Equal("boom", d.Message);
    }

    [Fact]
    public void A_line_redrawn_in_place_keeps_only_what_follows_the_cursor_reset()
    {
        // a progress display moved up and erased before the diagnostic was written on the same line
        Assert.Equal("    /work/proj/x.cs(1,2): error CS1: m", ProblemCollector.Clean("  app Build (1.2s)\e[1F\e[2K    /work/proj/x.cs(1,2): error CS1: m"));
        Assert.Equal("text", ProblemCollector.Clean("progress 50%\e[2K\e[Gtext"));
        Assert.Equal("keep this", ProblemCollector.Clean("keep\e[K this")); // erase to the end of the line erases nothing before the cursor
    }

    // ------------------------------------------------------------------ T43: background (watch) matchers

    [Fact]
    public void Tsc_watch_follows_compilations_and_each_cycle_replaces_the_last_ones_problems()
    {
        var c = Collector("\"$tsc-watch\"", cwd: "/work/proj");
        var events = new List<string>();
        c.BackgroundBegan = () => events.Add("began");
        c.BackgroundEnded = () => events.Add("ended");
        Assert.True(c.IsWatching);
        Assert.True(c.IsBusy); // activeBegins: busy before any output

        c.Append("\e[2J\e[3J\e[H[\e[90m10:00:00 AM\e[0m] Starting compilation in watch mode...\n\n");
        Assert.Empty(events); // already busy
        c.Append("src/a.ts(3,5): error TS2322: Type 'string' is not assignable to type 'number'.\n");
        c.Append("src/b.ts(1,1): error TS1005: ';' expected.\n");
        c.Append("[\e[90m10:00:02 AM\e[0m] Found 2 errors. Watching for file changes.\n");
        Assert.Equal(["ended"], events);
        Assert.False(c.IsBusy);
        Assert.Equal(2, _diagnostics.GetAll().Count);
        Assert.Equal(2, c.Errors);

        // the next compilation fixed b.ts: its problem goes when the cycle ends, a.ts's stays
        c.Append("[\e[90m10:01:00 AM\e[0m] File change detected. Starting incremental compilation...\n");
        Assert.True(c.IsBusy);
        Assert.Equal(["ended", "began"], events);
        c.Append("src/a.ts(3,5): error TS2322: Type 'string' is not assignable to type 'number'.\n");
        c.Append("[\e[90m10:01:01 AM\e[0m] Found 1 error. Watching for file changes.\n");
        Assert.Equal(["ended", "began", "ended"], events);
        var (uri, d) = Single();
        Assert.Equal("/work/proj/src/a.ts", uri.LocalPath);
        Assert.Equal("2322", d.Code);
        Assert.Equal(1, c.Errors);
    }

    [Fact]
    public void A_custom_background_matcher_and_the_watching_alias_are_read()
    {
        var m = Assert.Single(ProblemMatchers.Parse(JsonNode.Parse("""
            {"owner":"w","pattern":{"regexp":"^(.*):(\\d+): (.*)$","file":1,"line":2,"message":3},
             "background":{"beginsPattern":"^build started","endsPattern":{"regexp":"^build (done|failed)"}}}
            """)));
        Assert.NotNull(m.Background);
        Assert.False(m.Background!.ActiveBegins);
        Assert.Matches(m.Background.Begins!, "build started");
        Assert.Matches(m.Background.Ends!, "build failed");
        var alias = Assert.Single(ProblemMatchers.Parse(JsonNode.Parse("""{"base":"$tsc","watching":{"activeBegins":true,"endsPattern":"^ok"}}""")));
        Assert.True(alias.Background!.ActiveBegins);
        var inherited = Assert.Single(ProblemMatchers.Parse(JsonNode.Parse("""{"base":"$tsc-watch","owner":"web"}""")));
        Assert.NotNull(inherited.Background); // extending $tsc-watch keeps its background patterns
        Assert.Null(Assert.Single(ProblemMatchers.Parse(JsonNode.Parse("\"$tsc\""))).Background);
    }

    [Fact]
    public void A_matcher_can_extend_a_builtin()
    {
        var matchers = ProblemMatchers.Parse(JsonNode.Parse("""{"base":"$tsc","owner":"web","fileLocation":["relative","/elsewhere"]}"""));
        var m = Assert.Single(matchers);
        Assert.Equal("web", m.Owner);
        Assert.Equal(FileLocationKind.Relative, m.FileLocation);
        Assert.Equal("/elsewhere", m.FileBase);
        Assert.Single(m.Patterns);
    }
}
