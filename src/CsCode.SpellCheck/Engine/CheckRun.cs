using System.Diagnostics;
using System.Text.RegularExpressions;
using CsCode.Core.Languages;
using CsCode.SpellCheck.Settings;
using CsCode.SpellCheck.Text;

namespace CsCode.SpellCheck.Engine;

/// <summary>A misspelt (or forbidden) word: <see cref="Line"/>, <see cref="Start"/> column and <see cref="Length"/> in UTF-16 code units.</summary>
public sealed record SpellIssue(int Line, int Start, int Length, string Word, bool Forbidden)
{
    public string Message => Forbidden ? $"\"{Word}\": Forbidden word." : $"\"{Word}\": Unknown word.";
}

/// <summary>
/// One check of one document text: which parts of each line are read (the editor's token kinds: comments, strings and prose; code too with
/// <see cref="SpellSettings.CheckIdentifiers"/>; the whole line in a prose language the editor has no grammar for), the words in them
/// (<see cref="WordSplitter"/>), and what the settings, the file's own directives and the dictionaries say about each. Runs in slices
/// (<see cref="Step"/>) so the spell lane can stop between them when the text changes; pure otherwise (no I/O, no shared state but the
/// word checker's cache).
/// </summary>
public sealed class CheckRun
{
    /// <summary>Only this much of a line is read (a minified line in a string is not prose).</summary>
    public const int MaxLineLength = 10_000;

    private readonly string _text;
    private readonly SyntaxClassification _classification;
    private readonly bool _prose;
    private readonly SpellSettings _settings;
    private readonly IWordChecker _checker;
    private readonly List<SpellIssue> _issues = [];
    private readonly Dictionary<string, int> _perWord = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SyntaxTextRun> _runs = [];
    private readonly List<WordSpan> _words = [];
    private int[] _lineStarts = [];
    private (int Start, int End)[] _ignored = [];
    private InlineDirectives _directives = InlineDirectives.None;
    private bool _prepared;
    private int _line;
    private int _ignoredCursor;

    public CheckRun(string text, SyntaxClassification classification, bool prose, SpellSettings settings, IWordChecker checker)
    {
        _text = text.Length > settings.CheckLimitKb * 1024 ? text[..(settings.CheckLimitKb * 1024)] : text;
        _classification = classification;
        _prose = prose;
        _settings = settings;
        _checker = checker;
    }

    public IReadOnlyList<SpellIssue> Issues => _issues;

    /// <summary>Stopped at <see cref="SpellSettings.MaxNumberOfProblems"/>.</summary>
    public bool Truncated { get; private set; }

    public bool IsFinished { get; private set; }

    public int WordsChecked { get; private set; }

    public InlineDirectives Directives => _directives;

    /// <summary>Checks the whole text at once (tests, small texts).</summary>
    public static IReadOnlyList<SpellIssue> CheckAll(string text, SyntaxClassification classification, bool prose, SpellSettings settings, IWordChecker checker)
    {
        var run = new CheckRun(text, classification, prose, settings, checker);
        while (!run.Step(TimeSpan.MaxValue, CancellationToken.None)) { }
        return run.Issues;
    }

    /// <summary>Checks lines until <paramref name="budget"/> is spent (at least one line); true once the whole text is done.</summary>
    public bool Step(TimeSpan budget, CancellationToken ct)
    {
        if (IsFinished) return true;
        var started = Stopwatch.GetTimestamp();
        if (!_prepared)
        {
            Prepare();
            _prepared = true;
        }
        var lineCount = _lineStarts.Length;
        while (_line < lineCount)
        {
            ct.ThrowIfCancellationRequested();
            CheckLine(_line++);
            if (_issues.Count >= _settings.MaxNumberOfProblems)
            {
                Truncated = true;
                break;
            }
            if (Stopwatch.GetElapsedTime(started) >= budget) return false;
        }
        IsFinished = true;
        return true;
    }

    private void Prepare()
    {
        var starts = new List<int>(Math.Max(16, _text.Length / 40)) { 0 };
        for (var i = 0; i < _text.Length; i++)
        {
            var c = _text[i];
            if (c == '\r')
            {
                if (i + 1 < _text.Length && _text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (c == '\n') starts.Add(i + 1);
        }
        _lineStarts = starts.ToArray();
        _directives = InlineDirectives.Parse(_text, LineOf);
        _ignored = IgnoredRanges(_text, _settings.IgnoreRegExps);
    }

    private int LineOf(int offset)
    {
        var i = Array.BinarySearch(_lineStarts, offset);
        return i >= 0 ? i : ~i - 1;
    }

    private void CheckLine(int line)
    {
        if (_directives.IsDisabled(line)) return;
        var start = _lineStarts[line];
        var end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : _text.Length;
        while (end > start && _text[end - 1] is '\n' or '\r') end--;
        if (end - start > MaxLineLength) end = start + MaxLineLength;
        if (end <= start) return;
        var text = _text.AsSpan(start, end - start);

        if (_classification.State == SyntaxClassificationState.Tokenized && line < _classification.LineCount && _classification.TryGetRuns(line, _runs))
        {
            for (var r = 0; r < _runs.Count; r++)
            {
                if (!IsRead(_runs[r].Kind)) continue;
                var from = Math.Min(_runs[r].Start, text.Length);
                var to = r + 1 < _runs.Count ? Math.Min(_runs[r + 1].Start, text.Length) : text.Length;
                // adjacent read runs (a comment, then a string in it...) are one stretch, so a word is never cut at a run boundary
                while (r + 1 < _runs.Count && IsRead(_runs[r + 1].Kind))
                {
                    r++;
                    to = r + 1 < _runs.Count ? Math.Min(_runs[r + 1].Start, text.Length) : text.Length;
                }
                if (to > from) CheckStretch(line, start, text, from, to);
            }
        }
        else if (_prose && _classification.State is SyntaxClassificationState.NoGrammar or SyntaxClassificationState.Unavailable)
        {
            CheckStretch(line, start, text, 0, text.Length);
        }
    }

    private bool IsRead(SyntaxTextKind kind) => kind switch
    {
        SyntaxTextKind.Comment or SyntaxTextKind.String or SyntaxTextKind.Text => true,
        SyntaxTextKind.Code => _settings.CheckIdentifiers,
        _ => false,
    };

    private void CheckStretch(int line, int lineStart, ReadOnlySpan<char> text, int from, int to)
    {
        if (_issues.Count >= _settings.MaxNumberOfProblems) return;
        _words.Clear();
        WordSplitter.Split(text, from, to, _words);
        var words = _settings.Words.GetAlternateLookup<ReadOnlySpan<char>>();
        var ignore = _settings.IgnoreWords.GetAlternateLookup<ReadOnlySpan<char>>();
        var flags = _settings.FlagWords.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var w in _words)
        {
            if (w.Length < _settings.MinWordLength) continue;
            if (IsIgnoredByPattern(lineStart + w.Start, w.Length)) continue;
            var span = text.Slice(w.Start, w.Length);
            WordsChecked++;
            bool forbidden;
            if (flags.ContainsKey(span) || (_directives.FlaggedWords.Count > 0 && _directives.FlaggedWords.Contains(span.ToString()))) forbidden = true;
            else if (words.Contains(span) || ignore.Contains(span) || (_directives.Words.Count > 0 && _directives.Words.Contains(span.ToString()))) continue;
            else if (_checker.IsKnown(span)) continue;
            else forbidden = false;

            var word = span.ToString();
            var seen = _perWord.GetValueOrDefault(word) + 1;
            _perWord[word] = seen;
            if (seen > _settings.MaxDuplicateProblems) continue;
            _issues.Add(new SpellIssue(line, w.Start, w.Length, word, forbidden));
            if (_issues.Count >= _settings.MaxNumberOfProblems) return;
        }
    }

    /// <summary>True when an <c>ignoreRegExpList</c> match overlaps the word (words arrive in text order, so a cursor walks the sorted ranges).</summary>
    private bool IsIgnoredByPattern(int offset, int length)
    {
        if (_ignored.Length == 0) return false;
        while (_ignoredCursor < _ignored.Length && _ignored[_ignoredCursor].End <= offset) _ignoredCursor++;
        for (var i = _ignoredCursor; i < _ignored.Length && _ignored[i].Start < offset + length; i++)
            if (_ignored[i].End > offset) return true;
        return false;
    }

    private static (int Start, int End)[] IgnoredRanges(string text, IReadOnlyList<Regex> patterns)
    {
        if (patterns.Count == 0) return [];
        var ranges = new List<(int Start, int End)>();
        foreach (var regex in patterns)
        {
            try
            {
                for (var m = regex.Match(text); m.Success; m = m.NextMatch())
                    if (m.Length > 0) ranges.Add((m.Index, m.Index + m.Length));
            }
            catch (RegexMatchTimeoutException)
            {
                // a pattern that takes too long on this text ignores what it matched so far
            }
        }
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int End)>();
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, r.End));
            else merged.Add(r);
        }
        return merged.ToArray();
    }
}
