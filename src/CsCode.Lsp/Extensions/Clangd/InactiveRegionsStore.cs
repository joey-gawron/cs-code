using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.Lsp.Extensions.Clangd;

/// <summary>
/// One clangd server's latest <c>textDocument/inactiveRegions</c> per document, served to the editor. Bounded: the
/// <see cref="MaxDocuments"/> documents reported most recently, at most <see cref="MaxRegions"/> regions each. Disposed with the server: every
/// document it knew is announced once more, now with no regions, so the editors stop fading them.
/// </summary>
internal sealed class InactiveRegionsStore : IInactiveRegionsProvider, IDisposable
{
    internal const int MaxDocuments = 256;
    internal const int MaxRegions = 10_000;

    private readonly object _gate = new();
    // most recently reported last
    private readonly Dictionary<Uri, LinkedListNode<(Uri Uri, IReadOnlyList<TextRange> Regions)>> _byUri = [];
    private readonly LinkedList<(Uri Uri, IReadOnlyList<TextRange> Regions)> _order = new();
    private readonly Emitter<Uri> _changed = new();
    private bool _disposed;

    public IEventSource<Uri> Changed => _changed;

    public IReadOnlyList<TextRange> GetInactiveRegions(Uri document)
    {
        lock (_gate) return _byUri.TryGetValue(document, out var node) ? node.Value.Regions : [];
    }

    public void Set(Uri document, IReadOnlyList<TextRange> regions)
    {
        Uri? evicted = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (_byUri.Remove(document, out var old)) _order.Remove(old);
            if (regions.Count > 0) _byUri[document] = _order.AddLast((document, regions));
            if (_order.Count > MaxDocuments && _order.First is { } first)
            {
                _order.RemoveFirst();
                _byUri.Remove(first.Value.Uri);
                evicted = first.Value.Uri;
            }
        }
        _changed.Fire(document);
        if (evicted is not null) _changed.Fire(evicted);
    }

    public void Dispose()
    {
        Uri[] known;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            known = [.. _byUri.Keys];
            _byUri.Clear();
            _order.Clear();
        }
        foreach (var uri in known) _changed.Fire(uri);
        _changed.Clear();
    }
}
