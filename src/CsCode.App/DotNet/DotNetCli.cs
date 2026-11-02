using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CsCode.App.DotNet;

/// <summary>A package a NuGet search found: its id, latest version, every listed version (newest first), description and downloads.</summary>
public sealed record NuGetPackage(string Id, string Version, IReadOnlyList<string> Versions, string? Description, long Downloads);

/// <summary>A package reference of a project (<c>dotnet list package --format json</c>).</summary>
public sealed record PackageReference(string Id, string? Requested, string? Resolved);

/// <summary>A <c>dotnet new</c> template (<c>dotnet new list</c>): its name, the short name to pass, its languages (default first) and tags.</summary>
public sealed record DotNetTemplate(string Name, string ShortName, IReadOnlyList<string> Languages, string Tags);

/// <summary>
/// The .NET command line and NuGet's v3 HTTP API as the shell's .NET commands use them (docs/CSHARP.md, "NuGet and dotnet new"). Every call is
/// asynchronous and does its I/O and JSON off the caller's thread; nothing here touches the UI.
/// <list type="bullet">
/// <item>Package sources are the folder's own (<c>dotnet nuget list source --format short</c>, which reads every nuget.config above it and the
/// user's): <c>E https://…/index.json</c> per enabled source.</item>
/// <item>A source's service index names its <c>SearchQueryService</c>; <c>?q=…&amp;take=20&amp;semVerLevel=2.0.0</c> answers packages with every
/// version. Sources that need credentials are not searched (no credential provider): they answer 401 and are skipped with a note.</item>
/// </list>
/// </summary>
public static class DotNetCli
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>The <c>dotnet</c> to run: on PATH, else in DOTNET_ROOT, else the per-user and default installs (a Dock-started app's PATH lacks them).</summary>
    public static string Find()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, name))
            .Concat(new[] { Environment.GetEnvironmentVariable("DOTNET_ROOT") }.OfType<string>().Select(d => Path.Combine(d, name)))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", name))
            .Append(OperatingSystem.IsWindows() ? @"C:\Program Files\dotnet\dotnet.exe" : OperatingSystem.IsMacOS() ? "/usr/local/share/dotnet/dotnet" : "/usr/share/dotnet/dotnet");
        return candidates.FirstOrDefault(File.Exists) ?? name;
    }

    /// <summary>Runs <c>dotnet</c> with the arguments in <paramref name="directory"/> and returns its exit code and standard output (stderr appended).</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string directory, IEnumerable<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Find()) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("dotnet could not be started");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return (p.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }

    /// <summary>The enabled package sources of <c>dotnet nuget list source --format short</c>: <c>E url</c> lines (D: disabled).</summary>
    public static IReadOnlyList<string> ParseSources(string output) =>
        [.. output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("E ", StringComparison.Ordinal)).Select(l => l[2..].Trim()).Where(l => l.Length > 0)];

    /// <summary>The package sources of the folder (nuget.org when the command fails).</summary>
    public static async Task<IReadOnlyList<string>> SourcesAsync(string folder, CancellationToken ct)
    {
        var (exit, output) = await RunAsync(folder, ["nuget", "list", "source", "--format", "short"], ct).ConfigureAwait(false);
        var sources = exit == 0 ? ParseSources(output) : [];
        return sources.Count > 0 ? sources : ["https://api.nuget.org/v3/index.json"];
    }

    /// <summary>
    /// Packages matching <paramref name="query"/> on every v3 source (a local folder source is not searched), merged by id (the first source's
    /// answer wins). <paramref name="skipped"/> receives each source that could not be searched, and why.
    /// </summary>
    public static async Task<IReadOnlyList<NuGetPackage>> SearchAsync(IReadOnlyList<string> sources, string query, bool prerelease, Action<string>? skipped, CancellationToken ct)
    {
        var found = new Dictionary<string, NuGetPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (!source.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { skipped?.Invoke($"{source}: not an HTTP source"); continue; }
            try
            {
                var search = await SearchServiceAsync(source, ct).ConfigureAwait(false);
                if (search is null) { skipped?.Invoke($"{source}: no search service"); continue; }
                var url = $"{search}?q={Uri.EscapeDataString(query)}&take=20&prerelease={(prerelease ? "true" : "false")}&semVerLevel=2.0.0";
                using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) { skipped?.Invoke($"{source}: {(int)response.StatusCode} {response.ReasonPhrase}"); continue; }
                foreach (var p in ParseSearch(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))) found.TryAdd(p.Id, p);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (ct.IsCancellationRequested) throw;
                skipped?.Invoke($"{source}: {ex.Message}");
            }
        }
        return [.. found.Values];
    }

    private static async Task<string?> SearchServiceAsync(string serviceIndex, CancellationToken ct)
    {
        using var response = await Http.GetAsync(serviceIndex, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}");
        return ParseSearchService(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>The <c>SearchQueryService</c> URL of a v3 service index.</summary>
    public static string? ParseSearchService(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array) return null;
        foreach (var r in resources.EnumerateArray())
        {
            var type = r.TryGetProperty("@type", out var t) ? t.GetString() : null;
            if (type is not null && type.StartsWith("SearchQueryService", StringComparison.Ordinal) && r.TryGetProperty("@id", out var id)) return id.GetString();
        }
        return null;
    }

    /// <summary>The packages of a v3 search answer, versions newest first.</summary>
    public static IReadOnlyList<NuGetPackage> ParseSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var list = new List<NuGetPackage>();
        foreach (var d in data.EnumerateArray())
        {
            var id = d.TryGetProperty("id", out var i) ? i.GetString() : null;
            var version = d.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (id is null || version is null) continue;
            var versions = d.TryGetProperty("versions", out var vs) && vs.ValueKind == JsonValueKind.Array
                ? vs.EnumerateArray().Select(x => x.TryGetProperty("version", out var xv) ? xv.GetString() : null).OfType<string>().Reverse().ToArray()
                : [version];
            list.Add(new NuGetPackage(id, version, versions, d.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                d.TryGetProperty("totalDownloads", out var dl) && dl.TryGetInt64(out var n) ? n : 0));
        }
        return list;
    }

    /// <summary>The top-level package references of <c>dotnet list &lt;project&gt; package --format json</c> (every framework's, merged by id).</summary>
    public static IReadOnlyList<PackageReference> ParsePackageList(string output)
    {
        var start = output.IndexOf('{');
        if (start < 0) return [];
        try
        {
            using var doc = JsonDocument.Parse(output[start..]);
            var refs = new Dictionary<string, PackageReference>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("projects", out var projects))
                foreach (var p in projects.EnumerateArray())
                    if (p.TryGetProperty("frameworks", out var fws))
                        foreach (var f in fws.EnumerateArray())
                            if (f.TryGetProperty("topLevelPackages", out var pkgs))
                                foreach (var pkg in pkgs.EnumerateArray())
                                    if (pkg.TryGetProperty("id", out var id) && id.GetString() is { } name)
                                        refs.TryAdd(name, new PackageReference(name, pkg.TryGetProperty("requestedVersion", out var r) ? r.GetString() : null,
                                            pkg.TryGetProperty("resolvedVersion", out var s) ? s.GetString() : null));
            return [.. refs.Values.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)];
        }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// The templates of <c>dotnet new list</c>: the table under its dashed line, columns cut where the dashes are. A short name with aliases
    /// (<c>webapp,razor</c>) gives its first; the default language is the bracketed one (<c>[C#],F#</c>).
    /// </summary>
    public static IReadOnlyList<DotNetTemplate> ParseTemplates(string output)
    {
        var lines = output.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var dash = Array.FindIndex(lines, l => l.TrimStart().StartsWith("---", StringComparison.Ordinal));
        if (dash < 0) return [];
        var columns = new List<(int Start, int End)>();
        var dashes = lines[dash];
        for (var i = 0; i < dashes.Length;)
        {
            if (dashes[i] != '-') { i++; continue; }
            var start = i;
            while (i < dashes.Length && dashes[i] == '-') i++;
            columns.Add((start, i));
        }
        if (columns.Count < 2) return [];
        string Cell(string line, int c)
        {
            var start = columns[c].Start;
            if (start >= line.Length) return "";
            var end = c + 1 < columns.Count ? Math.Min(columns[c + 1].Start, line.Length) : line.Length;
            return line[start..end].Trim();
        }
        var templates = new List<DotNetTemplate>();
        foreach (var line in lines.Skip(dash + 1))
        {
            if (line.Trim().Length == 0) break;
            var shortName = Cell(line, 1).Split(',')[0].Trim();
            if (shortName.Length == 0) continue;
            var languages = columns.Count > 2 ? Cell(line, 2) : "";
            var ordered = languages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .OrderBy(l => l.StartsWith('[') ? 0 : 1).Select(l => l.Trim('[', ']')).ToArray();
            templates.Add(new DotNetTemplate(Cell(line, 0), shortName, ordered, columns.Count > 3 ? Cell(line, 3) : ""));
        }
        return templates;
    }

    private static bool IsProjectFile(string name) =>
        name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase);

    private static Uri DirectoryUri(string path) => new(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar);

    /// <summary>The project files (.csproj, .fsproj, .vbproj) under <paramref name="folder"/>, build output, tool folders and links skipped, at most 500.</summary>
    public static async Task<IReadOnlyList<string>> FindProjectsAsync(Core.Platform.IFileSystem fs, string folder, CancellationToken ct)
    {
        string[] skipped = ["bin", "obj", ".git", "node_modules", ".vs", ".idea", ".scratch", "artifacts"];
        var found = new List<string>();
        var pending = new Stack<string>([folder]);
        while (pending.Count > 0 && found.Count < 500)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            IReadOnlyList<Core.Platform.DirectoryEntry> entries;
            try { entries = await fs.ReadDirectoryAsync(DirectoryUri(dir), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                if ((e.Type & Core.Platform.FileType.SymbolicLink) != 0) continue;
                if ((e.Type & Core.Platform.FileType.File) != 0 && IsProjectFile(e.Name)) found.Add(Path.Combine(dir, e.Name));
                else if ((e.Type & Core.Platform.FileType.Directory) != 0 && !skipped.Contains(e.Name, StringComparer.OrdinalIgnoreCase)) pending.Push(Path.Combine(dir, e.Name));
            }
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>The project a file belongs to: the nearest project file in its directory or above, up to <paramref name="stopAt"/>.</summary>
    public static async Task<string?> NearestProjectAsync(Core.Platform.IFileSystem fs, string file, string? stopAt, CancellationToken ct)
    {
        for (var dir = Path.GetDirectoryName(file); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            try
            {
                var entries = await fs.ReadDirectoryAsync(DirectoryUri(dir), ct).ConfigureAwait(false);
                if (entries.FirstOrDefault(e => (e.Type & Core.Platform.FileType.File) != 0 && IsProjectFile(e.Name)) is { } project) return Path.Combine(dir, project.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            if (stopAt is not null && string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(stopAt), StringComparison.Ordinal)) return null;
        }
        return null;
    }

    /// <summary>A download count as NuGet shows it (12.3M, 456K).</summary>
    public static string Downloads(long n) => n switch
    {
        >= 1_000_000_000 => (n / 1_000_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (n / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (n / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>A command line as the task terminal echoes it (arguments with spaces quoted).</summary>
    public static string CommandLine(IEnumerable<string> arguments)
    {
        var sb = new StringBuilder("dotnet");
        foreach (var a in arguments) sb.Append(' ').Append(a.Contains(' ', StringComparison.Ordinal) ? "\"" + a + "\"" : a);
        return sb.ToString();
    }
}
