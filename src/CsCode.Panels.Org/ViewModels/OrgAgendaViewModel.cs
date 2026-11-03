using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CsCode.Core.Lifecycle;
using CsCode.Panels.Org.Engine;
using CsCode.Ui.Collections;

namespace CsCode.Panels.Org.ViewModels;

/// <summary>What the Org Agenda window lists: one day, a week, or every entry still to do.</summary>
public enum OrgAgendaMode { Day, Week, Todo }

/// <summary>A row of the agenda: a day (or, in the TODO list, a file) header, an entry, or a note that a day has nothing.</summary>
public abstract class OrgRow
{
    public abstract string Key { get; }
    public override bool Equals(object? obj) => obj is OrgRow r && r.GetType() == GetType() && r.Key == Key;
    public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);
}

/// <summary>A day of the day or week agenda: <c>Tuesday 3 November</c>, marked when it is today.</summary>
public sealed class OrgHeaderRow(string title, string? detail, bool isToday, DateOnly? date) : OrgRow
{
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public bool IsToday { get; } = isToday;
    /// <summary>The day (null for a file header of the TODO list).</summary>
    public DateOnly? Date { get; } = date;
    public override string Key => "h:" + Title;
}

/// <summary>"Nothing scheduled" under a day without entries.</summary>
public sealed class OrgEmptyRow(DateOnly date, string text) : OrgRow
{
    public DateOnly Date { get; } = date;
    public string Text { get; } = text;
    public override string Key => "e:" + Date.DayNumber;
}

/// <summary>An entry of the agenda.</summary>
public sealed class OrgItemRow(OrgAgendaItem item, bool showDate) : OrgRow
{
    public OrgAgendaItem Item { get; } = item;
    public OrgHeading Heading => Item.Heading;
    public OrgDocument Document => Item.Document;
    public string Title => Heading.Title.Length > 0 ? Heading.Title : "(untitled)";
    public string? Keyword => Heading.Keyword;
    public bool HasKeyword => Heading.Keyword is not null;
    public bool IsDone => Heading.IsDone;
    public string Priority => Heading.Priority is { } p ? $"[#{p}]" : "";
    public bool HasPriority => Heading.Priority is not null;

    /// <summary>The second line: time or why it is on this day, the file's category, the tags.</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            var why = Item.Kind switch
            {
                OrgAgendaKind.Scheduled => "Scheduled",
                OrgAgendaKind.ScheduledOverdue => $"Scheduled {Item.Days} d. ago",
                OrgAgendaKind.Deadline => "Due today",
                OrgAgendaKind.DeadlineUpcoming => $"Due in {Item.Days} d. ({Item.Timestamp!.Date.ToString("ddd d MMM", CultureInfo.InvariantCulture)})",
                OrgAgendaKind.DeadlineOverdue => $"Overdue {Item.Days} d.",
                OrgAgendaKind.Timestamp => Item.Extra.TrimEnd(':'),
                _ => Due(),
            };
            if (Item.TimeText.Length > 0) parts.Add(Item.TimeText);
            if (why.Length > 0) parts.Add(why);
            if (showDate && Item.Kind != OrgAgendaKind.Todo) parts.Add(Item.Day.ToString("ddd d MMM", CultureInfo.InvariantCulture));
            parts.Add(Heading.Category);
            if (Heading.Tags.Count > 0) parts.Add(":" + string.Join(':', Heading.Tags) + ":");
            return string.Join(" · ", parts);
        }
    }

    // the TODO list: its deadline or scheduled day, when it has one
    private string Due()
    {
        if (Heading.Deadline is { } d) return "Due " + d.Date.ToString("ddd d MMM", CultureInfo.InvariantCulture);
        if (Heading.Scheduled is { } s) return "Scheduled " + s.Date.ToString("ddd d MMM", CultureInfo.InvariantCulture);
        return "";
    }

    /// <summary>The theme colour of the second line: overdue and due today in the error colour, coming deadlines in the warning colour.</summary>
    public string DetailColorKey => IsDone ? "descriptionForeground" : Item.Kind switch
    {
        OrgAgendaKind.DeadlineOverdue or OrgAgendaKind.Deadline or OrgAgendaKind.ScheduledOverdue => "list.errorForeground",
        OrgAgendaKind.DeadlineUpcoming => "list.warningForeground",
        _ => "descriptionForeground",
    };

    public string KeywordColorKey => IsDone ? "gitDecoration.addedResourceForeground" : "list.errorForeground";

    public string Tooltip => string.Join(" › ", [Document.FileName, .. Heading.Ancestors(), Title]);

    public override string Key => $"i:{Document.Uri}:{Heading.Line}:{Heading.Title}:{Item.Kind}:{Item.Day.DayNumber}:{Item.Line}";
}

/// <summary>
/// The Org Agenda window's state (UI thread): the view (day, week, TODO list) and the day it starts on, the rows (rebuilt from the store's
/// documents in one bulk update), the filter, the selected entry and its notes. The commands on entries go through <see cref="OrgHost"/>.
/// Disposed with the view.
/// </summary>
public sealed class OrgAgendaViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly OrgHost _host;
    private readonly DisposableStore _store = new();
    private OrgAgendaMode _mode;
    private DateOnly _start;
    private DateOnly _today;
    private string _filter = "";
    private OrgRow? _selected;
    private bool _disposed;

    public OrgAgendaViewModel(OrgHost host)
    {
        _host = host;
        _today = host.Today;
        _mode = host.Config?.Get<string>(OrgSettings.AgendaSpan) == "day" ? OrgAgendaMode.Day : OrgAgendaMode.Week;
        _start = SpanStart(_today);
        _store.Add(host.AgendaChanged.Subscribe(_ => Rebuild()));
        if (host.Config is not null)
            _store.Add(host.Config.Changed.Subscribe(e => { if (e.Affects("org.agenda") || e.Affects(OrgSettings.DeadlineWarningDays)) host.Ui.Post(() => { if (!_disposed) { _start = SpanStart(_start); Rebuild(); } }); }));
        Rebuild();
    }

    public BulkObservableCollection<OrgRow> Rows { get; } = [];

    public OrgAgendaMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            _start = SpanStart(value == OrgAgendaMode.Day && _today >= _start && _today < _start.AddDays(7) ? _today : _start);
            OnChanged();
            OnChanged(nameof(IsDay));
            OnChanged(nameof(IsWeek));
            OnChanged(nameof(IsTodo));
            Rebuild();
        }
    }

    public bool IsDay => _mode == OrgAgendaMode.Day;
    public bool IsWeek => _mode == OrgAgendaMode.Week;
    public bool IsTodo => _mode == OrgAgendaMode.Todo;

    /// <summary>The first day shown (day and week views).</summary>
    public DateOnly Start => _start;

    public DateOnly Today => _today;

    public string Filter
    {
        get => _filter;
        set
        {
            if (_filter == value) return;
            _filter = value;
            OnChanged();
            Rebuild();
        }
    }

    public OrgRow? Selected
    {
        get => _selected;
        set
        {
            if (Equals(_selected, value) && ReferenceEquals(_selected, value)) return;
            _selected = value;
            OnChanged();
            OnChanged(nameof(SelectedItem));
            OnChanged(nameof(HasDetails));
            OnChanged(nameof(DetailsHeader));
            OnChanged(nameof(DetailsPlanning));
            OnChanged(nameof(Details));
        }
    }

    public OrgItemRow? SelectedItem => _selected as OrgItemRow;

    /// <summary>The window's title line: <c>Tuesday 3 November 2026</c>, <c>Week 45 · 2 – 8 November 2026</c>, <c>To do · 12 entries</c>.</summary>
    public string Title { get; private set; } = "";

    public string EmptyText { get; private set; } = "";
    public bool IsEmpty { get; private set; }

    /// <summary>True when the agenda has no file at all (the empty state offers to capture or create the notes file).</summary>
    public bool NoFiles { get; private set; }

    public bool HasDetails => SelectedItem is not null;

    public string DetailsHeader => SelectedItem is { } row
        ? string.Join(" ", new[] { row.Keyword, row.HasPriority ? row.Priority : null, row.Title }.OfType<string>())
        : "";

    public string DetailsPlanning
    {
        get
        {
            if (SelectedItem is not { } row) return "";
            var parts = new List<string> { string.Join(" › ", [row.Document.FileName, .. row.Heading.Ancestors()]) };
            if (row.Heading.Scheduled is { } s) parts.Add("Scheduled " + OrgDateInput.Describe(s));
            if (row.Heading.Deadline is { } d) parts.Add("Deadline " + OrgDateInput.Describe(d));
            if (row.Heading.Closed is { } c) parts.Add("Closed " + OrgDateInput.Describe(c));
            if (row.Heading.AllTags.Count > 0) parts.Add("Tags :" + string.Join(':', row.Heading.AllTags) + ":");
            return string.Join("\n", parts);
        }
    }

    /// <summary>The selected entry's own text (its notes), or a hint when it has none.</summary>
    public string Details => SelectedItem is { } row
        ? row.Document.BodyText(row.Heading) is { Length: > 0 } body ? body : "No notes yet. Write Notes opens the entry in the editor, ready to write."
        : "";

    // ------------------------------------------------------------------------------------------------ navigation

    private DateOnly SpanStart(DateOnly day)
    {
        if (_mode != OrgAgendaMode.Week) return day;
        var weekday = _host.Config?.Get(OrgSettings.StartOnWeekday, 1) ?? 1;
        if (weekday is < 0 or > 6) return day;
        var back = ((int)day.DayOfWeek - weekday + 7) % 7;
        return day.AddDays(-back);
    }

    private int SpanDays => _mode == OrgAgendaMode.Week ? 7 : 1;

    public void Next() => Move(+1);
    public void Previous() => Move(-1);

    private void Move(int direction)
    {
        if (_mode == OrgAgendaMode.Todo) return;
        _start = _start.AddDays(direction * SpanDays);
        Rebuild();
    }

    public void GoToday()
    {
        _today = _host.Today;
        if (_mode == OrgAgendaMode.Todo) Mode = OrgAgendaMode.Week;
        _start = SpanStart(_today);
        Rebuild();
    }

    public void GoTo(DateOnly day)
    {
        if (_mode == OrgAgendaMode.Todo) _mode = OrgAgendaMode.Day;
        _start = SpanStart(day);
        OnChanged(nameof(Mode));
        OnChanged(nameof(IsDay));
        OnChanged(nameof(IsWeek));
        OnChanged(nameof(IsTodo));
        Rebuild();
    }

    /// <summary>Called periodically: when the date has changed (past midnight), a view that showed the old today moves to the new one.</summary>
    public void CheckDate()
    {
        var today = _host.Today;
        if (today == _today) return;
        var followed = _today >= _start && _today < _start.AddDays(SpanDays);
        _today = today;
        if (followed) _start = SpanStart(today);
        Rebuild();
    }

    // ------------------------------------------------------------------------------------------------ rows

    public void Rebuild()
    {
        if (_disposed) return;
        var docs = _host.Store?.Documents ?? [];
        var keep = _selected;
        var rows = new List<OrgRow>();
        var filter = ParseFilter(_filter);
        var total = 0;
        if (_mode == OrgAgendaMode.Todo)
        {
            var items = OrgAgenda.TodoList(docs, _today).Where(i => Matches(i, filter)).ToList();
            total = items.Count;
            foreach (var group in items.GroupBy(i => i.Document))
            {
                rows.Add(new OrgHeaderRow(group.Key.Title is { Length: > 0 } t ? t : group.Key.FileName, $"{group.Count()}", false, null));
                rows.AddRange(group.Select(i => new OrgItemRow(i, showDate: false)));
            }
            Title = total == 1 ? "To do · 1 entry" : $"To do · {total} entries";
        }
        else
        {
            var end = _start.AddDays(SpanDays - 1);
            var days = OrgAgenda.Build(docs, _start, end, _today,
                new OrgAgendaOptions(_host.DeadlineWarningDays, _host.Config?.Get(OrgSettings.ShowDone, true) ?? true));
            foreach (var day in days)
            {
                var items = day.Items.Where(i => Matches(i, filter)).ToList();
                total += items.Count;
                var isToday = day.Date == _today;
                var name = day.Date.ToString("dddd d MMMM", CultureInfo.InvariantCulture);
                rows.Add(new OrgHeaderRow(isToday ? "Today · " + name : name, items.Count == 0 ? null : items.Count.ToString(CultureInfo.InvariantCulture), isToday, day.Date));
                if (items.Count == 0) rows.Add(new OrgEmptyRow(day.Date, filter.Count > 0 ? "Nothing matches the filter" : "Nothing scheduled"));
                rows.AddRange(items.Select(i => new OrgItemRow(i, showDate: false)));
            }
            Title = _mode == OrgAgendaMode.Day
                ? _start.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)
                : $"Week {ISOWeek.GetWeekOfYear(_start.ToDateTime(TimeOnly.MinValue))} · {RangeText(_start, end)}";
        }

        NoFiles = _host.Store is { Loaded: true } s && s.Files.Count == 0;
        IsEmpty = NoFiles || (_mode == OrgAgendaMode.Todo && total == 0);
        EmptyText = _host.Store is null
            ? "Org mode needs the file system service."
            : _host.Store is { Loaded: false } ? "Reading org files..."
            : NoFiles ? NoFilesText()
            : filter.Count > 0 ? "Nothing matches the filter." : "Nothing to do. Add a task with + or Capture.";
        Rows.ReplaceAll(rows);
        Selected = keep is null ? null : rows.FirstOrDefault(r => r.Equals(keep));
        OnChanged(nameof(Title));
        OnChanged(nameof(EmptyText));
        OnChanged(nameof(IsEmpty));
        OnChanged(nameof(NoFiles));
        OnChanged(nameof(Start));
        OnChanged(nameof(Details));
        OnChanged(nameof(DetailsPlanning));
    }

    private string NoFilesText()
    {
        var entries = _host.Store?.Missing is { Count: > 0 } missing ? missing : OrgSettings.AgendaEntries(_host.Config);
        var where = string.Join(", ", entries.Select(Pretty));
        return $"No org files in {where} yet.\nCapture a task or a note to start one, or point org.agendaFiles at your own .org files.";
    }

    internal static string Pretty(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }

    private static string RangeText(DateOnly a, DateOnly b)
    {
        var inv = CultureInfo.InvariantCulture;
        if (a.Year != b.Year) return $"{a.ToString("d MMM yyyy", inv)} – {b.ToString("d MMM yyyy", inv)}";
        if (a.Month != b.Month) return $"{a.ToString("d MMM", inv)} – {b.ToString("d MMM yyyy", inv)}";
        return $"{a.Day} – {b.ToString("d MMMM yyyy", inv)}";
    }

    /// <summary>The filter's words: each must match (a word starting with <c>:</c> or <c>+</c> a tag, else text of the title, keyword or category).</summary>
    internal static List<string> ParseFilter(string text) =>
        [.. text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant())];

    internal static bool Matches(OrgAgendaItem item, List<string> words)
    {
        foreach (var w in words)
        {
            var h = item.Heading;
            if (w[0] is ':' or '+')
            {
                var tag = w.Trim(':', '+');
                if (tag.Length > 0 && !h.AllTags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))) return false;
                continue;
            }
            if (!(h.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || (h.Keyword?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
                  || h.Category.Contains(w, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        return true;
    }

    /// <summary>The day new entries captured from the agenda are scheduled on: the selected row's day (day and week views), else none.</summary>
    public DateOnly? SelectedDay => _mode == OrgAgendaMode.Todo ? null : _selected switch
    {
        OrgHeaderRow { Date: { } d } => d,
        OrgEmptyRow e => e.Date,
        OrgItemRow i => i.Item.Day,
        _ => _start <= _today && _today < _start.AddDays(SpanDays) ? _today : _start,
    };

    // ------------------------------------------------------------------------------------------------ commands on entries

    private Task<bool> Edit(OrgItemRow row, Func<OrgDocument, OrgHeading, OrgEditResult?> edit) => _host.EditEntryAsync(row.Document, row.Heading, edit);

    /// <summary>The check mark: done → the first to-do state; otherwise → the first done state (repeating entries move to their next date).</summary>
    public Task<bool> ToggleDoneAsync(OrgItemRow row)
    {
        var keywords = row.Document.Keywords;
        var target = row.IsDone ? keywords.NotDone.FirstOrDefault() : keywords.Done.FirstOrDefault();
        return Edit(row, (doc, h) => OrgEdits.SetKeyword(doc, h, target, _host.Now(), _host.LogDone));
    }

    public Task<bool> CycleTodoAsync(OrgItemRow row) => Edit(row, (doc, h) => OrgEdits.CycleKeyword(doc, h, _host.Now(), _host.LogDone));

    public async Task SetStateAsync(OrgItemRow row)
    {
        if (await _host.PickTodoStateAsync(row.Keyword, row.Document.Keywords).ConfigureAwait(true) is { } pick)
            await Edit(row, (doc, h) => OrgEdits.SetKeyword(doc, h, pick.Value, _host.Now(), _host.LogDone)).ConfigureAwait(true);
    }

    /// <summary>Schedule... / Deadline...: asks for the date (org's syntax), then sets it.</summary>
    public async Task PlanAsync(OrgItemRow row, OrgPlanningKind kind)
    {
        var suggested = kind == OrgPlanningKind.Scheduled && row.Item.Kind != OrgAgendaKind.Todo ? row.Item.Day : (DateOnly?)null;
        if (await _host.PromptDateAsync(kind, row.Heading.PlanningOf(kind), suggested).ConfigureAwait(true) is { } chosen)
            await SetPlanningAsync(row, kind, chosen.Value).ConfigureAwait(true);
    }

    public Task<bool> SetPlanningAsync(OrgItemRow row, OrgPlanningKind kind, OrgTimestamp? value) =>
        Edit(row, (doc, h) => OrgEdits.SetPlanning(doc, h, kind, value));

    /// <summary>The planning the row is about: its deadline for deadline rows, else its scheduled day.</summary>
    public static OrgPlanningKind KindOf(OrgItemRow row) =>
        row.Item.Kind is OrgAgendaKind.Deadline or OrgAgendaKind.DeadlineOverdue or OrgAgendaKind.DeadlineUpcoming ? OrgPlanningKind.Deadline : OrgPlanningKind.Scheduled;

    /// <summary>Org's S-right / S-left in the agenda: the row's date (scheduled day, or deadline) one day later or earlier.</summary>
    public Task<bool> ShiftDateAsync(OrgItemRow row, int days)
    {
        var kind = KindOf(row);
        var current = row.Heading.PlanningOf(kind);
        if (current is null) return Task.FromResult(false);
        return SetPlanningAsync(row, kind, current with { Date = current.Date.AddDays(days), EndDate = current.EndDate?.AddDays(days) });
    }

    /// <summary>Reschedules to a given day, keeping the time and repeater of the timestamp it had.</summary>
    public Task<bool> RescheduleAsync(OrgItemRow row, DateOnly day)
    {
        var kind = KindOf(row);
        var current = row.Heading.PlanningOf(kind);
        var value = current is null ? new OrgTimestamp(true, day) : current with { Date = day, EndDate = null };
        return SetPlanningAsync(row, kind, value);
    }

    public async Task PriorityAsync(OrgItemRow row)
    {
        if (await _host.PickPriorityAsync(row.Heading.Priority).ConfigureAwait(true) is { } pick)
            await Edit(row, (doc, h) => OrgEdits.SetPriority(doc, h, pick.Value)).ConfigureAwait(true);
    }

    public Task<bool> ShiftPriorityAsync(OrgItemRow row, bool up) =>
        Edit(row, (doc, h) => OrgEdits.SetPriority(doc, h, OrgEdits.ShiftPriority(h.Priority, up)));

    public async Task TagsAsync(OrgItemRow row)
    {
        if (await _host.PromptTagsAsync(row.Heading.Tags).ConfigureAwait(true) is { } tags)
            await Edit(row, (doc, h) => OrgEdits.SetTags(doc, h, tags)).ConfigureAwait(true);
    }

    public Task OpenAsync(OrgItemRow row, bool preserveFocus = false) =>
        _host.OpenEntryAsync(row.Document, row.Heading, row.Item.Kind == OrgAgendaKind.Timestamp ? row.Item.Line : null, preserveFocus);

    public Task WriteNotesAsync(OrgItemRow row) => _host.EditNotesAsync(row.Document, row.Heading);

    public Task CaptureAsync(OrgHost.CaptureKind? kind = null) => _host.CaptureAsync(SelectedDay, kind);

    public Task OpenNotesFileAsync() => _host.OpenNotesAsync();

    public async Task RefreshAsync()
    {
        if (_host.Store is { } store)
        {
            store.ResetWatches();
            await store.RefreshAsync().ConfigureAwait(true);
        }
        CheckDate();
        Rebuild();
    }

    /// <summary>Jump to a date (org's <c>j</c>): asks for it in org's syntax and shows its day (or week).</summary>
    public async Task JumpAsync()
    {
        if (_host.Window is null) return;
        var text = await _host.Window.ShowInputBoxAsync(new Core.Platform.InputBoxOptions(Title: "Go to Date",
            Prompt: "today, tomorrow, +2w, fri, nov 20, 2026-11-20", Validate: s => OrgDateInput.Parse(s, _host.Today, out var e) is null ? e : null)).ConfigureAwait(true);
        if (text is not null && OrgDateInput.Parse(text, _host.Today, out _) is { } ts)
        {
            if (_mode == OrgAgendaMode.Todo) _mode = OrgAgendaMode.Day;
            _start = SpanStart(ts.Date);
            OnChanged(nameof(Mode));
            OnChanged(nameof(IsDay));
            OnChanged(nameof(IsWeek));
            OnChanged(nameof(IsTodo));
            Rebuild();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Dispose();
        PropertyChanged = null;
    }
}
