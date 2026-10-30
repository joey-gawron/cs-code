using System.Reflection;
using CsCode.Core.Lifecycle;

namespace CsCode.Profiling.Testing;

/// <summary>Something that can report how many subscribers it has (an <see cref="Emitter{T}"/> through <see cref="EmitterProbe.For{T}"/>).</summary>
public interface IEmitterProbe
{
    string Name { get; }
    int SubscriberCount { get; }
}

public static class EmitterProbe
{
    public static IEmitterProbe For<T>(Emitter<T> emitter, string name) => new Probe(name, () => emitter.SubscriberCount);

    public static IEmitterProbe For(string name, Func<int> count) => new Probe(name, count);

    private sealed record Probe(string Name, Func<int> Count) : IEmitterProbe
    {
        public int SubscriberCount => Count();
    }
}

/// <summary>
/// Event-subscription audit (MUST 4): takes the subscriber counts of a set of <see cref="Emitter{T}"/>s before a scenario and asserts they are back
/// afterwards — a subscription a closed editor forgot to dispose shows up here even when the subscriber object itself is small. Services keep their
/// emitters private, so <see cref="Scan"/> finds them by reflection (every instance field of type <c>Emitter&lt;T&gt;</c>, also in base classes):
/// no module has to expose anything for the audit. Counts are read through the emitter's public <c>SubscriberCount</c>.
/// </summary>
public sealed class SubscriptionAudit
{
    private readonly List<IEmitterProbe> _probes = new();

    public IReadOnlyList<IEmitterProbe> Probes => _probes;

    public SubscriptionAudit Add(IEmitterProbe probe)
    {
        _probes.Add(probe);
        return this;
    }

    /// <summary>Adds a probe for every <c>Emitter&lt;T&gt;</c> field of each object (named <c>Type.field</c>). Nulls are skipped.</summary>
    public SubscriptionAudit Scan(params object?[] services)
    {
        foreach (var service in services)
        {
            if (service is null) continue;
            for (var type = service.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (!field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(Emitter<>)) continue;
                    if (field.GetValue(service) is not { } emitter) continue;
                    var count = field.FieldType.GetProperty(nameof(Emitter<int>.SubscriberCount))!;
                    _probes.Add(EmitterProbe.For($"{type.Name}.{field.Name}", () => (int)count.GetValue(emitter)!));
                }
            }
        }
        return this;
    }

    /// <summary>The current counts by probe name.</summary>
    public IReadOnlyDictionary<string, int> Snapshot()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in _probes) result[p.Name] = p.SubscriberCount;
        return result;
    }

    /// <summary>Throws when any emitter has more than <paramref name="tolerance"/> subscribers more than in <paramref name="baseline"/>.</summary>
    public void AssertNoGrowth(IReadOnlyDictionary<string, int> baseline, int tolerance = 0)
    {
        var grown = new List<string>();
        foreach (var p in _probes)
        {
            var before = baseline.TryGetValue(p.Name, out var b) ? b : 0;
            var now = p.SubscriberCount;
            if (now > before + tolerance) grown.Add($"{p.Name}: {before} -> {now}");
        }
        if (grown.Count > 0) throw new MemoryAssertionException("subscriptions leaked: " + string.Join("; ", grown));
    }
}
