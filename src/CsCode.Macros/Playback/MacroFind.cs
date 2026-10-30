using System.Text.RegularExpressions;

namespace CsCode.Macros;

/// <summary>
/// The search of a recorded Find step (<see cref="MacroCommands.Find"/>): pure, run off the UI thread on a snapshot of the document. Unlike the
/// editor's interactive find it never wraps around: at the end of the file (the start, backwards) it finds nothing, which ends "Play until search
/// fails". Forwards it looks from the end of the selection (the previous match) or the caret; backwards, before the selection's start or the caret.
/// </summary>
public static class MacroFind
{
    /// <summary>A regular expression gets this long per search (catastrophic patterns fail instead of hanging the search).</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The match (offset, length) of <paramref name="args"/> in <paramref name="text"/>, or null.</summary>
    /// <param name="selectionStart">Start offset of the selection (equals <paramref name="selectionEnd"/> when nothing is selected: the caret).</param>
    /// <exception cref="ArgumentException">An invalid regular expression.</exception>
    /// <exception cref="RegexMatchTimeoutException">The pattern took longer than <see cref="RegexTimeout"/>.</exception>
    public static (int Start, int Length)? Search(string text, int selectionStart, int selectionEnd, MacroFindArgs args)
    {
        if (string.IsNullOrEmpty(args.Text)) return null;
        var regex = Compile(args);
        if (!args.Backwards)
        {
            var from = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, text.Length);
            for (var m = regex.Match(text, from); m.Success; m = m.NextMatch())
            {
                // an empty match right at the end of the previous one would be found again and again
                if (m.Length == 0 && m.Index == from && selectionEnd != selectionStart) continue;
                return (m.Index, m.Length);
            }
            return null;
        }
        var before = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length);
        (int, int)? last = null;
        for (var m = regex.Match(text); m.Success && m.Index < before; m = m.NextMatch()) last = (m.Index, m.Length);
        return last;
    }

    private static Regex Compile(MacroFindArgs args)
    {
        var pattern = args.Regex ? args.Text : Regex.Escape(args.Text);
        if (args.WholeWord) pattern = $@"(?<![\p{{L}}\p{{N}}_])(?:{pattern})(?![\p{{L}}\p{{N}}_])";
        var options = RegexOptions.CultureInvariant | RegexOptions.Multiline | (args.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        return new Regex(pattern, options, RegexTimeout);
    }
}
