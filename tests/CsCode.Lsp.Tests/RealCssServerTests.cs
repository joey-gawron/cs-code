using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// Colours and colour presentations from the REAL vscode-css-language-server (vscode-langservers-extracted, child process over stdio, through
/// the built-in <c>css</c> definition): what the editor's colour decorators and colour picker use (docs/EDITOR.md §7g). Install it with
/// <c>tools/fetch-lsp-tools.sh --with-css</c>; without it (or without node) the test SKIPS with that reason.
/// </summary>
public class RealCssServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string CssLs => Path.Combine(LspTestEnv.ToolsDir, "lsp", "node_modules", ".bin", "vscode-css-language-server");

    private static bool RequireTools()
    {
        if (!File.Exists(CssLs)) { Assert.Skip("vscode-css-language-server is not installed in .scratch/tools/lsp; run tools/fetch-lsp-tools.sh --with-css"); return false; }
        if (ToolResolver.FindOnPath("node", null) is null) { Assert.Skip("node is not on PATH"); return false; }
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) { Assert.Skip("integration tests are written for unix"); return false; }
        return true;
    }

    [Fact]
    public async Task The_css_server_reports_colours_and_writes_a_picked_colour_as_rgb_hex_and_hsl()
    {
        if (!RequireTools()) return;
        await using var env = new LspTestEnv(c =>
        {
            c.Set(LspSettingKeys.InteractiveTimeoutMs, 30_000);
            c.Set(LspSettingKeys.BackgroundTimeoutMs, 60_000);
            c.Set(LspSettingKeys.InitializeTimeoutMs, 120_000);
        }, realServers: true);
        const string Text = ".a { color: #ff0000; background: rgba(0, 0, 255, 0.5); }\n";
        var doc = env.Open("style.css", Text, "css");
        await env.WaitForStateAsync("css", LanguageServerState.Running, TimeSpan.FromSeconds(90));
        await TestHelpers.EventuallyAsync(() => env.Registry.GetProviders<IDocumentColorProvider>(doc).Count > 0, () => "colour provider\n" + env.Dump(), TimeSpan.FromSeconds(60));
        var provider = env.Provider<IDocumentColorProvider>(doc);

        IReadOnlyList<ColorInformation>? colours = null;
        for (var deadline = DateTime.UtcNow.AddSeconds(60); DateTime.UtcNow < deadline; await Task.Delay(300, Ct))
        {
            colours = await provider.ProvideDocumentColorsAsync(doc, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(40), Ct);
            if (colours is { Count: 2 }) break;
        }
        Assert.True(colours is { Count: 2 }, "two colours\n" + env.Dump());
        var red = colours!.OrderBy(c => c.Range.Start.Character).First();
        var blue = colours!.OrderBy(c => c.Range.Start.Character).Last();
        Assert.Equal(new TextRange(new TextPosition(0, 12), new TextPosition(0, 19)), red.Range);
        Assert.Equal((1d, 0d, 0d, 1d), (red.Red, red.Green, red.Blue, red.Alpha));
        Assert.Equal(Text.IndexOf("rgba(", StringComparison.Ordinal), blue.Range.Start.Character);
        Assert.Equal(0.5, blue.Alpha, 3);

        // the picker asks for the presentations of the colour it picked, over the colour's range
        var picked = red with { Red = 0, Green = 0, Blue = 1 };
        var presentations = await provider.ProvideColorPresentationsAsync(doc, picked, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(40), Ct);
        Assert.NotNull(presentations);
        var labels = presentations.Select(p => p.Label).ToList();
        Assert.Contains("rgb(0, 0, 255)", labels);
        Assert.Contains("#0000ff", labels);
        Assert.Contains("hsl(240, 100%, 50%)", labels);
        foreach (var p in presentations.Where(p => p.TextEdit is not null))
        {
            Assert.Equal(red.Range, p.TextEdit!.Range);
            Assert.Equal(p.Label, p.TextEdit.NewText);
        }
        var translucent = await provider.ProvideColorPresentationsAsync(doc, blue with { Alpha = 0.25 }, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(40), Ct);
        Assert.Contains("rgba(0, 0, 255, 0.25)", translucent!.Select(p => p.Label));
    }
}
