using CsCode.Panels.Search.Engine;
using Xunit;

namespace CsCode.Panels.Search.Tests.Engine;

public class GitIgnoreTests : IDisposable
{
    // The walker handles .gitignore; flipping "use exclude settings" off selects it in the default source.
    private readonly SearchHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task<IReadOnlyList<string>> Names(bool useIgnore = true)
    {
        var r = await _h.RunAsync(new SearchQuery("needle", UseExcludeSettings: false, UseIgnoreFiles: useIgnore));
        var root = _h.Dir.Path.TrimEnd('/') + "/";
        return r.Files.Select(f => f.Uri.LocalPath[root.Length..]).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task Wildcard_rules_ignore_matching_files_at_any_depth()
    {
        _h.Write(".gitignore", "*.log\n");
        _h.Write("a.log", "needle"); _h.Write("sub/b.log", "needle"); _h.Write("sub/keep.txt", "needle");
        var names = await Names();
        Assert.Contains("sub/keep.txt", names);
        Assert.DoesNotContain("a.log", names);
        Assert.DoesNotContain("sub/b.log", names);
    }

    [Fact]
    public async Task A_trailing_slash_rule_ignores_directories_but_not_files_of_that_name()
    {
        _h.Write(".gitignore", "build/\n");
        _h.Write("build/out.txt", "needle"); _h.Write("src/build", "needle"); _h.Write("src/x/build/y.txt", "needle");
        var names = await Names();
        Assert.DoesNotContain("build/out.txt", names);
        Assert.DoesNotContain("src/x/build/y.txt", names);
        Assert.Contains("src/build", names); // a FILE named build is not a directory rule match
    }

    [Fact]
    public async Task Negation_re_includes_a_file_ignored_earlier()
    {
        _h.Write(".gitignore", "*.log\n!important.log\n");
        _h.Write("x.log", "needle"); _h.Write("important.log", "needle");
        var names = await Names();
        Assert.Contains("important.log", names);
        Assert.DoesNotContain("x.log", names);
    }

    [Fact]
    public async Task A_leading_slash_anchors_the_rule_to_the_gitignore_folder()
    {
        _h.Write(".gitignore", "/root-only.txt\n");
        _h.Write("root-only.txt", "needle"); _h.Write("sub/root-only.txt", "needle");
        var names = await Names();
        Assert.DoesNotContain("root-only.txt", names);
        Assert.Contains("sub/root-only.txt", names);
    }

    [Fact]
    public async Task A_rule_with_a_middle_slash_is_anchored_too()
    {
        _h.Write(".gitignore", "docs/generated\n");
        _h.Write("docs/generated/a.txt", "needle"); _h.Write("x/docs/generated/b.txt", "needle");
        var names = await Names();
        Assert.DoesNotContain("docs/generated/a.txt", names);
        Assert.Contains("x/docs/generated/b.txt", names);
    }

    [Fact]
    public async Task A_nested_gitignore_applies_only_to_its_own_subtree()
    {
        _h.Write("sub/.gitignore", "secret.txt\n");
        _h.Write("secret.txt", "needle"); _h.Write("sub/secret.txt", "needle"); _h.Write("sub/deep/secret.txt", "needle");
        var names = await Names();
        Assert.Contains("secret.txt", names);
        Assert.DoesNotContain("sub/secret.txt", names);
        Assert.DoesNotContain("sub/deep/secret.txt", names);
    }

    [Fact]
    public async Task A_deeper_gitignore_can_re_include_what_a_parent_ignored()
    {
        _h.Write(".gitignore", "*.gen\n");
        _h.Write("sub/.gitignore", "!keep.gen\n");
        _h.Write("a.gen", "needle"); _h.Write("sub/keep.gen", "needle"); _h.Write("sub/other.gen", "needle");
        var names = await Names();
        Assert.Contains("sub/keep.gen", names);
        Assert.DoesNotContain("a.gen", names);
        Assert.DoesNotContain("sub/other.gen", names);
    }

    [Fact]
    public async Task Double_star_rules_match_across_directories()
    {
        _h.Write(".gitignore", "**/temp/**\nlogs/**/*.old\n");
        _h.Write("a/temp/x.txt", "needle"); _h.Write("temp/y.txt", "needle"); _h.Write("logs/2020/1.old", "needle"); _h.Write("logs/2020/1.new", "needle");
        var names = await Names();
        Assert.DoesNotContain("a/temp/x.txt", names);
        Assert.DoesNotContain("temp/y.txt", names);
        Assert.DoesNotContain("logs/2020/1.old", names);
        Assert.Contains("logs/2020/1.new", names);
    }

    [Fact]
    public async Task Comments_blank_lines_and_trailing_spaces_are_handled()
    {
        _h.Write(".gitignore", "# a comment\n\nignored.txt   \n\\#hash.txt\n");
        _h.Write("ignored.txt", "needle"); _h.Write("#hash.txt", "needle"); _h.Write("# a comment", "needle");
        var names = await Names();
        Assert.DoesNotContain("ignored.txt", names);
        Assert.DoesNotContain("#hash.txt", names);
        Assert.Contains("# a comment", names);
    }

    [Fact]
    public async Task Switching_off_use_ignore_files_includes_ignored_files()
    {
        _h.Write(".gitignore", "*.log\n");
        _h.Write("a.log", "needle");
        Assert.Contains("a.log", await Names(useIgnore: false));
    }

    [Fact]
    public async Task Ignored_directories_are_never_listed()
    {
        _h.Write(".gitignore", "node_modules/\n");
        for (var i = 0; i < 20; i++) _h.Write($"node_modules/pkg{i}/index.js", "needle");
        _h.Write("src/a.js", "needle");
        await Names();
        var fs = (DiskFileSystem)_h.Workspace.FileSystem;
        Assert.True(fs.ReadDirectoryCalls <= 3, $"walker listed {fs.ReadDirectoryCalls} directories"); // root + src (+ nothing under node_modules)
    }

    [Fact]
    public async Task Version_control_folders_are_always_skipped()
    {
        _h.Write(".git/objects/blob", "needle"); _h.Write(".svn/x", "needle"); _h.Write("a.txt", "needle");
        var names = await Names(useIgnore: false);
        Assert.Equal(["a.txt"], names);
    }

    [Fact]
    public async Task Directory_symlinks_are_not_followed_so_cycles_cannot_loop()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("needs symlinks"); return; }
        _h.Write("real/a.txt", "needle");
        Directory.CreateSymbolicLink(_h.Dir.Combine("loop"), _h.Dir.Path);
        Directory.CreateSymbolicLink(_h.Dir.Combine("alias"), _h.Dir.Combine("real"));
        var names = await Names();
        Assert.Equal(["real/a.txt"], names);
    }

    [Fact]
    public async Task Walk_order_is_deterministic_depth_first_by_name()
    {
        _h.Write("b/2.txt", "needle"); _h.Write("a/1.txt", "needle"); _h.Write("a/0.txt", "needle"); _h.Write("c.txt", "needle");
        var fs = new WalkingFileSource(new DiskFileSystem());
        var req = new FileSourceRequest([_h.Dir.Uri], new RootSet([_h.Dir.Uri]), true, true, GlobFilter.Empty, GlobFilter.Empty, GlobFilter.Empty);
        var uris = new List<string>();
        await foreach (var u in fs.EnumerateAsync(req, TestContext.Current.CancellationToken)) uris.Add(u.LocalPath[(_h.Dir.Path.TrimEnd('/').Length + 1)..]);
        Assert.Equal(["c.txt", "a/0.txt", "a/1.txt", "b/2.txt"], uris);
    }

    // ----------------------------------------------------------------------------- IgnoreRules unit

    [Theory]
    [InlineData("*.log", "a.log", false, true)]
    [InlineData("*.log", "dir/a.log", false, true)]
    [InlineData("*.log", "a.logx", false, null)]
    [InlineData("/a.log", "a.log", false, true)]
    [InlineData("/a.log", "d/a.log", false, null)]
    [InlineData("a/b", "a/b", false, true)]
    [InlineData("a/b", "x/a/b", false, null)]
    [InlineData("build/", "build", true, true)]
    [InlineData("build/", "build", false, null)]
    [InlineData("**/x", "a/b/x", false, true)]
    [InlineData("a/**", "a/b", false, true)]
    [InlineData("a/**/z", "a/z", false, true)]
    [InlineData("a/**/z", "a/b/c/z", false, true)]
    [InlineData("f?o", "foo", false, true)]
    [InlineData("f?o", "fooo", false, null)]
    [InlineData("[abc].txt", "b.txt", false, true)]
    [InlineData("[!abc].txt", "b.txt", false, null)]
    [InlineData("[!abc].txt", "d.txt", false, true)]
    [InlineData("[a-c]x", "bx", false, true)]
    [InlineData("\\*star", "*star", false, true)]
    public void Rules_evaluate_like_git(string rule, string path, bool isDir, bool? expected) =>
        Assert.Equal(expected, IgnoreRules.Parse(rule).Evaluate(path, isDir));

    [Fact]
    public void The_last_matching_rule_wins()
    {
        var rules = IgnoreRules.Parse("*.txt\n!keep.txt\nkeep.txt\n");
        Assert.True(rules.Evaluate("keep.txt", false));
        var rules2 = IgnoreRules.Parse("*.txt\n!keep.txt\n");
        Assert.False(rules2.Evaluate("keep.txt", false));
    }

    [Fact]
    public void An_empty_file_has_no_rules() => Assert.Equal(0, IgnoreRules.Parse("\n# nothing\n").Count);
}
