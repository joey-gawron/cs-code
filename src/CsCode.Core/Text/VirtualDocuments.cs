using System.Text;
using CsCode.Core.Platform;

namespace CsCode.Core.Text;

// Additive (stage 5, C#): read-only documents whose text comes from a provider rather than a file on disk.

/// <summary>
/// What a virtual document holds; its label is the suffix of the tab title (<c>String.cs [decompiled]</c>).
/// </summary>
public static class VirtualDocumentKinds
{
    /// <summary>A library's code decompiled from its IL (ICSharpCode.Decompiler in csharp-ls and Roslyn).</summary>
    public const string Decompiled = "decompiled";
    /// <summary>A library's public surface as declarations only, from its metadata (no bodies).</summary>
    public const string Metadata = "metadata";
    /// <summary>A library's original source, fetched through SourceLink or embedded in its PDB.</summary>
    public const string Source = "library source";
    /// <summary>A file a source generator produced during compilation.</summary>
    public const string Generated = "generated";
}

/// <summary>
/// URIs of virtual documents: <c>cscode-virtual://&lt;provider&gt;/&lt;kind&gt;/&lt;id&gt;/&lt;file name&gt;</c>. One scheme for library code and
/// source-generated files, whichever language server produced them; the <c>provider</c> (the URI's authority) says who serves the text and the
/// <c>id</c> is that provider's own key. The file name gives the tab its name and the editor its language (<c>.cs</c> → C#).
/// </summary>
public static class VirtualDocumentUri
{
    public const string Scheme = "cscode-virtual";

    public static Uri Create(string provider, string kind, string id, string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        ArgumentException.ThrowIfNullOrEmpty(id);
        var name = string.IsNullOrWhiteSpace(fileName) ? "untitled" : fileName.Replace('/', '_').Replace('\\', '_');
        return new Uri($"{Scheme}://{Uri.EscapeDataString(provider.ToLowerInvariant())}/{Uri.EscapeDataString(kind)}/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(name)}");
    }

    public static bool IsVirtual(Uri? uri) => uri is { IsAbsoluteUri: true } && string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>The parts of a virtual URI, or false when it is not one.</summary>
    public static bool TryParse(Uri? uri, out string provider, out string kind, out string id, out string fileName)
    {
        provider = kind = id = fileName = "";
        if (!IsVirtual(uri)) return false;
        var parts = uri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || uri.Host.Length == 0) return false;
        provider = Uri.UnescapeDataString(uri.Host);
        kind = Uri.UnescapeDataString(parts[0]);
        id = Uri.UnescapeDataString(parts[1]);
        fileName = Uri.UnescapeDataString(parts[2]);
        return true;
    }

    /// <summary>The tab title of a virtual document: <c>String.cs [decompiled]</c>.</summary>
    public static string Title(Uri uri) =>
        TryParse(uri, out _, out var kind, out _, out var name) ? (kind.Length > 0 ? $"{name} [{kind}]" : name) : uri.ToString();
}

/// <summary>A virtual document's text, and its language when the file name does not say it.</summary>
public sealed record VirtualDocumentContent(string Text, string? LanguageId = null);

/// <summary>Serves the virtual documents of one <see cref="VirtualDocumentUri"/> authority (CsCode.Lsp serves <c>lsp</c>).</summary>
public interface IVirtualDocumentProvider
{
    /// <summary>The URI authority served, lower case.</summary>
    string Name { get; }

    /// <summary>The text of <paramref name="uri"/>, or null when it is unknown (the server that produced it stopped and forgot it).</summary>
    ValueTask<VirtualDocumentContent?> ProvideAsync(Uri uri, CancellationToken ct);
}

/// <summary>
/// The virtual documents: providers register by authority; the editor opens a <c>cscode-virtual:</c> URI like a file (through
/// <see cref="IFileSystem"/>: Core's <see cref="VirtualDocumentService"/> is also the scheme's <see cref="IFileSystemProvider"/>), read-only.
/// </summary>
public interface IVirtualDocumentService
{
    IDisposable Register(IVirtualDocumentProvider provider);

    /// <summary>The content of a virtual URI (null: not virtual, no provider, or the provider does not know it).</summary>
    ValueTask<VirtualDocumentContent?> GetAsync(Uri uri, CancellationToken ct = default);
}

/// <summary>
/// Core's <see cref="IVirtualDocumentService"/>, and the read-only file system of the <c>cscode-virtual</c> scheme. A document is asked of its
/// provider when it is opened (stat, then read: the second answer comes from a small cache) and lives in the editor as long as a view shows it;
/// the cache keeps the last <see cref="MaxCachedDocuments"/> texts up to <see cref="MaxCachedChars"/> characters in all, so nothing grows with
/// the number of documents visited. Writes, deletes and renames fail with <see cref="UnauthorizedAccessException"/>.
/// </summary>
public sealed class VirtualDocumentService : IVirtualDocumentService, IFileSystemProvider
{
    public const int MaxCachedDocuments = 16;
    public const long MaxCachedChars = 8L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, IVirtualDocumentProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    // most recently used last
    private readonly LinkedList<(Uri Uri, VirtualDocumentContent Content)> _cache = new();
    private long _cachedChars;

    public string Scheme => VirtualDocumentUri.Scheme;

    public IDisposable Register(IVirtualDocumentProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
        {
            if (!_providers.TryAdd(provider.Name, provider)) throw new InvalidOperationException($"A virtual document provider named '{provider.Name}' is already registered.");
        }
        return Lifecycle.Disposable.Create(() =>
        {
            lock (_gate)
            {
                if (_providers.TryGetValue(provider.Name, out var p) && ReferenceEquals(p, provider)) _providers.Remove(provider.Name);
                for (var node = _cache.First; node is not null;)
                {
                    var next = node.Next;
                    if (string.Equals(node.Value.Uri.Host, provider.Name, StringComparison.OrdinalIgnoreCase)) Evict(node);
                    node = next;
                }
            }
        });
    }

    public async ValueTask<VirtualDocumentContent?> GetAsync(Uri uri, CancellationToken ct = default)
    {
        if (!VirtualDocumentUri.TryParse(uri, out var name, out _, out _, out _)) return null;
        IVirtualDocumentProvider? provider;
        lock (_gate)
        {
            for (var node = _cache.Last; node is not null; node = node.Previous)
                if (node.Value.Uri == uri)
                {
                    _cache.Remove(node);
                    _cache.AddLast(node);
                    return node.Value.Content;
                }
            provider = _providers.GetValueOrDefault(name);
        }
        if (provider is null) return null;
        var content = await provider.ProvideAsync(uri, ct).ConfigureAwait(false);
        if (content is null) return null;
        lock (_gate)
        {
            if (!_providers.TryGetValue(name, out var still) || !ReferenceEquals(still, provider)) return content; // unregistered meanwhile
            for (var node = _cache.First; node is not null; node = node.Next)
                if (node.Value.Uri == uri) { Evict(node); break; }
            if (content.Text.Length <= MaxCachedChars)
            {
                _cache.AddLast((uri, content));
                _cachedChars += content.Text.Length;
                while (_cache.Count > MaxCachedDocuments || _cachedChars > MaxCachedChars) Evict(_cache.First!);
            }
        }
        return content;
    }

    private void Evict(LinkedListNode<(Uri Uri, VirtualDocumentContent Content)> node)
    {
        _cachedChars -= node.Value.Content.Text.Length;
        _cache.Remove(node);
    }

    /// <summary>Documents cached right now (tests).</summary>
    public int CachedCount { get { lock (_gate) return _cache.Count; } }

    // ---- IFileSystemProvider: read-only files ----

    public async ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default) =>
        await GetAsync(uri, ct).ConfigureAwait(false) is { } content
            ? new FileStat(FileType.File, Encoding.UTF8.GetByteCount(content.Text), DateTimeOffset.UnixEpoch, IsReadOnly: true)
            : null;

    public async ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default) =>
        await GetAsync(uri, ct).ConfigureAwait(false) is { } content
            ? Encoding.UTF8.GetBytes(content.Text)
            : throw new FileNotFoundException($"{uri} is not available: the language server that produced it no longer knows it.", uri.ToString());

    public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<DirectoryEntry>>([]);

    public ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default) => ReadOnly(uri);
    public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) => ReadOnly(uri);
    public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) => ReadOnly(uri);
    public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => ReadOnly(source);
    public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => ReadOnly(target);

    private static ValueTask ReadOnly(Uri uri) =>
        ValueTask.FromException(new UnauthorizedAccessException($"{VirtualDocumentUri.Title(uri)} is read-only: it is library or generated code."));
}
