using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.Rust;

/// <summary>
/// Maps a libtest path (<c>tests::nested::deep</c>) of a cargo target to the file and line of its <c>#[test] fn</c>, starting at the target's
/// crate root (<c>src/lib.rs</c>, <c>tests/integ.rs</c>) and following Rust's module rules: an inline <c>mod x { }</c> stays in the file, an
/// out-of-line <c>mod x;</c> is <c>x.rs</c> or <c>x/mod.rs</c> next to a crate root or a <c>mod.rs</c>, and under the directory named after any
/// other file (<c>src/a.rs</c>'s <c>mod b;</c> is <c>src/a/b.rs</c>). <c>#[path]</c> attributes are not followed. Parsed files are cached per
/// locator (one per discovery).
/// </summary>
public sealed class RustTestLocator(Func<string, Task<string?>> readFile)
{
    private readonly Dictionary<string, RustTestScanner.Result?> _scans = new(StringComparer.Ordinal);

    public async Task<TestLocation?> LocateAsync(string crateRoot, string testPath, CancellationToken ct)
    {
        var segments = testPath.Split("::");
        var file = crateRoot;
        var dir = Path.GetDirectoryName(crateRoot) ?? "";
        var inFile = new List<string>(); // inline module path inside the current file
        for (var k = 0; k < segments.Length; k++)
        {
            ct.ThrowIfCancellationRequested();
            var scan = await ScanAsync(file).ConfigureAwait(false);
            if (scan is null) return null;
            var prefix = string.Join("::", inFile);
            if (k == segments.Length - 1)
            {
                var full = prefix.Length > 0 ? prefix + "::" + segments[k] : segments[k];
                var fn = scan.Tests.FirstOrDefault(t => t.Path == full);
                return fn is null ? null : new TestLocation(file, fn.Line);
            }
            var modPath = prefix.Length > 0 ? prefix + "::" + segments[k] : segments[k];
            var module = scan.Modules.FirstOrDefault(m => m.Path == modPath);
            if (module is { Inline: true })
            {
                inFile.Add(segments[k]);
                continue;
            }
            // out of line: relative to this file's module directory, plus the inline modules it is nested in
            var baseDir = Path.Combine([dir, .. inFile]);
            var candidates = new[] { Path.Combine(baseDir, segments[k] + ".rs"), Path.Combine(baseDir, segments[k], "mod.rs") };
            string? next = null;
            foreach (var cand in candidates)
                if (await ScanAsync(cand).ConfigureAwait(false) is not null) { next = cand; break; }
            if (next is null) return null;
            file = next;
            dir = Path.GetFileName(next) == "mod.rs" ? Path.GetDirectoryName(next)! : Path.Combine(Path.GetDirectoryName(next)!, Path.GetFileNameWithoutExtension(next));
            inFile.Clear();
        }
        return null;
    }

    private async Task<RustTestScanner.Result?> ScanAsync(string file)
    {
        if (_scans.TryGetValue(file, out var cached)) return cached;
        var text = await readFile(file).ConfigureAwait(false);
        var result = text is null ? null : RustTestScanner.Scan(text);
        _scans[file] = result;
        return result;
    }
}
