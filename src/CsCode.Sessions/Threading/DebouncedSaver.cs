namespace CsCode.Sessions;

/// <summary>
/// Autosave with a bounded delay: the first change after a save arms one timer, later changes do not re-arm it, so a session is written at most
/// <c>delay</c> after a change even while the user keeps typing (budget: ≤ 2 s). <see cref="MarkDirty"/> is what the UI thread calls on every
/// change: a volatile write and, at most once per save, a timer arm. Saves never overlap; a change made during a save arms the next one.
/// </summary>
public sealed class DebouncedSaver : IDisposable
{
    private readonly Func<CancellationToken, Task> _save;
    private readonly TimeSpan _delay;
    private readonly ITimer _timer;
    private readonly SemaphoreSlim _saving = new(1, 1);
    private readonly CancellationTokenSource _disposed = new();
    private int _dirty, _armed, _saves, _failures;

    public DebouncedSaver(TimeProvider time, TimeSpan delay, Func<CancellationToken, Task> save)
    {
        _save = save;
        _delay = delay;
        _timer = time.CreateTimer(static s => _ = ((DebouncedSaver)s!).OnTimerAsync(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool IsDirty => Volatile.Read(ref _dirty) != 0;
    public int SaveCount => Volatile.Read(ref _saves);
    public int FailureCount => Volatile.Read(ref _failures);

    /// <summary>The last save's failure (null after a successful save).</summary>
    public Exception? LastError { get; private set; }

    public void MarkDirty()
    {
        Volatile.Write(ref _dirty, 1);
        if (Interlocked.Exchange(ref _armed, 1) == 0 && !_disposed.IsCancellationRequested) _timer.Change(_delay, Timeout.InfiniteTimeSpan);
    }

    private async Task OnTimerAsync()
    {
        Volatile.Write(ref _armed, 0);
        try { await SaveAsync(onlyIfDirty: true).ConfigureAwait(false); }
        catch (Exception) { /* recorded in LastError; the next change retries */ }
    }

    /// <summary>Saves now (hide, quit, crash path): waits for an in-flight save, then saves whatever is pending (or always, with <paramref name="onlyIfDirty"/> false).</summary>
    public async Task SaveAsync(bool onlyIfDirty = false)
    {
        if (_disposed.IsCancellationRequested) return;
        await _saving.WaitAsync(_disposed.Token).ConfigureAwait(false);
        try
        {
            if (onlyIfDirty && Volatile.Read(ref _dirty) == 0) return;
            Volatile.Write(ref _dirty, 0);
            try
            {
                await _save(_disposed.Token).ConfigureAwait(false);
                LastError = null;
                Interlocked.Increment(ref _saves);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastError = ex;
                Interlocked.Increment(ref _failures);
                Volatile.Write(ref _dirty, 1); // retried on the next change or flush
                throw;
            }
        }
        finally { _saving.Release(); }
    }

    public void Dispose()
    {
        if (_disposed.IsCancellationRequested) return;
        _disposed.Cancel();
        _timer.Dispose();
    }
}
