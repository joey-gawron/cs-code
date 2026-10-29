using Avalonia.Media;
using CsCode.Core.Theming;
using CsCode.Editor.Languages;
using CsCode.Editor.Markdown;
using CsCode.Editor.TextMate;
using CsCode.Editor.Theming;
using TextMateSharp.Grammars;

namespace CsCode.Editor.Features;

/// <summary>
/// Colours the fenced code in hovers/documentation with the same TextMate grammars and theme as the editor. <b>Worker thread only</b>
/// (the grammar registry is confined to it): the result is a list of coloured spans per block that the UI renders without any parsing.
/// </summary>
internal static class CodeHighlighter
{
    private const int MaxCodeLength = 20_000;

    public static IReadOnlyDictionary<MdCodeBlock, IReadOnlyList<CodeSpan>> HighlightBlocks(
        IReadOnlyList<MdBlock> blocks,
        TokenizationManager tokens,
        LanguageService languages,
        ColorTheme theme,
        Rgba defaultForeground,
        string fallbackLanguage,
        CancellationToken ct)
    {
        var result = new Dictionary<MdCodeBlock, IReadOnlyList<CodeSpan>>();
        TokenColorResolver? resolver = null;
        foreach (var block in Walk(blocks))
        {
            ct.ThrowIfCancellationRequested();
            if (block.Text.Length == 0 || block.Text.Length > MaxCodeLength) continue;
            var language = ResolveLanguage(block.Language, languages) ?? fallbackLanguage;
            var grammar = tokens.Grammars.GrammarForLanguage(language);
            if (grammar is null) continue;
            resolver ??= new TokenColorResolver(theme, defaultForeground);
            result[block] = Spans(block.Text, grammar, resolver, tokens.Interner);
        }
        return result;
    }

    private static IEnumerable<MdCodeBlock> Walk(IReadOnlyList<MdBlock> blocks)
    {
        foreach (var b in blocks)
        {
            if (b is MdCodeBlock c) yield return c;
            else if (b is MdQuote q)
                foreach (var inner in Walk(q.Blocks)) yield return inner;
        }
    }

    /// <summary>Fence info strings are language ids ("csharp"), aliases ("cs", "ts", "py") or extensions; null = unknown.</summary>
    private static string? ResolveLanguage(string? info, LanguageService languages)
    {
        if (string.IsNullOrEmpty(info)) return null;
        if (languages.Get(info) is { } direct) return direct.Id;
        var byExtension = languages.DetectLanguageId(new Uri("file:///snippet." + info.ToLowerInvariant()));
        return byExtension == "plaintext" ? null : byExtension;
    }

    private static IReadOnlyList<CodeSpan> Spans(string code, IGrammar grammar, TokenColorResolver resolver, ScopeInterner interner)
    {
        var spans = new List<CodeSpan>();
        IStateStack? state = null;
        var offset = 0;
        var cache = new Dictionary<int, ResolvedTokenStyle>();
        foreach (var rawLine in code.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            try
            {
                var result = grammar.TokenizeLine(line, state, TimeSpan.FromMilliseconds(50));
                state = result.RuleStack;
                var tokens = result.Tokens;
                for (var i = 0; i < tokens.Length; i++)
                {
                    var t = tokens[i];
                    var start = Math.Min(t.StartIndex, line.Length);
                    var end = Math.Min(t.EndIndex, line.Length);
                    if (end <= start) continue;
                    var id = interner.Intern(t.Scopes);
                    if (!cache.TryGetValue(id, out var style)) cache[id] = style = resolver.Resolve(interner.Scopes(id));
                    if (style.IsPlain) continue;
                    spans.Add(new CodeSpan(offset + start, end - start, style.Foreground is { } f ? Color.FromArgb(f.A, f.R, f.G, f.B) : null, style.Bold, style.Italic));
                }
            }
            catch (Exception)
            {
                // an odd line in a snippet must not lose the highlighting of the rest of the block: it is simply left plain
            }
            offset += rawLine.Length + 1;
        }
        return spans;
    }
}
