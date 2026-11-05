using Avalonia.Headless.XUnit;
using CsCode.App.QuickInput;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Tests.RealApp.Vim;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.SpellCheck;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// The spell checker in the REAL application (docs/SPELLCHECK.md): the real editor's TextMate tokens decide what is read, the squiggles are the
/// editor's diagnostics, quick fixes go through the editor's Ctrl+. menu, Vim's ]s z= zg drive the checker's commands, and adding a word writes
/// the user's settings.json (under the host's temp CSCODE_HOME).
/// </summary>
public class SpellCheckRealAppTests(ITestOutputHelper output)
{
    private const string Code = "using System;\n// a wierd comment\nclass Recieve { } // and a definately\n";

    private static IReadOnlyList<Diagnostic> Spelling(RealAppHost host, Uri uri) =>
        host.Get<IDiagnosticService>().Get(uri).Where(d => d.Source == SpellCheckService.Owner).OrderBy(d => d.Range.Start).ToArray();

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Comments_are_checked_in_a_real_editor_and_Vim_keys_jump_fix_and_add()
    {
        await using var app = await VimApp.StartAsync(Code, "a.cs");
        var uri = app.View.Document.Uri;
        Assert.True(await RealAppHost.WaitFor(() => Spelling(app.Host, uri).Count == 2, 20_000),
            "spelling diagnostics: " + string.Join(", ", Spelling(app.Host, uri).Select(d => d.Message)));
        var found = Spelling(app.Host, uri);
        Assert.Equal(["\"wierd\": Unknown word.", "\"definately\": Unknown word."], found.Select(d => d.Message)); // not the class name Recieve
        Assert.All(found, d => Assert.Equal(DiagnosticSeverity.Information, d.Severity));
        Assert.True(await RealAppHost.WaitFor(() => StatusText(app.Host) == "$(book) Spell: 2"), "status bar: " + StatusText(app.Host));

        // ]s ]s [s: the cursor goes to each word's start (the editor's caret follows the command's selection)
        app.SetCaret(0, 0);
        await app.KeysAsync("]s");
        Assert.True(await RealAppHost.WaitFor(() => app.Caret == new TextPosition(1, 5)), $"caret at {app.Caret}");
        await app.KeysAsync("]s");
        Assert.True(await RealAppHost.WaitFor(() => app.Caret == new TextPosition(2, 27)), $"caret at {app.Caret}");
        await app.KeysAsync("[s");
        Assert.True(await RealAppHost.WaitFor(() => app.Caret == new TextPosition(1, 5)), $"caret at {app.Caret}");

        // z= on "wierd": the suggestions in the quick pick; the first replaces the word
        var quick = app.Host.Get<QuickInputViewModel>();
        await app.KeysAsync("z=");
        Assert.True(await RealAppHost.WaitFor(() => quick.IsVisible && quick.Items.Count > 0), "the suggestions did not open");
        output.WriteLine("z= offered: " + string.Join(", ", quick.Items.Select(i => i.Label)));
        Assert.Equal("weird", quick.Items[0].Label);
        quick.SelectedIndex = 0;
        quick.Accept();
        Assert.True(await RealAppHost.WaitFor(() => app.Text.Contains("// a weird comment", StringComparison.Ordinal)), app.Text);
        Assert.True(await RealAppHost.WaitFor(() => Spelling(app.Host, uri).Count == 1, 20_000));

        // zg on "definately": added to the user dictionary (settings.json), the squiggle goes at once
        app.SetCaret(2, 27);
        await app.KeysAsync("zg");
        Assert.True(await RealAppHost.WaitFor(() => Spelling(app.Host, uri).Count == 0, 20_000), "still: " + string.Join(", ", Spelling(app.Host, uri).Select(d => d.Message)));
        var settings = File.ReadAllText(app.Host.Home.Combine("config/settings.json"));
        Assert.Contains("\"cSpell.words\"", settings, StringComparison.Ordinal);
        Assert.Contains("\"definately\"", settings, StringComparison.Ordinal);
        Assert.True(await RealAppHost.WaitFor(() => StatusText(app.Host) == "$(check) Spell"), "status bar: " + StatusText(app.Host));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Ctrl_dot_offers_the_spelling_fixes_and_applies_one()
    {
        await using var app = await VimApp.StartAsync(Code, "b.cs");
        var uri = app.View.Document.Uri;
        Assert.True(await RealAppHost.WaitFor(() => Spelling(app.Host, uri).Count == 2, 20_000));
        app.SetCaret(2, 30);
        var quick = app.Host.Get<QuickInputViewModel>();
        var running = app.Host.Get<ICommandService>().ExecuteAsync(KnownCommands.QuickFix).AsTask();
        Assert.True(await RealAppHost.WaitFor(() => quick.IsVisible && quick.Items.Count > 0), "Ctrl+. did not open");
        var labels = quick.Items.Select(i => i.Label).ToList();
        output.WriteLine("Ctrl+. offered: " + string.Join(", ", labels));
        Assert.Equal("Change spelling to \"definitely\"", labels[0]);
        Assert.Contains("Add \"definately\" to user dictionary", labels);
        Assert.Contains("Ignore \"definately\" in this file", labels);
        quick.SelectedIndex = 0;
        quick.Accept();
        await running;
        Assert.True(await RealAppHost.WaitFor(() => app.Text.Contains("// and a definitely", StringComparison.Ordinal)), app.Text);
        Assert.True(await RealAppHost.WaitFor(() => Spelling(app.Host, uri).Count == 1, 20_000));
        Assert.Contains(app.Host.Get<ICommandService>().All, c => c.Id == SpellCheckHost.ToggleEnableSpellChecker && c.Title == "Toggle Spell Checking");
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task The_commit_message_box_gets_the_spelling_layer_over_the_real_checker()
    {
        await using var host = await RealAppHost.Create().StartAsync([]);
        await host.RunAsync(CsCode.Panels.Git.GitHost.ShowScm);
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Tools.Get(CsCode.Panels.Git.GitHost.ViewId)?.LiveView is CsCode.Panels.Git.Views.SourceControlView), "the window");
        var view = (CsCode.Panels.Git.Views.SourceControlView)host.Shell.Tools.Get(CsCode.Panels.Git.GitHost.ViewId)!.LiveView!;
        // the layer (internal to the panel) sits over the message box when the composition has a spell checker
        Assert.Contains(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view), v => v.GetType().Name == "CommitMessageSpelling");
        var issues = await host.Get<ITextSpellChecker>().CheckTextAsync("Fix the wierd bug\n\nIt is definately fixed.", "scminput");
        Assert.Equal(["wierd", "definately"], issues.Select(i => i.Word));
    }

    private static string? StatusText(RealAppHost host) =>
        host.Get<IStatusBarService>().Items.FirstOrDefault(i => i.Id == SpellCheckHost.StatusItemId)?.Text;
}
