using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using CsCode.Panels.Search.SearchEditor;
using Xunit;

namespace CsCode.Panels.Search.Tests.SearchEditor;

/// <summary>VS Code's <c>.code-search</c> format: header, results, paths, round trips.</summary>
public sealed class SearchEditorFormatTests
{
    private static readonly SearchEditorConfig Full = new()
    {
        Query = @"a\b" + "\n" + "c",
        IsRegex = true,
        MatchCase = true,
        WholeWord = true,
        OnlyOpenEditors = true,
        UseExcludeSettingsAndIgnoreFiles = false,
        Include = "src, *.cs",
        Exclude = "**/bin/**",
        ContextLines = 2,
    };

    [Fact]
    public void The_header_is_written_as_vs_code_writes_it()
    {
        Assert.Equal(
            "# Query: a\\\\b\\nc\n# Flags: CaseSensitive WordMatch RegExp OpenEditors IgnoreExcludeSettings\n# Including: src, *.cs\n# Excluding: **/bin/**\n# ContextLines: 2\n",
            SearchEditorFormat.SerializeHeader(Full));
        // defaults leave the optional lines out
        Assert.Equal("# Query: foo\n", SearchEditorFormat.SerializeHeader(new SearchEditorConfig { Query = "foo" }));
    }

    [Fact]
    public void A_saved_search_editor_round_trips()
    {
        const string results = "2 results - 1 file\n\nsrc/a.cs:\n  1: alpha\n  2  context\n  3: alpha again\n";
        var text = SearchEditorFormat.Serialize(Full, results);
        var (config, body) = SearchEditorFormat.Parse(text);
        Assert.Equal(Full, config);
        Assert.Equal(results, body);
        Assert.Equal(text, SearchEditorFormat.Serialize(config, body));
        // CRLF on disk reads the same
        var (crlfConfig, crlfBody) = SearchEditorFormat.Parse(text.Replace("\n", "\r\n"));
        Assert.Equal(Full, crlfConfig);
        Assert.Equal(results, crlfBody);
    }

    [Fact]
    public void A_file_saved_by_vs_code_is_read()
    {
        const string vscode = "# Query: TODO\n# Flags: RegExp WordMatch\n# Including: ./src\n# ContextLines: 1\n\n3 results - 2 files\n\nsrc/a.ts:\n  1  import x;\n  2: // TODO one\n\nsrc/b.ts:\n  10: // TODO two\n  11  }\n";
        var (config, body) = SearchEditorFormat.Parse(vscode);
        Assert.Equal(new SearchEditorConfig { Query = "TODO", IsRegex = true, WholeWord = true, Include = "./src", ContextLines = 1 }, config);
        Assert.StartsWith("3 results - 2 files\n", body);
        Assert.Equal(SearchResultLineKind.File, SearchEditorFormat.Classify("src/a.ts:"));
        Assert.Equal(SearchResultLineKind.Match, SearchEditorFormat.Classify("  2: // TODO one"));
        Assert.Equal(SearchResultLineKind.Context, SearchEditorFormat.Classify("  11  }"));
        Assert.Equal(SearchResultLineKind.Other, SearchEditorFormat.Classify("3 results - 2 files"));
        Assert.Equal(SearchResultLineKind.Other, SearchEditorFormat.Classify(""));
    }

    [Fact]
    public void The_header_is_read_leniently()
    {
        // a backslash before anything but n or another backslash stays (VS Code throws); keys ignore case; unknown lines are skipped
        var config = SearchEditorFormat.ParseHeader(["# Query: \\d+\\s", "# flags: RegExp", "# Something: else", "not a header", "# ContextLines: 9999"]);
        Assert.Equal(@"\d+\s", config.Query);
        Assert.True(config.IsRegex);
        Assert.Equal(SearchEditorFormat.MaxContextLines, config.ContextLines);
        Assert.Equal("", SearchEditorFormat.ParseHeader(["# Query: "]).Query);
        Assert.Equal(" spaced ", SearchEditorFormat.ParseHeader(["# Query:  spaced "]).Query);
        foreach (var q in new[] { "", "plain", @"back\slash", @"\\n", "line\nbreak", "\\" })
            Assert.Equal(q, SearchEditorFormat.UnescapeQuery(SearchEditorFormat.EscapeQuery(q)));
    }

    [Fact]
    public void Summaries_use_vs_codes_wording()
    {
        Assert.Equal("No Results", SearchEditorFormat.Summary(0, 0));
        Assert.Equal("1 result - 1 file", SearchEditorFormat.Summary(1, 1));
        Assert.Equal("12 results - 3 files", SearchEditorFormat.Summary(12, 3));
        Assert.Equal("20000 results - 3 files - " + SearchEditorFormat.LimitHitMessage, SearchEditorFormat.Summary(20000, 3, limitHit: true));
        Assert.Equal("Bad regex: x:.", SearchEditorFormat.SafeSummary("Bad regex: x:"));
    }

    [Fact]
    public void Long_lines_are_clipped_around_the_match_with_elisions()
    {
        var line = new string('a', 3000) + "NEEDLE" + new string('b', 3000);
        var clipped = SearchEditorFormat.ClipLine(line, 3000);
        Assert.StartsWith("⟪ 2900 characters skipped ⟫", clipped);
        Assert.EndsWith("⟪ 2106 characters skipped ⟫", clipped);
        Assert.Contains("NEEDLE", clipped);
        Assert.Equal("short", SearchEditorFormat.ClipLine("short", 2));
    }

    [Fact]
    public void Blocks_show_match_lines_with_context_and_gaps()
    {
        var uri = new Uri("file:///w/a.txt");
        var lines = FileLines.Extract(string.Join("\n", Enumerable.Range(1, 30).Select(i => "line " + i)), Enumerable.Range(0, 30).ToArray());
        var file = new FileMatches(uri, [Match(1, 0, 4), Match(2, 0, 4), Match(19, 5, 2)]);
        var block = ResultBlockBuilder.Build(file, context: 1, lines);
        Assert.Equal(
        [
            "   1  line 1",
            "   2: line 2",
            "   3: line 3",
            "   4  line 4",
            "",
            "  19  line 19",
            "  20: line 20",
            "  21  line 21",
        ], block.Lines);
        Assert.Equal(3, block.Matches);
        // without context: only the match lines, no gaps
        Assert.Equal(["   2: line 2", "   3: line 3", "  20: line 20"], ResultBlockBuilder.Build(file, 0, lines).Lines);
        // the file could not be read again: the engine's previews stand in
        Assert.Equal(["  2: preview"], ResultBlockBuilder.Build(new FileMatches(uri, [Match(1, 0, 4)]), 3, null).Lines);
    }

    [Fact]
    public void Lines_split_like_the_engine_and_decode_with_its_encoding()
    {
        var lines = FileLines.Extract("a\r\nb\rc\nd", [0, 1, 2, 3]);
        Assert.Equal(["a", "b", "c", "d"], new[] { lines[0]!, lines[1]!, lines[2]!, lines[3]! });
        Assert.Equal(4, lines.LineCount);
        Assert.Equal(3, FileLines.Extract("x\ny\nz", [0]).LineCount);
        Assert.Equal("é", FileLines.Decode([0xE9], "latin1"));
        Assert.Equal("é", FileLines.Decode([0xEF, 0xBB, 0xBF, 0xC3, 0xA9], "utf-8-bom"));
        Assert.Equal("é", FileLines.Decode([0xFF, 0xFE, 0xE9, 0x00], "utf-16le"));
    }

    [Fact]
    public void Path_lines_are_relative_to_the_folder_and_resolve_back()
    {
        var root = new Uri(Path.Combine(Path.GetTempPath(), "proj") + "/");
        var other = new Uri(Path.Combine(Path.GetTempPath(), "lib") + "/");
        var file = new Uri(Path.Combine(root.LocalPath, "src", "my file.cs")); // as the engine makes them: from paths
        Assert.Equal("src/my file.cs", SearchResultPaths.LabelFor(file, [root]));
        Assert.Equal(file.LocalPath, SearchResultPaths.Resolve("src/my file.cs", [root])!.LocalPath);
        // several folders: "folder • path"
        Assert.Equal("proj • src/my file.cs", SearchResultPaths.LabelFor(file, [other, root]));
        Assert.Equal(file.LocalPath, SearchResultPaths.Resolve("proj • src/my file.cs", [other, root])!.LocalPath);
        // outside every folder: absolute, and back
        var outside = new Uri("file:///elsewhere/x.cs");
        Assert.Equal(outside.LocalPath, SearchResultPaths.LabelFor(outside, [root]));
        Assert.Equal(outside, SearchResultPaths.Resolve(SearchResultPaths.LabelFor(outside, [root]), [root]));
        // another scheme keeps its URI
        var remote = new Uri("ssh://box/home/me/a.cs");
        Assert.Equal(remote, SearchResultPaths.Resolve(SearchResultPaths.LabelFor(remote, [root]), [root]));
        Assert.Null(SearchResultPaths.Resolve("/Untitled-1", [root]));
        Assert.Null(SearchResultPaths.Resolve("relative.cs", []));
    }

    private static SearchMatch Match(int line, int col, int len) =>
        new(new TextRange(new TextPosition(line, col), new TextPosition(line, col + len)), "preview", 0, len, "x");
}
