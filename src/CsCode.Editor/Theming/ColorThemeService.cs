using CsCode.Core.Contributions;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Theming;
using CsCode.Editor.Languages;
using Microsoft.Extensions.Logging;

namespace CsCode.Editor.Theming;

/// <summary>
/// <see cref="IColorThemeService"/>: the one palette namespace of the app (VS Code workbench colour keys).
/// Themes come from three places: the built-ins (VS Code's Dark+/Light+/Dark/Light/High Contrast, embedded in TextMateSharp.Grammars),
/// <see cref="Register"/> (the shell registers its Visual Studio 2022 themes here) and <see cref="ColorThemeContribution"/>s (the user's
/// <c>{UserDataDir}/themes/*.json</c>, VS Code theme format, loaded by the editor's user-content loader). The selection is persisted in
/// <c>workbench.colorTheme</c> (the theme's label, as VS Code does) and followed live when that setting changes.
/// </summary>
public sealed class ColorThemeService : IColorThemeService, IDisposable
{
    public const string BuiltinPathPrefix = "builtin-theme:";
    private const string DefaultThemeLabel = "Dark+";

    private readonly EditorServices _svc;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly List<ColorTheme> _themes = [];
    private readonly Emitter<ColorTheme> _changed = new();
    private readonly DisposableStore _subscriptions = new();
    private readonly Dictionary<ColorThemeContribution, IDisposable?> _contributed = new();
    private ColorTheme _current;
    private string? _wantedName; // the persisted selection, kept until a theme with that name is registered (the shell registers late)

    public ColorThemeService(EditorServices svc)
    {
        _svc = svc;
        _log = svc.LoggerFactory.CreateLogger<ColorThemeService>();
        foreach (var t in LoadBuiltins()) _themes.Add(t);
        _current = _themes[0];

        _wantedName = svc.Config.GetOr<string?>(SettingKeys.WorkbenchColorTheme, null);
        if (_wantedName is not null && Find(_wantedName) is { } wanted) _current = wanted;

        _subscriptions.Add(svc.Config.Changed.Subscribe(e =>
        {
            if (!e.Affects(SettingKeys.WorkbenchColorTheme)) return;
            var name = svc.Config.GetOr<string?>(SettingKeys.WorkbenchColorTheme, null);
            if (name is not null) Select(name, persist: false);
        }));
        _subscriptions.Add(svc.Contributions.Of<ColorThemeContribution>().Changed.Subscribe(OnContributionsChanged));
        OnContributionsChanged(svc.Contributions.Of<ColorThemeContribution>().Items);
    }

    public ColorTheme Current { get { lock (_gate) return _current; } }

    public IReadOnlyList<ColorTheme> Available { get { lock (_gate) return _themes.ToArray(); } }

    public IEventSource<ColorTheme> Changed => _changed;

    public IDisposable Register(ColorTheme theme)
    {
        ColorTheme? switchedTo = null;
        lock (_gate)
        {
            var existing = _themes.FindIndex(t => t.Id == theme.Id);
            if (existing >= 0)
            {
                var replaced = _themes[existing];
                _themes[existing] = theme;
                if (ReferenceEquals(_current, replaced)) switchedTo = _current = theme;
            }
            else _themes.Add(theme);
            // The persisted selection names this theme (registered after startup): adopt it.
            if (switchedTo is null && _wantedName is not null && Matches(theme, _wantedName) && !ReferenceEquals(_current, theme))
                switchedTo = _current = theme;
        }
        if (switchedTo is not null) _changed.Fire(switchedTo);
        return Disposable.Create(() => Unregister(theme));
    }

    private void Unregister(ColorTheme theme)
    {
        ColorTheme? fallback = null;
        lock (_gate)
        {
            if (!_themes.Remove(theme) || _themes.Count == 0) return;
            if (ReferenceEquals(_current, theme)) fallback = _current = Find(DefaultThemeLabel) ?? _themes[0];
        }
        if (fallback is not null) _changed.Fire(fallback);
    }

    public ValueTask SetCurrentAsync(string themeId, CancellationToken ct = default)
    {
        Select(themeId, persist: true);
        return ValueTask.CompletedTask;
    }

    private void Select(string idOrLabel, bool persist)
    {
        ColorTheme? theme;
        lock (_gate) theme = Find(idOrLabel);
        if (theme is null)
        {
            // Not registered (yet): the shell registers its themes after startup, so remember the choice and adopt it when it appears.
            lock (_gate) _wantedName = idOrLabel;
            _log.LogDebug("Colour theme '{Theme}' is not registered; keeping the current one until it is", idOrLabel);
            return;
        }
        var changed = false;
        lock (_gate)
        {
            _wantedName = theme.Label;
            if (!ReferenceEquals(_current, theme))
            {
                _current = theme;
                changed = true;
            }
        }
        if (changed) _changed.Fire(theme);
        if (persist) _ = PersistAsync(theme.Label);
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

    private ColorTheme? Find(string idOrLabel) =>
        _themes.FirstOrDefault(t => string.Equals(t.Id, idOrLabel, StringComparison.OrdinalIgnoreCase))
        ?? _themes.FirstOrDefault(t => string.Equals(t.Label, idOrLabel, StringComparison.OrdinalIgnoreCase));

    private static bool Matches(ColorTheme t, string name) =>
        string.Equals(t.Id, name, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Label, name, StringComparison.OrdinalIgnoreCase);

    // ---- built-ins ----

    private static IEnumerable<ColorTheme> LoadBuiltins()
    {
        var reader = new BuiltinThemeReader();
        foreach (var (file, id, kind) in new[]
                 {
                     ("dark_plus.json", "vscode.dark-plus", "vs-dark"),
                     ("light_plus.json", "vscode.light-plus", "vs"),
                     ("dark_vs.json", "vscode.dark-vs", "vs-dark"),
                     ("light_vs.json", "vscode.light-vs", "vs"),
                     ("hc_black.json", "vscode.hc-black", "hc-black"),
                     ("hc_light.json", "vscode.hc-light", "hc-light"),
                 })
        {
            var path = BuiltinPathPrefix + file;
            var text = reader.ReadSync(file) ?? throw new InvalidOperationException($"Built-in theme {file} is missing from TextMateSharp.Grammars");
            // Built-ins only include other built-ins, all embedded: a fully synchronous, in-memory load (no I/O, nothing to await).
            yield return VsCodeThemeLoader.LoadSync(id, null, kind, text, path, (_, rel) =>
            {
                var f = rel.TrimStart('.', '/');
                return reader.ReadSync(f) is { } t ? (BuiltinPathPrefix + f, t) : null;
            });
        }
    }

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
        private const string Prefix = "TextMateSharp.Grammars.Resources.Themes.";

        public string? ReadSync(string file)
        {
            using var s = BuiltinLanguages.Open(Prefix + file);
            if (s is null) return null;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        public ValueTask<(string Path, string Text)?> ReadRelativeAsync(string fromPath, string relativePath, CancellationToken ct)
        {
            var file = relativePath.TrimStart('.', '/');
            var text = ReadSync(file);
            return ValueTask.FromResult<(string, string)?>(text is null ? null : (BuiltinPathPrefix + file, text));
        }
    }
}
