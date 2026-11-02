using CsCode.App.DotNet;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests.DotNet;

/// <summary>
/// The .NET commands' readers (docs/CSHARP.md, "Tasks and NuGet"): package sources, NuGet's v3 search and a project's package list, from
/// outputs copied from SDK 10.0.403 and nuget.org (2026-11-01); then the real SDK and nuget.org, skipped when either is unavailable.
/// </summary>
public class DotNetCliTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Enabled_sources_are_read_from_the_short_format()
    {
        Assert.Equal(["https://api.nuget.org/v3/index.json", "https://pkgs.example.com/v3/index.json"],
            DotNetCli.ParseSources("E https://api.nuget.org/v3/index.json\nD https://disabled.example.com/v3/index.json\nE https://pkgs.example.com/v3/index.json\n"));
    }

    [Fact]
    public void The_search_service_and_its_results_are_read()
    {
        Assert.Equal("https://azuresearch-usnc.nuget.org/query", DotNetCli.ParseSearchService("""
            {"version":"3.0.0","resources":[{"@id":"https://api.nuget.org/v3-flatcontainer/","@type":"PackageBaseAddress/3.0.0"},
             {"@id":"https://azuresearch-usnc.nuget.org/query","@type":"SearchQueryService"},{"@id":"https://x/query","@type":"SearchQueryService/3.5.0"}]}
            """));
        var found = DotNetCli.ParseSearch("""
            {"totalHits":1,"data":[{"id":"Newtonsoft.Json","version":"13.0.4","description":"Json.NET","totalDownloads":6000000000,
              "versions":[{"version":"12.0.3","downloads":1},{"version":"13.0.3","downloads":2},{"version":"13.0.4","downloads":3}]}]}
            """);
        var p = Assert.Single(found);
        Assert.Equal(("Newtonsoft.Json", "13.0.4"), (p.Id, p.Version));
        Assert.Equal(["13.0.4", "13.0.3", "12.0.3"], p.Versions);
        Assert.Equal("6B", DotNetCli.Downloads(p.Downloads));
    }

    [Fact]
    public void A_projects_package_references_are_read_from_dotnet_list_package()
    {
        var refs = DotNetCli.ParsePackageList("""
            {"version":1,"parameters":"","projects":[{"path":"/w/App/App.csproj","frameworks":[
              {"framework":"net10.0","topLevelPackages":[{"id":"Serilog","requestedVersion":"4.0.0","resolvedVersion":"4.0.0"},{"id":"Avalonia","requestedVersion":"12.0.0","resolvedVersion":"12.0.0"}]},
              {"framework":"net9.0","topLevelPackages":[{"id":"Serilog","requestedVersion":"4.0.0","resolvedVersion":"4.0.0"}]}]}]}
            """);
        Assert.Equal(["Avalonia", "Serilog"], refs.Select(r => r.Id));
        Assert.Empty(DotNetCli.ParsePackageList("error: no project"));
    }

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task The_folders_package_sources_are_read_and_nuget_org_answers_a_search()
    {
        var dotnet = DotNetCli.Find();
        Assert.SkipUnless(Path.IsPathRooted(dotnet) && File.Exists(dotnet), "dotnet is not installed");
        using var dir = new TempDirectory("cscode-dotnet-cli-");
        var sources = await DotNetCli.SourcesAsync(dir.Path, Ct);
        var skipped = new List<string>();
        var found = await DotNetCli.SearchAsync(sources, "packageid:Newtonsoft.Json", prerelease: false, skipped.Add, Ct);
        Assert.SkipWhen(found.Count == 0 && skipped.Count > 0, "the package sources could not be searched (offline?): " + string.Join("; ", skipped));
        var json = Assert.Single(found, p => p.Id == "Newtonsoft.Json");
        Assert.Contains("13.0.1", json.Versions);
    }

    [Fact]
    public async Task Projects_are_found_below_a_folder_and_a_file_belongs_to_its_nearest_project()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("src/App/App.csproj", "<Project />");
        var source = dir.WriteFile("src/App/Models/Item.cs", "class Item { }");
        dir.WriteFile("src/App/obj/Ignored.csproj", "<Project />");
        dir.WriteFile("tests/App.Tests/App.Tests.fsproj", "<Project />");
        using var lane = new CsCode.Platform.Io.IoLane("dotnet-cli-tests", maxThreads: 1);
        var fs = new CsCode.Platform.FileSystem.LocalFileSystem(lane);
        Assert.Equal([dir.Combine("src", "App", "App.csproj"), dir.Combine("tests", "App.Tests", "App.Tests.fsproj")], await DotNetCli.FindProjectsAsync(fs, dir.Path, Ct));
        Assert.Equal(dir.Combine("src", "App", "App.csproj"), await DotNetCli.NearestProjectAsync(fs, source, dir.Path, Ct));
        Assert.Null(await DotNetCli.NearestProjectAsync(fs, dir.WriteFile("README.md", "x"), dir.Path, Ct));
    }
}
