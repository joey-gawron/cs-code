using CsCode.Platform.Io;
using Xunit;

namespace CsCode.Platform.Tests;

public class FileUriTests
{
    public static TheoryData<string> UnixPaths() => new()
    {
        "/tmp/plain.txt",
        "/tmp/with space/file name.cs",
        "/tmp/hash#tag/f#1.txt",
        "/tmp/100%/done%20already.txt",
        "/tmp/question?mark.txt",
        "/tmp/plus+and&amp;=.txt",
        "/tmp/ünïcödé/日本語.txt",
        "/tmp/brackets[1]/(x).txt",
        "/",
    };

    [Theory]
    [MemberData(nameof(UnixPaths))]
    public void Unix_paths_round_trip(string path)
    {
        var uri = FileUri.FromPath(path, windows: false);
        Assert.Equal("file", uri.Scheme);
        Assert.Equal(path, FileUri.ToPath(uri, windows: false));
    }

    [Theory]
    [InlineData("/tmp/plain.txt")]
    [InlineData("/tmp/with space/file.cs")]
    [InlineData("/home/u/proj/src/Program.cs")]
    public void FromPath_equals_new_Uri_for_ordinary_names(string path)
    {
        // Document managers key dictionaries by Uri; whoever builds the Uri must produce an equal one.
        Assert.Equal(new Uri(path), FileUri.FromPath(path, windows: false));
    }

    [Theory]
    [InlineData(@"C:\Users\me\file.txt", "file:///C:/Users/me/file.txt")]
    [InlineData(@"C:\My Docs\a#b\100%.txt", "file:///C:/My%20Docs/a%23b/100%25.txt")]
    [InlineData(@"D:\", "file:///D:/")]
    [InlineData(@"\\server\share\dir\f.txt", "file://server/share/dir/f.txt")]
    public void Windows_paths_map_to_drive_and_unc_uris(string path, string expected)
    {
        var uri = FileUri.FromPath(path, windows: true);
        Assert.Equal(expected, uri.AbsoluteUri, ignoreCase: true);
        Assert.Equal(path, FileUri.ToPath(uri, windows: true), ignoreCase: true);
    }

    [Fact]
    public void Drive_letter_uri_converts_back_with_backslashes()
    {
        Assert.Equal(@"C:\a b\c.txt", FileUri.ToPath(new Uri("file:///C:/a%20b/c.txt"), windows: true));
        Assert.Equal(@"c:\a b\c.txt", FileUri.ToPath(new Uri("file:///c:/a%20b/c.txt"), windows: true)); // drive-letter case is preserved
    }

    [Fact]
    public void Non_file_schemes_are_rejected()
    {
        Assert.Throws<NotSupportedException>(() => FileUri.ToPath(new Uri("ssh://host/x"), windows: false));
        Assert.Throws<NotSupportedException>(() => FileUri.ToPath(new Uri("untitled:Untitled-1"), windows: false));
    }

    [Fact]
    public void Directory_uri_ends_with_slash()
    {
        Assert.EndsWith("/", FileUri.FromDirectoryPath("/tmp/dir").AbsoluteUri);
        Assert.EndsWith("/", FileUri.FromDirectoryPath("/tmp/dir/").AbsoluteUri);
    }
}

public class AppPathsTests
{
    private static Func<string, string?> Env(params (string, string)[] vars)
    {
        var d = vars.ToDictionary(v => v.Item1, v => v.Item2);
        return k => d.GetValueOrDefault(k);
    }

    [Fact]
    public void Linux_uses_xdg_defaults_under_the_home_directory()
    {
        var p = new AppPaths(Env(), HostOs.Linux, "/home/ann");
        Assert.Equal("/home/ann/.config/cscode", p.ConfigDir);
        Assert.Equal("/home/ann/.local/share/cscode", p.UserDataDir);
        Assert.Equal("/home/ann/.cache/cscode", p.CacheDir);
        Assert.Equal("/home/ann/.local/state/cscode/logs", p.LogsDir);
        Assert.Equal("/home/ann/.local/share/cscode/globalStorage", p.GlobalStorageDir);
        Assert.Equal("/home/ann/.local/share/cscode/tools", p.ToolsDir);
    }

    [Fact]
    public void Linux_honours_absolute_xdg_variables_and_ignores_relative_ones()
    {
        var p = new AppPaths(Env(("XDG_CONFIG_HOME", "/cfg"), ("XDG_DATA_HOME", "relative/data"), ("XDG_CACHE_HOME", "/cache")), HostOs.Linux, "/home/ann");
        Assert.Equal("/cfg/cscode", p.ConfigDir);
        Assert.Equal("/home/ann/.local/share/cscode", p.UserDataDir); // relative XDG values are invalid per the spec
        Assert.Equal("/cache/cscode", p.CacheDir);
    }

    [Fact]
    public void CSCODE_HOME_puts_everything_under_one_directory()
    {
        var p = new AppPaths(Env(("CSCODE_HOME", "/portable/cs")), HostOs.Linux, "/home/ann");
        Assert.All(new[] { p.ConfigDir, p.UserDataDir, p.CacheDir, p.LogsDir, p.GlobalStorageDir, p.ToolsDir },
            d => Assert.StartsWith("/portable/cs/", d));
        Assert.StartsWith("/portable/cs/", p.WorkspaceStorageDir(new Uri("file:///w"))!);
    }

    [Fact]
    public void Windows_layout_uses_appdata_and_localappdata()
    {
        var p = new AppPaths(Env(("APPDATA", @"C:\Users\ann\AppData\Roaming"), ("LOCALAPPDATA", @"C:\Users\ann\AppData\Local")), HostOs.Windows, @"C:\Users\ann");
        Assert.Equal(@"C:\Users\ann\AppData\Roaming\CsCode", p.UserDataDir);
        Assert.Equal(p.UserDataDir, p.ConfigDir);
        Assert.Equal(@"C:\Users\ann\AppData\Local\CsCode\Logs", p.LogsDir);
        Assert.StartsWith(@"C:\Users\ann\AppData\Roaming\CsCode\workspaceStorage\", p.WorkspaceStorageDir(new Uri("file:///C:/w"))!);
    }

    [Fact]
    public void MacOs_layout_uses_application_support()
    {
        var p = new AppPaths(Env(), HostOs.MacOs, "/Users/ann");
        Assert.Equal("/Users/ann/Library/Application Support/CsCode", p.UserDataDir);
        Assert.Equal("/Users/ann/Library/Caches/CsCode", p.CacheDir);
        Assert.Equal("/Users/ann/Library/Logs/CsCode", p.LogsDir);
    }

    [Fact]
    public void Workspace_storage_dir_is_a_stable_hash_of_the_folder_uri()
    {
        var p = new AppPaths(Env(), HostOs.Linux, "/home/ann");
        var a1 = p.WorkspaceStorageDir(new Uri("file:///home/ann/proj"));
        var a2 = p.WorkspaceStorageDir(new Uri("file:///home/ann/proj/")); // trailing slash is the same folder
        var b = p.WorkspaceStorageDir(new Uri("file:///home/ann/other"));
        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.Null(p.WorkspaceStorageDir(null));
        // Known-answer: the hash must never change between runs/versions or every workspace would lose its saved state.
        Assert.Equal(AppPaths.StableHash(new Uri("file:///home/ann/proj"), false), Path.GetFileName(a1));
        Assert.Equal(32, Path.GetFileName(a1)!.Length);
    }

    [Fact]
    public void Case_insensitive_platforms_fold_case_in_the_workspace_hash()
    {
        Assert.Equal(AppPaths.StableHash(new Uri("file:///C:/Work/Proj"), true), AppPaths.StableHash(new Uri("file:///C:/work/proj"), true));
        Assert.NotEqual(AppPaths.StableHash(new Uri("file:///work/Proj"), false), AppPaths.StableHash(new Uri("file:///work/proj"), false));
    }

    // files-8: on macOS every workspace hash was case-folded, so "Foo" and "foo" on a case-sensitive APFS volume shared their saved state.
    [Fact]
    public void On_macos_the_workspace_hash_folds_case_only_on_a_case_insensitive_volume()
    {
        var sensitive = new AppPaths(Env(), HostOs.MacOs, "/Users/ann", isCaseSensitive: p => p.StartsWith("/Volumes/Dev", StringComparison.Ordinal));
        Assert.NotEqual(sensitive.WorkspaceStorageDir(new Uri("file:///Volumes/Dev/Foo")), sensitive.WorkspaceStorageDir(new Uri("file:///Volumes/Dev/foo")));
        Assert.Equal(sensitive.WorkspaceStorageDir(new Uri("file:///Users/ann/Foo")), sensitive.WorkspaceStorageDir(new Uri("file:///Users/ann/foo")));
        // The hash of a folder on the default (case-insensitive) volume is unchanged, or every such workspace would lose its saved state.
        Assert.Equal(AppPaths.StableHash(new Uri("file:///Users/ann/Foo"), true), Path.GetFileName(sensitive.WorkspaceStorageDir(new Uri("file:///Users/ann/Foo"))));
        Assert.Equal(AppPaths.StableHash(new Uri("ssh://box/Foo"), true), Path.GetFileName(sensitive.WorkspaceStorageDir(new Uri("ssh://box/Foo"))));
    }

    [Fact]
    public void Install_dir_is_the_application_base_directory()
    {
        Assert.Equal(AppContext.BaseDirectory, new AppPaths().InstallDir);
    }

    [Fact]
    public void Computing_paths_never_touches_the_disk()
    {
        var root = Path.Combine(Path.GetTempPath(), "cscode-nonexistent-" + Guid.NewGuid().ToString("N"));
        var p = new AppPaths(Env(("CSCODE_HOME", root)), HostOs.Linux, "/home/ann");
        _ = (p.ConfigDir, p.UserDataDir, p.LogsDir, p.WorkspaceStorageDir(new Uri("file:///x")));
        Assert.False(Directory.Exists(root));
    }
}
