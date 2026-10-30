namespace CsCode.Sessions;

/// <summary>
/// A named, persisted workspace state (<c>{UserDataDir}/sessions/&lt;name&gt;.json</c>). Immutable; the shell builds a new one per autosave.
/// Breakpoints, watches, search/palette history and Vim registers are NOT copied here: their modules already persist them per workspace and
/// application, so restoring the session's folders brings them back (docs/SESSIONS.md, "What a session holds").
/// </summary>
public sealed record SessionDocument
{
    /// <summary>Schema version written by this build. Older files are migrated on load (<see cref="SessionSerializer"/>); newer ones are refused.</summary>
    public const int CurrentVersion = 2;

    public int Version { get; init; } = CurrentVersion;
    public required string Name { get; init; }

    /// <summary>Created by <c>--new-window</c> (or a folder with no session of its own): named <c>window-N</c> until the user saves it under a name.</summary>
    public bool IsUnnamed { get; init; }

    public DateTimeOffset LastUsedUtc { get; init; }

    /// <summary>Workspace folder URIs (local <c>file:</c> or remote <c>ssh:</c>), primary first.</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    /// <summary>Open editors in tab order per group.</summary>
    public IReadOnlyList<SessionEditor> Editors { get; init; } = [];

    public string? ActiveEditor { get; init; }

    /// <summary>The shell's dock layout snapshot (tool windows, splitters, document groups) as the shell serialises it; opaque to this module.</summary>
    public string? Layout { get; init; }

    public SessionWindow? Window { get; init; }

    /// <summary>True while a process kept this session resident (set on start, cleared by Quit Session); <c>--background</c> with
    /// <c>cscode.background.restoreOnLogin</c> restarts these hidden after a reboot.</summary>
    public bool WasResident { get; init; }
}

/// <summary>One editor tab of a session.</summary>
public sealed record SessionEditor
{
    public required string Uri { get; init; }
    public int Group { get; init; }
    public int Order { get; init; }
    public bool Pinned { get; init; }
    public bool Preview { get; init; }

    /// <summary>Language of an untitled buffer (files re-detect theirs).</summary>
    public string? LanguageId { get; init; }

    public SessionViewState? ViewState { get; init; }

    /// <summary>Id of the hot-exit backup holding this editor's unsaved text (<see cref="HotExitBackupStore"/>), or null when it was saved.</summary>
    public string? BackupId { get; init; }
}

/// <summary>Caret, selection, scroll and folds of one editor; 0-based lines and UTF-16 columns (they survive edits made while no view existed).</summary>
public sealed record SessionViewState(
    int CaretLine,
    int CaretColumn,
    int SelectionStartLine,
    int SelectionStartColumn,
    int SelectionEndLine,
    int SelectionEndColumn,
    double VerticalOffset,
    double HorizontalOffset,
    IReadOnlyList<int> FoldedStartLines);

/// <summary>Window bounds in screen pixels and the maximised/full-screen state (<c>normal</c>, <c>maximized</c>, <c>fullscreen</c>).</summary>
public sealed record SessionWindow(int X, int Y, double Width, double Height, string State);

/// <summary>What a session list shows without loading every session completely.</summary>
public sealed record SessionSummary(string Name, bool IsUnnamed, DateTimeOffset LastUsedUtc, IReadOnlyList<string> Folders, int EditorCount, bool WasResident = false);
