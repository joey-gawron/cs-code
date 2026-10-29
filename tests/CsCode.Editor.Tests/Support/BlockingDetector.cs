using System.Runtime.CompilerServices;

namespace CsCode.Editor.Tests.Support;

/// <summary>
/// The "no sync-over-async on the UI thread" assertion: a <see cref="SynchronizationContext"/> wrapper that asks the runtime to notify it
/// whenever the thread it is installed on performs a managed blocking wait (<c>Task.Wait</c>, <c>.Result</c>, <c>WaitHandle.WaitOne</c>,
/// <c>Thread.Join</c>, a contended monitor…). Posting/sending are forwarded to the wrapped context, so the dispatcher keeps working.
/// Every wait is recorded with the stack of whoever blocked, so a violation names the offending code.
/// </summary>
public sealed class BlockingDetector : SynchronizationContext, IDisposable
{
    private readonly SynchronizationContext? _inner;
    private readonly List<string> _waits = new();
    private readonly SynchronizationContext? _previous;
    private bool _enabled = true;

    public BlockingDetector()
    {
        _previous = Current;
        _inner = _previous;
        SetWaitNotificationRequired();
        SetSynchronizationContext(this);
    }

    public IReadOnlyList<string> Waits { get { lock (_waits) return _waits.ToArray(); } }
    public int Count { get { lock (_waits) return _waits.Count; } }

    /// <summary>Suspends recording (the test itself legitimately blocks in places).</summary>
    public IDisposable Pause()
    {
        _enabled = false;
        return new Resume(this);
    }

    private sealed class Resume(BlockingDetector owner) : IDisposable
    {
        public void Dispose() => owner._enabled = true;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (_inner is not null) _inner.Post(d, state);
        else base.Post(d, state);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (_inner is not null) _inner.Send(d, state);
        else base.Send(d, state);
    }

    public override SynchronizationContext CreateCopy() => this;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
    {
        if (_enabled && millisecondsTimeout != 0) lock (_waits) _waits.Add(new System.Diagnostics.StackTrace(1, true).ToString());
        return base.Wait(waitHandles, waitAll, millisecondsTimeout);
    }

    public void Dispose() => SetSynchronizationContext(_previous);
}
