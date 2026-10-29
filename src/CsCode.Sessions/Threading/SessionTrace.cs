using System.Diagnostics;
using System.Globalization;

namespace CsCode.Sessions;

/// <summary>
/// Opt-in timing trace of the launcher and of session processes (<c>CSCODE_SESSION_TRACE=&lt;file&gt;</c>): one line per phase with the
/// milliseconds since the process started, appended to the file. Off (and free) unless the variable is set; used to measure the latencies
/// documented in docs/SESSIONS.md.
/// </summary>
public static class SessionTrace
{
    private static readonly string? s_path = Environment.GetEnvironmentVariable("CSCODE_SESSION_TRACE") is { Length: > 0 } p ? p : null;
    private static readonly object s_gate = new();

    public static bool Enabled => s_path is not null;

    public static void Mark(string phase)
    {
        if (s_path is null) return;
        var sinceStart = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.fff} pid {Environment.ProcessId} +{sinceStart:0} ms {phase}\n");
        lock (s_gate)
        {
            try { File.AppendAllText(s_path, line); }
            catch (IOException) { /* tracing never fails the caller */ }
        }
    }
}
