using System.Text;

namespace CsCode.Panels.Git.Engine;

/// <summary>The branch part of <c>git status --branch</c>: the checked-out branch (null when detached), its upstream and how far apart they are.</summary>
public sealed record GitBranchInfo(string? Oid, string? Head, string? Upstream, int Ahead, int Behind)
{
    public static readonly GitBranchInfo None = new(null, null, null, 0, 0);

    public bool IsDetached => Head is null && Oid is not null;

    /// <summary>A repository with no commit yet (<c>branch.oid (initial)</c>).</summary>
    public bool IsInitial => Oid is null;

    /// <summary>What the status bar shows: the branch name, else the short commit of a detached HEAD.</summary>
    public string DisplayName => Head ?? (Oid is { Length: >= 8 } oid ? oid[..8] + "…" : "(no branch)");
}

/// <summary>
/// One path of <c>git status --porcelain=v2</c>. <see cref="Index"/> and <see cref="WorkTree"/> are git's X and Y codes (<c>.</c> = unchanged,
/// <c>M</c> modified, <c>A</c> added, <c>D</c> deleted, <c>R</c> renamed, <c>C</c> copied, <c>T</c> type changed; for an unmerged path the two sides
/// of the conflict: <c>U</c>, <c>A</c>, <c>D</c>). <see cref="Path"/> is relative to the repository root, with forward slashes.
/// </summary>
public sealed record GitStatusEntry(string Path, char Index, char WorkTree, GitEntryKind Kind, string? OriginalPath = null, string? IndexMode = null)
{
    public bool IsConflict => Kind == GitEntryKind.Unmerged;
    public bool IsUntracked => Kind == GitEntryKind.Untracked;
    public bool IsIgnored => Kind == GitEntryKind.Ignored;

    /// <summary>Has a change in the index (listed under Staged Changes).</summary>
    public bool IsStaged => Kind is GitEntryKind.Changed or GitEntryKind.Renamed && Index != '.';

    /// <summary>Has a change in the working tree (listed under Changes): modified or deleted on disk, untracked, or in conflict.</summary>
    public bool IsUnstaged => Kind switch
    {
        GitEntryKind.Changed or GitEntryKind.Renamed => WorkTree != '.',
        GitEntryKind.Untracked or GitEntryKind.Unmerged => true,
        _ => false,
    };

    /// <summary>The letter shown for the working-tree side (Changes list, Explorer): M, D, U (untracked), T, ! (conflict).</summary>
    public char WorkTreeLetter => Kind switch
    {
        GitEntryKind.Untracked => 'U',
        GitEntryKind.Unmerged => '!',
        _ => WorkTree,
    };

    /// <summary>The letter shown for the index side (Staged Changes list): A, M, D, R, C, T.</summary>
    public char IndexLetter => Index;
}

public enum GitEntryKind { Changed, Renamed, Unmerged, Untracked, Ignored }

/// <summary>An immutable status of one repository.</summary>
public sealed record GitStatusSnapshot(GitBranchInfo Branch, IReadOnlyList<GitStatusEntry> Entries)
{
    public static readonly GitStatusSnapshot Empty = new(GitBranchInfo.None, []);

    public IEnumerable<GitStatusEntry> Staged => Entries.Where(e => e.IsStaged);
    public IEnumerable<GitStatusEntry> Unstaged => Entries.Where(e => e.IsUnstaged);
    public IEnumerable<GitStatusEntry> Conflicts => Entries.Where(e => e.IsConflict);
}

/// <summary>
/// Parser of <c>git status --porcelain=v2 -z --branch [--untracked-files=all] [--ignored=matching]</c>. The format is stable across git versions
/// and locales; records end with NUL and paths are never quoted. Unknown record types and malformed records are skipped, never thrown on.
/// </summary>
public static class PorcelainV2
{
    public static GitStatusSnapshot Parse(ReadOnlySpan<byte> output) => Parse(Encoding.UTF8.GetString(output));

    public static GitStatusSnapshot Parse(string output)
    {
        string? oid = null, head = null, upstream = null;
        int ahead = 0, behind = 0;
        var entries = new List<GitStatusEntry>();
        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var r = records[i];
            if (r.Length < 2) continue;
            switch (r[0])
            {
                case '#':
                    ParseHeader(r, ref oid, ref head, ref upstream, ref ahead, ref behind);
                    break;
                case '1':
                {
                    // 1 XY sub mH mI mW hH hI path
                    var f = r.Split(' ', 9);
                    if (f.Length < 9 || f[1].Length != 2) break;
                    entries.Add(new GitStatusEntry(f[8], f[1][0], f[1][1], GitEntryKind.Changed, IndexMode: f[4]));
                    break;
                }
                case '2':
                {
                    // 2 XY sub mH mI mW hH hI Xscore path NUL origPath
                    var f = r.Split(' ', 10);
                    if (f.Length < 10 || f[1].Length != 2) break;
                    var orig = i + 1 < records.Length ? records[++i] : null;
                    entries.Add(new GitStatusEntry(f[9], f[1][0], f[1][1], GitEntryKind.Renamed, orig, f[4]));
                    break;
                }
                case 'u':
                {
                    // u XY sub m1 m2 m3 mW h1 h2 h3 path
                    var f = r.Split(' ', 11);
                    if (f.Length < 11 || f[1].Length != 2) break;
                    entries.Add(new GitStatusEntry(f[10], f[1][0], f[1][1], GitEntryKind.Unmerged, IndexMode: f[6]));
                    break;
                }
                case '?':
                    if (r.Length > 2 && r[1] == ' ') entries.Add(new GitStatusEntry(r[2..], '?', '?', GitEntryKind.Untracked));
                    break;
                case '!':
                    if (r.Length > 2 && r[1] == ' ') entries.Add(new GitStatusEntry(r[2..], '!', '!', GitEntryKind.Ignored));
                    break;
            }
        }
        return new GitStatusSnapshot(new GitBranchInfo(oid, head, upstream, ahead, behind), entries);
    }

    private static void ParseHeader(string r, ref string? oid, ref string? head, ref string? upstream, ref int ahead, ref int behind)
    {
        // # branch.oid <commit> | (initial);  # branch.head <branch> | (detached);  # branch.upstream <upstream>;  # branch.ab +<ahead> -<behind>
        var parts = r.Split(' ', 3);
        if (parts.Length < 3) return;
        switch (parts[1])
        {
            case "branch.oid": oid = parts[2] == "(initial)" ? null : parts[2]; break;
            case "branch.head": head = parts[2] == "(detached)" ? null : parts[2]; break;
            case "branch.upstream": upstream = parts[2]; break;
            case "branch.ab":
                var ab = parts[2].Split(' ');
                if (ab.Length == 2)
                {
                    _ = int.TryParse(ab[0].TrimStart('+'), out ahead);
                    _ = int.TryParse(ab[1].TrimStart('-'), out behind);
                }
                break;
        }
    }
}
