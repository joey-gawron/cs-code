using System.Text;
using CsCode.Remote.Protocol;
using CsCode.Remote.Ssh;

namespace CsCode.Remote.Transport;

/// <summary>Where the fixed server scripts run: the remote host through ssh, or this machine's <c>/bin/sh</c> (tests, local isolation).</summary>
public interface IRemoteShell
{
    /// <summary>For logs and progress ("ssh me@box", "local sh").</summary>
    string Description { get; }

    /// <summary>The process that runs <c>sh -c &lt;script&gt; cscode &lt;args…&gt;</c> on the other side, its stdin/stdout connected to ours.</summary>
    ProcessLaunch Launch(string script, IReadOnlyList<string> args);
}

/// <summary>Runs scripts through the system ssh client (one ssh process per script; the remote command is <see cref="PosixShell.ScriptCommand"/>).</summary>
public sealed class SshRemoteShell(SshOptions options, RemoteAuthority destination) : IRemoteShell
{
    public SshOptions Options { get; } = options;
    public RemoteAuthority Destination { get; } = destination;
    public string Description => "ssh " + Destination.Destination;

    public ProcessLaunch Launch(string script, IReadOnlyList<string> args) =>
        new(Options.SshPath, SshArgs.Command(Options, Destination, PosixShell.ScriptCommand(script, args))) { Environment = Options.Environment };
}

/// <summary>Runs scripts with this machine's <c>/bin/sh</c> (argument vector, no quoting involved).</summary>
public sealed class LocalRemoteShell(string shell = "/bin/sh", IReadOnlyDictionary<string, string?>? environment = null) : IRemoteShell
{
    public string Description => "local " + shell;

    public ProcessLaunch Launch(string script, IReadOnlyList<string> args) =>
        new(shell, ["-c", script, "cscode", .. args]) { Environment = environment };
}

public sealed record ShellResult(int ExitCode, string Stdout, string Stderr);

public static class RemoteShellExtensions
{
    /// <summary>
    /// Runs a script to completion on a dedicated thread: <paramref name="stdin"/> is copied in (then closed), stdout/stderr are collected (bounded
    /// to 1 MB each). Cancellation or the timeout kills the process tree.
    /// </summary>
    public static Task<ShellResult> RunAsync(this IRemoteShell shell, string script, IReadOnlyList<string> args, Stream? stdin, TimeSpan timeout, CancellationToken ct) =>
        BlockingWork.RunAsync("shell", () =>
        {
            var channel = ProcessChannel.Start(shell.Launch(script, args));
            using var killer = ct.Register(() => _ = channel.DisposeAsync());
            using var timer = new Timer(_ => _ = channel.DisposeAsync(), null, timeout, Timeout.InfiniteTimeSpan);
            var stdout = new StringBuilder();
            var reader = new Thread(() =>
            {
                try
                {
                    var buf = new byte[8192];
                    int n;
                    while ((n = channel.Input.Read(buf)) > 0)
                        if (stdout.Length < 1024 * 1024) lock (stdout) stdout.Append(Encoding.UTF8.GetString(buf, 0, n));
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }) { IsBackground = true, Name = "CsCode.Remote-shell-out" };
            reader.Start();
            try
            {
                if (stdin is not null) stdin.CopyTo(channel.Output, 64 * 1024);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* the script ended early: its exit code tells why */ }
            channel.CloseOutput();
            reader.Join();
            var code = channel.Exited.Wait(TimeSpan.FromSeconds(10)) ? channel.Exited.Result : -1;
            ct.ThrowIfCancellationRequested();
            channel.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(6));
            lock (stdout) return new ShellResult(code, stdout.ToString(), channel.StderrTail);
        });
}

/// <summary>Runs blocking work (process I/O during connect/install) on its own short-lived thread, never on the UI thread or the shared pool.</summary>
internal static class BlockingWork
{
    public static Task<T> RunAsync<T>(string name, Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.TrySetResult(work()); }
            catch (OperationCanceledException oce) { tcs.TrySetCanceled(oce.CancellationToken); }
            catch (AggregateException ae) when (ae.InnerException is OperationCanceledException oce) { tcs.TrySetCanceled(oce.CancellationToken); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }) { IsBackground = true, Name = "CsCode.Remote-" + name };
        t.Start();
        return tcs.Task;
    }
}
