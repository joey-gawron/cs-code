namespace CsCode.TestDoubles;

/// <summary>Unique temp directory deleted on dispose (best effort).</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory(string? prefix = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), (prefix ?? "cscode-test-") + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public Uri Uri => new(Path.TrimEnd('/') + "/");

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string WriteFile(string relativePath, string content)
    {
        var full = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>
    /// <paramref name="path"/> with every symbolic link among its directories resolved (realpath): what a shell started there reports as its
    /// directory (macOS's temp directory is under /var, a link to /private/var).
    /// </summary>
    public static string PhysicalPath(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var result = System.IO.Path.GetPathRoot(full)!;
        foreach (var part in full[result.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            result = System.IO.Path.Combine(result, part);
            if (new DirectoryInfo(result).ResolveLinkTarget(returnFinalTarget: true) is { } target) result = target.FullName;
        }
        return result;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
