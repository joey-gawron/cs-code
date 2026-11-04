using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.SpellCheck.Engine;

namespace CsCode.SpellCheck;

/// <summary>
/// The quick fixes of a misspelt word (the lightbulb, Ctrl+. and the editor's right-click Quick Actions): up to
/// <see cref="Settings.SpellSettings.NumSuggestions"/> replacements in the word's own case, "Add to user dictionary", "Add to workspace
/// dictionary" (with a folder open) and "Ignore in this file" (a cspell <c>ignore</c> directive in a comment, where the language has comments).
/// </summary>
public sealed partial class SpellCodeActionProvider(SpellCheckService service, ILanguageService? languages, Func<IWorkspaceService?> workspace) : ICodeActionProvider
{
    public async ValueTask<IReadOnlyList<CodeAction>?> ProvideCodeActionsAsync(ITextDocument document, TextRange range, CodeActionContext context, CancellationToken ct)
    {
        if (context.Only is { Count: > 0 } only && !only.Any(o => o is "quickfix" || "quickfix".StartsWith(o + ".", StringComparison.Ordinal))) return null;
        var target = Find(document, range, context);
        if (target is null) return null;
        var (diagnostic, word, forbidden) = target.Value;
        var actions = new List<CodeAction>();
        var suggestions = await service.SuggestAsync(word, ct).ConfigureAwait(false);
        for (var i = 0; i < suggestions.Count; i++)
        {
            var edit = new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>> { [document.Uri] = [new TextEdit(diagnostic.Range, suggestions[i])] });
            actions.Add(new CodeAction($"Change spelling to \"{suggestions[i]}\"", "quickfix", [diagnostic], IsPreferred: i == 0, Edit: edit));
        }
        if (!forbidden)
        {
            actions.Add(new CodeAction($"Add \"{word}\" to user dictionary", "quickfix", [diagnostic],
                Command: new Command($"Add \"{word}\" to user dictionary", SpellCheckHost.AddWordToUserDictionary, [JsonSerializer.SerializeToElement(word)])));
            if (workspace()?.Folders.Count > 0)
                actions.Add(new CodeAction($"Add \"{word}\" to workspace dictionary", "quickfix", [diagnostic],
                    Command: new Command($"Add \"{word}\" to workspace dictionary", SpellCheckHost.AddWordToWorkspaceDictionary, [JsonSerializer.SerializeToElement(word)])));
            if (IgnoreInFileEdit(document, word) is { } ignore)
                actions.Add(new CodeAction($"Ignore \"{word}\" in this file", "quickfix", [diagnostic], Edit: ignore));
        }
        return actions;
    }

    /// <summary>The spelling diagnostic the request is about: one the editor passed in, else the last result's issue under the range.</summary>
    private (Diagnostic Diagnostic, string Word, bool Forbidden)? Find(ITextDocument document, TextRange range, CodeActionContext context)
    {
        foreach (var d in context.Diagnostics)
        {
            if (d.Source != SpellCheckService.Owner || d.Range.Start.Line != d.Range.End.Line) continue;
            var word = WordOf(d.Message) ?? document.GetText(d.Range);
            if (word.Length == 0) continue;
            return (d, word, d.Message.EndsWith("Forbidden word.", StringComparison.Ordinal));
        }
        if (service.ResultFor(document.Uri) is not { } result || result.Version != document.Version) return null;
        foreach (var issue in result.Issues)
        {
            if (issue.Line < range.Start.Line || issue.Line > range.End.Line) continue;
            var r = new TextRange(new TextPosition(issue.Line, issue.Start), new TextPosition(issue.Line, issue.Start + issue.Length));
            if (r.Start > range.End || r.End < range.Start) continue;
            return (new Diagnostic(r, issue.Message, service.Settings.Severity, Source: SpellCheckService.Owner), issue.Word, issue.Forbidden);
        }
        return null;
    }

    [GeneratedRegex("^\"(?<w>.+)\": (?:Unknown|Forbidden) word\\.$")]
    private static partial Regex MessagePattern();

    private static string? WordOf(string message) => MessagePattern().Match(message) is { Success: true } m ? m.Groups["w"].Value : null;

    [GeneratedRegex(@"(?:cspell|spell-?checker)\s*:\s*ignore\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IgnoreDirective();

    /// <summary>
    /// The edit that ignores <paramref name="word"/> in this file: the word appended to the file's first <c>cspell:ignore</c> directive, or a new
    /// directive comment as the first line (after a <c>#!</c> line, an XML declaration or Markdown front matter). Null where it would not fit
    /// cleanly: a language without comments, JSON.
    /// </summary>
    public TextEdit? IgnoreInFile(ITextDocument document, string word)
    {
        if (document.LanguageId is "json" || languages is null) return null;
        var config = languages.GetConfiguration(document.LanguageId);
        var lineCount = Math.Min(document.LineCount, 5000);
        for (var line = 0; line < lineCount; line++)
        {
            var text = document.GetLineText(line);
            if (IgnoreDirective().Match(text) is not { Success: true } m) continue;
            var end = text.Length;
            foreach (var closer in (string[])["*/", "-->"])
            {
                var at = text.IndexOf(closer, m.Index, StringComparison.Ordinal);
                if (at >= 0) end = Math.Min(end, at);
            }
            while (end > m.Index + m.Length && char.IsWhiteSpace(text[end - 1])) end--;
            return TextEdit.Insert(new TextPosition(line, end), " " + word);
        }
        string directive;
        if (config.LineComment is { Length: > 0 } lineComment) directive = $"{lineComment} cspell:ignore {word}";
        else if (config.BlockComment is { } block) directive = $"{block.Open} cspell:ignore {word} {block.Close}";
        else return null;
        var eol = document is IEditorDocument { EndOfLine: { Length: > 0 } e } ? e : "\n";
        return TextEdit.Insert(new TextPosition(FirstLineForDirective(document), 0), directive + eol);
    }

    private WorkspaceEdit? IgnoreInFileEdit(ITextDocument document, string word) =>
        IgnoreInFile(document, word) is { } edit ? new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>> { [document.Uri] = [edit] }) : null;

    private static int FirstLineForDirective(ITextDocument document)
    {
        if (document.LineCount == 0) return 0;
        var first = document.GetLineText(0);
        if (first.StartsWith("#!", StringComparison.Ordinal) || first.StartsWith("<?xml", StringComparison.Ordinal)) return Math.Min(1, document.LineCount);
        if (first.TrimEnd() == "---")
        {
            var limit = Math.Min(document.LineCount, 500);
            for (var i = 1; i < limit; i++)
                if (document.GetLineText(i).TrimEnd() is "---" or "...") return i + 1 < document.LineCount ? i + 1 : i;
        }
        return 0;
    }
}
