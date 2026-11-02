using CsCode.Core.Text;
using CsCode.Lsp.Client;

namespace CsCode.Lsp.CSharp;

/// <summary>
/// How one language server names documents that are not files of the workspace (library code it decompiled or read from metadata, files a
/// source generator produced) and how their text is fetched. Registered per definition id in <see cref="ExternalDocuments"/>.
/// </summary>
internal interface IExternalDocumentSource
{
    /// <summary>
    /// The kind (<see cref="VirtualDocumentKinds"/>) and file name of <paramref name="serverUri"/> when it is such a document of this server,
    /// else null (a workspace file: left as it is).
    /// </summary>
    (string Kind, string FileName)? Classify(string serverUri);

    /// <summary>The text of <paramref name="serverUri"/>, asked of the running server (or read from where it wrote it). Null: unknown now.</summary>
    ValueTask<string?> FetchAsync(LanguageClient client, string serverUri, CancellationToken ct);
}

/// <summary>
/// The language servers' library and generated documents, as Core virtual documents (<c>cscode-virtual://lsp/…</c>, provider <see cref="ProviderName"/>):
/// a location a server answers with (go to definition into <c>System.String</c>) is turned into a virtual URI here, the editor opens it read-only
/// through <see cref="IVirtualDocumentService"/>, and this class fetches its text from the server that named it. The opened document is synced
/// back to that same server under the server's own URI (<see cref="ServerUriOf"/>), so hover and go to definition work inside it.
/// <para>Bounded: the newest <see cref="MaxEntries"/> names are remembered (a name is a few hundred bytes; the text is not kept here). A name
/// that fell out, or whose server stopped, opens no more and says so.</para>
/// </summary>
internal sealed class ExternalDocuments : IVirtualDocumentProvider
{
    public const string ProviderName = "lsp";
    internal const int MaxEntries = 1024;

    internal sealed record Entry(string Id, string DefinitionId, string SlotKey, string ServerUri, string Kind, Uri VirtualUri);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Def, string Slot, string ServerUri), Entry> _byServerUri = new();
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, IExternalDocumentSource> _sources = new(StringComparer.Ordinal);
    private readonly Func<string, string, LanguageClient?> _clientOf;
    private long _next;

    /// <param name="clientOf">(definition id, slot key) → the running client of that server, or null.</param>
    public ExternalDocuments(Func<string, string, LanguageClient?> clientOf) => _clientOf = clientOf;

    public string Name => ProviderName;

    public void AddSource(string definitionId, IExternalDocumentSource source)
    {
        lock (_gate) _sources[definitionId] = source;
    }

    public int Count { get { lock (_gate) return _byId.Count; } }

    /// <summary>The virtual URI for a location <paramref name="client"/>'s server answered with, or null when it names a workspace file.</summary>
    public Uri? ToVirtual(LanguageClient client, string serverUri)
    {
        IExternalDocumentSource? source;
        lock (_gate) source = _sources.GetValueOrDefault(client.Definition.Id);
        if (source?.Classify(serverUri) is not { } what) return null;
        var key = (client.Definition.Id, client.SlotKey, serverUri);
        lock (_gate)
        {
            if (_byServerUri.TryGetValue(key, out var known)) return known.VirtualUri;
            var id = (++_next).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
            var entry = new Entry(id, client.Definition.Id, client.SlotKey, serverUri, what.Kind,
                VirtualDocumentUri.Create(ProviderName, what.Kind, id, what.FileName));
            _byId[id] = entry;
            _byServerUri[key] = entry;
            _order.Enqueue(id);
            while (_byId.Count > MaxEntries && _order.TryDequeue(out var oldest))
                if (_byId.Remove(oldest, out var gone)) _byServerUri.Remove((gone.DefinitionId, gone.SlotKey, gone.ServerUri));
            return entry.VirtualUri;
        }
    }

    /// <summary>The entry behind a <c>cscode-virtual://lsp/…</c> URI, or null.</summary>
    public Entry? Find(Uri uri)
    {
        if (!VirtualDocumentUri.TryParse(uri, out var provider, out _, out var id, out _) || provider != ProviderName) return null;
        lock (_gate) return _byId.GetValueOrDefault(id);
    }

    /// <summary>The URI the owning server knows a virtual document by (what didOpen and every request about it carry), or null.</summary>
    public string? ServerUriOf(Uri uri) => Find(uri)?.ServerUri;

    public async ValueTask<VirtualDocumentContent?> ProvideAsync(Uri uri, CancellationToken ct)
    {
        if (Find(uri) is not { } entry) return null;
        IExternalDocumentSource? source;
        lock (_gate) source = _sources.GetValueOrDefault(entry.DefinitionId);
        if (source is null || _clientOf(entry.DefinitionId, entry.SlotKey) is not { IsOperational: true } client) return null;
        var text = await source.FetchAsync(client, entry.ServerUri, ct).ConfigureAwait(false);
        return text is null ? null : new VirtualDocumentContent(text, "csharp");
    }

    /// <summary>The last path segment of a URI or path, unescaped (<c>System.String.cs</c>).</summary>
    internal static string LastSegment(string uriOrPath)
    {
        var path = Uri.TryCreate(uriOrPath, UriKind.Absolute, out var u) ? Uri.UnescapeDataString(u.AbsolutePath) : uriOrPath;
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }
}
