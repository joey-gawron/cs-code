using System.Reflection;
using WeCantSpell.Hunspell;

namespace CsCode.SpellCheck.Engine;

/// <summary>One Hunspell dictionary on offer: the bundled en_US or a <c>.dic</c>/<c>.aff</c> pair from <c>{UserDataDir}/dictionaries</c>.</summary>
/// <param name="Locale">Normalised file name (<c>en_us</c>, <c>de_de_frami</c>): what <c>cSpell.language</c> entries are matched against.</param>
public sealed record DictionarySource(string Name, string Locale, string? DicPath, string? AffPath, long DicBytes, DateTime Stamp)
{
    public bool IsBundled => DicPath is null;
}

/// <summary>
/// The dictionaries on offer and the choice <c>cSpell.language</c> makes among them: <c>"en"</c> is the bundled en_US (cspell's meaning),
/// <c>"en-GB"</c> / <c>"en_GB"</c> a dictionary named <c>en_GB</c>, <c>"de"</c> the first dictionary whose name starts with <c>de</c>
/// (<c>de_DE</c> before <c>de_AT</c> when both are there). User dictionaries are files: <c>{UserDataDir}/dictionaries/de_DE.dic</c> + <c>.aff</c>.
/// </summary>
public static class DictionaryCatalog
{
    public const string BundledName = "en_US";
    private const string ResourcePrefix = "CsCode.SpellCheck.Dictionaries.";

    /// <summary>The bundled en_US plus every pair in <paramref name="userDirectory"/> (missing directory: none). File I/O: call off the UI thread.</summary>
    public static IReadOnlyList<DictionarySource> Discover(string? userDirectory)
    {
        var list = new List<DictionarySource> { new(BundledName, Normalise(BundledName), null, null, BundledDicBytes(), DateTime.MinValue) };
        if (userDirectory is null || !Directory.Exists(userDirectory)) return list;
        try
        {
            foreach (var dic in Directory.EnumerateFiles(userDirectory, "*.dic").Order(StringComparer.Ordinal))
            {
                var aff = Path.ChangeExtension(dic, ".aff");
                if (!File.Exists(aff)) continue;
                var info = new FileInfo(dic);
                var name = Path.GetFileNameWithoutExtension(dic);
                list.Add(new DictionarySource(name, Normalise(name), dic, aff, info.Length, Max(info.LastWriteTimeUtc, File.GetLastWriteTimeUtc(aff))));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an unreadable folder offers nothing more; the bundled dictionary still works
        }
        return list;
    }

    /// <summary>
    /// The dictionaries <paramref name="languages"/> (the entries of <c>cSpell.language</c>) select, in order, without duplicates; and the entries
    /// nothing matched. A user dictionary of the exact name wins over the bundled one (an <c>en_US.dic</c> of your own replaces it).
    /// </summary>
    public static (IReadOnlyList<DictionarySource> Chosen, IReadOnlyList<string> Missing) Choose(IReadOnlyList<DictionarySource> available, IReadOnlyList<string> languages)
    {
        var chosen = new List<DictionarySource>();
        var missing = new List<string>();
        foreach (var raw in languages)
        {
            var wanted = Normalise(raw);
            if (wanted.Length == 0) continue;
            if (wanted is "en") wanted = "en_us";
            var exact = available.Where(d => d.Locale == wanted).OrderBy(d => d.IsBundled).FirstOrDefault();
            var match = exact
                ?? available.Where(d => d.Locale.StartsWith(wanted + "_", StringComparison.Ordinal))
                    .OrderBy(d => d.Locale == wanted + "_" + wanted ? 0 : 1).ThenBy(d => d.Locale, StringComparer.Ordinal).FirstOrDefault();
            if (match is null) missing.Add(raw.Trim());
            else if (!chosen.Contains(match)) chosen.Add(match);
        }
        if (chosen.Count == 0 && missing.Count == 0) chosen.Add(available[0]); // "cSpell.language": "" → the default
        return (chosen, missing);
    }

    /// <summary>Loads a dictionary (tens of milliseconds and ~11 MB for en_US). File and resource I/O: the spell lane only.</summary>
    public static WordList Load(DictionarySource source)
    {
        if (source.IsBundled)
        {
            using var dic = OpenResource(BundledName + ".dic");
            using var aff = OpenResource(BundledName + ".aff");
            return WordList.CreateFromStreams(dic, aff);
        }
        return WordList.CreateFromFiles(source.DicPath!, source.AffPath!);
    }

    /// <summary>CsCode's software terms and language keywords (lower case).</summary>
    public static HashSet<string> LoadSoftwareTerms()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(OpenResource("software-terms.txt"));
        while (reader.ReadLine() is { } line)
        {
            var word = line.Trim();
            if (word.Length == 0 || word[0] == '#') continue;
            set.Add(word);
        }
        return set;
    }

    /// <summary><c>en-US</c>, <c>en_us</c> and <c>EN_US</c> are one name.</summary>
    public static string Normalise(string name) => name.Trim().Replace('-', '_').ToLowerInvariant();

    private static Stream OpenResource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + name)
        ?? throw new InvalidOperationException($"The spell checker's resource {name} is missing from the assembly.");

    private static long BundledDicBytes()
    {
        using var s = OpenResource(BundledName + ".dic");
        return s.Length;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
