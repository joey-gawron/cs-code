using System.Globalization;

namespace CsCode.SpellCheck.Text;

/// <summary>A word found in a line: <see cref="Start"/> and <see cref="Length"/> in UTF-16 code units of that line.</summary>
public readonly record struct WordSpan(int Start, int Length);

/// <summary>
/// Splits text into the words a spell checker reads, the way cspell reads code: identifiers are split into their parts (<c>camelCase</c>,
/// <c>PascalCase</c>, <c>snake_case</c>, <c>kebab-case</c>, and at digits: <c>parseUtf8Bytes</c> → parse, Utf, Bytes), an upper-case run
/// stays one word (<c>XMLHttpRequest</c> → XML, Http, Request; <c>URLs</c> and <c>APIs</c> stay whole), apostrophes inside a word are kept
/// (<c>don't</c>). Skipped whole: URLs, e-mail addresses, file paths (<c>src/a.cs</c>, <c>./x</c>, <c>~/x</c>, <c>C:\x</c>), hex numbers
/// (<c>0xFF</c>, <c>#a0b1c2</c>), and words with letters of a script other than Latin, Greek or Cyrillic (no dictionary could split them).
/// Allocation-free: words are appended to a caller-owned list.
/// </summary>
public static class WordSplitter
{
    /// <summary>Appends the words of <paramref name="text"/>[<paramref name="start"/>, <paramref name="end"/>) to <paramref name="words"/>.</summary>
    public static void Split(ReadOnlySpan<char> text, int start, int end, List<WordSpan> words)
    {
        end = Math.Min(end, text.Length);
        var i = Math.Max(0, start);
        while (i < end)
        {
            if (IsChunkBreak(text[i]))
            {
                i++;
                continue;
            }
            var chunkStart = i;
            while (i < end && !IsChunkBreak(text[i])) i++;
            var chunkEnd = i;
            // a URL or e-mail address can carry characters that break chunks ('=' '&' '?' in a query): skip to its real end
            if (IsUrlStart(text, chunkStart, end) || IsEmail(text[chunkStart..chunkEnd]))
            {
                i = chunkEnd = UrlEnd(text, chunkStart, end);
                continue;
            }
            var chunk = text[chunkStart..chunkEnd];
            if (IsPath(chunk) || IsHexNumber(chunk)) continue;
            SplitChunk(text, chunkStart, chunkEnd, words);
        }
    }

    /// <summary>Splits one identifier-like chunk at everything that is not a letter (digits, underscores, hyphens, dots...) and at case changes.</summary>
    private static void SplitChunk(ReadOnlySpan<char> text, int start, int end, List<WordSpan> words)
    {
        var i = start;
        while (i < end)
        {
            if (!IsLetter(text, i))
            {
                i++;
                continue;
            }
            var runStart = i;
            var foreign = false;
            // a run of letters, with apostrophes between letters (don't, it's, O'Brien)
            while (i < end)
            {
                if (IsLetter(text, i))
                {
                    if (!IsCheckableScript(text[i])) foreign = true;
                    i++;
                }
                else if (IsApostrophe(text[i]) && i + 1 < end && IsLetter(text, i + 1) && i > runStart) i++;
                else break;
            }
            if (foreign) continue;
            SplitCase(text, runStart, i, words);
        }
    }

    /// <summary>camelCase, PascalCase and upper-case runs: XMLHttpRequest → XML Http Request; getURLs → get URLs; IOStream → IO Stream.</summary>
    private static void SplitCase(ReadOnlySpan<char> text, int start, int end, List<WordSpan> words)
    {
        var wordStart = start;
        var i = start + 1;
        while (i < end)
        {
            var prev = text[i - 1];
            var c = text[i];
            if (char.IsLower(prev) && char.IsUpper(c))
            {
                Add(words, wordStart, i);
                wordStart = i;
            }
            else if (char.IsUpper(prev) && char.IsUpper(c) && i + 1 < end && char.IsLower(text[i + 1]) && !IsPluralAcronym(text, i, end))
            {
                // XMLHttp: the last capital starts the next word
                Add(words, wordStart, i);
                wordStart = i;
            }
            i++;
        }
        Add(words, wordStart, end);
    }

    // URLs (two or more capitals followed by a lone 's' at the end of the run or before the next capital): one word, the checker drops the 's'
    private static bool IsPluralAcronym(ReadOnlySpan<char> text, int i, int end) =>
        text[i + 1] == 's' && (i + 2 >= end || !char.IsLower(text[i + 2])) && i >= 1 && char.IsUpper(text[i - 1]);

    private static void Add(List<WordSpan> words, int start, int end)
    {
        if (end > start) words.Add(new WordSpan(start, end - start));
    }

    private static bool IsLetter(ReadOnlySpan<char> text, int i)
    {
        var c = text[i];
        if (char.IsLetter(c)) return true;
        var cat = char.GetUnicodeCategory(c);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
    }

    private static bool IsApostrophe(char c) => c is '\'' or '’';

    /// <summary>Latin (with its extensions), Greek and Cyrillic: scripts that separate words with spaces and that Hunspell dictionaries cover.</summary>
    private static bool IsCheckableScript(char c) =>
        c < 0x0250 || (c >= 0x0300 && c < 0x0530) || (c >= 0x1D00 && c < 0x1DC0) || (c >= 0x1E00 && c < 0x2000);

    /// <summary>
    /// Characters that end a chunk: whitespace, quotes, brackets and most punctuation. Not '.', '-', '_', '/', '\', ':', '@', '#', '~' and the
    /// apostrophe, which a path, an address, a hex colour or a word can contain.
    /// </summary>
    private static bool IsChunkBreak(char c) =>
        char.IsWhiteSpace(c) || c is '"' or '`' or '(' or ')' or '[' or ']' or '{' or '}' or '<' or '>' or ',' or ';' or '|' or '*' or '='
            or '!' or '?' or '&' or '+' or '^' or '$' or '%' or '“' or '”' or '‘';

    private static bool IsUrlStart(ReadOnlySpan<char> text, int i, int end)
    {
        var rest = text[i..end];
        foreach (var scheme in (ReadOnlySpan<string>)["https://", "http://", "ftp://", "file://", "ssh://", "git://", "mailto:", "www."])
            if (rest.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static int UrlEnd(ReadOnlySpan<char> text, int i, int end)
    {
        while (i < end && !char.IsWhiteSpace(text[i]) && text[i] is not ('"' or '\'' or '<' or '>' or '`' or ')' or ']' or '}')) i++;
        return i;
    }

    /// <summary>name@host.tld</summary>
    private static bool IsEmail(ReadOnlySpan<char> chunk)
    {
        var at = chunk.IndexOf('@');
        if (at <= 0 || at == chunk.Length - 1) return false;
        var host = chunk[(at + 1)..];
        var dot = host.IndexOf('.');
        return dot > 0 && dot < host.Length - 1 && char.IsLetterOrDigit(chunk[at - 1]);
    }

    /// <summary>
    /// A path: a separator between two path characters (<c>a/b</c>, <c>src\x</c>), or one after <c>.</c> or <c>~</c> (<c>./x</c>, <c>~/x</c>,
    /// <c>../x</c>), or a drive (<c>C:\</c>). A lone separator (<c>//</c>, <c>/</c>) is not one, so a comment's <c>//TODO</c> is still read.
    /// </summary>
    private static bool IsPath(ReadOnlySpan<char> chunk)
    {
        if (chunk.Length >= 3 && char.IsAsciiLetter(chunk[0]) && chunk[1] == ':' && chunk[2] is '\\' or '/') return true;
        for (var i = 0; i < chunk.Length; i++)
        {
            if (chunk[i] is not ('/' or '\\')) continue;
            var before = i > 0 ? chunk[i - 1] : '\0';
            var after = i + 1 < chunk.Length ? chunk[i + 1] : '\0';
            var beforeOk = char.IsLetterOrDigit(before) || before is '.' or '~' or '_' or '-';
            var afterOk = char.IsLetterOrDigit(after) || after is '.' or '_' or '-' or '*';
            if (beforeOk && afterOk) return true;
            if (i == 0 && afterOk && chunk[(i + 1)..].IndexOfAny('/', '\\') > 0) return true; // /usr/bin
        }
        return false;
    }

    /// <summary>0x1F, 0XFF_FF, #a0b1c2 (3 to 8 hex digits), and a UUID-like run of hex groups.</summary>
    private static bool IsHexNumber(ReadOnlySpan<char> chunk)
    {
        if (chunk.Length > 2 && chunk[0] == '0' && chunk[1] is 'x' or 'X') return AllHex(chunk[2..]);
        if (chunk.Length is >= 4 and <= 9 && chunk[0] == '#') return AllHex(chunk[1..]);
        return false;
    }

    private static bool AllHex(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
            if (!char.IsAsciiHexDigit(c) && c != '_') return false;
        return s.Length > 0;
    }
}
