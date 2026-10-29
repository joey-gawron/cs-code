using System.Text;
using Avalonia.Headless.XUnit;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Text;
using CsCode.Vim;
using Xunit;

namespace CsCode.App.Tests.RealApp.Vim;

/// <summary>
/// The adapter must not change Vim's semantics: table cases of the engine's suite (tests/CsCode.Vim.Tests/Cases, whose expectations were
/// blessed from Neovim) are typed as real key events into the real window, each in a file of its own, and the result is compared with
/// (1) the same keys through the engine's headless <see cref="InMemoryVimHost"/> with the same options, and (2) the case's expected text and
/// cursor. The app is configured like plain Vim for this (tab size 8 with tabs, no ignorecase/smartcase, no auto-closing, no autoindent).
/// </summary>
public class VimCrossCheckTests
{
    private const string PlainVimSettings = """
        "editor.tabSize": 8, "editor.insertSpaces": false, "editor.detectIndentation": false,
        "editor.autoClosingBrackets": "never", "editor.autoClosingQuotes": "never", "editor.autoSurround": "never",
        "editor.quickSuggestions": false, "editor.suggestOnTriggerCharacters": false, "editor.formatOnType": false,
        "vim.ignorecase": false, "vim.smartcase": false
        """;

    /// <summary>Keys the shell keeps in some mode, or that open IntelliSense, or that need the system clipboard: not used for the cross-check.</summary>
    private static readonly string[] ExcludedKeys =
        ["<C-c>", "<C-k>", "<C-q>", "<C-e>", "<C-y>", "<C-a>", "<C-x>", "<C-n>", "<C-p>", "<C-v>", "<C-\\>", "<C-]>", "<C-^>", "<C-@>", "<C-g>", "<C-l>",
         "<C-z>", "<C-h>", "<C-s>", "<S-", "<M-", "<A-", "<F", "<Insert>", "<Nop>", "<Plug>", "<C-6>", "<C-_>", "<C-j>", "<C-m>", "<Home>", "<End>", "\"+", "\"*", "<k"];

    private static readonly string[] Groups = ["motion", "delete", "change", "yank", "textobj", "visual", "block", "insert", "dot", "registers", "macros", "undo", "join", "replace", "shift", "search", "substitute", "marks"];

    private sealed record Case(string Id, string[] Lines, int Line, int Col, string Keys, string[] Expected, int? ExpectedLine, int? ExpectedCol, string Mode);

    [AvaloniaFact(Timeout = 600_000)]
    public async Task Table_cases_typed_into_the_real_window_match_the_headless_engine_and_the_neovim_expectations()
    {
        var cases = SelectCases(perGroup: 4);
        Assert.True(cases.Count >= 40, $"only {cases.Count} cases selected");
        await using var app = await VimApp.StartAsync("start\n", settings: PlainVimSettings);
        var session = app.Vim.Service.Session;
        session.Options.AutoIndent = false; // plain Vim (the integration turns it on for the editor's sake)
        var failures = new StringBuilder();
        var matchedNvim = 0;
        var index = 0;
        foreach (var c in cases)
        {
            var tab = await app.OpenAsync($"case{index++}.txt", string.Join("\n", c.Lines));
            app.SetCaret(c.Line, c.Col);
            session.Registers.Clear();
            session.Search.Pattern = "";
            app.Keys(c.Keys);
            await DevScene.SettleAsync(1);
            var engine = app.Engine;
            var appLines = app.Text.Split('\n');
            var appCursor = engine.Cursor;
            var appMode = engine.ModeCode;

            // (1) the headless engine, same options, same keys one at a time
            var reference = new VimSession();
            reference.Options.CopyFrom(session.Options);
            var host = new InMemoryVimHost(string.Join("\n", c.Lines)) { Caret = new TextPosition(c.Line, c.Col) };
            using (var refEngine = new VimEngine(host, reference))
            {
                refEngine.TypeKeysOneByOne(c.Keys);
                var refLines = Enumerable.Range(0, host.Buffer.LineCount).Select(host.Buffer.GetLine).ToArray();
                if (!refLines.SequenceEqual(appLines) || refEngine.Cursor != appCursor || refEngine.ModeCode != appMode)
                    failures.AppendLine($"{c.Id} keys {c.Keys}: app [{string.Join("\\n", appLines)}] {appCursor} {appMode} vs engine [{string.Join("\\n", refLines)}] {refEngine.Cursor} {refEngine.ModeCode}");
            }
            // the editor shows what the engine thinks
            if (appMode is "n" && app.Caret != appCursor) failures.AppendLine($"{c.Id}: editor caret {app.Caret} but engine cursor {appCursor}");

            // (2) Neovim's result, as blessed into the case
            var nvimOk = c.Expected.SequenceEqual(appLines) && (c.ExpectedLine is null || (c.ExpectedLine == appCursor.Line && c.ExpectedCol == appCursor.Character)) && c.Mode == appMode;
            if (nvimOk) matchedNvim++;
            else failures.AppendLine($"{c.Id} keys {c.Keys}: app [{string.Join("\\n", appLines)}] {appCursor} {appMode} vs nvim [{string.Join("\\n", c.Expected)}] {c.ExpectedLine}:{c.ExpectedCol} {c.Mode}");

            await app.Well.CloseAsync(tab, prompt: false);
            await DevScene.SettleAsync(1);
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"{cases.Count} cases from {Groups.Length} groups typed into the real window; {matchedNvim} match Neovim's blessed result");
        Assert.True(failures.Length == 0, $"{failures}");
        Assert.Equal(cases.Count, matchedNvim);
    }

    private static List<Case> SelectCases(int perGroup)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "CsCode.Vim.Tests", "Cases"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var casesDir = Path.Combine(dir!.FullName, "tests", "CsCode.Vim.Tests", "Cases");
        var divergent = File.ReadAllLines(Path.Combine(casesDir, "known-divergences.txt")).Select(l => l.Split(' ', 2)[0].Trim()).ToHashSet();
        var selected = new List<Case>();
        foreach (var group in Groups)
        {
            var taken = 0;
            foreach (var c in Parse(group, File.ReadAllLines(Path.Combine(casesDir, group + ".vimtest"))))
            {
                if (taken >= perGroup) break;
                if (divergent.Contains(c.Id) || divergent.Contains(c.Id.Split(':')[1]) || ExcludedKeys.Any(k => c.Keys.Contains(k, StringComparison.Ordinal))) continue;
                selected.Add(c);
                taken++;
            }
        }
        return selected;
    }

    /// <summary>The subset of the .vimtest format these cases use (see tests/CsCode.Vim.Tests/Harness/VimTestCase.cs); cases with setup, error,
    /// register or mark expectations, or tags, are skipped.</summary>
    private static IEnumerable<Case> Parse(string file, string[] lines)
    {
        string? name = null;
        List<string>? input = null, expected = null;
        string? keys = null, mode = null;
        var skip = false;
        Case? Finish()
        {
            if (name is null || skip || keys is null || input is null || expected is null) return null;
            var (initial, line, col) = Cursor(input);
            var (result, eline, ecol) = Cursor(expected);
            return new Case($"{file}:{name}", initial, line ?? 0, col ?? 0, keys, result, eline, ecol, mode ?? "n");
        }
        foreach (var raw in lines.Append("=== end"))
        {
            if (raw.StartsWith("===", StringComparison.Ordinal))
            {
                if (Finish() is { } done) yield return done;
                var header = raw[3..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                name = header.FirstOrDefault();
                skip = header.Length > 1;
                input = null; expected = null; keys = null; mode = null;
                continue;
            }
            if (raw.StartsWith("> ", StringComparison.Ordinal) || raw == ">") (input ??= []).Add(Unescape(raw.Length > 2 ? raw[2..] : ""));
            else if (raw.StartsWith("< ", StringComparison.Ordinal) || raw == "<") (expected ??= []).Add(Unescape(raw.Length > 2 ? raw[2..] : ""));
            else if (raw.StartsWith("keys: ", StringComparison.Ordinal)) keys = raw[6..];
            else if (raw.StartsWith("mode: ", StringComparison.Ordinal)) mode = raw[6..].Trim();
            else if (raw.StartsWith("set:", StringComparison.Ordinal) || raw.StartsWith("ex:", StringComparison.Ordinal) || raw.StartsWith("err:", StringComparison.Ordinal)
                     || raw.StartsWith("reg ", StringComparison.Ordinal) || raw.StartsWith("mark ", StringComparison.Ordinal)) skip = true;
        }
    }

    private static (string[] Lines, int? Line, int? Col) Cursor(List<string> lines)
    {
        var joined = string.Join("\n", lines);
        var at = joined.IndexOf('‸');
        if (at < 0) return (joined.Split('\n'), null, null);
        var before = joined[..at].Split('\n');
        return (joined.Remove(at, 1).Split('\n'), before.Length - 1, before[^1].Length);
    }

    private static string Unescape(string s)
    {
        if (s.EndsWith('⏎')) s = s[..^1];
        return s.Replace("\\t", "\t").Replace("\\\\", "\\");
    }
}
