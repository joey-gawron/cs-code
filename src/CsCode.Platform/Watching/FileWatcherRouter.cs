using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Platform.Watching;

/// <summary>
/// The process-wide <see cref="IFileWatcher"/>: <c>file:</c> roots go to <see cref="FileWatcher"/>, other schemes to the
/// <see cref="IFileWatcherProvider"/> registered for them. A root of a scheme nobody serves gets an inert watch (nothing to report,
/// nothing to release) instead of an exception, so a consumer that watches every workspace folder (Explorer, settings) never fails for one of them.
/// Providers are resolved lazily on the first non-<c>file:</c> watch (see <see cref="FileSystem.FileSystemRouter"/> for why).
/// </summary>
public sealed class FileWatcherRouter : IFileWatcher
{
    private readonly IFileWatcher _local;
    private readonly Lazy<IReadOnlyDictionary<string, IFileWatcherProvider>> _providers;

    public FileWatcherRouter(IFileWatcher local, Func<IEnumerable<IFileWatcherProvider>>? providers = null)
    {
        _local = local;
        _providers = new(() =>
        {
            var map = new Dictionary<string, IFileWatcherProvider>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in providers?.Invoke() ?? [])
                if (!string.Equals(p.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)) map.TryAdd(p.Scheme, p);
            return map;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IFileWatcher Local => _local;

    public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!root.IsAbsoluteUri || root.IsFile) return _local.Watch(root, recursive, excludeGlobs, onChanges);
        return _providers.Value.TryGetValue(root.Scheme, out var p) ? p.Watch(root, recursive, excludeGlobs, onChanges) : Disposable.Empty;
    }
}
