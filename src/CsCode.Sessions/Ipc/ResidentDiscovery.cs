using System.Diagnostics;

namespace CsCode.Sessions.Ipc;

/// <summary>A CsCode process found in the runtime directory.</summary>
public sealed record ResidentProcess(
    int Pid,
    string Endpoint,
    string? Session,
    string State,
    DateTimeOffset? LastShownUtc,
    bool Debugging,
    long? WorkingSetBytes,
    bool Responsive)
{
    public bool IsSpare => Responsive && State == ProcessStates.Spare;
    public bool OwnsSession(string name) => Responsive && string.Equals(Session, name, StringComparison.Ordinal) && State != ProcessStates.Quitting;
}

/// <summary>
/// Finds this user's CsCode processes: every endpoint in the <see cref="RuntimeDirectory"/> gets a <c>hello</c> (in parallel, each bounded by the
/// connect timeout). Endpoints of dead processes are removed on the way (stale sockets); a live process that does not answer in time is listed
/// as unresponsive (wedged) and never waited for.
/// </summary>
public static class ResidentDiscovery
{
    /// <summary>Off the calling thread (session processes call this from the UI thread's continuations).</summary>
    public static Task<IReadOnlyList<ResidentProcess>> DiscoverAsync(RuntimeDirectory dir, TimeSpan timeout, CancellationToken ct = default, int? excludePid = null) =>
        Task.Run(() => Discover(dir, timeout, excludePid), ct);

    public static Task<ResidentProcess?> ProbeAsync(RuntimeDirectory dir, int pid, TimeSpan timeout, CancellationToken ct = default) =>
        Task.Run(() => Probe(dir, pid, timeout), ct);

    /// <summary>
    /// Blocking: one hello after another (a responsive process answers within a millisecond, so this is fast; each wedged one costs at most
    /// <paramref name="timeout"/>). The launcher's path, which has no thread pool warmed up and nothing else to do meanwhile.
    /// </summary>
    public static IReadOnlyList<ResidentProcess> Discover(RuntimeDirectory dir, TimeSpan timeout, int? excludePid = null) =>
        dir.ListEndpointPids().Where(p => p != excludePid).Distinct().Order().Select(pid => Probe(dir, pid, timeout)).OfType<ResidentProcess>().ToArray();

    public static ResidentProcess? Probe(RuntimeDirectory dir, int pid, TimeSpan timeout)
    {
        var endpoint = dir.EndpointOf(pid);
        try
        {
            var hello = IpcClient.Request(endpoint, new IpcMessage { Type = IpcVerbs.Hello, ProtocolVersion = IpcMessage.ProtocolVersionCurrent }, timeout, timeout);
            if (hello.Ok == false) return new ResidentProcess(pid, endpoint, null, ProcessStates.Starting, null, false, null, Responsive: false);
            return new ResidentProcess(hello.Pid ?? pid, endpoint, hello.Session, hello.State ?? ProcessStates.Starting,
                hello.LastShownUnixMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null, hello.Debugging ?? false, hello.WorkingSetBytes, Responsive: true);
        }
        catch (IpcException ex) when (ex.Failure is IpcFailure.NoEndpoint) { return null; }
        catch (IpcException ex) when (ex.Failure is IpcFailure.WrongOwner) { return null; }
        catch (IpcException)
        {
            if (!IsCsCodeAlive(pid))
            {
                RemoveStale(endpoint);
                return null;
            }
            return new ResidentProcess(pid, endpoint, null, ProcessStates.Starting, null, false, null, Responsive: false);
        }
    }

    private static void RemoveStale(string endpoint)
    {
        if (OperatingSystem.IsWindows()) return; // a pipe disappears with its process
        try { File.Delete(endpoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// True when <paramref name="pid"/> is running and (on Linux, where it can be checked) is a CsCode process. A pid reused by an unrelated program
    /// counts as dead, so its old endpoint is removed.
    /// </summary>
    public static bool IsCsCodeAlive(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var cmdline = File.ReadAllText($"/proc/{pid}/cmdline");
                if (cmdline.Length == 0) return false; // zombie
                return cmdline.Contains("cscode", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }
}
