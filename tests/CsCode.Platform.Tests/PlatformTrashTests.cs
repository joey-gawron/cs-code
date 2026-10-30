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

    // pkg-15: the test harnesses protected the user's Trash with XDG_DATA_HOME, which the macOS trash ignores: CSCODE_TRASH_DIR works everywhere.
    [Fact]
    public async Task CSCODE_TRASH_DIR_replaces_the_OS_trash_on_every_system_and_relative_values_are_ignored()
    {
        var dir = _dir.Combine("cscode-trash");
        var trash = Assert.IsType<FreedesktopTrash>(PlatformTrash.Create(k => k == PlatformTrash.DirectoryEnvironmentVariable ? dir : null));
        Assert.Equal(Path.Combine(dir, "files"), trash.FilesDir);
        var ignored = PlatformTrash.Create(k => k == PlatformTrash.DirectoryEnvironmentVariable ? "relative" : null);
        Assert.False(ignored is FreedesktopTrash { FilesDir: var files } && files.StartsWith("relative", StringComparison.Ordinal));

        using var lane = new IoLane("test.trash", 1);
        var fs = new LocalFileSystem(lane, trash);
        var victim = _dir.WriteFile("work/note.txt", "bye");
        await fs.DeleteAsync(FileUri.FromPath(victim), useTrash: true, ct: Ct);
        Assert.False(File.Exists(victim));
        Assert.Equal("bye", File.ReadAllText(Path.Combine(dir, "files", "note.txt")));
    }

    // files-2: when Foundation cannot trash an item, MacTrash only RENAMES it into ~/.Trash (here a temp directory), Finder style.
    [Fact]
    public void The_mac_fallback_renames_into_the_trash_directory_with_finder_style_names()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "no lstat on Windows");
        var dir = _dir.Combine("dot-trash");
        var trash = new MacTrash(dir, useFoundation: false);
        var first = _dir.WriteFile("work/a.txt", "1");
        trash.MoveToTrash(first, Ct);
        var second = _dir.WriteFile("work/a.txt", "2");
        trash.MoveToTrash(second, Ct);
        _dir.WriteFile("work/folder.d/inner.txt", "in");
        trash.MoveToTrash(_dir.Combine("work", "folder.d"), Ct);

        Assert.Equal("1", File.ReadAllText(Path.Combine(dir, "a.txt")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(dir, "a 2.txt")));
        Assert.Equal("in", File.ReadAllText(Path.Combine(dir, "folder.d", "inner.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(_dir.Combine("work")));
    }
}
