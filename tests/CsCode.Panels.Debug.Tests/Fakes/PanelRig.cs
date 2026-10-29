using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.TestDoubles;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Debug.Tests.Fakes;

/// <summary>Round-trips values through JSON like the real storage does.</summary>
public sealed class InMemoryStateStorage : IStateStorage
{
    private readonly Dictionary<(string, StorageScope), string> _values = new();

    public T? Get<T>(string key, StorageScope scope = StorageScope.Application) =>
        _values.TryGetValue((key, scope), out var json) ? JsonSerializer.Deserialize<T>(json, CoreJson.Options) : default;

    public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application) => _values[(key, scope)] = JsonSerializer.Serialize(value, CoreJson.Options);
    public void Remove(string key, StorageScope scope = StorageScope.Application) => _values.Remove((key, scope));
    public Task FlushAsync() => Task.CompletedTask;
}

/// <summary>Everything a panel view-model needs, wired to fakes, plus helpers to drive a stop.</summary>
public sealed class PanelRig : IDisposable
{
    public FakeDebugService Debug { get; } = new();
    public FakeDebugSession Session { get; }
    public TestWindowService Window { get; } = new();
    public DocumentManager Documents { get; } = new();
    public RecordingNavigationService Navigation { get; }
    public InMemoryStateStorage State { get; } = new();
    public TestContextKeyService ContextKeys { get; } = new();
    public IUiDispatcher Ui { get; }
    public DebugPanelServices Services { get; }

    public PanelRig(bool withSession = true, IUiDispatcher? ui = null, FakeDebugSession? session = null)
    {
        Ui = ui ?? AvaloniaUiDispatcher.Instance;
        Navigation = new RecordingNavigationService(Documents);
        Session = session ?? new FakeDebugSession().WithDefaultScript();
        Services = new DebugPanelServices(Debug, Ui, Debug, Debug, Navigation, Window, null, ContextKeys, State, Documents);
        if (withSession) Debug.AddSession(Session);
    }

    /// <summary>Stops at <paramref name="frameIndex"/> and publishes the stop location (the real service does that a moment after <c>stopped</c>).</summary>
    public void StopAt(int frameIndex = 0, string reason = "breakpoint", string? description = null, string? text = null) => Debug.StopAt(Session, reason, frameIndex, 1, description, text);

    /// <summary>The debuggee resumes (step/continue): handles die, the service clears the stop location.</summary>
    public void Resume()
    {
        Session.Resume();
        Debug.SetStopLocation(null);
    }

    public static async Task Eventually(Func<bool> condition, int timeoutMs = 5_000, string? what = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(4);
        }
        if (!condition()) throw new TimeoutException("Condition not reached in time" + (what is null ? "" : ": " + what));
    }

    public void Dispose() { }
}
