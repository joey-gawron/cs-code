using System.Diagnostics;
using System.Text.RegularExpressions;
using CsCode.Core.Remote;
using CsCode.Remote.Protocol;
using CsCode.Terminal.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>
/// R4 chaos: the link to a persistent server is cut again and again (randomly timed) while a terminal floods output, files are read and the
/// user types; afterwards nothing is lost or repeated, the server is the same process, and the typing path cost an enqueue throughout. Plus
/// connect/disconnect cycles that must leave no process, thread or daemon behind.
/// </summary>
public class R4ChaosTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    [Fact]
    public async Task Repeated_link_cuts_during_a_terminal_flood_file_reads_and_typing_lose_nothing_and_never_block_the_caller()
    {
        var control = new CuttableTransport.Control();
        await using var host = new RemoteTestHost(o => o.Reconnect = new Client.ReconnectPolicy(8, () => 0.5), wrap: t => new CuttableTransport(t, control), r3: true,
            persistent: TimeSpan.FromSeconds(120));
        var data = new byte[1024 * 1024];
        new Random(11).NextBytes(data);
        File.WriteAllBytes(Path.Combine(host.Workspace.Path, "data.bin"), data);
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();
        var instance = host.Manager.Session!.Server.InstanceId;
        var terminals = host.Services.GetRequiredService<TerminalService>();
        var flood = (TerminalSession)await terminals.CreateAsync(TerminalProbe.Bash() with { InitialSize = (80, 1000) }, Ct);
        var typing = (TerminalSession)await terminals.CreateAsync(TerminalProbe.Bash(), Ct);
        await typing.SendTextAsync("stty -echo; cat > /dev/null", true, Ct);

        const int Lines = 900;
        await flood.SendTextAsync($"for i in $(seq 1 {Lines}); do echo flood-$i; sleep 0.004; done; echo flood-done", true, Ct);

        var random = new Random(5);
        using var stop = new CancellationTokenSource();
        var reads = 0;
        var readFailures = 0;
        var reader = Task.Run(async () =>
        {
            var uri = host.Remote("data.bin");
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    Assert.Equal(data, await host.FileSystem.ReadFileAsync(uri, Ct));
                    Interlocked.Increment(ref reads);
                }
                catch (RemoteDisconnectedException) { Interlocked.Increment(ref readFailures); } // in flight at the moment of a cut: the caller retries
            }
        }, Ct);
        var typed = 0L;
        var typingTicks = 0L;
        var typist = Task.Run(async () =>
        {
            var key = "x"u8.ToArray();
            while (!stop.IsCancellationRequested)
            {
                var t0 = Stopwatch.GetTimestamp();
                typing.Input(key);
                Interlocked.Add(ref typingTicks, Stopwatch.GetTimestamp() - t0);
                Interlocked.Increment(ref typed);
                await Task.Delay(2, Ct);
            }
        }, Ct);

        for (var cut = 0; cut < 4; cut++)
        {
            await Task.Delay(random.Next(300, 900), Ct);
            await control.CutAsync();
            await TestEnv.WaitUntilAsync(() => host.Manager.Session is { IsOpen: true } && control.Connects >= cut + 2, TimeSpan.FromSeconds(60), $"reconnect {cut + 1}");
        }
        var screen = await TerminalProbe.WaitForTextAsync(flood, "\nflood-done", TimeSpan.FromSeconds(120)); // the output line, not the command's echo
        stop.Cancel();
        await Task.WhenAll(reader, typist);

        var lines = Regex.Matches(screen, @"^flood-(\d+)$", RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.True(Enumerable.Range(1, Lines).SequenceEqual(lines),
            $"missing: {string.Join(',', Enumerable.Range(1, Lines).Except(lines).Take(40))}; repeated: {string.Join(',', lines.GroupBy(l => l).Where(g => g.Count() > 1).Select(g => g.Key).Take(40))}\n"
            + Around(screen, Enumerable.Range(1, Lines).Except(lines).FirstOrDefault()));
        Assert.Equal(instance, host.Manager.Session!.Server.InstanceId); // one server process throughout
        Assert.False(typing.HasExited);
        Assert.False(flood.HasExited);
        Assert.True(reads > 3, $"{reads} reads, {readFailures} failed by a cut");
        var perKeyUs = Stopwatch.GetElapsedTime(0, Interlocked.Read(ref typingTicks)).TotalMicroseconds / Math.Max(1, Interlocked.Read(ref typed));
        Assert.True(perKeyUs < 100, $"a keystroke cost the typing thread {perKeyUs:0.0} us on average ({typed} keystrokes)"); // enqueue only, cuts or not
        await terminals.KillAsync(flood, Ct);
        await terminals.KillAsync(typing, Ct);
    }

    private static string Around(string screen, int missing)
    {
        var at = screen.IndexOf($"flood-{missing - 1}\n", StringComparison.Ordinal);
        if (at < 0) return "";
        var tail = screen[at..];
        return $"count after gap point: {Regex.Matches(tail, "^flood-", RegexOptions.Multiline).Count}\n" + tail[..Math.Min(500, tail.Length)].Replace("\n", " | ", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Twenty_persistent_connect_disconnect_cycles_leave_no_process_daemon_or_thread_behind()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc entries");
        await using var host = new RemoteTestHost(r3: true, persistent: TimeSpan.FromSeconds(60));
        await host.ConnectAsync(); // warm-up: JIT, thread pool
        await host.Manager.DisconnectAsync(Ct);
        await TestEnv.AssertNoProcessesAsync(host.Marker);
        var threads = Process.GetCurrentProcess().Threads.Count;
        var fds = Directory.GetFiles("/proc/self/fd").Length;
        for (var i = 0; i < 20; i++)
        {
            await host.Manager.ConnectAsync(RemoteTestHost.Host, Ct);
            Assert.Equal(RemoteConnectionState.Connected, host.Manager.Status.State);
            await host.Manager.DisconnectAsync(Ct);
            await TestEnv.AssertNoProcessesAsync(host.Marker, TimeSpan.FromSeconds(20)); // relay and daemon both gone, every time
        }
        await Task.Delay(500, Ct);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var threadsAfter = Process.GetCurrentProcess().Threads.Count;
        var fdsAfter = Directory.GetFiles("/proc/self/fd").Length;
        Assert.True(threadsAfter - threads <= 12, $"threads {threads} -> {threadsAfter}");
        Assert.True(fdsAfter - fds <= 12, $"file descriptors {fds} -> {fdsAfter}");
        Assert.Equal(0, host.Manager.Channels?.Count ?? 0);
    }
}
