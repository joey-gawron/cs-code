using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using Xunit;

namespace CsCode.Panels.Search.Tests.Engine;

public class FilterTests : IDisposable
{
    private readonly SearchHarness _h = new();

    public FilterTests()
    {
        _h.Write("src/app/Program.cs", "needle");
        _h.Write("src/app/util.md", "needle");
        _h.Write("src/lib/Lib.cs", "needle");
        _h.Write("src/bin/out.cs", "needle");
        _h.Write("docs/readme.md", "needle");
        _h.Write("docs/bin/tool.md", "needle");
        _h.Write("tests/a.test.cs", "needle");
        _h.Write("root.txt", "needle");
    }

    public void Dispose() => _h.Dispose();

    private async Task<IReadOnlyList<string>> Names(SearchQuery q)
    {
        var r = await _h.RunAsync(q);
        var root = _h.Dir.Path.TrimEnd('/') + "/";
        return r.Files.Select(f => f.Uri.LocalPath[root.Length..]).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task Without_filters_every_file_is_searched() =>
        Assert.Equal(8, (await Names(new SearchQuery("needle"))).Count);

    [Fact]
    public async Task Include_extension_glob() =>
        Assert.Equal(["src/app/Program.cs", "src/bin/out.cs", "src/lib/Lib.cs", "tests/a.test.cs"], await Names(new SearchQuery("needle", Include: "*.cs")));

    [Fact]
    public async Task Include_accepts_several_comma_separated_globs() =>
        Assert.Equal(["docs/bin/tool.md", "docs/readme.md", "root.txt", "src/app/util.md"], await Names(new SearchQuery("needle", Include: "*.md, *.txt")));

    [Fact]
    public async Task Include_with_braces_does_not_split_on_the_inner_comma() =>
        Assert.Equal(["docs/bin/tool.md", "docs/readme.md", "root.txt", "src/app/util.md"], await Names(new SearchQuery("needle", Include: "*.{md,txt}")));

    [Fact]
    public async Task A_bare_folder_name_includes_that_folder_at_any_depth() =>
        Assert.Equal(["docs/bin/tool.md", "src/bin/out.cs"], await Names(new SearchQuery("needle", Include: "bin")));

    [Fact]
    public async Task Include_path_glob() =>
        Assert.Equal(["src/app/Program.cs", "src/bin/out.cs", "src/lib/Lib.cs"], await Names(new SearchQuery("needle", Include: "src/**/*.cs")));

    [Fact]
    public async Task Include_a_nested_path_without_wildcards() =>
        Assert.Equal(["src/app/Program.cs", "src/app/util.md"], await Names(new SearchQuery("needle", Include: "src/app")));

    [Fact]
    public async Task Exclude_glob() =>
        Assert.DoesNotContain("src/app/Program.cs", await Names(new SearchQuery("needle", Exclude: "Program.cs")));

    [Fact]
    public async Task Exclude_a_bare_folder_name_excludes_it_at_every_depth()
    {
        var names = await Names(new SearchQuery("needle", Exclude: "bin"));
        Assert.DoesNotContain(names, n => n.Contains("/bin/"));
        Assert.Equal(6, names.Count);
    }

    [Fact]
    public async Task A_dot_slash_pattern_is_anchored_at_the_workspace_folder()
    {
        _h.Write("docs/docs/inner.md", "needle");
        var names = await Names(new SearchQuery("needle", Exclude: "./docs"));
        Assert.DoesNotContain("docs/readme.md", names);
        Assert.DoesNotContain("docs/docs/inner.md", names); // everything below the anchored folder goes
        Assert.Contains("src/app/util.md", names);
    }

    [Fact]
    public async Task A_dot_slash_pattern_does_not_hit_a_same_named_folder_deeper_down()
    {
        _h.Write("src/docs/deep.md", "needle");
        var names = await Names(new SearchQuery("needle", Exclude: "./docs"));
        Assert.Contains("src/docs/deep.md", names);
    }

    [Fact]
    public async Task Include_and_exclude_combine() =>
        Assert.Equal(["src/app/Program.cs", "src/lib/Lib.cs", "tests/a.test.cs"], await Names(new SearchQuery("needle", Include: "*.cs", Exclude: "bin")));

    [Fact]
    public async Task Exclude_with_a_double_star_pattern() =>
        Assert.DoesNotContain("tests/a.test.cs", await Names(new SearchQuery("needle", Exclude: "**/*.test.cs")));

    [Fact]
    public async Task Files_exclude_and_search_exclude_settings_apply_by_default()
    {
        _h.Config.Set("files.exclude", new Dictionary<string, bool> { ["**/bin"] = true });
        _h.Config.Set("search.exclude", new Dictionary<string, bool> { ["**/docs"] = true, ["**/lib"] = false });
        var names = await Names(new SearchQuery("needle"));
        Assert.DoesNotContain(names, n => n.Contains("/bin/") || n.StartsWith("docs/"));
        Assert.Contains("src/lib/Lib.cs", names); // a false entry is not an exclude
    }

    [Fact]
    public async Task Switching_off_use_exclude_settings_searches_the_excluded_folders()
    {
        _h.Config.Set("files.exclude", new Dictionary<string, bool> { ["**/bin"] = true });
        var names = await Names(new SearchQuery("needle", UseExcludeSettings: false));
        Assert.Contains("src/bin/out.cs", names);
        Assert.Equal(8, names.Count);
    }

    [Fact]
    public async Task Roots_restrict_the_search_to_a_folder()
    {
        var names = await Names(new SearchQuery("needle", Roots: [_h.UriOf("docs/")]));
        Assert.Equal(["docs/bin/tool.md", "docs/readme.md"], names);
    }

    [Fact]
    public async Task Every_workspace_folder_is_searched()
    {
        using var other = new CsCode.TestDoubles.TempDirectory();
        other.WriteFile("elsewhere.txt", "needle");
        await _h.Workspace.OpenFolderAsync(other.Uri, TestContext.Current.CancellationToken);
        var r = await _h.RunAsync(new SearchQuery("needle"));
        Assert.Contains(r.Files, f => f.Uri.LocalPath.EndsWith("elsewhere.txt", StringComparison.Ordinal));
        Assert.Equal(9, r.Files.Count);
    }

    [Fact]
    public async Task Include_globs_are_relative_to_each_workspace_folder()
    {
        using var other = new CsCode.TestDoubles.TempDirectory();
        other.WriteFile("src/x.cs", "needle");
        await _h.Workspace.OpenFolderAsync(other.Uri, TestContext.Current.CancellationToken);
        var r = await _h.RunAsync(new SearchQuery("needle", Include: "./src"));
        Assert.Contains(r.Files, f => f.Uri.LocalPath.EndsWith("/src/x.cs", StringComparison.Ordinal));
        Assert.Contains(r.Files, f => f.Uri.LocalPath.EndsWith("/src/lib/Lib.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Files, f => f.Uri.LocalPath.EndsWith("root.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_platform_enumeration_is_used_when_excludes_and_ignore_files_are_on()
    {
        await _h.RunAsync(new SearchQuery("needle"));
        Assert.Equal(1, _h.Workspace.FindFilesCalls);
        Assert.Equal(0, ((DiskFileSystem)_h.Workspace.FileSystem).ReadDirectoryCalls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task The_own_walker_is_used_when_either_toggle_is_off(bool excludes, bool ignore)
    {
        await _h.RunAsync(new SearchQuery("needle", UseExcludeSettings: excludes, UseIgnoreFiles: ignore));
        Assert.Equal(0, _h.Workspace.FindFilesCalls);
        Assert.True(((DiskFileSystem)_h.Workspace.FileSystem).ReadDirectoryCalls > 0);
    }

    [Fact]
    public async Task Whatever_the_platform_enumeration_yields_is_filtered_by_the_query_globs()
    {
        _h.Workspace.Files = [_h.UriOf("src/app/Program.cs"), _h.UriOf("docs/readme.md")];
        Assert.Equal(["src/app/Program.cs"], await Names(new SearchQuery("needle", Include: "*.cs")));
    }

    // ---------------------------------------------------------------------------------- GlobFilter

    [Theory]
    [InlineData("*.cs", "/a/b/c.cs", true)]
    [InlineData("*.cs", "/a/b/c.cs.bak", false)]
    [InlineData("src", "/src/x.cs", true)]
    [InlineData("src", "/a/src/x.cs", true)]
    [InlineData("src", "/a/srcx/x.cs", false)]
    [InlineData("src/", "/a/src/x.cs", true)]
    [InlineData("./src", "/src/x.cs", true)]
    [InlineData("./src", "/a/src/x.cs", false)]
    [InlineData("/src", "/src/x.cs", true)]
    [InlineData("a/b", "/x/a/b/c.cs", true)]
    [InlineData("a/b", "/x/a/bc/c.cs", false)]
    [InlineData("**/*.{cs,md}", "/x/y.md", true)]
    [InlineData("{a,b}/**", "/b/q.txt", true)]
    [InlineData("file?.txt", "/d/file1.txt", true)]
    [InlineData("file?.txt", "/d/file12.txt", false)]
    public void Parse_expands_the_user_syntax(string pattern, string path, bool expected) =>
        Assert.Equal(expected, GlobFilter.Parse(pattern).IsMatch(path));

    [Fact]
    public void Blank_input_means_no_filter()
    {
        Assert.True(GlobFilter.Parse(null).IsEmpty);
        Assert.True(GlobFilter.Parse("  ,  ").IsEmpty);
    }

    [Fact]
    public void Root_set_maps_uris_to_workspace_relative_glob_paths()
    {
        var roots = new RootSet([new Uri("file:///work/proj/"), new Uri("file:///work/other/")]);
        Assert.Equal("/src/a.cs", roots.ToRelative(new Uri("file:///work/proj/src/a.cs")));
        Assert.Equal("/b.cs", roots.ToRelative(new Uri("file:///work/other/b.cs")));
        Assert.Equal("/elsewhere/c.cs", roots.ToRelative(new Uri("file:///elsewhere/c.cs")));
        Assert.True(roots.Contains(new Uri("file:///work/proj/x")));
        Assert.False(roots.Contains(new Uri("file:///work/projx/x")));
    }

    [Fact]
    public void Open_editors_only_searches_exactly_the_open_documents()
    {
        // covered end-to-end in DocumentSearchTests; here: a Uri outside every root keeps its absolute path for glob matching
        Assert.Equal("/tmp/z.cs", new RootSet([new Uri("file:///work/")]).ToRelative(new Uri("file:///tmp/z.cs")));
    }

    [Fact]
    public void Selection_scope_is_a_plain_record()
    {
        var s = new SelectionScope(new Uri("file:///a"), TextRange.FromLines(1, 2, 3, 4));
        Assert.Equal(1, s.Range.Start.Line);
    }
}
