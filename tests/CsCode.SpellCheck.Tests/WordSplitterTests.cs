using CsCode.SpellCheck.Text;

namespace CsCode.SpellCheck.Tests;

public class WordSplitterTests
{
    private static string[] Words(string text)
    {
        var spans = new List<WordSpan>();
        WordSplitter.Split(text, 0, text.Length, spans);
        return spans.Select(s => text.Substring(s.Start, s.Length)).ToArray();
    }

    [Theory]
    [InlineData("camelCaseWord", new[] { "camel", "Case", "Word" })]
    [InlineData("PascalCaseWord", new[] { "Pascal", "Case", "Word" })]
    [InlineData("snake_case_word", new[] { "snake", "case", "word" })]
    [InlineData("SCREAMING_SNAKE", new[] { "SCREAMING", "SNAKE" })]
    [InlineData("kebab-case-word", new[] { "kebab", "case", "word" })]
    [InlineData("XMLHttpRequest", new[] { "XML", "Http", "Request" })]
    [InlineData("IOStream", new[] { "IO", "Stream" })]
    [InlineData("getHTTPResponse", new[] { "get", "HTTP", "Response" })]
    [InlineData("parseUtf8Bytes", new[] { "parse", "Utf", "Bytes" })]
    [InlineData("base64Encode", new[] { "base", "Encode" })]
    [InlineData("readURLs", new[] { "read", "URLs" })]
    [InlineData("APIsAreFine", new[] { "APIs", "Are", "Fine" })]
    [InlineData("std::vector<int>", new[] { "std", "vector", "int" })]
    [InlineData("--dry-run", new[] { "dry", "run" })]
    public void Identifiers_are_split_into_their_words(string text, string[] expected) =>
        Assert.Equal(expected, Words(text));

    [Fact]
    public void Prose_keeps_apostrophes_inside_words_and_drops_quotes_around_them() =>
        Assert.Equal(["Don't", "say", "it's", "quoted", "the", "parser's"], Words("Don't say it's 'quoted', the parser's"));

    [Theory]
    [InlineData("see https://example.com/recieve?a=b&c=d for more", new[] { "see", "for", "more" })]
    [InlineData("go to www.recieve.org now", new[] { "go", "to", "now" })]
    [InlineData("mail jhon.doe@exampel.com today", new[] { "mail", "today" })]
    [InlineData("open src/CsCode.Core/Recieve.cs here", new[] { "open", "here" })]
    [InlineData("in ~/.config/cscode and ./scripts/biuld.sh", new[] { "in", "and" })]
    [InlineData(@"at C:\Users\recieve\file.txt ok", new[] { "at", "ok" })]
    [InlineData("mask 0xDEADBEEF and #FFAA00 colour", new[] { "mask", "and", "colour" })]
    public void Urls_emails_paths_and_hex_numbers_are_skipped(string text, string[] expected) =>
        Assert.Equal(expected, Words(text));

    [Fact]
    public void A_comment_marker_is_not_a_path() =>
        Assert.Equal(["TODO", "fix", "this"], Words("//TODO fix this"));

    [Fact]
    public void Words_in_scripts_without_a_dictionary_are_skipped() =>
        Assert.Equal(["hello", "world"], Words("hello 世界 world こんにちは"));

    [Fact]
    public void Greek_and_Cyrillic_words_are_words() =>
        Assert.Equal(["λόγος", "слово"], Words("λόγος слово"));

    [Fact]
    public void Offsets_are_relative_to_the_line_and_honour_the_range()
    {
        var spans = new List<WordSpan>();
        const string line = "int x = 1; // recieve it";
        WordSplitter.Split(line, 11, line.Length, spans);
        Assert.Equal([new WordSpan(14, 7), new WordSpan(22, 2)], spans);
    }

    [Theory]
    [InlineData("recieve", "receive", "receive")]
    [InlineData("Recieve", "receive", "Receive")]
    [InlineData("RECIEVE", "receive", "RECEIVE")]
    [InlineData("english", "English", "English")]
    public void Suggestions_take_the_case_of_the_word_they_replace(string original, string suggestion, string expected) =>
        Assert.Equal(expected, CaseMatch.Apply(original, suggestion));
}
