using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Core.Text;

namespace CsCode.Panels.Search.Engine;

/// <summary>
/// A set of globs over workspace-relative paths ("/src/app/Program.cs": forward slashes, leading slash). Backed by Core's
/// <see cref="Glob"/> so the syntax is the one every other part of CsCode uses (<c>**</c>, <c>*</c>, <c>?</c>, <c>{a,b}</c>, <c>[abc]</c>).
/// </summary>
public sealed class GlobFilter
{
    public static readonly GlobFilter Empty = new([]);

    private readonly string[] _patterns;

    private GlobFilter(string[] patterns) => _patterns = patterns;

    public bool IsEmpty => _patterns.Length == 0;
    public IReadOnlyList<string> Patterns => _patterns;

    /// <summary>
    /// Parses the user-facing "files to include/exclude" syntax: comma separated (commas inside braces stay), where a bare name
    /// (<c>node_modules</c>, <c>src/app</c>, <c>bin/</c>) means that file or folder (and everything below it) at any depth, a leading
    /// <c>./</c> or <c>/</c> anchors at the workspace folder, and anything with wildcards is used as written.
    /// </summary>
    public static GlobFilter Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;
        var list = new List<string>();
        foreach (var raw in SplitTerms(text))
        {
            var t = raw.Trim().Replace('\\', '/');
            if (t.Length == 0) continue;
            var anchored = false;
            if (t.StartsWith("./", StringComparison.Ordinal)) { t = t[2..]; anchored = true; }
            else if (t.StartsWith('/')) { t = t.TrimStart('/'); anchored = true; }
            var folderOnly = t.EndsWith('/');
            t = t.TrimEnd('/');
            if (t.Length == 0) continue;

            var hasWildcards = t.IndexOfAny(['*', '?', '[', '{']) >= 0;
            var hasSlash = t.Contains('/');
            // Wildcard patterns are used as written (Core Glob already matches "*.cs" by file name and "src/*.cs" at any directory
            // boundary); a plain name or path means "that file or folder at any depth".
            var body = anchored ? "/" + t : hasWildcards ? t : "**/" + t;
            list.Add(body);
            // A pattern that can name a folder also covers everything below it.
            if (!t.EndsWith("**", StringComparison.Ordinal) && (!hasWildcards || hasSlash || folderOnly)) list.Add(body + "/**");
        }
        return list.Count == 0 ? Empty : new GlobFilter(list.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Patterns used exactly as given (settings such as <c>files.exclude</c> already contain full globs like <c>**/node_modules</c>).</summary>
    public static GlobFilter FromPatterns(IEnumerable<string> patterns)
    {
        var arr = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim().Replace('\\', '/')).Distinct(StringComparer.Ordinal).ToArray();
        return arr.Length == 0 ? Empty : new GlobFilter(arr);
    }

    /// <summary>Reads a <c>{ "glob": true|false }</c> object setting (VS Code's files.exclude shape); only <c>true</c> entries count.</summary>
    public static GlobFilter FromSetting(IConfigurationService? config, string key)
    {
        if (config?.GetRaw(key) is not { ValueKind: JsonValueKind.Object } obj) return Empty;
        var list = new List<string>();
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.True) continue;
            list.Add(p.Name);
            // An excluded folder hides everything below it ("**/node_modules" must also exclude "…/node_modules/x.js").
            if (!p.Name.EndsWith("**", StringComparison.Ordinal)) list.Add(p.Name.TrimEnd('/') + "/**");
        }
        return FromPatterns(list);
    }

    public GlobFilter Combine(GlobFilter other)
    {
        if (other.IsEmpty) return this;
        if (IsEmpty) return other;
        return new GlobFilter([.. _patterns, .. other._patterns]);
    }

    /// <summary><paramref name="path"/> must be a forward-slash path with a leading '/'.</summary>
    public bool IsMatch(string path)
    {
        foreach (var p in _patterns)
            if (Glob.IsMatch(p, path)) return true;
        return false;
    }

    private static IEnumerable<string> SplitTerms(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{': depth++; break;
                case '}' when depth > 0: depth--; break;
                case ',' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }
        yield return text[start..];
    }
}

/// <summary>Maps file URIs to workspace-relative glob paths ("/src/a.cs") for the folders being searched.</summary>
public sealed class RootSet
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string[] _roots; // local paths with forward slashes and a trailing '/'

    public RootSet(IEnumerable<Uri> folders)
    {
        _roots = folders.Where(f => f.IsFile).Select(f => Normalise(f.LocalPath)).Select(p => p.EndsWith('/') ? p : p + "/")
            .OrderByDescending(p => p.Length).ToArray();
    }

    public IReadOnlyList<string> Roots => _roots;

    public static string Normalise(string localPath) => localPath.Replace('\\', '/');

    /// <summary>The folder-relative path with a leading slash; absolute (leading slash) when no root contains the file.</summary>
    public string ToRelative(Uri uri)
    {
        var path = uri.IsFile ? Normalise(uri.LocalPath) : Uri.UnescapeDataString(uri.AbsolutePath);
        return ToRelative(path);
    }

    public string ToRelative(string normalisedPath)
    {
        foreach (var root in _roots)
        {
            if (normalisedPath.StartsWith(root, PathComparison)) return "/" + normalisedPath[root.Length..];
            // The root itself (no trailing slash in the file path).
            if (normalisedPath.Length + 1 == root.Length && root.StartsWith(normalisedPath, PathComparison)) return "/";
        }
        return normalisedPath.StartsWith('/') ? normalisedPath : "/" + normalisedPath;
    }

    public bool Contains(Uri uri)
    {
        if (!uri.IsFile) return false;
        var path = Normalise(uri.LocalPath);
        foreach (var root in _roots)
            if (path.StartsWith(root, PathComparison)) return true;
        return false;
    }
}
