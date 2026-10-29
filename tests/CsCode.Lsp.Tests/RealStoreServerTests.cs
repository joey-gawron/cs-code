using System.Text;
using CsCode.Core.Languages;
using CsCode.Lsp.Manager;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// B3: more of the new built-in definitions run for REAL, with servers this machine's Nix store happens to hold although the user has not
/// installed them (they are on no PATH; the test only reads the store and installs nothing). Each server is reached solely through
/// <c>cscode.lsp.searchDirs</c> (a temp directory with one symlink; PATH cut down to /usr/bin:/bin), so what is tested is the definition itself:
/// its command and arguments, or its ALTERNATIVE when the primary command is missing (nixd for nil, protols for buf, elp for erlang_ls). Each
/// runs with HOME/XDG in a temp dir: initialize, the launch line, below-normal priority, a diagnostic for a broken document where the server
/// checks syntax on open, still running after the document was opened, shutdown, and no process left behind. Skips with the reason when the
/// package is not in the store. zls and nil, which ARE installed, have fuller tests in <see cref="RealZlsAndNilServerTests"/>.
/// </summary>
public class RealStoreServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Store = "/nix/store";

    // the store holds ~100k entries: list them once per test run
    private static readonly Lazy<string[]> StoreEntries = new(() =>
        Directory.Exists(Store) ? Directory.EnumerateDirectories(Store).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray() : []);

    /// <summary>The executable of a nixpkgs package (<c>{hash}-{package}-{version}/bin/{exe}</c>) in the store, or null.</summary>
    private static string? StoreExecutable(string package, string exe)
    {
        foreach (var entry in StoreEntries.Value) // entries sort by hash, not version: any one will do, and the pick is stable
        {
            var dash = entry.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0 || !entry.AsSpan(dash + 1).StartsWith(package + "-", StringComparison.Ordinal)) continue;
            var candidate = Path.Combine(Store, entry, "bin", exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// (definition id, language, nixpkgs package, executable, alternative it runs as (null: the definition's own command), file, text,
    /// whether the server reports a diagnostic for that text on open).
    /// </summary>
    public static TheoryData<string, string, string, string, string?, string, string, bool> Cases => new()
    {
        { "marksman", "markdown", "marksman", "marksman", null, "docs/a.md", "# Title\n\nSee [[missing]].\n", true },
        { "dockerfile", "dockerfile", "dockerfile-language-server", "docker-langserver", null, "Dockerfile", "FROM alpine\nBOGUS x\n", true },
        // protols reports errors through protoc, which the cut-down PATH does not have
        { "protobuf", "protobuf", "protols", "protols", "protols", "a.proto", "syntax = \"proto3\";\nmessage A { int32 x = ; }\n", false },
        { "sqls", "sql", "sqls", "sqls", null, "q.sql", "SELECT 1;\n", false },
        { "terraform-ls", "terraform", "terraform-ls", "terraform-ls", null, "main.tf", "variable \"x\" {\n", true },
        { "svelte", "svelte", "svelte-language-server", "svelteserver", null, "App.svelte", "<script>let a = 1;</script>\n<p>{a}</p>\n", false },
        { "graphql", "graphql", "graphql-language-service-cli", "graphql-lsp", null, "schema.graphql", "type Query { a: Int }\n", false },
        { "taplo", "toml", "taplo", "taplo", null, "a.toml", "a = \n", true },
        { "nix", "nix", "nixd", "nixd", "nixd", "default.nix", "let x = ; in x\n", true },
        { "erlang", "erlang", "erlang-language-platform", "elp", "elp", "a.erl", "-module(a).\n", false },
    };

    /// <summary>
    /// The project file a server needs before it does anything useful: graphql-lsp serves only documents its configuration covers, and
    /// Marksman indexes (and checks links in) a folder only when it has .marksman.toml or .git.
    /// </summary>
    private static readonly Dictionary<string, (string Name, string Text)> ProjectFiles = new()
    {
        ["graphql"] = (".graphqlrc.yml", "schema: schema.graphql\n"),
        ["marksman"] = (".marksman.toml", ""),
    };

    /// <summary>Live processes whose environment carries the tag (Linux /proc): the server and everything it started.</summary>
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

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Server_from_the_store_starts_through_the_search_dirs_and_leaves_nothing_behind(
        string id, string language, string package, string exe, string? alternative, string file, string text, bool diagnoses)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("reads the Nix store and /proc: Linux only"); return; }
        if (StoreExecutable(package, exe) is not { } real) { Assert.Skip($"{exe} is not in the Nix store (no {package} package)"); return; }

        var builtin = BuiltinLanguageServers.Find(id)!;
        var expectedArgs = alternative is null ? builtin.Definition.Args : builtin.Alternatives.Single(a => a.Command == alternative).Args;
        Assert.Equal(alternative ?? builtin.Definition.Command, exe);

        using var bin = new TempDirectory("cscode-store-bin-");
        var link = bin.Combine(exe);
        File.CreateSymbolicLink(link, real);
        using var home = new TempDirectory("cscode-store-home-");
        var tag = Guid.NewGuid().ToString("N");
        var env = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin:/bin", // not the user's PATH: the executable must be found through cscode.lsp.searchDirs
            ["HOME"] = home.Path,
            ["XDG_CONFIG_HOME"] = home.Combine("config"),
            ["XDG_CACHE_HOME"] = home.Combine("cache"),
            ["XDG_DATA_HOME"] = home.Combine("data"),
            ["XDG_STATE_HOME"] = home.Combine("state"),
            ["CSCODE_SMOKE_TAG"] = tag, // inherited by every descendant: lets the test find all of them, wherever they were re-parented
        };
        await using var lsp = new LspTestEnv(c =>
        {
            c.Set(LspSettingKeys.InitializeTimeoutMs, 120_000);
            c.Set(LspSettingKeys.ShutdownTimeoutMs, 5_000);
            c.Set(LspSettingKeys.SearchDirs, new[] { bin.Path });
            c.Set(LspSettingKeys.LanguageServers, new Dictionary<string, object> { [id] = new { env } });
        }, realServers: true);
        if (ProjectFiles.TryGetValue(id, out var project)) lsp.Dir.WriteFile(project.Name, project.Text);
        var doc = lsp.Open(file, text, language);
        try
        {
            var info = await lsp.WaitForStateAsync(id, LanguageServerState.Running, TimeSpan.FromSeconds(90));
            var pid = info.ProcessId!.Value;

            // the launch line: the symlink found in the search dirs, then the definition's (or the alternative's) arguments. nixpkgs' Node
            // servers are wrapper scripts that exec node on the real script, so for those argv[0] is node from the store.
            var argv = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(argv[0] == link || argv[0].StartsWith(Store + "/", StringComparison.Ordinal), $"{id} argv: {string.Join(' ', argv)}");
            Assert.Equal(expectedArgs, argv[^expectedArgs.Count..]);
            Assert.True(NativePriority.GetProcessNice(pid) >= 10, $"{id} nice = {NativePriority.GetProcessNice(pid)}: language servers run below normal priority");

            if (diagnoses)
                await TestHelpers.EventuallyAsync(() => lsp.Diagnostics.Get(doc.Uri).Any(),
                    () => $"{id}: a diagnostic for the broken {file}\n{lsp.Dump()}", TimeSpan.FromSeconds(60));
            else
                await Task.Delay(1500, Ct); // give a server that dislikes the document time to fall over

            Assert.Equal(LanguageServerState.Running, lsp.Manager.Servers.Single(s => s.Id == id).State);
            Assert.Contains(pid, Tagged(tag));
            TestContext.Current.SendDiagnosticMessage($"{id}: {info.ServerName} {info.ServerVersion} ({real} {string.Join(' ', expectedArgs)})");
        }
        finally
        {
            await lsp.Manager.StopAllAsync(Ct);
            await TestHelpers.EventuallyAsync(() => Tagged(tag).Length == 0,
                () => $"processes of {id} still running after shutdown: {string.Join(",", Tagged(tag))}", TimeSpan.FromSeconds(15));
        }
    }
}
