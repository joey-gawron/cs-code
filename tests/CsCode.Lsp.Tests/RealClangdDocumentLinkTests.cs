using CsCode.Core.Languages;
using CsCode.Lsp.Features;
using CsCode.Lsp.Manager;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// Stage 3 (C): <c>textDocument/documentLink</c> against the real clangd (skipped with the reason when it is not installed). clangd links each
/// <c>#include</c> to the header it resolves; the include path comes from a <c>compile_flags.txt</c> in the temp project. clangd runs with a
/// private HOME and XDG directories, so it never touches the user's configuration or caches.
/// </summary>
[Trait("Category", "RealTools")]
public class RealClangdDocumentLinkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Link_targets_must_be_absolute_uris()
    {
        Assert.Equal(new Uri("file:///usr/include/stdio.h"), DocumentLinkProvider.Target("file:///usr/include/stdio.h"));
        Assert.Null(DocumentLinkProvider.Target("include/x.h"));
        Assert.Null(DocumentLinkProvider.Target(null));
    }

    [Fact]
    public async Task Clangd_links_includes_to_their_headers()
    {
        if (ToolResolver.Resolve("clangd", null, null, null, new LspSettings().SearchDirs) is null) { Assert.Skip("clangd is not installed (not on PATH nor in the cscode.lsp.searchDirs directories)"); return; }
        using var home = new TestDoubles.TempDirectory("cscode-clangd-home-");
        var env = new Dictionary<string, string> { ["HOME"] = home.Path, ["XDG_CONFIG_HOME"] = home.Combine("config"), ["XDG_CACHE_HOME"] = home.Combine("cache") };
        await using var lsp = new LspTestEnv(c =>
        {
            c.Set(LspSettingKeys.InteractiveTimeoutMs, 30_000);
            c.Set(LspSettingKeys.InitializeTimeoutMs, 60_000);
            c.Set(LspSettingKeys.LanguageServers, new Dictionary<string, object> { ["clangd"] = new { env } });
        }, realServers: true);
        File.WriteAllText(Path.Combine(lsp.Root, "compile_flags.txt"), "-Iinclude\n");
        Directory.CreateDirectory(Path.Combine(lsp.Root, "include"));
        var header = Path.Combine(lsp.Root, "include", "mathlib.h");
        File.WriteAllText(header, "int add(int a, int b);\n");
        var main = lsp.Open("src/main.c", "#include <stdio.h>\n#include \"mathlib.h\"\nint main(void) { return add(1, 2); }\n", "c");
        await lsp.WaitForStateAsync("clangd", LanguageServerState.Running, TimeSpan.FromSeconds(60));

        IReadOnlyList<DocumentLink>? links = null;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            links = await lsp.Provider<IDocumentLinkProvider>(main).ProvideDocumentLinksAsync(main, Ct);
            if (links is { Count: >= 2 }) break;
            await Task.Delay(250, Ct);
        }
        Assert.True(links is { Count: >= 2 }, "clangd returned no links\n" + lsp.Dump());
        var local = links!.Single(l => l.Range.Start.Line == 1);
        Assert.Equal("\"mathlib.h\"", main.GetText(local.Range));
        Assert.NotNull(local.Target);
        Assert.Equal(TestDoubles.TempDirectory.PhysicalPath(header), TestDoubles.TempDirectory.PhysicalPath(local.Target!.LocalPath));
        var system = links.Single(l => l.Range.Start.Line == 0);
        Assert.EndsWith("stdio.h", system.Target!.LocalPath, StringComparison.Ordinal);
    }
}
