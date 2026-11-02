using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Extensions.Clangd;
using CsCode.Lsp.Manager;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// Stage 3 (C): clangd's extension against the real clangd (skipped with the reason when it is not installed): inactive regions, the file
/// status, Switch Between Source/Header and Show AST, each through the built-in extensions as the app has them. clangd runs with a private HOME
/// and XDG directories, so it never touches the user's configuration or caches.
/// </summary>
[Trait("Category", "RealTools")]
public class RealClangdExtensionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Clangd_reports_inactive_regions_and_its_file_status_and_answers_switch_and_ast()
    {
        if (ToolResolver.Resolve("clangd", null, null, null, new LspSettings().SearchDirs) is null) { Assert.Skip("clangd is not installed (not on PATH nor in the cscode.lsp.searchDirs directories)"); return; }
        using var home = new TempDirectory("cscode-clangd-home-");
        var env = new Dictionary<string, string> { ["HOME"] = home.Path, ["XDG_CONFIG_HOME"] = home.Combine("config"), ["XDG_CACHE_HOME"] = home.Combine("cache") };
        await using var lsp = new LspTestEnv(c =>
        {
            c.Set(LspSettingKeys.InteractiveTimeoutMs, 30_000);
            c.Set(LspSettingKeys.BackgroundTimeoutMs, 30_000);
            c.Set(LspSettingKeys.InitializeTimeoutMs, 60_000);
            c.Set(LspSettingKeys.LanguageServers, new Dictionary<string, object> { ["clangd"] = new { env } });
        }, realServers: true);
        File.WriteAllText(Path.Combine(lsp.Root, "compile_flags.txt"), "-xc\n");
        var header = Path.Combine(lsp.Root, "src", "mathlib.h");
        Directory.CreateDirectory(Path.GetDirectoryName(header)!);
        File.WriteAllText(header, "int add(int a, int b);\n");
        var source = lsp.Open("src/mathlib.c", "#include \"mathlib.h\"\n#if 0\nint dead(void) { return 1; }\n#endif\nint add(int a, int b) { return a + b; }\n", "c");
        lsp.Navigation.ActiveDocument = new EditorDocumentView(source);
        await lsp.WaitForStateAsync("clangd", LanguageServerState.Running, TimeSpan.FromSeconds(60));

        // inactive regions: the body of #if 0 (clangd leaves the directive lines active)
        var provider = await lsp.WaitForAsync(() => lsp.Registry.GetProviders<IInactiveRegionsProvider>(source).FirstOrDefault(p => p.GetInactiveRegions(source.Uri).Count > 0),
            "clangd's inactive regions", TimeSpan.FromSeconds(60));
        var region = Assert.Single(provider.GetInactiveRegions(source.Uri));
        Assert.Equal("int dead(void) { return 1; }", source.GetText(region));

        // the file status: clangd says "idle" once it has built the file
        await lsp.WaitForAsync(() => lsp.StatusBar.Get(FileStatusDisplay.ItemId) is { Text: "clangd: idle" } item ? item : null, () => "clangd: idle, now: "
            + lsp.StatusBar.Get(FileStatusDisplay.ItemId)?.Text, TimeSpan.FromSeconds(60));

        // Switch Between Source/Header opens mathlib.h
        await lsp.Commands.ExecuteAsync(ClangdExtension.SwitchHeaderSourceCommand).AsTask().WaitAsync(TimeSpan.FromSeconds(60), Ct);
        var opened = Assert.Single(lsp.Navigation.Opened).Uri;
        Assert.Equal(TempDirectory.PhysicalPath(header), TempDirectory.PhysicalPath(opened.LocalPath));

        // Show AST at "a + b": the innermost node and its children, read-only
        lsp.Navigation.ActiveCaret = new TextPosition(4, 34);
        await lsp.Commands.ExecuteAsync(ClangdExtension.ShowAstCommand).AsTask().WaitAsync(TimeSpan.FromSeconds(60), Ct);
        var ast = lsp.Navigation.Opened[^1].Uri;
        var text = (await lsp.ReadOnlyDocuments.ProvideAsync(ast, Ct))!.Text;
        Assert.StartsWith("BinaryOperator +  [line 5]", text, StringComparison.Ordinal);
        Assert.Contains("DeclRef a", text, StringComparison.Ordinal);
    }
}
