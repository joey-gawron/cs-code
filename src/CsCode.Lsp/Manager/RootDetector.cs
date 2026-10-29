using System.Collections.Concurrent;
using CsCode.Core.Languages;

namespace CsCode.Lsp.Manager;

/// <summary>
/// Finds the workspace root a document belongs to for a server: walk up from the file looking for any of the definition's RootMarkers
/// (names or globs such as <c>*.sln</c>), never leaving the workspace folder that contains the file; fall back to that folder, then to
/// the file's directory. Definitions without markers use the workspace folder(s) directly (one server for all folders).
/// Probing touches the file system, so it only ever runs on the Language lane. Results are cached briefly and the cache is bounded.
/// </summary>
internal sealed class RootDetector
{
    private const int MaxCache = 512;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<(string Dir, string Markers), (string? Root, long Ticks)> _cache = new();

    public readonly record struct Root(string Key, Uri? Uri, bool FollowsWorkspaceFolders);

    public Root Resolve(LanguageServerDefinition def, Uri documentUri, IReadOnlyList<Uri> workspaceFolders)
    {
        var markers = def.RootMarkers;
        if (markers is not { Count: > 0 })
            return new Root("ws", workspaceFolders.FirstOrDefault(), true);

        if (!documentUri.IsFile)
        {
            var folder = workspaceFolders.FirstOrDefault();
            return new Root(folder?.AbsoluteUri ?? "none", folder, false);
        }

        var file = documentUri.LocalPath;
        var dir = Path.GetDirectoryName(file);
        var bound = workspaceFolders.Where(f => f.IsFile && Contains(f.LocalPath, file)).OrderByDescending(f => f.LocalPath.Length).FirstOrDefault();
        var found = dir is null ? null : Probe(dir, bound?.LocalPath, markers);
        if (found is not null) return ToRoot(found);
        if (bound is not null) return ToRoot(bound.LocalPath);
        return dir is null ? new Root("none", null, false) : ToRoot(dir);
    }

    private static Root ToRoot(string path)
    {
        var uri = new Uri(Path.TrimEndingDirectorySeparator(path) + "/");
        return new Root(uri.AbsoluteUri, uri, false);
    }

    private static bool Contains(string folder, string file)
    {
        var f = Path.TrimEndingDirectorySeparator(folder);
        return file.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.Ordinal) || file.StartsWith(f + "/", StringComparison.Ordinal);
    }

    private string? Probe(string startDir, string? boundDir, IReadOnlyList<string> markers)
    {
        var markerKey = string.Join('|', markers);
        var bound = boundDir is null ? null : Path.TrimEndingDirectorySeparator(boundDir);
        var now = Environment.TickCount64;
        for (var dir = startDir; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (_cache.TryGetValue((dir, markerKey), out var hit) && now - hit.Ticks < Ttl.TotalMilliseconds)
            {
                if (hit.Root is not null) return hit.Root;
            }
            else
            {
                var has = HasMarker(dir, markers);
                if (_cache.Count >= MaxCache) _cache.Clear(); // crude but bounded: probing is cheap to redo
                _cache[(dir, markerKey)] = (has ? dir : null, now);
                if (has) return dir;
            }
            if (bound is not null && string.Equals(Path.TrimEndingDirectorySeparator(dir), bound, StringComparison.Ordinal)) break;
        }
        return null;
    }

    private static bool HasMarker(string dir, IReadOnlyList<string> markers)
    {
        foreach (var m in markers)
        {
            try
            {
                if (m.AsSpan().IndexOfAny('*', '?') >= 0)
                {
                    using var e = Directory.EnumerateFileSystemEntries(dir, m).GetEnumerator();
                    if (e.MoveNext()) return true;
                }
                else if (File.Exists(Path.Combine(dir, m)) || Directory.Exists(Path.Combine(dir, m))) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable dir: not a root */ }
        }
        return false;
    }
}
