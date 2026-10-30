using System.Collections.Concurrent;
using System.Text;
using CsCode.Core.Tasks;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.Logging;

namespace CsCode.Server;

/// <summary>A server PTY a task runs in, as its task sees it.</summary>
internal interface ITaskPty
{
    /// <summary>Ends the program (SIGHUP to its group, SIGKILL after <paramref name="grace"/>); the exit is reported as terminated.</summary>
    void Kill(TimeSpan grace);
}

/// <summary>
/// T49 — remote tasks in remote terminals, as VS Code runs them. The server's task runner (the Dap module's: tasks.json, variables, dependsOn and the
/// problem matchers run here, next to the files) presents a task through this <see cref="ITaskTerminalHost"/>: it asks the client for a terminal
/// (<c>task/terminal</c>; the client's own task presentation: named after the task, reused, revealed, the command line echoed), and that
/// terminal's <c>term/create</c> comes back tagged, so its PTY — on this machine — is connected to the waiting run: the output feeds the problem
/// matchers, the program's exit is the task's result, cancelling kills it. A connection lost meanwhile changes nothing (the PTY is a resumable
/// channel and the run waits for the program, not for the client). Offered only while the connected client announced
/// <see cref="RemoteProtocol.Capabilities.TaskTerminals"/>; otherwise the runner pipes the output into its "Tasks" output channel.
/// </summary>
internal sealed class ServerTaskTerminals : ITaskTerminalHost
{
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, Run> _unclaimed = new(StringComparer.Ordinal);
    private readonly IRpcEndpoint _link;
    private readonly ILogger _log;
    private volatile bool _clientPresents;

    public ServerTaskTerminals(IRpcEndpoint link, ILogger log)
    {
        _link = link;
        _log = log;
    }

    /// <summary>The connected client shows tasks in terminals.</summary>
    public bool Available => _clientPresents && !_link.IsClosed;

    /// <summary>Tasks waiting for their terminal (bookkeeping, tests).</summary>
    public int Unclaimed => _unclaimed.Count;

    /// <summary>A client connected: what it announced.</summary>
    public void OnHello(IReadOnlyList<string> capabilities) => _clientPresents = capabilities.Contains(RemoteProtocol.Capabilities.TaskTerminals);

    public async ValueTask<ITaskTerminalRun> StartAsync(TaskTerminalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tag = Guid.NewGuid().ToString("N");
        var run = new Run(this, tag, request.Output);
        _unclaimed[tag] = run;
        // Forgotten if no terminal ever claims it (the client failed before its term/create): bounded bookkeeping.
        _ = Task.Delay(ClaimTimeout, CancellationToken.None).ContinueWith(_ => _unclaimed.TryRemove(tag, out var _), TaskScheduler.Default);
        // A start that fails or is cancelled stays known until the timer above: a terminal that still comes for it is ended at once, so a task
        // nobody waits for never runs.
        try
        {
            await _link.InvokeAsync<TaskTerminalParams, Empty>(RemoteMethods.TaskTerminal, new TaskTerminalParams(tag, request.Label, request.FileName, request.Arguments,
                request.WorkingDirectory, request.Environment, request.Presentation, request.CommandLine), ct, ClaimTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            run.Terminate();
            throw;
        }
        catch (Exception ex) when (ex is RemoteDisconnectedException or TimeoutException)
        {
            // The connection went away with the answer. The client goes on (its terminal's term/create may come after the reconnect): the
            // terminal decides, not the lost answer.
            try { await run.TerminalStarted.WaitAsync(ClaimTimeout, ct).ConfigureAwait(false); }
            catch (Exception wait) when (wait is TimeoutException or OperationCanceledException)
            {
                run.Terminate();
                if (wait is OperationCanceledException) throw;
                throw new InvalidOperationException("the connection to the client was lost before its terminal started: " + ex.Message, ex);
            }
        }
        catch (IOException ex) // the client's answer: it could not open the terminal
        {
            run.Terminate();
            throw new InvalidOperationException("the client could not show it: " + ex.Message, ex);
        }
        if (!run.Claimed)
        {
            run.Terminate();
            throw new InvalidOperationException("its terminal did not start on the remote host");
        }
        return run;
    }

    /// <summary>A tagged <c>term/create</c>: the run waiting for this terminal, or null (unknown or stale tag: an ordinary terminal).</summary>
    public Run? Claim(string tag)
    {
        if (!_unclaimed.TryRemove(tag, out var run)) return null;
        run.Claimed = true;
        run.ClaimedSignal.TrySetResult();
        return run;
    }

    /// <summary>One task's process in a server PTY. The PTY's reader reports output and exit in order.</summary>
    internal sealed class Run(ServerTaskTerminals owner, string tag, Action<string>? output) : ITaskTerminalRun
    {
        private readonly TaskCompletionSource<TaskTerminalExit> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
        private char[] _chars = new char[4096];
        private ITaskPty? _pty;
        private volatile bool _terminate;

        public volatile bool Claimed;
        public readonly TaskCompletionSource ClaimedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the task's terminal has started on this server.</summary>
        public Task TerminalStarted => ClaimedSignal.Task;

        public Task<TaskTerminalExit> Completion => _done.Task;

        /// <summary>The PTY was started for this run (on the term/create handler, before any output).</summary>
        public void Attach(ITaskPty pty)
        {
            _pty = pty;
            if (_terminate) pty.Kill(TimeSpan.FromSeconds(1));
        }

        /// <summary>The program's output, decoded (a character split across reads waits for its rest), for the problem matchers.</summary>
        public void OnOutput(ReadOnlySpan<byte> data)
        {
            if (output is null || data.IsEmpty) return;
            var needed = _decoder.GetCharCount(data, flush: false);
            if (_chars.Length < needed) _chars = new char[Math.Max(needed, _chars.Length * 2)];
            var n = _decoder.GetChars(data, _chars, flush: false);
            if (n > 0) output(new string(_chars, 0, n));
        }

        public void OnExited(int exitCode, bool killed) => _done.TrySetResult(new TaskTerminalExit(exitCode, killed || _terminate));

        public void Terminate()
        {
            _terminate = true;
            _pty?.Kill(TimeSpan.FromSeconds(1));
        }

        public ValueTask RevealAsync(CancellationToken ct = default)
        {
            if (!owner._link.Notify(RemoteMethods.TaskReveal, new TaskTagParams(tag)))
                owner._log.LogDebug("Task terminal {Tag} could not be revealed: no client is connected", tag);
            return ValueTask.CompletedTask;
        }
    }
}
