namespace CsCode.Core.Text;

/// <summary>
/// Whether .gitignore rules match case-insensitively, as git decides it: <c>core.ignorecase</c> from the repository's config, which
/// <c>git init</c> and <c>git clone</c> set to true on a case-insensitive file system (a stock Mac, Windows). Without a repository or the key,
/// the platform's usual file system decides (<see cref="PlatformDefault"/>).
/// </summary>
public static class GitIgnoreCase
{
    /// <summary>True on macOS and Windows, whose default file systems ignore case; false elsewhere.</summary>
    public static bool PlatformDefault { get; } = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    /// <summary>The <c>core.ignorecase</c> value in a git config file's text (the last one wins), or null when it is not set or not a boolean.</summary>
    public static bool? Parse(string configText)
    {
        bool? value = null;
        var inCore = false;
        foreach (var raw in configText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                var close = line.IndexOf(']', StringComparison.Ordinal);
                inCore = close > 0 && line[1..close].Trim().Equals("core", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inCore) continue;
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            var key = (eq < 0 ? line : line[..eq]).Trim();
            if (!key.Equals("ignorecase", StringComparison.OrdinalIgnoreCase)) continue;
            if (eq < 0) { value = true; continue; } // a bare key is true
            var text = line[(eq + 1)..];
            var comment = text.IndexOfAny(['#', ';']);
            if (comment >= 0) text = text[..comment];
            value = text.Trim().Trim('"').ToLowerInvariant() switch
            {
                "true" or "yes" or "on" or "1" => true,
                "false" or "no" or "off" or "0" or "" => false,
                _ => value,
            };
        }
        return value;
    }
}
