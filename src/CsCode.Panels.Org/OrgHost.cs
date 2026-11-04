using System.Text.RegularExpressions;
using CsCode.Core.Contributions;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Org.Engine;
using CsCode.Panels.Org.Language;
using CsCode.Panels.Org.ViewModels;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Org;

/// <summary>
/// The org module's composition point: registers the <c>org</c> language (grammar, configuration, folding, outline), the <c>org.*</c> settings and
/// commands with their Emacs keys, keeps the context key <c>orgHeadingAtCursor</c>, owns the <see cref="OrgStore"/> of the agenda's files and the
/// status bar item, and performs the edits the editor commands and the Org Agenda window share: on an open file in its editor (one undo step),
/// on a file that is not open on disk. The clock, refile and archive, export, code blocks and capture templates are in
/// <c>OrgHost.Features.cs</c>. Resolved at startup through the tool window.
/// </summary>
public sealed partial class OrgHost : IDisposable
{
    public const string ViewId = "org.agenda";
    public const string HeadingAtCursorKey = "orgHeadingAtCursor";

    // the agenda and capture (global)
    public const string ShowAgenda = "org.agenda";
    public const string Capture = "org.capture";
    public const string OpenNotes = "org.openNotes";
    public const string AddToAgenda = "org.addToAgenda";
    public const string RemoveFromAgenda = "org.removeFromAgenda";
    public const string RefreshAgenda = "org.agenda.refresh";
    // the entry at the caret (org files)
    public const string Todo = "org.todo";
    public const string SetTodoState = "org.setTodoState";
    public const string Schedule = "org.schedule";
    public const string Deadline = "org.deadline";
    public const string Priority = "org.priority";
    public const string SetTags = "org.setTags";
    public const string CtrlCCtrlC = "org.ctrlCCtrlC";
    public const string Timestamp = "org.timestamp";
    public const string TimestampInactive = "org.timestampInactive";
    public const string InsertHeading = "org.insertHeading";
    public const string InsertTodoHeading = "org.insertTodoHeading";
    public const string Promote = "org.promote";
    public const string Demote = "org.demote";
    public const string PromoteSubtree = "org.promoteSubtree";
    public const string DemoteSubtree = "org.demoteSubtree";
    public const string MoveSubtreeUp = "org.moveSubtreeUp";
    public const string MoveSubtreeDown = "org.moveSubtreeDown";
    public const string Cycle = "org.cycle";

    private static readonly Regex HeadingLineRe = new(@"^\*+[ \t]", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly DisposableStore _store = new();
    private readonly IUiDispatcher _ui;
    private readonly Emitter<IReadOnlyList<OrgDocument>> _agendaChanged = new();
    private IDisposable? _statusItem;
    private OrgTodoKeywords _keywords;
    private bool? _headingAtCursor;
    private bool _disposed;

    public OrgHost(IServiceProvider services)
    {
        _ui = services.GetService<IUiDispatcher>() ?? AvaloniaUiDispatcher.Instance;
        Config = services.GetService<IConfigurationService>();
        Commands = services.GetService<ICommandService>();
        Keys = services.GetService<IContextKeyService>();
        Window = services.GetService<IWindowService>();
        Navigation = services.GetService<INavigationService>();
        Views = services.GetService<IViewActivator>();
        StatusBar = services.GetService<IStatusBarService>();
        Documents = services.GetService<IDocumentManager>();
        _fs = services.GetService<IFileSystem>();
        _output = services.GetService<IOutputService>();
        _memory = services.GetService<Core.Diagnostics.IMemoryMonitor>();
        _keywords = OrgSettings.Keywords(Config);
        Language = new OrgLanguage(() => _keywords);

        if (Config is not null)
        {
            _store.Add(Config.RegisterProperties(OrgSettings.Properties()));
            _store.Add(Config.Changed.Subscribe(e => { if (e.Affects("org")) OnSettingsChanged(); }));
        }
        if (services.GetService<IFileSystem>() is { } fs)
        {
            Store = new OrgStore(fs, services.GetService<IFileWatcher>(), Documents,
                () => new OrgStoreOptions(OrgSettings.AgendaEntries(Config), _keywords));
            _store.Add(Store);
            _store.Add(Store.Changed.Subscribe(docs => _ui.Post(() => OnAgendaChanged(docs))));
            Store.ScheduleRefresh(1500); // after start-up's own work
        }
        if (Commands is not null) RegisterCommands(Commands);
        if (services.GetService<IContributionRegistry>() is { } contributions)
        {
            var dir = OrgLanguage.ResourceDirectory;
            _store.Add(contributions.Contribute(ContributionSources.Builtin, dir, [OrgLanguage.Language(dir)]));
            _store.Add(contributions.Contribute(ContributionSources.Builtin, dir, [OrgLanguage.Grammar(dir)]));
            _store.Add(contributions.Contribute(ContributionSources.Builtin, AppContext.BaseDirectory, Keybindings));
        }
        if (services.GetService<ILanguageFeatureRegistry>() is { } features)
        {
            var org = DocumentSelector.ForLanguage(OrgLanguage.LanguageId);
            _store.Add(features.Register<IFoldingRangeProvider>(org, Language, "org"));
            _store.Add(features.Register<IDocumentSymbolProvider>(org, Language, "org"));
            _store.Add(features.Register<ISemanticTokensProvider>(org, Language, "org")); // a file's own TODO states coloured
        }
        if (Navigation is not null)
        {
            _store.Add(Navigation.ActiveCaretChanged.Subscribe(_ => UpdateHeadingKey()));
            _store.Add(Navigation.ActiveDocumentChanged.Subscribe(_ => UpdateHeadingKey()));
        }
    }

    public IConfigurationService? Config { get; }
    public ICommandService? Commands { get; }
    public IContextKeyService? Keys { get; }
    public IWindowService? Window { get; }
    public INavigationService? Navigation { get; }
    public IViewActivator? Views { get; }
    public IStatusBarService? StatusBar { get; }
    public IDocumentManager? Documents { get; }
    public OrgLanguage Language { get; }
    /// <summary>The agenda's files (null without a file system: tests of other modules).</summary>
    public OrgStore? Store { get; }
    internal IUiDispatcher Ui => _ui;

    /// <summary>The TODO states of files without their own <c>#+TODO</c> line (from <c>org.todoKeywords</c>).</summary>
    public OrgTodoKeywords Keywords => _keywords;

    /// <summary>The store's documents changed (UI thread).</summary>
    public IEventSource<IReadOnlyList<OrgDocument>> AgendaChanged => _agendaChanged;

    /// <summary>The live Org Agenda view-model (null while the window is closed).</summary>
    public OrgAgendaViewModel? LiveView { get; private set; }

    internal OrgAgendaViewModel CreateViewModel()
    {
        var vm = new OrgAgendaViewModel(this);
        LiveView = vm;
        if (Store is { Loaded: false }) _ = Store.RefreshAsync();
        return vm;
    }

    internal void ReleaseViewModel(OrgAgendaViewModel vm)
    {
        if (ReferenceEquals(LiveView, vm)) LiveView = null;
        vm.Dispose();
    }

    /// <summary>Today, as the agenda sees it (tests replace the clock).</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.Now;

    public DateOnly Today => DateOnly.FromDateTime(Now());

    internal bool LogDone => Config?.Get<string>(OrgSettings.LogDone) is not "none";

    private void OnSettingsChanged()
    {
        _keywords = OrgSettings.Keywords(Config);
        Store?.ResetWatches();
        Store?.ScheduleRefresh(100);
        _ui.Post(UpdateStatusBar);
    }

    private void OnAgendaChanged(IReadOnlyList<OrgDocument> docs)
    {
        if (_disposed) return;
        UpdateClockFromStore();
        UpdateStatusBar();
        _agendaChanged.Fire(docs);
    }

    // ------------------------------------------------------------------------------------------------ status bar, context key

    /// <summary><c>$(checklist) 3</c>: entries scheduled or due today, or overdue, still to do (click: the agenda). Hidden when there are none.</summary>
    internal void UpdateStatusBar()
    {
        _statusItem?.Dispose();
        _statusItem = null;
        if (StatusBar is null || Store is null || Config?.Get(OrgSettings.StatusBar, true) == false) return;
        var today = Today;
        var day = OrgAgenda.Build(Store.Documents, today, today, today, new OrgAgendaOptions(DeadlineWarningDays, ShowDone: false)).FirstOrDefault();
        var due = day?.Items.Where(i => i.Kind is not (OrgAgendaKind.DeadlineUpcoming or OrgAgendaKind.Timestamp) && !i.Heading.IsDone).Select(i => i.Heading).Distinct().Count() ?? 0;
        if (due == 0) return;
        var overdue = day!.Items.Count(i => i.Kind is OrgAgendaKind.DeadlineOverdue or OrgAgendaKind.ScheduledOverdue);
        var tip = $"Org: {due} to do today" + (overdue > 0 ? $", {overdue} overdue" : "") + ". Show Agenda";
        _statusItem = StatusBar.Set(new StatusBarItemModel("org.agenda", $"$(checklist) {due}", tip, ShowAgenda, StatusBarAlignment.Left, 50));
    }

    internal int DeadlineWarningDays => Config?.Get(OrgSettings.DeadlineWarningDays, 14) ?? 14;

    private void UpdateHeadingKey()
    {
        if (Keys is null) return;
        var on = false;
        if (Navigation?.ActiveDocument is { LanguageId: OrgLanguage.LanguageId } doc && Navigation.ActiveCaret is { } caret && caret.Line < doc.LineCount)
            on = HeadingLineRe.IsMatch(doc.GetLineText(caret.Line));
        if (_headingAtCursor == on) return;
        _headingAtCursor = on;
        Keys.Set(HeadingAtCursorKey, on);
    }

    // ------------------------------------------------------------------------------------------------ keys

    private const string InOrg = "editorTextFocus && editorLangId == 'org'";
    private const string OnHeading = InOrg + " && " + HeadingAtCursorKey;

    // Emacs's keys on a Mac (Control is free there: Cmd copies); elsewhere Ctrl+C copies, so Ctrl+Alt+O stands in for org's C-c prefix.
    private static KeybindingContribution Chord(string command, string emacs, string? when = InOrg) =>
        new(command, "ctrl+alt+o " + emacs, Mac: "ctrl+c " + emacs, When: when);

    private const string Anywhere = "!terminalFocus";

    /// <summary>The module's key bindings (org's keys; docs/ORG.md lists them).</summary>
    public static IReadOnlyList<KeybindingContribution> Keybindings { get; } =
    [
        Chord(ShowAgenda, "a", "!terminalFocus"),
        Chord(Capture, "c", "!terminalFocus"),
        Chord(Todo, "ctrl+t"),
        Chord(Schedule, "ctrl+s"),
        Chord(Deadline, "ctrl+d"),
        Chord(Priority, ","),
        Chord(SetTags, "ctrl+q"),
        Chord(CtrlCCtrlC, "ctrl+c"),
        Chord(Timestamp, "."),
        new(InsertHeading, "alt+enter", When: InOrg + " && !editorReadonly"),
        new(InsertTodoHeading, "alt+shift+enter", When: InOrg + " && !editorReadonly"),
        new(Promote, "alt+left", When: OnHeading),
        new(Demote, "alt+right", When: OnHeading),
        new(PromoteSubtree, "alt+shift+left", When: OnHeading),
        new(DemoteSubtree, "alt+shift+right", When: OnHeading),
        new(MoveSubtreeUp, "alt+up", When: OnHeading),
        new(MoveSubtreeDown, "alt+down", When: OnHeading),
        new(Cycle, "tab", When: OnHeading + " && !editorHasSelection"),
        // the clock (C-c C-x ...: in or out from anywhere once a clock runs, as org's are global)
        Chord(ClockIn, "ctrl+x ctrl+i"),
        Chord(ClockOut, "ctrl+x ctrl+o", Anywhere),
        Chord(ClockCancel, "ctrl+x ctrl+q", Anywhere),
        Chord(ClockGoto, "ctrl+x ctrl+j", Anywhere),
        Chord(ClockReport, "ctrl+x ctrl+r"),
        // refile, archive, export, code blocks
        Chord(Refile, "ctrl+w"),
        Chord(ArchiveSubtree, "ctrl+x ctrl+s"),
        Chord(ToggleArchiveTag, "ctrl+x a"),
        Chord(Export, "ctrl+e"),
        Chord(BabelExecute, "ctrl+v e"),
        Chord(BabelExecuteBuffer, "ctrl+v b"),
    ];

    // ------------------------------------------------------------------------------------------------ commands

    private void RegisterCommands(ICommandService commands)
    {
        void Reg(string id, string title, Func<CommandInvocation, Task> run)
        {
            if (commands.Has(id)) return;
            _store.Add(commands.Register(id, async inv => { await run(inv).ConfigureAwait(false); return null; }, new CommandMetadata(id, title, "Org")));
        }
        Reg(ShowAgenda, "Agenda", async _ => { if (Views is not null) await Views.ShowViewAsync(ViewId).ConfigureAwait(false); });
        Reg(RefreshAgenda, "Refresh Agenda", async _ => { if (Store is not null) await Store.RefreshAsync().ConfigureAwait(false); });
        Reg(Capture, "Capture...", inv => CaptureAsync(day: inv.Arg<string>(0) is { } d && DateOnly.TryParse(d, System.Globalization.CultureInfo.InvariantCulture, out var date) ? date : null,
            key: inv.Arg<string>(1)));
        Reg(OpenNotes, "Open Notes File", _ => OpenNotesAsync());
        Reg(AddToAgenda, "Add File to Agenda", _ => SetInAgendaAsync(add: true));
        Reg(RemoveFromAgenda, "Remove File from Agenda", _ => SetInAgendaAsync(add: false));

        Reg(Todo, "Cycle TODO State", _ => OnEntryAsync((doc, h, _) => OrgEdits.CycleKeyword(doc, h, Now(), LogDone)));
        Reg(SetTodoState, "Set TODO State...", async _ =>
        {
            if (ActiveEntry() is not { } at) return;
            if (await PickTodoStateAsync(at.Heading.Keyword).ConfigureAwait(false) is not { } pick) return;
            await OnEntryAsync((doc, h, _) => OrgEdits.SetKeyword(doc, h, pick.Value, Now(), LogDone)).ConfigureAwait(false);
        });
        Reg(Schedule, "Schedule...", _ => PlanAtCaretAsync(OrgPlanningKind.Scheduled));
        Reg(Deadline, "Deadline...", _ => PlanAtCaretAsync(OrgPlanningKind.Deadline));
        Reg(Priority, "Set Priority...", async _ =>
        {
            if (ActiveEntry() is not { } at) return;
            if (await PickPriorityAsync(at.Heading.Priority).ConfigureAwait(false) is not { } pick) return;
            await OnEntryAsync((doc, h, _) => OrgEdits.SetPriority(doc, h, pick.Value)).ConfigureAwait(false);
        });
        Reg(SetTags, "Set Tags...", async _ =>
        {
            if (ActiveEntry() is not { } at) return;
            if (await PromptTagsAsync(at.Heading.Tags).ConfigureAwait(false) is not { } tags) return;
            await OnEntryAsync((doc, h, _) => OrgEdits.SetTags(doc, h, tags)).ConfigureAwait(false);
        });
        Reg(CtrlCCtrlC, "Run Code Block / Update Clock Table / Toggle Checkbox", _ => CtrlCCtrlCAsync());
        Reg(Timestamp, "Insert Timestamp...", _ => InsertTimestampAsync(active: true));
        Reg(TimestampInactive, "Insert Inactive Timestamp...", _ => InsertTimestampAsync(active: false));
        Reg(InsertHeading, "Insert Heading", _ => OnEntryAsync((doc, _, line) => OrgEdits.InsertHeading(doc, line, null), needsHeading: false));
        Reg(InsertTodoHeading, "Insert TODO Heading", _ => OnEntryAsync((doc, _, line) => OrgEdits.InsertHeading(doc, line, _keywords.NotDone.FirstOrDefault() ?? "TODO"), needsHeading: false));
        Reg(Promote, "Promote Heading", _ => LevelAsync(-1, subtree: false));
        Reg(Demote, "Demote Heading", _ => LevelAsync(+1, subtree: false));
        Reg(PromoteSubtree, "Promote Subtree", _ => LevelAsync(-1, subtree: true));
        Reg(DemoteSubtree, "Demote Subtree", _ => LevelAsync(+1, subtree: true));
        Reg(MoveSubtreeUp, "Move Subtree Up", _ => OnEntryAsync((doc, h, _) => OrgEdits.MoveSubtree(doc, h, up: true, Navigation?.ActiveCaret?.Character ?? 0)
            ?? Nothing("No earlier heading at this level to move past")));
        Reg(MoveSubtreeDown, "Move Subtree Down", _ => OnEntryAsync((doc, h, _) => OrgEdits.MoveSubtree(doc, h, up: false, Navigation?.ActiveCaret?.Character ?? 0)
            ?? Nothing("No later heading at this level to move past")));
        Reg(Cycle, "Toggle Fold of Heading", async _ => { if (Commands is { } c && c.Has("editor.toggleFold")) await c.ExecuteAsync("editor.toggleFold").ConfigureAwait(false); });
        RegisterFeatureCommands(Reg);
    }

    private OrgEditResult? Nothing(string message)
    {
        Status(message);
        return null;
    }

    private Task LevelAsync(int delta, bool subtree) =>
        OnEntryAsync((doc, h, _) => OrgEdits.ChangeLevel(doc, h, delta, subtree) ?? Nothing("A heading cannot go above level 1"));

    private void Status(string message) => Window?.SetStatusBarMessage(message, TimeSpan.FromSeconds(4));

    // ------------------------------------------------------------------------------------------------ the entry at the caret

    /// <summary>The org document in the active editor, parsed, with the caret and the entry it is in (null outside org files and above the first heading).</summary>
    internal (IEditorDocument Document, OrgDocument Parsed, OrgHeading Heading, TextPosition Caret)? ActiveEntry()
    {
        if (Navigation?.ActiveDocument is not { LanguageId: OrgLanguage.LanguageId } doc || Navigation.ActiveCaret is not { } caret) return null;
        var parsed = OrgDocument.Parse(doc.GetText(), _keywords, uri: doc.Uri);
        if (parsed.HeadingAt(caret.Line) is not { } h)
        {
            Status("Not in an org entry: there is no heading above the caret");
            return null;
        }
        return (doc, parsed, h, caret);
    }

    /// <summary>
    /// Runs an edit on the active org editor: reads its text and applies the result in one UI-thread step (so nothing typed in between is
    /// overwritten), as one undo step, then moves the caret where the edit says.
    /// </summary>
    private async Task OnEntryAsync(Func<OrgDocument, OrgHeading, int, OrgEditResult?> edit, bool needsHeading = true)
    {
        if (Navigation is not { } nav || nav.ActiveDocument is not { LanguageId: OrgLanguage.LanguageId } doc || doc.IsReadOnly) return;
        var result = await _ui.InvokeAsync(() =>
        {
            if (nav.ActiveCaret is not { } caret) return null;
            var parsed = OrgDocument.Parse(doc.GetText(), _keywords, uri: doc.Uri);
            var h = parsed.HeadingAt(caret.Line);
            if (h is null && needsHeading) { Status("Not in an org entry: there is no heading above the caret"); return null; }
            var r = edit(parsed, h!, caret.Line);
            if (r is null || r.Edits.Count == 0) return r;
            doc.ApplyEdits(OrgText.WithEol(r.Edits, doc.EndOfLine));
            return r;
        }).ConfigureAwait(false);
        if (result is null) return;
        if (result.Message is { } message) Status(message);
        if (result.Caret is { } at) await nav.OpenAsync(doc.Uri, new TextRange(at, at)).ConfigureAwait(false);
    }

    private async Task PlanAtCaretAsync(OrgPlanningKind kind)
    {
        if (ActiveEntry() is not { } at) return;
        var prompt = await PromptDateAsync(kind, at.Heading.PlanningOf(kind)).ConfigureAwait(false);
        if (prompt is not { } chosen) return;
        await OnEntryAsync((doc, h, _) => OrgEdits.SetPlanning(doc, h, kind, chosen.Value)).ConfigureAwait(false);
    }

    private async Task InsertTimestampAsync(bool active)
    {
        if (Navigation is not { } nav || nav.ActiveDocument is not { LanguageId: OrgLanguage.LanguageId } doc || doc.IsReadOnly || Window is null) return;
        var text = await Window.ShowInputBoxAsync(new InputBoxOptions(
            Title: active ? "Insert Timestamp" : "Insert Inactive Timestamp",
            Prompt: "Date: today, tomorrow, +3d, fri 10:00, nov 20, 2026-11-20 (+1w repeats). Empty: today.",
            Validate: s => string.IsNullOrWhiteSpace(s) || OrgDateInput.Parse(s, Today, out _) is not null ? null : Error(s))).ConfigureAwait(false);
        if (text is null || OrgDateInput.Parse(text, Today, out _) is not { } ts) return;
        if (!active) ts = ts with { Active = false };
        await _ui.InvokeAsync(() =>
        {
            if (nav.ActiveCaret is { } caret) doc.ApplyEdits([TextEdit.Insert(caret, ts.ToString())]);
        }).ConfigureAwait(false);
    }

    private string? Error(string input)
    {
        OrgDateInput.Parse(input, Today, out var error);
        return error;
    }

    // ------------------------------------------------------------------------------------------------ prompts

    /// <summary>A date for <paramref name="kind"/> in org's date syntax: null when cancelled, a null value to remove the current one.</summary>
    internal async Task<Choice<OrgTimestamp?>?> PromptDateAsync(OrgPlanningKind kind, OrgTimestamp? current, DateOnly? suggested = null)
    {
        if (Window is null) return null;
        var name = kind == OrgPlanningKind.Deadline ? "Deadline" : "Schedule";
        var value = current is not null ? current.ToString()[1..^1] : suggested?.ToString("yyyy-MM-dd ddd", System.Globalization.CultureInfo.InvariantCulture);
        var text = await Window.ShowInputBoxAsync(new InputBoxOptions(
            Title: name,
            Prompt: "Date: today, tomorrow, +3d, fri 10:00, nov 20, 2026-11-20; +1w repeats, -2d warns. " + (current is null ? "Empty: today." : "Empty: remove it."),
            Value: value,
            Validate: s => string.IsNullOrWhiteSpace(s) || OrgDateInput.Parse(s, Today, out _) is not null ? null : Error(s))).ConfigureAwait(false);
        if (text is null) return null;
        if (string.IsNullOrWhiteSpace(text)) return new Choice<OrgTimestamp?>(current is null ? new OrgTimestamp(true, Today) : null);
        return OrgDateInput.Parse(text, Today, out _) is { } ts ? new Choice<OrgTimestamp?>(ts) : null;
    }

    internal async Task<Choice<char?>?> PickPriorityAsync(char? current)
    {
        if (Window is null) return null;
        QuickPickItem Item(string label, string description, char? p) => new(label, description, Picked: p == current, Tag: p);
        var pick = await Window.ShowQuickPickAsync(
            [Item("A", "highest", 'A'), Item("B", "", 'B'), Item("C", "lowest", 'C'), Item("None", "remove the priority", null)],
            new QuickPickOptions(Title: "Priority", Placeholder: current is { } c ? $"Now [#{c}]" : "No priority yet")).ConfigureAwait(false);
        return pick is null ? null : new Choice<char?>(pick.Tag as char?);
    }

    internal async Task<Choice<string?>?> PickTodoStateAsync(string? current, OrgTodoKeywords? keywords = null)
    {
        if (Window is null) return null;
        keywords ??= _keywords;
        var items = keywords.All.Select(k => new QuickPickItem(k, keywords.IsDone(k) ? "done" : "to do", Picked: k == current, Tag: k)).ToList();
        items.Add(new QuickPickItem("None", "no TODO state", Picked: current is null));
        var pick = await Window.ShowQuickPickAsync(items, new QuickPickOptions(Title: "TODO State", Placeholder: current ?? "None")).ConfigureAwait(false);
        return pick is null ? null : new Choice<string?>(pick.Tag as string);
    }

    internal async Task<IReadOnlyList<string>?> PromptTagsAsync(IReadOnlyList<string> current)
    {
        if (Window is null) return null;
        var text = await Window.ShowInputBoxAsync(new InputBoxOptions(Title: "Tags", Prompt: "Tags separated by spaces or colons (empty: none)", Value: string.Join(' ', current))).ConfigureAwait(false);
        return text?.Split([' ', ':', ',', '\t'], StringSplitOptions.RemoveEmptyEntries);
    }

    // ------------------------------------------------------------------------------------------------ edits from the agenda

    /// <summary>
    /// Applies an edit to the entry <paramref name="heading"/> of <paramref name="snapshot"/> (a document of the agenda, maybe older than the
    /// file) through <see cref="EditFileAsync"/>. The entry is found again in the current text (same line and title, else the nearest heading
    /// with that title). False when it is gone.
    /// </summary>
    public async Task<bool> EditEntryAsync(OrgDocument snapshot, OrgHeading heading, Func<OrgDocument, OrgHeading, OrgEditResult?> edit)
    {
        if (snapshot.Uri is not { } uri) return false;
        var found = true;
        var result = await EditFileAsync(uri, doc =>
        {
            if (Resolve(doc, heading) is { } h) return edit(doc, h);
            found = false;
            return null;
        }).ConfigureAwait(false);
        if (!found)
        {
            Status("That entry is no longer in the file; the agenda is refreshed");
            if (Store is not null) await Store.RefreshAsync().ConfigureAwait(false);
            return false;
        }
        if (result?.Message is { } message) Status(message);
        return true;
    }

    /// <summary>
    /// Applies an edit computed from a file's current text: in its editor when the file is open (one undo step; saved afterwards when it had no
    /// unsaved changes and <c>org.agenda.saveAfterEdit</c>), else on disk (read again and recomputed when the file changed meanwhile). Returns
    /// the edit's result (null when it had nothing to do or the file cannot be read or written).
    /// </summary>
    public async Task<OrgEditResult?> EditFileAsync(Uri uri, Func<OrgDocument, OrgEditResult?> edit)
    {
        if (Documents?.Get(uri) is { } open)
        {
            var r = await _ui.InvokeAsync(() =>
            {
                var doc = OrgDocument.Parse(open.GetText(), _keywords, uri: uri);
                var r = edit(doc);
                if (r is null || r.Edits.Count == 0) return r;
                var wasDirty = open.IsDirty;
                open.ApplyEdits(OrgText.WithEol(r.Edits, (open as IEditorDocument)?.EndOfLine ?? OrgText.DetectEol(open.GetText())));
                if (!wasDirty && open is IEditorDocument editor && Config?.Get(OrgSettings.SaveAfterEdit, true) != false) _ = editor.SaveAsync();
                return r;
            }).ConfigureAwait(false);
            Store?.ScheduleRefresh(50);
            return r;
        }
        if (Store is null) return null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var doc = await Store.ReadAsync(uri).ConfigureAwait(false);
            if (doc is null) return null;
            var r = edit(doc);
            if (r is null || r.Edits.Count == 0) return r;
            try
            {
                if (await Store.WriteEditsAsync(uri, doc, r.Edits).ConfigureAwait(false)) return r;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await ShowErrorAsync($"Could not write {Path.GetFileName(uri.LocalPath)}: {ex.Message}").ConfigureAwait(false);
                return null;
            }
        }
        return null;
    }

    /// <summary>The heading of <paramref name="doc"/> that <paramref name="stale"/> (from an older parse) is: same line and title, else the nearest with its title and level.</summary>
    internal static OrgHeading? Resolve(OrgDocument doc, OrgHeading stale)
    {
        if (doc.HeadingAt(stale.Line) is { } h && h.Line == stale.Line && h.Title == stale.Title && h.Level == stale.Level) return h;
        return doc.Headings.Where(x => x.Title == stale.Title && x.Level == stale.Level).MinBy(x => Math.Abs(x.Line - stale.Line));
    }

    /// <summary>Opens the entry in its file, the caret at its heading (or at <paramref name="line"/>).</summary>
    public async Task OpenEntryAsync(OrgDocument doc, OrgHeading heading, int? line = null, bool preserveFocus = false)
    {
        if (doc.Uri is not { } uri || Navigation is null) return;
        var current = await (Store?.ReadAsync(uri) ?? Task.FromResult<OrgDocument?>(null)).ConfigureAwait(false);
        var h = current is null ? heading : Resolve(current, heading) ?? heading;
        var at = line is { } l && h.Line == heading.Line ? new TextPosition(l, 0) : new TextPosition(h.Line, h.TitleColumn);
        await Navigation.OpenAsync(uri, new TextRange(at, at), preserveFocus: preserveFocus).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the entry with the caret at the end of its text (after its planning line, drawer and notes), on a fresh line, ready to write notes.
    /// </summary>
    public async Task EditNotesAsync(OrgDocument snapshot, OrgHeading heading)
    {
        if (snapshot.Uri is not { } uri || Navigation is null) return;
        var editor = await Navigation.OpenAsync(uri).ConfigureAwait(false);
        if (editor is null) return;
        // the editor's text (unsaved changes included) decides where the entry's text ends; a blank line to write on goes right after it
        var caret = await _ui.InvokeAsync<TextPosition?>(() =>
        {
            var doc = OrgDocument.Parse(editor.GetText(), _keywords, uri: uri);
            if (Resolve(doc, heading) is not { } h) return null;
            var last = h.End - 1;
            while (last >= h.ContentStart && string.IsNullOrWhiteSpace(doc.Lines[last])) last--;
            var lastText = Math.Max(last, h.ContentStart - 1);
            if (editor.IsReadOnly) return new TextPosition(lastText, doc.Lines[lastText].Length);
            editor.ApplyEdits([TextEdit.Insert(new TextPosition(lastText, doc.Lines[lastText].Length), editor.EndOfLine)]);
            return new TextPosition(lastText + 1, 0);
        }).ConfigureAwait(false);
        if (caret is not { } at) { Status("That entry is no longer in the file"); return; }
        await Navigation.OpenAsync(uri, new TextRange(at, at)).ConfigureAwait(false);
    }

    /// <summary>A prompt's answer (null for the prompt itself means it was cancelled; a null value is an answer: none).</summary>
    internal readonly record struct Choice<T>(T Value);

    private async Task ShowErrorAsync(string message)
    {
        if (Window is not null) await Window.ShowMessageAsync(MessageSeverity.Error, message).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------ capture, notes file, agenda files

    /// <summary>Opens the notes file (<c>org.defaultNotesFile</c>), creating it first when it does not exist.</summary>
    public async Task OpenNotesAsync()
    {
        if (Store is null || Navigation is null) return;
        var file = new Uri(OrgSettings.NotesFile(Config));
        try
        {
            await Store.EnsureFileAsync(file, "#+TITLE: Notes\n\n").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowErrorAsync($"Could not create {file.LocalPath}: {ex.Message}").ConfigureAwait(false);
            return;
        }
        await Navigation.OpenAsync(file).ConfigureAwait(false);
    }

    /// <summary>Org's C-c [ / C-c ]: adds the active file to <c>org.agendaFiles</c> (or removes it), keeping the folder the agenda read before.</summary>
    private async Task SetInAgendaAsync(bool add)
    {
        if (Config is null || Navigation?.ActiveDocument?.Uri is not { IsFile: true } uri) { Status("Open an org file first"); return; }
        var path = Path.GetFullPath(uri.LocalPath);
        var entries = OrgSettings.AgendaEntries(Config).ToList();
        var inAgenda = Store?.IsTracked(uri) == true;
        if (add)
        {
            if (inAgenda) { Status($"{Path.GetFileName(path)} is already in the agenda"); return; }
            entries.Add(path);
        }
        else
        {
            if (!entries.Remove(path))
            {
                Status(inAgenda ? $"{Path.GetFileName(path)} is in the agenda through its folder; remove the folder from org.agendaFiles instead" : $"{Path.GetFileName(path)} is not in the agenda");
                return;
            }
        }
        await Config.UpdateAsync(OrgSettings.AgendaFiles, entries.Select(Home).ToArray()).ConfigureAwait(false);
        Status(add ? $"Added {Path.GetFileName(path)} to the agenda" : $"Removed {Path.GetFileName(path)} from the agenda");

        static string Home(string p)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return p.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~" + p[home.Length..] : p;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _statusItem?.Dispose();
        DisposeClock();
        _store.Dispose();
        _agendaChanged.Clear();
    }
}
