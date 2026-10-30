namespace CsCode.Editor.TreeSitter.Grammars;

/// <summary>Grammars that must not be loaded (an earlier process crashed, or this one hung, inside them). Thread-safe.</summary>
public interface IGrammarQuarantine
{
    /// <summary>
    /// Why grammar <paramref name="grammar"/> in the build <paramref name="identity"/> (see <see cref="GrammarIdentity"/>) is refused, or null
    /// when it may be used. A new build of the grammar (another identity) is never refused for what an old one did.
    /// </summary>
    string? Reason(string grammar, string identity);
}

/// <summary>A short, stable id of one build of a grammar library: its bundle digest, or (outside the bundle) a digest of path, size and time.</summary>
public static class GrammarIdentity
{
    public static string Of(GrammarFiles files)
    {
        if (files.Bundle?.Grammar(files.Name) is { } bundled) return bundled.Sha256[..16];
        long size = -1, ticks = 0;
        try
        {
            var info = new FileInfo(files.ParserPath);
            if (info.Exists)
            {
                size = info.Length;
                ticks = info.LastWriteTimeUtc.Ticks;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{files.ParserPath}|{size}|{ticks}");
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
    }
}
