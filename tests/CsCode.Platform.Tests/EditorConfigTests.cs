using System.Diagnostics;
using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Platform.Configuration;
using CsCode.Platform.EditorConfig;
using CsCode.Platform.Io;
using CsCode.Platform.Tests.Support;
using CsCode.Platform.Workspace;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Platform.Tests;

public class EditorConfigParserTests
{
    private static EditorConfigSettings Resolve(string text, string relPath)
    {
        var bag = new Dictionary<string, string>();
        EditorConfigParser.Parse(text).Apply(relPath, bag);
        return EditorConfigParser.ToSettings(bag);
    }

    private static bool Matches(string sectionGlob, string relPath) =>
        Resolve($"[{sectionGlob}]\ninsert_final_newline = true\n", relPath).InsertFinalNewline == true;

    // ---- section globs -----------------------------------------------------------------------------------------------------------

    [Theory]
    // no slash: matches at any depth
    [InlineData("*", "a.txt", true)]
    [InlineData("*", "dir/sub/a.txt", true)]
    [InlineData("*.cs", "a.cs", true)]
    [InlineData("*.cs", "x/y/a.cs", true)]
    [InlineData("*.cs", "a.csx", false)]
    [InlineData("*.cs", "a.cs/b", false)]
    [InlineData("Makefile", "Makefile", true)]
    [InlineData("Makefile", "sub/Makefile", true)]
    [InlineData("Makefile", "makefile", false)]                 // case-sensitive
    // braces
    [InlineData("*.{cs,vb}", "a.cs", true)]
    [InlineData("*.{cs,vb}", "a.vb", true)]
    [InlineData("*.{cs,vb}", "a.fs", false)]
    [InlineData("{package.json,.travis.yml}", "package.json", true)]
    [InlineData("{package.json,.travis.yml}", "sub/.travis.yml", true)]
    [InlineData("{package.json,.travis.yml}", "other.json", false)]
    [InlineData("*.{a,b{c,d}}", "x.a", true)]
    [InlineData("*.{a,b{c,d}}", "x.bc", true)]
    [InlineData("*.{a,b{c,d}}", "x.bd", true)]
    [InlineData("*.{a,b{c,d}}", "x.b", false)]
    [InlineData("{single}", "{single}", true)]                  // one alternative without a comma: literal braces
    [InlineData("{single}", "single", false)]
    [InlineData("{a,b", "{a,b", true)]                          // unclosed brace: literal
    // numeric ranges
    [InlineData("file{1..3}.txt", "file2.txt", true)]
    [InlineData("file{1..3}.txt", "file4.txt", false)]
    [InlineData("file{1..3}.txt", "file12.txt", false)]
    [InlineData("v{-1..1}", "v-1", true)]
    [InlineData("v{-1..1}", "v0", true)]
    [InlineData("v{-1..1}", "v2", false)]
    // anchoring: a slash anchors to the .editorconfig's directory
    [InlineData("/a.cs", "a.cs", true)]
    [InlineData("/a.cs", "sub/a.cs", false)]
    [InlineData("src/*.js", "src/a.js", true)]
    [InlineData("src/*.js", "src/x/a.js", false)]               // * does not cross '/'
    [InlineData("src/*.js", "other/src/a.js", false)]
    [InlineData("src/**.js", "src/a.js", true)]
    [InlineData("src/**.js", "src/x/y/a.js", true)]             // ** does
    [InlineData("**/foo.txt", "foo.txt", false)]               // "**/" needs a directory: editorconfig's ** is plain ".*"
    [InlineData("**/foo.txt", "a/foo.txt", true)]
    [InlineData("**/foo.txt", "a/b/foo.txt", true)]
    [InlineData("lib/**", "lib/x/y", true)]
    // ? and classes
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("file?.txt", "file12.txt", false)]
    [InlineData("file?.txt", "file/.txt", false)]
    [InlineData("[abc].txt", "a.txt", true)]
    [InlineData("[abc].txt", "d.txt", false)]
    [InlineData("[!abc].txt", "d.txt", true)]
    [InlineData("[!abc].txt", "a.txt", false)]
    [InlineData("[a-c].txt", "b.txt", true)]
    [InlineData("a[b", "a[b", true)]                            // unclosed class: literal
    // escapes
    [InlineData("a\\*b", "a*b", true)]
    [InlineData("a\\*b", "axb", false)]
    [InlineData("a\\{b", "a{b", true)]
    [InlineData("*.c++", "x.c++", true)]
    [InlineData("a.b", "aXb", false)]
    [InlineData("日本語.txt", "日本語.txt", true)]
    public void Section_globs_match_per_the_spec(string glob, string path, bool expected) =>
        Assert.Equal(expected, Matches(glob, path));

    // ---- properties --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void All_documented_properties_are_read()
    {
        var s = Resolve("""
            root = true
            [*]
            indent_style = space
            indent_size = 4
            end_of_line = crlf
            charset = utf-8-bom
            trim_trailing_whitespace = true
            insert_final_newline = false
            """, "a.cs");
        Assert.Equal(IndentStyle.Space, s.IndentStyle);
        Assert.Equal(4, s.IndentSize);
        Assert.Equal(4, s.TabWidth);
        Assert.Equal(EndOfLineStyle.CrLf, s.EndOfLine);
        Assert.Equal("utf-8-bom", s.Charset);
        Assert.True(s.TrimTrailingWhitespace);
        Assert.False(s.InsertFinalNewline);
    }

    [Theory]
    [InlineData("lf", EndOfLineStyle.Lf)]
    [InlineData("CR", EndOfLineStyle.Cr)]
    [InlineData("CrLf", EndOfLineStyle.CrLf)]
    public void End_of_line_values_are_case_insensitive(string value, EndOfLineStyle expected) =>
        Assert.Equal(expected, Resolve($"[*]\nend_of_line = {value}", "a").EndOfLine);

    [Theory]
    [InlineData("latin1")]
    [InlineData("utf-8")]
    [InlineData("utf-16be")]
    [InlineData("utf-16le")]
    [InlineData("UTF-8")]
    public void Charset_values(string value) => Assert.Equal(value.ToLowerInvariant(), Resolve($"[*]\ncharset={value}", "a").Charset);

    [Fact]
    public void Keys_are_case_insensitive_and_colon_works_as_a_separator()
    {
        var s = Resolve("[*]\nINDENT_STYLE = Tab\nTab_Width: 8\n", "a");
        Assert.Equal(IndentStyle.Tab, s.IndentStyle);
        Assert.Equal(8, s.TabWidth);
    }

    [Theory]
    [InlineData("indent_size = abc")]
    [InlineData("indent_size = -2")]
    [InlineData("indent_size = 0")]
    [InlineData("indent_size = 99999")]
    [InlineData("end_of_line = windows")]
    [InlineData("charset = utf8")]
    [InlineData("indent_style = tabs")]
    [InlineData("insert_final_newline = yes")]
    [InlineData("trim_trailing_whitespace =")]
    [InlineData("not a property line at all")]
    public void Invalid_values_are_ignored_not_fatal(string line)
    {
        var s = Resolve($"[*]\n{line}\n", "a");
        Assert.True(s.IsEmpty, $"'{line}' should have been ignored but gave {s}");
    }

    [Fact]
    public void Indent_size_tab_uses_tab_width_and_tab_width_defaults_to_indent_size()
    {
        var a = Resolve("[*]\nindent_size = tab\ntab_width = 4\n", "f");
        Assert.Equal((4, 4), (a.IndentSize, a.TabWidth));

        var b = Resolve("[*]\nindent_size = 2\n", "f");
        Assert.Equal((2, 2), (b.IndentSize, b.TabWidth));

        var c = Resolve("[*]\nindent_size = 4\ntab_width = 8\n", "f");
        Assert.Equal((4, 8), (c.IndentSize, c.TabWidth));

        var d = Resolve("[*]\nindent_style = tab\ntab_width = 8\n", "f");   // tab style, no size: the indent is one tab wide
        Assert.Equal((8, 8), (d.IndentSize, d.TabWidth));

        var e = Resolve("[*]\nindent_style = tab\nindent_size = 2\n", "f");
        Assert.Equal((2, 2), (e.IndentSize, e.TabWidth));

        var f = Resolve("[*]\nindent_size = tab\n", "f");                     // "tab" with no tab_width: unknown
        Assert.Null(f.IndentSize);
    }

    [Fact]
    public void Later_sections_override_earlier_ones_and_unset_removes()
    {
        var text = """
            [*]
            indent_size = 4
            trim_trailing_whitespace = true
            [*.md]
            trim_trailing_whitespace = false
            [Makefile]
            indent_style = tab
            indent_size = unset
            """;
        Assert.True(Resolve(text, "a.cs").TrimTrailingWhitespace);
        Assert.False(Resolve(text, "docs/x.md").TrimTrailingWhitespace);
        Assert.Equal(4, Resolve(text, "docs/x.md").IndentSize);
        var make = Resolve(text, "Makefile");
        Assert.Equal(IndentStyle.Tab, make.IndentStyle);
        Assert.Null(make.IndentSize);                                          // unset removed the inherited value
    }

    [Fact]
    public void Comments_blank_lines_crlf_bom_and_padding_are_tolerated()
    {
        var text = "﻿# top comment\r\n; another\r\nroot = true\r\n\r\n[*]  \r\n  indent_size   =   3  \r\n# trailing\r\n";
        var file = EditorConfigParser.Parse(text);
        Assert.True(file.IsRoot);
        var bag = new Dictionary<string, string>();
        file.Apply("x.txt", bag);
        Assert.Equal(3, EditorConfigParser.ToSettings(bag).IndentSize);
    }

    [Fact]
    public void Root_is_only_read_from_the_preamble_and_defaults_to_false()
    {
        Assert.False(EditorConfigParser.Parse("[*]\nroot = true\n").IsRoot);
        Assert.False(EditorConfigParser.Parse("").IsRoot);
        Assert.True(EditorConfigParser.Parse("root = TRUE\n").IsRoot);
    }

    [Fact]
    public void Properties_before_any_section_do_not_apply_to_files()
    {
        Assert.True(Resolve("indent_size = 8\n[*.md]\ninsert_final_newline = true\n", "a.cs").IsEmpty);
    }

    [Fact]
    public void A_section_whose_glob_cannot_be_compiled_is_skipped_and_the_rest_still_work()
    {
        var s = Resolve("[*.{cs]\nindent_size = 9\n[*.cs]\nindent_size = 2\n", "a.cs");
        Assert.Equal(2, s.IndentSize);
    }

    [Fact]
    public void A_giant_file_is_cut_off_without_hanging()
    {
        var text = "[*]\n" + string.Join('\n', Enumerable.Range(0, 100_000).Select(i => $"key{i} = v")) + "\nindent_size = 4\n";
        var sw = Stopwatch.StartNew();
        var s = Resolve(text, "a.cs");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Null(s.IndentSize);      // the line after the 20,000-line cap is not read
    }

    [Fact]
    public void A_regex_bomb_glob_cannot_hang_matching()
    {
        var evil = string.Concat(Enumerable.Repeat("*a", 30)) + "b";
        var sw = Stopwatch.StartNew();
        Assert.False(Matches(evil, new string('a', 3000)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }
}

public sealed class EditorConfigFixture : IDisposable
{
    public TempDirectory Temp { get; } = new("cscode-ec-");
    public IoLane Lane { get; } = new("test.ec", 2);
    public FakeFileWatcher Watcher { get; } = new();
    public EditorConfigService Service { get; }

    public EditorConfigFixture(bool watch = true) => Service = new EditorConfigService(Lane, watch ? Watcher : null);

    public string P(string rel) => Temp.Combine(rel.Split('/'));
    public Uri U(string rel) => FileUri.FromPath(P(rel));
    public void Write(string rel, string text) => Temp.WriteFile(rel, text);
    public void Dispose() { Service.Dispose(); Lane.Dispose(); Temp.Dispose(); }
}

public class EditorConfigServiceTests : IDisposable
{
    private readonly EditorConfigFixture _f = new();
    public void Dispose() => _f.Dispose();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- cascade -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Nearer_files_override_farther_ones_and_each_file_gets_its_own_answer()
    {
        _f.Write(".editorconfig", "root = true\n[*]\nindent_size = 4\nindent_style = space\n[*.md]\ntrim_trailing_whitespace = false\n");
        _f.Write("sub/.editorconfig", "[*.cs]\nindent_size = 2\n");

        var deep = await _f.Service.GetAsync(_f.U("sub/a.cs"), Ct);
        Assert.Equal(2, deep.IndentSize);                       // overridden by the nearer file
        Assert.Equal(IndentStyle.Space, deep.IndentStyle);      // inherited from the farther one

        var md = await _f.Service.GetAsync(_f.U("sub/readme.md"), Ct);
        Assert.Equal(4, md.IndentSize);
        Assert.False(md.TrimTrailingWhitespace);

        var top = await _f.Service.GetAsync(_f.U("top.cs"), Ct);
        Assert.Equal(4, top.IndentSize);
    }

    [Fact]
    public async Task root_true_stops_the_search_upwards()
    {
        _f.Write(".editorconfig", "[*]\nend_of_line = crlf\nindent_size = 8\n");
        _f.Write("app/.editorconfig", "root = true\n[*]\nindent_size = 2\n");
        var inApp = await _f.Service.GetAsync(_f.U("app/a.cs"), Ct);
        Assert.Equal(2, inApp.IndentSize);
        Assert.Null(inApp.EndOfLine);                            // the parent's crlf is not inherited past root=true
        var outside = await _f.Service.GetAsync(_f.U("b.cs"), Ct);
        Assert.Equal(EndOfLineStyle.CrLf, outside.EndOfLine);
    }

    [Fact]
    public async Task Sections_are_matched_relative_to_the_directory_of_their_own_file()
    {
        _f.Write("proj/.editorconfig", "root = true\n[src/*.cs]\nindent_size = 3\n");
        Assert.Equal(3, (await _f.Service.GetAsync(_f.U("proj/src/a.cs"), Ct)).IndentSize);
        Assert.Null((await _f.Service.GetAsync(_f.U("proj/other/src/a.cs"), Ct)).IndentSize);
        Assert.Null((await _f.Service.GetAsync(_f.U("proj/src/deep/a.cs"), Ct)).IndentSize);
    }

    [Fact]
    public async Task A_file_with_no_applicable_editorconfig_gets_Empty_and_non_file_uris_too()
    {
        _f.Write("proj/.editorconfig", "root = true\n[*.md]\nindent_size = 2\n");
        Assert.True((await _f.Service.GetAsync(_f.U("proj/a.cs"), Ct)).IsEmpty);
        Assert.True((await _f.Service.GetAsync(new Uri("untitled:Untitled-1"), Ct)).IsEmpty);
        Assert.True((await _f.Service.GetAsync(new Uri("ssh://host/x.cs"), Ct)).IsEmpty);
    }

    [Fact]
    public async Task Files_that_do_not_exist_yet_still_resolve_by_their_directory()
    {
        _f.Write(".editorconfig", "root = true\n[*.cs]\nindent_size = 5\n");
        Assert.Equal(5, (await _f.Service.GetAsync(_f.U("new/folder/Brand.cs"), Ct)).IndentSize);
    }

    // ---- caching -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Results_are_cached_synchronously_available_and_TryGetCached_never_touches_the_disk()
    {
        _f.Write(".editorconfig", "root = true\n[*]\nindent_size = 4\n");
        Assert.Null(_f.Service.TryGetCached(_f.U("a.cs")));

        var first = await _f.Service.GetAsync(_f.U("a.cs"), Ct);
        Assert.Equal(first, _f.Service.TryGetCached(_f.U("a.cs")));

        var again = _f.Service.GetAsync(_f.U("a.cs"), Ct);
        Assert.True(again.IsCompletedSuccessfully);              // served from the cache without a lane hop
        Assert.Equal(first, await again);

        File.Delete(_f.P(".editorconfig"));                      // cached answers survive until invalidated/revalidated
        Assert.Equal(4, _f.Service.TryGetCached(_f.U("a.cs"))!.IndentSize);
    }

    [Fact]
    public async Task Siblings_share_the_parsed_directory_and_the_caches_stay_bounded()
    {
        _f.Write(".editorconfig", "root = true\n[*]\nindent_size = 4\n");
        for (var i = 0; i < 200; i++) await _f.Service.GetAsync(_f.U($"same/dir/f{i}.cs"), Ct);
        Assert.True(_f.Service.CachedDirectoryCount < 20, $"{_f.Service.CachedDirectoryCount} directory entries for one directory chain");

        for (var i = 0; i < 6000; i++) await _f.Service.GetAsync(_f.U($"d{i}/f.cs"), Ct);   // thousands of directories and results
        Assert.InRange(_f.Service.CachedDirectoryCount, 1, 1100);
        Assert.InRange(_f.Service.CachedResultCount, 1, 4100);
    }

    // ---- invalidation ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_watched_editorconfig_change_clears_the_cache_and_raises_Changed()
    {
        _f.Write(".editorconfig", "root = true\n[*]\nindent_size = 4\n");
        await ((IWorkspaceBound)_f.Service).OnWorkspaceFoldersChangedAsync([FileUri.FromDirectoryPath(_f.Temp.Path)], Ct);
        Assert.Equal(4, (await _f.Service.GetAsync(_f.U("a.cs"), Ct)).IndentSize);

        var changed = 0;
        using var sub = _f.Service.Changed.Subscribe(_ => changed++);
        _f.Write(".editorconfig", "root = true\n[*]\nindent_size = 8\n");
        _f.Watcher.Raise(_f.P(".editorconfig"));

        Assert.Equal(1, changed);
        Assert.Null(_f.Service.TryGetCached(_f.U("a.cs")));
        Assert.Equal(8, (await _f.Service.GetAsync(_f.U("a.cs"), Ct)).IndentSize);
    }

    [Fact]
    public async Task Other_files_changing_in_the_workspace_do_not_invalidate()
    {
        _f.Write(".editorconfig", "root = true\n[*]\nindent_size = 4\n");
        await ((IWorkspaceBound)_f.Service).OnWorkspaceFoldersChangedAsync([FileUri.FromDirectoryPath(_f.Temp.Path)], Ct);
        await _f.Service.GetAsync(_f.U("a.cs"), Ct);
        var changed = 0;
        using var sub = _f.Service.Changed.Subscribe(_ => changed++);
        _f.Watcher.Raise(_f.P("a.cs"));
        _f.Watcher.Raise(_f.P("sub/.editorconfig.bak"));
        Assert.Equal(0, changed);
        Assert.NotNull(_f.Service.TryGetCached(_f.U("a.cs")));
    }

    [Fact]
    public async Task Files_outside_any_watched_workspace_are_revalidated_by_timestamp()
    {
        _f.Service.RevalidateAfterMs = 0;                         // every lookup re-checks (the real default is 5 s)
        _f.Write(".editorconfig", "root = true\n[*]\nindent_size = 4\n");
        Assert.Equal(4, (await _f.Service.GetAsync(_f.U("a.cs"), Ct)).IndentSize);
        var changed = 0;
        using var sub = _f.Service.Changed.Subscribe(_ => changed++);

        _f.Write(".editorconfig", "root = true\n[*]\nindent_size = 6 \n[*.cs]\ntab_width = 3\n");   // different size => detected
        var after = await _f.Service.GetAsync(_f.U("a.cs"), Ct);
        Assert.Equal(6, after.IndentSize);
        Assert.Equal(3, after.TabWidth);
        Assert.True(changed >= 1);

        File.Delete(_f.P(".editorconfig"));
        Assert.True((await _f.Service.GetAsync(_f.U("a.cs"), Ct)).IsEmpty);
    }

    [Fact]
    public async Task Changing_workspace_folders_swaps_the_watches_and_dispose_releases_them()
    {
        var bound = (IWorkspaceBound)_f.Service;
        var a = FileUri.FromDirectoryPath(_f.Temp.Combine("a"));
        var b = FileUri.FromDirectoryPath(_f.Temp.Combine("b"));
        Directory.CreateDirectory(_f.Temp.Combine("a"));
        Directory.CreateDirectory(_f.Temp.Combine("b"));

        await bound.OnWorkspaceFoldersChangedAsync([a], Ct);
        Assert.Equal(1, _f.Watcher.ActiveCount);
        await bound.OnWorkspaceFoldersChangedAsync([a, b], Ct);
        Assert.Equal(2, _f.Watcher.ActiveCount);                  // old watch replaced, not accumulated
        await bound.OnWorkspaceFoldersChangedAsync([], Ct);
        Assert.Equal(0, _f.Watcher.ActiveCount);

        await bound.OnWorkspaceFoldersChangedAsync([a], Ct);
        _f.Service.Dispose();
        Assert.Equal(0, _f.Watcher.ActiveCount);
    }

    // ---- robustness / threading --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_huge_editorconfig_is_ignored()
    {
        _f.Write(".editorconfig", "root = true\n[*]\nindent_size = 4\n" + new string('#', 1_200_000));
        Assert.True((await _f.Service.GetAsync(_f.U("a.cs"), Ct)).IsEmpty);
    }

    [Fact]
    public async Task Many_files_resolve_in_parallel_with_consistent_results()
    {
        _f.Write(".editorconfig", "root = true\n[*.cs]\nindent_size = 4\n[*.md]\nindent_size = 2\n");
        var tasks = Enumerable.Range(0, 300).Select(i => Task.Run(async () =>
        {
            var cs = await _f.Service.GetAsync(_f.U($"p{i % 7}/f{i}.cs"), Ct);
            var md = await _f.Service.GetAsync(_f.U($"p{i % 7}/f{i}.md"), Ct);
            Assert.Equal(4, cs.IndentSize);
            Assert.Equal(2, md.IndentSize);
        }, Ct)).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    [Fact]
    public async Task GetAsync_does_not_block_the_caller_for_uncached_files_when_the_lane_is_saturated()
    {
        _f.Write(".editorconfig", "root = true\n[*]\nindent_size = 4\n");
        using var lane = new IoLane("test.sat", 1);
        using var svc = new EditorConfigService(lane);
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var blocker = lane.RunAsync(_ => { started.Set(); gate.Wait(); }, Ct);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), Ct));

        var sw = Stopwatch.StartNew();
        var pending = svc.GetAsync(_f.U("a.cs"), Ct);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(100));
        Assert.False(pending.IsCompleted);

        gate.Set();
        Assert.Equal(4, (await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct)).IndentSize);
        await blocker;
    }

    [Fact]
    public async Task Cancellation_while_queued_cancels_the_request()
    {
        using var lane = new IoLane("test.sat", 1);
        using var svc = new EditorConfigService(lane);
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var blocker = lane.RunAsync(_ => { started.Set(); gate.Wait(); }, Ct);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), Ct));
        using var cts = new CancellationTokenSource();
        var pending = svc.GetAsync(_f.U("a.cs"), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        gate.Set();
        await blocker;
    }

    [Fact]
    public void A_disposed_service_and_its_watch_callbacks_are_not_retained()
    {
        LeakAssert.NotRetained(() =>
        {
            var svc = new EditorConfigService(_f.Lane, _f.Watcher);
            ((IWorkspaceBound)svc).OnWorkspaceFoldersChangedAsync([FileUri.FromDirectoryPath(_f.Temp.Path)], CancellationToken.None).AsTask().GetAwaiter().GetResult();
            svc.Changed.Subscribe(_ => { });
            svc.Dispose();
            return [svc];
        });
        Assert.Equal(0, _f.Watcher.ActiveCount);
    }
}

public class LanguageScopedSettingsTests : IDisposable
{
    private readonly ConfigFixture _f = new();
    public void Dispose() => _f.Dispose();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_language_block_overrides_the_plain_setting_only_for_that_language()
    {
        _f.WriteUser("""{ "editor.tabSize": 4, "[csharp]": { "editor.tabSize": 2 }, "[python]": { "editor": { "tabSize": 8, "insertSpaces": false } } }""");
        using var cfg = _f.Create();
        Assert.Equal(2, cfg.Get("editor.tabSize", "csharp", 4));
        Assert.Equal(8, cfg.Get("editor.tabSize", "python", 4));       // the nested form works too
        Assert.False(cfg.Get("editor.insertSpaces", "python", true));
        Assert.Equal(4, cfg.Get("editor.tabSize", "java", 99));        // no block: the plain (registered) value
        Assert.True(cfg.Get("editor.insertSpaces", "csharp", false));  // block exists but does not mention it: plain default (true)
        Assert.Equal(4, cfg.Get<int>("editor.tabSize"));               // the plain setting is untouched
    }

    [Fact]
    public void HasLanguageOverride_and_fallbacks()
    {
        _f.WriteUser("""{ "[csharp]": { "editor.tabSize": 2 } }""");
        using var cfg = _f.Create();
        Assert.True(cfg.HasLanguageOverride("editor.tabSize", "csharp"));
        Assert.False(cfg.HasLanguageOverride("editor.tabSize", "go"));
        Assert.False(cfg.HasLanguageOverride("editor.fontSize", "csharp"));
        Assert.Equal(0, cfg.Get<int>("nothing.set", "csharp"));
        Assert.Equal(7, cfg.Get("nothing.set", "csharp", 7));
        Assert.Equal(2, cfg.GetRaw("editor.tabSize", "csharp")!.Value.GetInt32());
        Assert.Equal(14, cfg.GetRaw("editor.fontSize", "csharp")!.Value.GetInt32());
    }

    [Fact]
    public void A_language_value_of_the_wrong_type_yields_the_fallback()
    {
        _f.WriteUser("""{ "[csharp]": { "editor.tabSize": "wide" } }""");
        using var cfg = _f.Create();
        Assert.Equal(5, cfg.Get("editor.tabSize", "csharp", 5));
    }

    [Fact]
    public async Task UpdateAsync_can_write_language_scoped_values_and_they_read_back()
    {
        using var cfg = _f.Create();
        await cfg.UpdateAsync("[csharp].editor.tabSize", 3, ct: Ct);
        Assert.Equal(3, cfg.Get("editor.tabSize", "csharp", 4));
        Assert.Equal(4, cfg.Get<int>("editor.tabSize"));
    }

    [Fact]
    public async Task Change_events_for_language_blocks_are_recognised_by_the_language_aware_check()
    {
        _f.WriteUser("""{ "[csharp]": { "editor.tabSize": 2 } }""");
        using var cfg = _f.Create();
        ConfigurationChangedEventArgs? seen = null;
        using var sub = cfg.Changed.Subscribe(e => seen = e);

        _f.WriteUser("""{ "[csharp]": { "editor.tabSize": 6 } }""");
        await cfg.ReloadAsync(Ct);

        Assert.NotNull(seen);
        Assert.Contains("[csharp].editor.tabSize", seen!.AffectedKeys);
        Assert.False(seen.Affects("editor.tabSize"));                       // the plain check cannot see language blocks
        Assert.True(seen.Affects("editor.tabSize", "csharp"));
        Assert.True(seen.Affects("editor", "csharp"));
        Assert.False(seen.Affects("editor.tabSize", "python"));
        Assert.False(seen.Affects("editor.fontSize", "csharp"));
    }

    [Fact]
    public void The_extension_works_with_any_configuration_service_not_just_ours()
    {
        var cfg = new TestConfigurationService();
        cfg.Set("editor.tabSize", 4);
        cfg.Set("[csharp]", JsonDocument.Parse("""{ "editor": { "tabSize": 2 } }""").RootElement);
        Assert.Equal(2, cfg.Get("editor.tabSize", "csharp", 0));
        Assert.Equal(4, cfg.Get("editor.tabSize", "vb", 0));
    }
}
