using System.Diagnostics;
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
/// T2/T11: the .NET part of a language server's launch environment (<see cref="DotnetLaunchEnvironment"/>). The rules on dictionaries and fake
/// dotnet installations, the real process factory with CsCode's own environment changed for the duration of a test (the suite runs serially), and
/// the two real cases from the triage: csharp-ls installed with <c>dotnet tool install -g</c> started without DOTNET_ROOT and with a PATH that has no
/// dotnet (the installed app started from the desktop menu), and nixpkgs' marksman, whose wrapper sets its own DOTNET_ROOT, started by a CsCode
/// that inherited <c>DOTNET_ROOT_&lt;ARCH&gt;</c> from <c>dotnet run</c> / <c>dotnet test</c>.
/// </summary>
public class DotnetLaunchEnvironmentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Root = DotnetLaunchEnvironment.RootVariable;
    private static string Arch => DotnetLaunchEnvironment.ArchRootVariable;

    private static LanguageServerDefinition Def(string command, IReadOnlyDictionary<string, string>? env = null) =>
        new("t", "T", DocumentSelector.ForLanguage("t"), command, [], Env: env);

    /// <summary>A fake dotnet installation: <c>{root}/dotnet</c> (executable) and <c>{root}/host/fxr/9.9.9/</c>.</summary>
    private static string FakeDotnetRoot(TempDirectory dir, string name = "dotnet-root")
    {
        var root = dir.Combine(name);
        Directory.CreateDirectory(Path.Combine(root, "host", "fxr", "9.9.9"));
        Executable(Path.Combine(root, "dotnet"), "#!/bin/sh\nexit 0\n");
        return root;
    }

    private static string Executable(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>An apphost-shaped server: <c>{dir}/srv</c> (a script that sleeps, standing in for the native host) with <c>srv.dll</c> beside it.</summary>
    private static string FakeDotnetServer(TempDirectory dir)
    {
        var exe = Executable(dir.Combine("server", "srv"), "#!/bin/sh\nexec sleep 30\n");
        File.WriteAllText(dir.Combine("server", "srv.dll"), "");
        return exe;
    }

    private static Dictionary<string, string?> Env(params (string Key, string? Value)[] vars) => vars.ToDictionary(v => v.Key, v => v.Value);

    // ---- rule 1: the arch-specific variable ------------------------------------------------------------------------------------------------

    [Fact]
    public void An_arch_variable_alone_becomes_DOTNET_ROOT()
    {
        var env = Env((Arch, "/opt/dotnet"), ("PATH", "/usr/bin"));
        DotnetLaunchEnvironment.Apply(env, Def("anything"), "/usr/bin/true", null, [], null);
        Assert.Equal("/opt/dotnet", env[Root]);
        Assert.False(env.ContainsKey(Arch), "the arch variable outranks a wrapper's DOTNET_ROOT and must not reach the server");
    }

    [Fact]
    public void An_arch_variable_equal_to_DOTNET_ROOT_is_dropped_so_a_wrapper_can_set_its_own()
    {
        var env = Env((Arch, "/opt/dotnet/"), (Root, "/opt/dotnet"));
        DotnetLaunchEnvironment.Apply(env, Def("marksman"), "/usr/bin/true", null, [], null);
        Assert.Equal("/opt/dotnet", env[Root]);
        Assert.False(env.ContainsKey(Arch));
    }

    [Fact]
    public void An_arch_variable_naming_a_symlink_to_DOTNET_ROOT_counts_as_the_same_directory()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("symlinked directories: Unix only"); return; }
        using var dir = new TempDirectory("cscode-dotnet-");
        var root = FakeDotnetRoot(dir);
        var link = dir.Combine("wrapped");
        Directory.CreateSymbolicLink(link, root); // nixpkgs: dotnet-sdk-wrapped/share/dotnet -> dotnet-sdk/share/dotnet
        var env = Env((Arch, link), (Root, root));
        DotnetLaunchEnvironment.Apply(env, Def("x"), "/usr/bin/true", null, [], null);
        Assert.Equal(root, env[Root]);
        Assert.False(env.ContainsKey(Arch));
    }

    [Fact]
    public void Different_arch_and_generic_roots_are_a_deliberate_set_up_and_left_alone()
    {
        var env = Env((Arch, "/opt/dotnet-x64"), (Root, "/opt/dotnet"));
        DotnetLaunchEnvironment.Apply(env, Def("csharp-ls"), "/usr/bin/true", null, [], null);
        Assert.Equal("/opt/dotnet-x64", env[Arch]);
        Assert.Equal("/opt/dotnet", env[Root]);
    }

    [Theory]
    [InlineData("DOTNET_ROOT")]
    [InlineData("arch")]
    public void A_definition_env_that_sets_either_variable_is_never_changed(string variable)
    {
        var name = variable == "arch" ? Arch : Root;
        using var dir = new TempDirectory("cscode-dotnet-");
        var root = FakeDotnetRoot(dir);
        var env = Env((name, "/mine"), ("PATH", root));
        if (name == Root) env[Arch] = "/inherited";
        DotnetLaunchEnvironment.Apply(env, Def("csharp-ls", new Dictionary<string, string> { [name] = "/mine" }), "/x/csharp-ls", null, [root], null);
        Assert.Equal("/mine", env[name]);
        if (name == Root) Assert.Equal("/inherited", env[Arch]);
        else Assert.False(env.ContainsKey(Root), "nothing is added next to the user's own choice");
    }

    // ---- rule 2: DOTNET_ROOT for a .NET server ---------------------------------------------------------------------------------------------

    [Fact]
    public void An_unset_DOTNET_ROOT_is_provided_from_the_dotnet_on_the_servers_PATH_through_its_symlinks()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("symlinked dotnet: Unix only"); return; }
        using var dir = new TempDirectory("cscode-dotnet-");
        var root = FakeDotnetRoot(dir);
        var bin = dir.Combine("profile-bin");
        Directory.CreateDirectory(bin);
        File.CreateSymbolicLink(Path.Combine(bin, "dotnet"), Path.Combine(root, "dotnet")); // ~/.nix-profile/bin/dotnet -> store
        var server = FakeDotnetServer(dir);
        var env = Env(("PATH", "/nonexistent:" + bin));
        var log = new List<string>();
        DotnetLaunchEnvironment.Apply(env, Def("srv"), server, null, [], log.Add);
        Assert.Equal(root, env[Root]);
        Assert.Contains(log, l => l.StartsWith("[client] srv is a .NET program", StringComparison.Ordinal) && l.Contains(root, StringComparison.Ordinal));
    }

    [Fact]
    public void The_search_dirs_are_tried_after_PATH_and_a_dotnet_that_is_no_installation_is_passed_over()
    {
        using var dir = new TempDirectory("cscode-dotnet-");
        var root = FakeDotnetRoot(dir);
        var bogus = Executable(dir.Combine("bogus", "dotnet"), "#!/bin/sh\n"); // a wrapper script with no host/fxr beside it
        var server = FakeDotnetServer(dir);
        var env = Env(("PATH", Path.GetDirectoryName(bogus)!));
        DotnetLaunchEnvironment.Apply(env, Def("srv"), server, [dir.Combine("empty"), root], [], null);
        Assert.Equal(root, env[Root]);
    }

    [Fact]
    public void Without_a_dotnet_on_PATH_or_in_the_search_dirs_the_fallback_roots_are_used_then_nothing()
    {
        using var dir = new TempDirectory("cscode-dotnet-");
        var root = FakeDotnetRoot(dir, ".dotnet");
        var server = FakeDotnetServer(dir);

        var env = Env(("PATH", "/nonexistent"));
        DotnetLaunchEnvironment.Apply(env, Def("srv"), server, null, [dir.Combine("missing"), root], null);
        Assert.Equal(root, env[Root]);

        var none = Env(("PATH", "/nonexistent"));
        var log = new List<string>();
        DotnetLaunchEnvironment.Apply(none, Def("srv"), server, null, [dir.Combine("missing")], log.Add);
        Assert.False(none.ContainsKey(Root));
        Assert.Contains(log, l => l.Contains("no dotnet was found", StringComparison.Ordinal));
    }

    // ---- lsp-core-5: the host's own lookup and the server's framework ------------------------------------------------------------------

    /// <summary>A fake installation with shared frameworks: <c>{root}/shared/Microsoft.NETCore.App/&lt;version&gt;/</c> per version.</summary>
    private static string FakeDotnetRootWith(TempDirectory dir, string name, params string[] runtimeVersions)
    {
        var root = FakeDotnetRoot(dir, name);
        foreach (var v in runtimeVersions) Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", v));
        return root;
    }

    /// <summary>An apphost server whose runtimeconfig.json asks for Microsoft.NETCore.App <paramref name="version"/>.</summary>
    private static string FakeServerNeeding(TempDirectory dir, string version, string? rollForward = null)
    {
        var exe = FakeDotnetServer(dir);
        var roll = rollForward is null ? "" : $"\"rollForward\": \"{rollForward}\", ";
        File.WriteAllText(dir.Combine("server", "srv.runtimeconfig.json"),
            $$"""{ "runtimeOptions": { {{roll}}"tfm": "net8.0", "framework": { "name": "Microsoft.NETCore.App", "version": "{{version}}" } } }""");
        return exe;
    }

    [Fact]
    public void A_global_runtime_the_host_finds_by_itself_is_not_overridden()
    {
        // The reviewer's case: a global .NET 8 (install_location or /usr/share/dotnet), a Nix SDK 10 first on PATH, a net8.0 tool. Setting
        // DOTNET_ROOT to the SDK made the host look only there ("You must install or update .NET").
        using var dir = new TempDirectory("cscode-dotnet-");
        var global = FakeDotnetRootWith(dir, "global", "8.0.11");
        var nix = FakeDotnetRootWith(dir, "nix-sdk", "10.0.0");
        var server = FakeServerNeeding(dir, "8.0.0");
        var env = Env(("PATH", nix));
        var log = new List<string>();
        DotnetLaunchEnvironment.Apply(env, Def("srv"), server, null, [], log.Add, hostDefaultRoot: global);
        Assert.False(env.ContainsKey(Root));
        Assert.Contains(log, l => l.Contains("finds " + global, StringComparison.Ordinal) && l.Contains("Microsoft.NETCore.App 8.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void A_global_runtime_without_the_server_s_framework_is_bypassed_for_one_that_has_it()
    {
        using var dir = new TempDirectory("cscode-dotnet-");
        var global = FakeDotnetRootWith(dir, "global", "6.0.36");
        var first = FakeDotnetRootWith(dir, "first-on-path", "10.0.0");
        var second = FakeDotnetRootWith(dir, "in-search-dirs", "8.0.4");
        var server = FakeServerNeeding(dir, "8.0.0");
        var env = Env(("PATH", first));
        var log = new List<string>();
        DotnetLaunchEnvironment.Apply(env, Def("srv"), server, [second], [], log.Add, hostDefaultRoot: global);
        Assert.Equal(second, env[Root]); // the first dotnet found that HAS 8.0 (Minor roll-forward: 10.0 does not count)
        Assert.Contains(log, l => l.Contains(second, StringComparison.Ordinal) && l.Contains("has it", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_fitting_runtime_anywhere_the_first_dotnet_is_still_used_and_the_log_says_so()
    {
        using var dir = new TempDirectory("cscode-dotnet-");
        var only = FakeDotnetRootWith(dir, "only", "10.0.0");
        var server = FakeServerNeeding(dir, "8.0.0");
        var env = Env(("PATH", only));
        var log = new List<string>();
        DotnetLaunchEnvironment.Apply(env, Def("srv"), server, null, [], log.Add);
        Assert.Equal(only, env[Root]);
        Assert.Contains(log, l => l.Contains("no dotnet found has that framework", StringComparison.Ordinal));
    }

    [Fact]
    public void A_server_that_does_not_say_what_it_needs_leaves_a_global_install_to_the_host()
    {
        using var dir = new TempDirectory("cscode-dotnet-");
        var global = FakeDotnetRootWith(dir, "global", "8.0.11");
        var path = FakeDotnetRootWith(dir, "path", "10.0.0");
        var server = FakeDotnetServer(dir); // srv + srv.dll, no runtimeconfig.json
        var env = Env(("PATH", path));
        DotnetLaunchEnvironment.Apply(env, Def("srv"), server, null, [], null, hostDefaultRoot: global);
        Assert.False(env.ContainsKey(Root));

        var noGlobal = Env(("PATH", path));
        DotnetLaunchEnvironment.Apply(noGlobal, Def("srv"), server, null, [], null, hostDefaultRoot: null); // T2's case is unchanged
        Assert.Equal(path, noGlobal[Root]);
    }

    [Theory]
    [InlineData("8.0.0", null, "8.0.11", true)]
    [InlineData("8.0.0", null, "9.0.0", false)]
    [InlineData("8.0.0", "Major", "9.0.0", true)]
    [InlineData("8.0.0", "LatestMajor", "10.0.0-rc.1", true)]
    [InlineData("8.0.5", "LatestPatch", "8.0.11", true)]
    [InlineData("8.0.5", "LatestPatch", "8.1.0", false)]
    [InlineData("8.0.5", null, "8.0.4", false)]
    public void The_roll_forward_policy_decides_which_installed_runtimes_fit(string required, string? rollForward, string installed, bool fits)
    {
        using var dir = new TempDirectory("cscode-dotnet-");
        var root = FakeDotnetRootWith(dir, "root", installed);
        var server = FakeServerNeeding(dir, required, rollForward);
        var frameworks = DotnetLaunchEnvironment.RequiredFrameworks(server);
        Assert.Equal(fits, DotnetLaunchEnvironment.Satisfies(root, frameworks));
    }

    [Fact]
    public void A_dotnet_tool_shim_s_framework_is_read_from_its_store()
    {
        using var dir = new TempDirectory("cscode-dotnet-");
        var shim = Executable(dir.Combine("tools", "csharp-ls"), "#!/bin/sh\n");
        var any = dir.Combine("tools", ".store", "csharp-ls", "0.16.0", "csharp-ls", "0.16.0", "tools", "net8.0", "any");
        Directory.CreateDirectory(any);
        File.WriteAllText(Path.Combine(any, "CSharpLanguageServer.runtimeconfig.json"),
            """{ "runtimeOptions": { "tfm": "net8.0", "framework": { "name": "Microsoft.NETCore.App", "version": "8.0.0" } } }""");
        var required = Assert.Single(DotnetLaunchEnvironment.RequiredFrameworks(shim));
        Assert.Equal("Microsoft.NETCore.App", required.Name);
        Assert.Equal(new Version(8, 0, 0), required.Version);
    }

    /// <summary>
    /// A tool packed for net8.0 and net9.0, installed as <c>fsautocomplete</c>: both runtimeconfigs in its .store, and a shim whose embedded app path
    /// (as <c>dotnet tool install</c> writes it into the apphost) names <paramref name="runs"/>, or a shim without one (a script) when null.
    /// </summary>
    private static string MultiTargetToolShim(TempDirectory dir, string? runs)
    {
        var shim = dir.Combine("tools", "fsautocomplete");
        Directory.CreateDirectory(Path.GetDirectoryName(shim)!);
        if (runs is null) Executable(shim, "#!/bin/sh\n");
        else
        {
            // apphost bytes around the placeholder: code before, the NUL-terminated relative path, padding and more code after
            var embedded = $".store/fsautocomplete/0.70.0/fsautocomplete/0.70.0/tools/{runs}/any/fsautocomplete.dll";
            byte[] bytes = [.. "\u007fELF\u0002\u0001"u8, .. new byte[300], .. "store.store.dll .store-ish"u8, 0, .. System.Text.Encoding.UTF8.GetBytes(embedded), .. new byte[700], .. "GLIBC_2.34"u8, 0];
            File.WriteAllBytes(shim, bytes);
        }
        foreach (var (tfm, version) in new[] { ("net8.0", "8.0.0"), ("net9.0", "9.0.0") })
        {
            var any = dir.Combine("tools", ".store", "fsautocomplete", "0.70.0", "fsautocomplete", "0.70.0", "tools", tfm, "any");
            Directory.CreateDirectory(any);
            File.WriteAllText(Path.Combine(any, "fsautocomplete.runtimeconfig.json"),
                $$"""{ "runtimeOptions": { "tfm": "{{tfm}}", "framework": { "name": "Microsoft.NETCore.App", "version": "{{version}}" } } }""");
        }
        return shim;
    }

    [Fact]
    public void A_tool_shim_packed_for_several_frameworks_needs_the_one_its_embedded_path_runs()
    {
        // The reviewer's case: the shim runs the net9.0 build, the global runtime has only 8.0, the net8.0 runtimeconfig sorts first. Reading
        // that one left DOTNET_ROOT unset ("You must install .NET"); the shim's own path says net9.0, so the dotnet on PATH (9.0) is used.
        using var dir = new TempDirectory("cscode-dotnet-");
        var global = FakeDotnetRootWith(dir, "global", "8.0.11");
        var path = FakeDotnetRootWith(dir, "path", "9.0.1");
        var shim = MultiTargetToolShim(dir, runs: "net9.0");

        Assert.Equal(Path.Combine(dir.Path, "tools", ".store", "fsautocomplete", "0.70.0", "fsautocomplete", "0.70.0", "tools", "net9.0", "any", "fsautocomplete.dll"),
            DotnetLaunchEnvironment.ShimAppPath(shim));
        var required = Assert.Single(DotnetLaunchEnvironment.RequiredFrameworks(shim));
        Assert.Equal(new Version(9, 0, 0), required.Version);

        var env = Env(("PATH", path));
        var log = new List<string>();
        DotnetLaunchEnvironment.Apply(env, Def("fsautocomplete"), shim, null, [], log.Add, hostDefaultRoot: global);
        Assert.Equal(path, env[Root]);
        Assert.Contains(log, l => l.Contains("Microsoft.NETCore.App 9.0.0", StringComparison.Ordinal) && l.Contains("has it", StringComparison.Ordinal));

        // and when it runs the net8.0 build, the global 8.0 is left to the host
        using var dir8 = new TempDirectory("cscode-dotnet-");
        var global8 = FakeDotnetRootWith(dir8, "global", "8.0.11");
        var shim8 = MultiTargetToolShim(dir8, runs: "net8.0");
        var env8 = Env(("PATH", FakeDotnetRootWith(dir8, "path", "9.0.1")));
        DotnetLaunchEnvironment.Apply(env8, Def("fsautocomplete"), shim8, null, [], null, hostDefaultRoot: global8);
        Assert.False(env8.ContainsKey(Root));
    }

    [Fact]
    public void A_multi_framework_tool_whose_shim_cannot_be_read_needs_every_framework_it_was_packed_for()
    {
        using var dir = new TempDirectory("cscode-dotnet-");
        var global = FakeDotnetRootWith(dir, "global", "8.0.11");
        var both = FakeDotnetRootWith(dir, "both", "8.0.4", "9.0.1");
        var shim = MultiTargetToolShim(dir, runs: null);
        Assert.Null(DotnetLaunchEnvironment.ShimAppPath(shim));
        var required = DotnetLaunchEnvironment.RequiredFrameworks(shim);
        Assert.Equal([new Version(8, 0, 0), new Version(9, 0, 0)], required.Select(f => f.Version).Order().ToArray());
        Assert.False(DotnetLaunchEnvironment.Satisfies(global, required)); // which build runs is unknown: 8.0 alone is not enough

        var env = Env(("PATH", FakeDotnetRootWith(dir, "path", "9.0.1")));
        DotnetLaunchEnvironment.Apply(env, Def("fsautocomplete"), shim, [both], [], null, hostDefaultRoot: global);
        Assert.Equal(both, env[Root]);
    }

    [Fact]
    public void The_embedded_app_path_of_a_real_dotnet_tool_shim_is_read()
    {
        var shim = new LspSettings().SearchDirs.SelectMany(d => new[] { "csharp-ls", "fsautocomplete", "dotnet-counters", "dotnet-trace" }.Select(t => Path.Combine(d, t)))
            .FirstOrDefault(p => File.Exists(p) && Directory.Exists(Path.Combine(Path.GetDirectoryName(p)!, ".store")));
        if (shim is null) { Assert.Skip("no dotnet tool shim is installed in the cscode.lsp.searchDirs (dotnet tool install --global csharp-ls)"); return; }
        var app = DotnetLaunchEnvironment.ShimAppPath(shim);
        Assert.NotNull(app);
        Assert.True(File.Exists(app), $"{shim} names {app}, which does not exist");
        Assert.Contains(Path.DirectorySeparatorChar + ".store" + Path.DirectorySeparatorChar, app, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.ChangeExtension(app, ".runtimeconfig.json")), $"no runtimeconfig.json beside {app}");
        Assert.NotEmpty(DotnetLaunchEnvironment.RequiredFrameworks(shim));
        TestContext.Current.SendDiagnosticMessage($"{shim} runs {app}");
    }

    [Fact]
    public void A_server_that_is_not_a_dotnet_program_gets_no_DOTNET_ROOT()
    {
        using var dir = new TempDirectory("cscode-dotnet-");
        var root = FakeDotnetRoot(dir);
        var plain = Executable(dir.Combine("bin", "rust-analyzer"), "#!/bin/sh\n");
        var env = Env(("PATH", root));
        var log = new List<string>();
        DotnetLaunchEnvironment.Apply(env, Def("rust-analyzer"), plain, null, [root], log.Add);
        Assert.False(env.ContainsKey(Root));
        Assert.Empty(log);
    }

    [Fact]
    public void Dotnet_programs_are_recognised_by_their_apphost_layout_a_tool_shim_or_their_name()
    {
        using var dir = new TempDirectory("cscode-dotnet-");
        var withDll = FakeDotnetServer(dir);
        var withConfig = Executable(dir.Combine("cfg", "srv2"), "#!/bin/sh\n");
        File.WriteAllText(dir.Combine("cfg", "srv2.runtimeconfig.json"), "{}");
        var shim = Executable(dir.Combine("tools", "some-tool"), "#!/bin/sh\n"); // dotnet tool install -g: ~/.dotnet/tools/<tool> + .store/
        Directory.CreateDirectory(dir.Combine("tools", ".store"));
        var wrapper = Executable(dir.Combine("nix", "marksman"), "#!/bin/sh\nexec /x/lib/marksman/marksman\n"); // nixpkgs wrapper script
        var plain = Executable(dir.Combine("plain", "srv"), "#!/bin/sh\n");

        Assert.True(DotnetLaunchEnvironment.IsDotnetServer("srv", withDll));
        Assert.True(DotnetLaunchEnvironment.IsDotnetServer("srv2", withConfig));
        Assert.True(DotnetLaunchEnvironment.IsDotnetServer("some-tool", shim));
        Assert.True(DotnetLaunchEnvironment.IsDotnetServer("marksman", wrapper));
        Assert.True(DotnetLaunchEnvironment.IsDotnetServer("csharp-ls", plain), "known by the definition's command");
        Assert.False(DotnetLaunchEnvironment.IsDotnetServer("srv", plain));
        if (!OperatingSystem.IsWindows())
        {
            var link = dir.Combine("bin", "srv");
            Directory.CreateDirectory(dir.Combine("bin"));
            File.CreateSymbolicLink(link, withDll); // a bin folder symlink to the apphost
            Assert.True(DotnetLaunchEnvironment.IsDotnetServer("srv", link));
        }
    }

    [Fact]
    public void The_root_of_the_real_dotnet_is_found_from_PATH_or_the_default_search_dirs()
    {
        var root = DotnetLaunchEnvironment.FindDotnetRoot(null, new LspSettings().SearchDirs);
        if (root is null) { Assert.Skip("no dotnet on PATH, in cscode.lsp.searchDirs, in ~/.dotnet or under the running runtime"); return; }
        Assert.True(Directory.Exists(Path.Combine(root, "host", "fxr")), root);
        Assert.True(Directory.Exists(Path.Combine(root, "shared", "Microsoft.NETCore.App")), root);
    }

    // ---- through the real process factory --------------------------------------------------------------------------------------------------

    /// <summary>Changes CsCode's (this test process's) own environment until disposed. The Lsp suite runs serially (AssemblyInfo.cs).</summary>
    private sealed class ProcessEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _saved = new();

        public ProcessEnvironment Set(string name, string? value)
        {
            if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
            return this;
        }

        public void Dispose()
        {
            foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static Dictionary<string, string> ProcEnviron(int pid)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in File.ReadAllText($"/proc/{pid}/environ").Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = entry.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0) result[entry[..eq]] = entry[(eq + 1)..];
        }
        return result;
    }

    private static async Task<Dictionary<string, string>> LaunchAndReadEnvironmentAsync(LanguageServerDefinition def, IReadOnlyList<string>? searchDirs)
    {
        var request = new LspLaunchRequest(def, null, null, ProcessPriority.Normal, SearchDirs: searchDirs);
        await using var connection = await new DefaultLspConnectionFactory().ConnectAsync(request, Ct);
        var pid = connection.ProcessId!.Value;
        // the script execs sleep: wait for that exec so /proc shows the server's own environment, not a half-started process
        await TestHelpers.EventuallyAsync(() => File.ReadAllText($"/proc/{pid}/cmdline").StartsWith("sleep", StringComparison.Ordinal), "the server running");
        var env = ProcEnviron(pid);
        await connection.KillAsync();
        return env;
    }

    [Fact]
    public async Task A_dotnet_server_started_by_a_CsCode_without_DOTNET_ROOT_receives_the_root_of_the_dotnet_it_finds()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("reads /proc: Linux only"); return; }
        using var dir = new TempDirectory("cscode-dotnet-");
        var root = FakeDotnetRoot(dir);
        var server = FakeDotnetServer(dir);
        using var _ = new ProcessEnvironment().Set(Root, null).Set(Arch, null);

        var env = await LaunchAndReadEnvironmentAsync(Def(server, new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" }), [root]);
        Assert.Equal(root, env.GetValueOrDefault(Root));
        Assert.False(env.ContainsKey(Arch));
    }

    [Fact]
    public async Task The_arch_variable_CsCode_was_started_with_does_not_reach_a_server()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("reads /proc: Linux only"); return; }
        using var dir = new TempDirectory("cscode-dotnet-");
        var root = FakeDotnetRoot(dir);
        var plain = Executable(dir.Combine("bin", "srv"), "#!/bin/sh\nexec sleep 30\n");
        using var _ = new ProcessEnvironment().Set(Root, null).Set(Arch, root); // what `dotnet run` gives CsCode's apphost

        var env = await LaunchAndReadEnvironmentAsync(Def(plain), null);
        Assert.Equal(root, env.GetValueOrDefault(Root));
        Assert.False(env.ContainsKey(Arch));
    }

    // ---- real servers ----------------------------------------------------------------------------------------------------------------------

    private static int[] Tagged(string tag)
    {
        var needle = Encoding.UTF8.GetBytes("CSCODE_SMOKE_TAG=" + tag);
        var found = new List<int>();
        foreach (var d in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(d), out var pid)) continue;
            try
            {
                if (File.ReadAllBytes(Path.Combine(d, "environ")).AsSpan().IndexOf(needle) >= 0) found.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* exited, or not ours */ }
        }
        return found.ToArray();
    }

    /// <summary>A private HOME/XDG, PATH cut down to /usr/bin:/bin (no dotnet), and a tag to find every process the server starts.</summary>
    private static Dictionary<string, string> PrivateEnv(TempDirectory home, string tag) => new()
    {
        ["PATH"] = "/usr/bin:/bin",
        ["HOME"] = home.Path,
        ["XDG_CONFIG_HOME"] = home.Combine("config"),
        ["XDG_CACHE_HOME"] = home.Combine("cache"),
        ["XDG_DATA_HOME"] = home.Combine("data"),
        ["XDG_STATE_HOME"] = home.Combine("state"),
        ["DOTNET_CLI_HOME"] = home.Path,
        ["NUGET_PACKAGES"] = home.Combine("nuget"),
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["CSCODE_SMOKE_TAG"] = tag,
    };

    private static async Task StopAndAssertNothingLeftAsync(LspTestEnv lsp, string id, string tag)
    {
        await lsp.Manager.StopAllAsync(Ct);
        await TestHelpers.EventuallyAsync(() => Tagged(tag).Length == 0,
            () => $"processes of {id} still running after shutdown: {string.Join(",", Tagged(tag))}", TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Csharp_ls_from_dotnet_tool_install_starts_without_DOTNET_ROOT_and_without_dotnet_on_PATH()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("reads /proc: Linux only"); return; }
        // the dotnet tool shim (~/.dotnet/tools/csharp-ls beside .store/), not a packaged wrapper script that finds a runtime on its own
        var defaults = new LspSettings().SearchDirs;
        var shim = defaults.Select(d => Path.Combine(d, "csharp-ls"))
            .FirstOrDefault(p => File.Exists(p) && Directory.Exists(Path.Combine(Path.GetDirectoryName(p)!, ".store")));
        if (shim is null) { Assert.Skip("csharp-ls is not installed as a dotnet tool in the cscode.lsp.searchDirs (dotnet tool install --global csharp-ls)"); return; }
        if (ToolResolver.Resolve("dotnet", null, null, null, defaults) is not { } dotnet) { Assert.Skip("no dotnet on PATH or in cscode.lsp.searchDirs"); return; }

        // search dirs as on a desktop-menu start: the tools folder, and a profile bin folder holding (a symlink to) dotnet
        using var bin = new TempDirectory("cscode-dotnet-bin-");
        File.CreateSymbolicLink(bin.Combine("dotnet"), dotnet);
        string[] searchDirs = [Path.GetDirectoryName(shim)!, bin.Path];
        if (DotnetLaunchEnvironment.FindDotnetRoot("/usr/bin:/bin", searchDirs) is not { } root) { Assert.Skip($"{dotnet} is not inside a dotnet installation"); return; }

        using var home = new TempDirectory("cscode-dotnet-home-");
        var tag = Guid.NewGuid().ToString("N");
        var env = PrivateEnv(home, tag);
        using var _ = new ProcessEnvironment().Set(Root, null).Set(Arch, null); // the installed app started from the desktop menu

        // control: the shim on its own cannot find a runtime here (no default install location on this machine), with CsCode's DOTNET_ROOT it can
        var alone = await RunToEndAsync(shim, env, "--version");
        if (alone.ExitCode == 0)
            TestContext.Current.SendDiagnosticMessage($"csharp-ls ran without DOTNET_ROOT (a dotnet in a default location): the control proves nothing here");
        else
            Assert.Contains("You must install", alone.Output); // "… .NET" (no runtime at all) or "… or update .NET" (not the version it needs)
        var helped = await RunToEndAsync(shim, new Dictionary<string, string>(env) { [Root] = root }, "--version");
        Assert.True(helped.ExitCode == 0, $"csharp-ls --version with DOTNET_ROOT={root}: exit {helped.ExitCode}: {helped.Output}");

        await using var lsp = new LspTestEnv(c =>
        {
            c.Set(LspSettingKeys.InitializeTimeoutMs, 120_000);
            c.Set(LspSettingKeys.ShutdownTimeoutMs, 5_000);
            c.Set(LspSettingKeys.SearchDirs, searchDirs);
            c.Set(LspSettingKeys.LanguageServers, new Dictionary<string, object> { ["csharp"] = new { env } });
            c.Set(LspSettingKeys.ServerChoice, new Dictionary<string, string> { [BuiltinLanguageServers.CSharpGroup] = "csharp" }); // Roslyn is C#'s default
        }, realServers: true);
        lsp.Open("Program.cs", "class P { static void Main() { } }\n", "csharp");
        try
        {
            var info = await lsp.WaitForStateAsync("csharp", LanguageServerState.Running, TimeSpan.FromSeconds(90));
            var pid = info.ProcessId!.Value;
            Assert.Equal(root, ProcEnviron(pid).GetValueOrDefault(Root));
            TestContext.Current.SendDiagnosticMessage($"csharp-ls: {info.ServerName} {info.ServerVersion} ({shim}) with DOTNET_ROOT={root}");
        }
        finally
        {
            await StopAndAssertNothingLeftAsync(lsp, "csharp", tag);
        }
    }

    [Fact]
    public async Task Nixpkgs_marksman_keeps_the_runtime_its_wrapper_chooses_when_CsCode_inherited_an_arch_variable()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("reads the Nix store and /proc: Linux only"); return; }
        var marksman = Directory.Exists("/nix/store")
            ? Directory.EnumerateDirectories("/nix/store", "*-marksman-*").Select(d => Path.Combine(d, "bin", "marksman")).Order(StringComparer.Ordinal).FirstOrDefault(File.Exists)
            : null;
        if (marksman is null) { Assert.Skip("marksman is not in the Nix store"); return; }
        var sdk = DotnetLaunchEnvironment.FindDotnetRoot(null, new LspSettings().SearchDirs);
        if (sdk is null) { Assert.Skip("no dotnet found"); return; }

        using var bin = new TempDirectory("cscode-store-bin-");
        File.CreateSymbolicLink(bin.Combine("marksman"), marksman);
        using var home = new TempDirectory("cscode-dotnet-home-");
        var tag = Guid.NewGuid().ToString("N");
        // `dotnet run` / `dotnet test` give CsCode's apphost DOTNET_ROOT_<ARCH> = the SDK's root, whose runtime is not the one marksman targets
        using var _ = new ProcessEnvironment().Set(Root, sdk).Set(Arch, sdk);

        await using var lsp = new LspTestEnv(c =>
        {
            c.Set(LspSettingKeys.InitializeTimeoutMs, 120_000);
            c.Set(LspSettingKeys.ShutdownTimeoutMs, 5_000);
            c.Set(LspSettingKeys.SearchDirs, new[] { bin.Path });
            c.Set(LspSettingKeys.LanguageServers, new Dictionary<string, object> { ["marksman"] = new { env = PrivateEnv(home, tag) } });
        }, realServers: true);
        lsp.Dir.WriteFile(".marksman.toml", "");
        lsp.Open("a.md", "# Title\n", "markdown");
        try
        {
            var info = await lsp.WaitForStateAsync("marksman", LanguageServerState.Running, TimeSpan.FromSeconds(90));
            var serverEnv = ProcEnviron(info.ProcessId!.Value);
            Assert.False(serverEnv.ContainsKey(Arch), "the inherited arch variable would outrank the wrapper's DOTNET_ROOT");
            Assert.NotEqual(sdk, serverEnv.GetValueOrDefault(Root)); // the wrapper's own runtime
        }
        finally
        {
            await StopAndAssertNothingLeftAsync(lsp, "marksman", tag);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunToEndAsync(string exe, IReadOnlyDictionary<string, string> env, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(Ct);
        var stderr = p.StandardError.ReadToEndAsync(Ct);
        try { await p.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct); }
        catch (TimeoutException)
        {
            p.Kill(entireProcessTree: true);
            throw;
        }
        return (p.ExitCode, await stdout + await stderr);
    }
}
