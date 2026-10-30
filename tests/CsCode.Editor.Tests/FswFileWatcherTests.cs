using CsCode.Core.Platform;
using CsCode.Editor.Fallbacks;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>The editor's standalone watcher (no Platform watcher registered): what its excludes leave out, and the wanted paths it reports anyway.</summary>
public class FswFileWatcherTests
{
    [Fact]
    public async Task A_wanted_folder_inside_an_excluded_one_is_reported_and_the_rest_of_it_is_not()
    {
        var ct = TestContext.Current.CancellationToken;
        using var t = new TempDirectory("cscode-fsw-");
        Directory.CreateDirectory(t.Combine("node_modules", "pkg"));
        Directory.CreateDirectory(t.Combine("node_modules", "other"));
        var seen = new List<string>();
        var wants = new FileWatchWants([new Uri(t.Combine("node_modules", "pkg") + "/")], []);
        using var watch = new FswFileWatcher().Watch(new Uri(t.Path), true, ["**/node_modules/**"], changes =>
        {
            lock (seen) seen.AddRange(changes.Select(c => Path.GetFileName(c.Uri.LocalPath)));
        }, wants);

        File.WriteAllText(t.Combine("node_modules", "other", "no.js"), "1");
        File.WriteAllText(t.Combine("node_modules", "pkg", "yes.js"), "1");
        for (var i = 0; i < 500 && !Seen("yes.js"); i++) await Task.Delay(20, ct);
        Assert.True(Seen("yes.js"));
        await Task.Delay(300, ct);
        Assert.False(Seen("no.js"));

        bool Seen(string name) { lock (seen) return seen.Contains(name); }
    }
}
