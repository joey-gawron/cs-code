using System.Text;
using CsCode.Core.Platform;

namespace CsCode.Lsp.Extensions;

/// <summary>
/// Text a server computed, shown as a read-only document (rust-analyzer's Expand Macro, View Syntax Tree / HIR / MIR): an
/// <see cref="IFileSystemProvider"/> for the <c>lsp-readonly:</c> scheme whose files say they are read-only, so the editor opens them like any
/// file but refuses edits. The path names the producer and ends with the file name the language is taken from
/// (<c>lsp-readonly:/rust-analyzer/expand-macro/vec!.rs</c>). Bounded: the 32 most recent documents are kept (opening one again later reads the
/// newest text put at its path; one evicted reads as missing). Registered in DI by <c>AddCsCodeLsp</c>; any extension may use it.
/// </summary>
public sealed class ReadOnlyDocuments : IFileSystemProvider
{
    public const string UriScheme = "lsp-readonly";
    internal const int Capacity = 32;

    private readonly object _gate = new();
    private readonly LinkedList<(string Path, byte[] Content, DateTimeOffset Written)> _recent = new();

    public string Scheme => UriScheme;

    /// <summary>Puts <paramref name="text"/> at <c>lsp-readonly:/&lt;producer&gt;/&lt;kind&gt;/&lt;fileName&gt;</c> and returns that URI (to open).</summary>
    public Uri Put(string producer, string kind, string fileName, string text)
    {
        var path = "/" + Segment(producer) + "/" + Segment(kind) + "/" + Segment(fileName);
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_gate)
        {
            for (var n = _recent.First; n is not null; n = n.Next)
                if (n.Value.Path == path) { _recent.Remove(n); break; }
            _recent.AddFirst((path, bytes, DateTimeOffset.UtcNow));
            while (_recent.Count > Capacity) _recent.RemoveLast();
        }
        return new Uri(UriScheme + ":" + path);
    }

    /// <summary>A path segment: no separators, percent-escaped where a URI needs it (<c>vec!</c> stays readable).</summary>
    private static string Segment(string s)
    {
        var clean = string.IsNullOrWhiteSpace(s) ? "document" : s.Replace('/', '_').Replace('\\', '_');
        return Uri.EscapeDataString(clean).Replace("%21", "!");
    }

    private (string Path, byte[] Content, DateTimeOffset Written)? Find(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        lock (_gate)
            foreach (var entry in _recent)
                if (Uri.UnescapeDataString(entry.Path) == path) return entry;
        return null;
    }

    public ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default) =>
        ValueTask.FromResult(Find(uri) is { } e ? new FileStat(FileType.File, e.Content.Length, e.Written, IsReadOnly: true) : null);

    public ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default) =>
        Find(uri) is { } e ? ValueTask.FromResult(e.Content) : ValueTask.FromException<byte[]>(new FileNotFoundException("This read-only document is no longer available.", uri.ToString()));

    public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<DirectoryEntry>>([]);

    private static ValueTask ReadOnly(Uri uri) => ValueTask.FromException(new UnauthorizedAccessException($"{uri} is read-only."));

    public ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default) => ReadOnly(uri);
    public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) => ReadOnly(uri);
    public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) => ReadOnly(uri);
    public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => ReadOnly(source);
    public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => ReadOnly(target);
}
