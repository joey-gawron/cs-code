using System.Text.Json;
using CsCode.Core.Platform;

namespace CsCode.Platform.Tests.Support;

/// <summary>A clock tests advance by hand (chord timeouts, debounces that read <see cref="TimeProvider"/>).</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private long _ms;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => Interlocked.Read(ref _ms);
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ms, (long)by.TotalMilliseconds);
}

/// <summary>Dictionary-backed <see cref="IStateStorage"/> for tests of services that merely consume state.</summary>
public sealed class DictionaryStateStorage : IStateStorage
{
    private readonly Dictionary<(StorageScope, string), JsonElement> _values = new();

    public T? Get<T>(string key, StorageScope scope = StorageScope.Application)
    {
        lock (_values) return _values.TryGetValue((scope, key), out var v) ? v.Deserialize<T>() : default;
    }

    public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application)
    {
        lock (_values) _values[(scope, key)] = JsonSerializer.SerializeToElement(value);
    }

    public void Remove(string key, StorageScope scope = StorageScope.Application)
    {
        lock (_values) _values.Remove((scope, key));
    }

    public Task FlushAsync() => Task.CompletedTask;
}
