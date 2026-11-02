using System.Text;

namespace CsCode.Testing.Engine;

/// <summary>What the providers share: the process runner, settings, a scratch directory per run.</summary>
public sealed class TestProviderContext(IProcessRunner processes, Func<string, string?>? setting = null)
{
    public IProcessRunner Processes => processes;

    /// <summary>A string setting (null when unset).</summary>
    public string? Setting(string key) => setting?.Invoke(key);

    /// <summary>A list-valued setting (<c>python.testing.pytestArgs</c>), or null when unset. Additive (language stage 1).</summary>
    public Func<string, IReadOnlyList<string>?>? ListSetting { get; init; }

    /// <summary>
    /// The Python interpreter selected for a folder (Python: Select Interpreter), or null when none is known; the Python provider then uses
    /// <c>python3</c> from PATH. Additive (language stage 1).
    /// </summary>
    public Func<string, CancellationToken, Task<string?>>? PythonInterpreter { get; init; }

    /// <summary>A fresh directory under the temp directory for one run's result files; the caller deletes it (<see cref="DeleteQuietly"/>).</summary>
    public static string NewTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cscode-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void DeleteQuietly(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Reads a file, or null when it does not exist or cannot be read.</summary>
    public static async Task<string?> TryReadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > TestLimits.MaxScannedFileBytes * 8) return null;
            return await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>Walks a folder for project and source files, skipping build output, dependencies and VCS directories.</summary>
public static class SourceFiles
{
    /// <summary>Directory names never descended into.</summary>
    public static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", "target", ".vs", ".vscode", ".cscode", ".idea", "dist", "build", "coverage", "out",
        ".scratch", ".claude", ".next", ".turbo", "packages", "TestResults",
    };

    /// <summary>
    /// Files under <paramref name="root"/> accepted by <paramref name="match"/> (by name), depth-first in name order, at most
    /// <paramref name="max"/>; <paramref name="stopAt"/> (a directory other than the root holding such a file, e.g. a nested project) is not entered.
    /// Never follows directory links.
    /// </summary>
    public static List<string> Find(string root, Func<string, bool> match, int max = 20_000, Func<string, bool>? stopAt = null, CancellationToken ct = default)
    {
        var result = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && result.Count < max)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(dir).GetFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
            Array.Sort(entries, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            if (dir != root && stopAt is not null && entries.Any(e => e is FileInfo && stopAt(e.Name))) continue;
            var subdirs = new List<string>();
            foreach (var e in entries)
            {
                if (e is DirectoryInfo d)
                {
                    if (d.LinkTarget is not null || SkippedDirectories.Contains(d.Name) || d.Name.StartsWith('.')) continue;
                    subdirs.Add(d.FullName);
                }
                else if (match(e.Name))
                {
                    result.Add(e.FullName);
                    if (result.Count >= max) break;
                }
            }
            for (var k = subdirs.Count - 1; k >= 0; k--) stack.Push(subdirs[k]);
        }
        return result;
    }
}
