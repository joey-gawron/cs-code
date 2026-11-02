using CsCode.ProjectTemplates.Generators;
using Xunit;

namespace CsCode.ProjectTemplates.Tests;

/// <summary>The parsers of the .NET SDK's output, on output captured from SDK 10.0.403 (the real-tool tests run them on the installed SDK).</summary>
public sealed class DotnetTemplatesTests
{
    private const string List = """
        These templates matched your input: --language='C#', --type='project'

        Template Name                                 Short Name           Language  Tags
        --------------------------------------------  -------------------  --------  ----------------------------------
        ASP.NET Core Empty                            web                  C#,F#     Web/Empty
        ASP.NET Core Web API                          webapi               C#,F#     Web/Web API/API/Service
        ASP.NET Core Web App (Razor Pages)            webapp,razor         C#        Web/MVC/Razor Pages
        Class Library                                 classlib             C#,F#,VB  Common/Library
        Console App                                   console              C#,F#,VB  Common/Console
        xUnit Test Project                            xunit                C#,F#,VB  Test/xUnit/Desktop/Web
        """;

    private const string ConsoleHelp = """
        Template options:
          -f, --framework <net10.0>    The target framework for the project.
                                       Type: choice
                                         net10.0  Target net10.0
                                         net9.0   Target net9.0
                                       Default: net10.0
          --langVersion <langVersion>  Sets the LangVersion property in the created project file
                                       Type: text
          --no-restore                 If specified, skips the automatic restore of the project on create.
                                       Type: bool
                                       Default: false
        """;

    [Fact]
    public void ParseList_reads_the_columns_by_the_dash_line()
    {
        var templates = DotnetTemplates.ParseList(List)!;
        Assert.Equal(6, templates.Count);
        var razor = templates.Single(t => t.Name.StartsWith("ASP.NET Core Web App", StringComparison.Ordinal));
        Assert.Equal("webapp", razor.ShortName); // the first of "webapp,razor"
        Assert.Equal("C#", razor.Languages);
        Assert.True(templates.Single(t => t.ShortName == "xunit").IsTestProject);
        Assert.False(templates.Single(t => t.ShortName == "console").IsTestProject);
    }

    [Fact]
    public void Order_puts_the_curated_templates_first()
    {
        var ordered = DotnetTemplates.Order(DotnetTemplates.ParseList(List)!).Select(t => t.ShortName).ToArray();
        Assert.Equal(["console", "classlib", "webapi", "xunit", "web", "webapp"], ordered);
    }

    [Fact]
    public void ParseList_without_a_table_is_null() => Assert.Null(DotnetTemplates.ParseList("No templates found matching: 'x'."));

    [Fact]
    public void ParseChoices_reads_a_choice_option_and_its_default()
    {
        var (values, @default) = DotnetTemplates.ParseChoices(ConsoleHelp, "--framework")!.Value;
        Assert.Equal(["net10.0", "net9.0"], values);
        Assert.Equal("net10.0", @default);
        Assert.True(DotnetTemplates.HasOption(ConsoleHelp, "--no-restore"));
        Assert.False(DotnetTemplates.HasOption(ConsoleHelp, "--format"));
    }
}
