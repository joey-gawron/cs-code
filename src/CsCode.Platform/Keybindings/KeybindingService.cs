using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Contributions;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Platform.Configuration;
using CsCode.Platform.Context;
using CsCode.Platform.Io;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Platform.Keybindings;

/// <summary>
/// Keybinding resolution with a chord state machine. Sources, lowest to highest precedence:
/// preset (embedded, <c>visualstudio</c> or <c>vscode</c>) &lt; <see cref="KeybindingContribution"/>s from the contribution registry
/// (built-in ones, then <see cref="ContributionSources.User"/> ones) &lt; the user's <c>keybindings.json</c>.
/// Later entries win; <c>"-command"</c> entries remove matching earlier ones; <c>mac</c>/<c>linux</c>/<c>win</c> replace <c>key</c> (an entry
/// with no <c>key</c> binds only on the platforms it names).
/// <para>
/// <see cref="Resolve"/> runs on every keystroke on the UI thread: the resolved index is an immutable snapshot, results are preallocated
/// per binding, the chord buffer is fixed and <c>when</c> clauses go through the allocation-free context evaluator — it allocates nothing.
/// A started chord is abandoned after <see cref="PlatformOptions.ChordTimeout"/> (checked lazily on the next stroke; no timer).
/// </para>
/// </summary>
public sealed class KeybindingService : IKeybindingService, IDisposable
{
    public const string VisualStudioPreset = "visualstudio";
    public const string VsCodePreset = "vscode";
    public static readonly IReadOnlyList<string> Presets = [VisualStudioPreset, VsCodePreset];

    private const int MaxChord = 8;
    private static readonly KeyResolveResult NoneResult = new(KeyResolution.None);
    private static readonly KeyResolveResult PendingResult = new(KeyResolution.Pending);
    private const string StatePresetKey = "keybindings.preset";

    private sealed class Candidate(KeyBinding binding, KeyStroke[] chord)
    {
        public KeyBinding Binding { get; } = binding;
        public KeyStroke[] Chord { get; } = chord;
        public string? When { get; } = binding.When;
        public KeyResolveResult Matched { get; } = new(KeyResolution.Matched, binding);
    }

    private sealed class BindingIndex(KeyBinding[] effective, Dictionary<KeyStroke, Candidate[]> byFirst)
    {
        public KeyBinding[] Effective { get; } = effective;          // ascending precedence, negatives already applied
        public Dictionary<KeyStroke, Candidate[]> ByFirst { get; } = byFirst; // each list DEscending precedence
    }

    private readonly IContextKeyService _context;
    private readonly IContributionRegistry? _contributions;
    private readonly IConfigurationService? _config;
    private readonly IStateStorage? _state;
    private readonly IoLane _lane;
    private readonly IFileWatcher? _watcher;
    private readonly PlatformOptions _options;
    private readonly HostOs _os;
    private readonly ILogger _log;
    private readonly Emitter<object?> _changed = new();
    private readonly DisposableStore _subscriptions = new();
    private readonly Timer _reloadTimer;
    private readonly object _chordGate = new();
    private readonly object _buildGate = new();
    private readonly KeyStroke[] _buffer = new KeyStroke[MaxChord];
    private readonly Dictionary<string, KeyBinding[]> _presetCache = new(StringComparer.Ordinal);
    private int _pending;
    private long _lastStroke;
    private BindingIndex _index = new([], new());
    private KeyBinding[] _userBindings = [];
    private string? _userText;
    private string _preset = VisualStudioPreset;
    private bool _disposed;

    public KeybindingService(
        IContextKeyService context,
        IAppPaths paths,
        IoLane lane,
        IContributionRegistry? contributions = null,
        IConfigurationService? configuration = null,
        IStateStorage? state = null,
        IFileWatcher? watcher = null,
        PlatformOptions? options = null,
        HostOs? os = null,
        ILogger<KeybindingService>? logger = null)
    {
        _context = context;
        _contributions = contributions;
        _config = configuration;
        _state = state;
        _lane = lane;
        _watcher = watcher;
        _options = options ?? new PlatformOptions();
        _os = os ?? (OperatingSystem.IsWindows() ? HostOs.Windows : OperatingSystem.IsMacOS() ? HostOs.MacOs : HostOs.Linux);
        _log = logger ?? NullLogger<KeybindingService>.Instance;
        UserKeybindingsPath = Path.Combine(AppPaths.ConfigDirOf(paths), "keybindings.json");

        _preset = InitialPreset();
        LoadUserFileCore();
        Rebuild(raise: false);

        _reloadTimer = new Timer(static s => ((KeybindingService)s!).OnReloadTimer(), this, Timeout.Infinite, Timeout.Infinite);

        // Live sources. Every subscription lives in the store; Dispose releases them all.
        if (_contributions is not null)
            _subscriptions.Add(_contributions.Of<KeybindingContribution>().Changed.Subscribe(_ => Rebuild(raise: true)));
        if (_config is not null)
            _subscriptions.Add(_config.Changed.Subscribe(e => { if (e.Affects(BuiltInSettings.KeymapKey)) OnKeymapSettingChanged(); }));
        if (_watcher is not null)
        {
            try
            {
                var dir = Path.GetDirectoryName(UserKeybindingsPath)!;
                Directory.CreateDirectory(dir);
                _subscriptions.Add(_watcher.Watch(FileUri.FromDirectoryPath(dir), recursive: false, excludeGlobs: [], changes =>
                {
                    foreach (var c in changes)
                        if (c.Uri.IsFile && Path.GetFileName(FileUri.ToPath(c.Uri)) == "keybindings.json") { ScheduleReload(); return; }
                }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Cannot watch the keybindings directory; edits to keybindings.json need a restart");
            }
        }
    }

    public string UserKeybindingsPath { get; }

    public IEventSource<object?> Changed => _changed;

    public string ActivePreset => Volatile.Read(ref _preset);

    /// <summary>Problems found in the last load of keybindings.json (bad key strings, missing commands, invalid when-clauses). Entries with problems are skipped.</summary>
    public IReadOnlyList<string> LoadWarnings { get; private set; } = [];

    // ---- resolution --------------------------------------------------------------------------------------------------------------

    public KeyResolveResult Resolve(KeyStroke stroke)
    {
        var index = Volatile.Read(ref _index);
        lock (_chordGate)
        {
            var now = _options.TimeProvider.GetTimestamp();
            if (_pending > 0 && _options.TimeProvider.GetElapsedTime(_lastStroke, now) > _options.ChordTimeout) _pending = 0;
            if (_pending >= MaxChord) _pending = 0;

            _buffer[_pending] = stroke;
            var count = _pending + 1;

            if (index.ByFirst.TryGetValue(_buffer[0], out var candidates))
            {
                // Highest precedence first; the first candidate whose chord starts with what was typed AND whose `when` holds decides.
                foreach (var c in candidates)
                {
                    var chord = c.Chord;
                    if (chord.Length < count) continue;
                    var prefix = true;
                    for (var i = 0; i < count; i++)
                        if (chord[i] != _buffer[i]) { prefix = false; break; }
                    if (!prefix) continue;
                    if (c.When is not null && !_context.Evaluate(c.When)) continue;

                    if (chord.Length > count)
                    {
                        _pending = count;
                        _lastStroke = now;
                        return PendingResult;
                    }
                    _pending = 0;
                    return c.Matched;
                }
            }

            _pending = 0; // unknown stroke / escape / chord that leads nowhere: reset
            return NoneResult;
        }
    }

    public void ResetChord()
    {
        lock (_chordGate) _pending = 0;
    }

    public IReadOnlyList<KeyStroke> PendingChord
    {
        get
        {
            lock (_chordGate)
            {
                if (_pending > 0 && _options.TimeProvider.GetElapsedTime(_lastStroke) > _options.ChordTimeout) _pending = 0;
                return _pending == 0 ? [] : _buffer.AsSpan(0, _pending).ToArray();
            }
        }
    }

    public IReadOnlyList<KeyBinding> All => Volatile.Read(ref _index).Effective;

    public IReadOnlyList<KeyBinding> GetBindingsFor(string commandId)
    {
        var effective = Volatile.Read(ref _index).Effective;
        var result = new List<KeyBinding>();
        for (var i = effective.Length - 1; i >= 0; i--)   // highest precedence first
            if (effective[i].Command == commandId) result.Add(effective[i]);
        return result;
    }

    public string? GetLabel(string commandId)
    {
        var best = GetBindingsFor(commandId);
        return best.Count == 0 ? null : KeyBindingFormatter.Format(best[0].Chord, _os);
    }

    // ---- presets -----------------------------------------------------------------------------------------------------------------

    public async ValueTask SetPresetAsync(string preset, CancellationToken ct = default)
    {
        var name = Presets.FirstOrDefault(p => string.Equals(p, preset, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown keymap preset '{preset}'. Available: {string.Join(", ", Presets)}", nameof(preset));
        if (name == ActivePreset) return;

        Volatile.Write(ref _preset, name);
        Rebuild(raise: true);

        // Persist: the visible setting when configuration is available, otherwise the state store.
        if (_config is not null) await _config.UpdateAsync(BuiltInSettings.KeymapKey, name, ConfigurationTarget.User, ct).ConfigureAwait(false);
        _state?.Set(StatePresetKey, name);
    }

    private string InitialPreset()
    {
        string? wanted = null;
        if (_config is not null) wanted = _config.Get<string>(BuiltInSettings.KeymapKey);
        wanted ??= _state?.Get<string>(StatePresetKey);
        if (wanted is null) return VisualStudioPreset;
        var match = Presets.FirstOrDefault(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase));
        if (match is null) _log.LogWarning("Unknown keymap '{Preset}' in settings; using '{Default}'", wanted, VisualStudioPreset);
        return match ?? VisualStudioPreset;
    }

    private void OnKeymapSettingChanged()
    {
        var wanted = _config?.Get<string>(BuiltInSettings.KeymapKey);
        var match = Presets.FirstOrDefault(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase));
        if (match is null) { _log.LogWarning("Unknown keymap '{Preset}' in settings; keeping '{Active}'", wanted, ActivePreset); return; }
        if (match == ActivePreset) return;
        Volatile.Write(ref _preset, match);
        Rebuild(raise: true);
    }

    // ---- building the effective table --------------------------------------------------------------------------------------------

    private void Rebuild(bool raise)
    {
        lock (_buildGate)
        {
            var ordered = new List<KeyBinding>();
            ordered.AddRange(LoadPreset(ActivePreset));

            if (_contributions is not null)
            {
                var items = _contributions.Of<KeybindingContribution>().Items;
                foreach (var c in items.Where(static c => c.SourceId != ContributionSources.User)) AddContribution(ordered, c, KeyBindingSource.Default);
                foreach (var c in items.Where(static c => c.SourceId == ContributionSources.User)) AddContribution(ordered, c, KeyBindingSource.User);
            }
            ordered.AddRange(_userBindings);

            // Apply "-command" removals in precedence order: a removal only affects entries declared BEFORE it.
            var active = new List<KeyBinding>(ordered.Count);
            foreach (var b in ordered)
            {
                if (!b.IsNegative) { active.Add(b); continue; }
                var target = b.Command[1..];
                active.RemoveAll(e => e.Command == target && (b.Chord.Count == 0 || SameChord(e.Chord, b.Chord)) && WhenCompatible(e.When, b.When));
            }

            var byFirst = new Dictionary<KeyStroke, List<Candidate>>();
            for (var i = active.Count - 1; i >= 0; i--) // descending precedence
            {
                var b = active[i];
                if (b.Chord.Count == 0) continue;
                var cand = new Candidate(b, [.. b.Chord]);
                if (!byFirst.TryGetValue(b.Chord[0], out var list)) byFirst[b.Chord[0]] = list = new();
                list.Add(cand);
            }
            var index = new BindingIndex([.. active], byFirst.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
            Volatile.Write(ref _index, index);
            ResetChord(); // a half-typed chord may no longer exist in the new table
        }
        if (raise && !_disposed) _changed.Fire(null);
    }

    private static bool SameChord(IReadOnlyList<KeyStroke> a, IReadOnlyList<KeyStroke> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // VS Code rule: a removal without `when` matches regardless; with `when` it must equal the target's (or the target has none).
    private static bool WhenCompatible(string? entryWhen, string? removalWhen) =>
        removalWhen is null || entryWhen is null || string.Equals(entryWhen.Trim(), removalWhen.Trim(), StringComparison.Ordinal);

    private void AddContribution(List<KeyBinding> into, Contributed<KeybindingContribution> item, KeyBindingSource source)
    {
        var c = item.Value;
        var key = _os switch { HostOs.MacOs => c.Mac, HostOs.Linux => c.Linux, _ => c.Win } ?? c.Key;
        try
        {
            into.Add(MakeBinding(c.Command, key, c.When, c.Args, source));
        }
        catch (FormatException ex)
        {
            _log.LogWarning("Ignoring keybinding contribution for '{Command}' from {Source}: {Message}", c.Command, item.SourceId, ex.Message);
        }
    }

    private static KeyBinding MakeBinding(string command, string? key, string? when, JsonElement? args, KeyBindingSource source)
    {
        var negative = command.StartsWith('-');
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!negative) throw new FormatException($"Keybinding for '{command}' has no key");
            return new KeyBinding(command, [], when, args, source, IsNegative: true); // "-command" alone: removes every binding of the command
        }
        return new KeyBinding(command, KeyStrokeParser.ParseChord(key), when, args, source, negative);
    }

    // ---- preset files ------------------------------------------------------------------------------------------------------------

    private KeyBinding[] LoadPreset(string name)
    {
        lock (_presetCache)
        {
            if (_presetCache.TryGetValue(name, out var cached)) return cached;
            var resource = $"CsCode.Platform.Keymaps.{name}.json";
            using var stream = typeof(KeybindingService).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded keymap '{resource}' is missing from the assembly");
            using var reader = new StreamReader(stream);
            var bindings = ParseEntries(reader.ReadToEnd(), KeyBindingSource.Default, resource, out var warnings);
            foreach (var w in warnings) _log.LogError("Built-in keymap problem: {Warning}", w); // a bug in our own preset
            return _presetCache[name] = bindings;
        }
    }

    /// <summary>Parses a keybindings JSONC array (preset or user file). Problem entries are skipped and reported in <paramref name="warnings"/>.</summary>
    internal KeyBinding[] ParseEntries(string text, KeyBindingSource source, string origin, out List<string> warnings)
    {
        warnings = new List<string>();
        var result = new List<KeyBinding>();
        JsoncNode? root;
        try { root = JsoncParser.Parse(text); }
        catch (JsoncException ex)
        {
            warnings.Add($"{origin}: not valid JSON with comments — {ex.Message}");
            return [];
        }
        if (root is null) return [];
        if (root is not JsoncArray array)
        {
            warnings.Add($"{origin}: expected a JSON array of keybindings at the top level");
            return [];
        }

        var n = 0;
        foreach (var item in array.Items)
        {
            n++;
            if (item is not JsoncObject o) { warnings.Add($"{origin}: entry #{n} is not an object"); continue; }
            string? Str(string name) => (o.Members.LastOrDefault(m => m.Name == name)?.Value as JsoncPrimitive) is { Kind: JsoncPrimitiveKind.String } p ? p.StringValue : null;

            var command = Str("command");
            if (string.IsNullOrWhiteSpace(command)) { warnings.Add($"{origin}: entry #{n} has no \"command\""); continue; }
            var key = _os switch { HostOs.MacOs => Str("mac"), HostOs.Linux => Str("linux"), _ => Str("win") } ?? Str("key");
            // An entry with only another platform's key ({"mac": "cmd+,", ...}) binds nothing here; it is not a keyless (negative) entry.
            if (key is null && (Str("mac") ?? Str("linux") ?? Str("win")) is not null) continue;
            var when = Str("when");
            JsonElement? args = null;
            if (o.Members.LastOrDefault(m => m.Name == "args") is { } argMember && JsoncParser.ToJsonNode(argMember.Value) is { } argNode)
                args = JsonSerializer.SerializeToElement(argNode);

            if (WhenClause.Validate(when) is { } whenError)
                warnings.Add($"{origin}: entry #{n} ('{command}'): invalid \"when\" — {whenError} (the binding will never fire)");
            try { result.Add(MakeBinding(command, key, when, args, source)); }
            catch (FormatException ex) { warnings.Add($"{origin}: entry #{n} ('{command}'): {ex.Message}"); }
        }
        return [.. result];
    }

    // ---- user keybindings.json ---------------------------------------------------------------------------------------------------

    private void LoadUserFileCore()
    {
        string text;
        try { text = File.Exists(UserKeybindingsPath) ? File.ReadAllText(UserKeybindingsPath) : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Cannot read {Path}; keeping previous user keybindings", UserKeybindingsPath);
            return;
        }
        ApplyUserText(text);
    }

    private bool ApplyUserText(string text)
    {
        if (text == _userText) return false;
        _userText = text;
        var parsed = ParseEntries(text, KeyBindingSource.User, UserKeybindingsPath, out var warnings);
        LoadWarnings = warnings;
        foreach (var w in warnings) _log.LogWarning("{Warning}", w);
        // A wholly unparseable file keeps the previous bindings (a typo while editing must not wipe the user's keymap).
        if (parsed.Length == 0 && warnings.Count > 0 && warnings[0].Contains("not valid JSON", StringComparison.Ordinal)) return false;
        _userBindings = parsed;
        return true;
    }

    /// <summary>Re-reads keybindings.json now (normally triggered by the file watcher) and completes after the table is rebuilt.</summary>
    public Task ReloadAsync(CancellationToken ct = default) => _lane.RunAsync(_ => ReloadUserFile(), ct);

    private void ReloadUserFile()
    {
        if (_disposed) return;
        string text;
        try { text = File.Exists(UserKeybindingsPath) ? File.ReadAllText(UserKeybindingsPath) : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (ApplyUserText(text)) Rebuild(raise: true);
    }

    private void ScheduleReload()
    {
        if (_disposed) return;
        try { _reloadTimer.Change(_options.ConfigReloadDebounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void OnReloadTimer() { if (!_disposed) _lane.Post(ReloadUserFile, ex => _log.LogWarning(ex, "keybindings.json reload failed")); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reloadTimer.Dispose();
        _subscriptions.Dispose();
        _changed.Clear();
    }
}
