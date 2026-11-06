using CsCode.Core.Platform;

namespace CsCode.Remote.Shell;

/// <summary>Tab completion of a directory path typed on the connected host (the Open Remote Folder box): lists the parent directory through the host's file system (<c>readDirectory</c>).</summary>
public static class RemotePathCompletion
{
    /// <summary>The most candidates offered (a huge directory is not listed whole).</summary>
    public const int MaxCandidates = 200;

    /// <summary>
    /// Directories (and symbolic links, which may lead to one) of the directory part of <paramref name="text"/> whose names start with the rest,
    /// each as a full path ending in <c>/</c>, sorted. <c>~</c> is the host's <paramref name="home"/>; a relative text is taken from it too. Names
    /// starting with a dot are offered only when the typed part starts with one. Nothing (not an error) when the directory cannot be read.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CompleteAsync(Func<Uri, CancellationToken, ValueTask<IReadOnlyList<DirectoryEntry>>> readDirectory, Func<string, Uri?> toUri, string home, string text, CancellationToken ct)
    {
        var h = home.TrimEnd('/');
        var path = text == "~" ? h + "/" : text.StartsWith("~/", StringComparison.Ordinal) ? h + text[1..] : text.StartsWith('/') ? text : h + "/" + text;
        var slash = path.LastIndexOf('/');
        var directory = path[..(slash + 1)];
        var prefix = path[(slash + 1)..];
        if (toUri(directory) is not { } uri) return [];
        IReadOnlyList<DirectoryEntry> entries;
        try { entries = await readDirectory(uri, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or TimeoutException) { return []; }
        var names = entries
            .Where(e => (e.Type & (FileType.Directory | FileType.SymbolicLink)) != 0)
            .Select(e => e.Name)
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && (prefix.StartsWith('.') || !n.StartsWith('.')))
            .Order(StringComparer.Ordinal)
            .Take(MaxCandidates);
        return [.. names.Select(n => directory + n + "/")];
    }
}
