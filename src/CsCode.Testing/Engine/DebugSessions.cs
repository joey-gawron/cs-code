using System.Text;
using CsCode.Core.Debugging;

namespace CsCode.Testing.Engine;

/// <summary>Waiting for a debug session of a test run, and reading the program's output from it.</summary>
public static class DebugSessions
{
    /// <summary>
    /// Feeds the debuggee's output (<c>stdout</c>/<c>stderr</c>/<c>console</c> events) to <paramref name="onLine"/> line by line until the session
    /// ends; returns its exit code (null when the adapter reported none). Cancelling stops the session.
    /// </summary>
    public static async Task<int?> PumpOutputAsync(IDebugSession session, Action<string> onLine, CancellationToken ct)
    {
        var buffer = new StringBuilder();
        var gate = new object();
        using var output = session.Output.Subscribe(e =>
        {
            if (e.Category is not ("stdout" or "stderr" or "console" or "")) return;
            lock (gate)
            {
                buffer.Append(e.Output);
                while (true)
                {
                    var text = buffer.ToString();
                    var nl = text.IndexOf('\n');
                    if (nl < 0) break;
                    onLine(text[..nl].TrimEnd('\r'));
                    buffer.Remove(0, nl + 1);
                }
            }
        });
        var exit = await WaitForEndAsync(session, ct).ConfigureAwait(false);
        lock (gate) if (buffer.Length > 0) onLine(buffer.ToString().TrimEnd('\r', '\n'));
        return exit;
    }

    /// <summary>Completes when the session terminates (or is already over); returns the exit code it reported.</summary>
    public static async Task<int?> WaitForEndAsync(IDebugSession session, CancellationToken ct)
    {
        var done = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var terminated = session.Terminated.Subscribe(e => done.TrySetResult(e.ExitCode));
        using var state = session.StateChanged.Subscribe(s => { if (s == DebugSessionState.Terminated) done.TrySetResult(null); });
        if (session.State == DebugSessionState.Terminated) done.TrySetResult(null);
        try
        {
            return await done.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { await session.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            throw;
        }
    }
}
