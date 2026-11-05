using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.SpellCheck.Engine;
using CsCode.SpellCheck.Settings;

namespace CsCode.SpellCheck;

/// <summary>
/// <see cref="ITextSpellChecker"/> over the spell checker: a text that is no document (the Source Control commit message box) is read whole
/// as prose, on the spell lane, with the same settings and dictionaries; adding a word writes the user dictionary as the quick fix does.
/// </summary>
public sealed class TextSpellChecker(SpellCheckService service, IConfigurationService config) : ITextSpellChecker
{
    public IEventSource<bool> Changed => service.TextsChanged;

    public Task<IReadOnlyList<SpellingIssue>> CheckTextAsync(string text, string languageId, CancellationToken ct = default) =>
        service.CheckTextAsync(text, languageId, ct);

    public Task<IReadOnlyList<string>> SuggestAsync(string word, CancellationToken ct = default) => service.SuggestAsync(word, ct);

    public async Task AddToUserDictionaryAsync(string word, CancellationToken ct = default)
    {
        service.AcceptNow([word]);
        await DictionaryWriter.AddToUserAsync(config, SpellSettingsLoader.Words, [word], ct).ConfigureAwait(false);
    }
}
