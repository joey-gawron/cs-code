using System.Diagnostics;
using CsCode.Profiling;
using CsCode.Profiling.Testing;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.MemoryTests.Monitor;

/// <summary>
/// lsp-core-4: a tracked child's memory is its whole process tree — typescript-language-server's tsserver children, a launcher script's real
/// server — so ChildProcessWorkingSetBytes, the Memory channel and the restartAboveBytes cap see it.
/// </summary>
public sealed class ChildTreeTests
{
    /// <summary>bash (no profile, no rc, throwaway HOME) running two sleeps in the background: a parent with two children.</summary>
    private static Process StartTree(TempDirectory home, int children = 2)
    {
        var psi = new ProcessStartInfo("bash")
        {
            UseShellExecute = false,
            ArgumentList = { "--noprofile", "--norc", "-c", string.Concat(Enumerable.Repeat("sleep 60 & ", children)) + "wait" },
        };
        psi.Environment["HOME"] = home.Path;
        psi.Environment["XDG_CONFIG_HOME"] = home.Combine("config");
        psi.Environment.Remove("BASH_ENV");
        psi.Environment.Remove("ENV");
        return Process.Start(psi)!;
    }

    private static void WaitForChildren(int pid, int count) =>
        Assert.True(SpinWait.SpinUntil(() => ProcessTree.Descendants(pid).Count >= count, 10_000), $"pid {pid} did not start {count} children");

    [Fact]
    public void A_child_is_accounted_with_its_descendants()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("/proc"); return; }
        using var home = new TempDirectory("cscode-tree-home-");
        using var root = StartTree(home);
        try
        {
            WaitForChildren(root.Id, 2);
            using var monitor = new MemoryMonitor(new MemoryMonitorOptions { ChildTreeRescanInterval = TimeSpan.Zero });
            using var tracked = monitor.TrackChildProcess("tree", root.Id);
            var snapshot = monitor.SampleNow();
            var info = Assert.Single(monitor.Children);
            Assert.Equal(3, info.Processes);
            var own = ProcessTree.Resident(root.Id);
            var expected = own + ProcessTree.Descendants(root.Id).Sum(d => d.ResidentBytes);
            Assert.InRange(info.WorkingSetBytes, own + 1, expected * 2);           // more than the root alone
            Assert.InRange(info.WorkingSetBytes, expected / 2, expected * 2);
            Assert.Equal(info.WorkingSetBytes, snapshot.ChildProcessWorkingSetBytes);
        }
        finally { root.Kill(entireProcessTree: true); root.WaitForExit(5000); }
    }

    [Fact]
    public void The_restart_cap_applies_to_the_tree_not_just_the_root()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("/proc"); return; }
        using var home = new TempDirectory("cscode-tree-home-");
        using var root = StartTree(home);
        try
        {
            WaitForChildren(root.Id, 2);
            var own = ProcessTree.Resident(root.Id);
            var calls = 0;
            using var monitor = new MemoryMonitor(new MemoryMonitorOptions { ChildTreeRescanInterval = TimeSpan.Zero, ChildOverBudgetSamples = 2 });
            // Above what the root uses, below what the tree uses: the reviewer's tsserver case in miniature.
            var cap = own + (ProcessTree.Descendants(root.Id).Sum(d => d.ResidentBytes) / 2);
            using var tracked = monitor.TrackChildProcess("tree", root.Id, restartAboveBytes: cap, onOverBudget: () =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.CompletedTask;
            });
            monitor.Tick();
            monitor.Tick();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, 5000), $"no over-budget call: tree {monitor.Children.Single().WorkingSetBytes} B, cap {cap} B, root {own} B");
        }
        finally { root.Kill(entireProcessTree: true); root.WaitForExit(5000); }
    }

    [Fact]
    public void Exited_descendants_drop_out_and_a_separately_tracked_one_is_not_counted_twice()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("/proc"); return; }
        using var home = new TempDirectory("cscode-tree-home-");
        using var root = StartTree(home, children: 3);
        try
        {
            WaitForChildren(root.Id, 3);
            var kids = ProcessTree.Descendants(root.Id).Select(d => d.Pid).ToArray();
            using var monitor = new MemoryMonitor(new MemoryMonitorOptions { ChildTreeRescanInterval = TimeSpan.Zero });
            using var tracked = monitor.TrackChildProcess("tree", root.Id);
            using var alsoTracked = monitor.TrackChildProcess("one of its children", kids[0]);
            monitor.SampleNow();
            Assert.Equal(3, monitor.Children.Single(c => c.Pid == root.Id).Processes); // root + 2: kids[0] is its own entry

            using (var victim = Process.GetProcessById(kids[1])) { victim.Kill(); victim.WaitForExit(5000); }
            Assert.True(SpinWait.SpinUntil(() => { monitor.SampleNow(); return monitor.Children.Single(c => c.Pid == root.Id).Processes == 2; }, 5000));
        }
        finally { root.Kill(entireProcessTree: true); root.WaitForExit(5000); }
    }

    [Fact]
    public void A_disposed_tracking_releases_the_descendants_files()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("/proc"); return; }
        using var home = new TempDirectory("cscode-tree-home-");
        using var root = StartTree(home);
        try
        {
            WaitForChildren(root.Id, 2);
            using var monitor = new MemoryMonitor(new MemoryMonitorOptions { ChildTreeRescanInterval = TimeSpan.Zero });
            var statusFiles = ProcessTree.Descendants(root.Id).Select(d => $"/proc/{d.Pid}/status").ToHashSet();
            int OpenStatusFiles() => Directory.GetFiles("/proc/self/fd").Count(fd =>
            {
                try { return new FileInfo(fd).LinkTarget is { } target && statusFiles.Contains(target); }
                catch (IOException) { return false; } // closed meanwhile (other tests run in this process)
            });
            var tracked = monitor.TrackChildProcess("tree", root.Id);
            monitor.SampleNow();
            Assert.Equal(2, OpenStatusFiles());
            tracked.Dispose();
            Assert.Equal(0, OpenStatusFiles());
        }
        finally { root.Kill(entireProcessTree: true); root.WaitForExit(5000); }
    }
}
