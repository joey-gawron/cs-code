using System.Reflection;

namespace CsCode.Editor.TreeSitter.Grammars;

/// <summary>
/// Queries CsCode writes itself (<c>OwnQueries/&lt;grammar&gt;/&lt;kind&gt;.scm</c>, embedded in this assembly, part of CsCode itself): the
/// outline (<c>symbols.scm</c>, in the tags format) for grammars whose project ships no <c>tags.scm</c> (more kinds can follow, e.g. Vim text objects). They are
/// read when no query directory has a file of that name, so the bundle (or a user's query folder) can still override them.
/// </summary>
public static class OwnQueries
{
    private const string Prefix = "cscode.queries/";
    private static readonly Assembly Asm = typeof(OwnQueries).Assembly;
    private static readonly Lazy<HashSet<string>> Names = new(() => new HashSet<string>(Asm.GetManifestResourceNames(), StringComparer.Ordinal));

    /// <summary>The text of CsCode's own <paramref name="kind"/> query for <paramref name="grammar"/>, or null.</summary>
    public static string? Read(string grammar, string kind)
    {
        var name = Prefix + grammar + "/" + kind + ".scm";
        if (!Names.Value.Contains(name)) return null;
        using var stream = Asm.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The grammars that have an own query of <paramref name="kind"/> (tests).</summary>
    public static IEnumerable<string> GrammarsWith(string kind) =>
        Names.Value.Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith("/" + kind + ".scm", StringComparison.Ordinal))
            .Select(n => n[Prefix.Length..n.LastIndexOf('/')]);
}
