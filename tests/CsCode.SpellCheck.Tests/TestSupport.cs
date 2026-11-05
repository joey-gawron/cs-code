using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.SpellCheck.Engine;
using CsCode.SpellCheck.Settings;

namespace CsCode.SpellCheck.Tests;

/// <summary>Runs per line, written by hand: what the editor's token store would say.</summary>
internal sealed class FakeClassification(IReadOnlyList<SyntaxTextRun[]?> lines) : SyntaxClassification
{
    public override SyntaxClassificationState State => SyntaxClassificationState.Tokenized;
    public override int LineCount => lines.Count;

    public override bool TryGetRuns(int line, List<SyntaxTextRun> runs)
    {
        runs.Clear();
        if (line >= lines.Count || lines[line] is not { } row) return false;
        runs.AddRange(row);
        return true;
    }

    /// <summary>
    /// A classification by a tiny convention for tests: per line, text after <c>//</c> is a comment, text between double quotes a string,
    /// the rest code. (The real one comes from the editor's grammar; see the editor tests.)
    /// </summary>
    public static FakeClassification SlashComments(string text)
    {
        var rows = new List<SyntaxTextRun[]?>();
        foreach (var line in text.Split('\n'))
        {
            var runs = new List<SyntaxTextRun> { new(0, SyntaxTextKind.Code) };
            var inString = false;
            for (var i = 0; i < line.Length; i++)
            {
                if (!inString && i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
                {
                    runs.Add(new SyntaxTextRun(i, SyntaxTextKind.Comment));
                    break;
                }
                if (line[i] == '"')
                {
                    inString = !inString;
                    runs.Add(new SyntaxTextRun(inString ? i : i + 1, inString ? SyntaxTextKind.String : SyntaxTextKind.Code));
                }
            }
            rows.Add(runs.Where((r, i) => i == runs.Count - 1 || runs[i + 1].Start != r.Start).ToArray());
        }
        return new FakeClassification(rows);
    }
}

/// <summary>A classification service the tests drive: what the editor would report for each document.</summary>
internal sealed class FakeClassifier : ISyntaxClassificationService
{
    private readonly Dictionary<Uri, SyntaxClassification> _map = new();
    private readonly Emitter<SyntaxClassificationChange> _changed = new();

    public IEventSource<SyntaxClassificationChange> Changed => _changed;

    public SyntaxClassification Capture(Uri uri)
    {
        lock (_map) return _map.TryGetValue(uri, out var c) ? c : SyntaxClassification.Empty(SyntaxClassificationState.Unavailable);
    }

    public void Set(Uri uri, SyntaxClassification classification, bool notify = true)
    {
        lock (_map) _map[uri] = classification;
        if (notify) _changed.Fire(new SyntaxClassificationChange(uri, 0, classification.LineCount));
    }
}

/// <summary>A word checker with a fixed vocabulary (any case).</summary>
internal sealed class FixedWords(params string[] words) : IWordChecker
{
    private readonly HashSet<string> _words = new(words, StringComparer.OrdinalIgnoreCase);

    public bool IsKnown(ReadOnlySpan<char> word) => _words.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word);

    public IReadOnlyList<string> Suggest(string word, int max) => [];
}

internal static class Dictionaries
{
    private static readonly Lazy<(IReadOnlyList<DictionarySource> Sources, WeCantSpell.Hunspell.WordList[] Lists)> Bundled = new(() =>
    {
        var (chosen, _) = DictionaryCatalog.Choose(DictionaryCatalog.Discover(null), ["en"]);
        return (chosen, chosen.Select(DictionaryCatalog.Load).ToArray());
    });

    /// <summary>
    /// The bundled en_US plus the software terms. The word list loads once for the test run (Hunspell's lists may be queried from several
    /// threads); each caller gets its own set, whose verdict cache is single-threaded like the spell lane's.
    /// </summary>
    public static SpellDictionarySet EnUs => new(Bundled.Value.Sources, Bundled.Value.Lists, DictionaryCatalog.LoadSoftwareTerms(), []);
}

internal static class SettingsFor
{
    public static SpellSettings With(Func<SpellSettings, SpellSettings> change) => change(SpellSettings.Default);
}
