using System.Diagnostics;
using CsCode.Sessions;
using Xunit;

namespace CsCode.Sessions.Tests;

/// <summary>
/// The warm spare's reversible low priority (<see cref="ProcessPriority"/>, docs/SESSIONS.md "The warm spare").
/// </summary>
[Collection(nameof(ProcessPriorityTests))] // the macOS case changes this test process's own priority for a moment
public class ProcessPriorityTests
{
    [Fact]
    public void On_macOS_the_darwin_background_state_is_entered_and_left_again()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Skip("macOS: PRIO_DARWIN_PROCESS");
        try
        {
            Assert.False(ProcessPriority.IsBackground());
            Assert.True(ProcessPriority.EnterBackground());
            Assert.True(ProcessPriority.IsBackground());
            // Reversible without privileges (unlike nice): the claimed spare runs at normal priority again.
            Assert.True(ProcessPriority.LeaveBackground());
            Assert.False(ProcessPriority.IsBackground());
        }
        finally { ProcessPriority.LeaveBackground(); }
    }

    [Fact]
    public void A_child_process_of_its_own_session_lowers_its_linux_autogroup_and_raises_it_back_to_normal()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux: /proc/self/autogroup");
        if (!File.Exists("/proc/self/autogroup")) Assert.Skip("this kernel has no scheduler autogroups (CONFIG_SCHED_AUTOGROUP)");
        // In a process that leads its own session (setsid, as every CsCode process the launcher starts does), so the autogroup is its alone and
        // the test runner's is untouched: write 10, read it, write 0 (no privilege needed), read it. The kernel takes one autogroup change per
        // 100 ms, system-wide: the writes are spaced.
        var psi = new ProcessStartInfo("setsid", ["-w", "sh", "-c",
            "cat /proc/self/autogroup; sleep 0.2; echo 10 > /proc/self/autogroup && cat /proc/self/autogroup && sleep 0.2 && echo 0 > /proc/self/autogroup && cat /proc/self/autogroup"])
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(10_000));
        Assert.True(p.ExitCode == 0, error);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith(" nice 0", lines[0]);
        Assert.EndsWith(" nice 10", lines[1]);
        Assert.EndsWith(" nice 0", lines[2]);
    }

    [Fact]
    public void The_linux_autogroup_file_is_written_and_read_as_the_kernel_lays_it_out()
    {
        var previous = ProcessPriority.LinuxAutogroupPath;
        var file = Path.Combine(Path.GetTempPath(), "cscode-autogroup-" + Guid.NewGuid().ToString("N"));
        ProcessPriority.LinuxAutogroupPath = file;
        try
        {
            File.WriteAllText(file, "/autogroup-4711 nice 0\n");
            Assert.Equal(0, ProcessPriority.ReadAutogroupNice());
            File.WriteAllText(file, "/autogroup-4711 nice 10\n");
            Assert.Equal(10, ProcessPriority.ReadAutogroupNice());
            File.WriteAllText(file, "garbage");
            Assert.Null(ProcessPriority.ReadAutogroupNice());
            if (OperatingSystem.IsLinux())
            {
                Assert.True(ProcessPriority.EnterBackground());
                Assert.Equal(ProcessPriority.LinuxBackgroundNice.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(file));
                Assert.True(ProcessPriority.LeaveBackground());
                Assert.Equal("0", File.ReadAllText(file));
            }
            File.Delete(file);
            Assert.Null(ProcessPriority.ReadAutogroupNice());
            if (OperatingSystem.IsLinux()) Assert.False(ProcessPriority.EnterBackground()); // no autogroups: nothing to do, and no exception
        }
        finally
        {
            ProcessPriority.LinuxAutogroupPath = previous;
            File.Delete(file);
        }
    }
}
