using System.Collections.Frozen;
using CsCode.Core.Languages;
using CsCode.SpellCheck.Engine;
using CsCode.SpellCheck.Settings;

namespace CsCode.SpellCheck.Tests;

public class CheckRunTests
{
    private static IReadOnlyList<SpellIssue> Check(string text, SpellSettings? settings = null, SyntaxClassification? classification = null, bool prose = false) =>
        CheckRun.CheckAll(text, classification ?? FakeClassification.SlashComments(text), prose, settings ?? SpellSettings.Default, Dictionaries.EnUs);

    private static string[] Words(IReadOnlyList<SpellIssue> issues) => issues.Select(i => i.Word).ToArray();

    [Fact]
    public void Only_comments_and_strings_are_read_in_code()
    {
        const string text = "var recieveCount = Compute(\"a wierd string\"); // occured here\nint misspeled = 0;";
        var issues = Check(text);
        Assert.Equal(["wierd", "occured"], Words(issues));
        Assert.Equal(new SpellIssue(0, 30, 5, "wierd", false), issues[0]);
    }

    [Fact]
    public void Identifiers_are_read_with_checkIdentifiers()
    {
        var issues = Check("var recieveCount = 0; // fine", SpellSettings.Default with { CheckIdentifiers = true });
        Assert.Equal(["recieve"], Words(issues));
        Assert.Equal(4, issues[0].Start);
    }

    [Fact]
    public void Ignored_runs_are_never_read()
    {
        var classification = new FakeClassification([[new(0, SyntaxTextKind.Comment), new(3, SyntaxTextKind.Ignored), new(23, SyntaxTextKind.Comment)]]);
        Assert.Equal(["tpyo"], Words(Check("// <paramref name=\"x\"/> tpyo", classification: classification)));
    }

    [Fact]
    public void Prose_languages_without_tokens_are_read_whole_and_code_without_tokens_is_not()
    {
        var none = SyntaxClassification.Empty(SyntaxClassificationState.NoGrammar);
        Assert.Equal(["Thsi", "wierd"], Words(Check("Thsi quick wierd fox", classification: none, prose: true)));
        Assert.Empty(Check("Thsi quick wierd fox", classification: none, prose: false));
        // a tokenised document's lines without tokens yet are left for later, prose or not
        var partial = new FakeClassification([[new(0, SyntaxTextKind.Text)], null]);
        Assert.Equal(["Thsi"], Words(Check("Thsi one\nwierd two", classification: partial, prose: true)));
    }

    [Fact]
    public void Short_words_and_words_with_digits_are_skipped()
    {
        Assert.Empty(Check("// teh xyz utf8 x86_64 abc123def"));
        Assert.Equal(["teh"], Words(Check("// teh", SpellSettings.Default with { MinWordLength = 3 })));
    }

    [Fact]
    public void Settings_words_ignore_words_and_flag_words_are_honoured_in_any_case()
    {
        var settings = SpellSettings.Default with
        {
            Words = new[] { "Kubectl" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            IgnoreWords = new[] { "wierd" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            FlagWords = new Dictionary<string, IReadOnlyList<string>> { ["colour"] = [] }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
        };
        var issues = Check("// KUBECTL kubectl WIERD and the color is not a Colour", settings);
        Assert.Equal([new SpellIssue(0, 48, 6, "Colour", true)], issues);
        Assert.Equal("\"Colour\": Forbidden word.", issues[0].Message);
    }

    [Fact]
    public void Ignore_patterns_hide_what_they_match()
    {
        var settings = SpellSettings.Default with { IgnoreRegExps = SpellSettingsLoader.CompilePatterns(["/TICKET-[A-Z]+-\\d+/", "Email"]) };
        Assert.Equal(["recieve"], Words(Check("// see TICKET-ABCDEFG-12 and recieve", settings)));
    }

    [Fact]
    public void In_document_directives_ignore_words_and_disable_lines()
    {
        const string text = """
            // cspell:ignore wierd foobaz
            // a wierd foobaz
            // cspell:disable-next-line
            // occured
            // occured again
            /* cSpell:disable */
            // definately
            // spell-checker: enable
            // seperate
            """;
        var issues = Check(text.Replace("\r\n", "\n"));
        Assert.Equal(["occured", "seperate"], Words(issues));
        Assert.Equal([4, 8], issues.Select(i => i.Line));
    }

    [Fact]
    public void Problems_are_capped_per_word_and_per_file()
    {
        var text = string.Join("\n", Enumerable.Repeat("// recieve wierd", 50));
        var issues = Check(text, SpellSettings.Default with { MaxDuplicateProblems = 3 });
        Assert.Equal(6, issues.Count);
        var many = string.Join("\n", Enumerable.Range(0, 200).Select(i => "// tpyo" + new string('x', i % 5) + " zzqq" + i));
        var run = new CheckRun(many, FakeClassification.SlashComments(many), false, SpellSettings.Default with { MaxNumberOfProblems = 10 }, Dictionaries.EnUs);
        while (!run.Step(TimeSpan.MaxValue, CancellationToken.None)) { }
        Assert.Equal(10, run.Issues.Count);
        Assert.True(run.Truncated);
    }

    [Fact]
    public void Only_the_check_limit_is_read()
    {
        var text = "// fine words\n" + new string('/', 2) + new string(' ', 2000) + " recieve";
        Assert.Empty(Check(text, SpellSettings.Default with { CheckLimitKb = 1 }));
        Assert.Single(Check(text, SpellSettings.Default with { CheckLimitKb = 4 }));
    }

    [Fact]
    public void A_run_stops_between_slices_and_resumes()
    {
        var text = string.Join("\n", Enumerable.Range(0, 3000).Select(i => $"// line {i} with a wierd word"));
        var run = new CheckRun(text, FakeClassification.SlashComments(text), false, SpellSettings.Default with { MaxDuplicateProblems = 5000, MaxNumberOfProblems = 5000 },
            Dictionaries.EnUs);
        var slices = 1;
        while (!run.Step(TimeSpan.Zero, CancellationToken.None)) slices++;
        Assert.Equal(3000, run.Issues.Count);
        Assert.True(slices > 100, $"one line per slice with a zero budget ({slices} slices)");
    }
}
