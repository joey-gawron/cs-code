using System.Text;

namespace CsCode.Remote.Ssh;

/// <summary>
/// Quoting for the one place a shell string is unavoidable: ssh hands the remote command to the remote login shell as a single string. We never
/// build that string from data by concatenation: it is always <c>sh -c '&lt;fixed script&gt;' cscode '&lt;arg1&gt;' '&lt;arg2&gt;'…</c> where the script is a
/// constant and every argument is single-quoted here (a <c>'</c> becomes <c>'\''</c>). Inside single quotes a POSIX shell interprets nothing
/// (<c>; $() ` " &amp; | \</c> and newlines are literal), which the fuzz test checks against a real <c>sh</c>.
/// Requires a POSIX-compatible login shell on the remote (sh, bash, dash, zsh, ksh); csh/fish are not supported.
/// </summary>
public static class PosixShell
{
    public static string Quote(string arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        if (arg.Contains('\0')) throw new ArgumentException("A shell argument cannot contain NUL", nameof(arg));
        var sb = new StringBuilder(arg.Length + 2).Append('\'');
        foreach (var ch in arg)
        {
            if (ch == '\'') sb.Append("'\\''");
            else sb.Append(ch);
        }
        return sb.Append('\'').ToString();
    }

    /// <summary><c>sh -c '&lt;script&gt;' &lt;argv0&gt; '&lt;args&gt;'…</c>: the script reads its inputs as <c>"$1"</c>, <c>"$2"</c>… and never sees them as code.</summary>
    public static string ScriptCommand(string script, IReadOnlyList<string> args, string argv0 = "cscode")
    {
        var sb = new StringBuilder("sh -c ").Append(Quote(script)).Append(' ').Append(Quote(argv0));
        foreach (var a in args) sb.Append(' ').Append(Quote(a));
        return sb.ToString();
    }
}
