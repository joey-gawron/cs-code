namespace CsCode.Panels.Search.SearchEditor;

/// <summary>
/// The path lines of a search editor, both ways (VS Code's label service and the <c>search-result</c> extension's <c>relativePathToUri</c>): a file
/// in the one workspace folder is written relative to it (<c>src/a.cs</c>), with several folders as <c>folder • src/a.cs</c>, anything else as
/// its absolute path (another scheme's file as its URI).
/// </summary>
public static class SearchResultPaths
{
    private const string MultiRootSeparator = " • ";
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The path line text (without the trailing ':') of <paramref name="file"/>.</summary>
    public static string LabelFor(Uri file, IReadOnlyList<Uri> folders)
    {
        foreach (var folder in folders)
        {
            if (Relative(folder, file) is not { } rel) continue;
            return folders.Count > 1 ? FolderName(folder) + MultiRootSeparator + rel : rel;
        }
        if (file.IsFile) return file.LocalPath;
        return file.ToString();
    }

    /// <summary>
    /// The file a path line names: absolute paths and URIs as they are, <c>~/</c> under the home folder, <c>folder • path</c> in the folder of
    /// that name, a relative path in the one workspace folder (with several, in the folder that holds a saved search editor, else the first).
    /// Null when nothing fits (an <c>/Untitled-1</c> path, no folder open).
    /// </summary>
    public static Uri? Resolve(string label, IReadOnlyList<Uri> folders, Uri? searchEditor = null)
    {
        if (label.Length == 0) return null;
        if (label.StartsWith("~/", StringComparison.Ordinal))
            return new Uri(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), label[2..]));
        if (Path.IsPathRooted(label) && (label.StartsWith('/') || label.StartsWith('\\') || (label.Length > 2 && label[1] == ':')))
        {
            if (label.TrimStart('/', '\\').StartsWith("Untitled-", StringComparison.Ordinal)) return null;
            return Uri.TryCreate(label, UriKind.Absolute, out var path) ? path : null;
        }
        // another scheme's file (ssh://host/path, untitled:Untitled-1): its URI; a one-letter scheme would be a Windows drive
        var colon = label.IndexOf(':');
        if (colon > 1 && Uri.CheckSchemeName(label[..colon]) && Uri.TryCreate(label, UriKind.Absolute, out var other)) return other;
        if (folders.Count == 0) return null;

        var sep = label.IndexOf(MultiRootSeparator, StringComparison.Ordinal);
        if (sep > 0 && folders.Count > 1)
        {
            var name = label[..sep];
            foreach (var folder in folders)
                if (string.Equals(FolderName(folder), name, PathComparison)) return Combine(folder, label[(sep + MultiRootSeparator.Length)..]);
        }
        if (folders.Count == 1) return Combine(folders[0], label);
        if (searchEditor is not null)
            foreach (var folder in folders)
                if (Relative(folder, searchEditor) is not null) return Combine(folder, label);
        return Combine(folders[0], label);
    }

    /// <summary>A folder's name as VS Code shows it in multi-root labels (its last path segment).</summary>
    public static string FolderName(Uri folder)
    {
        var path = (folder.IsFile ? folder.LocalPath : Uri.UnescapeDataString(folder.AbsolutePath)).TrimEnd('/', '\\');
        var name = Path.GetFileName(path);
        return name.Length > 0 ? name : path;
    }

    /// <summary><paramref name="file"/>'s path relative to <paramref name="folder"/> with '/' separators, or null when it is not inside it.</summary>
    internal static string? Relative(Uri folder, Uri file)
    {
        if (folder.IsFile != file.IsFile) return null;
        if (!folder.IsFile && !string.Equals(folder.GetLeftPart(UriPartial.Authority), file.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)) return null;
        var root = Normalise(folder).TrimEnd('/') + "/";
        var path = Normalise(file);
        return path.StartsWith(root, PathComparison) && path.Length > root.Length ? path[root.Length..] : null;
    }

    private static string Normalise(Uri uri) => (uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(uri.AbsolutePath)).Replace('\\', '/');

    /// <summary>A '/'-separated relative path under <paramref name="folder"/> (any scheme).</summary>
    internal static Uri Combine(Uri folder, string relative)
    {
        relative = relative.Replace('\\', '/').TrimStart('/');
        if (folder.IsFile) return new Uri(Path.Combine(folder.LocalPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        var baseUri = folder.AbsoluteUri.EndsWith('/') ? folder : new Uri(folder.AbsoluteUri + "/");
        return new Uri(baseUri, string.Join('/', relative.Split('/').Select(Uri.EscapeDataString)));
    }
}
