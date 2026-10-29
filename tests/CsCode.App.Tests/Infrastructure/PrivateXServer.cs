using System.Diagnostics;

namespace CsCode.App.Tests.Infrastructure;

/// <summary>
/// A private Xvfb for tests that need a real X server (selections, real windows): its own display number (:100 and up, never the user's :0/:1),
/// no TCP listener, started and stopped by this process (killed by its own PID). The binary comes from <c>CSCODE_XVFB</c>, else the Nix store path
/// this machine has, else <c>Xvfb</c> on PATH; tests skip when there is none.
/// </summary>
public sealed class PrivateXServer : IDisposable
{
    private const string NixXvfb = "/nix/store/qr738j3qn539cfx2imra2p8dx1dna7zx-xvfb-21.1.24/bin/Xvfb";

    private PrivateXServer(Process process, int number)
    {
        Process = process;
        Number = number;
    }

    public Process Process { get; }
    public int Number { get; }
    /// <summary>The DISPLAY value (":123").</summary>
    public string Display => ":" + Number;
    public int Pid => Process.Id;

    public static string? Binary()
    {
        if (Environment.GetEnvironmentVariable("CSCODE_XVFB") is { Length: > 0 } configured && File.Exists(configured)) return configured;
        if (File.Exists(NixXvfb)) return NixXvfb;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, "Xvfb"))) return Path.Combine(dir, "Xvfb");
        return null;
    }

    /// <summary>Starts an Xvfb on the first free display from :100; null (with the reason) when that is not possible.</summary>
    public static PrivateXServer? TryStart(out string why)
    {
        why = "";
        if (!OperatingSystem.IsLinux()) { why = "not Linux"; return null; }
        if (Binary() is not { } binary) { why = "no Xvfb binary (set CSCODE_XVFB)"; return null; }
        for (var n = 100 + Random.Shared.Next(0, 50); n < 200; n++)
        {
            if (File.Exists($"/tmp/.X11-unix/X{n}") || File.Exists($"/tmp/.X{n}-lock")) continue;
            var psi = new ProcessStartInfo(binary, $":{n} -nolisten tcp -screen 0 1280x800x24 -noreset")
            {
                UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            psi.Environment.Remove("DISPLAY");
            var process = Process.Start(psi);
            if (process is null) continue;
            _ = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10_000 && !process.HasExited && !File.Exists($"/tmp/.X11-unix/X{n}")) Thread.Sleep(20);
            if (!process.HasExited && File.Exists($"/tmp/.X11-unix/X{n}")) return new PrivateXServer(process, n);
            Stop(process, n); // lost a race for the number, or failed: try the next one
        }
        why = "could not start Xvfb on :100-:199";
        return null;
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int pid, int signal);

    private const int SigTerm = 15;

    /// <summary>
    /// SIGTERM first (our own child, by its PID): Xvfb then removes its socket and lock file. A killed (SIGKILL) server leaves both behind in /tmp,
    /// so they are removed here, but only when the lock file names this server's PID.
    /// </summary>
    private static void Stop(Process process, int number)
    {
        var pid = process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            if (!process.HasExited)
            {
                _ = SendSignal(process.Id, SigTerm);
                if (!process.WaitForExit(3_000))
                {
                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(5_000);
                }
            }
        }
        catch (InvalidOperationException) { }
        process.Dispose();
        var lockFile = $"/tmp/.X{number}-lock";
        try
        {
            if (File.Exists(lockFile) && File.ReadAllText(lockFile).Trim() == pid)
            {
                File.Delete($"/tmp/.X11-unix/X{number}");
                File.Delete(lockFile);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => Stop(Process, Number);
}
