using System.Text.RegularExpressions;
using System.Xml.Linq;
using CsCode.Core.Platform;

namespace CsCode.Dap.Adapters;

/// <summary>A .NET project of a workspace folder. <paramref name="RelativePath"/> uses '/' and is relative to the folder.</summary>
internal sealed record DotnetProject(string RelativePath, string AssemblyName, string TargetFramework, bool IsExecutable, bool IsTest = false)
{
    public string Directory => RelativePath.LastIndexOf('/') is var i and > 0 ? RelativePath[..i] : "";

    /// <summary>The project file's name without its extension (<c>App</c> for <c>src/App/App.csproj</c>).</summary>
    public string Name => Path.GetFileNameWithoutExtension(RelativePath);
}

/// <summary>What <see cref="DotnetProjects.FindAsync"/> found: the folder's own solution files (names, sorted) and its projects.</summary>
internal sealed record DotnetFolder(IReadOnlyList<string> Solutions, IReadOnlyList<DotnetProject> Projects);

/// <summary>
/// A folder's .NET projects read without MSBuild, for F5's launch.json (<see cref="NetCoreDbgAdapterFactory"/>) and the detected
/// <c>dotnet</c> tasks (<see cref="Tasks.DotnetTaskProvider"/>): the folder's own project file (.csproj/.fsproj/.vbproj), else the projects its
/// solution lists (.sln/.slnx), else the project files up to two levels down (the <c>dotnet new sln</c> layout: <c>src/Web/Web.csproj</c>).
/// Each project's properties come from its own file only (a Directory.Build.props is not read). Bounded: 64 projects, 256 directories.
/// </summary>
internal static partial class DotnetProjects
{
    internal const string DefaultTargetFramework = "net10.0";
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", "packages", "artifacts", "target", "out" };
    private const int MaxCandidates = 64, MaxDirectoriesScanned = 256;

    // the package references (and SDK) that make a project a test project before MSBuild has evaluated it (IsTestProject comes from them)
    private static readonly string[] TestPackages =
        ["Microsoft.NET.Test.Sdk", "xunit", "xunit.v3", "NUnit", "MSTest", "MSTest.TestFramework", "TUnit", "Microsoft.Testing.Platform"];

    public static async Task<DotnetFolder> FindAsync(IFileSystem fs, Uri folder, CancellationToken ct)
    {
        var root = folder.ToString().TrimEnd('/');
        Uri At(string relative) => new(root + "/" + relative);
        async Task<IReadOnlyList<DirectoryEntry>> ListAsync(string relative)
        {
            try { return await fs.ReadDirectoryAsync(relative.Length == 0 ? folder : At(relative), ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }
        static bool IsProject(string name) => ProjectExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        static bool IsScanned(DirectoryEntry e) => e.Type == FileType.Directory && !e.Name.StartsWith('.') && !SkippedDirectories.Contains(e.Name);

        var top = await ListAsync("");
        var solutions = top.Where(e => e.Type == FileType.File && (e.Name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
            .Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var paths = top.Where(e => e.Type == FileType.File && IsProject(e.Name)).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (paths.Count == 0)
        {
            // the projects the folder's solution lists (relative to the solution, which is in the folder)
            foreach (var solution in solutions)
            {
                string text;
                try { text = System.Text.Encoding.UTF8.GetString(await fs.ReadFileAsync(At(solution), ct)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var listed in SolutionProjects(solution, text))
                    if (IsProject(listed) && !paths.Contains(listed, StringComparer.Ordinal) && await fs.StatAsync(At(listed), ct) is { Type: FileType.File })
                        paths.Add(listed);
            }
        }
        if (paths.Count == 0)
        {
            // no solution either: the project files one and two levels down (bounded)
            var scanned = 0;
            var level = top.Where(IsScanned).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            for (var depth = 1; depth <= 2 && level.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var dir in level)
                {
                    if (++scanned > MaxDirectoriesScanned || paths.Count >= MaxCandidates) break;
                    foreach (var e in (await ListAsync(dir)).OrderBy(e => e.Name, StringComparer.Ordinal))
                    {
                        if (e.Type == FileType.File && IsProject(e.Name)) paths.Add(dir + "/" + e.Name);
                        else if (IsScanned(e)) next.Add(dir + "/" + e.Name);
                    }
                }
                level = next;
            }
        }

        var projects = new List<DotnetProject>();
        foreach (var path in paths.Take(MaxCandidates))
        {
            string? xml = null;
            try { xml = System.Text.Encoding.UTF8.GetString(await fs.ReadFileAsync(At(path), ct)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* named after its file, default framework */ }
            projects.Add(ReadProject(path, xml));
        }
        return new DotnetFolder(solutions, projects);
    }

    /// <summary>
    /// The project paths a solution lists, with '/': a .sln's <c>Project("{…}") = "Name", "src\Web\Web.csproj", "{…}"</c> lines, a .slnx's
    /// <c>&lt;Project Path="…"/&gt;</c> elements. Paths that leave the folder are left out.
    /// </summary>
    internal static IReadOnlyList<string> SolutionProjects(string solutionName, string text)
    {
        var listed = new List<string>();
        if (solutionName.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            try { listed.AddRange(XDocument.Parse(text).Descendants().Where(e => e.Name.LocalName == "Project").Select(e => (string?)e.Attribute("Path")).OfType<string>()); }
            catch (System.Xml.XmlException) { /* not a solution we can read */ }
        }
        else
        {
            foreach (Match m in SlnProjectLine().Matches(text)) listed.Add(m.Groups[1].Value);
        }
        var result = new List<string>();
        foreach (var raw in listed)
        {
            var path = raw.Trim().Replace('\\', '/');
            while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            if (path.Length == 0 || path.StartsWith("../", StringComparison.Ordinal) || path.Contains("/../", StringComparison.Ordinal) || Path.IsPathRooted(path)) continue;
            result.Add(path);
        }
        return result;
    }

    [GeneratedRegex("""^\s*Project\("\{[^}]*\}"\)\s*=\s*"[^"]*"\s*,\s*"([^"]+)"\s*,""", RegexOptions.Multiline)]
    private static partial Regex SlnProjectLine();

    /// <summary>
    /// Assembly name, first target framework, whether it builds a program (OutputType Exe/WinExe, the Web and Worker SDKs; a test project does
    /// not count) and whether it is a test project (<c>IsTestProject</c>, the MSTest SDK, the test runners' switches or a test framework's package).
    /// </summary>
    internal static DotnetProject ReadProject(string relativePath, string? xml)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var tfm = DefaultTargetFramework;
        bool executable = false, test = false;
        if (xml is not null)
        {
            try
            {
                var doc = XDocument.Parse(xml);
                string? Property(string property) =>
                    doc.Descendants().Where(e => e.Name.LocalName == property).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0 && !v.Contains("$(", StringComparison.Ordinal));
                bool IsTrue(string property) => string.Equals(Property(property), "true", StringComparison.OrdinalIgnoreCase);
                tfm = Property("TargetFramework")
                    ?? Property("TargetFrameworks")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
                    ?? tfm;
                if (Property("AssemblyName") is { } assembly) name = assembly;
                var sdk = (string?)doc.Root?.Attribute("Sdk") ?? "";
                var outputType = Property("OutputType");
                executable = outputType is not null
                    ? string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase)
                    : sdk.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) || sdk.StartsWith("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase);
                var packages = doc.Descendants().Where(e => e.Name.LocalName == "PackageReference").Select(e => (string?)e.Attribute("Include")).OfType<string>();
                test = IsTrue("IsTestProject") || IsTrue("EnableMSTestRunner") || IsTrue("EnableNUnitRunner")
                    || sdk.StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase)
                    || packages.Any(p => TestPackages.Contains(p, StringComparer.OrdinalIgnoreCase));
                if (test && !string.Equals(Property("IsTestProject"), "false", StringComparison.OrdinalIgnoreCase)) executable = false;
                else test = false;
            }
            catch (System.Xml.XmlException) { /* keep the defaults */ }
        }
        return new DotnetProject(relativePath, name, tfm, executable, test);
    }
}
