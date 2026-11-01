using Avalonia.Headless.XUnit;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Testing;
using CsCode.TestDoubles;
using CsCode.Ui;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>The Test Explorer in the real composition: its tool window, its commands, its keys in the Visual Studio preset, the Test menu and the editor's glyph provider.</summary>
public class TestExplorerCompositionTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_real_app_has_the_test_explorer_its_commands_keys_menu_and_glyph_provider()
    {
        await Task.Yield();
        using var home = new TempDirectory("cscode-testexplorer-");
        var previousHome = Environment.GetEnvironmentVariable("CSCODE_HOME");
        Environment.SetEnvironmentVariable("CSCODE_HOME", home.Path);
        try
        {
            await using var sp = AppServices.Build([]);
            var window = Assert.Single(sp.GetServices<IToolWindow>(), w => w.Id == TestingHost.ViewId);
            Assert.Equal(("Test Explorer", TestingHost.FocusView), (window.Title, window.ToggleCommandId));
            var commands = sp.GetRequiredService<ICommandService>();
            foreach (var id in new[] { TestingHost.RunAll, TestingHost.DebugAll, TestingHost.RunFailed, TestingHost.RunLast, TestingHost.RunAtCursor, TestingHost.CancelRun, TestingHost.FocusView })
                Assert.True(commands.Has(id), id);
            var keys = sp.GetRequiredService<IKeybindingService>();
            var chord = Assert.Single(keys.GetBindingsFor(TestingHost.RunAll)).Chord; // Visual Studio's Ctrl+R, A (Cmd+R, A on a Mac)
            Assert.Equal(2, chord.Count);
            Assert.NotNull(keys.GetLabel(TestingHost.RunAll));
            var features = sp.GetRequiredService<ILanguageFeatureRegistry>();
            Assert.Single(features.GetProviders<ITestGutterProvider>(new InMemoryTextDocument(new Uri("file:///w/T.cs"), "csharp")));
            Assert.Single(features.GetProviders<ITestGutterProvider>(new InMemoryTextDocument(new Uri("file:///w/lib.rs"), "rust")));
            // "Run Test | Debug Test" code lenses through the editor's code lens, with their commands
            Assert.Single(features.GetProviders<ICodeLensProvider>(new InMemoryTextDocument(new Uri("file:///w/T.cs"), "csharp")));
            Assert.True(commands.Has("testing.codeLens.run") && commands.Has("testing.codeLens.debug"));
            var config = sp.GetRequiredService<IConfigurationService>();
            foreach (var key in new[] { "testing.codeLens", "editor.codeLens", "editor.codeLensFontSize", "editor.codeLensFontFamily", "editor.semanticHighlighting.enabled" })
                Assert.Contains(config.Properties, p => p.Key == key && p.Description is { Length: > 0 });
            Assert.Contains(Menus.ShellMenus.Build(), r => r.Header == "Te_st" && r.Children!.Any(c => c.CommandId == TestingHost.RunAll));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CSCODE_HOME", previousHome);
        }
    }
}
