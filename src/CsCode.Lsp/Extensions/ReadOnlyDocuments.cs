using CsCode.Core.Text;

namespace CsCode.Lsp.Extensions;

/// <summary>
/// Text a server computed, shown as a read-only document (rust-analyzer's Expand Macro, View Syntax Tree / HIR / MIR): the <c>results</c>
/// provider of Core's virtual documents (<see cref="IVirtualDocumentService"/>, the <c>cscode-virtual:</c> scheme that also serves library and
/// generated code), so the editor opens them like any file but refuses edits, and the tab says what they are (<c>vec!.rs [expanded]</c>).
/// <see cref="Put"/> returns <c>cscode-virtual://results/&lt;kind&gt;/&lt;producer&gt;/&lt;fileName&gt;</c>; the file name gives the language.
/// Bounded: the 32 most recent documents are kept (opening one again later reads the newest text put at its URI; one evicted reads as missing).
/// Registered in DI by <c>AddCsCodeLsp</c>, which registers it with the virtual document service; any extension may use it.
/// </summary>
public sealed class ReadOnlyDocuments : IVirtualDocumentProvider, IDisposable
{
    /// <summary>The <see cref="VirtualDocumentUri"/> authority served.</summary>
    public const string ProviderName = "results";
    internal const int Capacity = 32;

    private readonly object _gate = new();
    // most recent first; keyed by the URI's unescaped parts, so an opened URI whose text was re-escaped (vec%21.rs, vec!.rs) is still found
    private readonly LinkedList<(string Key, VirtualDocumentContent Content)> _recent = new();
    private readonly IVirtualDocumentService? _service;
    private readonly IDisposable? _registration;

    /// <param name="service">The virtual documents to serve through (null: <see cref="Put"/> still works, nothing can open the result).</param>
    public ReadOnlyDocuments(IVirtualDocumentService? service = null)
    {
        _service = service;
        _registration = service?.Register(this);
    }

    public string Name => ProviderName;

    /// <summary>Puts <paramref name="text"/> under <paramref name="producer"/>'s <paramref name="kind"/> and returns its URI (to open).</summary>
    public Uri Put(string producer, string kind, string fileName, string text)
    {
        var uri = VirtualDocumentUri.Create(ProviderName, string.IsNullOrWhiteSpace(kind) ? "result" : kind, string.IsNullOrWhiteSpace(producer) ? "server" : producer, fileName);
        var key = KeyOf(uri)!;
        lock (_gate)
        {
            for (var n = _recent.First; n is not null; n = n.Next)
                if (n.Value.Key == key) { _recent.Remove(n); break; }
            _recent.AddFirst((key, new VirtualDocumentContent(text)));
            while (_recent.Count > Capacity) _recent.RemoveLast();
        }
        _service?.Invalidate(uri); // the service may hold the text put there before
        return uri;
    }

    public ValueTask<VirtualDocumentContent?> ProvideAsync(Uri uri, CancellationToken ct)
    {
        if (KeyOf(uri) is { } key)
            lock (_gate)
                foreach (var entry in _recent)
                    if (entry.Key == key) return ValueTask.FromResult<VirtualDocumentContent?>(entry.Content);
        return ValueTask.FromResult<VirtualDocumentContent?>(null);
    }

    private static string? KeyOf(Uri uri) =>
        VirtualDocumentUri.TryParse(uri, out var provider, out var kind, out var id, out var name) && provider == ProviderName ? string.Join('\0', kind, id, name) : null;

    public void Dispose() => _registration?.Dispose();
}
