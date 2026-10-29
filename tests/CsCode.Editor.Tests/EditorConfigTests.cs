using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using CsCode.Core.Platform;
using CsCode.Editor.Documents;
using CsCode.Editor.Tests.Support;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>.editorconfig: glob semantics, parsing, merging across a directory chain (nearest wins, root stops), caching, and the editor honouring it.</summary>
public sealed class EditorConfigTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    // ---- glob → regex ----

    [Theory]
    // basename patterns match at any depth
    [InlineData("*.cs", "Foo.cs", true)]
    [InlineData("*.cs", "src/deep/Foo.cs", true)]
    [InlineData("*.cs", "Foo.csx", false)]
    [InlineData("*", "anything/at/all.txt", true)]
    [InlineData("Makefile", "sub/Makefile", true)]
    [InlineData("Makefile", "Makefile.am", false)]
    // '*' does not cross directories when the pattern has a slash; '**' does
    [InlineData("src/*.cs", "src/Foo.cs", true)]
    [InlineData("src/*.cs", "src/a/Foo.cs", false)]
    [InlineData("src/**.cs", "src/a/b/Foo.cs", true)]
    [InlineData("/src/*.cs", "src/Foo.cs", true)]
    [InlineData("/src/*.cs", "other/src/Foo.cs", false)]
    // ? and character classes
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("file?.txt", "file12.txt", false)]
    [InlineData("[abc].txt", "b.txt", true)]
    [InlineData("[abc].txt", "d.txt", false)]
    [InlineData("[!abc].txt", "d.txt", true)]
    [InlineData("[!abc].txt", "a.txt", false)]
    // braces: alternation and numeric ranges
    [InlineData("*.{js,ts}", "app.ts", true)]
    [InlineData("*.{js,ts}", "app.css", false)]
    [InlineData("{package.json,.travis.yml}", "package.json", true)]
    [InlineData("{package.json,.travis.yml}", "sub/.travis.yml", true)]
    [InlineData("file{1..3}.txt", "file2.txt", true)]
    [InlineData("file{1..3}.txt", "file4.txt", false)]
    [InlineData("file{3..1}.txt", "file2.txt", true)]
    [InlineData("a{b}c", "a{b}c", true)] // a brace without a comma or a range is literal
    // escaping and regex metacharacters in names
    [InlineData("a.b", "aXb", false)]
    [InlineData("a+b.txt", "a+b.txt", true)]
    [InlineData(@"\*.txt", "*.txt", true)]
    public void Globs_follow_the_editorconfig_rules(string glob, string path, bool matches)
    {
        var regex = new Regex(EditorConfigResolver.GlobToRegex(glob), RegexOptions.CultureInvariant);
        Assert.Equal(matches, regex.IsMatch(path));
    }

    // ---- parsing ----

    private static EditorConfigResolver.Parsed Parse(string text) => EditorConfigResolver.Parse(text, new FileStat(FileType.File, text.Length, DateTimeOffset.UnixEpoch));

    [Fact]
    public void Comments_inline_comments_blank_lines_and_key_case_are_handled()
    {
        var parsed = Parse("""
            # a comment
            ; another comment
            ROOT = TRUE

            [*.cs]
            Indent_Style = Space   # trailing comment
            indent_size = 4 ; also trailing
            end_of_line = CRLF
            garbage line without equals
            """);
        Assert.True(parsed.IsRoot);
        var section = Assert.Single(parsed.Sections);
        Assert.Equal("space", section.Values["indent_style"]);
        Assert.Equal("4", section.Values["indent_size"]);
        Assert.Equal("crlf", section.Values["end_of_line"]);
    }

    [Fact]
    public void Root_is_only_honoured_in_the_preamble_and_an_invalid_glob_matches_nothing()
    {
        var parsed = Parse("[*.cs]\nroot = true\n[[unterminated\nindent_size = 3\n");
        Assert.False(parsed.IsRoot);
        Assert.All(parsed.Sections.Skip(1), s => Assert.DoesNotMatch(s.Pattern, "[unterminated"));
    }

    // ---- resolution over a real directory tree ----

    private async Task<EditorConfigProperties> Resolve(string relativePath)
    {
        var resolver = new EditorConfigResolver(_host.Services.FileSystem);
        return await resolver.ResolveAsync(new Uri(_host.Dir.Combine(relativePath)));
    }

    [Fact]
    public async Task The_nearest_file_wins_and_a_root_file_stops_the_search()
    {
        _host.Dir.WriteFile("outer/.editorconfig", "[*]\nindent_size = 99\nend_of_line = cr\n"); // above the root: must be ignored
        _host.Dir.WriteFile("outer/proj/.editorconfig", "root = true\n[*]\nindent_style = space\nindent_size = 4\ncharset = utf-8\n[*.md]\ntrim_trailing_whitespace = false\n");
        _host.Dir.WriteFile("outer/proj/sub/.editorconfig", "[*.cs]\nindent_size = 8\nend_of_line = crlf\ninsert_final_newline = true\n");
        _host.Dir.WriteFile("outer/proj/sub/Foo.cs", "");
        _host.Dir.WriteFile("outer/proj/Readme.md", "");

        var cs = await Resolve("outer/proj/sub/Foo.cs");
        Assert.Equal("space", cs.IndentStyle);   // from proj/.editorconfig
        Assert.Equal(8, cs.IndentSize);          // sub/ overrides proj/
        Assert.Equal("crlf", cs.EndOfLine);      // sub/ only; outer/ (cr) is beyond the root
        Assert.Equal("utf-8", cs.Charset);
        Assert.True(cs.InsertFinalNewline);
        Assert.Null(cs.TrimTrailingWhitespace);

        var md = await Resolve("outer/proj/Readme.md");
        Assert.Equal(4, md.IndentSize);
        Assert.False(md.TrimTrailingWhitespace);
        Assert.Null(md.EndOfLine);
        Assert.Equal("\r\n", cs.EolString);
    }

    [Fact]
    public async Task Later_sections_win_unset_clears_and_tab_means_tab_width()
    {
        _host.Dir.WriteFile("p/.editorconfig", """
            root = true
            [*]
            indent_style = space
            indent_size = 2
            trim_trailing_whitespace = true
            [*.go]
            indent_style = tab
            indent_size = tab
            tab_width = 8
            [*.go]
            trim_trailing_whitespace = unset
            """);
        _host.Dir.WriteFile("p/main.go", "");
        var go = await Resolve("p/main.go");
        Assert.Equal("tab", go.IndentStyle);
        Assert.Equal(8, go.IndentSize);                 // indent_size = tab → tab_width
        Assert.Equal(8, go.EffectiveIndentSize);
        Assert.Null(go.TrimTrailingWhitespace);         // unset removes the inherited value
        var other = await Resolve("p/main.rs");
        Assert.Equal(2, other.IndentSize);
        Assert.True(other.TrimTrailingWhitespace);
    }

    [Fact]
    public async Task Without_any_editorconfig_or_for_a_non_file_uri_nothing_is_specified()
    {
        _host.Dir.WriteFile("bare/x.txt", "");
        Assert.Equal(EditorConfigProperties.None, await Resolve("bare/x.txt"));
        var resolver = new EditorConfigResolver(_host.Services.FileSystem);
        Assert.Equal(EditorConfigProperties.None, await resolver.ResolveAsync(new Uri("untitled:Untitled-1")));
    }

    [Fact]
    public async Task A_changed_editorconfig_is_picked_up_without_restarting()
    {
        var path = _host.Dir.WriteFile("c/.editorconfig", "root = true\n[*]\nindent_size = 3\n");
        _host.Dir.WriteFile("c/a.txt", "");
        var resolver = new EditorConfigResolver(_host.Services.FileSystem);
        var uri = new Uri(_host.Dir.Combine("c/a.txt"));
        Assert.Equal(3, (await resolver.ResolveAsync(uri)).IndentSize);
        await File.WriteAllTextAsync(path, "root = true\n[*]\nindent_size = 7\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(7, (await resolver.ResolveAsync(uri)).IndentSize);
        File.Delete(path);
        Assert.Null((await resolver.ResolveAsync(uri)).IndentSize);
    }

    // ---- the editor honours it ----

    [AvaloniaFact]
    public async Task Indent_settings_from_editorconfig_beat_user_settings_in_the_view()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.tabSize", 8);
        host.Config.Set("editor.insertSpaces", true);
        host.Dir.WriteFile("proj/.editorconfig", "root = true\n[*.cs]\nindent_style = tab\nindent_size = 2\n");
        var (doc, view, _) = await host.OpenAsync("class A {}\n", "proj/Thing.cs");
        Assert.Equal(2, doc.EditorConfigProperties.EffectiveIndentSize);
        Assert.Equal(2, view.Settings.TabSize);
        Assert.False(view.Settings.InsertSpaces);
        Assert.Equal(2, view.Editor.Options.IndentationSize);
        Assert.False(view.Editor.Options.ConvertTabsToSpaces);
    }
}
