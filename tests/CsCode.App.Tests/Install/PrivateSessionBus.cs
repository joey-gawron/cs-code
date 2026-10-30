using System.Diagnostics;
using System.Runtime.Versioning;

namespace CsCode.App.Tests.Install;

/// <summary>
/// A D-Bus session bus of the test's own (a dbus-daemon on a socket in the sandbox's runtime directory, started and stopped by PID), standing in
/// for the desktop session's bus. Without one, an X11 program whose environment has no DBUS_SESSION_BUS_ADDRESS makes libdbus/GDBus AUTOLAUNCH
/// a bus (dbus-launch --autolaunch) plus whatever it activates (the accessibility bus, gvfsd), which outlive the program and would read as
/// processes the app left behind. Services this bus activates carry DBUS_STARTER_ADDRESS in their environment, which tells them apart from
/// processes the app starts.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PrivateSessionBus : IDisposable
{
    private const string Daemon = "/usr/bin/dbus-daemon";
    private readonly Process _process;

    private PrivateSessionBus(Process process, string address)
    {
        _process = process;
        Address = address;
    }

    public string Address { get; }
    public int Pid => _process.Id;

    /// <summary>Starts the daemon; null with the reason when that is not possible.</summary>
    public static async Task<(PrivateSessionBus? Bus, string Why)> TryStartAsync(InstallSandbox box)
    {
        if (!File.Exists(Daemon)) return (null, "no " + Daemon);
        var psi = new ProcessStartInfo("/usr/bin/env") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        psi.ArgumentList.Add("-i");
        foreach (var (k, v) in box.Environment()) if (v is not null) psi.ArgumentList.Add($"{k}={v}");
        psi.ArgumentList.Add("XDG_RUNTIME_DIR=" + box.Run);
        foreach (var a in new[] { Daemon, "--session", "--nofork", "--nopidfile", "--print-address=1", "--address=unix:path=" + Path.Combine(box.Run, "bus") })
            psi.ArgumentList.Add(a);
        var process = Process.Start(psi)!; // env execs the daemon: the same PID
        process.StandardInput.Close();
        _ = process.StandardError.ReadToEndAsync();
        string? address;
        try { address = await process.StandardOutput.ReadLineAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { address = null; }
        if (string.IsNullOrWhiteSpace(address))
        {
            Stop(process);
            return (null, "dbus-daemon did not print its address");
        }
        _ = process.StandardOutput.ReadToEndAsync();
        return (new PrivateSessionBus(process, address.Trim()), "");
    }

    /// <summary>A process the bus started for a service (D-Bus activation sets DBUS_STARTER_ADDRESS), or the daemon itself.</summary>
    public bool Owns(int pid)
    {
        if (pid == Pid) return true;
        try { return File.ReadAllText($"/proc/{pid}/environ").Contains("\0DBUS_STARTER_ADDRESS=", StringComparison.Ordinal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5_000);
            }
        }
        catch (InvalidOperationException) { }
        process.Dispose();
    }

    /// <summary>Stops the daemon (its activated services lose their bus and exit; the sandbox kills any that do not, by PID).</summary>
    public void Dispose() => Stop(_process);
}
