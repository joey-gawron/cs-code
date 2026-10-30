using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Panels.Search.ViewModels;
using CsCode.Ui.Collections;
using CsCode.Ui.Text;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Search.Settings;

public enum SettingsScope { User, Workspace }

/// <summary>One row of the category tree on the left (Level 0 = category, 1 = sub-category; Id "" = all settings).</summary>
public sealed record CategoryNode(string Id, string Title, int Level, int Count);

/// <summary>
/// The Settings document view: every registered <see cref="ConfigurationProperty"/> rendered by its schema, grouped by key prefix,
/// filtered with the fuzzy matcher (and <c>@modified</c>), edited per scope (User / Workspace). Values are read from
/// <see cref="IConfigurationService"/> and follow its <c>Changed</c> event; edits go back through <c>UpdateAsync</c> (the platform's
/// comment-preserving writer), debounced for text and numbers so typing never writes per keystroke. UI-thread object.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SearchServices _s;
    private readonly DisposableStore _subs = new();
    private readonly List<SettingItemViewModel> _all = new();
    private readonly Dictionary<string, PendingWrite> _pending = new(StringComparer.Ordinal);
    private readonly string _refreshKey = "settings.refresh." + Guid.NewGuid().ToString("N");
    private readonly int _debounceMs;
    private IReadOnlyList<ScopeFile> _scopeFiles = [];
    private string _searchText = "";
    private CategoryNode? _selectedCategory;
    private SettingsScope _scope = SettingsScope.User;
    private bool _disposed;

    private sealed class PendingWrite(CancellationTokenSource cts, object? value, ConfigurationTarget target, Task completion)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public object? Value { get; } = value;
        public ConfigurationTarget Target { get; } = target;
        public Task Completion { get; set; } = completion;
        private int _claimed;

        /// <summary>Exactly one of the debounced write, a flush and a superseding edit takes the write: the first to claim it.</summary>
        public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
    }

    public SettingsViewModel(SearchServices services, int debounceMs = 400)
    {
        _s = services;
        _debounceMs = debounceMs;
        OpenJsonCommand = new RelayCommand(() => _ = OpenSettingsJsonAsync());
        ClearFilterCommand = new RelayCommand(() => SearchText = "");
        Reload();
        if (services.Configuration is { } cfg)
            _subs.Add(cfg.Changed.Subscribe(args => services.Ui.PostCoalesced(_refreshKey, () => { _ = RefreshAsync(); }, UiPriority.Background)));
        _subs.Add(services.Workspace.FoldersChanged.Subscribe(args => services.Ui.Post(() =>
        {
            OnPropertyChanged(nameof(IsWorkspaceAvailable));
            if (!IsWorkspaceAvailable && _scope == SettingsScope.Workspace) SelectedScope = SettingsScope.User;
            _ = RefreshAsync();
        })));
        _ = RefreshAsync();
    }

    public BulkObservableCollection<CategoryNode> Categories { get; } = new();
    public BulkObservableCollection<SettingItemViewModel> Items { get; } = new();
    public ICommand OpenJsonCommand { get; }
    public ICommand ClearFilterCommand { get; }

    public IReadOnlyList<SettingItemViewModel> AllItems => _all;

    /// <summary>
    /// The services the page's Environment &gt; Appearance section needs for its Color Theme dropdown (the same control as the toolbar's,
    /// <c>CsCode.Ui.Theming.ThemePicker</c>); null when no colour theme service is composed, and the section is then not shown.
    /// </summary>
    public IColorThemeService? Themes => _s.Themes;
    public IUiDispatcher Ui => _s.Ui;
    public IConfigurationService? Configuration => _s.Configuration;
    public int ModifiedCount => _all.Count(i => i.IsModified);
    public bool IsWorkspaceAvailable => _s.Workspace.Folders.Count > 0;
    public int DebounceMs => _debounceMs;

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ApplyFilter(); }
    }

    public CategoryNode? SelectedCategory
    {
        get => _selectedCategory;
        set { if (SetProperty(ref _selectedCategory, value)) ApplyFilter(); }
    }

    public SettingsScope SelectedScope
    {
        get => _scope;
        set
        {
            if (value == SettingsScope.Workspace && !IsWorkspaceAvailable) return;
            if (!SetProperty(ref _scope, value)) return;
            foreach (var item in _all) item.DiscardInvalidEdit(); // half-typed text belonged to the other scope's value
            OnPropertyChanged(nameof(IsUserScope));
            OnPropertyChanged(nameof(IsWorkspaceScope));
            _ = RefreshAsync();
        }
    }

    /// <summary>Two-way friendly flags for the scope tabs.</summary>
    public bool IsUserScope { get => _scope == SettingsScope.User; set { if (value) SelectedScope = SettingsScope.User; else OnPropertyChanged(nameof(IsUserScope)); } }
    public bool IsWorkspaceScope { get => _scope == SettingsScope.Workspace; set { if (value) SelectedScope = SettingsScope.Workspace; else OnPropertyChanged(nameof(IsWorkspaceScope)); } }

    private ConfigurationTarget Target => _scope == SettingsScope.Workspace ? ConfigurationTarget.Workspace : ConfigurationTarget.User;

    // -------------------------------------------------------------------------------------- building

    /// <summary>(Re)creates the item view-models from the registered properties.</summary>
    public void Reload()
    {
        _all.Clear();
        var props = _s.Configuration?.Properties ?? [];
        foreach (var p in props.GroupBy(p => p.Key).Select(g => g.Last())
                     .OrderBy(p => SettingsCategories.Of(p.Key).Top, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(p => SettingsCategories.Of(p.Key).Sub ?? "", StringComparer.OrdinalIgnoreCase)
                     .ThenBy(p => p.Key, StringComparer.Ordinal))
            _all.Add(SettingItemViewModel.Create(this, p));
        BuildCategories();
        foreach (var item in _all) LoadItem(item, null);
        ApplyFilter();
    }

    private void BuildCategories()
    {
        var previous = SelectedCategory?.Id;
        var nodes = new List<CategoryNode> { new("", "All Settings", 0, _all.Count) };
        foreach (var top in _all.GroupBy(i => SettingsCategories.Of(i.Key).Top).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            nodes.Add(new CategoryNode(top.Key, top.Key, 0, top.Count()));
            foreach (var sub in top.Where(i => SettingsCategories.Of(i.Key).Sub is not null).GroupBy(i => SettingsCategories.Of(i.Key).Sub!).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                nodes.Add(new CategoryNode(top.Key + "/" + sub.Key, sub.Key, 1, sub.Count()));
        }
        Categories.ReplaceAll(nodes);
        _selectedCategory = nodes.FirstOrDefault(n => n.Id == previous) ?? nodes[0];
        OnPropertyChanged(nameof(SelectedCategory));
    }

    // ----------------------------------------------------------------------------------- values/scope

    /// <summary>Re-reads the scope files and the effective values and pushes them into the items (never into items being edited).</summary>
    public async Task RefreshAsync()
    {
        if (_disposed) return;
        var registered = (_s.Configuration?.Properties ?? []).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var files = new List<ScopeFile>();
        foreach (var (label, target, path) in SettingsFiles())
            files.Add(new ScopeFile(label, target, await ReadScopeAsync(path, registered)));
        if (_disposed) return;
        _scopeFiles = files;
        foreach (var item in _all) LoadItem(item, _scopeFiles);
        OnPropertyChanged(nameof(ModifiedCount));
        ApplyFilter();
    }

    private void LoadItem(SettingItemViewModel item, IReadOnlyList<ScopeFile>? files)
    {
        // The user is typing: never overwrite the box, neither with an edit waiting for its debounce nor with text that is not valid yet.
        if (_pending.ContainsKey(item.Key) || item.HasInvalidEdit) return;

        var cfg = _s.Configuration;
        var merged = cfg?.GetRaw(item.Key);
        if (merged is { ValueKind: JsonValueKind.Null }) merged = null;
        JsonElement? own = null;
        List<string>? elsewhere = null;
        foreach (var file in files ?? [])
        {
            if (file.Values is null || !file.Values.TryGetValue(item.Key, out var value)) continue;
            if (file.Target == Target) own = value;
            else (elsewhere ??= []).Add(file.Label);
        }
        item.Load(own ?? merged ?? item.Default);

        // Modified = set in the file this tab writes (so Reset can remove it). Only when no file mentions the key at all does a value that
        // differs from the default count as modified. A key that only .vscode/settings.json or another folder sets is not modified here:
        // the tab could not reset it; "(also modified in …)" says where it is.
        item.IsModified = own is not null || (elsewhere is null && merged is { } m && !Same(m, item.Default));
        item.AlsoModifiedIn = elsewhere is null ? null : string.Join(", ", elsewhere);
    }

    private static bool Same(JsonElement a, JsonElement? b) => b is { } e && a.GetRawText() == e.GetRawText();

    /// <summary>One settings file of the layering; <see cref="Target"/> is set for the two files the tabs write (User, Workspace).</summary>
    private sealed record ScopeFile(string Label, ConfigurationTarget? Target, IReadOnlyDictionary<string, JsonElement>? Values);

    /// <summary>
    /// The settings files, in the order the platform's <c>ConfigurationService</c> layers them. Core's <see cref="IConfigurationService"/> has
    /// no per-scope "inspect", so the panel reads the same files itself: the user settings.json, then the primary folder's
    /// .vscode/settings.json and .cscode/settings.json (the Workspace tab's file), then every other folder's .cscode/settings.json.
    /// </summary>
    internal IEnumerable<(string Label, ConfigurationTarget? Target, string Path)> SettingsFiles()
    {
        if (_s.Configuration?.UserSettingsPath is { Length: > 0 } user) yield return ("User", ConfigurationTarget.User, user);
        var folders = _s.Workspace.Folders;
        if (folders.Count == 0) yield break;
        if (folders[0] is { IsFile: true } primary)
        {
            yield return (".vscode/settings.json", null, Path.Combine(primary.LocalPath, ".vscode", "settings.json"));
            yield return ("Workspace", ConfigurationTarget.Workspace, Path.Combine(primary.LocalPath, ".cscode", "settings.json"));
        }
        foreach (var folder in folders.Skip(1).Where(f => f.IsFile))
            yield return (Path.GetFileName(folder.LocalPath.TrimEnd('/', '\\')) + "/.cscode/settings.json", null, Path.Combine(folder.LocalPath, ".cscode", "settings.json"));
    }

    private async Task<IReadOnlyDictionary<string, JsonElement>?> ReadScopeAsync(string path, ISet<string> registered)
    {
        var fs = _s.FileSystem ?? _s.Workspace.FileSystem;
        try
        {
            var uri = FileUri(path);
            if (await fs.StatAsync(uri) is null) return new Dictionary<string, JsonElement>();
            var bytes = await fs.ReadFileAsync(uri);
            return Flatten(bytes, registered);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null; // unreadable or invalid: unknown, fall back to the effective values
        }
    }

    internal string? SettingsFilePath(ConfigurationTarget scope)
    {
        if (scope == ConfigurationTarget.User) return _s.Configuration?.UserSettingsPath;
        var folder = _s.Workspace.Folders.FirstOrDefault();
        return folder is { IsFile: true } ? Path.Combine(folder.LocalPath, ".cscode", "settings.json") : null;
    }

    private static Uri FileUri(string path) => new(new Uri("file:///"), path.StartsWith('/') ? path : "/" + path.Replace('\\', '/'));

    /// <summary>Flattens a settings.json: <c>{"editor":{"fontSize":14}}</c> and <c>"editor.fontSize": 14</c> both become the key editor.fontSize; registered object settings stay whole.</summary>
    internal static IReadOnlyDictionary<string, JsonElement> Flatten(byte[] bytes, ISet<string> registered)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        // A UTF-8 BOM (common on Windows; the platform's writer keeps it) is not skipped by the byte overload of JsonDocument.Parse.
        var json = bytes.AsMemory(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0);
        if (json.Length == 0) return result;
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
        void Walk(JsonElement obj, string prefix)
        {
            foreach (var p in obj.EnumerateObject())
            {
                var key = prefix + p.Name;
                if (p.Value.ValueKind == JsonValueKind.Object && !registered.Contains(key) && registered.Any(k => k.StartsWith(key + ".", StringComparison.Ordinal))) Walk(p.Value, key + ".");
                else result[key] = p.Value.Clone();
            }
        }
        Walk(doc.RootElement, "");
        return result;
    }

    // ------------------------------------------------------------------------------------- filtering

    public void ApplyFilter()
    {
        if (_disposed) return;
        var text = SearchText.Trim();
        var modifiedOnly = false;
        if (text.Contains("@modified", StringComparison.OrdinalIgnoreCase))
        {
            modifiedOnly = true;
            text = System.Text.RegularExpressions.Regex.Replace(text, "@modified", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        }
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<SettingItemViewModel> source = _all;

        if (words.Length == 0)
        {
            var cat = SelectedCategory;
            if (cat is { Id.Length: > 0 })
                source = cat.Level == 0
                    ? source.Where(i => SettingsCategories.Of(i.Key).Top == cat.Id)
                    : source.Where(i => SettingsCategories.Of(i.Key) is var (t, s) && t + "/" + s == cat.Id);
            if (modifiedOnly) source = source.Where(i => i.IsModified);
            Items.ReplaceAll(source.ToList());
            return;
        }

        // Searching spans every category; each word must fuzzy-match title, key or description.
        var scored = new List<(SettingItemViewModel Item, int Score)>();
        foreach (var item in source)
        {
            if (modifiedOnly && !item.IsModified) continue;
            var haystack = item.Title + " " + item.Key + " " + item.Description;
            var total = 0;
            var ok = true;
            foreach (var w in words)
            {
                // Substring anywhere (the description included) is a hit; the fuzzy fallback only looks at title and key, because a
                // subsequence match across a whole description matches nearly everything.
                int s;
                if (item.Key.Contains(w, StringComparison.OrdinalIgnoreCase) || item.Title.Contains(w, StringComparison.OrdinalIgnoreCase)) s = 140;
                else if (haystack.Contains(w, StringComparison.OrdinalIgnoreCase)) s = 60;
                else s = FuzzyMatcher.Score(w, item.Title + " " + item.Key);
                if (s < 0) { ok = false; break; }
                total += s;
            }
            if (ok) scored.Add((item, total));
        }
        Items.ReplaceAll(scored.OrderByDescending(x => x.Score).ThenBy(x => x.Item.Key, StringComparer.Ordinal).Select(x => x.Item).ToList());
    }

    // -------------------------------------------------------------------------------------- writing

    /// <summary>Called by an item when the user edited it. Text and numbers are debounced; toggles and choices write at once.</summary>
    internal void Write(SettingItemViewModel item, object value, bool debounce)
    {
        if (_disposed || _s.Configuration is null) return;
        var target = IsWorkspaceAvailable && _scope == SettingsScope.Workspace ? ConfigurationTarget.Workspace : ConfigurationTarget.User;
        if (_pending.Remove(item.Key, out var old))
        {
            old.TryClaim(); // superseded: it writes nothing unless its write already started
            old.Cts.Cancel();
        }
        var cts = new CancellationTokenSource();
        var pending = new PendingWrite(cts, value, target, Task.CompletedTask);
        _pending[item.Key] = pending;
        pending.Completion = RunWriteAsync(item, pending, debounce);
    }

    private async Task RunWriteAsync(SettingItemViewModel item, PendingWrite pending, bool debounce)
    {
        try
        {
            if (debounce && _debounceMs > 0)
            {
                try { await Task.Delay(_debounceMs, pending.Cts.Token); }
                catch (OperationCanceledException) { return; } // superseded by a newer edit (or flushed: see FlushAsync)
            }
            // the delay may have ended just before a flush or a newer edit took the write: then it is theirs
            if (pending.Cts.IsCancellationRequested || !pending.TryClaim()) return;
            await _s.Configuration!.UpdateAsync(item.Key, pending.Value, pending.Target);
            item.IsModified = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            item.ReportError("Could not save: " + ex.Message);
        }
        finally
        {
            if (_pending.TryGetValue(item.Key, out var current) && ReferenceEquals(current, pending)) _pending.Remove(item.Key);
        }
    }

    /// <summary>Writes every edit that is still waiting for its debounce (used when the view closes, and by tests).</summary>
    public async Task FlushAsync()
    {
        // Snapshot first: Dispose clears the item list right after starting this, so everything needed is captured synchronously.
        var work = _pending.Select(kv => (kv.Value, Item: _all.FirstOrDefault(i => i.Key == kv.Key), kv.Key)).ToList();
        _pending.Clear();
        foreach (var (pending, item, key) in work)
        {
            pending.Cts.Cancel();
            if (!pending.TryClaim())
            {
                await pending.Completion; // its debounced write is already under way: wait for it rather than write the value twice
                continue;
            }
            if (item is null || _s.Configuration is null) continue;
            try { await _s.Configuration.UpdateAsync(key, pending.Value, pending.Target); item.IsModified = true; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { item.ReportError("Could not save: " + ex.Message); }
        }
    }

    /// <summary>Waits for edits that are already being written.</summary>
    public async Task WaitIdleAsync()
    {
        foreach (var p in _pending.Values.ToArray()) await p.Completion;
    }

    public async Task ResetAsync(SettingItemViewModel item)
    {
        if (_s.Configuration is null) return;
        if (_pending.Remove(item.Key, out var p)) p.Cts.Cancel();
        item.DiscardInvalidEdit();
        try
        {
            // null removes the key from the file; the effective value falls back to the next scope or the default.
            await _s.Configuration.UpdateAsync(item.Key, null, Target);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            item.ReportError("Could not reset: " + ex.Message);
            return;
        }
        await RefreshAsync();
    }

    public async Task CopyIdAsync(SettingItemViewModel item)
    {
        if (_s.Windows is not null) await _s.Windows.CopyToClipboardAsync(item.Key);
    }

    // ------------------------------------------------------------------------------- settings.json

    public Task OpenSettingsJsonAsync() => OpenFileAtAsync(Target, null, null);

    /// <summary>Opens the scope's settings.json with the key selected, adding it with its current value first if the file does not mention it.</summary>
    public Task EditInJsonAsync(SettingItemViewModel item) => OpenFileAtAsync(Target, item.Key, item);

    private async Task OpenFileAtAsync(ConfigurationTarget target, string? key, SettingItemViewModel? item)
    {
        if (_s.Navigation is null || _s.Configuration is null) return;
        if (target == ConfigurationTarget.Workspace && !IsWorkspaceAvailable) target = ConfigurationTarget.User;
        var path = SettingsFilePath(target);
        if (path is null) return;
        var fs = _s.FileSystem ?? _s.Workspace.FileSystem;
        var uri = FileUri(path);
        try
        {
            if (await fs.StatAsync(uri) is null) await fs.WriteFileAsync(uri, "{\n}\n"u8.ToArray(), createParents: true);
            TextRange? selection = null;
            if (key is not null)
            {
                var text = System.Text.Encoding.UTF8.GetString(await fs.ReadFileAsync(uri));
                var needle = "\"" + key + "\"";
                if (text.IndexOf(needle, StringComparison.Ordinal) < 0 && item is not null)
                {
                    var value = _s.Configuration.GetRaw(key) ?? item.Default;
                    await _s.Configuration.UpdateAsync(key, value, target);
                    text = System.Text.Encoding.UTF8.GetString(await fs.ReadFileAsync(uri));
                }
                var at = text.IndexOf(needle, StringComparison.Ordinal);
                if (at >= 0)
                {
                    var doc = new InMemoryTextDocument(uri, "jsonc", text);
                    selection = new TextRange(doc.PositionAt(at + 1), doc.PositionAt(at + 1 + key.Length));
                }
            }
            await _s.Navigation.OpenAsync(uri, selection);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (_s.Windows is not null) await _s.Windows.ShowMessageAsync(MessageSeverity.Error, "Could not open the settings file: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Edits still waiting for their debounce are written now, so closing the tab never loses a change.
        _ = FlushAsync();
        _disposed = true;
        _subs.Dispose();
        Items.Clear();
        Categories.Clear();
        _all.Clear();
        _pending.Clear();
    }
}
