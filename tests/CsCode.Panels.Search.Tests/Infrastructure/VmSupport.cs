using System.Collections.Concurrent;
using System.Text.Json;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using CsCode.Panels.Search.ViewModels;
using CsCode.TestDoubles;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Search.Tests;

/// <summary>A dispatcher whose queue the test drains by hand (the "UI thread" is the test thread). Thread-safe for posting from engine threads.</summary>
public sealed class ManualUiDispatcher : UiDispatcherBase
{
    private readonly ConcurrentQueue<Action> _queue = new();

    public override bool CheckAccess() => true;
    protected override void PostCore(Action action, UiPriority priority) => _queue.Enqueue(action);
    public int Pending => _queue.Count;

    public int RunPending()
    {
        var n = 0;
        while (_queue.TryDequeue(out var a)) { a(); n++; }
        return n;
    }

    /// <summary>Drains the queue until <paramref name="done"/> holds (engine threads keep posting), or fails after the timeout.</summary>
    public async Task PumpUntilAsync(Func<bool> done, int timeoutMs = 20_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            RunPending();
            if (done()) return;
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not reached while pumping the dispatcher.");
            await Task.Delay(2);
        }
    }

    public Task PumpAsync(Task task, int timeoutMs = 20_000) => PumpUntilAsync(() => task.IsCompleted, timeoutMs);
}

public sealed class InMemoryStateStorage : IStateStorage
{
    private readonly Dictionary<string, string> _store = new();
    public T? Get<T>(string key, StorageScope scope = StorageScope.Application) =>
        _store.TryGetValue(scope + ":" + key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;
    public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application) => _store[scope + ":" + key] = JsonSerializer.Serialize(value);
    public void Remove(string key, StorageScope scope = StorageScope.Application) => _store.Remove(scope + ":" + key);
    public Task FlushAsync() => Task.CompletedTask;
}

public sealed class FakeKeybindings : IKeybindingService
{
    private readonly Emitter<object?> _changed = new();
    public List<KeyBinding> Bindings { get; } = new();
    public IReadOnlyList<KeyBinding> All => Bindings.ToArray();
    public IReadOnlyList<KeyBinding> GetBindingsFor(string commandId) => Bindings.Where(b => b.Command == commandId).ToArray();
    public string? GetLabel(string commandId) => GetBindingsFor(commandId).FirstOrDefault() is { } b ? string.Join(", ", b.Chord) : null;
    public string ActivePreset => "visualstudio";
    public ValueTask SetPresetAsync(string preset, CancellationToken ct = default) => ValueTask.CompletedTask;
    public KeyResolveResult Resolve(KeyStroke stroke) => new(KeyResolution.None);
    public void ResetChord() { }
    public IReadOnlyList<KeyStroke> PendingChord => [];
    public IEventSource<object?> Changed => _changed;
    public void Fire() => _changed.Fire(null);
}

/// <summary>Decorates the test configuration to record the target of every write and to point UserSettingsPath at a real temp file.</summary>
public sealed class RecordingConfiguration(TestConfigurationService inner, string userSettingsPath) : IConfigurationService
{
    public List<(string Key, object? Value, ConfigurationTarget Target)> Writes { get; } = new();
    public string UserSettingsPath { get; } = userSettingsPath;
    public IReadOnlyList<ConfigurationProperty> Properties => inner.Properties;
    public IEventSource<ConfigurationChangedEventArgs> Changed => inner.Changed;
    public T Get<T>(string key, T defaultValue) => inner.Get(key, defaultValue);
    public T? Get<T>(string key) => inner.Get<T>(key);
    public JsonElement? GetRaw(string key) => inner.GetRaw(key);
    public IDisposable RegisterProperties(IEnumerable<ConfigurationProperty> properties) => inner.RegisterProperties(properties);

    public ValueTask UpdateAsync(string key, object? value, ConfigurationTarget target = ConfigurationTarget.User, CancellationToken ct = default)
    {
        lock (Writes) Writes.Add((key, value, target));
        return inner.UpdateAsync(key, value, target, ct);
    }
}

/// <summary>Everything a view-model test needs: workspace over a temp dir, real engine, fakes for the Core services, a manual UI dispatcher.</summary>
public sealed class VmFixture : IDisposable
{
    public SearchHarness Harness { get; }
    public ManualUiDispatcher Ui { get; } = new();
    public RecordingNavigationService Navigation { get; }
    public RecordingWindowService Windows { get; } = new();
    public InMemoryStateStorage State { get; } = new();
    public TestCommandService Commands { get; } = new();
    public TestContextKeyService ContextKeys { get; } = new();
    public FakeKeybindings Keybindings { get; } = new();
    public ReplaceService Replace { get; }
    public SearchServices Services { get; }
    public IConfigurationService Configuration { get; }
    public TestConfigurationService ConfigStore => Harness.Config;
    public string UserSettingsFile { get; }

    public VmFixture(SearchLimits? limits = null)
    {
        Harness = new SearchHarness(limits);
        Navigation = new RecordingNavigationService(Harness.Documents);
        Replace = new ReplaceService(Harness.Workspace, Harness.Documents, limits);
        UserSettingsFile = Harness.Dir.Combine("user-data", "settings.json");
        Configuration = new RecordingConfiguration(Harness.Config, UserSettingsFile);
        Services = new SearchServices
        {
            Workspace = Harness.Workspace, Ui = Ui, Engine = Harness.Engine, Replace = Replace, Configuration = Configuration, Documents = Harness.Documents,
            Navigation = Navigation, Windows = Windows, State = State, Commands = Commands, Keybindings = Keybindings, ContextKeys = ContextKeys,
            FileSystem = Harness.Workspace.FileSystem,
        };
    }

    public RecordingConfiguration Recording => (RecordingConfiguration)Configuration;

    public FindInFilesViewModel NewFind() => new(Services);

    /// <summary>The same services with another window service and/or navigation service (shell behaviours the default fakes do not have).</summary>
    public SearchServices With(IWindowService? windows = null, INavigationService? navigation = null) => new()
    {
        Workspace = Services.Workspace, Ui = Services.Ui, Engine = Services.Engine, Replace = Services.Replace, Configuration = Services.Configuration,
        Documents = Services.Documents, Navigation = navigation ?? Services.Navigation, Windows = windows ?? Services.Windows, State = Services.State,
        Commands = Services.Commands, Keybindings = Services.Keybindings, ContextKeys = Services.ContextKeys, FileSystem = Services.FileSystem,
    };

    public async Task SearchAsync(FindInFilesViewModel vm, string query)
    {
        vm.Query = query;
        vm.Start();
        await Ui.PumpAsync(vm.WaitForSearchAsync());
    }

    public void Dispose()
    {
        Replace.Dispose();
        Harness.Dispose();
    }
}
