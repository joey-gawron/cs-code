using System.Diagnostics;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Xunit;

namespace CsCode.App.Tests.Install;

/// <summary>
/// tools/package-macos.sh on a Mac, with a stand-in publish (no dotnet publish, no server bundles): the bundle is signed ad hoc and verifies,
/// its Info.plist declares what Finder and Launch Services read, its .icns is iconutil's, and --install puts the app and a `cscode` command
/// into the directories it is given (never the user's own) that passes every argument to the app. Linux checks the same bundle's layout
/// without Apple's tools (<see cref="PackagingTests"/>).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacPackagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csmac-" + Guid.NewGuid().ToString("N")[..8]);

    public MacPackagingTests()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "packages with Apple's sips, iconutil, plutil and codesign");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Publish()
    {
        var dir = Path.Combine(_root, "publish");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cscode"), "#!/bin/sh\nfor a in \"$@\"; do printf 'arg:%s\\n' \"$a\"; done\n");
        File.SetUnixFileMode(Path.Combine(dir, "cscode"), (UnixFileMode)0b111_101_101);
        File.WriteAllText(Path.Combine(dir, "cscode.dll"), "not really an assembly");
        File.WriteAllText(Path.Combine(dir, "cscode.deps.json"), "{\n  \"targets\": {\n    \".NETCoreApp,Version=v10.0/osx-arm64\": {\n      \"cscode/1.2.3\": {\n");
        return dir;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string program, params string[] args)
    {
        var psi = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (p.ExitCode, await output + await error);
    }

    private Task<(int ExitCode, string Output)> PackageAsync(params string[] args) =>
        RunAsync("/bin/bash", [Path.Combine(RepoRoot(), "tools", "package-macos.sh"), "--from", Publish(), "--no-server-bundles",
            "--out", Path.Combine(_root, "out"), .. args]);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CsCode.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (CsCode.slnx) not found above " + AppContext.BaseDirectory);
    }

    private static string Rid => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64 ? "osx-x64" : "osx-arm64";

    [Fact]
    public async Task The_bundle_is_signed_ad_hoc_and_declares_its_documents_minimum_macOS_and_icon()
    {
        var (exit, output) = await PackageAsync();
        Assert.True(exit == 0, output);
        var app = Path.Combine(_root, "out", Rid, "CsCode.app");
        var verify = await RunAsync("/usr/bin/codesign", "--verify", "--deep", "--strict", app);
        Assert.True(verify.ExitCode == 0, verify.Output);
        Assert.Contains("Signature=adhoc", (await RunAsync("/usr/bin/codesign", "-dv", app)).Output);

        var dict = XDocument.Load(Path.Combine(app, "Contents", "Info.plist")).Root!.Element("dict")!.Elements().ToList();
        XElement Value(string key) => dict.SkipWhile(e => e.Name != "key" || e.Value != key).Skip(1).First();
        Assert.Equal("local.cscode.CsCode", Value("CFBundleIdentifier").Value);
        Assert.Equal("1.2.3", Value("CFBundleShortVersionString").Value);
        Assert.Equal("12.0", Value("LSMinimumSystemVersion").Value);
        Assert.Equal("true", Value("NSHighResolutionCapable").Name.LocalName);
        var types = Value("CFBundleDocumentTypes").Descendants("string").Select(s => s.Value).ToList();
        Assert.Contains("public.source-code", types);
        Assert.Contains("public.plain-text", types);
        Assert.Contains("public.folder", types);
        Assert.Contains("Alternate", types); // never the default app of a type

        // iconutil's .icns: it reads back into an .iconset with every size up to 512 pt @2x (1024 px).
        var iconset = Path.Combine(_root, "back.iconset");
        var back = await RunAsync("/usr/bin/iconutil", "-c", "iconset", "-o", iconset, Path.Combine(app, "Contents", "Resources", "cscode.icns"));
        Assert.True(back.ExitCode == 0, back.Output);
        foreach (var name in new[] { "16x16", "16x16@2x", "32x32", "32x32@2x", "128x128", "128x128@2x", "256x256", "256x256@2x", "512x512", "512x512@2x" })
            Assert.True(File.Exists(Path.Combine(iconset, $"icon_{name}.png")), $"icon_{name}.png");
    }

    [Fact]
    public async Task Install_puts_the_app_and_a_cscode_command_that_passes_every_argument_where_it_is_told()
    {
        var apps = Path.Combine(_root, "My Apps");
        var bin = Path.Combine(_root, "bin");
        var (exit, output) = await PackageAsync("--install", "--app-dir", apps, "--bin-dir", bin);
        Assert.True(exit == 0, output);
        Assert.True(File.Exists(Path.Combine(apps, "CsCode.app", "Contents", "MacOS", "cscode.dll")));
        Assert.Contains("is not on your PATH", output);
        var run = await RunAsync(Path.Combine(bin, "cscode"), "a file.txt:3:4", "--wait", "it's");
        Assert.Equal("arg:a file.txt:3:4\narg:--wait\narg:it's\n", run.Output);
    }

    [Fact]
    public async Task Install_refuses_a_cscode_it_did_not_write_before_installing_anything()
    {
        var apps = Path.Combine(_root, "apps");
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "cscode"), "#!/bin/sh\necho someone else's\n");
        var (exit, output) = await PackageAsync("--install", "--app-dir", apps, "--bin-dir", bin);
        Assert.NotEqual(0, exit);
        Assert.Contains("--force", output);
        Assert.False(Directory.Exists(Path.Combine(apps, "CsCode.app")), "nothing is installed");
        Assert.Equal("#!/bin/sh\necho someone else's\n", File.ReadAllText(Path.Combine(bin, "cscode")));
    }
}
