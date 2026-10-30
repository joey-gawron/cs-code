namespace CsCode.Platform.FileSystem;

/// <summary>
/// Compares local paths the way the volume holding them does (files-8): Windows folds case, Linux does not, and macOS asks the volume
/// (<see cref="FileSystemNative.IsCaseSensitive"/>), since APFS and HFS+ can be formatted either way. The volume is asked only about two paths
/// that differ in nothing but case, so the usual lookup costs no system call; the hash folds case, which every such pair shares.
/// </summary>
internal sealed class VolumePathComparer(Func<string, bool> isCaseSensitive) : IEqualityComparer<string>
{
    /// <summary>The comparer for this OS's local paths.</summary>
    public static readonly IEqualityComparer<string> Current =
        OperatingSystem.IsMacOS() ? new VolumePathComparer(FileSystemNative.IsCaseSensitive)
        : OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public bool Equals(string? x, string? y) =>
        string.Equals(x, y, StringComparison.Ordinal)
        || (x is not null && y is not null && string.Equals(x, y, StringComparison.OrdinalIgnoreCase) && !isCaseSensitive(x));

    public int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj);
}
