using CsCode.Core.Platform;
using Xunit;

namespace CsCode.Macros.Tests;

/// <summary>Saving into a hand-edited macros.json changes only the saved macro's object: comments, layout and other macros stay.</summary>
public class MacroFileEditorTests
{
    private static Macro M(string name, params string[] texts) => new(name, texts.Select(t => (MacroStep)new TextStep(t)).ToArray());

    private static MacroFile Parse(string text)
    {
        var file = MacroJson.ParseFile(text);
        Assert.Empty(file.Errors);
        return file;
    }

    [Fact]
    public void A_missing_file_becomes_the_guide_plus_the_macro()
    {
        var text = MacroFileEditor.Upsert(null, M("a", "x"));
        Assert.StartsWith("// CsCode macros", text);
        Assert.Equal([M("a", "x")], Parse(text).Macros);
    }

    [Fact]
    public void Appending_keeps_comments_and_the_other_macros_untouched()
    {
        const string original = """
            // my notes
            {
              // the important one
              "macros": [
                { "name": "first", "steps": [ { "text": "1" } ] } // inline note
              ],
              "unrelated": true
            }
            """;
        var text = MacroFileEditor.Upsert(original, M("second", "2"));

        Assert.Contains("// my notes", text);
        Assert.Contains("// the important one", text);
        Assert.Contains("{ \"name\": \"first\", \"steps\": [ { \"text\": \"1\" } ] }, // inline note", text);
        Assert.Contains("\"unrelated\": true", text);
        Assert.Equal(["first", "second"], Parse(text).Macros.Select(m => m.Name));
    }

    [Fact]
    public void Replacing_a_macro_rewrites_only_its_object()
    {
        const string original = """
            {
              "macros": [
                { "name": "a", "steps": [ { "text": "old" } ] },
                // b is mine
                { "name": "b", "steps": [ { "text": "keep" } ] }
              ]
            }
            """;
        var text = MacroFileEditor.Upsert(original, M("a", "new"));

        Assert.Contains("// b is mine", text);
        Assert.Contains("{ \"name\": \"b\", \"steps\": [ { \"text\": \"keep\" } ] }", text);
        Assert.DoesNotContain("old", text);
        var file = Parse(text);
        Assert.Equal([M("a", "new"), M("b", "keep")], file.Macros);
    }

    [Fact]
    public void A_trailing_comma_after_the_last_macro_is_respected()
    {
        var text = MacroFileEditor.Upsert("{ \"macros\": [ { \"name\": \"a\", \"steps\": [] }, ] }", M("b", "x"));
        Assert.Equal(["a", "b"], Parse(text).Macros.Select(m => m.Name));
        Assert.DoesNotContain(",,", text.Replace(" ", "").Replace("\n", ""));
    }

    [Fact]
    public void An_empty_macros_array_and_a_file_without_one_get_the_macro()
    {
        Assert.Equal(["a"], Parse(MacroFileEditor.Upsert("{ \"macros\": [] }", M("a"))).Macros.Select(m => m.Name));
        var noArray = MacroFileEditor.Upsert("{\n  // just a comment\n  \"x\": 1\n}\n", M("a", "y"));
        Assert.Contains("// just a comment", noArray);
        Assert.Equal(["a"], Parse(noArray).Macros.Select(m => m.Name));
        Assert.Equal(["a"], Parse(MacroFileEditor.Upsert("{}", M("a"))).Macros.Select(m => m.Name));
    }

    [Fact]
    public void Windows_line_endings_are_kept()
    {
        var text = MacroFileEditor.Upsert("{\r\n  \"macros\": [\r\n    { \"name\": \"a\", \"steps\": [] }\r\n  ]\r\n}\r\n", M("b", "x"));
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
        Assert.Equal(2, Parse(text).Macros.Count);
    }

    [Theory]
    [InlineData("{ \"macros\": [ ")]
    [InlineData("[]")]
    [InlineData("{ \"macros\": {} }")]
    [InlineData("{ } trailing")]
    public void A_broken_file_is_never_overwritten(string broken)
    {
        var ex = Assert.Throws<MacroFileException>(() => MacroFileEditor.Upsert(broken, M("a")));
        Assert.Contains("fix it first", ex.Message);
    }

    [Fact]
    public void Removing_a_macro_leaves_valid_json_and_the_rest()
    {
        const string original = """
            {
              "macros": [
                { "name": "a", "steps": [] },
                { "name": "b", "steps": [] },
                { "name": "c", "steps": [] }
              ]
            }
            """;
        Assert.Equal(["a", "c"], Parse(MacroFileEditor.Remove(original, "b")).Macros.Select(m => m.Name));
        Assert.Equal(["b", "c"], Parse(MacroFileEditor.Remove(original, "a")).Macros.Select(m => m.Name));
        Assert.Equal(["a", "b"], Parse(MacroFileEditor.Remove(original, "c")).Macros.Select(m => m.Name));
        Assert.Empty(Parse(MacroFileEditor.Remove("{ \"macros\": [ { \"name\": \"a\", \"steps\": [] } ] }", "a")).Macros);
        Assert.Equal(original, MacroFileEditor.Remove(original, "zzz"));
    }

    [Fact]
    public void Saving_repeatedly_converges_and_never_duplicates()
    {
        var text = MacroFileEditor.Upsert(null, M("a", "1"));
        for (var i = 0; i < 5; i++)
        {
            text = MacroFileEditor.Upsert(text, M("a", i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            text = MacroFileEditor.Upsert(text, M("b" + i, "x"));
        }
        var file = Parse(text);
        Assert.Equal(["a", "b0", "b1", "b2", "b3", "b4"], file.Macros.Select(m => m.Name));
        Assert.Equal(new TextStep("4"), file.Macros[0].Steps[0]);
        Assert.Equal(new KeyStroke("a"), KeyStrokeParser.ParseStroke("a"));
    }
}
