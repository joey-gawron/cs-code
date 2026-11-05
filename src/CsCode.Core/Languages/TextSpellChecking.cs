using CsCode.Core.Lifecycle;

namespace CsCode.Core.Languages;

/// <summary>A misspelt word in a text: <see cref="Start"/> and <see cref="Length"/> are UTF-16 offsets into the text checked.</summary>
public sealed record SpellingIssue(int Start, int Length, string Word, string Message);

/// <summary>
/// Spell checking of text that is not an open document — the Source Control commit message box — with the spell checker's dictionaries and
/// settings (implemented by CsCode.SpellCheck; docs/SPELLCHECK.md). Every call runs on the checker's own lane: a caller on the UI thread
/// pays only for enqueueing. Additive (spell).
/// </summary>
public interface ITextSpellChecker
{
    /// <summary>The misspelt words of <paramref name="text"/>, read whole as prose of <paramref name="languageId"/> (empty when spell checking is off for it).</summary>
    Task<IReadOnlyList<SpellingIssue>> CheckTextAsync(string text, string languageId, CancellationToken ct = default);

    /// <summary>Replacements for <paramref name="word"/>, best first, in its case.</summary>
    Task<IReadOnlyList<string>> SuggestAsync(string word, CancellationToken ct = default);

    /// <summary>Adds <paramref name="word"/> to the user dictionary (<c>cSpell.words</c> in the user settings).</summary>
    Task AddToUserDictionaryAsync(string word, CancellationToken ct = default);

    /// <summary>Fires (any thread) when the settings or dictionaries changed: texts should be checked again.</summary>
    IEventSource<bool> Changed { get; }
}
