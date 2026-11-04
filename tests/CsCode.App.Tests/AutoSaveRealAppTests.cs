using Avalonia.Headless.XUnit;
using CsCode.App.Notifications;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// <c>files.autoSave</c> through the REAL composition (settings.json in a temp <c>CSCODE_HOME</c>, the real editor documents and their save path):
/// a save after a delay writes the file without format on save and leaves the caret's line alone while a manual save still formats; a delayed
/// save waits for the "changed on disk" question; on focus change the editor that loses the keyboard is saved.
/// </summary>
public class AutoSaveRealAppTests
{
    private static void AddSettings(RealAppHost h, string settings)
    {
        var path = h.Home.Combine("config/settings.json");
        var current = File.ReadAllText(path).TrimEnd();
        File.WriteAllText(path, current[..^1].TrimEnd() + ",\n" + settings + "\n}\n");
    }

    private static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    /// <summary>A formatter that upper-cases the document: visible in the file when format on save ran.</summary>
    private sealed class UpperCaseFormatter : IDocumentFormattingProvider
    {
        public int Calls;

        public ValueTask<IReadOnlyList<TextEdit>?> FormatDocumentAsync(ITextDocument document, FormattingOptions options, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var text = document.GetText();
            return ValueTask.FromResult<IReadOnlyList<TextEdit>?>([new TextEdit(new TextRange(TextPosition.Zero, document.PositionAt(text.Length)), text.ToUpperInvariant())]);
        }
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task After_delay_saves_without_format_on_save_and_keeps_the_carets_line_while_a_manual_save_formats()
    {
        await using var host = RealAppHost.Create(h =>
        {
            AddSettings(h, """
                "vim.enable": false,
                "files.autoSave": "afterDelay",
                "files.autoSaveDelay": 150,
                "editor.formatOnSave": true,
                "files.trimTrailingWhitespace": true
                """);
            h.Files.WriteFile("a.txt", "keep  \ntwo\n");
        });
        await host.StartAsync();
        var formatter = new UpperCaseFormatter();
        using var registration = host.Get<ILanguageFeatureRegistry>().Register<IDocumentFormattingProvider>(DocumentSelector.All, formatter);
        var path = host.Files.Combine("a.txt");
        var tab = await host.Shell.Well.OpenAsync(new Uri(path));
        Assert.True(await RealAppHost.WaitFor(() => tab.Controller is not null), "the editor was not presented");

        tab.Doc.ApplyEdits([new TextEdit(TextRange.At(1, 3), " ")]); // "two " with the caret after the space just typed
        tab.Controller!.SetSelection(TextRange.At(1, 4));

        Assert.True(await RealAppHost.WaitFor(() => !tab.Doc.IsDirty, 10_000), "not auto-saved");
        Assert.Equal("keep\ntwo \n", Read(path)); // the other line trimmed, the caret's kept, no formatting
        Assert.Equal(0, formatter.Calls);

        await host.RunAsync(KnownCommands.Save); // the user's own save formats, as before
        Assert.Equal("KEEP\nTWO\n", Read(path));
        Assert.Equal(1, formatter.Calls);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_delayed_save_waits_until_the_changed_on_disk_question_is_answered()
    {
        await using var host = RealAppHost.Create(h =>
        {
            AddSettings(h, """
                "vim.enable": false,
                "files.autoSaveDelay": 100
                """);
            h.Files.WriteFile("a.txt", "disk v1\n");
        });
        await host.StartAsync();
        var path = host.Files.Combine("a.txt");
        var tab = await host.Shell.Well.OpenAsync(new Uri(path));
        var doc = (EditorDocument)tab.Doc;
        doc.ApplyEdits([new TextEdit(TextRange.At(0, 0), "mine ")]);

        File.WriteAllText(path, "disk v2, written outside\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.True(await RealAppHost.WaitFor(() => doc.IsExternalChangePending, 30_000), "the changed-on-disk question did not come");

        await host.Get<IConfigurationService>().UpdateAsync(SettingKeys.FilesAutoSave, "afterDelay"); // turning it on would save at once
        await DevScene.SettleAsync(5);
        var until = DateTime.UtcNow.AddMilliseconds(800);
        while (DateTime.UtcNow < until) await DevScene.SettleAsync(2);
        Assert.Equal("disk v2, written outside\n", Read(path)); // not while the user is being asked
        Assert.True(doc.IsDirty);

        var question = host.Get<NotificationCenter>().History.Concat(host.Get<NotificationCenter>().Toasts)
            .First(n => n.Message.Contains("has changed on disk", StringComparison.Ordinal) && !n.IsCompleted);
        question.InvokeCommand.Execute(question.Actions.Single(a => a.Title == "Keep my changes"));
        Assert.True(await RealAppHost.WaitFor(() => Read(path) == "mine disk v1\n", 10_000), Read(path)); // saved once answered
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task On_focus_change_saves_the_editor_when_the_palette_takes_the_keyboard()
    {
        await using var host = RealAppHost.Create(h =>
        {
            AddSettings(h, """
                "vim.enable": false,
                "files.autoSave": "onFocusChange"
                """);
            h.Files.WriteFile("a.txt", "one\n");
        });
        await host.StartAsync();
        var path = host.Files.Combine("a.txt");
        var tab = await host.Shell.Well.OpenAsync(new Uri(path));
        Assert.True(await RealAppHost.WaitFor(() => tab.Controller is not null), "the editor was not presented");
        tab.Controller!.Focus();
        Assert.True(await RealAppHost.WaitFor(() => tab.Controller.IsFocused), "the editor did not take the keyboard");

        tab.Doc.ApplyEdits([new TextEdit(TextRange.At(0, 3), " two")]);
        await DevScene.SettleAsync(5);
        Assert.Equal("one\n", Read(path)); // typing alone saves nothing in this mode

        var palette = await host.OpenPaletteAsync(KnownCommands.CommandPalette);
        Assert.True(await RealAppHost.WaitFor(() => Read(path) == "one two\n", 10_000), Read(path));
        await host.ClosePaletteAsync(palette);
        Assert.False(tab.Doc.IsDirty);
    }
}
