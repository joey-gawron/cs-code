using System.Text;
using CsCode.Core.Contributions;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Snippets;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>MUST 8: the VS Code snippet grammar, variables, transforms, indentation, tab-stop model, files and the service.</summary>
public class SnippetEngineTests
{
    // ---- compact renderings so expectations stay readable ----

    private static string D(IReadOnlyList<SnippetNode> nodes) => string.Concat(nodes.Select(D));

    private static string D(SnippetNode n) => n switch
    {
        TextNode t => $"[{t.Text}]",
        TabstopNode { Transform: null } t => $"${t.Index}",
        TabstopNode t => $"${{{t.Index}{T(t.Transform!)}}}",
        PlaceholderNode p => $"${{{p.Index}:{D(p.Children)}}}",
        ChoiceNode c => $"${{{c.Index}|{string.Join(',', c.Options)}|}}",
        VariableNode { Default: null, Transform: null } v => $"${v.Name}",
        VariableNode v => $"${{{v.Name}{(v.Default is null ? "" : ":" + D(v.Default))}{(v.Transform is null ? "" : T(v.Transform))}}}",
        _ => "?",
    };

    private static string T(SnippetTransform t) => $"/{t.Pattern}/{string.Concat(t.Format.Select(F))}/{t.Flags}";

    private static string F(FormatNode f) => f switch
    {
        FormatText t => t.Text,
        FormatGroup { Kind: FormatKind.Plain } g => $"${g.Group}",
        FormatGroup { Kind: FormatKind.If } g => $"${{{g.Group}:+{g.IfText}}}",
        FormatGroup { Kind: FormatKind.Else } g => $"${{{g.Group}:-{g.ElseText}}}",
        FormatGroup { Kind: FormatKind.IfElse } g => $"${{{g.Group}:?{g.IfText}:{g.ElseText}}}",
        FormatGroup g => $"${{{g.Group}:/{g.Kind.ToString().ToLowerInvariant()}}}",
        _ => "?",
    };

    private static string Parse(string s) => D(SnippetParser.Parse(s).Nodes);

    private static readonly DateTimeOffset FixedNow = new(2026, 10, 28, 14, 5, 9, TimeSpan.FromHours(2));

    private static SnippetVariableContext Ctx(Action<Dictionary<string, object?>>? _ = null) => new()
    {
        FilePath = "/work/proj/src/My.File.test.ts",
        WorkspaceName = "proj",
        WorkspaceFolder = "/work/proj",
        SelectedText = "sel",
        CurrentLine = "  let x = 1;",
        LineIndex = 6,
        CurrentWord = "wor",
        Clipboard = () => "clip",
        LineComment = "//",
        BlockCommentStart = "/*",
        BlockCommentEnd = "*/",
        CursorIndex = 1,
        Now = FixedNow,
        Random = new Random(7),
        NewGuid = () => Guid.Parse("11111111-2222-3333-4444-555555555555"),
    };

    private static ResolvedSnippet Resolve(string s, SnippetVariableContext? ctx = null, SnippetIndentation? ind = null) =>
        SnippetResolver.Resolve(s, ctx ?? Ctx(), ind);

    private static string Groups(ResolvedSnippet r) =>
        string.Join(" ", r.Groups.Select(g => $"{g.Index}:" + string.Join(",", g.Ranges.Select(x => $"{(x.IsPrimary ? "*" : "")}{x.Start}+{x.Length}"))));

    // ================= parser: text & escapes =================

    [Theory]
    [InlineData("", "")]
    [InlineData("hello", "[hello]")]
    [InlineData("a b\tc", "[a b\tc]")]
    [InlineData("multi\nline", "[multi\nline]")]
    [InlineData(@"\$1", "[$1]")]                  // escaped dollar is text, not a tab stop
    [InlineData(@"\}", "[}]")]
    [InlineData(@"\\", @"[\]")]
    [InlineData(@"a\nb", @"[a\nb]")]              // other backslashes stay as they are
    [InlineData(@"C:\dir", @"[C:\dir]")]
    [InlineData("100$", "[100$]")]                // lone '$' at the end
    [InlineData("a $ b", "[a $ b]")]
    [InlineData("$-1", "[$-1]")]
    [InlineData("$.", "[$.]")]
    public void Text_and_escapes(string input, string expected) => Assert.Equal(expected, Parse(input));

    // ================= parser: tab stops =================

    [Theory]
    [InlineData("$1", "$1")]
    [InlineData("${1}", "$1")]
    [InlineData("$0", "$0")]
    [InlineData("${0}", "$0")]
    [InlineData("$12", "$12")]
    [InlineData("${12}", "$12")]
    [InlineData("a$1b", "[a]$1[b]")]
    [InlineData("$1$2", "$1$2")]
    [InlineData("$1 and $2", "$1[ and ]$2")]
    [InlineData("cost: $5", "[cost: ]$5")]
    public void Tabstops(string input, string expected) => Assert.Equal(expected, Parse(input));

    // ================= parser: placeholders =================

    [Theory]
    [InlineData("${1:foo}", "${1:[foo]}")]
    [InlineData("${1:}", "${1:}")]
    [InlineData("${1:foo bar}", "${1:[foo bar]}")]
    [InlineData("${2:a}${1:b}", "${2:[a]}${1:[b]}")]
    [InlineData("${1:a${2:b}c}", "${1:[a]${2:[b]}[c]}")]                 // nested placeholders
    [InlineData("${1:a${2:b${3:c}}}", "${1:[a]${2:[b]${3:[c]}}}")]
    [InlineData(@"${1:a\}b}", "${1:[a}b]}")]                              // escaped brace inside a placeholder
    [InlineData(@"${1:a\$2}", "${1:[a$2]}")]                              // escaped dollar inside a placeholder
    [InlineData("${1:$2}", "${1:$2}")]                                    // tab stop as the default of another
    [InlineData("${1:$TM_FILENAME}", "${1:$TM_FILENAME}")]                // variable as a default
    [InlineData("${1:x:y}", "${1:[x:y]}")]                                // colons are plain text in a default
    public void Placeholders(string input, string expected) => Assert.Equal(expected, Parse(input));

    // ================= parser: choices =================

    [Theory]
    [InlineData("${1|a,b,c|}", "${1|a,b,c|}")]
    [InlineData("${1|single|}", "${1|single|}")]
    [InlineData(@"${1|a\,b,c\|d|}", "${1|a,b,c|d|}")]                     // escaped comma and pipe stay inside an option
    [InlineData("${1|one two,three|}", "${1|one two,three|}")]
    [InlineData("x${1|a,b|}y", "[x]${1|a,b|}[y]")]
    public void Choices(string input, string expected) => Assert.Equal(expected, Parse(input));

    // ================= parser: variables =================

    [Theory]
    [InlineData("$TM_FILENAME", "$TM_FILENAME")]
    [InlineData("${TM_FILENAME}", "$TM_FILENAME")]
    [InlineData("$name_1", "$name_1")]
    [InlineData("${NAME:fallback}", "${NAME:[fallback]}")]
    [InlineData("${NAME:a${1:b}}", "${NAME:[a]${1:[b]}}")]
    [InlineData("${NAME:}", "${NAME:}")]
    [InlineData("$a-b", "$a[-b]")]                                         // a variable name ends at the first non-name char
    [InlineData("$1abc", "$1[abc]")]                                       // ...but a tab stop number is digits only
    [InlineData("${_x1}", "$_x1")]
    public void Variables(string input, string expected) => Assert.Equal(expected, Parse(input));

    // ================= parser: transforms =================

    [Theory]
    [InlineData("${1/(.*)/$1/}", "${1/(.*)/$1/}")]
    [InlineData("${1/(.*)/${1:/upcase}/g}", "${1/(.*)/${1:/upcase}/g}")]
    [InlineData("${1/a/b/gi}", "${1/a/b/gi}")]
    [InlineData(@"${1/a\/b/x/}", "${1/a/b/x/}")]                          // \/ in the regex is a slash
    [InlineData("${1/(a)|(b)/${1:+A}${2:?B:C}/}", "${1/(a)|(b)/${1:+A}${2:?B:C}/}")]
    [InlineData("${1/(x)/${1:-none}/}", "${1/(x)/${1:-none}/}")]
    [InlineData("${1/(x)/${1:none}/}", "${1/(x)/${1:-none}/}")]           // '-' is optional
    [InlineData(@"${1/x/\$1 literal/}", "${1/x/$1 literal/}")]
    [InlineData("${TM_FILENAME/(.*)\\..+$/$1/}", "${TM_FILENAME/(.*)\\..+$/$1/}")]
    [InlineData("${1/(.)/${1:/downcase}${1:/capitalize}${1:/camelcase}${1:/pascalcase}/}", "${1/(.)/${1:/downcase}${1:/capitalize}${1:/camelcase}${1:/pascalcase}/}")]
    public void Transforms(string input, string expected) => Assert.Equal(expected, Parse(input));

    // ================= parser: malformed input degrades to text =================

    [Theory]
    [InlineData("${1", "[${1]")]
    [InlineData("${1:foo", "[${1:foo]")]
    [InlineData("${", "[${]")]
    [InlineData("${}", "[${}]")]
    [InlineData("${1|a,b}", "[${1|a,b}]")]                                // unterminated choice
    [InlineData("${1/x/y", "[${1/x/y]")]                                  // unterminated transform
    [InlineData("${NAME", "[${NAME]")]
    [InlineData("${1:a${2:b}", "[${1:a]${2:[b]}")]                         // the broken outer one is text, the inner still works
    [InlineData("$}", "[$}]")]
    public void Malformed_syntax_is_kept_as_text_and_never_throws(string input, string expected) => Assert.Equal(expected, Parse(input));

    // ================= resolver: tab stop model =================

    [Fact]
    public void A_snippet_without_tabstops_is_just_text()
    {
        var r = Resolve("plain text");
        Assert.Equal("plain text", r.Text);
        Assert.False(r.HasTabstops);
    }

    [Fact]
    public void Tab_stops_are_visited_in_numeric_order_with_zero_last()
    {
        var r = Resolve("$2 $0 $1");
        Assert.Equal("  ", r.Text);
        Assert.Equal([1, 2, 0], r.Groups.Select(g => g.Index).ToArray());
    }

    [Fact]
    public void An_implicit_final_tabstop_is_added_at_the_end_when_missing()
    {
        var r = Resolve("a $1 b");
        Assert.Equal("a  b", r.Text);
        Assert.Equal([1, 0], r.Groups.Select(g => g.Index).ToArray());
        var final = r.Groups[^1].Ranges.Single();
        Assert.Equal((r.Text.Length, 0), (final.Start, final.Length));
    }

    [Fact]
    public void An_explicit_final_tabstop_is_not_duplicated()
    {
        var r = Resolve("a$0 $1");
        Assert.Equal(2, r.Groups.Count);
        Assert.Equal(0, r.Groups[^1].Index);
        Assert.Equal(1, r.Groups[^1].Ranges.Single().Start);
    }

    [Fact]
    public void Placeholder_defaults_become_text_and_selectable_ranges()
    {
        var r = Resolve("for (${1:i} = 0; ${2:n}; )");
        Assert.Equal("for (i = 0; n; )", r.Text);
        Assert.Equal("1:*5+1 2:*12+1 0:*16+0", Groups(r));
    }

    [Fact]
    public void Mirrors_repeat_the_primary_text_and_the_defining_placeholder_is_primary()
    {
        var r = Resolve("${1:foo} $1 ${1}");
        Assert.Equal("foo foo foo", r.Text);
        Assert.Equal("1:*0+3,4+3,8+3 0:*11+0", Groups(r));
    }

    [Fact]
    public void A_mirror_before_its_placeholder_still_shows_the_default_and_the_placeholder_is_primary()
    {
        var r = Resolve("$1 ${1:bar}");
        Assert.Equal("bar bar", r.Text);
        Assert.Equal("1:0+3,*4+3 0:*7+0", Groups(r));
    }

    [Fact]
    public void Nested_placeholders_have_nested_ranges()
    {
        var r = Resolve("${1:a${2:b}c}");
        Assert.Equal("abc", r.Text);
        Assert.Equal("1:*0+3 2:*1+1 0:*3+0", Groups(r));
    }

    [Fact]
    public void Choices_insert_the_first_option_and_keep_the_list()
    {
        var r = Resolve("x ${1|red,green,blue|} y");
        Assert.Equal("x red y", r.Text);
        Assert.Equal(["red", "green", "blue"], r.Groups[0].Choices);
        Assert.Equal("1:*2+3 0:*7+0", Groups(r));
    }

    [Fact]
    public void A_transformed_mirror_shows_the_transformed_default()
    {
        var r = Resolve("${1:Hello} ${1/(.*)/${1:/upcase}/}");
        Assert.Equal("Hello HELLO", r.Text);
        var mirror = r.Groups[0].Ranges[1];
        Assert.False(mirror.IsPrimary);
        Assert.NotNull(mirror.Transform);
    }

    [Fact]
    public void Escapes_resolve_to_literal_characters_in_the_inserted_text()
    {
        Assert.Equal("$1 } \\", Resolve(@"\$1 \} \\").Text);
    }

    // ================= resolver: variables =================

    [Theory]
    [InlineData("TM_SELECTED_TEXT", "sel")]
    [InlineData("TM_CURRENT_LINE", "  let x = 1;")]
    [InlineData("TM_CURRENT_WORD", "wor")]
    [InlineData("TM_LINE_INDEX", "6")]
    [InlineData("TM_LINE_NUMBER", "7")]
    [InlineData("TM_FILENAME", "My.File.test.ts")]
    [InlineData("TM_FILENAME_BASE", "My.File.test")]
    [InlineData("TM_DIRECTORY", "/work/proj/src")]
    [InlineData("TM_FILEPATH", "/work/proj/src/My.File.test.ts")]
    [InlineData("RELATIVE_FILEPATH", "src/My.File.test.ts")]
    [InlineData("CLIPBOARD", "clip")]
    [InlineData("WORKSPACE_NAME", "proj")]
    [InlineData("WORKSPACE_FOLDER", "/work/proj")]
    [InlineData("CURSOR_INDEX", "1")]
    [InlineData("CURSOR_NUMBER", "2")]
    [InlineData("CURRENT_YEAR", "2026")]
    [InlineData("CURRENT_YEAR_SHORT", "26")]
    [InlineData("CURRENT_MONTH", "10")]
    [InlineData("CURRENT_MONTH_NAME", "October")]
    [InlineData("CURRENT_MONTH_NAME_SHORT", "Oct")]
    [InlineData("CURRENT_DATE", "28")]
    [InlineData("CURRENT_DAY_NAME", "Wednesday")]
    [InlineData("CURRENT_DAY_NAME_SHORT", "Wed")]
    [InlineData("CURRENT_HOUR", "14")]
    [InlineData("CURRENT_MINUTE", "05")]
    [InlineData("CURRENT_SECOND", "09")]
    [InlineData("CURRENT_TIMEZONE_OFFSET", "+02:00")]
    [InlineData("UUID", "11111111-2222-3333-4444-555555555555")]
    [InlineData("BLOCK_COMMENT_START", "/*")]
    [InlineData("BLOCK_COMMENT_END", "*/")]
    [InlineData("LINE_COMMENT", "//")]
    public void Built_in_variables(string name, string expected) => Assert.Equal(expected, Resolve("$" + name).Text);

    [Fact]
    public void Unix_seconds_and_random_variables_have_the_documented_shape()
    {
        Assert.Equal(FixedNow.ToUnixTimeSeconds().ToString(), Resolve("$CURRENT_SECONDS_UNIX").Text);
        Assert.Matches("^[0-9]{6}$", Resolve("$RANDOM").Text);
        Assert.Matches("^[0-9a-f]{6}$", Resolve("$RANDOM_HEX").Text);
    }

    [Fact]
    public void Negative_timezone_offsets_are_formatted_with_a_minus_sign()
    {
        var ctx = new SnippetVariableContext { Now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, new TimeSpan(-7, -30, 0)) };
        Assert.Equal("-07:30", Resolve("$CURRENT_TIMEZONE_OFFSET", ctx).Text);
    }

    [Fact]
    public void Unknown_variables_become_placeholders_named_after_the_variable_with_fresh_indices()
    {
        var r = Resolve("$FOO and ${BAR} then $1");
        Assert.Equal("FOO and BAR then ", r.Text);
        Assert.Equal([1, 2, 3, 0], r.Groups.Select(g => g.Index).OrderBy(i => i == 0 ? int.MaxValue : i).ToArray());
        // $1 existed, so the generated indices are 2 and 3
        Assert.Equal(0, r.Groups.First(g => g.Index == 2).Ranges[0].Start);
        Assert.Equal(3, r.Groups.First(g => g.Index == 2).Ranges[0].Length);
    }

    [Fact]
    public void A_known_variable_without_a_value_uses_its_default_or_nothing()
    {
        var none = new SnippetVariableContext(); // no file path
        Assert.Equal("untitled", Resolve("${TM_FILENAME:untitled}", none).Text);
        Assert.Equal("[]", Resolve("[$TM_FILENAME]", none).Text);       // known but unavailable: empty, NOT a placeholder
        Assert.False(Resolve("$TM_FILENAME", none).HasTabstops);
    }

    [Fact]
    public void An_empty_variable_value_falls_back_to_the_default()
    {
        var ctx = new SnippetVariableContext { SelectedText = "" };
        Assert.Equal("fallback", Resolve("${TM_SELECTED_TEXT:fallback}", ctx).Text);
        Assert.Equal("picked", Resolve("${TM_SELECTED_TEXT:fallback}", new SnippetVariableContext { SelectedText = "picked" }).Text);
    }

    [Fact]
    public void Variable_defaults_may_contain_placeholders()
    {
        var r = Resolve("${TM_FILENAME:a${1:b}}", new SnippetVariableContext()); // no file name => the default, which holds a real placeholder
        Assert.Equal("ab", r.Text);
        Assert.Equal("1:*1+1 0:*2+0", Groups(r));
    }

    [Fact]
    public void Variable_transforms_apply_to_the_variable_value()
    {
        Assert.Equal("My.File.test", Resolve("${TM_FILENAME/(.*)\\.ts$/$1/}").Text);
        Assert.Equal("MyFileTest", Resolve("${TM_FILENAME_BASE/(.*)/${1:/pascalcase}/}").Text);
        Assert.Equal("MY.FILE.TEST", Resolve("${TM_FILENAME_BASE/(.*)/${1:/upcase}/}").Text);
    }

    // ================= transforms =================

    private static string Tr(string input, string transformText)
    {
        var node = (TabstopNode)SnippetParser.Parse("${1" + transformText + "}").Nodes.Single();
        return SnippetTransforms.Apply(input, node.Transform!);
    }

    [Theory]
    [InlineData("foo_bar-baz", "/(.*)/${1:/upcase}/", "FOO_BAR-BAZ")]
    [InlineData("FOO", "/(.*)/${1:/downcase}/", "foo")]
    [InlineData("foo bar", "/(.*)/${1:/capitalize}/", "Foo bar")]
    [InlineData("foo_bar-baz", "/(.*)/${1:/camelcase}/", "fooBarBaz")]
    [InlineData("foo_bar-baz", "/(.*)/${1:/pascalcase}/", "FooBarBaz")]
    [InlineData("Foo Bar", "/(.*)/${1:/camelcase}/", "fooBar")]
    [InlineData("a", "/(a)/${1:+yes}/", "yes")]
    [InlineData("b", "/(a)?b/${1:+yes}/", "")]                    // group did not participate: no 'if' text
    [InlineData("b", "/(a)?b/${1:-no}/", "no")]
    [InlineData("ab", "/(a)?b/${1:-no}/", "a")]                   // else form keeps the group when present
    [InlineData("ab", "/(a)?b/${1:?yes:no}/", "yes")]
    [InlineData("b", "/(a)?b/${1:?yes:no}/", "no")]
    [InlineData("abc", "/(b)/[$1]/", "a[b]c")]
    [InlineData("a.b.c", "/\\./_/g", "a_b_c")]
    [InlineData("a.b.c", "/\\./_/", "a_b.c")]                    // no 'g': only the first match
    [InlineData("ABC", "/b/x/i", "AxC")]
    [InlineData("nomatch", "/zzz/x/", "nomatch")]
    [InlineData("x", "/(x)/$1$1$2/", "xx")]                       // a group that does not exist is empty
    [InlineData("abc", "/(?<broken/x/", "abc")]                   // invalid regex leaves the text alone
    [InlineData("hello world", "/(\\w+) (\\w+)/$2 $1/", "world hello")]
    public void Transform_application(string input, string transform, string expected) => Assert.Equal(expected, Tr(input, transform));

    // ================= indentation =================

    [Fact]
    public void Lines_after_the_first_get_the_base_indent()
    {
        var r = Resolve("if ($1) {\n\t$0\n}", ind: new SnippetIndentation(BaseIndent: "    ", TabSize: 4, InsertSpaces: true));
        Assert.Equal("if () {\n        \n    }", r.Text);
        // the ranges still point at the right places in the re-indented text: $1 between the parens, $0 after the 8 spaces on line 2
        Assert.Equal(4, r.Groups[0].Ranges[0].Start);
        Assert.Equal(16, r.Groups[1].Ranges[0].Start);
    }

    [Fact]
    public void Tabs_in_the_snippet_become_spaces_when_the_editor_inserts_spaces()
    {
        var r = Resolve("a\n\tb\n\t\tc", ind: new SnippetIndentation("", 2, true));
        Assert.Equal("a\n  b\n    c", r.Text);
    }

    [Fact]
    public void Spaces_in_the_snippet_become_tabs_when_the_editor_uses_tabs()
    {
        var r = Resolve("a\n    b\n      c", ind: new SnippetIndentation("\t", 4, false));
        // 4 spaces = 1 tab (+ the base tab); 6 spaces = 1 tab + 2 spaces (+ the base tab)
        Assert.Equal("a\n\t\tb\n\t\t  c", r.Text);
    }

    [Fact]
    public void Line_endings_are_normalised_to_the_documents()
    {
        Assert.Equal("a\r\nb\r\nc", Resolve("a\nb\r\nc", ind: new SnippetIndentation(Eol: "\r\n")).Text);
        Assert.Equal("a\nb\nc", Resolve("a\r\nb\rc", ind: new SnippetIndentation(Eol: "\n")).Text);
    }

    [Fact]
    public void Multi_line_variable_values_are_indented_too()
    {
        var ctx = new SnippetVariableContext { SelectedText = "x\ny" };
        var r = Resolve("{ $TM_SELECTED_TEXT }", ctx, new SnippetIndentation("  "));
        Assert.Equal("{ x\n  y }", r.Text);
    }

    // ================= snippet files =================

    [Fact]
    public void Snippet_files_accept_string_and_array_bodies_prefix_arrays_scope_comments_and_trailing_commas()
    {
        var defs = SnippetFileParser.Parse("""
            {
              // a comment
              "Log": { "prefix": ["log", "cl"], "body": ["console.log($1);", "$0"], "description": "Log it", },
              "Single": { "prefix": "s", "body": "one line" },
              "Global": { "prefix": "g", "body": "x", "scope": "javascript, typescript" },
              "No body": { "prefix": "nb" },
              "Bad body": { "prefix": "bb", "body": 5 },
              "No prefix": { "body": "text" },
            }
            """);
        Assert.Equal(4, defs.Count);
        Assert.Equal(["log", "cl"], defs[0].Prefixes);
        Assert.Equal("console.log($1);\n$0", defs[0].Body);
        Assert.Equal("Log it", defs[0].Description);
        Assert.Equal("one line", defs[1].Body);
        Assert.Equal(["javascript", "typescript"], defs[2].Languages);
        Assert.Empty(defs[3].Prefixes);
    }

    [Fact]
    public void Invalid_snippet_json_throws_a_json_exception()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => SnippetFileParser.Parse("{ nope"));
    }

    [Fact]
    public void Real_vscode_builtin_snippet_files_all_parse_and_every_body_resolves()
    {
        var count = 0;
        foreach (var file in CsCode.Editor.Languages.BuiltinLanguages.Catalog.Snippets)
        {
            var defs = SnippetFileParser.Parse(CsCode.Editor.Languages.BuiltinLanguages.ReadText(file.ResourceName)!);
            foreach (var d in defs)
            {
                var r = SnippetResolver.Resolve(d.Body, new SnippetVariableContext { Now = FixedNow });
                Assert.NotNull(r.Text); // resolution of every real-world body must not throw
                count++;
            }
        }
        Assert.True(count > 40, $"only {count} real snippets exercised");
    }

    // ================= service / completion =================

    [Fact]
    public void Service_serves_builtin_snippets_per_language()
    {
        using var host = TestHost.Create(new ThreadAffinityUiThread());
        using var svc = new SnippetService(host.Services);
        Assert.Contains(svc.GetSnippets("csharp"), s => s.Prefixes.Contains("#region"));
        Assert.Empty(svc.GetSnippets("no-such-language"));
    }

    [Fact]
    public void Service_loads_user_files_off_thread_merges_them_and_filters_global_scopes()
    {
        using var host = TestHost.Create(new ThreadAffinityUiThread());
        using var svc = new SnippetService(host.Services);
        var changed = new List<string>();
        svc.Changed.Subscribe(changed.Add);
        var perLang = host.Dir.WriteFile("snippets/csharp.json", """{ "Mine": { "prefix": "mine", "body": "Mine($1)" } }""");
        var global = host.Dir.WriteFile("snippets/g.code-snippets", """{ "G1": { "prefix": "g1", "body": "g", "scope": "csharp" }, "G2": { "prefix": "g2", "body": "g", "scope": "python" }, "G3": { "prefix": "g3", "body": "g" } }""");
        var broken = host.Dir.WriteFile("snippets/broken.json", "{ nope");
        host.Services.Contributions.Contribute(ContributionSources.User, host.Dir.Combine("snippets"), new[]
        {
            new SnippetContribution("csharp", perLang), new SnippetContribution("*", global), new SnippetContribution("python", broken),
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!svc.GetSnippets("csharp").Any(s => s.Name == "Mine") && sw.ElapsedMilliseconds < 5000) Thread.Sleep(10);
        var cs = svc.GetSnippets("csharp").Select(s => s.Name).ToArray();
        Assert.Contains("Mine", cs);
        Assert.Contains("G1", cs);      // global snippet scoped to csharp
        Assert.Contains("G3", cs);      // global snippet without scope applies everywhere
        Assert.DoesNotContain("G2", cs);
        Assert.Contains("#region", svc.GetSnippets("csharp").SelectMany(s => s.Prefixes)); // built-ins stay
        Assert.Contains("csharp", changed);
        Assert.Contains("Mine", svc.GetSnippets("csharp").Select(s => s.Name));
        Assert.DoesNotContain(svc.GetSnippets("python"), s => s.Name == "Mine");
    }

    [Fact]
    public async Task Completion_provider_offers_snippet_items_in_snippet_format_and_caches_the_list()
    {
        using var host = TestHost.Create(new ThreadAffinityUiThread());
        using var svc = new SnippetService(host.Services);
        var provider = new SnippetCompletionProvider(svc);
        var doc = new InMemoryTextDocument(new Uri("file:///a.cs"), "csharp", "#reg");
        var list = await provider.ProvideCompletionAsync(doc, new TextPosition(0, 4), new CompletionContext(CompletionTriggerKind.Invoked), CancellationToken.None);
        Assert.NotNull(list);
        var region = list!.Items.First(i => i.Label == "#region");
        Assert.Equal(CompletionItemKind.Snippet, region.Kind);
        Assert.Equal(InsertTextFormat.Snippet, region.InsertTextFormat);
        Assert.Equal("#region $0", region.InsertText);
        Assert.Equal("#region", region.FilterText);
        var again = await provider.ProvideCompletionAsync(doc, new TextPosition(0, 4), new CompletionContext(CompletionTriggerKind.Invoked), CancellationToken.None);
        Assert.Same(list, again);
        var none = await provider.ProvideCompletionAsync(new InMemoryTextDocument(new Uri("file:///a.zzz"), "no-such", ""), TextPosition.Zero, new CompletionContext(CompletionTriggerKind.Invoked), CancellationToken.None);
        Assert.Null(none);
    }

    [Fact]
    public void Resolving_a_large_snippet_is_fast_and_linear()
    {
        var body = new StringBuilder();
        for (var i = 1; i <= 2000; i++) body.Append($"line ${{{i}:value{i}}} $TM_FILENAME\n\t");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Resolve(body.ToString());
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(2001, r.Groups.Count);
    }
}
