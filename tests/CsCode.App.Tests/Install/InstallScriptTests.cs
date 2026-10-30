using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using CsCode.Remote.Install;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests.Install;

/// <summary>
/// tools/install.sh and tools/uninstall.sh against throwaway homes (<see cref="InstallSandbox"/>), with a small stand-in for the publish so each
/// run takes a second (docs/briefs/app-icon-and-install.md, MUST 2-3): the layout, idempotence, upgrades that keep exactly one previous version,
/// the launcher's arguments and environment, the desktop entry, the icons, the bundled runtime pieces and adapters, and an uninstall that
/// removes exactly what the install created. The real publish is installed by <see cref="InstalledAppTests"/>.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class InstallScriptTests
{
    private static readonly string[] IconSizes = ["16", "32", "48", "64", "128", "256", "512", "scalable"];

    private static void Ok(SandboxRun run) => Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}\n{run.All}");

    private static string Branding(string file) => Path.Combine(InstallSandbox.RepoRoot(), "assets", "branding", file);

    [Fact]
    public async Task Install_lays_out_the_version_the_current_link_the_launcher_the_desktop_entry_and_every_icon()
    {
        using var box = new InstallSandbox();
        var publish = box.FakePublish();
        Ok(await box.InstallAsync("--from", publish, "--version", "1.0.0-a", "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));

        Assert.Equal("1.0.0-a", new FileInfo(box.Current).LinkTarget); // relative: the folder can move with its parent
        var app = Path.Combine(box.AppRoot, "1.0.0-a");
        Assert.True(File.Exists(Path.Combine(app, "cscode")) && File.Exists(Path.Combine(app, "nested", "data.txt")));
        Assert.True(File.Exists(Path.Combine(app, "uninstall.sh")), "the uninstaller ships with every version");
        Assert.Contains("version\t1.0.0-a", File.ReadAllText(Path.Combine(app, ".cscode-install", "info")));
        // the version's own list names every file and directory of it
        var listed = File.ReadAllLines(Path.Combine(app, ".cscode-install", "files.tsv")).Select(l => l.Split('\t')[1]).ToHashSet();
        var actual = Directory.EnumerateFileSystemEntries(app, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(app, p)).ToHashSet();
        Assert.Equal(actual.Order(), listed.Order());

        Assert.True(File.Exists(box.Launcher));
        Assert.True(File.GetUnixFileMode(box.Launcher).HasFlag(UnixFileMode.UserExecute));
        Assert.Contains("cscode-install-marker", File.ReadAllText(box.Launcher));
        Assert.Contains($"app='{box.AppRoot}/current/cscode'", File.ReadAllText(box.Launcher));
        foreach (var size in IconSizes)
            Assert.Equal(File.ReadAllBytes(Branding(size == "scalable" ? "cscode.svg" : $"cscode-{size}.png")), File.ReadAllBytes(box.Icon(size)));

        // The manifest lists every file outside the version folders, every directory the install created, and the version.
        Assert.Equal(["1.0.0-a"], box.ManifestEntries("version").Select(e => e[0]));
        var files = box.ManifestEntries("file").Select(e => e[1]).ToHashSet();
        Assert.Contains(box.Launcher, files);
        Assert.Contains(box.DesktopEntry, files);
        foreach (var size in IconSizes) Assert.Contains(box.Icon(size), files);
        var dirs = box.ManifestEntries("dir").Select(e => e[0]).ToHashSet();
        Assert.Contains(Path.Combine(box.Home, ".local", "bin"), dirs);
        Assert.Contains(Path.GetDirectoryName(box.Icon("16"))!, dirs);
        // Nothing outside the sandbox's home: every created path is under it.
        Assert.All(files.Concat(dirs), p => Assert.StartsWith(box.Home + "/", p));
    }

    [Fact]
    public async Task The_desktop_entry_names_the_launcher_and_validates_also_for_a_home_with_spaces()
    {
        using var box = new InstallSandbox("home with 'quotes' & $pace");
        Ok(await box.InstallAsync("--from", box.FakePublish(), "--version", "1", "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));
        var entry = DesktopEntry.Parse(File.ReadAllText(box.DesktopEntry));
        var main = entry["Desktop Entry"];
        Assert.Equal("Application", main["Type"]);
        Assert.Equal("CsCode", main["Name"]);
        Assert.Equal("Code Editor", main["GenericName"]);
        Assert.Equal("cscode", main["Icon"]);
        Assert.Equal("cscode", main["StartupWMClass"]);
        Assert.Equal("Development;IDE;TextEditor;", main["Categories"]);
        Assert.Equal("new-window;", main["Actions"]);
        var mime = main["MimeType"].Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var source in new[] { "text/x-csharp", "text/rust", "text/x-c++src", "text/x-python", "text/javascript", "application/json", "text/markdown" })
            Assert.Contains(source, mime);
        Assert.DoesNotContain("inode/directory", mime); // never the default for folders
        // Exec: the launcher by its absolute path, quoted and escaped as the Desktop Entry Specification asks; then the file list.
        Assert.Equal([box.Launcher, "%F"], DesktopEntry.ExecArguments(main["Exec"]));
        Assert.Equal(box.Launcher, main["TryExec"]);
        Assert.Equal([box.Launcher, "--new-window", "%F"], DesktopEntry.ExecArguments(entry["Desktop Action new-window"]["Exec"]));
        Assert.Equal("New Window", entry["Desktop Action new-window"]["Name"]);

        var validate = DesktopEntry.Validator();
        if (validate is null) return; // desktop-file-utils is not installed: the parse above is the check
        var run = await box.RunAsync(validate, [box.DesktopEntry]);
        Assert.True(run.ExitCode == 0, run.All);
        Assert.DoesNotContain("error", run.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rerunning_the_same_build_changes_nothing()
    {
        using var box = new InstallSandbox();
        var publish = box.FakePublish();
        // no --version: the folder is named after the build's content, so the same publish is the same version
        Ok(await box.InstallAsync("--from", publish, "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));
        var version = new FileInfo(box.Current).LinkTarget!;
        Assert.StartsWith("9.8.7-", version);
        var first = box.Snapshot();
        Ok(await box.InstallAsync("--from", publish, "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));
        var second = box.Snapshot();
        var info = Path.Combine(".local", "share", "cscode", version, ".cscode-install", "info"); // records the install time
        first.Remove(info);
        second.Remove(info);
        Assert.Equal(InstallSandbox.Describe(first), InstallSandbox.Describe(second));
        Assert.Equal(version, new FileInfo(box.Current).LinkTarget);
        Assert.Equal("", box.ManifestEntries("previous").Single()[0]);
    }

    [Fact]
    public async Task A_new_build_upgrades_and_keeps_exactly_one_previous_version_for_rollback()
    {
        using var box = new InstallSandbox();
        var a = box.FakePublish("a", extra: "a");
        var b = box.FakePublish("b", extra: "b");
        var c = box.FakePublish("c", extra: "c");
        string[] Versions() => new DirectoryInfo(box.AppRoot).GetDirectories().Where(d => d.LinkTarget is null).Select(d => d.Name).Order().ToArray();

        Ok(await box.InstallAsync("--from", a, "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));
        var va = new FileInfo(box.Current).LinkTarget!;
        Ok(await box.InstallAsync("--from", b, "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));
        var vb = new FileInfo(box.Current).LinkTarget!;
        Assert.NotEqual(va, vb);
        Assert.Equal(new[] { va, vb }.Order(), Versions());
        Assert.Equal(va, box.ManifestEntries("previous").Single()[0]);

        var upgrade = await box.InstallAsync("--from", c, "--no-adapters", "--no-tree-sitter", "--no-server-bundles");
        Ok(upgrade);
        var vc = new FileInfo(box.Current).LinkTarget!;
        Assert.Equal(new[] { vb, vc }.Order(), Versions()); // the oldest went, exactly
        Assert.Equal(vb, box.ManifestEntries("previous").Single()[0]);
        Assert.Equal(new[] { vb, vc }.Order(), box.ManifestEntries("version").Select(e => e[0]).Order());
        Assert.Contains($"ln -sfn {vb}", upgrade.Output); // how to roll back
        Assert.Contains("removed older version(s): " + va, upgrade.Output);

        // Reinstalling the previous build makes it current again and keeps the one it replaced.
        Ok(await box.InstallAsync("--from", b, "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));
        Assert.Equal(vb, new FileInfo(box.Current).LinkTarget);
        Assert.Equal(vc, box.ManifestEntries("previous").Single()[0]);
        Assert.Equal(new[] { vb, vc }.Order(), Versions());
    }

    [Fact]
    public async Task A_version_that_is_still_running_is_kept_until_a_later_install()
    {
        using var box = new InstallSandbox();
        Ok(await box.InstallAsync("--from", box.FakePublish("a", extra: "a"), "--version", "1", "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));
        using var running = Process.Start(Path.Combine(box.AppRoot, "1", "sleeper"), "300")!; // a process of version 1, like a resident session
        try
        {
            Ok(await box.InstallAsync("--from", box.FakePublish("b", extra: "b"), "--version", "2", "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));
            var third = await box.InstallAsync("--from", box.FakePublish("c", extra: "c"), "--version", "3", "--no-adapters", "--no-tree-sitter", "--no-server-bundles");
            Ok(third);
            Assert.Contains("CsCode 1 is still running", third.Error);
            Assert.True(File.Exists(Path.Combine(box.AppRoot, "1", "sleeper")));
            Assert.Equal(["1", "2", "3"], box.ManifestEntries("version").Select(e => e[0]).Order());
        }
        finally
        {
            running.Kill();
            running.WaitForExit(5_000);
        }
        Ok(await box.InstallAsync("--from", box.FakePublish("d", extra: "d"), "--version", "4", "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));
        Assert.False(Directory.Exists(Path.Combine(box.AppRoot, "1")));
        Assert.Equal(["3", "4"], box.ManifestEntries("version").Select(e => e[0]).Order());
    }

    [Fact]
    public async Task A_cscode_launcher_or_desktop_entry_the_installer_did_not_create_is_never_replaced()
    {
        using var box = new InstallSandbox();
        var mine = Path.Combine(box.Home, ".local", "bin", "cscode");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllText(mine, "#!/bin/sh\necho my own cscode\n");
        var before = box.Snapshot();
        var run = await box.InstallAsync("--from", box.FakePublish(), "--no-adapters", "--no-tree-sitter", "--no-server-bundles");
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("was not created by this installer", run.Error);
        Assert.Equal(InstallSandbox.Describe(before), InstallSandbox.Describe(box.Snapshot())); // refused before anything was written

        File.Delete(mine);
        Directory.CreateDirectory(Path.GetDirectoryName(box.DesktopEntry)!);
        File.WriteAllText(box.DesktopEntry, "[Desktop Entry]\nType=Application\nName=Someone else's\nExec=true\n");
        before = box.Snapshot();
        run = await box.InstallAsync("--from", box.FakePublish(), "--no-adapters", "--no-tree-sitter", "--no-server-bundles");
        Assert.NotEqual(0, run.ExitCode);
        Assert.Equal(InstallSandbox.Describe(before), InstallSandbox.Describe(box.Snapshot()));
    }

    [Fact]
    public async Task The_launcher_passes_every_argument_and_sets_DOTNET_ROOT_only_when_unset_and_a_dotnet_is_found()
    {
        using var box = new InstallSandbox();
        Ok(await box.InstallAsync("--from", box.FakePublish(), "--version", "1", "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));
        var output = Path.Combine(box.Home, "fake-app.out");
        async Task<Dictionary<string, string>> Launch(string[] args, Dictionary<string, string?>? env = null)
        {
            Ok(await box.RunAsync(box.Launcher, args, env));
            var lines = File.ReadAllLines(output);
            var result = lines.Where(l => !l.StartsWith("arg:", StringComparison.Ordinal)).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
            result["args"] = string.Join("|", lines.Where(l => l.StartsWith("arg:", StringComparison.Ordinal)).Select(l => l[4..]));
            return result;
        }
        var tools = Path.Combine(box.Home, ".dotnet", "tools");
        // a dotnet in a standard system location would be found: then "unset" cannot be observed here
        var systemDotnet = new[] { "/usr/share/dotnet", "/usr/lib/dotnet", "/usr/lib64/dotnet", "/usr/local/share/dotnet", "/opt/dotnet" }
            .Any(d => Directory.Exists(Path.Combine(d, "shared", "Microsoft.NETCore.App")));

        var plain = await Launch([".", "a b", "src/File.cs:3:4", "--new-window", "", "naïve", "$HOME", "*"]);
        Assert.Equal(".|a b|src/File.cs:3:4|--new-window||naïve|$HOME|*", plain["args"]); // verbatim: no splitting, globbing or expansion
        if (!systemDotnet) Assert.Equal("<unset>", plain["DOTNET_ROOT"]);
        Assert.Equal("/usr/bin:/bin:" + tools, plain["PATH"]);

        // No PATH at all (a bare env -i): the shell's default search path (dash has one; else the launcher's), plus the .NET tools.
        var bare = (await Launch([], new() { ["PATH"] = null }))["PATH"];
        Assert.EndsWith(":/bin:" + tools, bare);
        Assert.Contains("/usr/bin:", bare);
        // Already on PATH: not added twice.
        Assert.Equal($"/usr/bin:{tools}:/bin", (await Launch([], new() { ["PATH"] = $"/usr/bin:{tools}:/bin" }))["PATH"]);

        // ~/.dotnet with a runtime: DOTNET_ROOT is that folder.
        var home = Path.Combine(box.Home, ".dotnet");
        Directory.CreateDirectory(Path.Combine(home, "shared", "Microsoft.NETCore.App", "10.0.0"));
        File.WriteAllText(Path.Combine(home, "dotnet"), "#!/bin/sh\n");
        File.SetUnixFileMode(Path.Combine(home, "dotnet"), (UnixFileMode)0b111_101_101);
        Assert.Equal(home, (await Launch([]))["DOTNET_ROOT"]);
        // A dotnet on PATH that is a symlink into an SDK folder (Nix's profile, /usr/bin/dotnet -> /usr/lib/dotnet/dotnet): the real folder, first.
        var sdk = Path.Combine(box.Root, "sdk", "share", "dotnet");
        Directory.CreateDirectory(Path.Combine(sdk, "shared", "Microsoft.NETCore.App"));
        File.WriteAllText(Path.Combine(sdk, "dotnet"), "#!/bin/sh\n");
        File.SetUnixFileMode(Path.Combine(sdk, "dotnet"), (UnixFileMode)0b111_101_101);
        var bin = Path.Combine(box.Root, "profile", "bin");
        Directory.CreateDirectory(bin);
        File.CreateSymbolicLink(Path.Combine(bin, "dotnet"), Path.Combine(sdk, "dotnet"));
        Assert.Equal(sdk, (await Launch([], new() { ["PATH"] = bin + ":/usr/bin:/bin" }))["DOTNET_ROOT"]);
        // A dotnet without a runtime next to it does not count.
        Directory.Delete(Path.Combine(sdk, "shared"), recursive: true);
        Assert.Equal(home, (await Launch([], new() { ["PATH"] = bin + ":/usr/bin:/bin" }))["DOTNET_ROOT"]);
        // Set by the user: kept as it is.
        Assert.Equal("/opt/my-dotnet", (await Launch([], new() { ["DOTNET_ROOT"] = "/opt/my-dotnet" }))["DOTNET_ROOT"]);
    }

    [Fact]
    public async Task Uninstall_removes_exactly_what_the_installs_created_and_keeps_settings_state_and_other_programs_files()
    {
        using var box = new InstallSandbox();
        // The user's own things, before any install: settings, other apps' entries and icons, a bin directory.
        void Write(string rel, string text)
        {
            var path = Path.Combine(box.Home, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write(".config/cscode/settings.json", "{ \"editor.fontSize\": 15 }\n");
        Write(".local/share/applications/zed.desktop", "[Desktop Entry]\nType=Application\nName=Zed\nExec=zed\n");
        Write(".local/share/icons/hicolor/48x48/apps/zed.png", "png");
        Write(".local/bin/je", "#!/bin/sh\n");
        var before = box.Snapshot();

        Ok(await box.InstallAsync("--from", box.FakePublish("a", extra: "a"), "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));
        // What the app itself writes while it runs: sessions, downloaded tools, logs, caches (all kept by the uninstall).
        Write(".local/share/cscode/sessions/work.json", "{}");
        Write(".local/share/cscode/tools/netcoredbg/netcoredbg", "elf");
        Write(".local/state/cscode/logs/cscode.log", "log");
        Write(".cache/cscode/tokens.bin", "cache");
        string[] appWrites = [".local/share/cscode/sessions", ".local/share/cscode/tools", ".local/state", ".cache"];
        var userData = box.Snapshot().Where(kv => appWrites.Any(w => kv.Key.StartsWith(w, StringComparison.Ordinal))).ToList();
        Ok(await box.InstallAsync("--from", box.FakePublish("b", extra: "b"), "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));

        var uninstall = await box.UninstallAsync();
        Ok(uninstall);
        Assert.Contains("CsCode uninstalled", uninstall.Output);
        var after = box.Snapshot();
        foreach (var (path, what) in userData) Assert.Equal(what, after[path]);
        foreach (var path in userData.Select(kv => kv.Key)) after.Remove(path);
        // ...and the directories that only exist because the app wrote into them
        foreach (var path in after.Keys.Where(k => !before.ContainsKey(k) && after[k] == "d").ToList())
            if (userData.Any(u => u.Key.StartsWith(path + "/", StringComparison.Ordinal))) after.Remove(path);
        Assert.Equal(InstallSandbox.Describe(before), InstallSandbox.Describe(after));

        // Idempotent: a second run has nothing to do and says so.
        var again = await box.UninstallAsync();
        Ok(again);
        Assert.Contains("not installed", again.Output);
    }

    [Fact]
    public async Task Uninstall_without_any_user_data_leaves_the_home_as_it_was()
    {
        using var box = new InstallSandbox();
        var before = box.Snapshot();
        Ok(await box.InstallAsync("--from", box.FakePublish(), "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));
        Ok(await box.UninstallAsync());
        Assert.Equal(InstallSandbox.Describe(before), InstallSandbox.Describe(box.Snapshot()));
    }

    [Fact]
    public async Task Uninstall_keeps_a_launcher_or_an_icon_someone_replaced_after_the_install()
    {
        using var box = new InstallSandbox();
        Ok(await box.InstallAsync("--from", box.FakePublish(), "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));
        File.WriteAllText(box.Launcher, "#!/bin/sh\necho mine now\n");
        File.WriteAllText(box.Icon("48"), "another icon");
        var run = await box.UninstallAsync();
        Ok(run);
        Assert.Equal("#!/bin/sh\necho mine now\n", File.ReadAllText(box.Launcher));
        Assert.Equal("another icon", File.ReadAllText(box.Icon("48")));
        Assert.Contains("kept " + box.Launcher, run.Error);
        Assert.False(File.Exists(box.Icon("16")));
        Assert.False(File.Exists(box.DesktopEntry));
        Assert.False(Directory.Exists(box.AppRoot));
    }

    [Fact]
    public async Task Uninstall_refuses_while_the_installed_app_runs()
    {
        using var box = new InstallSandbox();
        Ok(await box.InstallAsync("--from", box.FakePublish(), "--version", "1", "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));
        var before = box.Snapshot();
        using var running = Process.Start(Path.Combine(box.AppRoot, "1", "sleeper"), "300")!;
        try
        {
            var run = await box.UninstallAsync();
            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("cscode --quit --all", run.Error);
            Assert.Equal(InstallSandbox.Describe(before), InstallSandbox.Describe(box.Snapshot()));
        }
        finally
        {
            running.Kill();
            running.WaitForExit(5_000);
        }
        Ok(await box.UninstallAsync());
        Assert.False(File.Exists(box.Launcher));
    }

    [Fact]
    public async Task The_shipped_copy_of_the_uninstaller_works_without_the_source_tree()
    {
        using var box = new InstallSandbox();
        Ok(await box.InstallAsync("--from", box.FakePublish(), "--version", "1", "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));
        var copy = Path.Combine(box.Root, "uninstall-copy.sh"); // outside the repository: nothing next to it
        File.Copy(Path.Combine(box.Current, "uninstall.sh"), copy);
        Ok(await box.ScriptAsync(copy, ["--no-refresh"]));
        Assert.False(File.Exists(box.Manifest));
        Assert.False(File.Exists(box.Launcher));
    }

    [Fact]
    public async Task Tree_sitter_shell_integration_and_server_bundles_are_placed_where_the_app_looks()
    {
        using var box = new InstallSandbox();
        var bundle = Path.Combine(box.Root, "ts");
        Directory.CreateDirectory(Path.Combine(bundle, "grammars"));
        File.WriteAllText(Path.Combine(bundle, "bundle.json"), "{ \"format\": 1 }");
        File.WriteAllText(Path.Combine(bundle, "grammars", "toml.so"), "elf");
        Directory.CreateDirectory(Path.Combine(bundle, ".work"));
        var servers = Path.Combine(box.Root, "servers");
        Directory.CreateDirectory(servers);
        File.WriteAllText(Path.Combine(servers, "cscode-server-linux-x64.tar.gz"), "tar");
        File.WriteAllText(Path.Combine(servers, "manifest.json"), JsonSerializer.Serialize(new
        {
            version = "test",
            bundles = new[] { new { flavour = "self-contained", rid = "linux-x64", file = "cscode-server-linux-x64.tar.gz", sha256 = new string('a', 64), hash = new string('a', 16) } },
        }));

        Ok(await box.InstallAsync("--from", box.FakePublish(), "--version", "1", "--no-adapters", "--tree-sitter", bundle, "--server-bundles", servers));
        var app = box.CurrentVersionDir;
        Assert.True(File.Exists(Path.Combine(app, "tree-sitter", "bundle.json")));
        Assert.True(File.Exists(Path.Combine(app, "tree-sitter", "grammars", "toml.so")));
        Assert.False(Directory.Exists(Path.Combine(app, "tree-sitter", ".work"))); // the build's scratch folder never ships
        // The fake publish has no shell integration: the installer takes the sources' scripts (a real publish carries them).
        foreach (var script in new[] { "cscode.bash", "cscode.fish", "cscode.ps1", "zsh/.zshrc", "zsh/.zshenv", "zsh/.zprofile", "zsh/cscode-integration.zsh" })
            Assert.True(File.Exists(Path.Combine(app, "shellIntegration", script)), script);
        // The Remote module finds the bundles next to the installed app (InstallDir = the app's folder).
        var set = ServerBundleSet.Find(null, app);
        Assert.NotNull(set);
        Assert.Equal(Path.Combine(app, "server-bundles"), set.Directory);

        // --no-tree-sitter / --no-server-bundles ship none, also when the publish carries them.
        Ok(await box.InstallAsync("--from", app, "--version", "2", "--no-adapters", "--no-tree-sitter", "--no-server-bundles"));
        Assert.False(Directory.Exists(Path.Combine(box.CurrentVersionDir, "tree-sitter")));
        Assert.False(Directory.Exists(Path.Combine(box.CurrentVersionDir, "server-bundles")));
    }

    [Fact]
    public async Task Debug_adapters_on_this_machine_are_bundled_where_the_factories_find_them_without_variables()
    {
        using var box = new InstallSandbox();
        var src = Path.Combine(box.Root, "adapters");
        string Touch(string rel, bool executable = false)
        {
            var path = Path.Combine(src, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "#!/bin/sh\n");
            if (executable) File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
            return path;
        }
        var netcoredbg = Touch("netcoredbg/netcoredbg", executable: true);
        Touch("netcoredbg/libdbgshim.so");
        Touch($"js-debug/{JsDebugInstaller.Version}/js-debug/src/dapDebugServer.js");
        Touch($"codelldb/{CodeLldbInstaller.Version}/extension/adapter/codelldb", executable: true);
        File.WriteAllText(Path.Combine(src, "codelldb", CodeLldbInstaller.Version, "VERSION"), CodeLldbInstaller.Version + "\n");
        File.WriteAllText(Path.Combine(src, "codelldb", CodeLldbInstaller.Version, "codelldb-linux-x64.vsix"), "the download itself");

        var run = await box.InstallAsync("--from", box.FakePublish(), "--version", "1", "--no-tree-sitter", "--no-server-bundles",
            "--netcoredbg", netcoredbg, // the executable: its folder is taken
            "--js-debug", Path.Combine(src, "js-debug", JsDebugInstaller.Version), // the folder holding js-debug/
            "--codelldb", Path.Combine(src, "codelldb", CodeLldbInstaller.Version, "extension")); // CodeLLDB's extension folder
        Ok(run);
        Assert.Contains($"debug adapters: netcoredbg js-debug {JsDebugInstaller.Version} CodeLLDB {CodeLldbInstaller.Version}", run.Output);

        // What the installed app's factories look up after the user's own choices (BundledAdapterTests: the order), with InstallDir = its folder.
        using var paths = new TestAppPaths { InstallDir = box.CurrentVersionDir };
        var bundled = Path.Combine(box.CurrentVersionDir, BundledAdapters.FolderName);
        Assert.Equal(Path.Combine(bundled, "netcoredbg", "netcoredbg"), BundledAdapters.NetCoreDbg(paths));
        Assert.Equal(Path.Combine(bundled, "js-debug", JsDebugInstaller.Version, "js-debug", "src", "dapDebugServer.js"), BundledAdapters.JsDebug(paths));
        Assert.Equal(Path.Combine(bundled, "codelldb", CodeLldbInstaller.Version, "extension", "adapter", "codelldb"), BundledAdapters.CodeLldb(paths));
        // The netcoredbg factory has no environment variable: with nothing configured and nothing downloaded, it takes the bundled one.
        Assert.Equal(BundledAdapters.NetCoreDbg(paths), new NetCoreDbgAdapterFactory(new DapPlatform { Paths = paths, Configuration = new TestConfigurationService() }).Locate());
        Assert.True(File.Exists(Path.Combine(bundled, "netcoredbg", "libdbgshim.so")), "netcoredbg's libraries come along");
        Assert.False(File.Exists(Path.Combine(bundled, "codelldb", CodeLldbInstaller.Version, "codelldb-linux-x64.vsix")), "the download archive is not shipped");
    }

    [Fact]
    public async Task An_adapter_of_another_version_or_a_folder_that_is_not_the_adapters_own_is_not_bundled()
    {
        using var box = new InstallSandbox();
        var other = Path.Combine(box.Root, "codelldb-old", "extension", "adapter", "codelldb");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "#!/bin/sh\n");
        File.SetUnixFileMode(other, (UnixFileMode)0b111_101_101);
        File.WriteAllText(Path.Combine(box.Root, "codelldb-old", "VERSION"), "v1.11.0\n");
        var lonely = Path.Combine(box.Root, "bin", "netcoredbg"); // e.g. a netcoredbg in /usr/bin: the whole folder would be copied
        Directory.CreateDirectory(Path.GetDirectoryName(lonely)!);
        File.WriteAllText(lonely, "#!/bin/sh\n");
        File.SetUnixFileMode(lonely, (UnixFileMode)0b111_101_101);
        var wrongJs = Path.Combine(box.Root, "js", "v1.99.0", "js-debug", "src", "dapDebugServer.js");
        Directory.CreateDirectory(Path.GetDirectoryName(wrongJs)!);
        File.WriteAllText(wrongJs, "//");

        var run = await box.InstallAsync("--from", box.FakePublish(), "--version", "1", "--no-tree-sitter", "--no-server-bundles",
            "--netcoredbg", lonely, "--js-debug", wrongJs, "--codelldb", Path.Combine(box.Root, "codelldb-old"));
        Ok(run); // the app still installs; it offers the downloads when a session needs them
        Assert.Contains("CodeLLDB: ", run.Error);
        Assert.Contains("v1.11.0", run.Error);
        Assert.Contains("does not look like netcoredbg's own folder", run.Error);
        Assert.Contains($"is not {JsDebugInstaller.Version}", run.Error);
        Assert.Contains("debug adapters: none", run.Output);
        Assert.False(Directory.Exists(Path.Combine(box.CurrentVersionDir, BundledAdapters.FolderName)));
    }

    [Fact]
    public async Task Bad_arguments_are_refused_before_anything_is_written()
    {
        using var box = new InstallSandbox();
        var before = box.Snapshot();
        var bad = await box.InstallAsync("--from", box.FakePublish(), "--version", "../../evil", "--no-adapters");
        Assert.NotEqual(0, bad.ExitCode);
        Assert.Contains("--version must start with a digit", bad.Error);
        var unknown = await box.InstallAsync("--frm", "x");
        Assert.NotEqual(0, unknown.ExitCode);
        Assert.Contains("unknown argument", unknown.Error);
        Assert.Equal(InstallSandbox.Describe(before), InstallSandbox.Describe(box.Snapshot()));
    }
}

/// <summary>Just enough of the Desktop Entry Specification to read back what the installer wrote.</summary>
[SupportedOSPlatform("linux")]
internal static class DesktopEntry
{
    public static Dictionary<string, Dictionary<string, string>> Parse(string text)
    {
        var groups = new Dictionary<string, Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { groups[line[1..^1]] = current = []; continue; }
            var eq = line.IndexOf('=');
            if (current is null || eq < 0) throw new FormatException("not a desktop entry line: " + line);
            current[line[..eq]] = Unescape(line[(eq + 1)..]);
        }
        return groups;
    }

    /// <summary>String-level escapes (\s \n \t \r \\).</summary>
    private static string Unescape(string value)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 == value.Length) { sb.Append(value[i]); continue; }
            var c = value[++i];
            sb.Append(c switch { 's' => ' ', 'n' => '\n', 't' => '\t', 'r' => '\r', _ => c });
        }
        return sb.ToString();
    }

    /// <summary>The Exec key's arguments: double-quoted arguments may hold reserved characters, with \" \` \$ \\ escaped inside quotes.</summary>
    public static List<string> ExecArguments(string exec)
    {
        var args = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        var any = false;
        for (var i = 0; i < exec.Length; i++)
        {
            var c = exec[i];
            if (quoted)
            {
                if (c == '"') quoted = false;
                else if (c == '\\' && i + 1 < exec.Length && "\"`$\\".Contains(exec[i + 1])) sb.Append(exec[++i]);
                else sb.Append(c);
            }
            else if (c == '"') { quoted = true; any = true; }
            else if (c == ' ') { if (any || sb.Length > 0) args.Add(sb.ToString()); sb.Clear(); any = false; }
            else sb.Append(c);
        }
        if (any || sb.Length > 0) args.Add(sb.ToString());
        return args.Select(a => a.Replace("%%", "%")).ToList();
    }

    public static string? Validator() => File.Exists("/usr/bin/desktop-file-validate") ? "/usr/bin/desktop-file-validate" : null;
}
