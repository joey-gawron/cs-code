namespace CsCode.Testing.Engine;

/// <summary>
/// Tools report the paths they resolved (node's <c>path.resolve</c>, a compiler's source paths): through symbolic links, so a folder opened as
/// <c>/var/…</c> on macOS comes back as <c>/private/var/…</c>, or a linked checkout as its target. These helpers put such a path back under the
/// directory the editor knows, so ids, Error List entries and editor glyphs match the open documents.
/// </summary>
public static class PhysicalPaths
{
    /// <summary><paramref name="path"/> with every symbolic link among its directories resolved (realpath).</summary>
    public static string Resolve(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var result = Path.GetPathRoot(full)!;
            foreach (var part in full[result.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                result = Path.Combine(result, part);
                var info = new DirectoryInfo(result);
                if (info.Exists && info.ResolveLinkTarget(returnFinalTarget: true) is { } target) result = target.FullName;
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return path; }
    }

    /// <summary>
    /// <paramref name="path"/> under <paramref name="directory"/> as the editor knows it: unchanged when it already is, rebased when it is under
    /// the directory's resolved form, else unchanged.
    /// </summary>
    public static string UnderLogical(string path, string directory)
    {
        if (StackTraces.IsUnder(path, directory)) return path;
        var physical = Resolve(directory);
        if (physical != directory && StackTraces.IsUnder(path, physical)) return Path.Combine(directory, path[(physical.TrimEnd(Path.DirectorySeparatorChar).Length + 1)..]);
        return path;
    }
}
