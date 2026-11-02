using System.Text.RegularExpressions;

namespace CsCode.Testing.Engine;

/// <summary>Finds where a failure happened in a stack trace or panic message: the first frame in a file under the project.</summary>
public static partial class StackTraces
{
    /// <summary>
    /// The first frame of a .NET stack trace (<c>at X in /path/File.cs:line 12</c>) whose file is under <paramref name="projectDirectory"/>
    /// (frames of the test framework's own sources, <c>/_/src/…</c>, are skipped); else the first frame with a file at all; else null.
    /// </summary>
    public static TestLocation? FromDotNet(string? stackTrace, string projectDirectory)
    {
        if (string.IsNullOrEmpty(stackTrace)) return null;
        TestLocation? first = null;
        string? physical = null;
        foreach (Match m in DotNetFrame().Matches(stackTrace))
        {
            var file = m.Groups["file"].Value.Trim();
            if (!int.TryParse(m.Groups["line"].Value, out var line)) continue;
            var loc = new TestLocation(file, Math.Max(0, line - 1));
            if (IsUnder(file, projectDirectory)) return loc;
            physical ??= PhysicalPaths.Resolve(projectDirectory);
            if (physical != projectDirectory && IsUnder(file, physical)) return loc with { FilePath = PhysicalPaths.UnderLogical(file, projectDirectory) };
            if (!file.StartsWith("/_/", StringComparison.Ordinal)) first ??= loc;
        }
        return first;
    }

    /// <summary>A Rust panic's place (<c>panicked at src/lib.rs:9:43:</c>), relative to the package directory.</summary>
    public static TestLocation? FromRustPanic(string? text, string packageDirectory)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = RustPanic().Match(text);
        if (!m.Success || !int.TryParse(m.Groups["line"].Value, out var line)) return null;
        var file = m.Groups["file"].Value;
        if (!Path.IsPathRooted(file)) file = Path.GetFullPath(Path.Combine(packageDirectory, file));
        return new TestLocation(file, Math.Max(0, line - 1));
    }

    /// <summary>The first frame of a JavaScript stack (<c>at fn (/path/a.test.js:12:5)</c>) in a file under the package, outside node_modules.</summary>
    public static TestLocation? FromJavaScript(string? text, string packageDirectory)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (Match m in JsFrame().Matches(text))
        {
            var file = m.Groups["file"].Value;
            if (file.StartsWith("file://", StringComparison.Ordinal) && Uri.TryCreate(file, UriKind.Absolute, out var u)) file = u.LocalPath;
            if (!Path.IsPathRooted(file) || file.Contains("/node_modules/", StringComparison.Ordinal) || file.Contains("\\node_modules\\", StringComparison.Ordinal)) continue;
            if (!IsUnder(file, packageDirectory) || !int.TryParse(m.Groups["line"].Value, out var line)) continue;
            return new TestLocation(file, Math.Max(0, line - 1));
        }
        return null;
    }

    internal static bool IsUnder(string file, string directory)
    {
        if (string.IsNullOrEmpty(directory)) return false;
        var dir = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return file.StartsWith(dir, comparison);
    }

    /// <summary>
    /// A Python failure (language stage 1): the LAST frame under <paramref name="rootDirectory"/> of a traceback (<c>File "/p/t.py", line 7, in
    /// test_x</c>, innermost last) or of pytest's report lines (<c>tests/test_x.py:7: AssertionError</c>, relative to pytest's rootdir).
    /// </summary>
    public static TestLocation? FromPython(string? text, string rootDirectory)
    {
        if (string.IsNullOrEmpty(text)) return null;
        TestLocation? last = null;
        foreach (Match m in PythonFrame().Matches(text))
        {
            var file = m.Groups["file"].Success ? m.Groups["file"].Value : m.Groups["short"].Value;
            var lineText = m.Groups["line"].Success ? m.Groups["line"].Value : m.Groups["shortline"].Value;
            if (!int.TryParse(lineText, out var line)) continue;
            if (!Path.IsPathRooted(file)) file = Path.GetFullPath(Path.Combine(rootDirectory, file));
            if (IsUnder(file, rootDirectory)) last = new TestLocation(file, Math.Max(0, line - 1));
        }
        return last;
    }

    [GeneratedRegex(@"File ""(?<file>[^""\n]+\.py)"", line (?<line>\d+)|^(?<short>[^\s:][^:\n]*\.py):(?<shortline>\d+): ", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex PythonFrame();

    [GeneratedRegex(@"\bin (?<file>.+?):line (?<line>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex DotNetFrame();

    [GeneratedRegex(@"panicked at (?<file>[^\s:][^:\n]*):(?<line>\d+):(?<col>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex RustPanic();

    [GeneratedRegex(@"\(?(?<file>(?:file://)?(?:/|[A-Za-z]:\\)[^():\n]+):(?<line>\d+):(?<col>\d+)\)?", RegexOptions.CultureInvariant)]
    private static partial Regex JsFrame();
}
