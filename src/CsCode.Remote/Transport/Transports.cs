using System.Text;
using CsCode.Remote.Install;
using CsCode.Remote.Protocol;

namespace CsCode.Remote.Transport;

/// <summary>
/// A started server: a duplex byte stream (its stdout/stdin) plus whatever carries it — normally a process (ssh, or the local server), whose tree is
/// killed on dispose; tests and wrappers (latency/fault injection) can supply plain streams with their own teardown.
/// </summary>
public sealed class RemoteChannel : IAsyncDisposable
{
    private readonly ProcessChannel? _process;
    private readonly Func<TimeSpan, Task>? _stop;
    private readonly Task<int> _exited;
    private int _disposed;

    internal RemoteChannel(ProcessChannel process, string description)
    {
        _process = process;
        Description = description;
        Input = process.Input;
        Output = process.Output;
        _exited = process.Exited;
    }

    /// <param name="stop">Ends the link (given a grace period); called once by <see cref="StopAsync"/>/<see cref="DisposeAsync"/>.</param>
    public RemoteChannel(Stream input, Stream output, string description, Func<TimeSpan, Task> stop, Task<int>? exited = null, int processId = 0)
    {
        Input = input;
        Output = output;
        Description = description;
        _stop = stop;
        _exited = exited ?? new TaskCompletionSource<int>().Task;
        ProcessId = processId;
    }

    /// <summary>The same link seen through other streams (fault injection); stopping the wrapper stops this channel.</summary>
    public RemoteChannel Wrap(Func<Stream, Stream> input, Func<Stream, Stream> output) =>
        new(input(Input), output(Output), Description, StopAsync, _exited, ProcessId);

    public string Description { get; }
    public Stream Input { get; }
    public Stream Output { get; }
    public Task<int> Exited => _exited;
    public int ProcessId { get => _process?.ProcessId ?? field; private init; }
    public string StderrTail => _process?.StderrTail ?? "";
    public Action<string>? StderrLine
    {
        get => _process?.StderrLine;
        set { if (_process is not null) _process.StderrLine = value; }
    }

    /// <summary>Closes stdin (the server exits at EOF), waits <paramref name="grace"/>, then kills the process tree.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_process is not null)
        {
            _process.CloseOutput();
            await _process.StopAsync(grace).ConfigureAwait(false);
        }
        else if (_stop is not null) await _stop(grace).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync(TimeSpan.Zero).ConfigureAwait(false);
}

/// <summary>Byte-stream transport to a <c>cscode-server</c> (ssh, local process; later WSL/Docker). Connecting includes starting (and if needed installing) the server.</summary>
public interface IRemoteTransport
{
    string Description { get; }
    /// <param name="progress">Human-readable steps ("Installing the server…") for the connection notification and the Remote log.</param>
    Task<RemoteChannel> ConnectAsync(IProgress<string>? progress, CancellationToken ct);
}

/// <summary>Starting or bootstrapping the server failed (ssh could not connect, no bundle for the platform, checksum, protocol mismatch…).</summary>
public sealed class RemoteBootstrapException(string message, Exception? inner = null) : IOException(message, inner)
{
    /// <summary>
    /// Retrying cannot fix it (no bundle for the platform, a refused upload, a protocol mismatch): the reconnect loop gives up at once. Set where it
    /// is thrown, never inferred from the message, which carries host names, paths and ssh's own text.
    /// </summary>
    public bool IsPermanent { get; init; }

    /// <summary>The transport never answered within its start timeout (no greeting, no exit): a hung connection rather than a refusal.</summary>
    public bool TimedOut { get; init; }
}

public sealed record ServerLaunchOptions
{
    /// <summary>Remote install root (<c>remote.ssh.serverInstallPath</c>); null/empty = <c>~/.cscode-server</c>.</summary>
    public string? InstallRoot { get; init; }
    /// <summary>The server's data directory (its settings, state and logs); null = <c>&lt;root&gt;/data</c>.</summary>
    public string? DataDir { get; init; }
    /// <summary>Opaque tag passed as <c>--marker</c> (lets tests and the leak checks find exactly the processes they started).</summary>
    public string? Marker { get; init; }
    public ServerBundleSet? Bundles { get; init; }
    /// <summary>Ignore an existing install and upload again (Remote: Reinstall Server).</summary>
    public bool ForceReinstall { get; init; }
    /// <summary>Upper bound for "server greeted us" (covers ssh authentication, which may wait for the user at an askpass prompt).</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan InstallTimeout { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>R4: the persistent server's session id (32 hex digits); null = the server ends with the connection.</summary>
    public string? PersistSession { get; init; }
    /// <summary>R4: seconds the persistent server waits for its client to come back.</summary>
    public int GraceSeconds { get; init; } = 10800;
}

/// <summary>
/// Starts the server through an <see cref="IRemoteShell"/> (ssh, or local sh for tests): runs the fixed start script; if it reports that nothing is
/// installed, decides which bundle fits the remote (<see cref="InstallDecision"/>), streams it over the same kind of channel to the install script
/// (checksum-verified, atomic rename), and starts again. An installed server costs one ssh connection.
/// </summary>
public sealed class ShellServerTransport(IRemoteShell shell, ServerLaunchOptions options) : IRemoteTransport
{
    public IRemoteShell Shell { get; } = shell;
    public ServerLaunchOptions Options { get; } = options;
    public string Description => Shell.Description;

    public async Task<RemoteChannel> ConnectAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var force = Options.ForceReinstall;
        for (var attempt = 0; ; attempt++)
        {
            progress?.Report(attempt == 0 ? $"Starting the server ({Shell.Description})" : "Starting the installed server");
            var hashes = Options.Bundles?.CandidateHashes ?? [];
            var launch = Shell.Launch(ServerScripts.Start, [Options.InstallRoot ?? "", Options.DataDir ?? "", Options.Marker ?? "", force ? "force" : "",
                Options.PersistSession ?? "", Options.PersistSession is null ? "" : Options.GraceSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), .. hashes]);
            var start = await Greeting.StartAsync(launch, Shell.Description, Options.StartTimeout, ct).ConfigureAwait(false);
            if (start.Channel is { } channel) return channel;
            if (start.TimedOut)
                throw new RemoteBootstrapException($"No answer from {Shell.Description} within {Options.StartTimeout.TotalSeconds:0} s: {LastLines(start.Stderr)}") { TimedOut = true };
            if (start.ProbeOutput is null)
                throw new RemoteBootstrapException($"The server did not start ({Shell.Description}, exit code {start.ExitCode}): {LastLines(start.Stderr)}");
            if (attempt > 0)
                throw new RemoteBootstrapException("The server is still not installed after uploading it: " + LastLines(start.ProbeOutput)) { IsPermanent = true };

            var probe = RemoteProbe.Parse(start.ProbeOutput);
            var plan = InstallDecision.Decide(probe, Options.Bundles);
            if (plan.Action != InstallAction.Upload || plan.Bundle is not { } bundle) throw new RemoteBootstrapException(plan.Reason) { IsPermanent = true };
            var size = new FileInfo(bundle.FullPath).Length;
            progress?.Report($"Installing the server on {Shell.Description}: {plan.Reason}, {size / (1024.0 * 1024):0.0} MB");
            ShellResult result;
            await using (var bytes = File.OpenRead(bundle.FullPath))
                result = await Shell.RunAsync(ServerScripts.Install, [Options.InstallRoot ?? "", bundle.Hash, bundle.Sha256, force ? "force" : ""], bytes, Options.InstallTimeout, ct)
                    .ConfigureAwait(false);
            // Permanent only when the install script itself refused the bundle (no checksum tool, wrong checksum, no server in it): uploading again
            // gets the same answer. Anything else — ssh's 255 (the link broke during the upload), the upload killed after the install timeout, a
            // container stopped mid-upload, a full disk — is retried by the reconnect loop like any other failed connection.
            if (result.ExitCode != 0 || !result.Stdout.Contains(ServerScripts.InstalledMarker, StringComparison.Ordinal))
                throw new RemoteBootstrapException($"Installing the server failed (exit code {result.ExitCode}): {LastLines(result.Stderr + "\n" + result.Stdout)}")
                    { IsPermanent = ServerScripts.InstallRefusalExitCodes.Contains(result.ExitCode) };
            force = false;
        }
    }

    internal static string LastLines(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var last = lines.Length == 0 ? "(no output)" : string.Join(" | ", lines.TakeLast(6));
        // The self-contained server needs the host's OpenSSL (as every .NET program on Linux): say what to install.
        return text.Contains("libssl", StringComparison.Ordinal)
            ? last + " — the remote host needs OpenSSL's libssl (Debian/Ubuntu: libssl3, Fedora/RHEL: openssl-libs, Alpine: libssl3)"
            : last;
    }
}

/// <summary>
/// Runs <c>cscode-server --stdio</c> on this machine as a child process (below-normal priority): the loopback transport of the tests, and the
/// out-of-process language host (<c>cscode.languageHost = process</c>).
/// </summary>
public sealed class LoopbackTransport(ProcessLaunch launch) : IRemoteTransport
{
    public ProcessLaunch Launch { get; } = launch;
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromMinutes(1);
    public string Description => "local server process";

    public async Task<RemoteChannel> ConnectAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Starting the local server process");
        var start = await Greeting.StartAsync(Launch, Description, StartTimeout, ct).ConfigureAwait(false);
        return start.Channel ?? throw new RemoteBootstrapException($"The local server did not start (exit code {start.ExitCode}): {start.Stderr}");
    }
}

/// <summary>Starts a process and reads its stdout up to the server greeting (or the start script's not-installed report).</summary>
internal static class Greeting
{
    public sealed record Outcome(RemoteChannel? Channel, string? ProbeOutput, int ExitCode, string Stderr, bool TimedOut = false);

    /// <summary>After the start timeout killed the process: how long the reading thread gets to see the end of its output before it is left behind.</summary>
    internal static readonly TimeSpan GiveUpGrace = TimeSpan.FromSeconds(5);

    public static async Task<Outcome> StartAsync(ProcessLaunch launch, string description, TimeSpan timeout, CancellationToken ct)
    {
        // Killing the process normally ends the read below (EOF). Not when another process holds the pipe's other end: an ssh session multiplexed over
        // a ControlMaster hands its stdin/stdout to the master, so a stale master keeps the read blocked after its client is killed (found against a
        // real host: the reconnect loop waited for ever). Then the attempt is given up as timed out without that thread, which ends with the master.
        var gaveUp = new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = BlockingWork.RunAsync("start", () =>
        {
            var process = ProcessChannel.Start(launch);
            var ok = false;
            var timedOut = 0;
            using var killer = ct.Register(() => _ = process.DisposeAsync());
            using var timer = new Timer(_ =>
            {
                Volatile.Write(ref timedOut, 1);
                _ = process.DisposeAsync();
                _ = Task.Delay(GiveUpGrace).ContinueWith(_ => gaveUp.TrySetResult(new Outcome(null, null, -1, process.StderrTail, TimedOut: true)), TaskScheduler.Default);
            }, null, timeout, Timeout.InfiniteTimeSpan);
            try
            {
                var junk = new StringBuilder();
                var line = new List<byte>(128);
                var total = 0;
                while (true)
                {
                    int b;
                    try { b = process.Input.ReadByte(); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { b = -1; }
                    if (b < 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        var code = process.Exited.Wait(TimeSpan.FromSeconds(5)) ? process.Exited.Result : -1;
                        return new Outcome(null, null, code, process.StderrTail + (junk.Length > 0 ? "\n" + junk : ""), Volatile.Read(ref timedOut) == 1);
                    }
                    if (++total > 1024 * 1024) throw new RemoteBootstrapException($"{description}: no server greeting in the first megabyte of output");
                    if (b != '\n') { line.Add((byte)b); continue; }
                    var text = Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                    line.Clear();
                    if (text == RemoteProtocol.Greeting)
                    {
                        ok = true;
                        return new Outcome(new RemoteChannel(process, description), null, 0, "");
                    }
                    if (text == ServerScripts.NotInstalledMarker)
                    {
                        var rest = new StreamReader(process.Input, Encoding.UTF8).ReadToEnd();
                        var code = process.Exited.Wait(TimeSpan.FromSeconds(10)) ? process.Exited.Result : -1;
                        return new Outcome(null, rest, code, process.StderrTail);
                    }
                    if (junk.Length < 4096) junk.AppendLine(text); // banner / motd printed by the login shell
                }
            }
            finally
            {
                if (!ok) process.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(6));
            }
        });
        return await (await Task.WhenAny(work, gaveUp.Task).ConfigureAwait(false)).ConfigureAwait(false);
    }
}
