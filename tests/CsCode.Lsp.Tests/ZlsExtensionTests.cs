using System.Text;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Extensions.Zls;
using CsCode.Lsp.Protocol;
using CsCode.Lsp.Tests.Harness;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// zls's server extension (docs/ZIG.md): the zig/zls version rule and message, zls's own complaint held back for CsCode's single message, the settings it
/// registers; and against the REAL zls (skips without it, tools/fetch-zig.sh): a mismatch reported once, and build-on-save diagnostics from
/// <c>zig build</c> in a <c>zig init</c> project.
/// </summary>
public class ZlsExtensionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("0.17.0", "0.17.0", true)]
    [InlineData("0.17.0", "0.17.1", true)]
    [InlineData("0.17.0", "0.16.0", false)]
    [InlineData("0.16.0", "0.17.0-dev.12+abcdef", false)]
    [InlineData("0.18.0-dev.40+1a2b3c", "0.18.0-dev.52+4d5e6f", true)]
    [InlineData("1.0.0", "0.17.0", false)]
    [InlineData("weird", "0.17.0", true)] // not judged
    public void Zls_serves_the_zig_of_its_own_major_and_minor_version(string zls, string zig, bool compatible) =>
        Assert.Equal(compatible, ZlsExtension.Compatible(zls, zig));

    [Theory]
    [InlineData("ZLS '0.17.0' should be used with a Zig '0.17' release but found Zig '0.16.0'.", true)]
    [InlineData("ZLS '0.16.0' does not support Zig '0.17.0'. A ZLS '0.17' release should be used instead.", true)]
    [InlineData("zig executable could not be found", false)]
    public void Zls_own_version_complaint_is_recognised(string text, bool expected) => Assert.Equal(expected, ZlsExtension.IsVersionMessage(text));

    [Fact]
    public void The_mismatch_message_names_both_versions_and_executables_and_the_way_out()
    {
        var m = ZlsExtension.Mismatch("0.16.0", "/opt/zls/zls", "0.17.0", "/opt/zig/zig");
        Assert.Equal("zls and Zig do not match: zls 0.16.0 (/opt/zls/zls) is built for Zig 0.16, but the zig it runs is 0.17.0 (/opt/zig/zig). " +
            "Install the zls release for Zig 0.17 (github.com/zigtools/zls/releases; tools/fetch-zig.sh pins a matching pair), or set 'zls.zig_exe_path' to a Zig 0.16.", m);
    }

    [Fact]
    public void Build_on_save_is_registered_on_by_default_and_the_zig_path_has_no_default()
    {
        var props = ZlsExtension.Properties().ToDictionary(p => p.Key);
        Assert.True(props[ZlsExtension.BuildOnSaveSetting].Default!.Value.GetBoolean());
        Assert.Null(props[ZlsExtension.ZigExePathSetting].Default);
    }

    /// <summary>The built-in zls definition served by a fake server (reports version 0.17.0) under the extension; its zig answers <paramref name="zigVersion"/>.</summary>
    private static (LspTestEnv Env, List<string> Runs, TempDirectory Dir) Make(string zigVersion)
    {
        var dir = new TempDirectory("zlscheck-");
        var zig = dir.WriteFile("other/zig", "");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(zig, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var runs = new List<string>();
        var extension = new ZlsExtension
        {
            Run = (exe, args, _) =>
            {
                lock (runs) runs.Add($"{Path.GetFileName(exe)} {args}");
                return Task.FromResult<string?>(args == "version" ? zigVersion : "0.17.0");
            },
        };
        var env = new LspTestEnv(c => c.Set(ZlsExtension.ZigExePathSetting, zig), builtins: true, extensions: [extension]);
        env.Connections.OnConnect = (server, _) => server.Version = "0.17.0";
        return (env, runs, dir);
    }

    private static async Task<FakeLspServer> StartAsync(LspTestEnv env, int index = 0)
    {
        if (index == 0) env.Open("main.zig", "", "zig");
        var server = await env.Connections.WaitForServerAsync(index);
        await server.WaitForAsync(LspMethods.Initialized);
        await env.WaitForStateAsync(ZlsExtension.ServerId, LanguageServerState.Running);
        return server;
    }

    private static Task ComplainAsync(FakeLspServer server, string text) => server.NotifyAsync("window/showMessage", new { type = 2, message = text });

    private const string Complaint = "ZLS '0.17.0' should be used with a Zig '0.17' release but found Zig '0.16.0'.";

    [Fact]
    public async Task A_running_zls_with_another_zig_gets_one_warning_and_zls_own_complaint_is_held_back()
    {
        var (env, runs, dir) = Make("0.16.0");
        using var _ = dir;
        await using var __ = env;
        var server = await StartAsync(env);
        await ComplainAsync(server, Complaint);
        await ComplainAsync(server, "something else"); // not zls's version complaint: shown as usual
        await TestHelpers.EventuallyAsync(() => env.Window.Messages.Count >= 2, () => "the mismatch warning and the other message: " + string.Join(" | ", env.Window.Messages), TimeSpan.FromSeconds(10));
        // the same pair again (a restart, zls complaining again): nothing more
        await env.Manager.RestartAsync(ZlsExtension.ServerId, Ct);
        var restarted = await StartAsync(env, 1);
        await ComplainAsync(restarted, Complaint);
        await Task.Delay(500, Ct);
        var (severity, message) = Assert.Single(env.Window.Messages, m => !m.Message.EndsWith("something else", StringComparison.Ordinal));
        Assert.Equal(MessageSeverity.Warning, severity);
        Assert.StartsWith("zls and Zig do not match: zls 0.17.0", message, StringComparison.Ordinal);
        Assert.Contains("the zig it runs is 0.16.0", message, StringComparison.Ordinal);
        Assert.Equal("Zig (zls): something else", Assert.Single(env.Window.Messages, m => m.Message.EndsWith("something else", StringComparison.Ordinal)).Message);
        lock (runs) Assert.DoesNotContain(runs, r => r.StartsWith("zls", StringComparison.Ordinal)); // the version zls reported in initialize is used
    }

    [Fact]
    public async Task When_the_versions_match_nothing_is_shown_unless_zls_complained_then_its_own_message_is()
    {
        var (env, runs, dir) = Make("0.17.0");
        using var _ = dir;
        await using var __ = env;
        var server = await StartAsync(env);
        await TestHelpers.EventuallyAsync(() => { lock (runs) return runs.Count > 0; }, "the check ran", TimeSpan.FromSeconds(10));
        await Task.Delay(300, Ct);
        Assert.Empty(env.Window.Messages);

        // a complaint the check cannot confirm (zls looked at another zig, say) is shown as zls said it
        await ComplainAsync(server, "ZLS '0.17.0' does not support Zig '0.16.0'. A ZLS '0.16' release should be used instead.");
        await TestHelpers.EventuallyAsync(() => env.Window.Messages.Count > 0, "zls's own message", TimeSpan.FromSeconds(10));
        Assert.Equal("Zig (zls): ZLS '0.17.0' does not support Zig '0.16.0'. A ZLS '0.16' release should be used instead.", Assert.Single(env.Window.Messages).Message);
    }

    // ------------------------------------------------------------------ real zls

    /// <summary>A real-zls environment with private HOME and caches, zig and zls from <see cref="ZigTools"/> (zls found through a search dir).</summary>
    private static LspTestEnv RealEnv(TempDirectory home, string zig, bool buildOnSave)
    {
        var env = new Dictionary<string, string>
        {
            ["HOME"] = home.Path,
            ["XDG_CONFIG_HOME"] = home.Combine("config"),
            ["XDG_CACHE_HOME"] = home.Combine("cache"),
            ["ZIG_GLOBAL_CACHE_DIR"] = home.Combine("zig-global-cache"),
        };
        return new LspTestEnv(c =>
        {
            c.Set(LspSettingKeys.InteractiveTimeoutMs, 30_000);
            c.Set(LspSettingKeys.BackgroundTimeoutMs, 60_000);
            c.Set(LspSettingKeys.InitializeTimeoutMs, 120_000);
            c.Set(LspSettingKeys.ShutdownTimeoutMs, 5_000);
            c.Set(LspSettingKeys.SearchDirs, new[] { Path.GetDirectoryName(ZigTools.FindZls()!)! });
            c.Set(LspSettingKeys.LanguageServers, new Dictionary<string, object> { ["zls"] = new { env } });
            c.Set(ZlsExtension.ZigExePathSetting, zig);
            // the app's configuration service answers with the registered default (on); the test double answers only what is set
            c.Set(ZlsExtension.BuildOnSaveSetting, buildOnSave);
            // zls asks for its whole section ("zls"): the app's configuration service answers with the object its dotted keys make, the test
            // double only with what was set under that key
            c.Set("zls", new Dictionary<string, object> { ["zig_exe_path"] = zig, ["enable_build_on_save"] = buildOnSave });
        }, realServers: true);
    }

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task Real_zls_with_a_zig_of_another_version_gets_exactly_one_message_naming_both_versions()
    {
        Assert.SkipWhen(ZigTools.ZlsSkipReason() is not null, ZigTools.ZlsSkipReason() ?? "");
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in zig is a shell script");
        using var home = new TempDirectory("cscode-zls-home-");
        // a zig that says it is 0.16.0 (zig version and zig env) and is the real one otherwise
        var real = ZigTools.FindZig()!;
        var fake = home.WriteFile("old-zig/zig", $"""
            #!/bin/sh
            case "$1" in
              version) echo 0.16.0 ;;
              env) "{real}" env | sed 's/\.version = "[^"]*"/.version = "0.16.0"/' ;;
              *) exec "{real}" "$@" ;;
            esac
            """);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var env = RealEnv(home, fake, buildOnSave: false);
        env.Open("main.zig", "pub fn main() void {}\n", "zig");
        var info = await env.WaitForStateAsync("zls", LanguageServerState.Running, TimeSpan.FromSeconds(90));
        await TestHelpers.EventuallyAsync(() => env.Window.Messages.Any(m => m.Message.Contains("do not match", StringComparison.Ordinal)),
            () => "the mismatch message\n" + string.Join("\n", env.Window.Messages) + "\n" + env.Dump(), TimeSpan.FromSeconds(60));
        await Task.Delay(2000, Ct); // zls's own complaint, if any, has arrived by now
        // only version messages count: the stand-in zig also makes zls say "zig executable could not be found" (it reads zig env differently)
        var zigMessages = env.Window.Messages.Where(m => m.Message.Contains("do not match", StringComparison.Ordinal) || ZlsExtension.IsVersionMessage(m.Message)).ToList();
        TestContext.Current.SendDiagnosticMessage("[zls] serverInfo " + info.ServerVersion + "; messages: " + string.Join(" | ", env.Window.Messages));
        var single = Assert.Single(zigMessages);
        Assert.Equal(MessageSeverity.Warning, single.Severity);
        Assert.Contains($"zls {info.ServerVersion}", single.Message, StringComparison.Ordinal);
        Assert.Contains("the zig it runs is 0.16.0", single.Message, StringComparison.Ordinal);
        await env.Manager.StopAllAsync(Ct);
    }

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task Real_zls_reports_the_compilers_errors_on_save_in_a_build_zig_project()
    {
        Assert.SkipWhen(ZigTools.ZlsSkipReason() is not null, ZigTools.ZlsSkipReason() ?? "");
        using var home = new TempDirectory("cscode-zls-home-");
        var zig = ZigTools.FindZig()!;
        await using var env = RealEnv(home, zig, buildOnSave: true);
        var init = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(zig, "init") { WorkingDirectory = env.Root, RedirectStandardError = true, UseShellExecute = false })!;
        await init.WaitForExitAsync(Ct);
        Assert.Equal(0, init.ExitCode);
        // a type error zls's own analysis does not report: only the compiler (zig build) finds it
        var path = Path.Combine(env.Root, "src", "main.zig");
        var text = File.ReadAllText(path).Replace("pub fn main(init: std.process.Init) !void {", "pub fn main(init: std.process.Init) !void {\n    const wrong: u32 = \"text\";\n    _ = wrong;", StringComparison.Ordinal);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        var doc = env.Open("src/main.zig", text, "zig");
        await env.WaitForStateAsync("zls", LanguageServerState.Running, TimeSpan.FromSeconds(90));
        env.Documents.NotifySaved(doc);
        var line = text.Split('\n').ToList().FindIndex(l => l.Contains("const wrong", StringComparison.Ordinal));
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Any(d => d.Range.Start.Line == line && d.Message.Contains("expected type", StringComparison.Ordinal)),
            () => "a build-on-save error on the const line\n" + string.Join("\n", env.Diagnostics.Get(doc.Uri).Select(d => $"{d.Range.Start.Line}: {d.Message} ({d.Source})")) + "\n" + env.Dump(),
            TimeSpan.FromSeconds(120));
        TestContext.Current.SendDiagnosticMessage("[zls] build-on-save: " + string.Join(" | ", env.Diagnostics.Get(doc.Uri).Select(d => $"{d.Range.Start.Line}: {d.Message} ({d.Source})")));
        await env.Manager.StopAllAsync(Ct);
    }
}
