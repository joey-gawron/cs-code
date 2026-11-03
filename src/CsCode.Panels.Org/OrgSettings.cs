using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Panels.Org.Engine;

namespace CsCode.Panels.Org;

/// <summary>The org-mode settings (org's variable names in VS Code's style), registered with their defaults and descriptions.</summary>
public static class OrgSettings
{
    public const string Directory = "org.directory";
    public const string AgendaFiles = "org.agendaFiles";
    public const string DefaultNotesFile = "org.defaultNotesFile";
    public const string TodoKeywords = "org.todoKeywords";
    public const string DeadlineWarningDays = "org.deadlineWarningDays";
    public const string LogDone = "org.logDone";
    public const string AgendaSpan = "org.agenda.span";
    public const string StartOnWeekday = "org.agenda.startOnWeekday";
    public const string ShowDone = "org.agenda.showDone";
    public const string SaveAfterEdit = "org.agenda.saveAfterEdit";
    public const string StatusBar = "org.statusBar.enabled";

    public const string DefaultDirectory = "~/org";

    private static ConfigurationProperty P(string key, object? value, string description, string schemaJson, ConfigurationScope? scope = null) =>
        new(key, JsonSerializer.SerializeToElement(value), description, JsonDocument.Parse(schemaJson).RootElement.Clone()) { Scope = scope ?? ConfigurationScope.Window };

    public static IReadOnlyList<ConfigurationProperty> Properties() =>
    [
        P(Directory, DefaultDirectory, "The folder of your org files (org-directory): where captured tasks and notes go, and the agenda's files when org.agendaFiles is empty. ~ is your home folder.", """{"type":"string"}""", ConfigurationScope.Application),
        P(AgendaFiles, Array.Empty<string>(), "The files and folders the agenda reads (org-agenda-files). A folder contributes the .org files directly in it. Empty: the .org files in org.directory. ~ is your home folder; Org: Add File to Agenda adds the active file.", """{"type":"array","items":{"type":"string"}}""", ConfigurationScope.Application),
        P(DefaultNotesFile, "notes.org", "The file Org: Capture adds tasks and notes to (org-default-notes-file); a relative path is relative to org.directory. Created when it does not exist.", """{"type":"string"}""", ConfigurationScope.Application),
        P(TodoKeywords, new[] { "TODO", "NEXT", "WAITING", "|", "DONE", "CANCELLED" }, "The TODO states (org-todo-keywords): the states still to do, then | and the done states. A file's own #+TODO: line wins.", """{"type":"array","items":{"type":"string"}}"""),
        P(DeadlineWarningDays, 14, "How many days before a deadline the agenda starts showing it on today (org-deadline-warning-days). A deadline's own warning (-3d) wins.", """{"type":"number","minimum":0}"""),
        P(LogDone, "time", "What marking an entry done records (org-log-done): \"time\" adds CLOSED: [date time] under the heading; \"none\" records nothing.", """{"type":"string","enum":["time","none"]}"""),
        P(AgendaSpan, "week", "How many days the agenda shows (org-agenda-span): \"day\" or \"week\".", """{"type":"string","enum":["day","week"]}"""),
        P(StartOnWeekday, 1, "The weekday the week agenda starts on (org-agenda-start-on-weekday): 0 Sunday, 1 Monday, ... 6 Saturday; -1 starts on today.", """{"type":"number","minimum":-1,"maximum":6}"""),
        P(ShowDone, true, "Whether the agenda lists entries already done on their day.", """{"type":"boolean"}"""),
        P(SaveAfterEdit, true, "Whether a change made from the agenda (done, schedule, deadline, priority, tags) saves a file open in the editor that had no unsaved changes before it. Files that are not open are always written.", """{"type":"boolean"}"""),
        P(StatusBar, true, "Show the number of entries due today and overdue in the status bar (click: the agenda).", """{"type":"boolean"}"""),
    ];

    /// <summary>The TODO states the settings name (<see cref="OrgTodoKeywords.Default"/> when they name none).</summary>
    public static OrgTodoKeywords Keywords(IConfigurationService? config)
    {
        if (config?.GetRaw(TodoKeywords) is { ValueKind: JsonValueKind.Array } list)
        {
            var words = list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
            var parsed = OrgTodoKeywords.Parse(words);
            if (parsed.All.Count > 0) return parsed;
        }
        return OrgTodoKeywords.Default;
    }

    /// <summary><c>~</c> and <c>~/...</c> as the home folder.</summary>
    public static string ExpandHome(string path)
    {
        path = path.Trim();
        if (path != "~" && !path.StartsWith("~/", StringComparison.Ordinal) && !path.StartsWith("~\\", StringComparison.Ordinal)) return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length <= 2 ? home : Path.Combine(home, path[2..]);
    }

    public static string OrgDirectory(IConfigurationService? config) =>
        Path.GetFullPath(ExpandHome(config?.Get<string>(Directory) is { Length: > 0 } dir ? dir : DefaultDirectory));

    public static string NotesFile(IConfigurationService? config)
    {
        var file = ExpandHome(config?.Get<string>(DefaultNotesFile) is { Length: > 0 } f ? f : "notes.org");
        return Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(OrgDirectory(config), file));
    }

    /// <summary>The agenda's entries (files and folders, full paths): <see cref="AgendaFiles"/>, else <see cref="Directory"/>.</summary>
    public static IReadOnlyList<string> AgendaEntries(IConfigurationService? config)
    {
        if (config?.GetRaw(AgendaFiles) is { ValueKind: JsonValueKind.Array } list)
        {
            var entries = list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                .Select(e => Path.GetFullPath(ExpandHome(e.GetString()!))).Distinct(StringComparer.Ordinal).ToList();
            if (entries.Count > 0) return entries;
        }
        return [OrgDirectory(config)];
    }
}
