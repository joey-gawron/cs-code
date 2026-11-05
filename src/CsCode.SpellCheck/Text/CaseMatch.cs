namespace CsCode.SpellCheck.Text;

/// <summary>Gives a suggestion the case of the word it replaces: <c>Recieve</c> → <c>Receive</c>, <c>RECIEVE</c> → <c>RECEIVE</c>.</summary>
public static class CaseMatch
{
    public static string Apply(string original, string suggestion)
    {
        if (original.Length == 0 || suggestion.Length == 0) return suggestion;
        var letters = 0;
        var upper = 0;
        foreach (var c in original)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (char.IsUpper(c)) upper++;
        }
        if (letters > 1 && upper == letters) return suggestion.ToUpperInvariant();
        if (char.IsUpper(original[0]) && !char.IsUpper(suggestion[0]))
            return string.Concat(char.ToUpperInvariant(suggestion[0]).ToString(), suggestion.AsSpan(1));
        // lower case (or mixed) original: the dictionary's own casing (a proper noun stays capitalised)
        return suggestion;
    }
}
