using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Platform.Configuration;
using Xunit;

namespace CsCode.Platform.Tests;

/// <summary>Golden before/after tests: every assertion is on the exact resulting text, because "preserves comments and formatting" is the whole point.</summary>
public class JsoncEditorTests
{
    private static JsonNode? J(object? v) => v is null ? null : JsonSerializer.SerializeToNode(v);
    private static string Set(string text, string key, object? value) => JsoncEditor.Set(text, key, J(value));

    // ---- replacing ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Replaces_only_the_value_span_of_an_existing_flat_key()
    {
        var before = """
            {
                // the font
                "editor.fontSize": 14, // keep this comment
                "editor.tabSize": 4
            }
            """;
        var after = """
            {
                // the font
                "editor.fontSize": 16, // keep this comment
                "editor.tabSize": 4
            }
            """;
        Assert.Equal(after, Set(before, "editor.fontSize", 16));
    }

    [Fact]
    public void Replaces_a_value_inside_a_nested_object_addressed_by_a_dotted_key()
    {
        var before = """
            {
              "editor": {
                "fontSize": 14, /* inline */
                "tabSize": 4
              }
            }
            """;
        var after = """
            {
              "editor": {
                "fontSize": 20, /* inline */
                "tabSize": 4
              }
            }
            """;
        Assert.Equal(after, Set(before, "editor.fontSize", 20));
    }

    [Fact]
    public void Prefers_the_flat_dotted_member_when_it_exists_alongside_a_nested_parent()
    {
        var before = """{ "editor.tabSize": 2, "editor": { "fontSize": 14 } }""";
        Assert.Equal("""{ "editor.tabSize": 8, "editor": { "fontSize": 14 } }""", Set(before, "editor.tabSize", 8));
    }

    [Fact]
    public void Replaces_the_last_of_duplicate_keys_because_the_last_one_wins()
    {
        var before = """{ "a": 1, "a": 2 }""";
        Assert.Equal("""{ "a": 1, "a": 3 }""", Set(before, "a", 3));
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    [InlineData(42, "42")]
    [InlineData(3.5, "3.5")]
    [InlineData("plain", "\"plain\"")]
    [InlineData("quote\"back\\slash", "\"quote\\\"back\\\\slash\"")]
    [InlineData("Cascadia Code, 'Courier New', monospace", "\"Cascadia Code, 'Courier New', monospace\"")]
    [InlineData("日本語 é", "\"日本語 é\"")]   // not turned into \uXXXX noise
    public void Scalar_values_are_written_compactly_and_readably(object value, string expectedJson)
    {
        Assert.Equal($$"""{ "k": {{expectedJson}} }""", Set("""{ "k": 0 }""", "k", value));
    }

    [Fact]
    public void Replacing_with_an_object_pretty_prints_it_at_the_members_indentation()
    {
        var before = """
            {
                "files.exclude": {"x": true},
                "other": 1
            }
            """;
        var after = """
            {
                "files.exclude": {
                    "**/.git": true,
                    "**/*.tmp": false
                },
                "other": 1
            }
            """;
        Assert.Equal(after, Set(before, "files.exclude", new Dictionary<string, bool> { ["**/.git"] = true, ["**/*.tmp"] = false }));
    }

    [Fact]
    public void Short_scalar_arrays_stay_on_one_line_and_long_ones_wrap()
    {
        Assert.Equal("""{ "r": [80, 120] }""", Set("""{ "r": [] }""", "r", new[] { 80, 120 }));
        var long1 = Enumerable.Range(0, 30).Select(i => "item-number-" + i).ToArray();
        var result = Set("""{ "r": 0 }""", "r", long1);
        Assert.Contains("\n", result);
        Assert.Equal(30, JsonNode.Parse(result)!["r"]!.AsArray().Count);
    }

    // ---- inserting ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Appends_a_new_member_after_the_last_one_adding_the_missing_comma_before_a_trailing_comment()
    {
        var before = """
            {
                "a": 1 // last one
            }
            """;
        var after = """
            {
                "a": 1, // last one
                "b": 2
            }
            """;
        Assert.Equal(after, Set(before, "b", 2));
    }

    [Fact]
    public void A_file_with_trailing_commas_keeps_its_style()
    {
        var before = """
            {
                "a": 1,
                "b": 2,
            }
            """;
        var after = """
            {
                "a": 1,
                "b": 2,
                "c": 3,
            }
            """;
        Assert.Equal(after, Set(before, "c", 3));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ }")]
    [InlineData("{\n}")]
    [InlineData("{\n\n}")]
    public void Inserting_into_an_empty_object_creates_an_indented_member(string before)
    {
        Assert.Equal("{\n    \"editor.fontSize\": 15\n}", Set(before, "editor.fontSize", 15));
    }

    [Fact]
    public void Inserting_into_an_object_that_only_holds_a_comment_keeps_the_comment()
    {
        var before = "{\n    // add your settings here\n}";
        var after = "{\n    // add your settings here\n    \"k\": 1\n}";
        Assert.Equal(after, Set(before, "k", 1));
    }

    [Fact]
    public void A_missing_or_blank_file_becomes_a_new_document()
    {
        Assert.Equal("{\n    \"editor.fontSize\": 18\n}\n", Set("", "editor.fontSize", 18));
        Assert.Equal("{\n    \"editor.fontSize\": 18\n}\n", Set("  \n\n", "editor.fontSize", 18));
    }

    [Fact]
    public void A_file_with_only_comments_keeps_them_and_gains_the_object()
    {
        var before = "// my settings\n// more notes\n";
        Assert.Equal("// my settings\n// more notes\n{\n    \"k\": true\n}\n", Set(before, "k", true));
    }

    [Fact]
    public void Matches_the_files_own_indentation_unit_for_tabs_and_for_two_spaces()
    {
        Assert.Equal("{\n\t\"a\": 1,\n\t\"b\": 2\n}", Set("{\n\t\"a\": 1\n}", "b", 2));
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": 2\n}", Set("{\n  \"a\": 1\n}", "b", 2));
    }

    [Fact]
    public void Single_line_objects_stay_single_line()
    {
        Assert.Equal("""{"a": 1, "b": 2}""", Set("""{"a": 1}""", "b", 2));
        Assert.Equal("""{"a": 1, "b": 2,}""", Set("""{"a": 1,}""", "b", 2));
    }

    [Fact]
    public void Uses_crlf_line_endings_when_the_file_does()
    {
        var before = "{\r\n    \"a\": 1\r\n}\r\n";
        var after = "{\r\n    \"a\": 1,\r\n    \"b\": 2\r\n}\r\n";
        Assert.Equal(after, Set(before, "b", 2));
        Assert.DoesNotContain("\n", Set(before, "b", 2).Replace("\r\n", ""));
    }

    [Fact]
    public void A_dotted_key_goes_into_an_existing_parent_object_as_the_remaining_dotted_name()
    {
        var before = """
            {
                "editor": {
                    "tabSize": 2
                }
            }
            """;
        var after = """
            {
                "editor": {
                    "tabSize": 2,
                    "fontSize": 14
                }
            }
            """;
        Assert.Equal(after, Set(before, "editor.fontSize", 14));
    }

    [Fact]
    public void A_deeper_dotted_key_with_a_partial_parent_inserts_the_rest_as_a_flat_name()
    {
        var before = """
            {
                "editor": {}
            }
            """;
        var after = """
            {
                "editor": {
                    "minimap.enabled": false
                }
            }
            """;
        Assert.Equal(after, Set(before, "editor.minimap.enabled", false));
        // ...and that nested/flat mix is still addressable afterwards
        Assert.Equal(after.Replace("false", "true"), Set(after, "editor.minimap.enabled", true));
    }

    [Fact]
    public void A_new_top_level_dotted_key_is_added_flat_like_vs_code_does()
    {
        var before = "{\n    \"a\": 1\n}";
        var after = "{\n    \"a\": 1,\n    \"workbench.colorTheme\": \"Visual Studio Light\"\n}";
        Assert.Equal(after, Set(before, "workbench.colorTheme", "Visual Studio Light"));
    }

    [Fact]
    public void A_conflicting_scalar_parent_does_not_corrupt_the_file()
    {
        var before = """{ "editor": 5 }""";
        var result = Set(before, "editor.fontSize", 12);
        var node = JsoncParser.ToJsonNode(JsoncParser.Parse(result));
        Assert.Equal(5, node!["editor"]!.GetValue<int>());
        Assert.Equal(12, node["editor.fontSize"]!.GetValue<int>());
    }

    [Fact]
    public void Inserted_object_values_use_the_members_indentation()
    {
        var before = "{\n    \"a\": 1\n}";
        var after = "{\n    \"a\": 1,\n    \"o\": {\n        \"x\": 1\n    }\n}";
        Assert.Equal(after, Set(before, "o", new { x = 1 }));
    }

    // ---- removing ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Removing_a_middle_member_drops_its_whole_line_including_a_trailing_comment()
    {
        var before = """
            {
                "a": 1,
                "b": 2, // remove me
                "c": 3
            }
            """;
        var after = """
            {
                "a": 1,
                "c": 3
            }
            """;
        Assert.Equal(after, Set(before, "b", null));
    }

    [Fact]
    public void Removing_the_last_member_also_removes_the_comma_before_it()
    {
        var before = """
            {
                "a": 1, // keep
                "b": 2
            }
            """;
        var after = """
            {
                "a": 1 // keep
            }
            """;
        Assert.Equal(after, Set(before, "b", null));
    }

    [Fact]
    public void Removing_the_last_member_of_a_trailing_comma_file_keeps_the_commas_consistent()
    {
        var before = "{\n    \"a\": 1,\n    \"b\": 2,\n}";
        Assert.Equal("{\n    \"a\": 1,\n}", Set(before, "b", null));
    }

    [Fact]
    public void Removing_the_first_and_the_only_member()
    {
        Assert.Equal("{\n    \"b\": 2\n}", Set("{\n    \"a\": 1,\n    \"b\": 2\n}", "a", null));
        Assert.Equal("{\n}", Set("{\n    \"a\": 1\n}", "a", null));
    }

    [Fact]
    public void Removing_from_inline_objects()
    {
        Assert.Equal("""{"b": 2, "c": 3}""", Set("""{"a": 1, "b": 2, "c": 3}""", "a", null));
        Assert.Equal("""{"a": 1, "c": 3}""", Set("""{"a": 1, "b": 2, "c": 3}""", "b", null));
        Assert.Equal("""{"a": 1, "b": 2}""", Set("""{"a": 1, "b": 2, "c": 3}""", "c", null));
        Assert.Equal("{}", Set("""{"a": 1}""", "a", null));
    }

    [Fact]
    public void Removing_a_nested_key_by_dotted_name()
    {
        var before = "{\n    \"editor\": {\n        \"a\": 1,\n        \"b\": 2\n    }\n}";
        Assert.Equal("{\n    \"editor\": {\n        \"a\": 1\n    }\n}", Set(before, "editor.b", null));
    }

    [Fact]
    public void Removing_a_key_that_is_not_there_changes_nothing()
    {
        var before = "{\n    // c\n    \"a\": 1\n}";
        Assert.Equal(before, Set(before, "nope", null));
        Assert.Equal("", Set("", "nope", null));
    }

    // ---- errors ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("{ \"a\": ")]
    [InlineData("{ \"a\" 1 }")]
    [InlineData("{ a: 1 }")]
    [InlineData("{ \"a\": 1 } trailing")]
    [InlineData("[1, 2]")]
    [InlineData("\"just a string\"")]
    [InlineData("{ \"a\": 'single' }")]
    public void Malformed_or_non_object_documents_are_refused_not_rewritten(string before)
    {
        Assert.Throws<JsoncException>(() => Set(before, "k", 1));
    }

    // ---- parser ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Parser_accepts_comments_trailing_commas_bom_and_escapes()
    {
        var text = "\uFEFF// c\n{ /* x */ \"a\": [1, 2, ], \"s\": \"t\\u0041\\n\\\"\", \"n\": -1.5e3, \"z\": null, \"t\": true, }";
        var node = JsoncParser.ToJsonNode(JsoncParser.Parse(text))!.AsObject();
        Assert.Equal(2, node["a"]!.AsArray().Count);
        Assert.Equal("tA\n\"", node["s"]!.GetValue<string>());
        Assert.Equal(-1500d, node["n"]!.GetValue<double>());
        Assert.Null(node["z"]);
        Assert.True(node["t"]!.GetValue<bool>());
    }

    [Fact]
    public void Parser_agrees_with_System_Text_Json_on_valid_documents()
    {
        string[] docs =
        [
            """{"a":1,"b":[true,false,null,"x",{"c":{"d":[]}}],"e":"\u00e9\n","f":1.25e-2}""",
            """{ "nested": { "deep": { "deeper": { "v": [1, [2, [3]]] } } }, "empty": {}, "arr": [] }""",
            """[1,2,3]""",
            "\"s\"",
            "12.5",
        ];
        foreach (var doc in docs)
        {
            var mine = JsoncParser.ToJsonNode(JsoncParser.Parse(doc))!.ToJsonString();
            var theirs = JsonNode.Parse(doc)!.ToJsonString();
            Assert.Equal(theirs, mine);
        }
    }

    [Fact]
    public void Parser_rejects_leading_zeros_bad_escapes_and_runaway_nesting()
    {
        Assert.Throws<JsoncException>(() => JsoncParser.Parse("{\"a\": 01}"));
        Assert.Throws<JsoncException>(() => JsoncParser.Parse("{\"a\": \"\\x\"}"));
        Assert.Throws<JsoncException>(() => JsoncParser.Parse(new string('[', 10_000)));
        Assert.Throws<JsoncException>(() => JsoncParser.Parse("{\"a\": 1,, }"));
        Assert.Throws<JsoncException>(() => JsoncParser.Parse("{\"a\": \"line\nbreak\"}"));
    }

    [Fact]
    public void Parser_records_spans_and_comma_positions()
    {
        const string text = "{ \"a\": 1, \"b\": [2] }";
        var root = (JsoncObject)JsoncParser.Parse(text)!;
        Assert.Equal(0, root.Start);
        Assert.Equal(text.Length, root.End);
        Assert.Equal("1", text[root.Members[0].Value.Start..root.Members[0].Value.End]);
        Assert.Equal(text.IndexOf(',', StringComparison.Ordinal), root.Members[0].CommaPos);
        Assert.Equal(-1, root.Members[1].CommaPos);
        Assert.Equal("[2]", text[root.Members[1].Value.Start..root.Members[1].Value.End]);
    }

    // ---- model-based fuzz: any sequence of edits keeps the file valid and the comments intact ------------------------------------

    [Fact]
    public void Random_edit_sequences_always_produce_valid_documents_that_keep_every_comment()
    {
        var rng = new Random(20261028);
        string[] keys = ["a", "b.c", "b.d", "editor.fontSize", "editor.minimap.enabled", "x.y.z", "files.exclude", "k", "[csharp].editor.tabSize"];
        for (var round = 0; round < 60; round++)
        {
            var text = rng.Next(4) switch
            {
                0 => "",
                1 => "{\n    // header comment\n    \"seed\": 1, // seed comment\n    \"b\": { \"c\": 2 } /* block */\n}\n",
                2 => "{ \"seed\": 1, }",
                _ => "{\r\n  // crlf comment\r\n  \"editor\": { \"fontSize\": 10 }\r\n}\r\n",
            };
            var comments = new[] { "header comment", "seed comment", "block", "crlf comment" }.Where(text.Contains).ToArray();

            for (var step = 0; step < 25; step++)
            {
                var key = keys[rng.Next(keys.Length)];
                object? value = rng.Next(5) switch { 0 => null, 1 => rng.Next(100), 2 => "v" + rng.Next(10), 3 => rng.Next(2) == 0, _ => new { n = rng.Next(5), s = "t" } };
                text = Set(text, key, value);

                var parsed = JsoncParser.Parse(text);                  // still valid JSONC (throws otherwise)
                if (parsed is null) { Assert.True(string.IsNullOrWhiteSpace(text)); continue; } // removing from an empty file is a no-op
                _ = JsoncParser.ToJsonNode(parsed);
                foreach (var c in comments) Assert.Contains(c, text);  // no comment ever lost
            }
        }
    }
}
