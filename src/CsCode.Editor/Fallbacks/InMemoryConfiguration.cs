using System.Text.Json;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Editor.Fallbacks;

/// <summary>
/// Non-persistent <see cref="IConfigurationService"/> used only when the Platform module is absent: defaults registered via
/// <see cref="RegisterProperties"/> plus values set through <see cref="UpdateAsync"/>. Dotted keys and nested objects are NOT merged
/// here (the real service does that); the editor only ever asks for leaf keys and "[language]" sections.
/// </summary>
internal sealed class InMemoryConfiguration : IConfigurationService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonElement> _values = new();
    private readonly List<ConfigurationProperty> _properties = new();
    private readonly Emitter<ConfigurationChangedEventArgs> _changed = new();

    public string UserSettingsPath => Path.Combine(Path.GetTempPath(), "cscode-editor-fallback-settings.json");
    public IReadOnlyList<ConfigurationProperty> Properties { get { lock (_gate) return _properties.ToArray(); } }
    public IEventSource<ConfigurationChangedEventArgs> Changed => _changed;

    public T Get<T>(string key, T defaultValue) => Get<T>(key) is { } v ? v : defaultValue;

    public T? Get<T>(string key)
    {
        var raw = GetRaw(key);
        if (raw is null) return default;
        try
        {
            return raw.Value.Deserialize<T>(CoreJson.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public JsonElement? GetRaw(string key)
    {
        lock (_gate)
        {
            if (_values.TryGetValue(key, out var v)) return v;
            return _properties.FirstOrDefault(p => p.Key == key)?.Default;
        }
    }

    public ValueTask UpdateAsync(string key, object? value, ConfigurationTarget target = ConfigurationTarget.User, CancellationToken ct = default)
    {
        lock (_gate) _values[key] = JsonSerializer.SerializeToElement(value);
        _changed.Fire(new ConfigurationChangedEventArgs([key]));
        return ValueTask.CompletedTask;
    }

    public IDisposable RegisterProperties(IEnumerable<ConfigurationProperty> properties)
    {
        var list = properties.ToList();
        lock (_gate) _properties.AddRange(list);
        return Disposable.Create(() => { lock (_gate) foreach (var p in list) _properties.Remove(p); });
    }
}
