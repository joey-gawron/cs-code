using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.Server;

/// <summary>
/// The server's <see cref="ILanguageFeatureRegistry"/>: the Core registry plus a numbered record of every registration, so each provider the language
/// servers register here can be mirrored by one bridge provider on the client (same feature, same selector, same trigger characters/legend) and a
/// client call can name exactly the provider it targets.
/// </summary>
internal sealed class MirroringFeatureRegistry : ILanguageFeatureRegistry
{
    public sealed record Registration(long Id, Type Feature, DocumentSelector Selector, ILanguageProvider Provider, string? Owner);

    private readonly LanguageFeatureRegistry _inner = new();
    private readonly object _gate = new();
    private readonly Dictionary<long, Registration> _byId = new();
    private readonly Emitter<Registration> _added = new();
    private readonly Emitter<Registration> _removed = new();
    private long _next;

    public IEventSource<Type> ProvidersChanged => _inner.ProvidersChanged;
    public IEventSource<Registration> Added => _added;
    public IEventSource<Registration> Removed => _removed;

    public IReadOnlyList<Registration> Registrations { get { lock (_gate) return _byId.Values.OrderBy(r => r.Id).ToArray(); } }

    public Registration? Get(long id) { lock (_gate) return _byId.GetValueOrDefault(id); }

    public IDisposable Register<TProvider>(DocumentSelector selector, TProvider provider, string? ownerId = null) where TProvider : class, ILanguageProvider
    {
        var token = _inner.Register(selector, provider, ownerId);
        // Same feature rule as the Core registry: an interface type argument serves that feature; a class serves every feature interface it implements.
        var features = typeof(TProvider).IsInterface
            ? [typeof(TProvider)]
            : provider.GetType().GetInterfaces().Where(i => i != typeof(ILanguageProvider) && typeof(ILanguageProvider).IsAssignableFrom(i)).ToArray();
        var regs = new List<Registration>(features.Length);
        lock (_gate)
            foreach (var f in features)
            {
                var r = new Registration(++_next, f, selector, provider, ownerId);
                _byId[r.Id] = r;
                regs.Add(r);
            }
        foreach (var r in regs) _added.Fire(r);
        return Disposable.Create(() =>
        {
            token.Dispose();
            var gone = new List<Registration>(regs.Count);
            lock (_gate)
                foreach (var r in regs)
                    if (_byId.Remove(r.Id)) gone.Add(r);
            foreach (var r in gone) _removed.Fire(r);
        });
    }

    public IReadOnlyList<TProvider> GetProviders<TProvider>(ITextDocument document) where TProvider : class, ILanguageProvider => _inner.GetProviders<TProvider>(document);
    public IReadOnlyList<TProvider> GetProviders<TProvider>(Uri uri, string languageId) where TProvider : class, ILanguageProvider => _inner.GetProviders<TProvider>(uri, languageId);
    public IReadOnlyList<TProvider> GetAll<TProvider>() where TProvider : class, ILanguageProvider => _inner.GetAll<TProvider>();
}
