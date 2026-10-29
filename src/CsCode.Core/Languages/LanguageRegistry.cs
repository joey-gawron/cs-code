using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.Core.Languages;

/// <summary>
/// The single place where language-feature providers meet their consumers. Register returns a token; disposing it
/// removes the provider (e.g. language server stopped or restarted). Consumers ask for the providers that
/// apply to a document, best selector score first; they decide whether to use the first, merge all, or race them.
/// </summary>
public interface ILanguageFeatureRegistry
{
    IDisposable Register<TProvider>(DocumentSelector selector, TProvider provider, string? ownerId = null) where TProvider : class, ILanguageProvider;

    /// <summary>Providers of type <typeparamref name="TProvider"/> whose selector matches, ordered best-first (stable).</summary>
    IReadOnlyList<TProvider> GetProviders<TProvider>(ITextDocument document) where TProvider : class, ILanguageProvider;

    IReadOnlyList<TProvider> GetProviders<TProvider>(Uri uri, string languageId) where TProvider : class, ILanguageProvider;

    /// <summary>All providers of a type regardless of document (workspace symbols etc.).</summary>
    IReadOnlyList<TProvider> GetAll<TProvider>() where TProvider : class, ILanguageProvider;

    /// <summary>Fires (with the provider interface type) when providers of that type are added/removed.</summary>
    IEventSource<Type> ProvidersChanged { get; }
}

public sealed class LanguageFeatureRegistry : ILanguageFeatureRegistry
{
    // Types = the feature interfaces this registration serves.
    private sealed record Entry(Type[] Types, DocumentSelector Selector, ILanguageProvider Provider, string? OwnerId, long Seq);

    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private readonly Emitter<Type> _changed = new();
    private long _seq;

    public IEventSource<Type> ProvidersChanged => _changed;

    /// <summary>
    /// If <typeparamref name="TProvider"/> is a feature interface (<c>Register&lt;IHoverProvider&gt;(...)</c>) the provider
    /// serves exactly that feature. If it is inferred as a concrete class (<c>Register(sel, new MyProvider())</c>), the provider
    /// serves every feature interface the class implements. (Register per-feature explicitly when a class implements more
    /// interfaces than the backing server actually supports.)
    /// </summary>
    public IDisposable Register<TProvider>(DocumentSelector selector, TProvider provider, string? ownerId = null)
        where TProvider : class, ILanguageProvider
    {
        var types = typeof(TProvider).IsInterface
            ? [typeof(TProvider)]
            : provider.GetType().GetInterfaces()
                .Where(i => i != typeof(ILanguageProvider) && typeof(ILanguageProvider).IsAssignableFrom(i))
                .ToArray();
        if (types.Length == 0)
            throw new ArgumentException($"{provider.GetType().Name} implements no language feature interface.", nameof(provider));

        var entry = new Entry(types, selector, provider, ownerId, Interlocked.Increment(ref _seq));
        lock (_gate) _entries.Add(entry);
        foreach (var t in types) _changed.Fire(t);
        return Disposable.Create(() =>
        {
            bool removed;
            lock (_gate) removed = _entries.Remove(entry);
            if (removed) foreach (var t in types) _changed.Fire(t);
        });
    }

    public IReadOnlyList<TProvider> GetProviders<TProvider>(ITextDocument document) where TProvider : class, ILanguageProvider =>
        GetProviders<TProvider>(document.Uri, document.LanguageId);

    public IReadOnlyList<TProvider> GetProviders<TProvider>(Uri uri, string languageId) where TProvider : class, ILanguageProvider
    {
        Entry[] snapshot;
        lock (_gate) snapshot = _entries.Where(e => Array.IndexOf(e.Types, typeof(TProvider)) >= 0).ToArray();
        return snapshot
            .Select(e => (e, score: e.Selector.Score(uri, languageId)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenByDescending(x => x.e.Seq)
            .Select(x => (TProvider)x.e.Provider)
            .ToArray();
    }

    public IReadOnlyList<TProvider> GetAll<TProvider>() where TProvider : class, ILanguageProvider
    {
        lock (_gate) return _entries.Where(e => Array.IndexOf(e.Types, typeof(TProvider)) >= 0).Select(e => (TProvider)e.Provider).ToArray();
    }
}

/// <summary>
/// Shared diagnostics store keyed by (document, owner). Language servers and other producers publish
/// here; the editor draws squiggles and the Error List binds to <see cref="GetAll"/>. Owner = server/producer id.
/// </summary>
public interface IDiagnosticService
{
    void Set(Uri uri, string owner, IReadOnlyList<Diagnostic> diagnostics, int? documentVersion = null);
    void Clear(string owner);
    void Clear(Uri uri, string owner);
    IReadOnlyList<Diagnostic> Get(Uri uri);
    IReadOnlyList<(Uri Uri, Diagnostic Diagnostic)> GetAll();
    (int Errors, int Warnings, int Infos, int Hints) Counts { get; }
    IEventSource<IReadOnlyCollection<Uri>> Changed { get; }
}

public sealed class DiagnosticService : IDiagnosticService
{
    private readonly object _gate = new();
    // uri -> owner -> diagnostics
    private readonly Dictionary<Uri, Dictionary<string, IReadOnlyList<Diagnostic>>> _store = new();
    private readonly Emitter<IReadOnlyCollection<Uri>> _changed = new();

    public IEventSource<IReadOnlyCollection<Uri>> Changed => _changed;

    public void Set(Uri uri, string owner, IReadOnlyList<Diagnostic> diagnostics, int? documentVersion = null)
    {
        lock (_gate)
        {
            if (diagnostics.Count == 0)
            {
                if (!_store.TryGetValue(uri, out var existing) || !existing.Remove(owner)) return;
                if (existing.Count == 0) _store.Remove(uri);
            }
            else
            {
                if (!_store.TryGetValue(uri, out var owners)) _store[uri] = owners = new();
                owners[owner] = diagnostics;
            }
        }
        _changed.Fire([uri]);
    }

    public void Clear(string owner)
    {
        List<Uri> touched = new();
        lock (_gate)
        {
            foreach (var (uri, owners) in _store.ToArray())
            {
                if (!owners.Remove(owner)) continue;
                touched.Add(uri);
                if (owners.Count == 0) _store.Remove(uri);
            }
        }
        if (touched.Count > 0) _changed.Fire(touched);
    }

    public void Clear(Uri uri, string owner) => Set(uri, owner, []);

    public IReadOnlyList<Diagnostic> Get(Uri uri)
    {
        lock (_gate)
            return _store.TryGetValue(uri, out var owners) ? owners.Values.SelectMany(v => v).ToArray() : [];
    }

    public IReadOnlyList<(Uri Uri, Diagnostic Diagnostic)> GetAll()
    {
        lock (_gate)
            return _store.SelectMany(kv => kv.Value.Values.SelectMany(v => v).Select(d => (kv.Key, d))).ToArray();
    }

    public (int Errors, int Warnings, int Infos, int Hints) Counts
    {
        get
        {
            int e = 0, w = 0, i = 0, h = 0;
            lock (_gate)
                foreach (var d in _store.Values.SelectMany(o => o.Values).SelectMany(v => v))
                    switch (d.Severity)
                    {
                        case DiagnosticSeverity.Error: e++; break;
                        case DiagnosticSeverity.Warning: w++; break;
                        case DiagnosticSeverity.Information: i++; break;
                        default: h++; break;
                    }
            return (e, w, i, h);
        }
    }
}
