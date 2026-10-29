using CsCode.Platform.FileSystem;
using CsCode.Platform.Io;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Platform.Tests;

public class PlatformTrashTests : IDisposable
{
    private readonly TempDirectory _dir = new("cscode-xdg-");
    public void Dispose() => _dir.Dispose();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void On_linux_the_trash_lives_under_XDG_DATA_HOME_and_relative_values_are_ignored()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the freedesktop trash is chosen on Linux only");
        var t = Assert.IsType<FreedesktopTrash>(PlatformTrash.Create(k => k == "XDG_DATA_HOME" ? "/xdg/data" : null));
        Assert.Equal("/xdg/data/Trash/files", t.FilesDir);
        Assert.Equal("/xdg/data/Trash/info", t.InfoDir);

        var fallback = Assert.IsType<FreedesktopTrash>(PlatformTrash.Create(k => k == "XDG_DATA_HOME" ? "relative/path" : null));
        Assert.EndsWith(Path.Combine(".local", "share", "Trash", "files"), fallback.FilesDir);
    }

    [Fact]
    public async Task Deleting_with_the_platform_trash_puts_the_file_under_a_temp_XDG_DATA_HOME()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the freedesktop trash is chosen on Linux only");
        var xdg = _dir.Combine("xdg-data");
        using var lane = new IoLane("test.trash", 1);
        var fs = new LocalFileSystem(lane, PlatformTrash.Create(k => k == "XDG_DATA_HOME" ? xdg : null));
        var victim = _dir.WriteFile("work/note.txt", "bye");

        await fs.DeleteAsync(FileUri.FromPath(victim), useTrash: true, ct: Ct);

        Assert.False(File.Exists(victim));
        Assert.Equal("bye", File.ReadAllText(Path.Combine(xdg, "Trash", "files", "note.txt")));
        Assert.Contains("Path=" + victim, File.ReadAllText(Path.Combine(xdg, "Trash", "info", "note.txt.trashinfo")));
    }
}
