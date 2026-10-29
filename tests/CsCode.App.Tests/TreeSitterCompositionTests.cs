using Avalonia.Headless.XUnit;
using CsCode.App.Docking;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Editor.TreeSitter.Native;
using CsCode.Editor.View;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// The EXPERIMENTAL tree-sitter engine through the REAL composition (all modules, the Platform settings service over a temp
/// <c>CSCODE_HOME</c>, the document well): the setting is registered where the Settings panel finds it, and changing it the way the panel
/// does (<c>IConfigurationService.UpdateAsync</c>, which writes the user's settings.json) switches an open editor between the engines live.
/// </summary>
public class TreeSitterCompositionTests
{
    private const string Key = "editor.experimental.treeSitter";

    [AvaloniaFact(Timeout = 120_000)]
    public async Task The_setting_switches_an_open_editor_between_TextMate_and_tree_sitter_in_the_real_app()
    {
        Assert.SkipUnless(TsRuntime.Load(), "libtree-sitter is not available on this machine: " + TsRuntime.FailureReason);
        using var env = new RealAppHome();
        var file = env.Workspace.WriteFile("main.rs", "fn main() {\n    let s = \"x\"; // c\n}\n");
        await using var run = new RealAppRun(env);
        await run.MainWindowAsync();
        var config = run.Services.GetRequiredService<IConfigurationService>();
        var well = run.Services.GetRequiredService<DocumentWell>();
        Assert.Contains(config.Properties, p => p.Key == Key);

        var tab = await well.OpenAsync(new Uri(file));
        Assert.True(await DevHost.WaitFor(() => tab.LiveView is CodeEditorControl, 10_000), "the editor was not presented");
        var view = (CodeEditorControl)tab.LiveView!;
        Assert.True(await DevHost.WaitFor(() => view.SyntaxEngine == "TextMate", 10_000), $"engine {view.SyntaxEngine}");

        await config.UpdateAsync(Key, true);
        Assert.True(await DevHost.WaitFor(() => view.SyntaxEngine == "tree-sitter", 20_000),
            $"engine {view.SyntaxEngine} (grammar missing? the editor falls back to TextMate and logs why)");
        Assert.Contains(Key, File.ReadAllText(config.UserSettingsPath), StringComparison.Ordinal);

        await config.UpdateAsync(Key, false);
        Assert.True(await DevHost.WaitFor(() => view.SyntaxEngine == "TextMate", 20_000), $"engine {view.SyntaxEngine}");
        await well.CloseAsync(tab, prompt: false);
    }
}
