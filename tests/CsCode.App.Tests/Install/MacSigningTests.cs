using System.Diagnostics;
using System.Runtime.Versioning;
using System.Xml.Linq;
using CsCode.Dap.Adapters;
using Xunit;

namespace CsCode.App.Tests.Install;

/// <summary>
/// tools/package-macos.sh's distribution path (audit pkg-2): Developer ID signing with the hardened runtime and notarization. No Apple
/// credentials exist here, so codesign, xcrun (notarytool, stapler) and spctl are recording stand-ins (CODESIGN, XCRUN, SPCTL): the tests check
/// what the script asks of them, in which order, and what it does with their answers. The entitlements themselves are checked for real: the
/// app's own apphost runs under the hardened runtime with packaging/macos/cscode.entitlements and fails without them.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacSigningTests : IDisposable
{
    private const string Identity = "Developer ID Application: Test Person (TEAM123456)";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cssign-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _log;

    public MacSigningTests()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "packages with Apple's sips, iconutil, plutil and ditto");
        Directory.CreateDirectory(_root);
        _log = Path.Combine(_root, "tools.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CsCode.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (CsCode.slnx) not found above " + AppContext.BaseDirectory);
    }

    private static string Rid => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64 ? "osx-x64" : "osx-arm64";

    private static string MachO(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy("/usr/bin/true", path);
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        return path;
    }

    /// <summary>A stand-in publish: the apphost, a runtime library and a native library of the app (Mach-O), the managed files.</summary>
    private string Publish()
    {
        var dir = Path.Combine(_root, "publish");
        MachO(Path.Combine(dir, "cscode"));
        MachO(Path.Combine(dir, "cscode-server"));
        MachO(Path.Combine(dir, "libcoreclr.dylib"));
        MachO(Path.Combine(dir, "libSkiaSharp.dylib"));
        File.WriteAllText(Path.Combine(dir, "cscode.dll"), "not really an assembly");
        File.WriteAllText(Path.Combine(dir, "cscode.deps.json"), "{\n  \"targets\": {\n    \".NETCoreApp,Version=v10.0/osx-arm64\": {\n      \"cscode/1.2.3\": {\n");
        return dir;
    }

    /// <summary>A recording stand-in: one line per run, the tool's name and its arguments separated by tabs; then <paramref name="body"/>.</summary>
    private string Tool(string name, string body = "exit 0\n")
    {
        var path = Path.Combine(_root, "tools", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"#!/bin/bash\n{{ printf '%s' '{name}'; for a in \"$@\"; do printf '\\t%s' \"$a\"; done; printf '\\n'; }} >> '{_log}'\n{body}");
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        return path;
    }

    private string[][] Calls() => File.Exists(_log) ? [.. File.ReadAllLines(_log).Select(l => l.Split('\t'))] : [];

    private async Task<(int ExitCode, string Output)> PackageAsync(Dictionary<string, string> env, params string[] args)
    {
        var psi = new ProcessStartInfo("/bin/bash") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])[Path.Combine(RepoRoot(), "tools", "package-macos.sh"), "--from", Publish(), "--no-server-bundles", "--out", Path.Combine(_root, "out"), .. args])
            psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (p.ExitCode, await output + await error);
    }

    private string App => Path.Combine(_root, "out", Rid, "CsCode.app");

    [Fact]
    public async Task A_Developer_ID_signature_signs_every_Mach_O_file_inside_out_with_the_hardened_runtime_a_timestamp_and_the_entitlements()
    {
        var adapters = Path.Combine(_root, "adapters");
        var lldb = Path.Combine(adapters, "codelldb", CodeLldbInstaller.Version);
        MachO(Path.Combine(lldb, "extension", "adapter", "codelldb"));
        MachO(Path.Combine(lldb, "extension", "lldb", "lib", "liblldb.dylib"));
        File.WriteAllText(Path.Combine(lldb, "VERSION"), CodeLldbInstaller.Version + "\n");
        var codesign = Tool("codesign");

        // only the CodeLLDB given here: not the shared cache's netcoredbg or js-debug
        var none = Path.Combine(_root, "none");
        var (exit, output) = await PackageAsync(new() { ["CODESIGN"] = codesign, ["CSCODE_NETCOREDBG"] = none, ["CSCODE_JSDEBUG"] = none },
            "--codelldb", lldb, "--sign-identity", Identity);
        Assert.True(exit == 0, output);
        Assert.Contains("not notarized", output); // said plainly: other Macs refuse it until it is

        var calls = Calls();
        var signs = calls.Where(c => c.Contains("--sign")).ToArray();
        Assert.NotEmpty(signs);
        Assert.All(signs, c =>
        {
            Assert.Equal(Identity, c[Array.IndexOf(c, "--sign") + 1]);
            Assert.Contains("--timestamp", c);
            Assert.Equal("runtime", c[Array.IndexOf(c, "--options") + 1]); // the hardened runtime, which notarization requires of each
            Assert.DoesNotContain("--deep", c); // Apple advises against --deep for signing
        });
        string Target(string[] c) => c[^1];
        string EntitlementsOf(string[] c) => Path.GetFileName(c[Array.IndexOf(c, "--entitlements") + 1]);
        var order = signs.Select(Target).ToList();
        var bundled = Path.Combine(App, "Contents", "Resources", BundledAdapters.FolderName, "codelldb", CodeLldbInstaller.Version, "extension");
        var expected = new[]
        {
            Path.Combine(bundled, "adapter", "codelldb"), Path.Combine(bundled, "lldb", "lib", "liblldb.dylib"), // the adapters first
            Path.Combine(App, "Contents", "MacOS", "libSkiaSharp.dylib"), Path.Combine(App, "Contents", "MacOS", "libcoreclr.dylib"), // libraries
            // and every other file in Contents/MacOS: codesign refuses a bundle with an unsigned file there ("code object is not signed at all
            // In subcomponent: …/Avalonia.Remote.Protocol.dll", seen with the real publish)
            Path.Combine(App, "Contents", "MacOS", "cscode.dll"), Path.Combine(App, "Contents", "MacOS", "cscode.deps.json"),
            Path.Combine(App, "Contents", "MacOS", "cscode-server"), // the other executables after what they load
            App, // the bundle (and its main executable) last
        };
        Assert.True(expected.Length == order.Count, string.Join("\n", order));
        foreach (var path in expected) Assert.Contains(path, order);
        Assert.True(order.IndexOf(expected[0]) < order.IndexOf(expected[2]) && order.IndexOf(expected[1]) < order.IndexOf(expected[3]), string.Join("\n", order));
        Assert.True(expected[2..6].All(p => order.IndexOf(p) < order.IndexOf(expected[6])), string.Join("\n", order));
        Assert.Equal(App, order[^1]);
        Assert.DoesNotContain(Path.Combine(App, "Contents", "MacOS", "cscode"), order); // the main executable: signed with the bundle
        Assert.All(signs.Where(c => Target(c).Contains(BundledAdapters.FolderName, StringComparison.Ordinal)), c => Assert.Equal("debug-adapters.entitlements", EntitlementsOf(c)));
        Assert.All(signs.Where(c => !Target(c).Contains(BundledAdapters.FolderName, StringComparison.Ordinal)), c => Assert.Equal("cscode.entitlements", EntitlementsOf(c)));
        Assert.Contains(calls, c => c is ["codesign", "--verify", "--deep", "--strict", ..] && Target(c) == App); // and checked
        Assert.DoesNotContain(calls, c => c.Contains("-")); // never ad hoc on this path
    }

    [Fact]
    public async Task Notarize_submits_a_zip_of_the_signed_app_staples_the_ticket_checks_Gatekeeper_and_writes_the_zip_to_hand_out()
    {
        var codesign = Tool("codesign");
        var xcrun = Tool("xcrun", """
            if [ "$1 $2" = "notarytool submit" ]; then
              unzip -l "$3" | grep -q 'CsCode.app/Contents/Info.plist' || { echo 'no app in the zip' >&2; exit 9; }
              cat <<'PLIST'
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict><key>id</key><string>11111111-2222-3333-4444-555555555555</string><key>status</key><string>Accepted</string><key>message</key><string>Processing complete</string></dict></plist>
            PLIST
            fi
            exit 0

            """);
        var spctl = Tool("spctl", "echo \"$5: accepted\"; echo 'source=Notarized Developer ID'\n");

        var (exit, output) = await PackageAsync(new() { ["CODESIGN"] = codesign, ["XCRUN"] = xcrun, ["SPCTL"] = spctl },
            "--no-adapters", "--sign-identity", Identity, "--notarize", "--notary-profile", "cscode-notary");
        Assert.True(exit == 0, output);

        var calls = Calls().Where(c => c[0] is "xcrun" or "spctl").ToArray();
        Assert.Equal(4, calls.Length);
        var submit = calls[0];
        Assert.Equal(["xcrun", "notarytool", "submit"], submit[..3]);
        Assert.EndsWith(".zip", submit[3], StringComparison.Ordinal);
        Assert.Equal("cscode-notary", submit[Array.IndexOf(submit, "--keychain-profile") + 1]);
        Assert.Contains("--wait", submit);
        Assert.Equal(["xcrun", "stapler", "staple", App], calls[1]);
        Assert.Equal(["xcrun", "stapler", "validate", App], calls[2]);
        Assert.Equal(["spctl", "--assess", "--type", "execute", "--verbose=4", App], calls[3]);
        Assert.False(File.Exists(submit[3]), "the submission zip is removed");
        var handout = Path.Combine(_root, "out", Rid, $"CsCode-1.2.3-{Rid}.zip");
        Assert.True(File.Exists(handout), output);
        Assert.Contains("Ready to hand out", output);
        // signing came before the submission
        var all = Calls();
        Assert.True(Array.FindLastIndex(all, c => c[0] == "codesign" && c.Contains("--sign")) < Array.FindIndex(all, c => c[0] == "xcrun"));
    }

    [Fact]
    public async Task A_rejected_submission_fails_the_package_with_notarytool_s_log_and_staples_nothing()
    {
        var codesign = Tool("codesign");
        var xcrun = Tool("xcrun", """
            case "$1 $2" in
              "notarytool submit") printf '<?xml version="1.0" encoding="UTF-8"?><plist version="1.0"><dict><key>id</key><string>abc-123</string><key>status</key><string>Invalid</string></dict></plist>' ;;
              "notarytool log") echo '{"issues":[{"path":"CsCode.app/Contents/MacOS/libfoo.dylib","message":"The signature does not include a secure timestamp."}]}' ;;
            esac
            exit 0

            """);
        var spctl = Tool("spctl");
        var (exit, output) = await PackageAsync(new() { ["CODESIGN"] = codesign, ["XCRUN"] = xcrun, ["SPCTL"] = spctl },
            "--no-adapters", "--sign-identity", Identity, "--notarize", "--notary-profile", "p");
        Assert.NotEqual(0, exit);
        Assert.Contains("Invalid", output);
        Assert.Contains("abc-123", output);
        Assert.Contains("does not include a secure timestamp", output); // the log names the file and the reason
        Assert.Contains(Calls(), c => c is ["xcrun", "notarytool", "log", "abc-123", ..]);
        Assert.DoesNotContain(Calls(), c => c is ["xcrun", "stapler", ..]);
        Assert.DoesNotContain(Calls(), c => c[0] == "spctl");
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "out", Rid), "CsCode-*.zip"));
    }

    [Theory]
    [InlineData("--notarize --notary-profile p", "needs --sign-identity")]
    [InlineData("--sign-identity ID --notarize", "needs --notary-profile")]
    [InlineData("--sign-identity ID --no-sign", "contradict")]
    public async Task Contradicting_or_missing_signing_options_are_refused_before_anything_is_built(string args, string message)
    {
        var codesign = Tool("codesign");
        var (exit, output) = await PackageAsync(new() { ["CODESIGN"] = codesign, ["XCRUN"] = Tool("xcrun") }, ["--no-adapters", .. args.Split(' ')]);
        Assert.Equal(2, exit);
        Assert.Contains(message, output);
        Assert.False(Directory.Exists(App));
        Assert.Empty(Calls());
    }

    [Fact]
    public async Task Without_an_identity_the_signature_stays_ad_hoc()
    {
        var (exit, output) = await PackageAsync([], "--no-adapters");
        Assert.True(exit == 0, output);
        var (_, details) = await Run("/usr/bin/codesign", "-dv", App);
        Assert.Contains("Signature=adhoc", details);
    }

    // ------------------------------------------------------------------ the entitlements, for real

    [Fact]
    public void The_entitlements_files_are_valid_property_lists_with_what_the_runtime_and_the_debuggers_need()
    {
        static IReadOnlyList<string> Keys(string file)
        {
            var dict = XDocument.Load(Path.Combine(RepoRoot(), "packaging", "macos", file)).Root!.Element("dict")!.Elements().ToList();
            for (var i = 0; i < dict.Count; i += 2) Assert.Equal("true", dict[i + 1].Name.LocalName);
            return [.. dict.Where(e => e.Name == "key").Select(e => e.Value)];
        }
        Assert.Equal(["com.apple.security.cs.allow-jit", "com.apple.security.cs.disable-library-validation"], Keys("cscode.entitlements"));
        Assert.Equal(["com.apple.security.cs.debugger", "com.apple.security.cs.allow-jit", "com.apple.security.cs.allow-unsigned-executable-memory",
            "com.apple.security.cs.disable-library-validation"], Keys("debug-adapters.entitlements"));
    }

    /// <summary>
    /// The app's own apphost (from this test's output, beside cscode.dll), signed ad hoc with the hardened runtime: with cscode.entitlements it
    /// starts .NET and prints its version; with no entitlements the runtime's libraries are refused (another team's signature), and with library
    /// validation off but no allow-jit CoreCLR cannot start. So both entitlements are needed, and allow-unsigned-executable-memory is not.
    /// </summary>
    [Fact]
    public async Task The_app_runs_under_the_hardened_runtime_with_its_entitlements_and_not_without_them()
    {
        var apphost = Path.Combine(AppContext.BaseDirectory, "cscode");
        Assert.SkipUnless(File.Exists(apphost) && File.Exists(Path.Combine(AppContext.BaseDirectory, "cscode.dll")), "the app's apphost is not in the test output");
        var hardened = Path.Combine(AppContext.BaseDirectory, "cscode-hardened-" + Guid.NewGuid().ToString("N")[..6]); // beside cscode.dll, which it runs
        File.Copy(apphost, hardened);
        try
        {
            async Task<(int Exit, string Output)> SignAndRun(string? entitlements)
            {
                string[] sign = entitlements is null ? ["--force", "--sign", "-", "--options", "runtime", hardened] : ["--force", "--sign", "-", "--options", "runtime", "--entitlements", entitlements, hardened];
                var signed = await Run("/usr/bin/codesign", sign);
                Assert.True(signed.Exit == 0, signed.Output);
                return await Run(hardened, "--version");
            }
            var good = await SignAndRun(Path.Combine(RepoRoot(), "packaging", "macos", "cscode.entitlements"));
            Assert.True(good.Exit == 0, good.Output);
            Assert.Matches(@"^\d+\.\d+\.\d+", good.Output.Trim());

            var none = await SignAndRun(null);
            Assert.NotEqual(0, none.Exit);
            Assert.Contains("different Team IDs", none.Output); // library validation

            var noJit = Path.Combine(_root, "no-jit.entitlements");
            File.WriteAllText(noJit, """
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0"><dict><key>com.apple.security.cs.disable-library-validation</key><true/></dict></plist>
                """);
            var withoutJit = await SignAndRun(noJit);
            Assert.Contains("Failed to create CoreCLR", withoutJit.Output);
        }
        finally { File.Delete(hardened); }
    }

    private static async Task<(int Exit, string Output)> Run(string program, params string[] args)
    {
        var psi = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (p.ExitCode, await output + await error);
    }
}
