using System.Globalization;

namespace CsCode.Panels.Git.Engine;

/// <summary>Who last changed one line (<see cref="Line"/> is 1-based, as git reports it).</summary>
public sealed record BlameLine(int Line, string Sha, string Author, string AuthorMail, DateTimeOffset AuthorTime, string Summary, string Content)
{
    /// <summary>A line that is not in any commit yet (changed on disk or in the editor).</summary>
    public bool IsUncommitted => Sha.Length > 0 && Sha.All(c => c == '0');

    public string ShortSha => Sha.Length > 8 ? Sha[..8] : Sha;
}

/// <summary>Parser of <c>git blame --porcelain</c> (commit headers appear once per commit, then only the line header and the tab-prefixed content).</summary>
public static class BlameParser
{
    private sealed class Commit
    {
        public string Author = "", Mail = "", Summary = "";
        public long Time;
        public string Tz = "+0000";
    }

    public static IReadOnlyList<BlameLine> Parse(string output)
    {
        var result = new List<BlameLine>();
        var commits = new Dictionary<string, Commit>(StringComparer.Ordinal);
        Commit? current = null;
        string? sha = null;
        var finalLine = 0;
        foreach (var raw in output.Split('\n'))
        {
            if (raw.Length == 0) continue;
            if (raw[0] == '\t')
            {
                if (sha is null || current is null) continue;
                result.Add(new BlameLine(finalLine, sha, current.Author, current.Mail, ToTime(current.Time, current.Tz), current.Summary, raw[1..]));
                continue;
            }
            var space = raw.IndexOf(' ');
            var key = space < 0 ? raw : raw[..space];
            var value = space < 0 ? "" : raw[(space + 1)..];
            if (key.Length == 40 && IsHex(key))
            {
                // <sha> <orig line> <final line> [<lines in group>]
                sha = key;
                var nums = value.Split(' ');
                finalLine = nums.Length >= 2 && int.TryParse(nums[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 0;
                if (!commits.TryGetValue(sha, out current)) commits[sha] = current = new Commit();
                continue;
            }
            if (current is null) continue;
            switch (key)
            {
                case "author": current.Author = value; break;
                case "author-mail": current.Mail = value.Trim('<', '>'); break;
                case "author-time": _ = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out current.Time); break;
                case "author-tz": current.Tz = value; break;
                case "summary": current.Summary = value; break;
            }
        }
        return result;
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s) if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    private static DateTimeOffset ToTime(long unix, string tz)
    {
        var offset = TimeSpan.Zero;
        if (tz.Length == 5 && int.TryParse(tz.AsSpan(1, 2), CultureInfo.InvariantCulture, out var h) && int.TryParse(tz.AsSpan(3, 2), CultureInfo.InvariantCulture, out var m))
            offset = new TimeSpan(h, m, 0) * (tz[0] == '-' ? -1 : 1);
        return DateTimeOffset.FromUnixTimeSeconds(unix).ToOffset(offset);
    }

    /// <summary>"just now", "5 minutes ago", "3 weeks ago", "2 years ago" (GitLens' style).</summary>
    public static string RelativeTime(DateTimeOffset time, DateTimeOffset now)
    {
        var span = now - time;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        static string Unit(double n, string unit) { var v = (int)Math.Floor(n); return $"{v} {unit}{(v == 1 ? "" : "s")} ago"; }
        if (span < TimeSpan.FromHours(1)) return Unit(span.TotalMinutes, "minute");
        if (span < TimeSpan.FromDays(1)) return Unit(span.TotalHours, "hour");
        if (span < TimeSpan.FromDays(7)) return Unit(span.TotalDays, "day");
        if (span < TimeSpan.FromDays(30)) return Unit(span.TotalDays / 7, "week");
        if (span < TimeSpan.FromDays(365)) return Unit(span.TotalDays / 30, "month");
        return Unit(span.TotalDays / 365, "year");
    }
}
