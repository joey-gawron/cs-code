using CsCode.Core.Debugging;

namespace CsCode.Panels.Debug.Native;

/// <summary>
/// The lines of the source files the Disassembly window interleaves, read off the UI thread: an open document's text, else the file on disk
/// (at most 4 MB). Bounded: the 16 most recent files. One per Disassembly view model (it dies with the window).
/// </summary>
internal sealed class SourceLineCache(DebugPanelServices services)
{
    private const int MaxFiles = 16;
    private const long MaxBytes = 4L * 1024 * 1024;
    private readonly LinkedList<(string Path, string[]? Lines)> _recent = new();

    /// <summary>The cached lines of <paramref name="path"/>, or null (not loaded yet, not readable). UI thread.</summary>
    public string[]? TryGet(string path)
    {
        foreach (var (p, lines) in _recent)
            if (p == path) return lines;
        return null;
    }

    public bool IsKnown(string path) => _recent.Any(e => e.Path == path);

    /// <summary>Loads the files not cached yet (off the UI thread), then caches them. UI thread in, UI thread out.</summary>
    public async Task LoadAsync(IEnumerable<DebugSource> sources, CancellationToken ct)
    {
        var paths = sources.Select(s => s.Path).OfType<string>().Where(p => p.Length > 0 && !IsKnown(p)).Distinct().Take(MaxFiles).ToList();
        if (paths.Count == 0) return;
        foreach (var path in paths)
        {
            var uri = ToUri(path);
            var lines = services.Documents?.Get(uri) is { } doc ? Split(doc.GetText()) : await ReadFileAsync(uri, ct).ConfigureAwait(true);
            _recent.AddFirst((path, lines));
            while (_recent.Count > MaxFiles) _recent.RemoveLast();
        }
    }

    private static Uri ToUri(string path)
    {
        try { return new Uri(path); }
        catch (UriFormatException) { return new Uri("file:///"); }
    }

    /// <summary>The file through the workspace's file system (its own lane: the UI thread only awaits), split into lines off the UI thread.</summary>
    private async Task<string[]?> ReadFileAsync(Uri uri, CancellationToken ct)
    {
        if (!uri.IsFile) return null;
        try
        {
            if (services.Workspace?.FileSystem is not { } fs) return null;
            if (await fs.StatAsync(uri, ct).ConfigureAwait(false) is not { Type: Core.Platform.FileType.File } stat || stat.Size > MaxBytes) return null;
            var bytes = await fs.ReadFileAsync(uri, ct).ConfigureAwait(false);
            return Split(System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException) { return null; }
    }

    private static string[] Split(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
}
