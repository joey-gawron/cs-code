using System.Text.Json;
using CsCode.Core.Platform;
using Xunit;

namespace CsCode.Macros.Tests;

/// <summary>The step model and the macros.json format: round trips, what is rejected and how it is reported.</summary>
public class MacroJsonTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly Macro Sample = new("comment-line",
    [
        new KeyStep(KeyStrokeParser.ParseStroke("home")),
        new TextStep("// \"quoted\" ünïcödé <tag> \\ tab\t"),
        new KeyStep(KeyStrokeParser.ParseStroke("ctrl+shift+right")),
        new CommandStep("editor.action.commentLine"),
        new CommandStep("workbench.action.gotoLine", [J("\"12:3\"")]),
        new CommandStep(MacroCommands.Find, [new MacroFindArgs("foo(", Regex: true, MatchCase: true).ToJson()]),
        new CommandStep("x.args", [J("{\"a\":[1,2,{\"b\":null}]}"), J("null"), J("true")]),
        new PauseStep(250),
        new KeyStep(KeyStrokeParser.ParseStroke("down")),
    ])
    { Description = "Comments the line and moves down", Key = "ctrl+alt+m" };

    [Fact]
    public void A_macro_round_trips_through_the_file_format()
    {
        var text = MacroJson.FormatFile([Sample, Sample with { Name = "second", Description = null, Key = null }]);
        var file = MacroJson.ParseFile(text);

        Assert.Empty(file.Errors);
        Assert.Equal(2, file.Macros.Count);
        Assert.Equal(Sample, file.Macros[0]);
        Assert.Equal("second", file.Macros[1].Name);
        Assert.Null(file.Macros[1].Key);
        Assert.Equal(Sample.Steps, file.Macros[1].Steps);
    }

    [Fact]
    public void The_file_is_human_readable_one_step_per_line_with_text_as_typed()
    {
        var text = MacroJson.FormatFile([Sample]);

        Assert.StartsWith("// CsCode macros", text);
        Assert.Contains("        { \"key\": \"home\" },\n", text);
        Assert.Contains("ünïcödé <tag>", text); // not \u-escaped
        Assert.Contains("{ \"command\": \"workbench.action.gotoLine\", \"args\": [\"12:3\"] }", text);
        Assert.Contains("{ \"pause\": 250 }", text);
        Assert.Contains("\"key\": \"ctrl+alt+m\",", text);
    }

    [Fact]
    public void Comments_trailing_commas_and_a_bom_free_hand_written_file_parse()
    {
        var file = MacroJson.ParseFile("""
            // my macros
            {
              /* block */ "macros": [
                { "name": "a", "steps": [ { "text": "x" }, { "key": "Enter" }, ], }, // trailing commas
              ],
              "other": 1,
            }
            """);

        Assert.Empty(file.Errors);
        var m = Assert.Single(file.Macros);
        Assert.Equal([new TextStep("x"), new KeyStep(new KeyStroke("enter"))], m.Steps);
    }

    [Fact]
    public void Command_args_that_are_not_an_array_are_the_single_argument()
    {
        var file = MacroJson.ParseFile("""{ "macros": [ { "name": "a", "steps": [ { "command": "c", "args": { "x": 1 } }, { "command": "d", "args": [[1, 2]] } ] } ] }""");

        var steps = Assert.Single(file.Macros).Steps.Cast<CommandStep>().ToArray();
        Assert.Equal(1, steps[0].Args[0].GetProperty("x").GetInt32());
        Assert.Single(steps[0].Args);
        Assert.Equal(JsonValueKind.Array, Assert.Single(steps[1].Args).ValueKind);
        Assert.Null(new CommandStep("c", [J("null")]).ArgumentArray()[0]); // JSON null is passed as null
    }

    [Theory]
    [InlineData("""{ "name": "a", "steps": [ { "key": "ctrl+k ctrl+c" } ] }""", "is a chord")]
    [InlineData("""{ "name": "a", "steps": [ { "key": "ctrl+" } ] }""", "step 1")]
    [InlineData("""{ "name": "a", "steps": [ { "text": "" } ] }""", "non-empty string")]
    [InlineData("""{ "name": "a", "steps": [ { "text": "a", "key": "b" } ] }""", "not both")]
    [InlineData("""{ "name": "a", "steps": [ { "pause": 90000 } ] }""", "milliseconds")]
    [InlineData("""{ "name": "a", "steps": [ { "typo": 1 } ] }""", "unknown property \"typo\"")]
    [InlineData("""{ "name": "a", "steps": [ { "text": "a", "args": [] } ] }""", "belongs to a command")]
    [InlineData("""{ "name": "a", "steps": [ 5 ] }""", "must be an object")]
    [InlineData("""{ "name": "a b", "steps": [] }""", "invalid name")]
    [InlineData("""{ "steps": [] }""", "\"name\" is missing")]
    [InlineData("""{ "name": "a", "steps": {} }""", "\"steps\" must be an array")]
    public void A_bad_macro_is_skipped_as_a_whole_and_the_problem_reported(string macro, string expected)
    {
        var file = MacroJson.ParseFile($$"""{ "macros": [ {{macro}}, { "name": "good", "steps": [ { "text": "ok" } ] } ] }""");

        Assert.Equal("good", Assert.Single(file.Macros).Name);
        Assert.Contains(expected, Assert.Single(file.Errors));
    }

    [Fact]
    public void A_bad_key_binding_keeps_the_macro_without_it()
    {
        var file = MacroJson.ParseFile("""{ "macros": [ { "name": "a", "key": "ctrl+shift+", "steps": [ { "text": "x" } ] } ] }""");

        Assert.Null(Assert.Single(file.Macros).Key);
        Assert.Contains("without a key binding", Assert.Single(file.Errors));
    }

    [Fact]
    public void Duplicate_names_keep_the_later_macro_and_say_so()
    {
        var file = MacroJson.ParseFile("""{ "macros": [ { "name": "a", "steps": [ { "text": "1" } ] }, { "name": "a", "steps": [ { "text": "2" } ] } ] }""");

        Assert.Equal(new TextStep("2"), Assert.Single(Assert.Single(file.Macros).Steps));
        Assert.Contains("also used", Assert.Single(file.Errors));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("{}", 0)]
    [InlineData("{ \"macros\": [] }", 0)]
    public void Empty_files_have_no_macros_and_no_errors(string text, int count)
    {
        var file = MacroJson.ParseFile(text);
        Assert.Equal(count, file.Macros.Count);
        Assert.Empty(file.Errors);
    }

    [Theory]
    [InlineData("{ \"macros\": [ ", "not valid JSON")]
    [InlineData("[1, 2]", "must contain an object")]
    [InlineData("{ \"macros\": 3 }", "must be an array")]
    public void Broken_files_report_one_error_and_load_nothing(string text, string expected)
    {
        var file = MacroJson.ParseFile(text);
        Assert.Empty(file.Macros);
        Assert.Contains(expected, Assert.Single(file.Errors));
    }

    [Fact]
    public void Limits_bound_what_a_file_can_load()
    {
        var steps = string.Join(",", Enumerable.Repeat("{\"text\":\"x\"}", 11));
        var file = MacroJson.ParseFile($$"""{ "macros": [ { "name": "big", "steps": [{{steps}}] } ] }""", maxStepsPerMacro: 10);
        Assert.Empty(file.Macros);
        Assert.Contains("more than 10 steps", Assert.Single(file.Errors));

        var many = string.Join(",", Enumerable.Range(0, MacroJson.MaxMacros + 5).Select(i => $"{{\"name\":\"m{i}\",\"steps\":[]}}"));
        var all = MacroJson.ParseFile($$"""{ "macros": [{{many}}] }""");
        Assert.Equal(MacroJson.MaxMacros, all.Macros.Count);
        Assert.Contains("the rest are ignored", Assert.Single(all.Errors));
    }

    [Fact]
    public void The_persisted_step_list_round_trips_and_rejects_garbage()
    {
        var json = MacroJson.FormatSteps(Sample.Steps);
        Assert.Equal(Sample.Steps, MacroJson.ParseSteps(json));
        Assert.Null(MacroJson.ParseSteps("{"));
        Assert.Null(MacroJson.ParseSteps("[{\"nope\":1}]"));
        Assert.Null(MacroJson.ParseSteps("{}"));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("Comment-Line_2", true)]
    [InlineData("9lives", true)]
    [InlineData("-a", false)]
    [InlineData("_a", false)]
    [InlineData("a b", false)]
    [InlineData("a.b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Macro_names_are_command_id_safe(string? name, bool valid) => Assert.Equal(valid, MacroNames.IsValid(name));

    [Fact]
    public void Steps_compare_by_value_including_json_arguments()
    {
        Assert.Equal(new CommandStep("c", [J("{\"a\": 1, \"b\": [2]}")]), new CommandStep("c", [J("{\"a\":1,\"b\":[2]}")]));
        Assert.NotEqual(new CommandStep("c", [J("1")]), new CommandStep("c", [J("2")]));
        Assert.True(new KeyStep(new KeyStroke("a")).TypesIntoEditor);
        Assert.False(new CommandStep("c").TypesIntoEditor);
        Assert.False(new Macro("x", [new CommandStep("c"), new PauseStep(1)]).TypesIntoEditor);
        Assert.True(Sample.TypesIntoEditor);
        Assert.Equal("macro.run.comment-line", Sample.CommandId);
    }
}
