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
///
/// Several processes share the file (background mode). Each clear bumps a GENERATION stored with the entries; a process that finds a newer
/// generation on disk drops what it held from before that clear instead of merging it back (a clear in one process used to be undone by
/// the next crash or hang another process recorded), and <see cref="Reason"/> re-reads the file when it changed, so another process's clear
/// or record is honoured here too.
/// </summary>
public sealed class GrammarQuarantine : IGrammarQuarantine
{
    public const int CrashesBeforeQuarantine = 2;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly string? _file;
    private readonly object _gate = new();
    private List<Entry> _entries;
    private int _generation;
    private (DateTime WriteUtc, long Length) _seen; // the file as last read or written here (re-read only when it changed)

    public sealed record Entry(string Grammar, string Identity, int Crashes, int Hangs, DateTime LastUtc, string? Detail);

    /// <summary>The file's content: the entries and the number of clears so far.</summary>
    private sealed record Stored(int Generation, List<Entry> Entries);

    /// <param name="file">Where the entries persist; null = this process only.</param>
    public GrammarQuarantine(string? file)
    {
        _file = file;
        var stored = Load(file);
        _entries = stored.Entries;
        _generation = stored.Generation;
        _seen = Stamp();
    }

    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_gate)
            {
                Refresh();
                return _entries.ToArray();
            }
        }
    }

    public string? Reason(string grammar, string identity)
    {
        lock (_gate)
        {
            Refresh();
            return ReasonOf(grammar, identity);
        }
    }

    /// <summary>Takes in what other processes wrote since this one last looked (a stat when nothing changed).</summary>
    private void Refresh()
    {
        if (_file is null) return;
        var stamp = Stamp();
        if (stamp == _seen) return;
        Absorb(Load(_file));
        _seen = stamp;
    }

    private void Absorb(Stored disk)
    {
        if (disk.Generation > _generation)
        {
            // cleared since this process last looked: everything held from before the clear is forgiven, not merged back
            _entries = disk.Entries;
            _generation = disk.Generation;
        }
        else _entries = Merge(_entries, disk.Entries);
    }

    private (DateTime, long) Stamp()
    {
        if (_file is null) return default;
        try
        {
            var info = new FileInfo(_file);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private string? ReasonOf(string grammar, string identity)
    {
        var e = _entries.Find(x => x.Grammar == grammar && x.Identity == identity);
        if (e is null) return null;
        if (e.Hangs > 0) return $"it stopped responding (an endless loop in the grammar's code){(e.Detail is null ? "" : ": " + e.Detail)}";
        if (e.Crashes >= CrashesBeforeQuarantine) return $"CsCode closed unexpectedly {e.Crashes} times while running it";
        return null;
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
            // (and a clear there since this process last looked forgives what it held from before)
            if (_file is not null) Absorb(Load(_file));
            var before = ReasonOf(grammar, identity) is not null;
            var i = _entries.FindIndex(x => x.Grammar == grammar && x.Identity == identity);
            var e = i >= 0 ? _entries[i] : new Entry(grammar, identity, 0, 0, DateTime.UtcNow, null);
            e = crash ? e with { Crashes = e.Crashes + 1, LastUtc = DateTime.UtcNow } : e with { Hangs = e.Hangs + 1, LastUtc = DateTime.UtcNow, Detail = detail };
            if (i >= 0) _entries[i] = e;
            else _entries.Add(e);
            Save();
            return !before && ReasonOf(grammar, identity) is not null;
        }
    }

    /// <summary>Forgets every entry (the grammars are tried again), here and, through the next generation in the file, in every other process.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _generation = Math.Max(_generation, Load(_file).Generation) + 1;
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
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Stored(_generation, _entries), Json));
            File.Move(tmp, _file, overwrite: true);
            _seen = Stamp();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the quarantine still holds for this process
        }
    }

    /// <summary>The file's entries and generation; the first format (a bare array of entries) reads as generation 0.</summary>
    private static Stored Load(string? file)
    {
        if (file is null || !File.Exists(file)) return new Stored(0, []);
        try
        {
            var text = File.ReadAllText(file);
            if (text.AsSpan().TrimStart().StartsWith("[")) return new Stored(0, JsonSerializer.Deserialize<List<Entry>>(text, Json) ?? []);
            var stored = JsonSerializer.Deserialize<Stored>(text, Json);
            return stored is null ? new Stored(0, []) : stored with { Entries = stored.Entries ?? [] };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Stored(0, []);
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
