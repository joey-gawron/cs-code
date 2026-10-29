using System.Collections.Concurrent;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Platform.Context;

/// <summary>
/// Context keys + when-clause evaluation. <see cref="Evaluate"/>, <see cref="Get"/> run on every keystroke from the UI thread, so the
/// read path is lock-free and allocation-free: values live in a <see cref="ConcurrentDictionary{TKey,TValue}"/>, clauses are parsed once
/// into an AST kept in a bounded cache, and comparison works on an unboxed value struct.
/// <para>
/// <see cref="CreateScope"/> returns a child that overlays (never mutates) its parent. Parents reach children only through WEAK references,
/// so a scope that is simply dropped (a closed editor's context) is collectable even if nobody called <see cref="Dispose"/>.
/// </para>
/// <para><c>config.&lt;setting&gt;</c> keys read live from <see cref="IConfigurationService"/> (when supplied); setting changes surface as
/// <c>config.*</c> entries on <see cref="Changed"/>.</para>
/// </summary>
public sealed class ContextKeyService : IContextKeyService, IDisposable, IContextReader
{
    private const int MaxCachedClauses = 2048;

    private readonly ContextKeyService? _parent;
    private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly Emitter<IReadOnlyCollection<string>> _changed = new();
    private readonly object _childGate = new();
    private readonly List<WeakReference<ContextKeyService>> _children = new();
    private readonly ConcurrentDictionary<string, WhenExpr> _clauses;      // shared by the whole scope tree
    private readonly IConfigurationService? _config;
    private readonly ILogger _log;
    private readonly DisposableStore _subscriptions = new();
    private bool _disposed;

    public ContextKeyService(IConfigurationService? configuration = null, ILogger<ContextKeyService>? logger = null)
    {
        _config = configuration;
        _log = logger ?? NullLogger<ContextKeyService>.Instance;
        _clauses = new ConcurrentDictionary<string, WhenExpr>(StringComparer.Ordinal);
        if (configuration is not null)
            _subscriptions.Add(configuration.Changed.Subscribe(e =>
                Publish(e.AffectedKeys.Select(k => "config." + k).ToArray(), fromParent: false)));
    }

    private ContextKeyService(ContextKeyService parent)
    {
        _parent = parent;
        _config = parent._config;
        _log = parent._log;
        _clauses = parent._clauses;
    }

    public IEventSource<IReadOnlyCollection<string>> Changed => _changed;

    // ---- values -------------------------------------------------------------------------------------------------------------------

    public void Set(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (SetCore(key, value)) Publish([key], fromParent: false);
    }

    /// <summary>Sets several keys and raises ONE change event listing exactly the keys whose value actually changed.</summary>
    public void SetMany(IEnumerable<KeyValuePair<string, object?>> values)
    {
        List<string>? changed = null;
        foreach (var (key, value) in values)
            if (SetCore(key, value)) (changed ??= new()).Add(key);
        if (changed is not null) Publish(changed.ToArray(), fromParent: false);
    }

    /// <summary>Removes a key from this scope (a child then sees the parent's value again).</summary>
    public void Reset(string key)
    {
        if (!_values.TryRemove(key, out var old)) return;
        var before = old;
        var after = _parent?.Get(key);
        if (!Equals(before, after)) Publish([key], fromParent: false);
    }

    private bool SetCore(string key, object? value)
    {
        value = Normalize(value);
        var inherited = _parent is null ? null : _parent.Get(key);
        if (_parent is null)
        {
            // Root scope: null means "unset" (keeps the table from filling up with dead keys).
            if (value is null) return _values.TryRemove(key, out _);
            var changed = true;
            _values.AddOrUpdate(key, value, (_, old) => { changed = !Equals(old, value); return value; });
            return changed;
        }

        // Child scope: an explicit null SHADOWS the parent's value, so it must be stored.
        var had = _values.TryGetValue(key, out var previous);
        var effectiveBefore = had ? previous : inherited;
        _values[key] = value;
        return !Equals(effectiveBefore, value);
    }

    // Enums/chars become strings once, at Set time, so that Evaluate never has to convert (allocate) per keystroke.
    private static object? Normalize(object? v) => v switch
    {
        Enum e => e.ToString(),
        char c => c.ToString(),
        _ => v,
    };

    public object? Get(string key)
    {
        for (var s = this; s is not null; s = s._parent)
            if (s._values.TryGetValue(key, out var v)) return v;
        return null;
    }

    CtxValue IContextReader.Get(string key) => CtxValue.From(Get(key));

    CtxValue IContextReader.GetConfig(string settingKey) =>
        _config?.GetRaw(settingKey) is { } e ? CtxValue.FromJson(e) : CtxValue.Null;

    // ---- clauses ------------------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public bool Evaluate(string? whenClause)
    {
        if (string.IsNullOrWhiteSpace(whenClause)) return true;
        if (!_clauses.TryGetValue(whenClause, out var expr)) expr = ParseAndCache(whenClause);
        return expr.Evaluate(this);
    }

    private WhenExpr ParseAndCache(string clause)
    {
        var expr = WhenParser.Parse(clause);
        if (expr is ErrorNode err) _log.LogWarning("Invalid when-clause '{Clause}': {Error}. The clause evaluates to false.", clause, err.Message);
        // Crude but bounded and lock-free: real applications use a few hundred distinct clauses; a flood of unique ones just resets the cache.
        if (_clauses.Count >= MaxCachedClauses) _clauses.Clear();
        _clauses[clause] = expr;
        return expr;
    }

    /// <summary>Number of parsed clauses currently cached (diagnostics/tests).</summary>
    public int CachedClauseCount => _clauses.Count;

    // ---- scopes & events ----------------------------------------------------------------------------------------------------------

    public IContextKeyService CreateScope()
    {
        var child = new ContextKeyService(this);
        lock (_childGate)
        {
            _children.RemoveAll(static w => !w.TryGetTarget(out _));
            _children.Add(new WeakReference<ContextKeyService>(child));
        }
        return child;
    }

    private void Publish(IReadOnlyCollection<string> keys, bool fromParent)
    {
        if (_disposed || keys.Count == 0) return;

        if (fromParent)
        {
            // Keys this scope shadows are unchanged from its point of view.
            var visible = keys.Where(k => !_values.ContainsKey(k)).ToArray();
            if (visible.Length == 0) return;
            keys = visible;
        }

        _changed.Fire(keys);

        ContextKeyService[] live;
        lock (_childGate)
        {
            if (_children.Count == 0) return;
            _children.RemoveAll(static w => !w.TryGetTarget(out _));
            live = _children.Select(static w => w.TryGetTarget(out var c) ? c : null).OfType<ContextKeyService>().ToArray();
        }
        foreach (var child in live) child.Publish(keys, fromParent: true);
    }

    /// <summary>Detaches a scope from its parent and drops its subscribers. Root: also unsubscribes from the configuration service.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _changed.Clear();
        _values.Clear();
        if (_parent is not null)
        {
            lock (_parent._childGate)
                _parent._children.RemoveAll(w => !w.TryGetTarget(out var c) || ReferenceEquals(c, this));
        }
        _subscriptions.Dispose();
    }
}
