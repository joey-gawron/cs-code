namespace CsCode.Core.Platform;

// Additive contracts (added by the Remote module) that let a module serve a URI scheme other than `file:`. Platform's routing
// IFileSystem / IFileWatcher dispatch by URI scheme to these; `file:` is always Platform's own local implementation.

/// <summary>A file system for one URI scheme (e.g. <c>ssh</c> for remote workspaces). Same contract and exceptions as <see cref="IFileSystem"/>.</summary>
public interface IFileSystemProvider : IFileSystem
{
    /// <summary>The URI scheme served, lower case (never <c>file</c>).</summary>
    string Scheme { get; }
}

/// <summary>A watcher for one URI scheme. Same contract as <see cref="IFileWatcher"/> (batched, debounced, disposable, a missing root is not an error).</summary>
public interface IFileWatcherProvider : IFileWatcher
{
    string Scheme { get; }
}

/// <summary>Options of <see cref="IFileSearchProvider.FindFilesAsync"/>, mirroring what the local workspace walk honours.</summary>
/// <param name="IncludeGlob">VS Code glob matched against the folder-relative path; null = every file.</param>
/// <param name="ExcludeGlobs">files.exclude patterns that are switched on.</param>
/// <param name="MaxResults">≤ 0 = unlimited.</param>
public sealed record FileSearchOptions(string? IncludeGlob, IReadOnlyList<string> ExcludeGlobs, bool UseIgnoreFiles = true, bool FollowSymlinks = true, int MaxResults = 0);

/// <summary>
/// Streams the files below a workspace folder of one URI scheme (quick open, Find in Files enumeration). <see cref="IWorkspaceService.FindFilesAsync"/>
/// uses it for folders whose scheme is not <c>file</c>; a remote implementation runs the walk next to the files.
/// </summary>
public interface IFileSearchProvider
{
    string Scheme { get; }
    IAsyncEnumerable<Uri> FindFilesAsync(Uri folder, FileSearchOptions options, CancellationToken ct = default);
}
