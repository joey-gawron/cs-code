using CsCode.Core.Contributions;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Theming;
using Microsoft.Extensions.Logging;

namespace CsCode.Editor.Theming;

/// <summary>
/// <see cref="IColorThemeService"/>: the one palette namespace of the app (VS Code workbench colour keys).
/// Themes come from three places: the built-ins (the 21 themes embedded in TextMateSharp.Grammars, <see cref="BuiltinColorThemes"/>),
/// <see cref="Register"/> (the shell registers its Visual Studio 2022 themes here) and <see cref="ColorThemeContribution"/>s (the user's
/// <c>{UserDataDir}/themes/*.json</c>, VS Code theme format, loaded by the editor's user-content loader). The selection is persisted in
/// <c>workbench.colorTheme</c> (the theme's label, as VS Code does) and followed live when that setting changes.
/// <para><see cref="IColorThemePreview"/>: a picker shows a theme without selecting it (nothing is persisted; <see cref="EndPreview"/> reverts).</para>
/// <para><see cref="SetCompletion"/>: the shell post-processes every theme (its VS 2022 chrome mapping), so every consumer sees the same complete
/// colours; the theme as registered is kept, so a later completion or an unregistration still finds it.</para>
/// </summary>
public sealed class ColorThemeService : IColorThemeService, IColorThemePreview, IDisposable
{
    public const string BuiltinPathPrefix = BuiltinColorThemes.PathPrefix;
    private const string DefaultThemeLabel = "Dark+ (default dark)";

    /// <summary>A registered theme: as given (<see cref="Raw"/>) and as shown (<see cref="Shown"/>, the completion's result, computed on first use).</summary>
    private sealed class Entry(ColorTheme raw)
    {
        public ColorTheme Raw { get; } = raw;
        public ColorTheme? Shown;
    }

    private readonly EditorServices _svc;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly Emitter<ColorTheme> _changed = new();
    private readonly DisposableStore _subscriptions = new();
    private readonly Dictionary<ColorThemeContribution, IDisposable?> _contributed = new();
    private Entry _selected; // the user's choice (what workbench.colorTheme names)
    private Entry _current;  // what is shown: the selection, or the theme being previewed
    private bool _previewing;
    private Func<ColorTheme, ColorTheme>? _completion;
    private string? _wantedName; // the persisted selection, kept until a theme with that name is registered (the shell registers late)

    public ColorThemeService(EditorServices svc)
    {
        _svc = svc;
        _log = svc.LoggerFactory.CreateLogger<ColorThemeService>();
        foreach (var d in BuiltinColorThemes.All)
        {
            try { _entries.Add(new Entry(BuiltinColorThemes.Load(d))); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or FileNotFoundException)
            {
                _log.LogWarning(ex, "Built-in colour theme {File} could not be loaded", d.File);
            }
        }
        if (_entries.Count == 0) throw new InvalidOperationException("No built-in colour theme could be loaded from TextMateSharp.Grammars");
        _selected = _current = FindEntry(DefaultThemeLabel) ?? _entries[0];

        _wantedName = svc.Config.GetOr<string?>(SettingKeys.WorkbenchColorTheme, null);
        if (_wantedName is not null && FindEntry(_wantedName) is { } wanted) _selected = _current = wanted;

        _subscriptions.Add(svc.Config.Changed.Subscribe(e =>
        {
            if (!e.Affects(SettingKeys.WorkbenchColorTheme)) return;
            var name = svc.Config.GetOr<string?>(SettingKeys.WorkbenchColorTheme, null);
            if (name is not null) Select(name, persist: false);
        }));
        _subscriptions.Add(svc.Contributions.Of<ColorThemeContribution>().Changed.Subscribe(OnContributionsChanged));
        OnContributionsChanged(svc.Contributions.Of<ColorThemeContribution>().Items);
    }

    public ColorTheme Current { get { lock (_gate) return Shown(_current); } }

    public IReadOnlyList<ColorTheme> Available
    {
        get
        {
            Entry[] entries;
            lock (_gate) entries = _entries.ToArray();
            var all = new ColorTheme[entries.Length];
            // One lock per theme: completing all of them takes ~10 ms (the shell does it on a pool thread after start-up), and a caller of
            // Current (the UI thread) must wait for one completion at most, well under a millisecond, never for the whole list.
            for (var i = 0; i < all.Length; i++)
                lock (_gate) all[i] = Shown(entries[i]);
            return all;
        }
    }

    public IEventSource<ColorTheme> Changed => _changed;

    /// <summary>Id of the selected theme (the one <c>workbench.colorTheme</c> names), which differs from Current while a preview is shown.</summary>
    public string SelectedThemeId { get { lock (_gate) return _selected.Raw.Id; } }

    /// <summary>True once <see cref="SetCompletion"/> installed a completion.</summary>
    public bool HasCompletion { get { lock (_gate) return _completion is not null; } }

    /// <summary>
    /// Post-processes every theme, now and when registered later (null: themes are shown as registered). The shell sets this once, before its
    /// first frame; themes are completed lazily on first use, so the call itself is cheap. Changed fires when the current theme's colours change.
    /// </summary>
    public void SetCompletion(Func<ColorTheme, ColorTheme>? completion)
    {
        ColorTheme current;
        lock (_gate)
        {
            _completion = completion;
            foreach (var e in _entries) e.Shown = null;
            current = Shown(_current);
        }
        _changed.Fire(current);
    }

    // Under _gate. A completion that throws must never take theming down: the theme is then shown as registered.
    private ColorTheme Shown(Entry e)
    {
        if (e.Shown is { } shown) return shown;
        if (_completion is not { } complete) return e.Raw;
        try { return e.Shown = complete(e.Raw); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Completing colour theme '{Theme}' failed; it is shown as registered", e.Raw.Label);
            return e.Shown = e.Raw;
        }
    }

    public IDisposable Register(ColorTheme theme)
    {
        ColorTheme? switchedTo = null;
        var entry = new Entry(theme);
        lock (_gate)
        {
            var existing = _entries.FindIndex(t => t.Raw.Id == theme.Id);
            if (existing >= 0)
            {
                var replaced = _entries[existing];
                _entries[existing] = entry;
                if (ReferenceEquals(_selected, replaced)) _selected = entry;
                if (ReferenceEquals(_current, replaced))
                {
                    _current = entry;
                    switchedTo = Shown(entry);
                }
            }
            else _entries.Add(entry);
            // The persisted selection names this theme (registered after startup): adopt it.
            if (_wantedName is not null && Matches(theme, _wantedName) && !ReferenceEquals(_selected, entry))
            {
                _selected = entry;
                if (!_previewing && !ReferenceEquals(_current, entry))
                {
                    _current = entry;
                    switchedTo = Shown(entry);
                }
            }
        }
        if (switchedTo is not null) _changed.Fire(switchedTo);
        return Disposable.Create(() => Unregister(entry));
    }

    private void Unregister(Entry entry)
    {
        ColorTheme? switchedTo = null;
        lock (_gate)
        {
            if (!_entries.Remove(entry) || _entries.Count == 0) return;
            var fallback = FindEntry(DefaultThemeLabel) ?? _entries[0];
            if (ReferenceEquals(_selected, entry)) _selected = fallback;
            if (ReferenceEquals(_current, entry))
            {
                _previewing = false;
                _current = _selected;
                switchedTo = Shown(_current);
            }
        }
        if (switchedTo is not null) _changed.Fire(switchedTo);
    }

    public ValueTask SetCurrentAsync(string themeId, CancellationToken ct = default)
    {
        Select(themeId, persist: true);
        return ValueTask.CompletedTask;
    }

    private void Select(string idOrLabel, bool persist)
    {
        ColorTheme? switchedTo = null;
        Entry? entry;
        lock (_gate)
        {
            entry = FindEntry(idOrLabel);
            if (entry is null)
            {
                // Not registered (yet): the shell registers its themes after startup, so remember the choice and adopt it when it appears.
                _wantedName = idOrLabel;
            }
            else
            {
                _wantedName = entry.Raw.Label;
                _selected = entry;
                // Choosing (persist) ends a preview; a setting change from elsewhere while a picker previews waits for the picker to finish.
                if (persist) _previewing = false;
                if (!_previewing && !ReferenceEquals(_current, entry))
                {
                    _current = entry;
                    switchedTo = Shown(entry);
                }
            }
        }
        if (entry is null)
        {
            _log.LogDebug("Colour theme '{Theme}' is not registered; keeping the current one until it is", idOrLabel);
            return;
        }
        if (switchedTo is not null) _changed.Fire(switchedTo);
        if (persist) _ = PersistAsync(entry.Raw.Label);
    }

    // ---- IColorThemePreview ----

    public string? PreviewedThemeId { get { lock (_gate) return _previewing ? _current.Raw.Id : null; } }

    public void Preview(string themeId)
    {
        ColorTheme? switchedTo = null;
        lock (_gate)
        {
            if (FindEntry(themeId) is not { } entry) return;
            _previewing = true;
            if (!ReferenceEquals(_current, entry))
            {
                _current = entry;
                switchedTo = Shown(entry);
            }
        }
        if (switchedTo is not null) _changed.Fire(switchedTo);
    }

    public void EndPreview()
    {
        ColorTheme? switchedTo = null;
        lock (_gate)
        {
            if (!_previewing) return;
            _previewing = false;
            if (!ReferenceEquals(_current, _selected))
            {
                _current = _selected;
                switchedTo = Shown(_current);
            }
        }
        if (switchedTo is not null) _changed.Fire(switchedTo);
    }

    private async Task PersistAsync(string label)
    {
        try
        {
            await _svc.Config.UpdateAsync(SettingKeys.WorkbenchColorTheme, label).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not persist workbench.colorTheme");
        }
    }

    /// <summary>The theme's colour for <paramref name="key"/>, falling back along the documented chain; transparent for unknown keys.</summary>
    public Rgba GetColor(string key)
    {
        var theme = Current;
        return DefaultColors.Resolve(key, theme.Colors, theme.Kind) ?? new Rgba(0, 0, 0, 0);
    }

    // Under _gate.
    private Entry? FindEntry(string idOrLabel) =>
        _entries.FirstOrDefault(t => string.Equals(t.Raw.Id, idOrLabel, StringComparison.OrdinalIgnoreCase))
        ?? _entries.FirstOrDefault(t => string.Equals(t.Raw.Label, idOrLabel, StringComparison.OrdinalIgnoreCase));

    private static bool Matches(ColorTheme t, string name) =>
        string.Equals(t.Id, name, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Label, name, StringComparison.OrdinalIgnoreCase);

    // ---- user theme contributions ----

    private void OnContributionsChanged(IReadOnlyList<Contributed<ColorThemeContribution>> items)
    {
        List<ColorThemeContribution> added;
        List<IDisposable?> removed = [];
        lock (_gate)
        {
            var now = items.Select(i => i.Value).ToList();
            added = now.Where(c => !_contributed.ContainsKey(c)).ToList();
            foreach (var gone in _contributed.Keys.Where(k => !now.Contains(k)).ToList())
            {
                removed.Add(_contributed[gone]);
                _contributed.Remove(gone);
            }
            foreach (var c in added) _contributed[c] = null;
        }
        foreach (var r in removed) r?.Dispose();
        foreach (var c in added) _ = LoadContributionAsync(c);
    }

    private async Task LoadContributionAsync(ColorThemeContribution c)
    {
        try
        {
            var uri = new Uri(c.Path);
            var text = System.Text.Encoding.UTF8.GetString(await _svc.FileSystem.ReadFileAsync(uri).ConfigureAwait(false));
            var theme = await VsCodeThemeLoader.LoadAsync(c.Id, c.Label, c.UiTheme, text, c.Path, new VsCodeThemeLoader.FileSystemReader(_svc.FileSystem)).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_contributed.ContainsKey(c)) return; // removed while loading
            }
            var registration = Register(theme);
            lock (_gate)
            {
                if (_contributed.ContainsKey(c)) _contributed[c] = registration;
                else registration.Dispose();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not load colour theme '{Label}' from {Path}", c.Label, c.Path);
        }
    }

    public void Dispose()
    {
        _subscriptions.Dispose();
        _changed.Clear();
        lock (_gate)
            foreach (var r in _contributed.Values) r?.Dispose();
    }

    /// <summary>Reads "builtin-theme:x.json" and its includes from the resources TextMateSharp.Grammars embeds.</summary>
    internal sealed class BuiltinThemeReader : IThemeFileReader
    {
        public string? ReadSync(string file) => BuiltinColorThemes.ReadResource(file);

        public ValueTask<(string Path, string Text)?> ReadRelativeAsync(string fromPath, string relativePath, CancellationToken ct)
        {
            var file = relativePath.TrimStart('.', '/');
            var text = ReadSync(file);
            return ValueTask.FromResult<(string, string)?>(text is null ? null : (BuiltinPathPrefix + file, text));
        }
    }
}
