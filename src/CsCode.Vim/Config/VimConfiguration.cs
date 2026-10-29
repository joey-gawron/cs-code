using System.Text.Json;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Vim;

/// <summary>A named register as stored by <c>vim.persistRegisters</c> (type: c, l or b).</summary>
public sealed record PersistedRegister(string Type, string Text, int Width);

/// <summary>
/// Binds a <see cref="VimSession"/> to the application's settings: registers the <c>vim.*</c> properties, applies them (and live changes) to
/// the session, loads the vimrc and persists the named registers. All of it runs on the caller's thread; file reading is async.
/// </summary>
public sealed class VimConfiguration : IDisposable
{
    public const string RegistersStateKey = "vim.registers";
    public const string VimrcOrigin = "vimrc";
    public const string SettingsOrigin = "settings";

    /// <summary>Largest persisted register and the total cap over all persisted registers (characters).</summary>
    public const int MaxPersistedRegisterChars = 64 * 1024;
    public const int MaxPersistedTotalChars = 512 * 1024;

    private readonly VimSession _s;
    private readonly IConfigurationService _config;
    private readonly IStateStorage? _state;
    private readonly IAppPaths? _paths;
    private readonly DisposableStore _disposables = new();
    private readonly Emitter<bool> _enabledChanged = new();
    private bool _disposed;

    public VimConfiguration(VimSession session, IConfigurationService config, IStateStorage? state = null, IAppPaths? paths = null)
    {
        _s = session;
        _config = config;
        _state = state;
        _paths = paths;
        _disposables.Add(config.RegisterProperties(VimSettings.Properties));
        _disposables.Add(config.Changed.Subscribe(OnChanged));
        ApplyAll();
        LoadPersistedRegisters();
        _s.RegistersChanged = SaveRegisters;
    }

    /// <summary>Whether Vim emulation is switched on (<c>vim.enable</c> and <c>vim.enabled</c>, or the last toggle command).</summary>
    public bool Enabled => _s.Enabled;

    public IEventSource<bool> EnabledChanged => _enabledChanged;

    /// <summary>Errors of the last vimrc load ("line 3: E492: Not an editor command: foo"). Never fatal.</summary>
    public IReadOnlyList<string> VimrcErrors { get; private set; } = [];

    /// <summary>The path of the vimrc that would be loaded, or null when there is none configured.</summary>
    public string? VimrcPath
    {
        get
        {
            var custom = Str(VimSettings.VimrcPath, "");
            if (custom.Length > 0) return ExpandHome(custom);
            return _paths is null ? null : Path.Combine(_paths.UserDataDir, "vimrc");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // enable / disable
    // ------------------------------------------------------------------------------------------------------------

    public void SetEnabled(bool enabled)
    {
        if (_s.Enabled == enabled) return;
        _s.Enabled = enabled;
        _enabledChanged.Fire(enabled);
    }

    // ------------------------------------------------------------------------------------------------------------
    // applying settings
    // ------------------------------------------------------------------------------------------------------------

    private void OnChanged(ConfigurationChangedEventArgs e)
    {
        if (_disposed || !e.Affects("vim")) return;
        Apply(e);
    }

    private void ApplyAll() => Apply(null);

    private void Apply(ConfigurationChangedEventArgs? e)
    {
        bool Changed(string key) => e is null || e.Affects(key);
        var o = _s.Options;

        if (Changed(VimSettings.Enable) || Changed(VimSettings.Enabled)) SetEnabled(Bool(VimSettings.Enable, true) && Bool(VimSettings.Enabled, true));
        if (Changed(VimSettings.UseSystemClipboard)) _s.UseSystemClipboard = Bool(VimSettings.UseSystemClipboard, false);
        if (Changed(VimSettings.HlSearch)) o.HlSearch = Bool(VimSettings.HlSearch, false);
        if (Changed(VimSettings.IncSearch)) o.IncSearch = Bool(VimSettings.IncSearch, true);
        if (Changed(VimSettings.IgnoreCase)) o.IgnoreCase = Bool(VimSettings.IgnoreCase, true);
        if (Changed(VimSettings.SmartCase)) o.SmartCase = Bool(VimSettings.SmartCase, true);
        if (Changed(VimSettings.WrapScan)) o.WrapScan = Bool(VimSettings.WrapScan, true);
        if (Changed(VimSettings.StartInInsertMode)) _s.StartInInsertMode = Bool(VimSettings.StartInInsertMode, false);
        if (Changed(VimSettings.PersistRegisters)) _s.PersistRegisters = Bool(VimSettings.PersistRegisters, true);
        if (Changed(VimSettings.MaxRegisterChars)) o.MaxRegisterChars = Math.Max(1024, Int(VimSettings.MaxRegisterChars, 8_000_000));
        if (Changed(VimSettings.Timeout))
        {
            var raw = Raw(VimSettings.Timeout);
            if (raw is { ValueKind: JsonValueKind.True or JsonValueKind.False } b) o.Timeout = b.GetBoolean();
            else { o.Timeout = true; o.TimeoutLen = Math.Max(0, Int(VimSettings.Timeout, 1000)); }
        }
        if (Changed(VimSettings.HandleKeys)) ApplyHandleKeys();
        if (Changed(VimSettings.CursorStylePerMode)) ApplyCursorStyles();

        var plugins = _s.Plugins;
        if (Changed(VimSettings.Surround)) plugins.Surround = Flag(VimSettings.Surround, true);
        if (Changed(VimSettings.Commentary)) plugins.Commentary = Flag(VimSettings.Commentary, true);
        if (Changed(VimSettings.Sneak)) plugins.Sneak = Flag(VimSettings.Sneak, false);
        if (Changed(VimSettings.EasyMotion)) plugins.EasyMotion = Flag(VimSettings.EasyMotion, false);
        if (Changed(VimSettings.CamelCaseMotion)) plugins.CamelCaseMotion = Flag(VimSettings.CamelCaseMotion, false);
        if (Changed(VimSettings.ArgumentObjects)) plugins.ArgumentObjects = Flag(VimSettings.ArgumentObjects, false);
        if (Changed(VimSettings.ReplaceWithRegister)) plugins.ReplaceWithRegister = Flag(VimSettings.ReplaceWithRegister, false);
        if (Changed(VimSettings.HighlightedYank)) plugins.HighlightedYank = Flag(VimSettings.HighlightedYank, true);
        if (Changed(VimSettings.Targets)) plugins.Targets = Flag(VimSettings.Targets, false);

        var leaderChanged = Changed(VimSettings.Leader);
        if (leaderChanged)
        {
            var leader = Str(VimSettings.Leader, "\\");
            _s.Leader = leader.Length > 0 ? leader : "\\";
        }
        if (leaderChanged || VimSettings.KeyBindingSettings.Any(k => Changed(k.Key))) ApplyKeyBindings();
        if (e is not null && (Changed(VimSettings.VimrcEnable) || Changed(VimSettings.VimrcPath))) _ = ReloadVimrcAsync();
    }

    private void ApplyHandleKeys()
    {
        _s.UnhandledKeys.Clear();
        if (Raw(VimSettings.HandleKeys) is not { ValueKind: JsonValueKind.Object } obj) return;
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.False) continue;
            var keys = Keys.ParseNotation(p.Name, _s.Leader, _s.LocalLeader);
            if (keys.Count == 1) _s.UnhandledKeys.Add(keys[0]);
        }
    }

    private void ApplyCursorStyles()
    {
        foreach (var (name, modes, def) in VimSettings.CursorStyleSettings)
        {
            var text = Str($"{VimSettings.CursorStylePerMode}.{name}", def);
            if (!VimSettings.TryParseCursorShape(text, out var shape) && !VimSettings.TryParseCursorShape(def, out shape)) continue;
            foreach (var m in modes) _s.CursorStyles[m] = shape;
        }
    }

    private void ApplyKeyBindings()
    {
        _s.Mappings.RemoveOrigin(SettingsOrigin);
        foreach (var (key, modes, nonRecursive) in VimSettings.KeyBindingSettings)
        {
            if (Raw(key) is not { ValueKind: JsonValueKind.Array } arr) continue;
            foreach (var entry in arr.EnumerateArray())
            {
                var mapping = ParseBinding(entry, modes, nonRecursive);
                if (mapping is not null) _s.Mappings.Add(mapping);
            }
        }
    }

    private Mapping? ParseBinding(JsonElement entry, MapModes modes, bool nonRecursive)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        if (!entry.TryGetProperty("before", out var before)) return null;
        var lhsText = KeyList(before);
        if (lhsText.Length == 0) return null;
        var rhsText = entry.TryGetProperty("after", out var after) ? KeyList(after) : "";
        List<VimBindingCommand>? commands = null;
        if (entry.TryGetProperty("commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array)
        {
            commands = [];
            foreach (var c in cmds.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.String) commands.Add(new VimBindingCommand(c.GetString() ?? "", null));
                else if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("command", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    List<object?>? args = null;
                    if (c.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array) args = a.EnumerateArray().Select(x => (object?)x.Clone()).ToList();
                    commands.Add(new VimBindingCommand(name.GetString() ?? "", args));
                }
            }
            if (commands.Count == 0) commands = null;
        }
        var lhs = Keys.ParseNotation(lhsText, _s.Leader, _s.LocalLeader);
        var rhs = Keys.ParseNotation(rhsText, _s.Leader, _s.LocalLeader);
        if (lhs.Count == 0) return null;
        return new Mapping
        {
            Lhs = lhs.ToArray(), Rhs = rhs.ToArray(), Modes = modes, NoRemap = nonRecursive, LhsText = lhsText, RhsText = rhsText,
            Commands = commands, Origin = SettingsOrigin,
        };
    }

    private static string KeyList(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) return e.GetString() ?? "";
        if (e.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
    }

    // ------------------------------------------------------------------------------------------------------------
    // vimrc
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Loads the vimrc (replacing the mappings of the previous load). Reading the file is asynchronous; the script runs on the continuation.</summary>
    public async Task<IReadOnlyList<string>> ReloadVimrcAsync(CancellationToken ct = default)
    {
        _s.Mappings.RemoveOrigin(VimrcOrigin);
        VimrcErrors = [];
        if (_disposed || !Bool(VimSettings.VimrcEnable, true)) return VimrcErrors;
        var path = VimrcPath;
        if (path is null) return VimrcErrors;
        string text;
        try
        {
            if (!File.Exists(path)) return VimrcErrors;
            text = await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            VimrcErrors = [$"Cannot read {path}: {ex.Message}"];
            return VimrcErrors;
        }
        if (_disposed) return VimrcErrors;
        VimrcErrors = RunScript(text, VimrcOrigin);
        return VimrcErrors;
    }

    /// <summary>Runs vimrc-style text (set, map family, let mapleader, ...) against the session. Errors are collected, never thrown.</summary>
    public IReadOnlyList<string> RunScript(string text, string origin = VimrcOrigin)
    {
        var host = new InMemoryVimHost("");
        using var engine = new VimEngine(host, _s);
        return engine.LoadScript(text, origin);
    }

    // ------------------------------------------------------------------------------------------------------------
    // register persistence
    // ------------------------------------------------------------------------------------------------------------

    private void LoadPersistedRegisters()
    {
        if (_state is null || !_s.PersistRegisters) return;
        try
        {
            var saved = _state.Get<Dictionary<string, PersistedRegister>>(RegistersStateKey);
            if (saved is null) return;
            var data = new Dictionary<string, (char Type, string Text, int Width)>();
            foreach (var (name, r) in saved)
            {
                if (r?.Text is null || name.Length != 1) continue;
                data[name] = (r.Type.Length > 0 ? r.Type[0] : 'c', r.Text.Length > MaxPersistedRegisterChars ? r.Text[..MaxPersistedRegisterChars] : r.Text, Math.Max(0, r.Width));
            }
            _s.Registers.Import(data);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            // a corrupt state entry must never stop Vim from loading
        }
    }

    private void SaveRegisters(RegisterFile registers)
    {
        if (_state is null || !_s.PersistRegisters || _disposed) return;
        var export = registers.Export(MaxPersistedRegisterChars, MaxPersistedTotalChars);
        var data = new Dictionary<string, PersistedRegister>(export.Count);
        foreach (var (name, v) in export) data[name] = new PersistedRegister(v.Type.ToString(), v.Text, v.Width);
        _state.Set(RegistersStateKey, data);
    }

    // ------------------------------------------------------------------------------------------------------------
    // raw setting access
    // ------------------------------------------------------------------------------------------------------------

    private JsonElement? Raw(string key)
    {
        try { return _config.GetRaw(key) ?? DefaultOf(key); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return DefaultOf(key); }
    }

    private static JsonElement? DefaultOf(string key)
    {
        foreach (var p in VimSettings.Properties) if (p.Key == key) return p.Default;
        return null;
    }

    private bool Bool(string key, bool def) => Raw(key) is { ValueKind: JsonValueKind.True or JsonValueKind.False } e ? e.GetBoolean() : def;

    private int Int(string key, int def) => Raw(key) is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var v) ? v : def;

    private string Str(string key, string def) => Raw(key) is { ValueKind: JsonValueKind.String } e ? e.GetString() ?? def : def;

    /// <summary>A plugin toggle: a boolean, or VSCodeVim's <c>{"enable": true}</c> object form.</summary>
    private bool Flag(string key, bool def)
    {
        var raw = Raw(key);
        if (raw is { ValueKind: JsonValueKind.Object } o)
            return o.TryGetProperty("enable", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False ? en.GetBoolean() : def;
        return Bool(key, def);
    }

    private static string ExpandHome(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(_s.RegistersChanged?.Target, this)) _s.RegistersChanged = null;
        _s.Mappings.RemoveOrigin(SettingsOrigin);
        _disposables.Dispose();
        _enabledChanged.Clear();
    }
}
