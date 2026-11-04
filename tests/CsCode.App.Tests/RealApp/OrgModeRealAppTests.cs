using System.Globalization;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Org;
using CsCode.Panels.Org.ViewModels;
using CsCode.Panels.Org.Views;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Org mode in the REAL app (docs/ORG.md): a <c>.org</c> file opens as the org language, the Org Agenda window lists the org folder's entries
/// and the status bar counts what is due, and org's keys (over the real keymap and keyboard pipeline) edit the entry at the caret: the structure
/// keys only on a heading line, the C-c chords (Ctrl+Alt+O off a Mac) anywhere in the entry.
/// </summary>
public class OrgModeRealAppTests
{
    private static string Day(int offset) => "<" + DateTime.Today.AddDays(offset).ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture) + ">";

    [AvaloniaFact(Timeout = 90_000)]
    public async Task The_agenda_lists_the_org_folder_and_org_keys_edit_the_open_file()
    {
        var work = $"""
            #+TITLE: Work
            * TODO [#A] Ship the release   :release:
            SCHEDULED: {Day(0)}
            Checklist for the release.
            - [ ] tag
            - [ ] notes
            * TODO Review the design doc
            DEADLINE: {Day(2)}
            * WAITING Answer from legal
            SCHEDULED: {Day(-1)}
            * Standup
            {Day(0)[..^1]} 09:30 +1d>

            """;
        await using var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("org/work.org", work);
            h.Files.WriteFile("org/home.org", $"* TODO Pay rent\nDEADLINE: {Day(-2)}\n* DONE Book flights\nCLOSED: [2026-11-01 Sun 10:00]\n");
            h.Home.WriteFile("config/settings.json", "{ \"workbench.startupEditor\": \"none\", \"org.directory\": \"" + h.Files.Combine("org").Replace("\\", "\\\\", StringComparison.Ordinal) + "\" }");
        });
        await host.StartAsync([host.Files.Combine("org", "work.org")]);
        var navigation = host.Get<INavigationService>();
        Assert.True(await RealAppHost.WaitFor(() => navigation.ActiveDocument?.LanguageId == "org"), "work.org did not open as org");

        // ---- the agenda
        await host.RunAsync(OrgHost.ShowAgenda);
        OrgAgendaView? View() => host.Window.GetVisualDescendants().OfType<OrgAgendaView>().FirstOrDefault();
        OrgAgendaViewModel? Agenda() => View() is { IsEffectivelyVisible: true } v ? v.DataContext as OrgAgendaViewModel : null;
        Assert.True(await RealAppHost.WaitFor(() => Agenda()?.Rows.OfType<OrgItemRow>().Any() == true), "the agenda shows no entries");
        var rows = Agenda()!.Rows.OfType<OrgItemRow>().Where(r => r.Item.Day == DateOnly.FromDateTime(DateTime.Today)).Select(r => r.Title).ToList();
        Assert.Equal(["Standup", "Pay rent", "Answer from legal", "Ship the release", "Review the design doc"], rows);
        Assert.True(await RealAppHost.WaitFor(() => host.Get<IStatusBarService>().Items.Any(i => i.Id == "org.agenda")));
        Assert.Equal("$(checklist) 3", host.Get<IStatusBarService>().Items.Single(i => i.Id == "org.agenda").Text);
        Capture(host, "org-agenda");

        // ---- org's keys in the editor
        var uri = new Uri(host.Files.Combine("org", "work.org"));
        var doc = navigation.ActiveDocument!;
        var keys = host.Get<IContextKeyService>();
        await navigation.OpenAsync(uri, TextRange.At(6, 0)); // "* TODO Review the design doc"
        Assert.True(await RealAppHost.WaitFor(() => keys.Get(OrgHost.HeadingAtCursorKey) is true), "orgHeadingAtCursor is not set on a heading");
        host.Window.KeyPress(Key.Up, RawInputModifiers.Alt, PhysicalKey.ArrowUp, null);
        Assert.True(await RealAppHost.WaitFor(() => doc.GetLineText(1) == "* TODO Review the design doc"), "Alt+Up did not move the subtree up: " + doc.GetText());
        // Tab on a heading folds it (org's TAB) instead of indenting
        var before = doc.GetText();
        host.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        await DevScene.SettleAsync(5);
        Assert.Equal(before, doc.GetText());
        host.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        await DevScene.SettleAsync(5);

        await navigation.OpenAsync(uri, TextRange.At(6, 3)); // "- [ ] tag", in "Ship the release" (now below the review)
        Assert.True(await RealAppHost.WaitFor(() => keys.Get(OrgHost.HeadingAtCursorKey) is false));
        Chord(host, Key.C);
        Assert.True(await RealAppHost.WaitFor(() => doc.GetText().Contains("- [X] tag", StringComparison.Ordinal)), "C-c C-c did not tick the box: " + doc.GetText());
        Chord(host, Key.T);
        Assert.True(await RealAppHost.WaitFor(() => doc.GetText().Contains("* NEXT [#A] Ship the release", StringComparison.Ordinal)), "C-c C-t did not cycle the state: " + doc.GetText());
        // the agenda follows the editor's unsaved text
        Assert.True(await RealAppHost.WaitFor(() => Agenda()?.Rows.OfType<OrgItemRow>().Any(r => r.Title == "Ship the release" && r.Keyword == "NEXT") == true),
            "the agenda did not follow the edit: " + string.Join(", ", Agenda()?.Rows.OfType<OrgItemRow>().Select(r => r.Keyword + " " + r.Title) ?? []));
        Capture(host, "org-editor");
    }

    private static string Logged(int offset) => "[" + DateTime.Today.AddDays(offset).ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture) + " 08:00]";

    [AvaloniaFact(Timeout = 90_000)]
    public async Task Clock_keys_code_blocks_habits_and_a_files_own_states_in_the_real_app()
    {
        var text = $"""
            #+TODO: OPEN WAIT | CLOSED
            * OPEN Exercise
            SCHEDULED: {Day(0)[..^1]} .+1d>
            :PROPERTIES:
            :STYLE: habit
            :END:
            :LOGBOOK:
            - State "CLOSED"     from "OPEN"       {Logged(-1)}
            - State "CLOSED"     from "OPEN"       {Logged(-3)}
            :END:
            * CLOSED Old
            * WAIT On hold
            #+BEGIN_SRC sh
            echo from babel
            #+END_SRC

            """;
        await using var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("org/habits.org", text);
            h.Home.WriteFile("config/settings.json", "{ \"workbench.startupEditor\": \"none\", \"org.babel.confirmEvaluate\": false, \"org.directory\": \""
                + h.Files.Combine("org").Replace("\\", "\\\\", StringComparison.Ordinal) + "\" }");
        });
        await host.StartAsync([host.Files.Combine("org", "habits.org")]);
        var navigation = host.Get<INavigationService>();
        Assert.True(await RealAppHost.WaitFor(() => navigation.ActiveDocument?.LanguageId == "org"));
        var doc = navigation.ActiveDocument!;
        var uri = new Uri(host.Files.Combine("org", "habits.org"));

        await host.RunAsync(OrgHost.ShowAgenda);
        OrgAgendaViewModel? Agenda() => host.Window.GetVisualDescendants().OfType<OrgAgendaView>().FirstOrDefault() is { IsEffectivelyVisible: true } v ? v.DataContext as OrgAgendaViewModel : null;
        Assert.True(await RealAppHost.WaitFor(() => Agenda()?.Rows.OfType<OrgItemRow>().Any(r => r.Title == "Exercise" && r.Habit is { Count: > 20 }) == true), "the habit has no graph");

        // C-c C-x C-i on "* WAIT On hold": a three-key chord
        await navigation.OpenAsync(uri, TextRange.At(11, 0));
        Keys(host, Key.X, Key.I);
        Assert.True(await RealAppHost.WaitFor(() => doc.GetText().Contains("* WAIT On hold\n:LOGBOOK:\nCLOCK: [", StringComparison.Ordinal)), "C-c C-x C-i did not clock in: " + doc.GetText());
        Assert.True(await RealAppHost.WaitFor(() => host.Get<IStatusBarService>().Items.Any(i => i.Id == "org.clock" && i.Text.EndsWith("On hold", StringComparison.Ordinal))));

        // C-c C-c in the code block runs it
        await navigation.OpenAsync(uri, TextRange.At(16, 2));
        Keys(host, Key.C);
        Assert.True(await RealAppHost.WaitFor(() => doc.GetText().Contains("#+END_SRC\n\n#+RESULTS:\n: from babel\n", StringComparison.Ordinal), 20_000),
            "C-c C-c did not run the block: " + doc.GetText());
        await DevScene.SettleAsync(20);
        Capture(host, "org-features");

        Keys(host, Key.X, Key.O);
        Assert.True(await RealAppHost.WaitFor(() => doc.GetText().Contains("] =>  0:0", StringComparison.Ordinal)), "C-c C-x C-o did not clock out: " + doc.GetText());
    }

    /// <summary>Org's C-c followed by Ctrl+each key (on a Mac), or Ctrl+Alt+O then Ctrl+each key (elsewhere).</summary>
    private static void Keys(RealAppHost host, params Key[] keys)
    {
        if (OperatingSystem.IsMacOS()) host.Window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
        else host.Window.KeyPress(Key.O, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.O, null);
        foreach (var key in keys)
        {
            Dispatcher.UIThread.RunJobs();
            host.Window.KeyPress(key, RawInputModifiers.Control, Enum.Parse<PhysicalKey>(key.ToString()), null);
        }
    }

    /// <summary>Org's C-c C-&lt;key&gt; (on a Mac) or Ctrl+Alt+O Ctrl+&lt;key&gt; (elsewhere), through the window's keyboard.</summary>
    private static void Chord(RealAppHost host, Key key)
    {
        if (OperatingSystem.IsMacOS()) host.Window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
        else host.Window.KeyPress(Key.O, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.O, null);
        Dispatcher.UIThread.RunJobs();
        host.Window.KeyPress(key, RawInputModifiers.Control, key == Key.C ? PhysicalKey.C : PhysicalKey.T, null);
    }

    private static void Capture(RealAppHost host, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        host.Window.CaptureRenderedFrame()?.Save(Path.Combine(DevHost.ScreenshotDirectory(), name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
