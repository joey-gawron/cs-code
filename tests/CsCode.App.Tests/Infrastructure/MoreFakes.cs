using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;

namespace CsCode.App.Tests.Infrastructure;

/// <summary>TimeProvider whose timers fire only when the test advances time.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = new();
    private TimeSpan _now = TimeSpan.Zero;

    public int ActiveTimers => _timers.Count(t => !t.Disposed);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(callback, state, _now + dueTime);
        _timers.Add(t);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
        foreach (var t in _timers.Where(t => !t.Disposed && !t.Fired && t.Due <= _now).ToArray())
        {
            t.Fired = true;
            t.Callback(t.State);
        }
        _timers.RemoveAll(t => t.Disposed || t.Fired);
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public TimeSpan Due { get; } = due;
        public bool Fired { get; set; }
        public bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}

/// <summary>Debug service double: tests flip <see cref="SetState"/> and read the shell's reactions.</summary>
public sealed class FakeDebugService : IDebugService
{
    private readonly Emitter<DebugState> _state = new();
    private readonly Emitter<IDebugSession> _started = new(), _terminated = new();
    private readonly Emitter<IDebugSession?> _active = new();
    private readonly Emitter<DebugStopLocation?> _stop = new();
    private readonly Emitter<DebugOutputEventArgs> _output = new();

    public IReadOnlyList<IDebugSession> Sessions => [];
    public IDebugSession? ActiveSession { get; set; }
    public DebugState State { get; private set; }
    public DebugStopLocation? StopLocation { get; private set; }
    // The composed Debug panels read breakpoints; a real in-memory manager (no storage) is the simplest faithful double.
    public IBreakpointManager Breakpoints { get; } = new CsCode.Dap.Breakpoints.BreakpointManager();
    public IDebugConfigurationService Configurations => Config;
    public FakeDebugConfigurations Config { get; } = new();
    public IReadOnlyList<DebuggerType> DebuggerTypes => [];
    public IReadOnlyList<WatchExpression> Watches => [];

    public void SetState(DebugState s) { State = s; _state.Fire(s); }
    public ValueTask<IDebugSession?> StartAsync(DebugConfiguration configuration, CancellationToken ct = default) => ValueTask.FromResult<IDebugSession?>(null);
    public ValueTask StopAllAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public IDisposable RegisterAdapterFactory(string debugType, IDebugAdapterFactory factory) => Disposable.Empty;
    public IDisposable RegisterConfigurationProvider(string debugType, IDebugConfigurationProvider provider) => Disposable.Empty;
    public WatchExpression AddWatch(string expression) => new(expression);
    public void RemoveWatch(WatchExpression watch) { }
    public IEventSource<IDebugSession> SessionStarted => _started;
    public IEventSource<IDebugSession> SessionTerminated => _terminated;
    public IEventSource<IDebugSession?> ActiveSessionChanged => _active;
    public IEventSource<DebugState> StateChanged => _state;
    public IEventSource<DebugStopLocation?> StopLocationChanged => _stop;
    public IEventSource<DebugOutputEventArgs> Output => _output;
}

public sealed class FakeDebugConfigurations : IDebugConfigurationService
{
    private readonly Emitter<DebugConfiguration?> _sel = new();
    private readonly Emitter<object?> _changed = new();
    public List<DebugConfiguration> Items { get; } = new();
    private DebugConfiguration? _selected;

    public ValueTask<IReadOnlyList<DebugConfiguration>> GetLaunchConfigurationsAsync(Uri? folder = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<DebugConfiguration>>(Items.ToArray());

    public DebugConfiguration? Selected { get => _selected; set { _selected = value; _sel.Fire(value); } }
    public IEventSource<DebugConfiguration?> SelectedChanged => _sel;
    public IEventSource<object?> ConfigurationsChanged => _changed;
    public ValueTask<DebugConfiguration> ResolveVariablesAsync(DebugConfiguration configuration, CancellationToken ct = default) => ValueTask.FromResult(configuration);
    public ValueTask<Uri> CreateLaunchJsonAsync(string debugType, Uri? folder = null, CancellationToken ct = default) => ValueTask.FromResult(new Uri("file:///launch.json"));
    public void RaiseChanged() => _changed.Fire(null);

    public static DebugConfiguration Make(string name) => DebugConfiguration.FromJson(JsonDocument.Parse($"{{\"type\":\"coreclr\",\"request\":\"launch\",\"name\":\"{name}\"}}").RootElement);
}
