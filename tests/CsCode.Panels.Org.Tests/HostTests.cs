using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CsCode.Core.Contributions;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Org.Engine;
using CsCode.Panels.Org.Language;
using CsCode.Panels.Org.ViewModels;
using CsCode.Panels.Org.Views;
using CsCode.Platform.FileSystem;
using CsCode.Platform.Io;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;
using Xunit;

namespace CsCode.Panels.Org.Tests;

/// <summary>An org folder in a temp directory with the services the host uses (the real local file system, fakes for the rest).</summary>
internal sealed class OrgFixture : IDisposable
{
    private readonly IoLane _lane = new("org-tests", maxThreads: 2);
    private readonly ServiceProvider _sp;

    public OrgFixture(DateTime? now = null)
    {
        Dir = new TempDirectory("cscode-org-");
        Config.Set(OrgSettings.Directory, Dir.Path);
        Documents = new DocumentManager();
        Navigation = new RecordingNavigationService(Documents);
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(new LocalFileSystem(_lane));
        services.AddSingleton<IConfigurationService>(Config);
        services.AddSingleton<IDocumentManager>(Documents);
        services.AddSingleton<INavigationService>(Navigation);
        services.AddSingleton<IWindowService>(Window);
        services.AddSingleton<IStatusBarService>(StatusBar);
        services.AddSingleton<ICommandService>(Commands);
        services.AddSingleton<IContextKeyService>(new TestContextKeyService());
        services.AddSingleton<IContributionRegistry>(Contributions);
        services.AddSingleton<ILanguageFeatureRegistry>(Features);
        _sp = services.BuildServiceProvider();
        Host = new OrgHost(_sp);
        var fixedNow = now ?? new DateTime(2026, 11, 3, 14, 5, 0);
        Host.Now = () => fixedNow;
    }

    public TempDirectory Dir { get; }
    public TestConfigurationService Config { get; } = new();
    public DocumentManager Documents { get; }
    public RecordingNavigationService Navigation { get; }
    public RecordingWindowService Window { get; } = new();
    public InMemoryStatusBarService StatusBar { get; } = new();
    public TestCommandService Commands { get; } = new();
    public ContributionRegistry Contributions { get; } = new();
    public LanguageFeatureRegistry Features { get; } = new();
    public OrgHost Host { get; }

    public string Write(string name, string text) => Dir.WriteFile(name, text);

    public string Read(string name) => File.ReadAllText(Dir.Combine(name));

    public async Task<OrgDocument> LoadAsync(string name)
    {
        await Host.Store!.RefreshAsync();
        return Host.Store.Documents.Single(d => d.FileName == name);
    }

    /// <summary>An open editor document over <paramref name="text"/> for the file <paramref name="name"/>, active with the caret at <paramref name="caret"/>.</summary>
    public InMemoryTextDocument Open(string name, string text, TextPosition caret)
    {
        var doc = new InMemoryTextDocument(new Uri(Dir.Combine(name)), OrgLanguage.LanguageId, text);
        var editor = new EditorDocumentView(doc);
        Documents.Register(editor); // as the shell registers an editor's document
        Navigation.ActiveDocument = editor;
        Navigation.ActiveCaret = caret;
        return doc;
    }

    public void Dispose()
    {
        Host.Dispose();
        _sp.Dispose();
        _lane.Dispose();
        Dir.Dispose();
    }
}

public sealed class StoreTests
{
    [AvaloniaFact]
    public async Task The_agenda_reads_the_org_files_of_its_folder_only()
    {
        using var f = new OrgFixture();
        f.Write("work.org", "* TODO A\n");
        f.Write("home.org", "* TODO B\n");
        f.Write(".#work.org", "lock");
        f.Write("#home.org#", "autosave");
        f.Write("notes.txt", "* TODO not org\n");
        f.Write("sub/deep.org", "* TODO C\n");
        await f.Host.Store!.RefreshAsync();
        Assert.Equal(["home.org", "work.org"], f.Host.Store.Documents.Select(d => d.FileName));
        Assert.Equal("B", f.Host.Store.Documents[0].Headings[0].Title);
    }

    [AvaloniaFact]
    public async Task Agenda_files_can_name_files_and_folders_and_report_the_missing()
    {
        using var f = new OrgFixture();
        var single = f.Write("projects/plan.org", "* TODO Plan\n");
        f.Config.Set(OrgSettings.AgendaFiles, new[] { single, f.Dir.Combine("nowhere") });
        await f.Host.Store!.RefreshAsync();
        Assert.Equal(["plan.org"], f.Host.Store.Documents.Select(d => d.FileName));
        Assert.Equal([f.Dir.Combine("nowhere")], f.Host.Store.Missing);
        Assert.True(f.Host.Store.IsTracked(new Uri(single)));
        Assert.False(f.Host.Store.IsTracked(new Uri(f.Dir.Combine("other.org"))));
    }

    [AvaloniaFact]
    public async Task An_open_editors_text_wins_over_the_disk()
    {
        using var f = new OrgFixture();
        f.Write("a.org", "* TODO On disk\n");
        f.Open("a.org", "* TODO In the editor\n", TextPosition.Zero);
        Assert.Equal("In the editor", (await f.LoadAsync("a.org")).Headings[0].Title);
    }

    [AvaloniaFact]
    public async Task Writing_edits_keeps_line_endings_and_byte_order_mark_and_refuses_a_changed_file()
    {
        using var f = new OrgFixture();
        var path = f.Dir.Combine("crlf.org");
        await File.WriteAllBytesAsync(path, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("* TODO Task\r\nNotes\r\n")], TestContext.Current.CancellationToken);
        var doc = await f.LoadAsync("crlf.org");
        var edit = OrgEdits.SetKeyword(doc, doc.Headings[0], "DONE", new DateTime(2026, 11, 3, 9, 0, 0));
        Assert.True(await f.Host.Store!.WriteEditsAsync(new Uri(path), doc, edit.Edits));
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        Assert.Equal("* DONE Task\r\nCLOSED: [2026-11-03 Tue 09:00]\r\nNotes\r\n", Encoding.UTF8.GetString(bytes[3..]));
        Assert.False(await f.Host.Store.WriteEditsAsync(new Uri(path), doc, edit.Edits)); // doc is the text before
    }
}

public sealed class HostTests
{
    [AvaloniaFact]
    public async Task Marking_an_entry_of_a_closed_file_done_writes_the_file()
    {
        using var f = new OrgFixture();
        f.Write("todo.org", "* TODO Buy milk\n  SCHEDULED: <2026-11-03 Tue>\n");
        var doc = await f.LoadAsync("todo.org");
        Assert.True(await f.Host.EditEntryAsync(doc, doc.Headings[0], (d, h) => OrgEdits.SetKeyword(d, h, "DONE", f.Host.Now())));
        Assert.Equal("* DONE Buy milk\n  CLOSED: [2026-11-03 Tue 14:05] SCHEDULED: <2026-11-03 Tue>\n", f.Read("todo.org"));
    }

    [AvaloniaFact]
    public async Task An_entry_moved_since_the_agenda_read_it_is_found_by_its_title()
    {
        using var f = new OrgFixture();
        f.Write("todo.org", "* TODO Buy milk\n");
        var doc = await f.LoadAsync("todo.org");
        f.Write("todo.org", "* Inserted above\n* TODO Buy milk\n");
        Assert.True(await f.Host.EditEntryAsync(doc, doc.Headings[0], (d, h) => OrgEdits.SetPriority(d, h, 'A')));
        Assert.Equal("* Inserted above\n* TODO [#A] Buy milk\n", f.Read("todo.org"));
        f.Write("todo.org", "* Something else\n");
        Assert.False(await f.Host.EditEntryAsync(doc, doc.Headings[0], (d, h) => OrgEdits.SetPriority(d, h, 'B')));
    }

    [AvaloniaFact]
    public async Task An_entry_of_an_open_file_is_edited_in_its_editor()
    {
        using var f = new OrgFixture();
        f.Write("todo.org", "* TODO Buy milk\n");
        var open = f.Open("todo.org", "* TODO Buy milk\n", TextPosition.Zero);
        var doc = await f.LoadAsync("todo.org");
        Assert.True(await f.Host.EditEntryAsync(doc, doc.Headings[0], (d, h) => OrgEdits.SetTags(d, h, ["shop"])));
        Assert.EndsWith(":shop:\n", open.GetText(), StringComparison.Ordinal);
        Assert.Equal("* TODO Buy milk\n", f.Read("todo.org"));
    }

    [AvaloniaFact]
    public async Task Write_notes_opens_the_entry_on_a_new_line_after_its_text()
    {
        using var f = new OrgFixture();
        const string text = "* TODO One\nSCHEDULED: <2026-11-03 Tue>\nfirst note\n\n* Two\n";
        f.Write("a.org", text);
        var doc = await f.LoadAsync("a.org");
        var open = f.Open("a.org", text.Replace("first note", "first note, edited", StringComparison.Ordinal), TextPosition.Zero);
        await f.Host.EditNotesAsync(doc, doc.Headings[0]);
        Assert.Equal("* TODO One\nSCHEDULED: <2026-11-03 Tue>\nfirst note, edited\n\n\n* Two\n", open.GetText());
        Assert.Equal(new TextPosition(3, 0), f.Navigation.Opened[^1].Selection!.Value.Start);
    }

    [AvaloniaFact]
    public async Task Editor_commands_act_on_the_entry_at_the_caret()
    {
        using var f = new OrgFixture();
        var doc = f.Open("a.org", "* First\n* Second\nbody\n", new TextPosition(2, 0));
        await f.Commands.ExecuteAsync(OrgHost.Todo);
        Assert.Equal("* First\n* TODO Second\nbody\n", doc.GetText());
        f.Window.InputResponses.Enqueue("fri 10:00");
        await f.Commands.ExecuteAsync(OrgHost.Schedule);
        Assert.Equal("* First\n* TODO Second\nSCHEDULED: <2026-11-06 Fri 10:00>\nbody\n", doc.GetText());
        f.Window.QuickPickResponses.Enqueue(0);
        await f.Commands.ExecuteAsync(OrgHost.Priority);
        Assert.Equal("* First\n* TODO [#A] Second\nSCHEDULED: <2026-11-06 Fri 10:00>\nbody\n", doc.GetText());
        f.Navigation.ActiveCaret = new TextPosition(1, 3);
        await f.Commands.ExecuteAsync(OrgHost.MoveSubtreeUp);
        Assert.Equal("* TODO [#A] Second\nSCHEDULED: <2026-11-06 Fri 10:00>\nbody\n* First\n", doc.GetText());
        Assert.Equal(new TextPosition(0, 3), f.Navigation.Opened[^1].Selection!.Value.Start);
        f.Window.InputResponses.Enqueue(""); // a deadline that was not set: empty means today
        await f.Commands.ExecuteAsync(OrgHost.Deadline);
        Assert.Contains("DEADLINE: <2026-11-03 Tue> SCHEDULED: <2026-11-06 Fri 10:00>", doc.GetText(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Capture_creates_the_notes_file_and_appends_a_task()
    {
        using var f = new OrgFixture();
        f.Window.QuickPickResponses.Enqueue(0); // Task
        f.Window.InputResponses.Enqueue("Call the plumber");
        await f.Host.CaptureAsync(day: new DateOnly(2026, 11, 4));
        Assert.Equal("#+TITLE: Notes\n\n* TODO Call the plumber\nSCHEDULED: <2026-11-04 Wed>\n[2026-11-03 Tue 14:05]\n", f.Read("notes.org"));
        Assert.Contains(f.Host.Store!.Documents, d => d.FileName == "notes.org");

        f.Window.QuickPickResponses.Enqueue(2); // Event
        f.Window.InputResponses.Enqueue("Dentist");
        f.Window.InputResponses.Enqueue("nov 12 9am");
        await f.Host.CaptureAsync();
        Assert.EndsWith("* Dentist\n<2026-11-12 Thu 09:00>\n", f.Read("notes.org"), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_status_bar_counts_what_is_due_today()
    {
        using var f = new OrgFixture();
        f.Write("a.org", "* TODO Late\nDEADLINE: <2026-11-01 Sun>\n* TODO Today\nSCHEDULED: <2026-11-03 Tue>\n* TODO Later\nSCHEDULED: <2026-11-09 Mon>\n");
        await f.Host.Store!.RefreshAsync();
        await WaitUntil(() => f.StatusBar.Items.Any(i => i.Id == "org.agenda"));
        var item = f.StatusBar.Items.Single(i => i.Id == "org.agenda");
        Assert.Equal("$(checklist) 2", item.Text);
        Assert.Contains("1 overdue", item.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_language_folds_headings_drawers_and_blocks_and_outlines_headings()
    {
        var doc = OrgDocument.Parse("* A\ntext\n:PROPERTIES:\n:ID: 1\n:END:\n** A1\n#+BEGIN_SRC sh\necho\n#+END_SRC\n\n* B\n");
        Assert.Equal([(0, 8), (5, 8), (2, 4), (6, 8)], OrgLanguage.FoldingRanges(doc).Select(r => (r.StartLine, r.EndLine)));
        var symbols = OrgLanguage.Symbols(doc);
        Assert.Equal(["A", "B"], symbols.Select(s => s.Name));
        Assert.Equal(["A1"], symbols[0].Children!.Select(s => s.Name));
        Assert.Equal(8, symbols[0].Range.End.Line);
    }

    [AvaloniaFact]
    public void The_language_registers_its_folding_outline_and_keyword_colours()
    {
        using var f = new OrgFixture();
        var doc = new InMemoryTextDocument(new Uri(f.Dir.Combine("x.org")), OrgLanguage.LanguageId, "* TODO a\n");
        Assert.Single(f.Features.GetProviders<IFoldingRangeProvider>(doc));
        Assert.Single(f.Features.GetProviders<IDocumentSymbolProvider>(doc));
        Assert.Single(f.Features.GetProviders<ISemanticTokensProvider>(doc));
        Assert.Contains(f.Contributions.Of<LanguageContribution>().Items, c => c.Value.Id == "org" && c.Value.Extensions.Contains(".org_archive"));
    }

    [Fact]
    public void Keys_are_emacs_chords_on_a_mac_and_ctrl_alt_o_elsewhere()
    {
        var todo = OrgHost.Keybindings.Single(k => k.Command == OrgHost.Todo);
        Assert.Equal("ctrl+c ctrl+t", todo.Mac);
        Assert.Equal("ctrl+alt+o ctrl+t", todo.Key);
        Assert.Contains("editorLangId == 'org'", todo.When, StringComparison.Ordinal);
        Assert.Contains(OrgHost.HeadingAtCursorKey, OrgHost.Keybindings.Single(k => k.Command == OrgHost.MoveSubtreeUp).When, StringComparison.Ordinal);
        Assert.Equal("!terminalFocus", OrgHost.Keybindings.Single(k => k.Command == OrgHost.ShowAgenda).When);
    }

    internal static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > until) throw new TimeoutException("condition not met");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }
}

public sealed class GrammarTests
{
    private static readonly Lazy<IGrammar> Grammar = new(() => new Registry(new Options()).LoadGrammar(OrgLanguage.ScopeName));

    private sealed class Options : IRegistryOptions
    {
        private readonly RegistryOptions _builtin = new(ThemeName.DarkPlus);

        public IRawGrammar GetGrammar(string scopeName)
        {
            if (scopeName != OrgLanguage.ScopeName) return _builtin.GetGrammar(scopeName);
            using var reader = new StreamReader(Path.Combine(OrgLanguage.ResourceDirectory, "org.tmLanguage.json"));
            return GrammarReader.ReadGrammarSync(reader);
        }

        public ICollection<string> GetInjections(string scopeName) => null!;
        public IRawTheme GetTheme(string scopeName) => _builtin.GetTheme(scopeName);
        public IRawTheme GetDefaultTheme() => _builtin.GetDefaultTheme();
    }

    /// <summary>The scopes of the token that covers <paramref name="text"/>'s first character in each line.</summary>
    private static List<string> ScopesAt(string[] lines, int line, string text)
    {
        IStateStack? state = null;
        List<string> found = [];
        for (var i = 0; i <= line; i++)
        {
            var r = Grammar.Value.TokenizeLine(lines[i], state, TimeSpan.FromSeconds(5));
            state = r.RuleStack;
            if (i != line) continue;
            var at = lines[i].IndexOf(text, StringComparison.Ordinal);
            Assert.True(at >= 0, $"'{text}' is not on line {line}");
            found = [.. r.Tokens.First(t => t.StartIndex <= at && at < t.EndIndex).Scopes];
        }
        return found;
    }

    [Fact]
    public void Headings_keywords_dates_tags_and_markup_get_their_scopes()
    {
        string[] lines =
        [
            "** TODO [#A] Write *the* report   :work:",
            "   DEADLINE: <2026-11-05 Thu> CLOSED: [2026-11-01 Sun]",
            "* DONE Old",
            "- [X] item with =code= and a [[https://example.com][link]]",
            "#+TITLE: Notes",
            "#+BEGIN_SRC python",
            "def f(): pass",
            "#+END_SRC",
        ];
        Assert.Contains("markup.heading.2.org", ScopesAt(lines, 0, "Write"));
        Assert.Contains("entity.name.type.org", ScopesAt(lines, 0, "Write")); // level 2's colour
        Assert.Contains("keyword.control.todo.org", ScopesAt(lines, 0, "TODO"));
        Assert.Contains("constant.other.priority.org", ScopesAt(lines, 0, "[#A]"));
        Assert.Contains("markup.bold.org", ScopesAt(lines, 0, "the"));
        Assert.Contains("entity.other.attribute-name.tag.org", ScopesAt(lines, 0, ":work:"));
        Assert.Contains("keyword.other.planning.org", ScopesAt(lines, 1, "DEADLINE"));
        Assert.Contains("constant.other.timestamp.active.org", ScopesAt(lines, 1, "<2026"));
        Assert.Contains("comment.timestamp.inactive.org", ScopesAt(lines, 1, "[2026"));
        Assert.Contains("comment.done.org", ScopesAt(lines, 2, "DONE"));
        Assert.Contains("constant.other.checkbox.org", ScopesAt(lines, 3, "[X]"));
        Assert.Contains("string.other.verbatim.org", ScopesAt(lines, 3, "code"));
        Assert.Contains("markup.underline.link.org", ScopesAt(lines, 3, "[[https"));
        Assert.Contains("markup.heading.title.org", ScopesAt(lines, 4, "Notes"));
        Assert.Contains("source.python", string.Join(' ', ScopesAt(lines, 6, "def")), StringComparison.Ordinal);
    }
}

public sealed class AgendaViewTests
{
    [AvaloniaFact]
    public async Task The_week_lists_days_and_entries_and_space_marks_one_done()
    {
        using var f = new OrgFixture();
        f.Write("work.org", "* TODO [#A] Ship it   :release:\nSCHEDULED: <2026-11-03 Tue>\n* TODO Review\nDEADLINE: <2026-11-05 Thu>\n");
        await f.Host.Store!.RefreshAsync();
        var view = new OrgAgendaView(f.Host.CreateViewModel(), f.Host.ReleaseViewModel, f.Host);
        var window = new Window { Content = view, Width = 360, Height = 700 };
        window.Show();
        try
        {
            var vm = view.Model;
            await HostTests.WaitUntil(() => vm.Rows.OfType<OrgItemRow>().Any());
            Assert.Equal("Week 45 · 2 – 8 November 2026", vm.Title);
            var headers = vm.Rows.OfType<OrgHeaderRow>().Select(h => h.Title).ToList();
            Assert.Equal(7, headers.Count);
            Assert.Equal("Today · Tuesday 3 November", headers[1]);
            var ship = vm.Rows.OfType<OrgItemRow>().First(r => r.Title == "Ship it");
            Assert.Contains("Scheduled", ship.Detail, StringComparison.Ordinal);
            Assert.Contains(":release:", ship.Detail, StringComparison.Ordinal);
            Assert.Contains(vm.Rows.OfType<OrgItemRow>(), r => r.Title == "Review" && r.Detail.StartsWith("Due in 2 d.", StringComparison.Ordinal));

            vm.Selected = ship;
            Dispatcher.UIThread.RunJobs();
            view.List.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, KeySymbol = " " });
            await HostTests.WaitUntil(() => f.Read("work.org").StartsWith("* DONE", StringComparison.Ordinal));
            Assert.StartsWith("* DONE [#A] Ship it   :release:\nCLOSED: [2026-11-03 Tue 14:05] SCHEDULED: <2026-11-03 Tue>\n", f.Read("work.org"), StringComparison.Ordinal);

            vm.Mode = OrgAgendaMode.Todo;
            await HostTests.WaitUntil(() => vm.Rows.OfType<OrgItemRow>().Count() == 1);
            Assert.Equal("Review", vm.Rows.OfType<OrgItemRow>().Single().Title);
            vm.Filter = ":nothing";
            Assert.Empty(vm.Rows.OfType<OrgItemRow>());
        }
        finally
        {
            window.Close();
            view.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Without_org_files_the_window_offers_to_start_one()
    {
        using var f = new OrgFixture();
        await f.Host.Store!.RefreshAsync();
        using var vm = f.Host.CreateViewModel();
        Assert.True(vm.NoFiles);
        Assert.True(vm.IsEmpty);
        Assert.Contains("No org files in", vm.EmptyText, StringComparison.Ordinal);
    }
}

public sealed class FeatureHostTests
{
    [AvaloniaFact]
    public async Task Clock_in_and_out_from_the_editor_with_the_status_bar_showing_the_clock()
    {
        using var f = new OrgFixture();
        var doc = f.Open("a.org", "* TODO Write\nnotes\n", new TextPosition(1, 0));
        await f.Commands.ExecuteAsync(OrgHost.ClockIn);
        Assert.Equal("* TODO Write\n:LOGBOOK:\nCLOCK: [2026-11-03 Tue 14:05]\n:END:\nnotes\n", doc.GetText());
        Assert.Equal("Write", f.Host.RunningClock!.Heading.Title);
        await HostTests.WaitUntil(() => f.StatusBar.Items.Any(i => i.Id == "org.clock"));
        Assert.Equal("$(watch) 0:00 Write", f.StatusBar.Items.Single(i => i.Id == "org.clock").Text);

        f.Host.Now = () => new DateTime(2026, 11, 3, 15, 10, 0);
        await f.Commands.ExecuteAsync(OrgHost.ClockOut);
        Assert.Contains("CLOCK: [2026-11-03 Tue 14:05]--[2026-11-03 Tue 15:10] =>  1:05\n", doc.GetText(), StringComparison.Ordinal);
        Assert.Null(f.Host.RunningClock);
        await HostTests.WaitUntil(() => f.StatusBar.Items.All(i => i.Id != "org.clock"));
    }

    [AvaloniaFact]
    public async Task A_clock_left_running_in_an_agenda_file_is_the_running_clock()
    {
        using var f = new OrgFixture();
        f.Write("work.org", "* TODO Report\n:LOGBOOK:\nCLOCK: [2026-11-03 Tue 13:00]\n:END:\n");
        await f.Host.Store!.RefreshAsync();
        await HostTests.WaitUntil(() => f.Host.RunningClock is not null);
        Assert.Equal(new DateTime(2026, 11, 3, 13, 0, 0), f.Host.RunningClock!.Start);
        await f.Host.ClockOutAsync();
        Assert.Contains("=>  1:05", f.Read("work.org"), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Refile_moves_an_entry_to_a_heading_of_another_file()
    {
        using var f = new OrgFixture();
        f.Write("inbox.org", "* TODO Call Bob\nnotes\n* Keep\n");
        f.Write("projects.org", "* Projects\n** Website\n");
        var inbox = await f.LoadAsync("inbox.org");
        f.Window.QuickPickResponses.Enqueue(4); // inbox.org, inbox.org / Keep, projects.org, projects.org / Projects, projects.org / Projects / Website
        await f.Host.RefileAsync(inbox, inbox.Headings[0]);
        Assert.Equal("* Projects\n** Website\n*** TODO Call Bob\nnotes\n", f.Read("projects.org"));
        Assert.Equal("* Keep\n", f.Read("inbox.org"));
    }

    [AvaloniaFact]
    public async Task Archive_moves_the_subtree_to_the_archive_file()
    {
        using var f = new OrgFixture();
        f.Write("notes.org", "* DONE Old task\nCLOSED: [2026-11-01 Sun 10:00]\n* Keep\n");
        var doc = await f.LoadAsync("notes.org");
        await f.Host.ArchiveAsync(doc, doc.Headings[0]);
        Assert.Equal("* Keep\n", f.Read("notes.org"));
        var archive = f.Read("notes.org_archive");
        Assert.StartsWith("#    -*- mode: org -*-\n\n\nArchived entries from file " + f.Dir.Combine("notes.org"), archive, StringComparison.Ordinal);
        Assert.EndsWith("* DONE Old task\nCLOSED: [2026-11-01 Sun 10:00]\n:PROPERTIES:\n:ARCHIVE_TIME: 2026-11-03 Tue 14:05\n:ARCHIVE_FILE: " + f.Dir.Combine("notes.org")
            + "\n:ARCHIVE_CATEGORY: notes\n:ARCHIVE_TODO: DONE\n:END:\n", archive, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_template_from_the_settings_captures_into_a_date_tree_and_opens_at_its_cursor()
    {
        using var f = new OrgFixture();
        f.Config.Set(OrgSettings.CaptureTemplates, new[] { new { key = "j", description = "Journal", template = "* %<%H:%M> %^{Title}\n%?", file = "journal.org", datetree = true } });
        f.Window.InputResponses.Enqueue("Standup");
        await f.Host.CaptureAsync(key: "j");
        Assert.Equal("#+TITLE: Journal\n\n* 2026\n** 2026-11 November\n*** 2026-11-03 Tuesday\n**** 14:05 Standup\n\n", f.Read("journal.org"));
        Assert.Equal(new TextPosition(6, 0), f.Navigation.Opened[^1].Selection!.Value.Start);
        Assert.Equal(["j"], f.Host.CaptureTemplatesList.Select(t => t.Key));
    }

    [AvaloniaFact]
    public async Task Export_writes_the_html_and_markdown_next_to_the_file()
    {
        using var f = new OrgFixture();
        f.Write("plan.org", "#+TITLE: Plan\n* TODO Ship\nText.\n");
        f.Open("plan.org", "#+TITLE: Plan\n* TODO Ship\nText.\n", TextPosition.Zero);
        await f.Commands.ExecuteAsync(OrgHost.ExportMarkdown);
        Assert.Equal("# Plan\n\n## TODO Ship\n\nText.\n", f.Read("plan.md"));
        await f.Commands.ExecuteAsync(OrgHost.ExportHtml);
        Assert.Contains("<h2><span class=\"todo\">TODO</span> Ship</h2>", f.Read("plan.html"), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Ctrl_c_ctrl_c_in_a_code_block_runs_it_after_asking_and_writes_its_results()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new OrgFixture();
        var doc = f.Open("code.org", "* Try\n#+BEGIN_SRC sh :var who=org\necho \"hello $who\"\n#+END_SRC\n", new TextPosition(2, 0));
        f.Window.MessageActionResponses.Enqueue("Cancel");
        await f.Commands.ExecuteAsync(OrgHost.CtrlCCtrlC);
        Assert.DoesNotContain("#+RESULTS:", doc.GetText(), StringComparison.Ordinal);
        f.Window.MessageActionResponses.Enqueue("Run");
        await f.Commands.ExecuteAsync(OrgHost.CtrlCCtrlC);
        Assert.EndsWith("#+END_SRC\n\n#+RESULTS:\n: hello org\n", doc.GetText(), StringComparison.Ordinal);
        Assert.Contains(f.Window.Messages, m => m.Message.Contains("Run this sh code block of code.org", StringComparison.Ordinal));
    }
}
