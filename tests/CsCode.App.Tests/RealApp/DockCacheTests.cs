using Avalonia.Controls.Recycling;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CsCode.App.Docking;
using CsCode.App.Tests.Infrastructure;
using Dock.Avalonia.Controls;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>Dock.Avalonia's control cache over the REAL composition (<see cref="RealAppHost"/>, real editor views).</summary>
public class RealAppDockCacheTests
{
    /// <summary>
    /// The cache never evicts on its own: a closed tab's host, and through its composition visual the editor view and document it held, stayed
    /// reachable for the rest of the session when Dock cached it (it made the 300-tab retention test fail whenever closed hosts missed a render
    /// pass). The shell's tabs are their own content templates (<see cref="ShellDockContent"/>), which Dock's content presenter asks before its
    /// cache, so the cache never holds a shell tab at all: not while it is open, not after it closed.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task Docks_control_cache_never_holds_a_shell_tab()
    {
        await using var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("a.txt", "alpha\n");
            h.Files.WriteFile("b.txt", "beta\n");
        });
        await host.StartAsync();
        var well = host.Shell.Well;
        var dock = host.Window.GetVisualDescendants().OfType<DockControl>().Single();
        Assert.True(ShellDockContent.IsInstalled(dock));
        var recycling = Assert.IsType<ControlRecycling>(ControlRecyclingDataTemplate.GetControlRecycling(dock));

        var a = await well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        Assert.True(await RealAppHost.WaitFor(() => a.LiveView is not null));
        var b = await well.OpenAsync(new Uri(host.Files.Combine("b.txt")));
        Assert.True(await RealAppHost.WaitFor(() => b.LiveView is not null && b.LiveView.IsAttachedToVisualTree()));
        Assert.False(recycling.TryGetValue(a, out _));
        Assert.False(recycling.TryGetValue(b, out _));
        foreach (var tool in host.Shell.Tools.Tools) Assert.False(recycling.TryGetValue(tool, out _), tool.Spec.Id);

        await well.CloseAsync(a);
        Assert.False(recycling.TryGetValue(a, out _));
        await well.CloseAsync(b);
        Assert.False(recycling.TryGetValue(b, out _));
    }
}
