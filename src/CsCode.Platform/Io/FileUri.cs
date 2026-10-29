using System.Text;

namespace CsCode.Platform.Io;

/// <summary>
/// <c>file:</c> URI &lt;-&gt; local path mapping. <see cref="Uri.LocalPath"/> alone is not enough: a path containing <c>#</c>, <c>?</c>
/// or <c>%</c> must survive a round trip, Windows drive letters and UNC shares need their own shape, and the same logic has to be
/// testable on Linux, so the OS-specific parts take an explicit <c>windows</c> flag.
/// </summary>
public static class FileUri
{
    public static string ToPath(Uri uri) => ToPath(uri, OperatingSystem.IsWindows());

    public static Uri FromPath(string path) => FromPath(path, OperatingSystem.IsWindows());

    /// <summary>Directory form of <see cref="FromPath(string)"/>: the URI ends with '/', so relative resolution works.</summary>
    public static Uri FromDirectoryPath(string path)
    {
        var u = FromPath(path);
        var s = u.AbsoluteUri;
        return s.EndsWith('/') ? u : new Uri(s + "/");
    }

    public static string ToPath(Uri uri, bool windows)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Unsupported URI scheme '{(uri.IsAbsoluteUri ? uri.Scheme : "(relative)")}': the local file system only handles 'file:'");

        // AbsolutePath keeps %XX escapes exactly as written; decode once ourselves (LocalPath re-interprets some of them).
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var host = uri.Host;
        var hasHost = host.Length > 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

        if (windows)
        {
            if (hasHost) return @"\\" + host + path.Replace('/', '\\'); // UNC: file://server/share/x
            if (path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':')
                path = path[1..];                                          // file:///C:/x -> C:/x
            return path.Replace('/', '\\');
        }

        if (hasHost) throw new NotSupportedException($"file URI with a remote host ('{host}') is not supported on this OS");
        return path;
    }

    public static Uri FromPath(string path, bool windows)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var sb = new StringBuilder("file://");

        if (windows)
        {
            var p = path.Replace('\\', '/');
            if (p.StartsWith("//", StringComparison.Ordinal))
            {
                // UNC \\server\share\dir -> file://server/share/dir
                var rest = p[2..];
                var slash = rest.IndexOf('/');
                var host = slash < 0 ? rest : rest[..slash];
                sb.Append(host);
                AppendPath(sb, slash < 0 ? "/" : rest[slash..]);
            }
            else if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
            {
                sb.Append('/').Append(p[0]).Append(':');
                AppendPath(sb, p.Length > 2 ? p[2..] : "/");
            }
            else
            {
                AppendPath(sb, p.StartsWith('/') ? p : "/" + p);
            }
        }
        else
        {
            if (path[0] != '/') path = Path.GetFullPath(path);
            AppendPath(sb, path);
        }
        return new Uri(sb.ToString());
    }

    // Escape only what would change the URI's structure (or its meaning on the way back); the rest is left to Uri's own
    // canonicalisation so that FromPath(p) == new Uri(p) for ordinary names (document managers key dictionaries by Uri).
    private static void AppendPath(StringBuilder sb, string path)
    {
        foreach (var ch in path)
        {
            switch (ch)
            {
                case '%': sb.Append("%25"); break;
                case '#': sb.Append("%23"); break;
                case '?': sb.Append("%3F"); break;
                case ' ': sb.Append("%20"); break;
                case < ' ' or '\x7f': sb.Append('%').Append(((int)ch).ToString("X2")); break;
                default: sb.Append(ch); break;
            }
        }
    }
}
