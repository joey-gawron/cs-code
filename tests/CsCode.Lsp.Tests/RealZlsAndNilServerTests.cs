using System.Text;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Manager;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// B3 smoke tests against the two new built-in servers that are installed on this machine: zls (Zig) and nil (Nix). Each runs the REAL
/// server through the built-in definition, the real process factory and the default executable lookup (PATH, then cscode.lsp.searchDirs):
/// initialize, diagnostics, hover, formatting, priority, shutdown, and no process left behind. Every test SKIPS with the reason when the
/// executable is not installed. The servers run with HOME/XDG directories in a temp dir so they never read or write the user's real
/// configuration or caches (zls.json, ~/.cache/zls, the Zig global cache).
/// </summary>
public class RealZlsAndNilServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string? Installed(string command) => ToolResolver.Resolve(command, null, null, null, new LspSettings().SearchDirs);

    private sealed class Session : IAsyncDisposable
    {
        public required LspTestEnv Env { get; init; }
        public required TempDirectory Home { get; init; }
        public required string Tag { get; init; }
        public async ValueTask DisposeAsync()
        {
            await Env.DisposeAsync();
            Home.Dispose();
        }
    }

    /// <summary>A real-server environment where the server <paramref name="id"/> runs with a private HOME and a unique tag in its environment.</summary>
    private static Session Start(string id, string? settingsJson = null)
    {
        var home = new TempDirectory("cscode-smoke-home-");
        var tag = Guid.NewGuid().ToString("N");
        var env = new Dictionary<string, string>
        {
            ["HOME"] = home.Path,
            ["XDG_CONFIG_HOME"] = home.Combine("config"),
            ["XDG_CACHE_HOME"] = home.Combine("cache"),
            ["XDG_DATA_HOME"] = home.Combine("data"),
            ["XDG_STATE_HOME"] = home.Combine("state"),
            ["ZIG_GLOBAL_CACHE_DIR"] = home.Combine("zig-global-cache"),
            ["ZIG_LOCAL_CACHE_DIR"] = home.Combine("zig-local-cache"),
            ["CSCODE_SMOKE_TAG"] = tag, // inherited by every descendant: lets the test find all of them, wherever they were re-parented
        };
        var lspEnv = new LspTestEnv(c =>
        {
            c.Set(LspSettingKeys.InteractiveTimeoutMs, 30_000);
            c.Set(LspSettingKeys.BackgroundTimeoutMs, 60_000);
            c.Set(LspSettingKeys.InitializeTimeoutMs, 120_000);
            c.Set(LspSettingKeys.ShutdownTimeoutMs, 5_000);
            var overrides = new Dictionary<string, object> { [id] = new { env } };
            c.Set(LspSettingKeys.LanguageServers, overrides);
        }, realServers: true);
        return new Session { Env = lspEnv, Home = home, Tag = tag };
    }

    /// <summary>Live processes whose environment carries the tag (Linux /proc).</summary>
    private static int[] Tagged(string tag)
    {
        var needle = Encoding.UTF8.GetBytes("CSCODE_SMOKE_TAG=" + tag);
        var found = new List<int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                if (File.ReadAllBytes(Path.Combine(dir, "environ")).AsSpan().IndexOf(needle) >= 0) found.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* exited, or not ours */ }
        }
        return found.ToArray();
    }

    private static async Task<T> UntilAsync<T>(Func<Task<T?>> query, string what, Func<T, bool> accept, LspTestEnv env, int seconds = 60) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        T? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await query().Within(TimeSpan.FromSeconds(40));
            if (last is not null && accept(last)) return last;
            await Task.Delay(250, Ct);
        }
        Assert.Fail($"no usable answer for: {what} (last: {last})\n{env.Dump()}");
        return null!;
    }

    private static string HoverText(Hover h) => string.Join("\n", h.Contents.Select(c => c.Value));

    private static async Task AssertBelowNormalAndShutDownCleanlyAsync(Session s, string id)
    {
        var info = s.Env.Manager.Servers.Single(x => x.Id == id);
        var pid = info.ProcessId!.Value;
        if (OperatingSystem.IsLinux())
        {
            Assert.True(NativePriority.GetProcessNice(pid) >= 10, $"{id} nice = {NativePriority.GetProcessNice(pid)}: language servers run below normal priority");
            Assert.Contains(pid, Tagged(s.Tag));
        }

        await s.Env.Manager.StopAllAsync(Ct);
        Assert.Equal(LanguageServerState.Stopped, s.Env.Manager.Servers.Single(x => x.Id == id).State);
        if (OperatingSystem.IsLinux())
            await TestHelpers.EventuallyAsync(() => Tagged(s.Tag).Length == 0,
                () => $"processes of {id} still running after shutdown: {string.Join(",", Tagged(s.Tag))}", TimeSpan.FromSeconds(15));
        else
            await TestHelpers.EventuallyAsync(() => !ProcessAlive(pid), $"{id} process exited", TimeSpan.FromSeconds(15));
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    // =================================================================================================================
    // zls
    // =================================================================================================================

    private const string MainZig = """
        const std = @import("std");

        /// Adds two numbers.
        pub fn add(a: u32, b: u32) u32 {
            return a + b;
        }

        pub fn main() void {
            const sum = add(1, 2);
            _ = sum;
            const broken = ;
        }

        """;

    [Fact]
    public async Task Zls_initializes_reports_diagnostics_answers_hover_formats_and_leaves_nothing_behind()
    {
        if (Installed("zls") is null) { Assert.Skip("zls is not installed (not on PATH nor in the cscode.lsp.searchDirs directories)"); return; }
        await using var s = Start("zls");
        // No build.zig on purpose: zls would run the project's build script through `zig build`, which a smoke test has no business doing.
        var main = s.Env.Open("src/main.zig", MainZig, "zig");
        var info = await s.Env.WaitForStateAsync("zls", LanguageServerState.Running, TimeSpan.FromSeconds(90));
        Assert.Equal("zls", info.ServerName);

        // diagnostics: the syntax error on `const broken = ;` (line 10)
        await TestHelpers.EventuallyAsync(() => s.Env.Diagnostics.Get(main.Uri).Any(d => d.Range.Start.Line == 10 && d.Severity == DiagnosticSeverity.Error),
            () => "zls syntax-error diagnostic on line 10\n" + s.Env.Dump(), TimeSpan.FromSeconds(60));

        // hover over the call `add(1, 2)`: the signature and the doc comment
        var call = main.PositionAt(main.GetText().IndexOf("add(1, 2)", StringComparison.Ordinal) + 1);
        var hover = await UntilAsync(async () => await s.Env.Provider<IHoverProvider>(main).ProvideHoverAsync(main, call, Ct), "hover over add",
            h => HoverText(h).Contains("fn add(a: u32, b: u32) u32", StringComparison.Ordinal), s.Env);
        Assert.Contains("Adds two numbers.", HoverText(hover));

        // formatting (zig fmt rules) on a well-formed file
        var messy = s.Env.Open("src/fmt.zig", "pub fn   f( )  void {\nreturn;\n}\n", "zig");
        var edits = await UntilAsync(async () => await s.Env.Provider<IDocumentFormattingProvider>(messy).FormatDocumentAsync(messy, new FormattingOptions(4, true), Ct),
            "format src/fmt.zig", e => e.Count > 0, s.Env);
        Assert.True(messy.ApplyEdits(edits));
        Assert.Equal("pub fn f() void {\n    return;\n}\n", messy.GetText());

        await AssertBelowNormalAndShutDownCleanlyAsync(s, "zls");
    }

    // =================================================================================================================
    // nil
    // =================================================================================================================

    private const string DefaultNix = """
        let
          greeting = "hello";
          unused = 1;
        in
        {
          message = greeting;
        }

        """;

    [Fact]
    public async Task Nil_initializes_reports_diagnostics_answers_hover_formats_with_nixfmt_and_leaves_nothing_behind()
    {
        if (Installed("nil") is null) { Assert.Skip("nil is not installed (not on PATH nor in the cscode.lsp.searchDirs directories)"); return; }
        await using var s = Start("nix");
        // default.nix (not flake.nix): a flake would make nil evaluate inputs through `nix`, possibly over the network.
        var doc = s.Env.Open("default.nix", DefaultNix, "nix");
        var info = await s.Env.WaitForStateAsync("nix", LanguageServerState.Running, TimeSpan.FromSeconds(90));
        Assert.Equal("nil", info.ServerName); // the definition's own command, not the nixd alternative

        // diagnostics: the unused let binding (line 2)
        await TestHelpers.EventuallyAsync(() => s.Env.Diagnostics.Get(doc.Uri).Any(d => d.Range.Start.Line == 2 && d.Message.Contains("unused", StringComparison.OrdinalIgnoreCase)),
            () => "nil unused-binding diagnostic on line 2\n" + s.Env.Dump(), TimeSpan.FromSeconds(60));

        // hover over the use of `greeting`: nil infers the type
        var use = doc.PositionAt(doc.GetText().IndexOf("= greeting", StringComparison.Ordinal) + 3);
        var hover = await UntilAsync(async () => await s.Env.Provider<IHoverProvider>(doc).ProvideHoverAsync(doc, use, Ct), "hover over greeting",
            h => HoverText(h).Contains("string", StringComparison.Ordinal), s.Env);
        Assert.Contains("greeting", HoverText(hover));

        // formatting goes through the nixfmt command the built-in definition configures (skipped when nixfmt is missing)
        if (Installed("nixfmt") is not null)
        {
            var messy = s.Env.Open("pkgs/messy.nix", "{a=1;   b =   [1 2];}\n", "nix");
            var edits = await UntilAsync(async () => await s.Env.Provider<IDocumentFormattingProvider>(messy).FormatDocumentAsync(messy, new FormattingOptions(2, true), Ct),
                "format pkgs/messy.nix with nixfmt", e => e.Count > 0, s.Env);
            Assert.True(messy.ApplyEdits(edits));
            Assert.Contains("a = 1;", messy.GetText());
            Assert.Contains("b = [", messy.GetText());
        }

        await AssertBelowNormalAndShutDownCleanlyAsync(s, "nix");
    }
}
