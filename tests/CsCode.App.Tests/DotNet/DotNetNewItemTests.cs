using CsCode.App.DotNet;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.ProjectTemplates.Generators;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests.DotNet;

/// <summary>
/// New Item (<c>dotnet.newItem</c>): the readers of <c>dotnet new</c>'s output (copied from SDK 10.0.403, 2026-11-02), then the command with the
/// real SDK on a project in a temp folder (skipped without it).
/// </summary>
public class DotNetNewItemTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // `dotnet new list --type item`, shortened
    private const string ItemList = """
        These templates matched your input: --type='item'

        Template Name                          Short Name                    Language    Tags
        -------------------------------------  ----------------------------  ----------  --------------------------
        API Controller                         apicontroller                 [C#]        Web/ASP.NET
        EditorConfig file                      editorconfig,.editorconfig                Config
        MSBuild Directory.Build.props file     buildprops                                MSBuild/props
        MSTest Test Class                      mstest-class                  [C#],F#,VB  Test/MSTest
        NUnit Test Item                        nunit-test                    [C#],F#,VB  Test/NUnit
        Protocol Buffer File                   proto                                     Web/gRPC
        """;

    [Fact]
    public void The_item_templates_of_a_projects_language_and_the_language_free_ones_are_offered()
    {
        var all = DotnetTemplates.ParseList(ItemList)!;
        Assert.Equal(["apicontroller", "editorconfig", "buildprops", "mstest-class", "nunit-test", "proto"], // by name
            DotNetCli.ItemTemplatesFor(all, "C#").Select(t => t.ShortName));
        Assert.Equal(["editorconfig", "buildprops", "mstest-class", "nunit-test", "proto"], DotNetCli.ItemTemplatesFor(all, "F#").Select(t => t.ShortName));
        Assert.Equal(["C#", "F#", "VB"], DotNetCli.LanguagesOf(all.Single(t => t.ShortName == "mstest-class")));
        Assert.True(DotNetCli.HasFixedName(all.Single(t => t.ShortName == "editorconfig")));
        Assert.True(DotNetCli.HasFixedName(all.Single(t => t.ShortName == "buildprops")));
        Assert.False(DotNetCli.HasFixedName(all.Single(t => t.ShortName == "proto")));
        Assert.Equal(("F#", "VB", "C#", "C#"), (DotNetCli.LanguageOf("/w/A.fsproj"), DotNetCli.LanguageOf("/w/B.vbproj"), DotNetCli.LanguageOf("/w/C.csproj"), DotNetCli.LanguageOf(null)));
    }

    [Fact]
    public void An_items_namespace_is_the_projects_root_namespace_and_the_folders_below_it()
    {
        Assert.Equal("My.App.Api.V1", DotNetCli.NamespaceFor("/w/src/App/App.csproj", "<Project><PropertyGroup><RootNamespace>My.App</RootNamespace></PropertyGroup></Project>", "/w/src/App/Api/V1"));
        Assert.Equal("My_Tool._2024", DotNetCli.NamespaceFor("/w/My-Tool.csproj", "<Project />", "/w/2024"));
        Assert.Equal("App", DotNetCli.NamespaceFor("/w/App.csproj", null, "/w"));
        Assert.Equal("App", DotNetCli.NamespaceFor("/w/App.csproj", null, "/elsewhere")); // a folder outside the project adds nothing
        Assert.Equal("Values", DotNetCommands.WithoutSourceExtension("Values.cs"));
        Assert.Equal("Index", DotNetCommands.WithoutSourceExtension("Index.razor"));
        Assert.Equal("my.service", DotNetCommands.WithoutSourceExtension("my.service"));
    }

    [Fact]
    public void A_dry_run_gives_the_files_it_would_create_and_those_it_would_replace()
    {
        var (created, overwritten) = DotNetCli.ParseDryRun("""
            File actions would have been taken:
              Create: ./T.fs
              Create: Controllers/Values.cs

            Processing post-creation actions...
            """);
        Assert.Equal(["T.fs", "Controllers/Values.cs"], created);
        Assert.Empty(overwritten);
        // exit code 73: it refuses without --force
        Assert.Equal(["Values.cs"], DotNetCli.ParseDryRun("""
            Creating this template will make changes to existing files:
              Overwrite   ./Values.cs

            To create the template anyway, run the command with '--force' option:
            """).Overwritten);
        var forced = DotNetCli.ParseDryRun("File actions would have been taken:\n  Overwrite: ./Values.cs\n"); // with --force
        Assert.Equal(["Values.cs"], forced.Created);
        Assert.Equal(["Values.cs"], forced.Overwritten);
    }

    [Fact]
    public async Task A_folder_with_a_project_or_solution_near_its_top_shows_New_Dotnet_Item()
    {
        using var dir = new TempDirectory();
        var fs = new DiskFileSystem();
        Assert.False(await DotNetCli.HasProjectNearTopAsync(fs, dir.Path, Ct));
        dir.WriteFile("node_modules/x/x.csproj", "<Project />");
        Assert.False(await DotNetCli.HasProjectNearTopAsync(fs, dir.Path, Ct));
        dir.WriteFile("src/App/App.csproj", "<Project />");
        Assert.True(await DotNetCli.HasProjectNearTopAsync(fs, dir.Path, Ct));
    }

    // ------------------------------------------------------------------ the real SDK

    private sealed class Rig : IDisposable
    {
        public TempDirectory Dir { get; } = new();
        public RecordingWindowService Window { get; } = new();
        public TestCommandService Commands { get; } = new();
        public ServiceProvider Services { get; }
        private readonly DotNetCommands _commands;

        public Rig()
        {
            var workspace = new FakeWorkspaceService();
            workspace.OpenFolderAsync(Dir.Uri).AsTask().GetAwaiter().GetResult();
            var services = new ServiceCollection();
            services.AddSingleton<IWindowService>(Window);
            services.AddSingleton<ICommandService>(Commands);
            services.AddSingleton<IFileSystem>(new DiskFileSystem());
            services.AddSingleton<IWorkspaceService>(workspace);
            Services = services.BuildServiceProvider();
            _commands = DotNetCommands.TryCreate(Services)!;
            _commands.Start();
        }

        public void Dispose()
        {
            _commands.Dispose();
            Services.Dispose();
            Dir.Dispose();
        }
    }

    private static int IndexOf(IReadOnlyList<DotnetTemplate> offered, string shortName) => offered.Select(t => t.ShortName).ToList().IndexOf(shortName);

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task New_Item_creates_a_namespaced_item_asks_before_replacing_it_and_writes_a_fixed_name_file_without_asking_a_name()
    {
        var (exit, list) = await DotNetCli.RunAsync(Path.GetTempPath(), ["new", "list", "--type", "item"], Ct);
        Assert.SkipWhen(exit != 0 || DotnetTemplates.ParseList(list) is not { Count: > 0 }, "the .NET SDK's item templates cannot be listed: " + list);
        var offered = DotNetCli.ItemTemplatesFor(DotnetTemplates.ParseList(list)!, "C#");
        Assert.SkipWhen(IndexOf(offered, "apicontroller") < 0 || IndexOf(offered, "editorconfig") < 0, "this SDK has no apicontroller or editorconfig template");

        using var rig = new Rig();
        rig.Dir.WriteFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>My.App</RootNamespace></PropertyGroup></Project>");
        Directory.CreateDirectory(Path.Combine(rig.Dir.Path, "Controllers"));
        var controllers = new Uri(Path.Combine(rig.Dir.Path, "Controllers") + "/").ToString(); // what the Explorer passes: the folder's URI

        rig.Window.QuickPickResponses.Enqueue(IndexOf(offered, "apicontroller"));
        rig.Window.InputResponses.Enqueue("Values.cs");
        await rig.Commands.ExecuteAsync(DotNetCommands.NewItem, controllers);
        var values = Path.Combine(rig.Dir.Path, "Controllers", "Values.cs");
        Assert.True(File.Exists(values), string.Join("\n", rig.Window.Messages));
        Assert.Contains("namespace My.App.Controllers", File.ReadAllText(values));
        Assert.Contains(rig.Window.StatusMessages, m => m.Contains("Values.cs", StringComparison.Ordinal));

        // again: dotnet new would replace it, so New Item asks first
        File.WriteAllText(values, "// mine\n");
        rig.Window.QuickPickResponses.Enqueue(IndexOf(offered, "apicontroller"));
        rig.Window.InputResponses.Enqueue("Values");
        rig.Window.MessageActionResponses.Enqueue("Cancel");
        await rig.Commands.ExecuteAsync(DotNetCommands.NewItem, controllers);
        Assert.Contains(rig.Window.Messages, m => m.Severity == MessageSeverity.Warning && m.Message.Contains("Values.cs already exists", StringComparison.Ordinal));
        Assert.Equal("// mine\n", File.ReadAllText(values));
        rig.Window.QuickPickResponses.Enqueue(IndexOf(offered, "apicontroller"));
        rig.Window.InputResponses.Enqueue("Values");
        rig.Window.MessageActionResponses.Enqueue("Replace");
        await rig.Commands.ExecuteAsync(DotNetCommands.NewItem, controllers);
        Assert.Contains("namespace My.App.Controllers", File.ReadAllText(values));

        // a fixed-name template asks for no name
        var inputsBefore = rig.Window.InputResponses.Count;
        rig.Window.QuickPickResponses.Enqueue(IndexOf(offered, "editorconfig"));
        await rig.Commands.ExecuteAsync(DotNetCommands.NewItem, rig.Dir.Uri.ToString());
        Assert.True(File.Exists(Path.Combine(rig.Dir.Path, ".editorconfig")), string.Join("\n", rig.Window.Messages));
        Assert.Equal(inputsBefore, rig.Window.InputResponses.Count);
    }
}
