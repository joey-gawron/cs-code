using System.Diagnostics;

namespace CsCode.Ui.Threading;

/// <summary>
/// Detects a UI thread that has not pumped for longer than <c>thresholdMs</c> and logs what it was doing.
/// A managed thread cannot capture another thread's stack in-process, so the report names the dispatcher callback that was
/// executing when the stall began (every <see cref="IUiDispatcher"/> callback is tracked) plus the stall length.
/// Enabled in Debug builds and via <c>cscode.diagnostics.uiStallLogMs</c> (0 = off). Costs one post per poll interval.
/// </summary>
public sealed class UiStallWatchdog : IDisposable
{
    private readonly IUiDispatcher _ui;
    private readonly Action<string> _log;
    private readonly int _thresholdMs;
    private readonly int _pollMs;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private long _lastAckTicks = Stopwatch.GetTimestamp();
    private int _outstanding;

    public UiStallWatchdog(IUiDispatcher ui, Action<string> log, int thresholdMs = 100, int pollMs = 20)
    {
        _ui = ui;
        _log = log;
        _thresholdMs = thresholdMs;
        _pollMs = pollMs;
        _thread = new Thread(Loop) { IsBackground = true, Name = "cscode-ui-watchdog", Priority = ThreadPriority.BelowNormal };
    }

    public void Start() => _thread.Start();

    private void Loop()
    {
        var stalled = false;
        var stallStart = 0L;
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            if (Interlocked.Exchange(ref _outstanding, 1) == 0)
                _ui.Post(Ack, UiPriority.Input);

            if (token.WaitHandle.WaitOne(_pollMs)) break;

            var sinceAckMs = (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastAckTicks)) * 1000.0 / Stopwatch.Frequency;
            if (!stalled && sinceAckMs > _thresholdMs && Volatile.Read(ref _outstanding) == 1)
            {
                stalled = true;
                stallStart = Volatile.Read(ref _lastAckTicks);
                _ui.Stats.OnStall();
                _log($"UI thread has not pumped for {sinceAckMs:F0} ms; in flight: {_ui.CurrentCallback ?? "(not in an instrumented callback)"}");
            }
            else if (stalled && sinceAckMs < _thresholdMs)
            {
                stalled = false;
                var total = (Stopwatch.GetTimestamp() - stallStart) * 1000.0 / Stopwatch.Frequency;
                _log($"UI thread recovered after about {total:F0} ms");
            }
        }
    }

    private void Ack()
    {
        Volatile.Write(ref _lastAckTicks, Stopwatch.GetTimestamp());
        Volatile.Write(ref _outstanding, 0);
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_thread.IsAlive) _thread.Join(500);
        _cts.Dispose();
    }
}
