using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;

namespace CsCode.TestDoubles;

/// <summary>Minimal in-memory <see cref="ICommandService"/> (stack semantics: later registration shadows earlier).</summary>
public sealed class TestCommandService : ICommandService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Func<CommandInvocation, ValueTask<object?>>>> _handlers = new();
    private readonly Dictionary<string, CommandMetadata> _meta = new();
    private readonly Emitter<string> _registered = new();

    public IEventSource<string> Registered => _registered;
    public IReadOnlyList<CommandMetadata> All { get { lock (_gate) return _meta.Values.ToArray(); } }
    public bool Has(string id) { lock (_gate) return _handlers.TryGetValue(id, out var l) && l.Count > 0; }

    public IDisposable Register(string id, Func<CommandInvocation, ValueTask<object?>> handler, CommandMetadata? metadata = null)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(id, out var list)) _handlers[id] = list = new();
            list.Add(handler);
            _meta[id] = metadata ?? new CommandMetadata(id);
        }
        _registered.Fire(id);
        return Disposable.Create(() => { lock (_gate) { if (_handlers.TryGetValue(id, out var l)) l.Remove(handler); } });
    }

    public async ValueTask<object?> ExecuteAsync(string id, params object?[] args)
    {
        var handler = Find(id);
        if (handler is null) throw new CommandNotFoundException(id);
        return await handler(new CommandInvocation(id, args));
    }

    private Func<CommandInvocation, ValueTask<object?>>? Find(string id)
    {
        lock (_gate) return _handlers.TryGetValue(id, out var l) && l.Count > 0 ? l[^1] : null;
    }
}

/// <summary>Dictionary-backed <see cref="IConfigurationService"/>; use <see cref="Set"/> in arrange steps.</summary>
public sealed class TestConfigurationService : IConfigurationService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonElement> _values = new();
    private readonly List<ConfigurationProperty> _props = new();
    private readonly Emitter<ConfigurationChangedEventArgs> _changed = new();

    public string UserSettingsPath => Path.Combine(Path.GetTempPath(), "cscode-test-settings.json");
    public IReadOnlyList<ConfigurationProperty> Properties { get { lock (_gate) return _props.ToArray(); } }
    public IEventSource<ConfigurationChangedEventArgs> Changed => _changed;

    public void Set(string key, object? value)
    {
        lock (_gate) _values[key] = JsonSerializer.SerializeToElement(value);
        _changed.Fire(new ConfigurationChangedEventArgs([key]));
    }

    public T Get<T>(string key, T defaultValue) => Get<T>(key) is { } v ? v : defaultValue;

    public T? Get<T>(string key)
    {
        lock (_gate)
        {
            if (_values.TryGetValue(key, out var v)) return v.Deserialize<T>(CoreJson.Options);
            var p = _props.FirstOrDefault(x => x.Key == key);
            return p?.Default is { } d ? d.Deserialize<T>(CoreJson.Options) : default;
        }
    }

    public JsonElement? GetRaw(string key) { lock (_gate) return _values.TryGetValue(key, out var v) ? v : null; }

    public ValueTask UpdateAsync(string key, object? value, ConfigurationTarget target = ConfigurationTarget.User, CancellationToken ct = default)
    {
        Set(key, value);
        return ValueTask.CompletedTask;
    }

    public IDisposable RegisterProperties(IEnumerable<ConfigurationProperty> properties)
    {
        var list = properties.ToList();
        lock (_gate) _props.AddRange(list);
        return Disposable.Create(() => { lock (_gate) foreach (var p in list) _props.Remove(p); });
    }
}

/// <summary>Records every interaction; answers prompts from queued responses (or the first item / null).</summary>
public sealed class RecordingWindowService : IWindowService
{
    public List<(MessageSeverity Severity, string Message)> Messages { get; } = new();
    public List<string> StatusMessages { get; } = new();
    public Queue<string?> MessageActionResponses { get; } = new();
    public Queue<string?> InputResponses { get; } = new();
    public Queue<int?> QuickPickResponses { get; } = new();

    public ValueTask<string?> ShowMessageAsync(MessageSeverity severity, string message, IReadOnlyList<MessageAction>? actions = null, CancellationToken ct = default)
    {
        lock (Messages) Messages.Add((severity, message));
        return ValueTask.FromResult(MessageActionResponses.Count > 0 ? MessageActionResponses.Dequeue() : null);
    }

    public ValueTask<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default)
    {
        var idx = QuickPickResponses.Count > 0 ? QuickPickResponses.Dequeue() : (items.Count > 0 ? 0 : null);
        return ValueTask.FromResult(idx is { } i && i < items.Count ? items[i] : null);
    }

    public ValueTask<IReadOnlyList<QuickPickItem>?> ShowQuickPickManyAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<QuickPickItem>?>(items.Where(i => i.Picked).ToArray());

    public ValueTask<string?> ShowInputBoxAsync(InputBoxOptions? options = null, CancellationToken ct = default) =>
        ValueTask.FromResult(InputResponses.Count > 0 ? InputResponses.Dequeue() : options?.Value);

    public ValueTask<IReadOnlyList<Uri>?> ShowOpenDialogAsync(OpenDialogOptions options, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Uri>?>(null);
    public ValueTask<Uri?> ShowSaveDialogAsync(SaveDialogOptions options, CancellationToken ct = default) => ValueTask.FromResult<Uri?>(null);

    public async Task<T> WithProgressAsync<T>(string title, bool cancellable, Func<IProgressReporter, CancellationToken, Task<T>> work) =>
        await work(new NullProgress(), CancellationToken.None);

    public IDisposable SetStatusBarMessage(string text, TimeSpan? timeout = null) { lock (StatusMessages) StatusMessages.Add(text); return Disposable.Empty; }
    public ValueTask<bool> OpenExternalAsync(Uri uri) => ValueTask.FromResult(true);
    public ValueTask CopyToClipboardAsync(string text) => ValueTask.CompletedTask;

    private sealed class NullProgress : IProgressReporter { public void Report(string? message = null, double? percent = null) { } }
}

public sealed class InMemoryOutputService : IOutputService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Channel> _channels = new();
    private readonly Emitter<string> _changed = new();
    public IReadOnlyList<string> ChannelNames { get { lock (_gate) return _channels.Keys.ToArray(); } }
    public IEventSource<string> ChannelsChanged => _changed;

    public IOutputChannel GetOrCreate(string name)
    {
        bool created;
        Channel ch;
        lock (_gate)
        {
            created = !_channels.TryGetValue(name, out ch!);
            if (created) _channels[name] = ch = new Channel(name);
        }
        if (created) _changed.Fire(name);
        return ch;
    }

    public string Text(string name) { lock (_gate) return _channels.TryGetValue(name, out var c) ? c.GetText() : ""; }

    private sealed class Channel(string name) : IOutputChannel
    {
        private readonly System.Text.StringBuilder _sb = new();
        private readonly Emitter<string> _appended = new();
        public string Name { get; } = name;
        public IEventSource<string> Appended => _appended;
        public void Append(string text) { lock (_sb) { _sb.Append(text); if (_sb.Length > 2_000_000) _sb.Remove(0, _sb.Length - 1_500_000); } _appended.Fire(text); }
        public void AppendLine(string line) => Append(line + "\n");
        public void Clear() { lock (_sb) _sb.Clear(); }
        public string GetText() { lock (_sb) return _sb.ToString(); }
        public void Dispose() { }
    }
}

public sealed class InMemoryStatusBarService : IStatusBarService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StatusBarItemModel> _items = new();
    private readonly Emitter<IReadOnlyList<StatusBarItemModel>> _changed = new();
    public IReadOnlyList<StatusBarItemModel> Items { get { lock (_gate) return _items.Values.ToArray(); } }
    public IEventSource<IReadOnlyList<StatusBarItemModel>> Changed => _changed;

    public IDisposable Set(StatusBarItemModel item)
    {
        lock (_gate) _items[item.Id] = item;
        _changed.Fire(Items);
        return Disposable.Create(() => { lock (_gate) _items.Remove(item.Id); _changed.Fire(Items); });
    }
}

/// <summary>Records navigation; applies WorkspaceEdits to documents registered in the <see cref="IDocumentManager"/>.</summary>
public sealed class RecordingNavigationService(IDocumentManager documents) : INavigationService
{
    private readonly Emitter<IEditorDocument?> _active = new();
    public List<(Uri Uri, TextRange? Selection)> Opened { get; } = new();
    public List<WorkspaceEdit> AppliedEdits { get; } = new();
    public IEditorDocument? ActiveDocument { get; set; }
    public TextPosition? ActiveCaret { get; set; }
    public IEventSource<IEditorDocument?> ActiveDocumentChanged => _active;
    public IReadOnlyList<IEditorDocument> OpenDocuments => documents.Documents.OfType<IEditorDocument>().ToArray();

    public ValueTask<IEditorDocument?> OpenAsync(Uri uri, TextRange? selection = null, bool preview = false, bool preserveFocus = false, CancellationToken ct = default)
    {
        Opened.Add((uri, selection));
        return ValueTask.FromResult(documents.Get(uri) as IEditorDocument);
    }

    public ValueTask<bool> ApplyWorkspaceEditAsync(WorkspaceEdit edit, string? label = null, CancellationToken ct = default)
    {
        AppliedEdits.Add(edit);
        foreach (var (uri, edits) in edit.Changes)
            if (documents.Get(uri) is { } doc && !doc.ApplyEdits(edits)) return ValueTask.FromResult(false);
        return ValueTask.FromResult(true);
    }
}

/// <summary>Truthiness-only context keys (no expression parser) — enough for non-Platform module tests.</summary>
public sealed class TestContextKeyService : IContextKeyService
{
    private readonly Dictionary<string, object?> _v = new();
    private readonly Emitter<IReadOnlyCollection<string>> _changed = new();
    public IEventSource<IReadOnlyCollection<string>> Changed => _changed;
    public void Set(string key, object? value) { lock (_v) _v[key] = value; _changed.Fire([key]); }
    public object? Get(string key) { lock (_v) return _v.GetValueOrDefault(key); }
    public bool Evaluate(string? whenClause)
    {
        if (string.IsNullOrWhiteSpace(whenClause)) return true;
        var neg = whenClause.StartsWith('!');
        var v = Get(neg ? whenClause[1..] : whenClause);
        var truthy = v is true || (v is string s && s.Length > 0) || (v is int i && i != 0);
        return neg ? !truthy : truthy;
    }
    public IContextKeyService CreateScope() => this;
}

/// <summary>App paths rooted in a temp directory.</summary>
public sealed class TestAppPaths : IAppPaths, IDisposable
{
    private readonly TempDirectory _dir = new("cscode-home-");
    public string UserDataDir => Dir("user");
    public string LogsDir => Dir("logs");
    public string CacheDir => Dir("cache");
    public string GlobalStorageDir => Dir("globalStorage");
    public string ToolsDir => Dir("tools");
    public string InstallDir { get; set; } = AppContext.BaseDirectory;
    public string? WorkspaceStorageDir(Uri? workspaceFolder) => workspaceFolder is null ? null : Dir("workspaceStorage/" + Math.Abs(workspaceFolder.ToString().GetHashCode()));
    private string Dir(string name) { var p = _dir.Combine(name.Split('/')); Directory.CreateDirectory(p); return p; }
    public void Dispose() => _dir.Dispose();
}
