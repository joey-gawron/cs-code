using Avalonia.Headless.XUnit;
using CsCode.App.Docking;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Editor.TreeSitter.Grammars;
using CsCode.Editor.TreeSitter.Native;
using CsCode.Editor.View;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// Tree-sitter through the REAL composition (every module, the Platform settings service over a temp <c>CSCODE_HOME</c>, the document well),
/// docs/TREE-SITTER.md: the hybrid default (TextMate for the languages it has, tree-sitter for those it has not), the setting the Settings panel
/// lists and its old alias, the app's bundled grammars, the structural providers in the shared registry, and the isolation files.
/// </summary>
public class TreeSitterCompositionTests
{
    private const string Key = "editor.syntaxEngine";
    private const string LegacyKey = "editor.experimental.treeSitter";

    private static async Task<CodeEditorControl> OpenAsync(DocumentWell well, string path)
    {
        var tab = await well.OpenAsync(new Uri(path));
        Assert.True(await DevHost.WaitFor(() => tab.LiveView is CodeEditorControl, 10_000), "the editor was not presented");
        return (CodeEditorControl)tab.LiveView!;
    }

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
        var property = Assert.Single(config.Properties, p => p.Key == Key);
        Assert.Equal("auto", property.Default!.Value.GetString());
        Assert.DoesNotContain(config.Properties, p => p.Key == LegacyKey); // replaced (still read as an alias)

        var view = await OpenAsync(well, file);
        Assert.True(await DevHost.WaitFor(() => view.SyntaxEngine == "TextMate", 10_000), $"engine {view.SyntaxEngine}");

        // the way the Settings panel changes it: settings.json
        await config.UpdateAsync("[rust]", new Dictionary<string, object> { [Key] = "treesitter" });
        Assert.True(await DevHost.WaitFor(() => view.SyntaxEngine == "tree-sitter", 20_000),
            $"engine {view.SyntaxEngine} (grammar missing? the editor falls back to TextMate and logs why)");
        Assert.Contains(Key, File.ReadAllText(config.UserSettingsPath), StringComparison.Ordinal);

        await config.UpdateAsync("[rust]", new Dictionary<string, object>());
        Assert.True(await DevHost.WaitFor(() => view.SyntaxEngine == "TextMate", 20_000), $"engine {view.SyntaxEngine}");

        // the old boolean still works
        await config.UpdateAsync(LegacyKey, true);
        Assert.True(await DevHost.WaitFor(() => view.SyntaxEngine == "tree-sitter", 20_000), $"engine {view.SyntaxEngine} (alias)");
        await config.UpdateAsync(LegacyKey, false);
        Assert.True(await DevHost.WaitFor(() => view.SyntaxEngine == "TextMate", 20_000), $"engine {view.SyntaxEngine} (alias off)");
        await well.CloseAsync(well.Find(new Uri(file))!, prompt: false);
    }

    /// <summary>
    /// The hybrid default with every module composed: a TOML file (no TextMate grammar) is coloured by tree-sitter from the app's bundle, a Rust
    /// file stays on TextMate, the TOML outline comes from the tree through the shared provider registry (no TOML server runs here), the
    /// quarantine command is in the palette with a title, and this process's flight recorder file is there while it runs and gone after a clean
    /// shutdown.
    /// </summary>
    [AvaloniaFact(Timeout = 120_000)]
    public async Task By_default_the_real_app_colours_TOML_with_the_bundled_grammar_and_Rust_with_TextMate()
    {
        Assert.SkipUnless(TsRuntime.Load(), "libtree-sitter is not available on this machine: " + TsRuntime.FailureReason);
        var host = RealAppHost.Create(h =>
        {
            // no TOML language server either (taplo may be installed): the outline below must come from the tree
            var settings = File.ReadAllText(Path.Combine(h.Home.Path, "config", "settings.json"));
            h.Home.WriteFile("config/settings.json", settings.Replace("\"cscode.languageServers\": {", "\"cscode.languageServers\": { \"taplo\": { \"enabled\": false },", StringComparison.Ordinal));
        });
        try
        {
            await host.StartAsync();
            var toml = host.Files.WriteFile("Cargo.toml", "[package]\nname = \"demo\"\n\n[dependencies]\nserde = \"1\"\n");
            var rust = host.Files.WriteFile("main.rs", "fn main() {}\n");
            var well = host.Get<DocumentWell>();
            var tomlView = await OpenAsync(well, toml);
            var rustView = await OpenAsync(well, rust);
            Assert.True(await DevHost.WaitFor(() => tomlView.SyntaxEngine == "tree-sitter" && rustView.SyntaxEngine == "TextMate", 20_000),
                $"TOML {tomlView.SyntaxEngine}, Rust {rustView.SyntaxEngine}");
            if (TreeSitterBundle.Default is not null) Assert.Equal("bundled", TsRuntime.LoadedFrom);

            var registry = host.Get<ILanguageFeatureRegistry>();
            var symbols = registry.GetProviders<IDocumentSymbolProvider>(tomlView.Document);
            var outline = await Assert.Single(symbols).ProvideDocumentSymbolsAsync(tomlView.Document, CancellationToken.None);
            Assert.Equal(["package", "dependencies"], outline!.Select(s => s.Name));
            Assert.Equal("serde", Assert.Single(outline![1].Children!).Name);

            var command = Assert.Single(host.Get<ICommandService>().All, c => c.Id == "editor.treeSitter.clearQuarantine");
            Assert.Equal("Clear Quarantined Tree-sitter Grammars", command.Title);

            // the crash isolation's flight recorder for this process, under the user data directory (removed on a clean exit: Editor tests)
            var flight = Path.Combine(host.Get<IAppPaths>().UserDataDir, "tree-sitter", "flight", Environment.ProcessId + ".flight");
            Assert.True(File.Exists(flight), flight);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }
}
