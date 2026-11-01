using System.Diagnostics;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Platform.FileSystem;
using CsCode.Platform.Io;
using CsCode.Platform.Watching;
using CsCode.TestDoubles;

namespace CsCode.Panels.Git.Tests;

/// <summary>A real git repository in a temp directory (under /var on macOS, a link to /private/var: git answers with the realpath).</summary>
public sealed class TestRepo : IDisposable
{
    private readonly TempDirectory _dir;

    public TestRepo(bool init = true, string? path = null)
    {
        _dir = new TempDirectory("cscode-git-");
        Root = path ?? _dir.Path;
        Directory.CreateDirectory(Root);
        if (!init) return;
        Git("init", "-q", "-b", "main");
        Configure(Root);
    }

    public static void Configure(string root)
    {
        RunGit(root, "config", "user.name", "Test User");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "commit.gpgsign", "false");
        RunGit(root, "config", "core.autocrlf", "false");
    }

    public string Root { get; }
    public Uri Uri => new(Root.TrimEnd('/') + "/");
    public string Dir => _dir.Path;

    public string Path(string rel) => System.IO.Path.Combine(Root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
    public Uri FileUri(string rel) => new(Path(rel));

    public void Write(string rel, string content)
    {
        var full = Path(rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string Read(string rel) => File.ReadAllText(Path(rel));

    public string Git(params string[] args) => RunGit(Root, args);

    public void Commit(string message = "commit")
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
    }

    public static string RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output;
    }

    public static bool GitAvailable
    {
        get
        {
            try { RunGit(System.IO.Path.GetTempPath(), "--version"); return true; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }
    }

    public void Dispose() => _dir.Dispose();
}

/// <summary>An <see cref="IWorkspaceService"/> over the real Platform file system and watcher (or none), with the folders a test gives it.</summary>
public sealed class TestWorkspace : IWorkspaceService, IDisposable
{
    private readonly Emitter<WorkspaceFoldersChangedEventArgs> _changed = new();
    private readonly IoLane _lane = new("test-git-io", 1);
    private readonly FileWatcher? _watcher;
    private List<Uri> _folders;

    public TestWorkspace(bool realWatcher, params Uri[] folders)
    {
        _folders = [.. folders];
        FileSystem = new LocalFileSystem(_lane);
        if (realWatcher) _watcher = new FileWatcher(_lane);
        Watcher = (IFileWatcher?)_watcher ?? new NullWatcher();
    }

    public IReadOnlyList<Uri> Folders => _folders;
    public string? Name => "test";
    public IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged => _changed;
    public IFileSystem FileSystem { get; }
    public IFileWatcher Watcher { get; }
    public IReadOnlyList<Uri> RecentFolders => [];

    public void SetFolders(params Uri[] folders)
    {
        var removed = _folders;
        _folders = [.. folders];
        _changed.Fire(new WorkspaceFoldersChangedEventArgs(folders, removed));
    }

    public ValueTask OpenFolderAsync(Uri folder, CancellationToken ct = default) { SetFolders(folder); return ValueTask.CompletedTask; }
    public ValueTask CloseFolderAsync(CancellationToken ct = default) { SetFolders(); return ValueTask.CompletedTask; }
    public async IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _lane.Dispose();
    }

    private sealed class NullWatcher : IFileWatcher
    {
        public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges) => Disposable.Empty;
    }
}

public static class Wait
{
    /// <summary>Polls <paramref name="condition"/> until it holds or <paramref name="timeout"/> passes (default 10 s).</summary>
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? because = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(because ?? "condition not met in time");
            await Task.Delay(20);
        }
    }
}
