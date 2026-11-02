using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Contributions;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Languages;
using Microsoft.Extensions.Logging;

namespace CsCode.Editor.Snippets;

/// <summary>One entry of a VS Code snippet file.</summary>
/// <param name="Languages">The <c>scope</c> of a global (<c>*.code-snippets</c>) snippet; null = the language of the file it came from.</param>
public sealed record SnippetDefinition(string Name, IReadOnlyList<string> Prefixes, string Body, string? Description, IReadOnlyList<string>? Languages = null);

/// <summary>Parses VS Code snippet JSON (<c>{ "Name": { "prefix": …, "body": string | string[], "description": …, "scope": "a,b" } }</c>; JSONC allowed).</summary>
public static class SnippetFileParser
{
    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <exception cref="JsonException">Not valid JSON(C).</exception>
    public static IReadOnlyList<SnippetDefinition> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, Options);
        var list = new List<SnippetDefinition>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return list;
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            var s = entry.Value;
            if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("body", out var body)) continue;
            var text = body.ValueKind switch
            {
                JsonValueKind.String => body.GetString()!,
                JsonValueKind.Array => string.Join("\n", body.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)),
                _ => null,
            };
            if (text is null) continue;
            var prefixes = new List<string>();
            if (s.TryGetProperty("prefix", out var prefix))
            {
                if (prefix.ValueKind == JsonValueKind.String) prefixes.Add(prefix.GetString()!);
                else if (prefix.ValueKind == JsonValueKind.Array)
                    prefixes.AddRange(prefix.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
            }
            // a snippet with no prefix can never be offered by completion (VS Code lists it only in "Insert Snippet")
            var description = s.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            string[]? scope = null;
            if (s.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String)
                scope = sc.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            list.Add(new SnippetDefinition(entry.Name, prefixes, text, description, scope));
        }
        return list;
    }
}

/// <summary>
/// The snippets available per language: the built-in ones (VS Code's, embedded in TextMateSharp.Grammars) plus the user's
/// <see cref="SnippetContribution"/>s (<c>{UserDataDir}/snippets/&lt;lang&gt;.json</c> and <c>*.code-snippets</c> with a <c>scope</c>), which
/// are read off the UI thread whenever the contributions change. The list for a language is an immutable snapshot, so completion can
/// hand it out per request without copying.
/// </summary>
public sealed class SnippetService : IDisposable
{
    private readonly EditorServices _svc;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly DisposableStore _subscriptions = new();
    private readonly Emitter<string> _changed = new();
    private readonly Dictionary<string, IReadOnlyList<SnippetDefinition>> _builtin = new(StringComparer.Ordinal);
    private Dictionary<SnippetContribution, IReadOnlyList<SnippetDefinition>> _user = new();
    private readonly Dictionary<string, IReadOnlyList<SnippetDefinition>> _merged = new(StringComparer.Ordinal);

    public SnippetService(EditorServices svc)
    {
        _svc = svc;
        _log = svc.LoggerFactory.CreateLogger<SnippetService>();
        _subscriptions.Add(svc.Contributions.Of<SnippetContribution>().Changed.Subscribe(items => _ = ReloadAsync(items.Select(i => i.Value).ToList())));
        _ = ReloadAsync(svc.Contributions.Of<SnippetContribution>().Items.Select(i => i.Value).ToList());
    }

    /// <summary>Fires (with the language id, or "*" for global files) after user snippets were (re)loaded.</summary>
    public IEventSource<string> Changed => _changed;

    /// <summary>Snippets for a language: built-ins first, then the user's (so the user's win ties in the suggest list).</summary>
    public IReadOnlyList<SnippetDefinition> GetSnippets(string languageId)
    {
        lock (_gate)
        {
            if (_merged.TryGetValue(languageId, out var cached)) return cached;
            var user = new List<SnippetDefinition>();
            foreach (var (contribution, defs) in _user)
            {
                var global = contribution.LanguageId == "*";
                if (!global && contribution.LanguageId != languageId) continue;
                user.AddRange(global ? defs.Where(d => d.Languages is null || d.Languages.Contains(languageId)) : defs);
            }
            // The user's snippets win: a built-in one with a prefix (or name) of theirs is left out, so typing the prefix offers theirs alone.
            var taken = user.SelectMany(d => d.Prefixes).ToHashSet(StringComparer.Ordinal);
            var names = user.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
            var list = BuiltinFor(languageId).Where(d => !names.Contains(d.Name) && !d.Prefixes.Any(taken.Contains)).ToList();
            list.AddRange(user);
            return _merged[languageId] = list;
        }
    }

    private IReadOnlyList<SnippetDefinition> BuiltinFor(string languageId)
    {
        if (_builtin.TryGetValue(languageId, out var cached)) return cached;
        var list = new List<SnippetDefinition>();
        foreach (var file in BuiltinLanguages.Catalog.Snippets.Where(s => s.LanguageId == languageId))
        {
            try
            {
                if (BuiltinLanguages.ReadText(file.ResourceName) is { } text) list.AddRange(SnippetFileParser.Parse(text));
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Built-in snippet file {Resource} is invalid", file.ResourceName);
            }
        }
        return _builtin[languageId] = list;
    }

    private async Task ReloadAsync(List<SnippetContribution> contributions)
    {
        var loaded = new Dictionary<SnippetContribution, IReadOnlyList<SnippetDefinition>>();
        foreach (var c in contributions)
        {
            try
            {
                var text = System.Text.Encoding.UTF8.GetString(await _svc.FileSystem.ReadFileAsync(new Uri(c.Path)).ConfigureAwait(false));
                loaded[c] = SnippetFileParser.Parse(text);
            }
            catch (Exception ex)
            {
                // a broken user snippet file must not take the others down with it
                _log.LogWarning(ex, "Could not load snippet file {Path}", c.Path);
            }
        }
        HashSet<string> touched;
        lock (_gate)
        {
            touched = _user.Keys.Concat(loaded.Keys).Select(c => c.LanguageId).ToHashSet();
            _user = loaded;
            _merged.Clear();
        }
        foreach (var language in touched) _changed.Fire(language);
    }

    public void Dispose()
    {
        _subscriptions.Dispose();
        _changed.Clear();
    }
}

/// <summary>
/// Offers every snippet of the document's language to the completion popup (kind Snippet, insert format Snippet), keyed on the snippet's
/// prefix; the popup's own fuzzy filter narrows the list by what has been typed. When the text before the caret is the start of a snippet
/// prefix that contains non-word characters (<c>#reg</c> → <c>#region</c>), the item carries a <see cref="TextEdit"/> covering exactly that
/// typed text, so accepting it does not leave the stray <c>#</c> behind. Registered with the language-feature registry like any other
/// provider, so it is merged with language-server results by the same code.
/// </summary>
public sealed class SnippetCompletionProvider(SnippetService snippets) : ICompletionProvider
{
    public IReadOnlyList<string> TriggerCharacters => [];

    public ValueTask<CompletionList?> ProvideCompletionAsync(ITextDocument document, TextPosition position, CompletionContext context, CancellationToken ct)
    {
        var defs = snippets.GetSnippets(document.LanguageId);
        if (defs.Count == 0) return ValueTask.FromResult<CompletionList?>(null);

        var line = document.GetLineText(position.Line);
        var before = line[..Math.Min(position.Character, line.Length)];
        var items = new List<CompletionItem>(defs.Count);
        foreach (var d in defs)
            foreach (var prefix in d.Prefixes)
            {
                var typed = TypedPrefixLength(before, prefix);
                items.Add(new CompletionItem
                {
                    Label = prefix,
                    LabelDescription = d.Name == prefix ? null : d.Name,
                    Kind = CompletionItemKind.Snippet,
                    Detail = d.Description ?? d.Name,
                    Documentation = MarkupContent.Markdown("```\n" + d.Body + "\n```"),
                    InsertText = d.Body,
                    InsertTextFormat = InsertTextFormat.Snippet,
                    FilterText = prefix,
                    SortText = prefix,
                    // only needed when the typed text reaches beyond the "word" (a non-word character belongs to the prefix)
                    TextEdit = typed > 0 && HasNonWordChar(prefix, typed)
                        ? new TextEdit(TextRange.FromLines(position.Line, before.Length - typed, position.Line, before.Length), d.Body)
                        : null,
                });
            }
        return ValueTask.FromResult<CompletionList?>(new CompletionList(items));
    }

    /// <summary>How many characters just before the caret are the beginning of <paramref name="prefix"/> (longest such, case-insensitive; 0 = none).</summary>
    internal static int TypedPrefixLength(string before, string prefix)
    {
        var max = Math.Min(Math.Min(before.Length, prefix.Length), 64);
        for (var k = max; k >= 1; k--)
            if (string.Compare(before, before.Length - k, prefix, 0, k, StringComparison.OrdinalIgnoreCase) == 0) return k;
        return 0;
    }

    private static bool HasNonWordChar(string prefix, int length)
    {
        for (var i = 0; i < length; i++)
            if (!(char.IsLetterOrDigit(prefix[i]) || prefix[i] == '_')) return true;
        return false;
    }
}
