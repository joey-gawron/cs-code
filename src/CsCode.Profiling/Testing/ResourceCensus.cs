using System.Diagnostics;

namespace CsCode.Profiling.Testing;

/// <summary>Threads, file descriptors and OS handles of the process at one moment.</summary>
public readonly record struct ResourceCounts(int Threads, int FileDescriptors, int Handles, int ChildProcesses)
{
    public override string ToString() => $"{Threads} threads, {FileDescriptors} fds, {Handles} handles, {ChildProcesses} children";
}

/// <summary>
/// Handle / thread / descriptor census (MUST 4): <c>/proc/self/task</c> and <c>/proc/self/fd</c> on Linux (exact, and they include native threads
/// and descriptors the runtime does not know about), <see cref="Process.Threads"/> and <c>/dev/fd</c> on macOS, <see cref="Process.HandleCount"/> and
/// <see cref="Process.Threads"/> elsewhere; child processes from <c>/proc/self/task/*/children</c> (macOS: libproc). <see cref="AssertReturnsToBaselineAsync"/> polls, because threads and descriptors are released
/// asynchronously (a reader thread exits after its pipe closed, the thread pool retires idle workers after ~20 s).
/// </summary>
public static class ResourceCensus
{
    public static ResourceCounts Capture()
    {
        if (OperatingSystem.IsLinux() && Directory.Exists("/proc/self/task"))
        {
            var threads = Count("/proc/self/task");
            var fds = Count("/proc/self/fd");
            return new ResourceCounts(threads, fds, fds, ChildPids().Count);
        }
        using var self = Process.GetCurrentProcess();
        if (OperatingSystem.IsMacOS())
        {
            var fds = Count("/dev/fd");
            return new ResourceCounts(self.Threads.Count, fds, fds, ChildPids().Count);
        }
        return new ResourceCounts(self.Threads.Count, 0, self.HandleCount, 0);
    }

    /// <summary>Direct children of this process (Linux, macOS; empty elsewhere).</summary>
    public static IReadOnlyList<int> ChildPids()
    {
        if (OperatingSystem.IsMacOS()) return MacProcesses.ChildPids(Environment.ProcessId);
        var pids = new List<int>();
        if (!OperatingSystem.IsLinux()) return pids;
        try
        {
            foreach (var task in Directory.EnumerateDirectories("/proc/self/task"))
            {
                string text;
                try { text = File.ReadAllText(Path.Combine(task, "children")); }
                catch (IOException) { continue; }
                foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(part, out var pid)) pids.Add(pid);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return pids;
    }

    /// <summary>
    /// Waits (up to <paramref name="timeout"/>) until threads, descriptors and children are back within <paramref name="tolerance"/> of
    /// <paramref name="baseline"/>; throws <see cref="MemoryAssertionException"/> with both counts otherwise.
    /// </summary>
    public static async Task<ResourceCounts> AssertReturnsToBaselineAsync(ResourceCounts baseline, int tolerance = 4, TimeSpan? timeout = null, int childTolerance = 0)
    {
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        ResourceCounts now;
        while (true)
        {
            now = Capture();
            if (Within(baseline, now, tolerance, childTolerance)) return now;
            if (deadline.Elapsed > limit) break;
            await Task.Delay(100);
        }
        throw new MemoryAssertionException($"resources did not return to the baseline within {limit.TotalSeconds:F0} s: before {baseline}, after {now} (tolerance {tolerance})");
    }

    public static bool Within(ResourceCounts baseline, ResourceCounts now, int tolerance, int childTolerance = 0) =>
        now.Threads <= baseline.Threads + tolerance && now.FileDescriptors <= baseline.FileDescriptors + tolerance &&
        now.Handles <= baseline.Handles + tolerance && now.ChildProcesses <= baseline.ChildProcesses + childTolerance;

    private static int Count(string directory)
    {
        try { return Directory.EnumerateFileSystemEntries(directory).Count(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return -1; }
    }
}
