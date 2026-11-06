using System.Collections.Concurrent;
using CsCode.Core.Platform;
using CsCode.Core.Terminal;
using CsCode.Remote.Protocol;
using CsCode.Terminal.Pty;
using CsCode.Terminal.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CsCode.Server;

/// <summary>
/// R3 remote terminals: <c>term/create</c> resolves the profile, shell, environment and working directory HERE (the Terminal module's resolver with this
/// server's settings — the client's user settings, mirrored — and workspace) and runs the program on a local PTY; its output and input travel as a
/// channel (<see cref="ServerChannel"/>: offsets, acknowledgements, replay after a reconnect). Kill/exit/dispose follow the PTY host's rules
/// (SIGHUP to the process group, SIGKILL after the grace period). A terminal the client opened for a task of the server's task runner
/// (<see cref="ServerTaskTerminals"/>) is tagged: its PTY's output and exit also go to that task.
/// </summary>
internal sealed class TerminalHandlers(IServiceProvider sp, ChannelHub hub, ILogger log, ServerTaskTerminals? tasks = null, NixDevelopEnvironment? nix = null)
{
    private readonly ConcurrentDictionary<long, PtySource> _terminals = new();
    private readonly object _taskStarts = new(); // a task's term/create and its repetition never run at once
    private readonly IConfigurationService? _config = sp.GetService<IConfigurationService>();
    private readonly TerminalLaunchResolver _resolver = new(sp.GetService<IConfigurationService>(), sp.GetService<IWorkspaceService>());

    public int Count => _terminals.Count(t => !t.Value.Channel.IsClosed);

    public void Register(IRpcEndpoint link)
    {
        link.OnRequest<TermCreateParams, TermCreateResult>(RemoteMethods.TermCreate, async (p, ct) =>
        {
            // The terminal may arrive before the client's workspace/setFolders does: ask for the folder's nix develop environment here too (a no-op when
            // it is resolved or being resolved), and wait for it (and the login shell's) as the local terminal service does before it forks a program.
            if (p.Folder is { IsFile: true } folder) nix?.Begin(folder.LocalPath);
            await LoginShellEnvironment.WaitAsync(ct).ConfigureAwait(false);
            return Create(p);
        });
        link.OnNotification<TermResizeParams>(RemoteMethods.TermResize, p =>
        {
            if (_terminals.TryGetValue(p.Id, out var t)) t.Host.Resize(Math.Clamp(p.Columns, 1, 4096), Math.Clamp(p.Rows, 1, 4096));
        }, inline: true);
        link.OnRequest<ChanIdParams, TermInfoResult>(RemoteMethods.TermInfo, (p, _) =>
            ValueTask.FromResult(new TermInfoResult(_terminals.TryGetValue(p.Id, out var t) && !t.Channel.IsClosed ? t.Host.GetForegroundProcessName() : null)));
    }

    private TermCreateResult Create(TermCreateParams p)
    {
        if (!PtyHosts.IsSupported) throw new NotSupportedException("This remote host cannot run terminals (no pseudo-terminal support).");
        var columns = Math.Clamp(p.Columns, 1, 4096);
        var rows = Math.Clamp(p.Rows, 1, 4096);
        var options = p.Options;
        // A task's terminal: the tag names the task start waiting for it (never part of the program's environment).
        string? taskTag = null;
        if (options.Env is { } requested && requested.TryGetValue(TaskTerminalTag.Variable, out var tag))
        {
            taskTag = tag;
            options = options with { Env = requested.Where(e => e.Key != TaskTerminalTag.Variable).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal) };
        }
        // The client's folder, unless terminal.integrated.cwd says otherwise (the resolver's order): this server may not have applied the client's
        // newest workspace folders yet when the terminal is created right after opening one.
        if (options.Cwd is null && p.Folder is { IsFile: true } folder && string.IsNullOrWhiteSpace(_config?.Get<string>(TerminalSettingKeys.Cwd)))
            options = options with { Cwd = folder.LocalPath.Length > 1 ? folder.LocalPath.TrimEnd('/') : folder.LocalPath };
        if (taskTag is null) return Start(options, columns, rows, null, null);
        lock (_taskStarts)
        {
            // The client lost the answer to its term/create for this task with the connection and asks again: the PTY started then is the task's.
            if (_terminals.Values.FirstOrDefault(t => t.Channel.TaskTag == taskTag) is { Answer: { } answer })
            {
                log.LogInformation("Terminal {Id}: the task terminal's start was asked for again (its answer was lost with the connection)", answer.Channel);
                return answer;
            }
            var task = tasks?.Claim(taskTag)
                       ?? throw new IOException("The task this terminal was opened for is no longer waiting for it on the remote host (it started elsewhere, failed or was cancelled).");
            return Start(options, columns, rows, task, taskTag);
        }
    }

    private TermCreateResult Start(TerminalCreateOptions options, int columns, int rows, ServerTaskTerminals.Run? task, string? taskTag)
    {
        PtyStartInfo info;
        try { info = _resolver.Resolve(options, columns, rows); } // PtySpawnException → an error answer with the reason
        catch (PtySpawnException)
        {
            task?.OnExited(-1, killed: false);
            throw;
        }
        var channel = hub.Create("terminal");
        channel.TaskTag = taskTag;
        var source = new PtySource(channel, PtyHosts.Create(), () => _terminals.TryRemove(channel.Id, out _)) { Task = task };
        channel.Source = source;
        _terminals[channel.Id] = source;
        try { source.Host.Start(info, source); }
        catch (PtySpawnException)
        {
            hub.Remove(channel.Id);
            task?.OnExited(-1, killed: false);
            throw;
        }
        task?.Attach(source);
        log.LogInformation("Terminal {Id}: {Program} (pid {Pid}) in {Cwd}{Task}", channel.Id, info.FileName, source.Host.ProcessId, info.WorkingDirectory,
            task is null ? "" : " (a task's)");
        return source.Answer = new TermCreateResult(channel.Id, source.Host.ProcessId, info.FileName, info.WorkingDirectory, ShellIntegrationOf(info));
    }

    /// <summary>
    /// The shell-integration variables the resolver gave the shell (injected with this server's own scripts, shipped next to it in the bundle):
    /// the client's terminal needs the nonce to trust the command lines the shell reports. The rest of the environment stays here.
    /// </summary>
    private static Dictionary<string, string>? ShellIntegrationOf(PtyStartInfo info)
    {
        if (!info.Environment.TryGetValue(ShellIntegrationInjector.InjectionVariable, out var injected)) return null;
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { [ShellIntegrationInjector.InjectionVariable] = injected };
        if (info.Environment.TryGetValue(ShellIntegrationInjector.NonceVariable, out var nonce)) env[ShellIntegrationInjector.NonceVariable] = nonce;
        return env;
    }

    /// <summary>
    /// One PTY as a channel source: output → channel (blocking there throttles the program), client input → the PTY's bounded writer. A task's PTY
    /// also reports its output (first: the problem matchers never wait for the client) and its exit to the task.
    /// </summary>
    private sealed class PtySource(ServerChannel channel, IPtyHost host, Action removed) : IChannelSource, IPtyListener, ITaskPty
    {
        private int _disposed;
        private volatile bool _killed;

        public ServerChannel Channel { get; } = channel;
        public IPtyHost Host { get; } = host;
        public ServerTaskTerminals.Run? Task { get; init; }

        /// <summary>What term/create answered (a task's terminal is answered with it again when the client lost it).</summary>
        public TermCreateResult? Answer { get; set; }

        public void OnOutput(ReadOnlySpan<byte> data)
        {
            Task?.OnOutput(data);
            Channel.Write(data);
        }

        public void OnExited(int exitCode)
        {
            Channel.Complete(exitCode, null);
            Task?.OnExited(exitCode, _killed);
        }

        public bool Accept(ReadOnlySpan<byte> data) => Host.Write(data);

        /// <summary>The client ended it (closed the terminal): a task in it counts as terminated.</summary>
        public void Close(TimeSpan grace) => Kill(grace);

        public void Kill(TimeSpan grace)
        {
            _killed = true;
            Host.Kill(grace);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            removed();
            Host.Dispose(); // SIGHUP to the program's group, SIGKILL to its session shortly after
            Task?.OnExited(-1, killed: true); // no-op when the exit was reported
        }
    }
}
