using Avalonia.Controls.Recycling;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CsCode.App.Tests.Infrastructure;
using Dock.Avalonia.Controls;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>Dock.Avalonia's control cache over the REAL composition (<see cref="RealAppHost"/>, real editor views).</summary>
public class RealAppDockCacheTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public async Task A_closed_tab_leaves_no_entry_in_Docks_control_cache()
    {
        await using var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("a.txt", "alpha\n");
            h.Files.WriteFile("b.txt", "beta\n");
        });
        await host.StartAsync();
        var well = host.Shell.Well;
        var recycling = Assert.IsType<ControlRecycling>(ControlRecyclingDataTemplate.GetControlRecycling(host.Window.GetVisualDescendants().OfType<DockControl>().Single()));

        var a = await well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        Assert.True(await RealAppHost.WaitFor(() => a.LiveView is not null));
        var b = await well.OpenAsync(new Uri(host.Files.Combine("b.txt")));
        Assert.True(await RealAppHost.WaitFor(() => b.LiveView is not null));
        Assert.True(recycling.TryGetValue(a, out _)); // Dock caches the content of every tab it presented
        Assert.True(recycling.TryGetValue(b, out _));

        // The cache never evicts on its own: a closed tab's host, and through its composition visual the editor view and document it held,
        // stayed reachable for the rest of the session (it made the 300-tab retention test fail whenever closed hosts missed a render pass).
        await well.CloseAsync(a);
        Assert.False(recycling.TryGetValue(a, out _));
        Assert.True(recycling.TryGetValue(b, out _)); // open tabs keep theirs
        await well.CloseAsync(b);
        Assert.False(recycling.TryGetValue(b, out _));
    }
}
