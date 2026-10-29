namespace CsCode.Sessions;

/// <summary>
/// Session names double as file names (<c>sessions/&lt;name&gt;.json</c>, <c>backups/&lt;name&gt;/</c>, the lock file), so they are restricted to a
/// portable set: letters, digits, space, <c>- _ . ( ) +</c>; 1..64 characters; no leading/trailing space or dot. Names compare ordinally
/// (case-sensitive, like the Linux file system they live on).
/// </summary>
public static class SessionNames
{
    public const int MaxLength = 64;

    /// <summary>Prefix of the automatic names of unnamed sessions (<c>window-1</c>, <c>window-2</c>, ...).</summary>
    public const string UnnamedPrefix = "window-";

    /// <summary>Null when <paramref name="name"/> is valid, otherwise why not (shown to the user).</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "A session name cannot be empty.";
        if (name.Length > MaxLength) return $"A session name can have at most {MaxLength} characters.";
        if (name[0] is ' ' or '.' || name[^1] is ' ' or '.') return "A session name cannot start or end with a space or a dot.";
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')' or '+') continue;
            return $"A session name cannot contain '{c}' (use letters, digits, spaces and - _ . ( ) +).";
        }
        return null;
    }

    public static bool IsValid(string? name) => Validate(name) is null;

    /// <summary>The lowest free <c>window-N</c> name not in <paramref name="taken"/>.</summary>
    public static string NextUnnamed(IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.Ordinal);
        for (var i = 1; ; i++)
        {
            var name = UnnamedPrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!used.Contains(name)) return name;
        }
    }
}
