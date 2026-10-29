using System.Text.Json;
using CsCode.Core.Lifecycle;

namespace CsCode.Core.Extensions;

// Typed `contributes.*` points from VS Code–compatible extension manifests (package.json). Paths are ABSOLUTE
// (resolved against the extension directory by the manifest loader). Consumers never parse package.json themselves.

public sealed record Contributed<T>(T Value, string ExtensionId, string ExtensionDirectory) where T : class;

public interface IContributionSet<T> where T : class
{
    IReadOnlyList<Contributed<T>> Items { get; }
    IEventSource<IReadOnlyList<Contributed<T>>> Changed { get; }
}

public interface IContributionRegistry
{
    IContributionSet<T> Of<T>() where T : class;
    IDisposable Contribute<T>(string extensionId, string extensionDirectory, IEnumerable<T> items) where T : class;
    /// <summary>Removes everything an extension contributed (uninstall/disable).</summary>
    void RemoveExtension(string extensionId);
}

public sealed class ContributionRegistry : IContributionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, object> _sets = new();

    public IContributionSet<T> Of<T>() where T : class => Set<T>();

    public IDisposable Contribute<T>(string extensionId, string extensionDirectory, IEnumerable<T> items) where T : class
    {
        var added = items.Select(v => new Contributed<T>(v, extensionId, extensionDirectory)).ToList();
        var set = Set<T>();
        set.Add(added);
        return Disposable.Create(() => set.Remove(added));
    }

    public void RemoveExtension(string extensionId)
    {
        object[] all;
        lock (_gate) all = _sets.Values.ToArray();
        foreach (var s in all) ((ISet)s).RemoveExtension(extensionId);
    }

    private ContributionSet<T> Set<T>() where T : class
    {
        lock (_gate)
        {
            if (!_sets.TryGetValue(typeof(T), out var s)) _sets[typeof(T)] = s = new ContributionSet<T>();
            return (ContributionSet<T>)s;
        }
    }

    private interface ISet { void RemoveExtension(string extensionId); }

    private sealed class ContributionSet<T> : IContributionSet<T>, ISet where T : class
    {
        private readonly object _gate = new();
        private readonly Emitter<IReadOnlyList<Contributed<T>>> _changed = new();
        private Contributed<T>[] _items = [];

        public IReadOnlyList<Contributed<T>> Items => Volatile.Read(ref _items);
        public IEventSource<IReadOnlyList<Contributed<T>>> Changed => _changed;

        public void Add(IReadOnlyCollection<Contributed<T>> added)
        {
            if (added.Count == 0) return;
            lock (_gate) _items = [.. _items, .. added];
            _changed.Fire(Items);
        }

        public void Remove(IReadOnlyCollection<Contributed<T>> removed)
        {
            bool changed;
            lock (_gate)
            {
                var next = _items.Where(i => !removed.Contains(i)).ToArray();
                changed = next.Length != _items.Length;
                _items = next;
            }
            if (changed) _changed.Fire(Items);
        }

        public void RemoveExtension(string extensionId)
        {
            bool changed;
            lock (_gate)
            {
                var next = _items.Where(i => i.ExtensionId != extensionId).ToArray();
                changed = next.Length != _items.Length;
                _items = next;
            }
            if (changed) _changed.Fire(Items);
        }
    }
}

/// <summary>Built-in contributions use this extension id.</summary>
public static class BuiltinExtension { public const string Id = "cscode.builtin"; }

public sealed record LanguageContribution(
    string Id,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> Filenames,
    IReadOnlyList<string> FilenamePatterns,
    string? FirstLine = null,
    string? ConfigurationPath = null,
    IReadOnlyList<string>? MimeTypes = null);

public sealed record GrammarContribution(
    string? LanguageId,
    string ScopeName,
    string Path,
    IReadOnlyDictionary<string, string>? EmbeddedLanguages = null,
    IReadOnlyDictionary<string, string>? TokenTypes = null,
    IReadOnlyList<string>? InjectTo = null);

public sealed record ColorThemeContribution(string Id, string Label, string UiTheme /* vs | vs-dark | hc-black | hc-light */, string Path);
public sealed record IconThemeContribution(string Id, string Label, string Path);
public sealed record SnippetContribution(string LanguageId, string Path);

public sealed record KeybindingContribution(string Command, string Key, string? Mac = null, string? Linux = null, string? Win = null, string? When = null, JsonElement? Args = null);

public sealed record MenuContribution(string MenuId, string Command, string? Group = null, string? When = null, string? Alt = null);

public sealed record JsonValidationContribution(IReadOnlyList<string> FileMatch, string Url);

/// <summary>`contributes.debuggers[]` (the part the host needs to launch an adapter and offer launch.json snippets).</summary>
public sealed record DebuggerContribution(
    string Type,
    string Label,
    string? Program = null,
    string? Runtime = null,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string>? PlatformPrograms = null,
    IReadOnlyList<string>? Languages = null,
    JsonElement? ConfigurationAttributes = null,
    JsonElement? InitialConfigurations = null,
    JsonElement? ConfigurationSnippets = null,
    JsonElement? Variables = null);

/// <summary>`contributes.breakpoints[]`: languages in which the gutter breakpoint toggle is enabled.</summary>
public sealed record BreakpointLanguageContribution(string Language);

/// <summary>Parsed package.json header for an installed extension.</summary>
public sealed record ExtensionDescription(
    string Id,
    string Publisher,
    string Name,
    string DisplayName,
    string Version,
    string Directory,
    bool IsBuiltin,
    string? Description,
    string? Main,
    string? Browser,
    string? EngineVscode,
    IReadOnlyList<string> ActivationEvents,
    IReadOnlyList<string> ExtensionDependencies,
    IReadOnlyList<string> ExtensionPack,
    string? IconPath,
    string? Repository,
    JsonElement Manifest);

public enum ExtensionHostState { NotStarted, Starting, Running, Crashed, Stopped }

public sealed record InstalledExtension(ExtensionDescription Description, bool Enabled, bool Activated);

public sealed record MarketplaceExtension(string Id, string Publisher, string Name, string DisplayName, string Version, string? Description, string? IconUrl, long? DownloadCount, double? AverageRating, string? DownloadUrl, string? Namespace);

/// <summary>Implemented by CsCode.Extensions. Marketplace = Open VSX (what VSCodium uses). Microsoft's marketplace is NOT used.</summary>
public interface IExtensionService
{
    IReadOnlyList<InstalledExtension> Installed { get; }
    ExtensionHostState HostState { get; }
    IEventSource<IReadOnlyList<InstalledExtension>> InstalledChanged { get; }
    IEventSource<ExtensionHostState> HostStateChanged { get; }

    /// <summary>Discovers manifests, populates <see cref="IContributionRegistry"/>, starts the host lazily on first activation event.</summary>
    ValueTask InitializeAsync(CancellationToken ct = default);
    ValueTask<InstalledExtension> InstallVsixAsync(string vsixPath, CancellationToken ct = default);
    ValueTask<InstalledExtension> InstallFromMarketplaceAsync(string extensionId, string? version = null, IProgress<double>? progress = null, CancellationToken ct = default);
    ValueTask UninstallAsync(string extensionId, CancellationToken ct = default);
    ValueTask SetEnabledAsync(string extensionId, bool enabled, CancellationToken ct = default);
    ValueTask<IReadOnlyList<MarketplaceExtension>> SearchMarketplaceAsync(string query, int offset = 0, int size = 30, CancellationToken ct = default);
    /// <summary>Fire an activation event (e.g. "onLanguage:python", "onCommand:foo", "workspaceContains:**/pom.xml", "*").</summary>
    ValueTask ActivateByEventAsync(string activationEvent, CancellationToken ct = default);
    ValueTask RestartHostAsync(CancellationToken ct = default);
}
