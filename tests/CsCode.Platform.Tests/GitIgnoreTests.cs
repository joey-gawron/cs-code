using CsCode.Platform.Workspace;
using Xunit;

namespace CsCode.Platform.Tests;

/// <summary>Golden tests for the gitignore engine: each row is (rules, path, isDirectory, expected verdict).</summary>
public class GitIgnoreTests
{
    private static bool Ignored(string rules, string path, bool isDir = false)
    {
        var file = GitIgnoreFile.Parse(rules);
        return IgnoreChain.IsIgnored(new IgnoreChain(file, "", null), path, isDir);
    }

    [Theory]
    // simple names and wildcards
    [InlineData("*.log", "a.log", false, true)]
    [InlineData("*.log", "deep/er/b.log", false, true)]
    [InlineData("*.log", "a.logx", false, false)]
    [InlineData("*.log", "alog", false, false)]
    [InlineData("secret.txt", "secret.txt", false, true)]
    [InlineData("secret.txt", "a/b/secret.txt", false, true)]
    [InlineData("secret.txt", "a/notsecret.txt", false, false)]
    [InlineData("secret.txt", "secret.txt.bak", false, false)]
    // anchoring
    [InlineData("/build", "build", true, true)]
    [InlineData("/build", "build", false, true)]
    [InlineData("/build", "src/build", true, false)]
    [InlineData("doc/frotz", "doc/frotz", false, true)]       // a slash in the middle anchors it
    [InlineData("doc/frotz", "a/doc/frotz", false, false)]
    [InlineData("/doc/frotz", "doc/frotz", false, true)]
    // directory-only
    [InlineData("build/", "build", true, true)]
    [InlineData("build/", "src/build", true, true)]
    [InlineData("build/", "build", false, false)]             // a FILE called build is not matched
    [InlineData("node_modules/", "a/b/node_modules", true, true)]
    [InlineData("/out/", "out", true, true)]
    [InlineData("/out/", "x/out", true, false)]
    // star and question mark never cross a slash
    [InlineData("a/*/b", "a/x/b", false, true)]
    [InlineData("a/*/b", "a/x/y/b", false, false)]
    [InlineData("a/*", "a/b", false, true)]
    [InlineData("a/*", "a/b/c", false, false)]                // children of a/b are not matched by a/* (the walker never gets there anyway)
    [InlineData("a?c", "abc", false, true)]
    [InlineData("a?c", "ac", false, false)]
    [InlineData("a?c", "abbc", false, false)]
    [InlineData("a?c", "a/c", false, false)]
    [InlineData("*.tmp", "dir.tmp", true, true)]              // no trailing slash: directories match too
    // double star
    [InlineData("**/foo", "foo", false, true)]
    [InlineData("**/foo", "a/foo", false, true)]
    [InlineData("**/foo", "a/b/foo", false, true)]
    [InlineData("**/foo", "a/foobar", false, false)]
    [InlineData("foo/**/bar", "foo/bar", false, true)]
    [InlineData("foo/**/bar", "foo/a/bar", false, true)]
    [InlineData("foo/**/bar", "foo/a/b/bar", false, true)]
    [InlineData("foo/**/bar", "foo/barx", false, false)]
    [InlineData("foo/**/bar", "x/foo/bar", false, false)]
    [InlineData("abc/**", "abc/x", false, true)]
    [InlineData("abc/**", "abc/x/y", false, true)]
    [InlineData("abc/**", "abc", true, false)]                // everything INSIDE, not the directory itself
    [InlineData("a**b", "axxb", false, true)]                 // "**" elsewhere behaves like "*"
    [InlineData("a**b", "a/b", false, false)]
    // character classes
    [InlineData("[abc].txt", "a.txt", false, true)]
    [InlineData("[abc].txt", "d.txt", false, false)]
    [InlineData("file[0-9]", "file7", false, true)]
    [InlineData("file[0-9]", "filex", false, false)]
    [InlineData("[!a]x", "bx", false, true)]
    [InlineData("[!a]x", "ax", false, false)]
    [InlineData("[^a]x", "bx", false, true)]
    [InlineData("[!a]x", "/x", false, false)]
    [InlineData("a[]]b", "a]b", false, true)]                 // ']' first in a class is a literal member
    [InlineData("a[b", "a[b", false, true)]                   // no closing bracket: a literal '['
    // escapes, comments, blanks, trailing spaces
    [InlineData("\\#file", "#file", false, true)]
    [InlineData("#file", "#file", false, false)]               // a comment
    [InlineData("\\!important", "!important", false, true)]
    [InlineData("a\\ ", "a ", false, true)]                    // an escaped trailing space is significant
    [InlineData("a   ", "a", false, true)]                     // unescaped trailing spaces are not
    [InlineData("\n\n   \n# only comments\n", "anything", false, false)]
    [InlineData("a\\*b", "a*b", false, true)]
    [InlineData("a\\*b", "axb", false, false)]
    // unicode and odd names
    [InlineData("日本語/", "日本語", true, true)]
    [InlineData("ünï*", "ünïcode.txt", false, true)]
    [InlineData("*.c++", "x.c++", false, true)]
    [InlineData("a.b", "aXb", false, false)]                   // '.' is literal
    public void Single_file_rules(string rules, string path, bool isDir, bool expected) =>
        Assert.Equal(expected, Ignored(rules, path, isDir));

    [Theory]
    [InlineData("*.log\n!keep.log", "a.log", true)]
    [InlineData("*.log\n!keep.log", "keep.log", false)]
    [InlineData("*.log\n!keep.log", "dir/keep.log", false)]
    [InlineData("!a.txt\n*.txt", "a.txt", true)]               // the LAST matching rule wins
    [InlineData("*.txt\n!a.txt\n*.txt", "a.txt", true)]
    [InlineData("*\n!*.cs", "x.cs", false)]
    [InlineData("*\n!*.cs", "x.txt", true)]
    [InlineData("a\n!a\na\n!a", "a", false)]
    public void Negations_and_ordering(string rules, string path, bool expected) =>
        Assert.Equal(expected, Ignored(rules, path));

    [Fact]
    public void Windows_line_endings_are_tolerated()
    {
        Assert.True(Ignored("*.log\r\nbuild/\r\n", "x.log"));
        Assert.True(Ignored("*.log\r\nbuild/\r\n", "build", isDir: true));
        Assert.False(Ignored("*.log\r\n!x.log\r\n", "x.log"));
    }

    [Fact]
    public void With_ignore_case_rules_match_whatever_the_case_and_without_it_only_the_same_case()
    {
        // files-10: git on a Mac (core.ignorecase=true) ignores Build/ and DEBUG.LOG for "build/" and "*.log"
        static bool Folded(string rules, string path, bool isDir = false) =>
            IgnoreChain.IsIgnored(new IgnoreChain(GitIgnoreFile.Parse(rules, ignoreCase: true), "", null), path, isDir);
        Assert.True(Folded("build/", "Build", isDir: true));
        Assert.True(Folded("*.log", "src/DEBUG.LOG"));
        Assert.True(Folded("/Docs/[A-C]*.md", "docs/b.MD"));
        Assert.True(Folded("SECRET.txt", "a/secret.TXT"));
        Assert.False(Folded("*.log\n!Keep.log", "keep.LOG"));
        Assert.True(Folded("*.log", new string('A', 600) + ".LOG")); // longer than the stack buffer
        Assert.False(Ignored("build/", "Build", isDir: true));
        Assert.False(Ignored("*.log", "src/DEBUG.LOG"));
        Assert.False(Ignored("SECRET.txt", "a/secret.TXT"));
    }

    [Theory]
    [InlineData("[core]\n\tignorecase = true\n", true)]
    [InlineData("[core]\n\trepositoryformatversion = 0\n\tignoreCase = false\n", false)]
    [InlineData("[Core]\n\tIgnoreCase\n", true)]                                   // a bare key is true
    [InlineData("[core]\n\tignorecase = yes ; set by git init\n", true)]
    [InlineData("[core]\n\tignorecase = true\n[core]\n\tignorecase = off\n", false)] // the last one wins
    [InlineData("[remote \"origin\"]\n\tignorecase = true\n", null)]               // another section
    [InlineData("[core]\n\tbare = false\n", null)]
    [InlineData("", null)]
    public void Core_ignorecase_is_read_from_a_git_config(string config, bool? expected) =>
        Assert.Equal(expected, CsCode.Core.Text.GitIgnoreCase.Parse(config));

    [Fact]
    public void A_repository_without_core_ignorecase_and_a_folder_outside_any_follow_the_platform()
    {
        using var temp = new CsCode.TestDoubles.TempDirectory("cscode-ignorecase-");
        var repo = temp.Combine("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        File.WriteAllText(Path.Combine(repo, ".git", "config"), "[core]\n\tbare = false\n");
        Assert.Equal(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), GitIgnoreFile.RepositoryIgnoresCase(Path.Combine(repo, "src")));

        File.WriteAllText(Path.Combine(repo, ".git", "config"), "[core]\n\tignorecase = true\n");
        Assert.True(GitIgnoreFile.RepositoryIgnoresCase(Path.Combine(repo, "src")));      // looked up from a subfolder
        File.WriteAllText(Path.Combine(repo, ".git", "config"), "[core]\n\tignorecase = false\n");
        Assert.False(GitIgnoreFile.RepositoryIgnoresCase(repo));

        // a linked worktree's .git file names its git directory; the config is the main repository's (commondir)
        var linked = Path.Combine(repo, ".git", "worktrees", "wt");
        Directory.CreateDirectory(linked);
        File.WriteAllText(Path.Combine(linked, "commondir"), "../..\n");
        var wt = temp.Combine("wt");
        Directory.CreateDirectory(wt);
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: " + linked + "\n");
        File.WriteAllText(Path.Combine(repo, ".git", "config"), "[core]\n\tignorecase = true\n");
        Assert.True(GitIgnoreFile.RepositoryIgnoresCase(wt));
    }

    [Fact]
    public void Malformed_lines_are_skipped_not_fatal()
    {
        var file = GitIgnoreFile.Parse("*.log\n[\n\\\n!\n/\n");
        Assert.True(file.RuleCount >= 1);
        Assert.True(IgnoreChain.IsIgnored(new IgnoreChain(file, "", null), "a.log", false));
    }

    [Fact]
    public void A_pattern_that_looks_like_a_regex_bomb_cannot_hang_matching()
    {
        var evil = string.Concat(Enumerable.Repeat("a*", 40)) + "b";
        var text = new string('a', 5000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(Ignored(evil, text));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"matching took {sw.Elapsed.TotalMilliseconds:F0} ms");
    }

    // ---- nested files ------------------------------------------------------------------------------------------------------------

    private static bool Nested(string rootRules, string subRules, string subDir, string path, bool isDir = false)
    {
        var root = new IgnoreChain(GitIgnoreFile.Parse(rootRules), "", null);
        var sub = new IgnoreChain(GitIgnoreFile.Parse(subRules), subDir, root);
        return IgnoreChain.IsIgnored(sub, path, isDir);
    }

    [Fact]
    public void A_nested_ignore_file_overrides_its_parent_within_its_own_directory()
    {
        // root: ignore every .log; sub/: un-ignore important.log
        Assert.True(Nested("*.log", "!important.log", "sub", "other.log"));
        Assert.False(Nested("*.log", "!important.log", "sub", "sub/important.log"));
        Assert.True(Nested("*.log", "!important.log", "sub", "sub/other.log"));          // still ignored by the parent
        Assert.False(Nested("*.log", "!important.log", "sub", "sub/deeper/important.log"));
    }

    [Fact]
    public void Patterns_in_a_nested_file_are_relative_to_that_files_directory()
    {
        // sub/.gitignore: "/local" and "gen/*.cs" are anchored at sub/, not at the repository root
        Assert.True(Nested("", "/local", "sub", "sub/local"));
        Assert.False(Nested("", "/local", "sub", "sub/x/local"));
        Assert.False(Nested("", "/local", "sub", "local"));
        Assert.True(Nested("", "gen/*.cs", "sub", "sub/gen/a.cs"));
        Assert.False(Nested("", "gen/*.cs", "sub", "gen/a.cs"));
    }

    [Fact]
    public void A_nested_file_with_no_opinion_defers_to_its_parent()
    {
        Assert.True(Nested("*.tmp", "*.bak", "sub", "sub/x.tmp"));
        Assert.True(Nested("*.tmp", "*.bak", "sub", "sub/x.bak"));
        Assert.False(Nested("*.tmp", "*.bak", "sub", "sub/x.txt"));
    }

    [Fact]
    public void An_info_exclude_file_sits_below_the_root_gitignore()
    {
        var exclude = new IgnoreChain(GitIgnoreFile.Parse("*.secret\n"), "", null);
        var gitignore = new IgnoreChain(GitIgnoreFile.Parse("!allowed.secret\n"), "", exclude);
        Assert.True(IgnoreChain.IsIgnored(gitignore, "x.secret", false));
        Assert.False(IgnoreChain.IsIgnored(gitignore, "allowed.secret", false));   // .gitignore is consulted first
    }

    [Fact]
    public void Matching_many_paths_against_many_rules_is_fast()
    {
        var rules = string.Join('\n', Enumerable.Range(0, 300).Select(i => $"/generated{i}/\n*.ext{i}\ndir{i}/sub/*.tmp"));
        var chain = new IgnoreChain(GitIgnoreFile.Parse(rules), "", null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hits = 0;
        for (var i = 0; i < 20_000; i++)
            if (IgnoreChain.IsIgnored(chain, $"src/module{i % 50}/file{i}.cs", false)) hits++;
        sw.Stop();
        Assert.Equal(0, hits);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"20k paths x 900 rules took {sw.Elapsed.TotalSeconds:F1} s");
    }
}
