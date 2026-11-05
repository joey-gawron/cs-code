using CsCode.Core.Text;

namespace CsCode.Editor.Editing;

/// <summary>
/// VS Code's Detect Indentation from Content (its <c>guessIndentation</c>, indentationGuesser.ts) and Convert Indentation to Spaces / Tabs
/// (indentation.ts), as pure functions over a document. Safe on any thread (a document read off the UI thread reads its snapshot).
/// </summary>
public static class IndentationOperations
{
    /// <summary>VS Code looks at most at this many lines.</summary>
    private const int MaxLines = 10_000;
    private static readonly int[] AllowedTabSizes = [2, 4, 6, 8, 3, 5, 7];
    private const int MaxTabSizeGuess = 8;

    /// <summary>
    /// Whether the document indents with spaces and by how many (VS Code's algorithm: tabs against spaces by the lines that start with each, the
    /// size by the most common indentation step between consecutive lines, alignment-looking steps ignored, 2 preferred over 4 when it is
    /// common enough). Lines that are blank or whitespace only do not count; without evidence the defaults stand.
    /// </summary>
    public static (int TabSize, bool InsertSpaces) Guess(ITextDocument doc, int defaultTabSize, bool defaultInsertSpaces)
    {
        var lineCount = Math.Min(doc.LineCount, MaxLines);
        int withTabs = 0, withSpaces = 0;
        var previousText = "";
        var previousIndent = 0;
        var spacesDiffCount = new int[MaxTabSizeGuess + 1];
        for (var line = 0; line < lineCount; line++)
        {
            var text = doc.GetLineText(line);
            var hasContent = false;
            int indent = 0, spaces = 0, tabs = 0;
            for (var j = 0; j < text.Length; j++)
            {
                var c = text[j];
                if (c == '\t') tabs++;
                else if (c == ' ') spaces++;
                else
                {
                    hasContent = true;
                    indent = j;
                    break;
                }
            }
            if (!hasContent) continue;
            if (tabs > 0) withTabs++;
            else if (spaces > 1) withSpaces++;

            var (diff, alignment) = SpacesDiff(previousText, previousIndent, text, indent);
            if (alignment && !(defaultInsertSpaces && defaultTabSize == diff)) continue;
            if (diff <= MaxTabSizeGuess) spacesDiffCount[diff]++;
            previousText = text;
            previousIndent = indent;
        }

        var insertSpaces = defaultInsertSpaces;
        if (withTabs != withSpaces) insertSpaces = withTabs < withSpaces;
        var tabSize = defaultTabSize;
        if (insertSpaces)
        {
            var best = 0;
            foreach (var size in AllowedTabSizes)
                if (spacesDiffCount[size] > best)
                {
                    best = spacesDiffCount[size];
                    tabSize = size;
                }
            // a tab size of 2 wins even if it is not the most common, when 4 was guessed (VS Code)
            if (tabSize == 4 && spacesDiffCount[4] > 0 && spacesDiffCount[2] > 0 && spacesDiffCount[2] >= spacesDiffCount[4] / 2) tabSize = 2;
        }
        return (tabSize, insertSpaces);
    }

    /// <summary>VS Code's spacesDiff: the indentation step from one line to the next, and whether it looks like alignment rather than indentation.</summary>
    private static (int Diff, bool Alignment) SpacesDiff(string a, int aLength, string b, int bLength)
    {
        var i = 0;
        while (i < aLength && i < bLength && a[i] == b[i]) i++;
        int aSpaces = 0, aTabs = 0, bSpaces = 0, bTabs = 0;
        for (var j = i; j < aLength; j++)
            if (a[j] == ' ') aSpaces++; else aTabs++;
        for (var j = i; j < bLength; j++)
            if (b[j] == ' ') bSpaces++; else bTabs++;
        if ((aSpaces > 0 && aTabs > 0) || (bSpaces > 0 && bTabs > 0)) return (0, false);
        var tabsDiff = Math.Abs(aTabs - bTabs);
        var spacesDiff = Math.Abs(aSpaces - bSpaces);
        if (tabsDiff == 0)
        {
            // const a = b + c,
            //       d = b || c;   is alignment, not an indentation step
            var alignment = spacesDiff > 0 && bSpaces - 1 >= 0 && bSpaces - 1 < a.Length && bSpaces < b.Length
                && b[bSpaces] != ' ' && a[bSpaces - 1] == ' ' && a.Length > 0 && a[^1] == ',';
            return (spacesDiff, alignment);
        }
        return spacesDiff % tabsDiff == 0 ? (spacesDiff / tabsDiff, false) : (0, false);
    }

    /// <summary>
    /// The edits of VS Code's Convert Indentation to Spaces (each tab of a line's leading whitespace becomes <paramref name="tabSize"/> spaces) or
    /// to Tabs (each run of <paramref name="tabSize"/> spaces in it becomes a tab; shorter runs stay). Whitespace-only lines are converted whole.
    /// One edit per changed line; apply them as one undo step.
    /// </summary>
    public static IReadOnlyList<TextEdit> Convert(ITextDocument doc, int tabSize, bool toSpaces)
    {
        tabSize = Math.Max(1, tabSize);
        var spaces = new string(' ', tabSize);
        var edits = new List<TextEdit>();
        for (var line = 0; line < doc.LineCount; line++)
        {
            var text = doc.GetLineText(line);
            var end = 0;
            while (end < text.Length && text[end] is ' ' or '\t') end++;
            if (end == 0) continue;
            var indentation = text[..end];
            var converted = toSpaces ? indentation.Replace("\t", spaces, StringComparison.Ordinal) : indentation.Replace(spaces, "\t", StringComparison.Ordinal);
            if (!string.Equals(converted, indentation, StringComparison.Ordinal)) edits.Add(new TextEdit(TextRange.FromLines(line, 0, line, end), converted));
        }
        return edits;
    }
}
