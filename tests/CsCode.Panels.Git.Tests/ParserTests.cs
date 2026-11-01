using CsCode.Core.Text;
using CsCode.Panels.Git.Engine;
using Xunit;

namespace CsCode.Panels.Git.Tests;

public sealed class ParserTests
{
    private const string Z = "\0";

    [Fact]
    public void Porcelain_v2_branch_headers_give_branch_upstream_and_ahead_behind()
    {
        var s = PorcelainV2.Parse("# branch.oid 1234567890abcdef1234567890abcdef12345678" + Z + "# branch.head feature/x" + Z
            + "# branch.upstream origin/feature/x" + Z + "# branch.ab +3 -2" + Z);
        Assert.Equal("feature/x", s.Branch.Head);
        Assert.Equal("origin/feature/x", s.Branch.Upstream);
        Assert.Equal(3, s.Branch.Ahead);
        Assert.Equal(2, s.Branch.Behind);
        Assert.False(s.Branch.IsDetached);
        Assert.Empty(s.Entries);
    }

    [Fact]
    public void Porcelain_v2_initial_and_detached_heads()
    {
        var initial = PorcelainV2.Parse("# branch.oid (initial)" + Z + "# branch.head main" + Z);
        Assert.True(initial.Branch.IsInitial);
        Assert.Equal("main", initial.Branch.DisplayName);
        var detached = PorcelainV2.Parse("# branch.oid abcdef0123456789abcdef0123456789abcdef01" + Z + "# branch.head (detached)" + Z);
        Assert.True(detached.Branch.IsDetached);
        Assert.Equal("abcdef01…", detached.Branch.DisplayName);
    }

    [Fact]
    public void Porcelain_v2_records_ordinary_renamed_unmerged_untracked_and_ignored()
    {
        var output =
            "1 .M N... 100644 100644 100644 aaaaaaa bbbbbbb src/with space.cs" + Z +
            "1 A. N... 000000 100644 100644 0000000 ccccccc new.txt" + Z +
            "1 MD N... 100644 100644 000000 ddddddd eeeeeee gone.txt" + Z +
            "2 R. N... 100644 100644 100644 fffffff fffffff R100 renamed.txt" + Z + "old name.txt" + Z +
            "u UU N... 100644 100644 100644 100644 1111111 2222222 3333333 conflict.txt" + Z +
            "? untracked file.txt" + Z +
            "! bin/" + Z;
        var s = PorcelainV2.Parse(output);
        Assert.Equal(7, s.Entries.Count);

        var modified = s.Entries[0];
        Assert.Equal("src/with space.cs", modified.Path);
        Assert.True(modified.IsUnstaged);
        Assert.False(modified.IsStaged);
        Assert.Equal('M', modified.WorkTreeLetter);

        var added = s.Entries[1];
        Assert.True(added.IsStaged);
        Assert.False(added.IsUnstaged);
        Assert.Equal('A', added.IndexLetter);
        Assert.Equal("100644", added.IndexMode);

        var both = s.Entries[2];
        Assert.True(both.IsStaged && both.IsUnstaged);
        Assert.Equal('D', both.WorkTreeLetter);

        var renamed = s.Entries[3];
        Assert.Equal(GitEntryKind.Renamed, renamed.Kind);
        Assert.Equal("renamed.txt", renamed.Path);
        Assert.Equal("old name.txt", renamed.OriginalPath);
        Assert.True(renamed.IsStaged);

        var conflict = s.Entries[4];
        Assert.True(conflict.IsConflict);
        Assert.True(conflict.IsUnstaged);
        Assert.Equal('!', conflict.WorkTreeLetter);

        Assert.True(s.Entries[5].IsUntracked);
        Assert.Equal("untracked file.txt", s.Entries[5].Path);
        Assert.Equal('U', s.Entries[5].WorkTreeLetter);
        Assert.True(s.Entries[6].IsIgnored);
        Assert.Equal("bin/", s.Entries[6].Path);
    }

    [Fact]
    public void Porcelain_v2_skips_malformed_records()
    {
        var s = PorcelainV2.Parse("1 M" + Z + "2 R. short" + Z + "x unknown" + Z + "? ok.txt" + Z + Z);
        var e = Assert.Single(s.Entries);
        Assert.Equal("ok.txt", e.Path);
    }

    [Fact]
    public void Decorations_follow_vscode_letters_and_colours()
    {
        static GitStatusEntry E(char x, char y, GitEntryKind k = GitEntryKind.Changed) => new("f", x, y, k);
        Assert.Equal(("M", "gitDecoration.modifiedResourceForeground"), D(E('.', 'M')));
        Assert.Equal(("M", "gitDecoration.modifiedResourceForeground"), D(E('M', '.')));
        Assert.Equal(("A", "gitDecoration.addedResourceForeground"), D(E('A', '.')));
        Assert.Equal(("D", "gitDecoration.deletedResourceForeground"), D(E('.', 'D')));
        Assert.True(Engine.GitService.DecorationFor(E('.', 'D'))!.Strikethrough);
        Assert.Equal(("U", "gitDecoration.untrackedResourceForeground"), D(E('?', '?', GitEntryKind.Untracked)));
        Assert.Equal(("!", "gitDecoration.conflictingResourceForeground"), D(E('U', 'U', GitEntryKind.Unmerged)));
        Assert.True(Engine.GitService.DecorationFor(E('!', '!', GitEntryKind.Ignored))!.Faded);
        // the working tree wins: added to the index, then modified again, shows M
        Assert.Equal(("M", "gitDecoration.modifiedResourceForeground"), D(E('A', 'M')));

        static (string, string) D(GitStatusEntry e) { var d = Engine.GitService.DecorationFor(e)!; return (d.Badge, d.ColorKey); }
    }

    [Fact]
    public void Blame_porcelain_reuses_commit_headers_for_later_lines()
    {
        var sha = new string('a', 40);
        var zero = new string('0', 40);
        var output =
            $"{sha} 1 1 2\nauthor Jane Doe\nauthor-mail <jane@example.com>\nauthor-time 1700000000\nauthor-tz +0100\ncommitter Jane\nsummary Fix the parser\nfilename a.cs\n\tline one\n" +
            $"{sha} 2 2\n\tline two\n" +
            $"{zero} 3 3 1\nauthor External file (--contents)\nauthor-mail <external.file>\nauthor-time 1700000100\nauthor-tz +0000\nsummary Version of a.cs from a.cs\nfilename a.cs\n\tnew line\n";
        var lines = BlameParser.Parse(output);
        Assert.Equal(3, lines.Count);
        Assert.Equal("Jane Doe", lines[1].Author);
        Assert.Equal("Fix the parser", lines[1].Summary);
        Assert.Equal(2, lines[1].Line);
        Assert.Equal("line two", lines[1].Content);
        Assert.Equal(TimeSpan.FromHours(1), lines[0].AuthorTime.Offset);
        Assert.False(lines[0].IsUncommitted);
        Assert.True(lines[2].IsUncommitted);
        Assert.Equal("You, Uncommitted changes", Engine.GitService.Format(lines[2], DateTimeOffset.Now).Text);
        var formatted = Engine.GitService.Format(lines[0], DateTimeOffset.FromUnixTimeSeconds(1700000000).AddDays(21));
        Assert.Equal("Jane Doe, 3 weeks ago • Fix the parser", formatted.Text);
        Assert.Contains("jane@example.com", formatted.Tooltip);
    }

    [Fact]
    public void Relative_times_read_like_gitlens()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("just now", BlameParser.RelativeTime(now.AddSeconds(-5), now));
        Assert.Equal("1 minute ago", BlameParser.RelativeTime(now.AddMinutes(-1.5), now));
        Assert.Equal("5 hours ago", BlameParser.RelativeTime(now.AddHours(-5), now));
        Assert.Equal("2 days ago", BlameParser.RelativeTime(now.AddDays(-2), now));
        Assert.Equal("3 months ago", BlameParser.RelativeTime(now.AddDays(-95), now));
        Assert.Equal("2 years ago", BlameParser.RelativeTime(now.AddDays(-800), now));
    }

    [Fact]
    public void Splicing_applies_only_the_picked_hunk()
    {
        var index = "a\nb\nc\nd\ne\nf\ng\n";
        var work = "a\nB\nc\nd\ne\nF\ng\nh\n";
        // stage the change on line 1 only
        var staged = TextSplice.Apply(index, work, c => TextSplice.TouchesModifiedLines(c, 1, 1));
        Assert.Equal("a\nB\nc\nd\ne\nf\ng\n", staged);
        // the last two hunks (F, and h added at the end)
        Assert.Equal("a\nb\nc\nd\ne\nF\ng\nh\n", TextSplice.Apply(index, work, c => TextSplice.TouchesModifiedLines(c, 5, 7)));
        Assert.Null(TextSplice.Apply(index, work, c => TextSplice.TouchesModifiedLines(c, 3, 3)));
    }

    [Fact]
    public void Splicing_keeps_line_endings_and_a_missing_final_newline()
    {
        Assert.Equal("a\r\nX\r\nc", TextSplice.Apply("a\r\nb\r\nc", "a\r\nX\r\nc", _ => true));
        // appending a line to a file without a final newline changes the old last line's terminator too
        Assert.Equal("a\nb", TextSplice.Apply("a", "a\nb", _ => true));
        Assert.Equal(["a\n", "b\r\n", "c"], TextSplice.SplitKeepEnds("a\nb\r\nc"));
        Assert.Empty(TextSplice.SplitKeepEnds(""));
    }

    [Fact]
    public void Deleted_lines_are_picked_from_either_neighbour()
    {
        var c = new DiffChange(2, 1, 2, 0);
        Assert.True(TextSplice.TouchesModifiedLines(c, 1, 1));
        Assert.True(TextSplice.TouchesModifiedLines(c, 2, 2));
        Assert.False(TextSplice.TouchesModifiedLines(c, 4, 5));
    }
}
