using System.Globalization;
using CsCode.App.Background;
using CsCode.Core.Platform;
using CsCode.Sessions;

namespace CsCode.App.QuickInput;

/// <summary>A saved session as the Welcome page and Quick Launch show it.</summary>
public sealed record SavedSession(string Name, bool IsUnnamed, DateTimeOffset LastUsedUtc, string? Folder, int EditorCount)
{
    /// <summary>"folder · last used 2026-10-29 21:40" (either part may be missing).</summary>
    public string Detail => string.Join(" · ", new[]
    {
        Folder,
        LastUsedUtc == DateTimeOffset.MinValue ? null : "last used " + LastUsedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
    }.OfType<string>());
}

/// <summary>
/// The saved sessions (docs/SESSIONS.md: <c>{UserDataDir}/sessions/*.json</c>) for the shell's pickers: the Welcome page's Sessions list and Quick
/// Launch's <c>session:</c> mode. Listing reads files, so it runs on a pool thread; opening one runs File ▸ Sessions ▸ Open Session with its name
/// (<see cref="SessionCommandIds.OpenSession"/>), which only session processes register: in a window run with <c>--foreground</c> there is no
/// background mode to open a session in, and the shell offers none (<see cref="CanOpen"/>). In a session process the store is read on its
/// session lane (<paramref name="runOnLane"/>), where the process writes it, never beside a write.
/// </summary>
public sealed class SessionCatalog(ICommandService commands, string? userDataDir, Func<string?>? currentSession = null,
    Func<Func<IReadOnlyList<SavedSession>>, Task<IReadOnlyList<SavedSession>>>? runOnLane = null)
{
    /// <summary>At most this many sessions are listed (most recently used first).</summary>
    public const int MaxListed = 50;

    /// <summary>Whether sessions can be opened from this window (it runs in a session process).</summary>
    public bool CanOpen => userDataDir is not null && commands.Has(SessionCommandIds.OpenSession);

    /// <summary>The session this window shows (left out of the lists), or null.</summary>
    public string? Current => currentSession?.Invoke();

    /// <summary>Saved sessions other than this window's, most recently used first. Off the UI thread; never throws for unreadable files.</summary>
    public Task<IReadOnlyList<SavedSession>> ListAsync(CancellationToken ct = default)
    {
        if (userDataDir is null) return Task.FromResult<IReadOnlyList<SavedSession>>([]);
        var current = Current;
        IReadOnlyList<SavedSession> List()
        {
            try
            {
                return new SessionStore(userDataDir).List()
                    .Where(s => s.Name != current)
                    .Take(MaxListed)
                    .Select(s => new SavedSession(s.Name, s.IsUnnamed, s.LastUsedUtc, FolderOf(s.Folders), s.EditorCount))
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }
        return runOnLane is { } lane ? lane(List) : Task.Run(List, ct);
    }

    /// <summary>Opens (or shows) a saved session through File ▸ Sessions ▸ Open Session.</summary>
    public async Task OpenAsync(string name)
    {
        if (!CanOpen || !SessionNames.IsValid(name)) return;
        await commands.ExecuteAsync(SessionCommandIds.OpenSession, name).ConfigureAwait(true);
    }

    /// <summary>File ▸ Sessions ▸ Open Session's own picker (every saved session, with their running state).</summary>
    public async Task PickAsync()
    {
        if (CanOpen) await commands.ExecuteAsync(SessionCommandIds.OpenSession).ConfigureAwait(true);
    }

    private static string? FolderOf(IReadOnlyList<string> folders) =>
        folders.FirstOrDefault() is { } f && Uri.TryCreate(f, UriKind.Absolute, out var u) ? (u.IsFile ? u.LocalPath : u.ToString()) : null;
}
