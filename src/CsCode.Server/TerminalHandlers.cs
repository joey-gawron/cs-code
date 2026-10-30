using System.Collections.Concurrent;
using CsCode.Core.Platform;
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
/// (SIGHUP to the process group, SIGKILL after the grace period).
/// </summary>
internal sealed class TerminalHandlers(IServiceProvider sp, ChannelHub hub, ILogger log)
{
    private readonly ConcurrentDictionary<long, PtySource> _terminals = new();
    private readonly IConfigurationService? _config = sp.GetService<IConfigurationService>();
    private readonly TerminalLaunchResolver _resolver = new(sp.GetService<IConfigurationService>(), sp.GetService<IWorkspaceService>());

    public int Count => _terminals.Count(t => !t.Value.Channel.IsClosed);

    public void Register(IRpcEndpoint link)
    {
        link.OnRequest<TermCreateParams, TermCreateResult>(RemoteMethods.TermCreate, (p, _) => ValueTask.FromResult(Create(p)));
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
        // The client's folder, unless terminal.integrated.cwd says otherwise (the resolver's order): this server may not have applied the client's
        // newest workspace folders yet when the terminal is created right after opening one.
        if (options.Cwd is null && p.Folder is { IsFile: true } folder && string.IsNullOrWhiteSpace(_config?.Get<string>(TerminalSettingKeys.Cwd)))
            options = options with { Cwd = folder.LocalPath.Length > 1 ? folder.LocalPath.TrimEnd('/') : folder.LocalPath };
        var info = _resolver.Resolve(options, columns, rows); // PtySpawnException → an error answer with the reason
        var channel = hub.Create("terminal");
        var source = new PtySource(channel, PtyHosts.Create(), () => _terminals.TryRemove(channel.Id, out _));
        channel.Source = source;
        _terminals[channel.Id] = source;
        try { source.Host.Start(info, source); }
        catch (PtySpawnException)
        {
            hub.Remove(channel.Id);
            throw;
        }
        log.LogInformation("Terminal {Id}: {Program} (pid {Pid}) in {Cwd}", channel.Id, info.FileName, source.Host.ProcessId, info.WorkingDirectory);
        return new TermCreateResult(channel.Id, source.Host.ProcessId, info.FileName, info.WorkingDirectory);
    }

    /// <summary>One PTY as a channel source: output → channel (blocking there throttles the program), client input → the PTY's bounded writer.</summary>
    private sealed class PtySource(ServerChannel channel, IPtyHost host, Action removed) : IChannelSource, IPtyListener
    {
        private int _disposed;

        public ServerChannel Channel { get; } = channel;
        public IPtyHost Host { get; } = host;

        public void OnOutput(ReadOnlySpan<byte> data) => Channel.Write(data);

        public void OnExited(int exitCode) => Channel.Complete(exitCode, null);

        public bool Accept(ReadOnlySpan<byte> data) => Host.Write(data);

        public void Close(TimeSpan grace) => Host.Kill(grace);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            removed();
            Host.Dispose(); // SIGHUP to the program's group, SIGKILL to its session shortly after

        }
    }
}
