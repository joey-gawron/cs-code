using System.Text.Json;
using System.Text.Json.Serialization;
using CsCode.Editor.TreeSitter.Grammars;

namespace CsCode.Editor.TreeSitter.Isolation;

/// <summary>
/// Grammars CsCode stopped trusting, persisted in <c>{UserDataDir}/tree-sitter/quarantine.json</c> so that a grammar that takes the process down
/// does not do it again on the next start (session restore reopens the same file): a grammar build is quarantined after
/// <see cref="CrashesBeforeQuarantine"/> crashes inside it (one crash may be the user killing a busy process) or after ONE hang (its lane is
/// lost for the rest of the process). A quarantined grammar is never loaded; its languages are coloured by TextMate (or not at all, for the
/// languages TextMate has no grammar for). Entries are per grammar BUILD (<see cref="GrammarIdentity"/>): a new build of the grammar starts
/// clean. <see cref="Clear"/> forgives everything (the user's command). Thread-safe; file errors are ignored (in memory only then).
/// </summary>
public sealed class GrammarQuarantine : IGrammarQuarantine
{
    public const int CrashesBeforeQuarantine = 2;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly string? _file;
    private readonly object _gate = new();
    private List<Entry> _entries;

    public sealed record Entry(string Grammar, string Identity, int Crashes, int Hangs, DateTime LastUtc, string? Detail);

    /// <param name="file">Where the entries persist; null = this process only.</param>
    public GrammarQuarantine(string? file)
    {
        _file = file;
        _entries = Load(file);
    }

    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_gate) return _entries.ToArray();
        }
    }

    public string? Reason(string grammar, string identity)
    {
        lock (_gate)
        {
            var e = _entries.Find(x => x.Grammar == grammar && x.Identity == identity);
            if (e is null) return null;
            if (e.Hangs > 0) return $"it stopped responding (an endless loop in the grammar's code){(e.Detail is null ? "" : ": " + e.Detail)}";
            if (e.Crashes >= CrashesBeforeQuarantine) return $"CsCode closed unexpectedly {e.Crashes} times while running it";
            return null;
        }
    }

    /// <summary>A process died inside the grammar. Returns true when this made it quarantined.</summary>
    public bool RecordCrash(string grammar, string identity) => Record(grammar, identity, crash: true, detail: null);

    /// <summary>This process hung inside the grammar (quarantined at once). Returns true when this made it quarantined.</summary>
    public bool RecordHang(string grammar, string identity, string detail) => Record(grammar, identity, crash: false, detail);

    private bool Record(string grammar, string identity, bool crash, string? detail)
    {
        lock (_gate)
        {
            // another process may have recorded something meanwhile (background mode runs several): merge with the file first
            _entries = Merge(_entries, Load(_file));
            var before = Reason(grammar, identity) is not null;
            var i = _entries.FindIndex(x => x.Grammar == grammar && x.Identity == identity);
            var e = i >= 0 ? _entries[i] : new Entry(grammar, identity, 0, 0, DateTime.UtcNow, null);
            e = crash ? e with { Crashes = e.Crashes + 1, LastUtc = DateTime.UtcNow } : e with { Hangs = e.Hangs + 1, LastUtc = DateTime.UtcNow, Detail = detail };
            if (i >= 0) _entries[i] = e;
            else _entries.Add(e);
            Save();
            return !before && Reason(grammar, identity) is not null;
        }
    }

    /// <summary>Forgets every entry (the grammars are tried again).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries = [];
            Save();
        }
    }

    private void Save()
    {
        if (_file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, Json));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the quarantine still holds for this process
        }
    }

    private static List<Entry> Load(string? file)
    {
        if (file is null || !File.Exists(file)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(file), Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static List<Entry> Merge(List<Entry> mine, List<Entry> disk)
    {
        var merged = new List<Entry>(disk);
        foreach (var m in mine)
        {
            var i = merged.FindIndex(x => x.Grammar == m.Grammar && x.Identity == m.Identity);
            if (i < 0) merged.Add(m);
            else merged[i] = merged[i] with { Crashes = Math.Max(merged[i].Crashes, m.Crashes), Hangs = Math.Max(merged[i].Hangs, m.Hangs) };
        }
        return merged;
    }
}
