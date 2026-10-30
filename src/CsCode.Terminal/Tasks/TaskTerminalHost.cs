using System.Text;
using CsCode.Core.Lifecycle;
using CsCode.Core.Tasks;
using CsCode.Core.Terminal;
using CsCode.Terminal.Sessions;

namespace CsCode.Terminal.Tasks;

/// <summary>
/// <see cref="ITaskTerminalHost"/>: tasks run in terminals named after them, with VS Code's <c>presentation</c> rules. A task terminal is
/// transient (no confirmation, never restored); after its task it shows "Terminal will be reused by tasks, press any key to close it" and is
/// reused by the next task: panel <c>shared</c> takes any idle shared task terminal, <c>dedicated</c> only the one this task ran in, <c>new</c>
/// never reuses; tasks with the same <c>group</c> split into one terminal group. <c>reveal</c> always shows the terminal (taking the keyboard
/// only with <c>focus</c>), silent and never leave it hidden (the task runner reveals a failed silent task); <c>clear</c> empties a reused
/// terminal first; <c>close</c> closes it when the task ends. The task's output reaches <see cref="TaskTerminalRequest.Output"/> from the PTY's
/// reader thread, decoded, before the emulator draws it.
/// </summary>
public sealed class TaskTerminalHost : ITaskTerminalHost, IDisposable
{
    private readonly TerminalService _terminals;
    private readonly object _gate = new();
    private readonly List<Slot> _slots = new();
    private readonly IDisposable _removed;
    private bool _disposed;

    /// <summary>A terminal that tasks run in, and the run it is busy with.</summary>
    internal sealed class Slot(TerminalSession session)
    {
        public readonly TerminalSession Session = session;
        public string Label = "";
        public TaskPanelKind Panel;
        public string? Group;
        public Run? Current;
    }

    public TaskTerminalHost(TerminalService terminals)
    {
        _terminals = terminals;
        _removed = terminals.Removed.Subscribe(OnRemoved);
    }

    /// <summary>The task runs whose process is still running (Terminate Task offers them).</summary>
    public IReadOnlyList<Run> Running
    {
        get { lock (_gate) return _slots.Select(s => s.Current).OfType<Run>().ToList(); }
    }

    public async ValueTask<ITaskTerminalRun> StartAsync(TaskTerminalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var p = request.Presentation;
        var run = new Run(this, request);
        Slot? reuse = null;
        TerminalSession? splitFrom = null;
        lock (_gate)
        {
            _slots.RemoveAll(s => s.Session.IsDisposed);
            if (p.Panel != TaskPanelKind.New)
                reuse = _slots.LastOrDefault(s => s.Current is null && s.Session.HasExited && s.Panel == p.Panel && s.Group == p.Group
                    && (p.Panel == TaskPanelKind.Shared || s.Label == request.Label));
            if (reuse is not null) Claim(reuse, request, run);
            else if (p.Group is { } group) splitFrom = _slots.LastOrDefault(s => s.Group == group && !s.Session.IsDisposed)?.Session;
        }

        var options = new TerminalCreateOptions
        {
            Name = request.Label,
            ShellPath = request.FileName,
            ShellArgs = request.Arguments,
            Cwd = request.WorkingDirectory,
            Env = request.Environment,
            IsTransient = true,
            Show = false,
        };
        try
        {
            if (reuse is not null)
            {
                var session = reuse.Session;
                session.SetName(request.Label);
                // what the last task left: an emptied buffer (clear), else a blank line between the runs
                session.WriteLocal(p.Clear ? "\e[0m\e[H\e[2J\e[3J" : "\e[0m\r\n");
                Prepare(session, run);
                if (!await _terminals.RestartAsync(session, options, run.Attach, ct).ConfigureAwait(false)) run.Complete(-1);
            }
            else
            {
                await _terminals.CreateSessionAsync(options, splitFrom, session =>
                {
                    lock (_gate)
                    {
                        var slot = new Slot(session);
                        Claim(slot, request, run);
                        _slots.Add(slot);
                    }
                    Prepare(session, run);
                    run.Attach(session);
                }, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            run.Complete(-1, terminated: true);
            throw;
        }
        if (p.Reveal == TaskRevealKind.Always && run.Session is { } shown)
            await _terminals.ShowAsync(shown, preserveFocus: !p.Focus, ct).ConfigureAwait(false);
        return run;
    }

    private static void Claim(Slot slot, TaskTerminalRequest request, Run run)
    {
        slot.Label = request.Label;
        slot.Panel = request.Presentation.Panel;
        slot.Group = request.Presentation.Group;
        slot.Current = run;
        run.Slot = slot;
    }

    /// <summary>The command line echoed above the output and VS Code's lines below it when the task ends.</summary>
    private static void Prepare(TerminalSession session, Run run)
    {
        var request = run.Request;
        var p = request.Presentation;
        session.CloseOnInputAfterExit = false;
        if (p.Echo && !string.IsNullOrEmpty(request.CommandLine)) session.WriteLocal($"\e[0m *  Executing task: {request.CommandLine} \r\n\r\n");
        var program = string.Join(", ", new[] { request.FileName }.Concat(request.Arguments).Select((a, i) => i == 0 ? a : $"'{a}'"));
        session.ExitMessage = code =>
        {
            var text = new StringBuilder("\e[0m\r\n");
            if (code != 0 && !run.TerminatedByRequest) text.Append($" *  The terminal process \"{program}\" terminated with exit code: {code}. \r\n");
            if (p.ShowReuseMessage && !p.Close) text.Append(" *  Terminal will be reused by tasks, press any key to close it. \r\n");
            return text.ToString();
        };
    }

    private void Finished(Run run)
    {
        Slot? slot;
        lock (_gate)
        {
            slot = run.Slot;
            if (slot is not null && ReferenceEquals(slot.Current, run)) slot.Current = null;
        }
        if (slot is null || slot.Session.IsDisposed) return;
        var p = run.Request.Presentation;
        var session = slot.Session;
        // (this runs on the terminal's own worker, inside its exit event: close it from elsewhere)
        if (p.Close) _ = Task.Run(async () => await _terminals.KillAsync(session).ConfigureAwait(false));
        else session.CloseOnInputAfterExit = p.ShowReuseMessage;
    }

    /// <summary>A task terminal was closed (killed by the user, or by "press any key"): its running task ends as terminated.</summary>
    private void OnRemoved(ITerminal terminal)
    {
        Run? running = null;
        lock (_gate)
        {
            var slot = _slots.FirstOrDefault(s => ReferenceEquals(s.Session, terminal));
            if (slot is null) return;
            running = slot.Current;
            slot.Current = null;
            _slots.Remove(slot);
        }
        running?.Complete(-1, terminated: true);
    }

    public void Dispose()
    {
        List<Run> running;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            running = _slots.Select(s => s.Current).OfType<Run>().ToList();
            _slots.Clear();
        }
        _removed.Dispose();
        foreach (var r in running) r.Terminate();
    }

    /// <summary>One task run: its terminal, the output tap and the completion.</summary>
    public sealed class Run : ITaskTerminalRun
    {
        private readonly TaskTerminalHost _host;
        private readonly TaskCompletionSource<TaskTerminalExit> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly object _decodeGate = new();
        private char[] _chars = new char[4096];
        private IDisposable? _exitSub;
        private volatile bool _terminated;

        internal Run(TaskTerminalHost host, TaskTerminalRequest request)
        {
            _host = host;
            Request = request;
        }

        public TaskTerminalRequest Request { get; }
        internal Slot? Slot { get; set; }
        public TerminalSession? Session { get; private set; }
        internal bool TerminatedByRequest => _terminated;
        public Task<TaskTerminalExit> Completion => _completion.Task;

        /// <summary>Before the program starts: its output goes to the request's sink, its exit completes the run.</summary>
        internal void Attach(TerminalSession session)
        {
            Session = session;
            if (Request.Output is not null) session.OutputTap = OnOutput;
            _exitSub = session.Exited.Subscribe(e => Complete(e.ExitCode ?? -1));
        }

        private void OnOutput(ReadOnlySpan<byte> data)
        {
            string text;
            lock (_decodeGate)
            {
                var needed = _decoder.GetCharCount(data, flush: false);
                if (needed == 0) return;
                if (_chars.Length < needed) _chars = new char[Math.Max(needed, _chars.Length * 2)];
                var n = _decoder.GetChars(data, _chars, flush: false);
                text = new string(_chars, 0, n);
            }
            try { Request.Output?.Invoke(text); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { } // the receiver went away
        }

        internal void Complete(int exitCode, bool terminated = false)
        {
            if (!_completion.TrySetResult(new TaskTerminalExit(exitCode, terminated || _terminated))) return;
            _exitSub?.Dispose();
            if (Session is { } s && s.OutputTap == OnOutput) s.OutputTap = null;
            _host.Finished(this);
        }

        public void Terminate()
        {
            if (_completion.Task.IsCompleted) return;
            _terminated = true;
            if (Session is { IsDisposed: false } s) s.KillProcess(TimeSpan.FromSeconds(1));
            else Complete(-1, terminated: true);
        }

        public async ValueTask RevealAsync(CancellationToken ct = default)
        {
            if (Session is { IsDisposed: false } s) await _host._terminals.ShowAsync(s, preserveFocus: true, ct).ConfigureAwait(false);
        }

        public override string ToString() => Request.Label;
    }
}
