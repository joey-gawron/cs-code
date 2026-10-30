using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Runtime;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// The macOS parts of a language server's launch environment: the PATH a server found outside PATH runs with (lsp-2), the dotnet root put first on
/// it (lsp-3), the macOS search directories (lsp-4, lsp-5), and <c>${env:NAME}</c>, <c>${userHome}</c> and <c>~</c> in a definition's
/// <c>env</c> (lsp-9). The rules run on any OS; what the real process factory does is checked on a Mac.
/// </summary>
public class MacServerEnvironmentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LanguageServerDefinition Def(string command, IReadOnlyDictionary<string, string>? env = null) =>
        new("t", "T", DocumentSelector.ForLanguage("t"), command, [], Env: env);

    private static string P(params string[] dirs) => string.Join(Path.PathSeparator, dirs);

    // ---- lsp-2: the server's PATH --------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_server_found_outside_PATH_gets_its_own_directory_first_and_the_search_dirs_last()
    {
        var path = ToolResolver.ExtendPath(P("/usr/bin", "/bin"), "/Users/me/.volta/bin/typescript-language-server", ["/opt/homebrew/bin", "/Users/me/go/bin"]);
        Assert.Equal(P("/Users/me/.volta/bin", "/usr/bin", "/bin", "/opt/homebrew/bin", "/Users/me/go/bin"), path);
    }

    [Fact]
    public void Directories_already_on_PATH_are_not_added_again()
    {
        Assert.Equal(P("/usr/bin", "/bin"), ToolResolver.ExtendPath(P("/usr/bin", "/bin"), "/usr/bin/clangd", ["/usr/bin/", "/bin"]));
        Assert.Equal(P("/opt/homebrew/bin/", "/usr/bin", "/x"),
            ToolResolver.ExtendPath(P("/opt/homebrew/bin/", "/usr/bin"), "/opt/homebrew/bin/gopls", ["/x", "/x", "/opt/homebrew/bin"]));
    }

    [Fact]
    public void A_missing_PATH_becomes_the_server_directory_and_the_search_dirs()
    {
        Assert.Equal(P("/srv/bin", "/a"), ToolResolver.ExtendPath(null, "/srv/bin/server", ["/a"]));
    }

    // ---- lsp-3: the dotnet root first on PATH ------------------------------------------------------------------------------------------------

    private static string FakeDotnetRoot(TempDirectory dir)
    {
        var root = dir.Combine("dotnet-root");
        Directory.CreateDirectory(Path.Combine(root, "host", "fxr", "9.9.9"));
        File.WriteAllText(Path.Combine(root, "dotnet"), "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(root, "dotnet"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return root;
    }

    [Fact]
    public void On_a_Mac_the_dotnet_root_provided_to_a_server_is_also_first_on_its_PATH()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("unix permission bits"); return; }
        using var dir = new TempDirectory("cscode-dotnet-");
        var root = FakeDotnetRoot(dir);
        var env = new Dictionary<string, string?> { ["PATH"] = P("/usr/bin", "/bin") };

        DotnetLaunchEnvironment.Apply(env, Def("csharp-ls"), "/Users/me/.dotnet/tools/csharp-ls", [root], [], null, prependRootToPath: true);

        Assert.Equal(root, env[DotnetLaunchEnvironment.RootVariable]);
        Assert.Equal(P(root, "/usr/bin", "/bin"), env["PATH"]);
    }

    [Fact]
    public void Elsewhere_or_with_the_definition_own_PATH_the_server_PATH_is_left_alone()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("unix permission bits"); return; }
        using var dir = new TempDirectory("cscode-dotnet-");
        var root = FakeDotnetRoot(dir);

        var linux = new Dictionary<string, string?> { ["PATH"] = P("/usr/bin", "/bin") };
        DotnetLaunchEnvironment.Apply(linux, Def("csharp-ls"), "/x/csharp-ls", [root], [], null, prependRootToPath: false);
        Assert.Equal(root, linux[DotnetLaunchEnvironment.RootVariable]);
        Assert.Equal(P("/usr/bin", "/bin"), linux["PATH"]);

        var own = new Dictionary<string, string?> { ["PATH"] = "/mine" }; // the definition's env was applied before
        DotnetLaunchEnvironment.Apply(own, Def("csharp-ls", new Dictionary<string, string> { ["PATH"] = "/mine" }), "/x/csharp-ls", [root], [], null, prependRootToPath: true);
        Assert.Equal("/mine", own["PATH"]);
    }

    // ---- lsp-4 / lsp-5: the macOS search directories -------------------------------------------------------------------------------------

    [Fact]
    public void The_Mac_search_dirs_have_keg_only_llvm_the_dotnet_root_MacPorts_and_the_version_manager_shims()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Skip("the macOS list"); return; }
        foreach (var dir in new[]
                 {
                     "/opt/homebrew/opt/llvm/bin", "/usr/local/opt/llvm/bin", "/usr/local/share/dotnet", "/opt/local/bin", "/usr/local/go/bin",
                     "~/.volta/bin", "~/.asdf/shims", "~/.local/share/mise/shims", "~/.rbenv/shims", "~/.pyenv/shims", "~/Library/pnpm", "~/.bun/bin",
                 })
            Assert.Contains(dir, ToolResolver.DefaultSearchDirs);
        // Homebrew's own bin stays ahead of the keg-only llvm (a clangd the user linked wins)
        Assert.True(ToolResolver.DefaultSearchDirs.ToList().IndexOf("/opt/homebrew/bin") < ToolResolver.DefaultSearchDirs.ToList().IndexOf("/opt/homebrew/opt/llvm/bin"));
    }

    // ---- lsp-9: variables in a definition's env ------------------------------------------------------------------------------------------

    private static IReadOnlyDictionary<string, string>? Expand(IReadOnlyDictionary<string, string>? env, string? folder = null) =>
        LaunchVariables.ExpandEnvironment(env, folder, name => name switch { "PATH" => "/usr/bin:/bin", "TOKEN" => "${userHome}", _ => null }, "/Users/me");

    [Fact]
    public void Env_values_expand_env_references_the_home_directory_and_the_workspace_folder()
    {
        var env = Expand(new Dictionary<string, string>
        {
            ["PATH"] = "~/.volta/bin:${env:PATH}",
            ["HOME_ONLY"] = "~",
            ["CACHE"] = "${userHome}/Library/Caches/x",
            ["ROOT"] = "${workspaceFolder}/node_modules",
            ["UNSET"] = "[${env:NOT_SET}]",
            ["AS_IS"] = "${env:TOKEN}", // a variable's value is inserted as it is
            ["MIDDLE"] = "a~b",
            ["OPEN"] = "${env:PATH",
        }, "/work/proj/")!;

        Assert.Equal("/Users/me/.volta/bin:/usr/bin:/bin", env["PATH"]);
        Assert.Equal("/Users/me", env["HOME_ONLY"]);
        Assert.Equal("/Users/me/Library/Caches/x", env["CACHE"]);
        Assert.Equal("/work/proj/node_modules", env["ROOT"]);
        Assert.Equal("[]", env["UNSET"]);
        Assert.Equal("${userHome}", env["AS_IS"]);
        Assert.Equal("a~b", env["MIDDLE"]);
        Assert.Equal("${env:PATH", env["OPEN"]);
    }

    [Fact]
    public void Env_without_variables_is_returned_as_it_is()
    {
        var env = new Dictionary<string, string> { ["RUST_LOG"] = "info" };
        Assert.Same(env, Expand(env));
        Assert.Null(Expand(null));
    }

    // ---- through the real process factory (macOS) ----------------------------------------------------------------------------------------

    [Fact]
    public async Task On_a_Mac_a_server_found_in_a_search_dir_runs_with_that_dir_on_its_PATH()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Skip("macOS only (lsp-2)"); return; }
        using var dir = new TempDirectory("cscode-lsppath-");
        var seen = dir.Combine("path.txt");
        var bin = dir.Combine("tools");
        Directory.CreateDirectory(bin);
        var server = Path.Combine(bin, "cscode-fake-server");
        File.WriteAllText(server, $"#!/bin/sh\nprintf '%s' \"$PATH\" > '{seen}'\nexec sleep 30\n");
        File.SetUnixFileMode(server, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var request = new LspLaunchRequest(Def("cscode-fake-server"), null, null, ProcessPriority.Normal, SearchDirs: [bin]);
        await using var connection = await new DefaultLspConnectionFactory().ConnectAsync(request, Ct);
        await TestHelpers.EventuallyAsync(() => File.Exists(seen) && new FileInfo(seen).Length > 0, "the server writing its PATH");
        var path = File.ReadAllText(seen).Split(Path.PathSeparator);
        await connection.KillAsync();

        Assert.Equal(bin, path[0]);
        Assert.Contains("/usr/bin", path);
    }
}
