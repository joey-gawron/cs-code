using System.Text;

namespace CsCode.Remote.Protocol;

/// <summary>
/// Maps document/file URIs between the two ends of a connection. The server only ever sees <c>file:</c> URIs of its own machine; the client sees
/// them as <c>ssh://user@host:port/abs/path</c> (remote workspace) or unchanged (local process isolation, <see cref="Identity"/>). The JSON converter
/// of a connection applies the mapping to every <see cref="Uri"/> in every model, both directions, so Core models cross the wire untouched.
/// </summary>
public abstract class UriMapper
{
    public static readonly UriMapper Identity = new IdentityMapper();

    /// <summary>Client URI → server URI (used when the client writes JSON).</summary>
    public abstract Uri ToServer(Uri clientUri);

    /// <summary>Server URI → client URI (used when the client reads JSON).</summary>
    public abstract Uri ToClient(Uri serverUri);

    /// <summary>True when <paramref name="clientUri"/> denotes something on the other end (a document or file the server serves).</summary>
    public abstract bool Handles(Uri clientUri);

    /// <summary>The server-side file path of a handled client URI (workspace folders), or null when it has none (untitled buffers).</summary>
    public abstract string? ToServerPath(Uri clientUri);

    private sealed class IdentityMapper : UriMapper
    {
        public override Uri ToServer(Uri clientUri) => clientUri;
        public override Uri ToClient(Uri serverUri) => serverUri;
        public override bool Handles(Uri clientUri) => clientUri.IsAbsoluteUri && (clientUri.IsFile || clientUri.Scheme == "untitled");
        public override string? ToServerPath(Uri clientUri) => clientUri.IsAbsoluteUri && clientUri.IsFile ? FileUris.ToPath(clientUri, OperatingSystem.IsWindows()) : null;
    }
}

/// <summary>
/// <c>ssh://[user@]host[:port]/abs/path</c> ⇄ remote path ⇄ the server's <c>file:</c> URI.
/// <list type="bullet">
/// <item>Client URIs are built with one escaping rule (<see cref="Uri.EscapeDataString"/> per path segment), the same one the Explorer uses for the
/// children it lists, so a URI that comes back from the server (a diagnostic, a definition) equals the one the editor opened.</item>
/// <item>Server URIs are built exactly like Platform's <c>FileUri.FromPath</c> (asserted by a test), so they equal what the server's language host uses.</item>
/// <item>Windows remotes: the remote path <c>C:/Users/x</c> is the URI path <c>/C:/Users/x</c>.</item>
/// </list>
/// </summary>
public sealed class SshUriMapper : UriMapper
{
    public const string Scheme = "ssh";

    public SshUriMapper(RemoteAuthority authority, bool windowsRemote = false)
    {
        Authority = authority;
        WindowsRemote = windowsRemote;
    }

    public RemoteAuthority Authority { get; }
    public bool WindowsRemote { get; }

    public override bool Handles(Uri clientUri) =>
        clientUri.IsAbsoluteUri && string.Equals(clientUri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) && Authority.Matches(clientUri);

    /// <summary>The remote path of a client URI (<c>/home/me/a b.txt</c>; <c>C:/x</c> on a Windows remote).</summary>
    public string ToRemotePath(Uri clientUri)
    {
        if (!Handles(clientUri)) throw new NotSupportedException($"'{clientUri}' is not on {Authority}");
        return PathOf(clientUri, WindowsRemote);
    }

    internal static string PathOf(Uri uri, bool windowsRemote)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.Length == 0) path = "/";
        if (windowsRemote && path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':') path = path[1..];
        return path;
    }

    /// <summary>The client URI of a remote path. A trailing '/' is kept (directory URIs).</summary>
    public Uri FromRemotePath(string remotePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(remotePath);
        var p = remotePath.Replace('\\', '/');
        if (!p.StartsWith('/')) p = "/" + p; // C:/x -> /C:/x
        var sb = new StringBuilder(Authority.Root.AbsoluteUri.TrimEnd('/'));
        var segments = p.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            sb.Append('/');
            var seg = segments[i];
            // keep the drive letter's colon readable ("/C:/"), everything else uses the Explorer's escaping
            if (i == 1 && WindowsRemote && seg.Length == 2 && char.IsAsciiLetter(seg[0]) && seg[1] == ':') sb.Append(seg);
            else sb.Append(Uri.EscapeDataString(seg));
        }
        return new Uri(sb.ToString(), UriKind.Absolute);
    }

    public override string? ToServerPath(Uri clientUri) => Handles(clientUri) ? ToRemotePath(clientUri) : null;

    public override Uri ToServer(Uri clientUri) => Handles(clientUri) ? FileUris.FromPath(ToRemotePath(clientUri), WindowsRemote) : clientUri;

    public override Uri ToClient(Uri serverUri) =>
        serverUri.IsAbsoluteUri && serverUri.IsFile ? FromRemotePath(FileUris.ToPath(serverUri, WindowsRemote)) : serverUri;
}

/// <summary>Who we are connected to: user, host (name, alias or IP literal) and port. Its <see cref="Root"/> is <c>ssh://user@host:port/</c>.</summary>
public sealed record RemoteAuthority
{
    public RemoteAuthority(string host, string? user = null, int? port = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        Host = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        User = string.IsNullOrEmpty(user) ? null : user;
        Port = port is > 0 and < 65536 ? port : null;
        var sb = new StringBuilder(SshUriMapper.Scheme).Append("://");
        if (User is not null) sb.Append(Uri.EscapeDataString(User)).Append('@');
        sb.Append(Host.Contains(':') ? "[" + Host + "]" : Host); // IPv6 literal
        if (Port is { } p) sb.Append(':').Append(p);
        Root = new Uri(sb.Append('/').ToString(), UriKind.Absolute);
    }

    public string Host { get; }
    public string? User { get; }
    public int? Port { get; }

    /// <summary><c>ssh://user@host:port/</c></summary>
    public Uri Root { get; }

    /// <summary>The <c>[user@]host[:port]</c> text, as ssh takes it (IPv6 in brackets only when a port follows).</summary>
    public string Destination => (User is null ? "" : User + "@") + (Port is null ? Host : (Host.Contains(':') ? "[" + Host + "]" : Host) + ":" + Port);

    /// <summary>True when <paramref name="uri"/> (any scheme) has this authority (case-insensitive host, exact user and port).</summary>
    public bool Matches(Uri uri) =>
        string.Equals(uri.IdnHost.Trim('[', ']'), Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.UserInfo.Length == 0 ? null : Uri.UnescapeDataString(uri.UserInfo), User, StringComparison.Ordinal)
        && (uri.IsDefaultPort || uri.Port < 0 ? Port is null : uri.Port == Port);

    /// <summary>Parses <c>[user@]host[:port]</c> (IPv6 as <c>[::1]:22</c> or bare <c>::1</c>) or an <c>ssh://</c> URI's authority.</summary>
    public static RemoteAuthority Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var s = text.Trim();
        if (s.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            var u = new Uri(s, UriKind.Absolute);
            return new RemoteAuthority(u.IdnHost.Trim('[', ']'), u.UserInfo.Length == 0 ? null : Uri.UnescapeDataString(u.UserInfo), u.IsDefaultPort || u.Port < 0 ? null : u.Port);
        }
        string? user = null;
        var at = s.LastIndexOf('@');
        if (at >= 0) { user = s[..at]; s = s[(at + 1)..]; }
        int? port = null;
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close < 0) throw new FormatException($"Unterminated IPv6 host in '{text}'");
            var rest = s[(close + 1)..];
            if (rest.StartsWith(':') && int.TryParse(rest[1..], out var p6)) port = p6;
            else if (rest.Length > 0) throw new FormatException($"Unexpected '{rest}' after the host in '{text}'");
            s = s[1..close];
        }
        else if (s.Count(c => c == ':') == 1)
        {
            var colon = s.IndexOf(':');
            if (!int.TryParse(s[(colon + 1)..], out var p)) throw new FormatException($"Invalid port in '{text}'");
            port = p;
            s = s[..colon];
        }
        if (s.Length == 0) throw new FormatException($"No host in '{text}'");
        return new RemoteAuthority(s, user, port);
    }

    public override string ToString() => Destination;
}

/// <summary>
/// <c>file:</c> URI ⇄ path, byte-for-byte the algorithm of Platform's <c>FileUri</c> (Remote references Core only; a test pins the equality), so URIs
/// the server builds for paths equal the ones its language host and document manager use.
/// </summary>
internal static class FileUris
{
    public static Uri FromPath(string path, bool windows)
    {
        var sb = new StringBuilder("file://");
        var p = windows ? path.Replace('\\', '/') : path;
        if (windows && p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
        {
            sb.Append('/').Append(p[0]).Append(':');
            Append(sb, p.Length > 2 ? p[2..] : "/");
        }
        else Append(sb, p.StartsWith('/') ? p : "/" + p);
        return new Uri(sb.ToString());
    }

    public static string ToPath(Uri uri, bool windows)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (windows && path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':') path = path[1..];
        return path;
    }

    private static void Append(StringBuilder sb, string path)
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
