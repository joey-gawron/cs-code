using System.Runtime.Versioning;
using System.Xml.Linq;
using Xunit;

namespace CsCode.App.Tests.Install;

/// <summary>
/// The Windows and macOS installers (docs/briefs/app-icon-and-install.md, SHOULD) cannot run here; what can be checked on Linux is: the .icns
/// holds every committed PNG unchanged under the right icon types, the .app bundle has the layout, Info.plist and icon macOS expects, and
/// tools/install.ps1 parses in PowerShell 7 (when a pwsh is available: CSCODE_PWSH or the repository's .scratch/downloads).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PackagingTests
{
    private static readonly (string Type, int Size)[] IcnsTypes =
        [("icp4", 16), ("ic11", 32), ("icp5", 32), ("ic12", 64), ("ic07", 128), ("ic13", 256), ("ic08", 256), ("ic14", 512), ("ic09", 512), ("ic10", 1024)];

    private static string Tool(params string[] parts) => Path.Combine([InstallSandbox.RepoRoot(), "tools", .. parts]);

    private static void Ok(SandboxRun run) => Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}\n{run.All}");

    /// <summary>The (type, payload) entries of an .icns file.</summary>
    private static List<(string Type, byte[] Data)> ReadIcns(byte[] icns)
    {
        Assert.Equal("icns"u8.ToArray(), icns[..4]);
        Assert.Equal(icns.Length, ReadBigEndian(icns, 4));
        var entries = new List<(string, byte[])>();
        for (var at = 8; at < icns.Length;)
        {
            var length = ReadBigEndian(icns, at + 4);
            Assert.True(length >= 8 && at + length <= icns.Length, $"bad entry length {length} at {at}");
            entries.Add((System.Text.Encoding.ASCII.GetString(icns, at, 4), icns[(at + 8)..(at + length)]));
            at += length;
        }
        return entries;
    }

    private static int ReadBigEndian(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    private static void AssertIcnsIsTheArtwork(byte[] icns)
    {
        var entries = ReadIcns(icns);
        Assert.Equal(IcnsTypes.Select(t => t.Type), entries.Select(e => e.Type));
        foreach (var (type, size) in IcnsTypes)
            Assert.Equal(File.ReadAllBytes(Path.Combine(InstallSandbox.RepoRoot(), "assets", "branding", $"cscode-{size}.png")), entries.Single(e => e.Type == type).Data);
    }

    [Fact]
    public async Task The_icns_holds_every_committed_png_unchanged_under_its_icon_type()
    {
        if (!File.Exists("/usr/bin/python3")) Assert.Skip("python3 is not installed");
        using var box = new InstallSandbox();
        var icns = Path.Combine(box.Root, "cscode.icns");
        Ok(await box.RunAsync("/usr/bin/python3", ["-I", Tool("macos", "make-icns.py"), icns]));
        AssertIcnsIsTheArtwork(File.ReadAllBytes(icns));
    }

    [Fact]
    public async Task The_macOS_app_bundle_has_the_layout_Info_plist_and_icon_that_macOS_reads()
    {
        if (!File.Exists("/usr/bin/python3")) Assert.Skip("python3 is not installed");
        using var box = new InstallSandbox();
        var publish = box.FakePublish();
        File.WriteAllText(Path.Combine(publish, "cscode.deps.json"), "{\n  \"targets\": {\n    \".NETCoreApp,Version=v10.0/osx-arm64\": {\n      \"cscode/1.2.3\": {\n");
        var out1 = Path.Combine(box.Root, "out");
        Ok(await box.ScriptAsync(Tool("macos", "make-app-bundle.sh"), ["--from", publish, "--out", out1, "--no-sign"]));
        var app = Path.Combine(out1, "CsCode.app", "Contents");
        Assert.True(File.GetUnixFileMode(Path.Combine(app, "MacOS", "cscode")).HasFlag(UnixFileMode.UserExecute));
        Assert.True(File.Exists(Path.Combine(app, "MacOS", "nested", "data.txt")), "the whole publish is in Contents/MacOS");
        Assert.Equal("APPL????", File.ReadAllText(Path.Combine(app, "PkgInfo")));
        AssertIcnsIsTheArtwork(File.ReadAllBytes(Path.Combine(app, "Resources", "cscode.icns")));

        var dict = XDocument.Load(Path.Combine(app, "Info.plist")).Root!.Element("dict")!.Elements().ToList();
        string Value(string key) => (string)dict.SkipWhile(e => e.Name != "key" || e.Value != key).Skip(1).First();
        Assert.Equal("cscode", Value("CFBundleExecutable"));
        Assert.Equal("cscode", Value("CFBundleIconFile")); // Resources/cscode.icns
        Assert.Equal("CsCode", Value("CFBundleName"));
        Assert.Equal("APPL", Value("CFBundlePackageType"));
        Assert.Equal("1.2.3", Value("CFBundleShortVersionString"));
        Assert.Equal("true", dict.SkipWhile(e => e.Value != "NSHighResolutionCapable").Skip(1).First().Name.LocalName);

        // A rebuild replaces the bundle.
        Ok(await box.ScriptAsync(Tool("macos", "make-app-bundle.sh"), ["--from", publish, "--out", out1, "--no-sign"]));
        Assert.Single(Directory.GetDirectories(out1));
    }

    [Fact]
    public async Task Install_ps1_parses_in_PowerShell_7()
    {
        var pwsh = Pwsh();
        if (pwsh is null) Assert.Skip("no pwsh (tools/fetch-shells.sh pwsh, or CSCODE_PWSH)");
        using var box = new InstallSandbox();
        var script = Tool("install.ps1").Replace("'", "''", StringComparison.Ordinal);
        var run = await box.RunAsync(pwsh, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            $"$errors = $null; $null = [System.Management.Automation.Language.Parser]::ParseFile('{script}', [ref]$null, [ref]$errors); " +
            "$errors | ForEach-Object { $_.ToString() }; exit $errors.Count"],
            new Dictionary<string, string?> { ["POWERSHELL_TELEMETRY_OPTOUT"] = "1", ["POWERSHELL_UPDATECHECK"] = "Off", ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" });
        Assert.True(run.ExitCode == 0, run.All);
    }

    private static string? Pwsh()
    {
        if (Environment.GetEnvironmentVariable("CSCODE_PWSH") is { Length: > 0 } configured && File.Exists(configured)) return configured;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, ".scratch", "downloads", "pwsh", "7.6.7", "extracted", "pwsh");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
